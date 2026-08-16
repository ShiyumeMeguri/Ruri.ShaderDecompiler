using Ruri.ShaderTools.Pipeline.Backend;
using Ruri.ShaderTools.Pipeline.Diagnostics;
using Ruri.ShaderTools.Pipeline.Frontend;
using Ruri.ShaderTools.Pipeline.Naming;
using Ruri.ShaderTools.Spirv.ConstantBuffers;
using Ruri.ShaderTools.Spirv.ScalarLayout;
using Ruri.ShaderTools.Spirv.SymbolInjection;

namespace Ruri.ShaderTools.Pipeline;

/// <summary>
/// The decompile route, end to end:
///
/// <code>
///   binary → SPIR-V → scalar-layout normalise → structure constant buffers
///          → [host symbol enrichment] → inject symbols → emit source
/// </code>
///
/// Every step is engine-agnostic. Engine knowledge enters only through the
/// symbol table the caller builds, which is what lets one pipeline serve
/// unrelated engines without a branch anywhere in it.
///
/// NOT thread-safe: it holds per-call state (the structurer's resolved names and
/// log, the emitter's last failure). Each worker owns its own instance — which is
/// cheap, since construction resolves no resources.
/// </summary>
internal sealed class DecompilePipeline
{
    /// <summary>
    /// Shader model forced on the source backend.
    ///
    /// The backend uses this value to gate WHICH INTRINSICS IT IS WILLING TO
    /// EMIT — never to validate input. Every gate is of the form "emitting X
    /// requires SM ≥ N"; there is no gate that requires SM ≤ N. So raising it can
    /// only unlock, never lose:
    ///
    ///   * "Wave ops requires SM 6.0 or higher" — fires on SM5 inputs too,
    ///     because translation can produce subgroup ops regardless of source model
    ///   * "Sampling non-float textures is not supported in HLSL SM &lt; 6.7" —
    ///     fires on classic SM5 shaders sampling uint/sint textures
    ///   * mesh-shader and variable-rate-shading emission gates
    ///
    /// Left at the legacy default, a large fraction of a shipping archive fails to
    /// emit for reasons that have nothing to do with the shaders. A caller asking
    /// for a HIGHER model keeps it — the floor only raises.
    /// </summary>
    private const uint MinimumEmitShaderModel = 67;

    private readonly ConstantBufferStructurer _structurer = new();
    private readonly SourceEmitter _emitter = new();
    private readonly SpirvFrontend _frontend = new();

    public DecompileResult Run(byte[] binary, DecompileOptions options)
    {
        SerializedProgramData symbols = options.Symbols ?? new SerializedProgramData();
        ShaderBinaryFormat format = ShaderBinaryFormatDetector.Detect(options.Format, binary);
        uint shaderModel = Math.Max(options.ShaderModel, MinimumEmitShaderModel);

        var result = new DecompileResult { FinalSymbols = symbols, FinalUnityMetadata = options.UnityMetadata };
        DecompileStage stage = DecompileStage.FrontendConversion;

        try
        {
            byte[] spirv = _frontend.Convert(format, binary);

            stage = DecompileStage.ScalarLayoutNormalization;
            spirv = ScalarBlockVectorizer.Vectorize(spirv);
            result.SpirvAfterFrontend = spirv;

            stage = DecompileStage.ConstantBufferStructuring;
            byte[] structured = Structure(spirv, symbols);
            result.SpirvAfterStructuring = structured;

            stage = DecompileStage.SymbolEnrichment;
            Enrich(options, structured, symbols);

            stage = DecompileStage.SymbolInjection;
            byte[] injected = Inject(structured, symbols);
            result.SpirvAfterSymbolInjection = injected;

            stage = DecompileStage.SourceEmission;
            EmittedSource source = Emit(injected, symbols, shaderModel);

            result.Success = true;
            result.FailedStage = DecompileStage.Completed;
            result.SourceCode = source.Text;
            result.SourceLanguage = source.Language;
            result.SourceFileExtension = source.FileExtension;
            result.FinalSpirv = injected;
            result.StructuringLog = _structurer.LastRewriteSummary;
            return result;
        }
        catch (Exception exception)
        {
            return Fail(result, stage, exception, binary, options, symbols);
        }
    }

    // Each wrapper below exists so the thrown message names the stage AND carries
    // the module state that explains it. A bare "SPIR-V emission failed" is
    // unactionable; the same message with the patch plan and built-in decorations
    // attached usually is not.

    private byte[] Structure(byte[] spirv, SerializedProgramData symbols)
    {
        try
        {
            return _structurer.Rewrite(spirv, symbols);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Constant buffer structuring failed.{Environment.NewLine}{ModuleReports.DescribeBuiltInDecorations(spirv)}",
                exception);
        }
    }

    private static void Enrich(DecompileOptions options, byte[] structured, SerializedProgramData symbols)
    {
        if (options.SymbolEnricher is null)
        {
            return;
        }

        try
        {
            options.SymbolEnricher(structured, symbols);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"SymbolEnricher threw: {exception.Message}", exception);
        }
    }

    private byte[] Inject(byte[] spirv, SerializedProgramData symbols)
    {
        try
        {
            // Mark the members nothing will ever have a name for FIRST, so the
            // scan below sees them and the real names injected afterwards
            // overwrite whichever of them turn out to be recoverable.
            spirv = AnonymousMemberNamer.Apply(spirv);

            if (symbols.GetResourceBindingCount() == 0)
            {
                return spirv;
            }

            List<DescriptorBindingInfo> bindings = BindingScanner.Scan(spirv);
            List<NamePatch> names = new ResourceNamePlanner(_structurer.GetResolvedBlockName).Plan(bindings, symbols);
            List<MemberNamePatch> members = new BlockMemberNamePlanner(_structurer.GetResolvedBlockName).Plan(bindings, symbols);

            return DebugNameInjector.Inject(spirv, names, members);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Symbol injection failed.{Environment.NewLine}" +
                $"{ModuleReports.DescribePatchPlan(spirv, symbols, _structurer.GetResolvedBlockName)}{Environment.NewLine}" +
                $"{ModuleReports.DescribeBuiltInDecorations(spirv)}",
                exception);
        }
    }

    private EmittedSource Emit(byte[] spirv, SerializedProgramData symbols, uint shaderModel)
    {
        try
        {
            return _emitter.Emit(spirv, symbols.EntryPoint, shaderModel);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Source emission failed after symbol injection.{Environment.NewLine}" +
                $"{ModuleReports.DescribePatchPlan(spirv, symbols, _structurer.GetResolvedBlockName)}{Environment.NewLine}" +
                $"{ModuleReports.DescribeBuiltInDecorations(spirv)}",
                exception);
        }
    }

    private DecompileResult Fail(
        DecompileResult result,
        DecompileStage stage,
        Exception exception,
        byte[] binary,
        DecompileOptions options,
        SerializedProgramData symbols)
    {
        result.Success = false;
        result.ErrorMessage = exception.ToString();
        result.FailedStage = stage;
        result.FinalSpirv = result.SpirvAfterSymbolInjection ?? result.SpirvAfterStructuring ?? result.SpirvAfterFrontend;
        result.StructuringLog = _structurer.LastRewriteSummary;
        result.NativeToolDiagnostics = _frontend.LastFailure ?? _emitter.LastFailure;

        // Report against the deepest module that exists: an earlier snapshot would
        // describe a state the failure did not happen in.
        byte[]? reportable = result.FinalSpirv;
        if (reportable is not null)
        {
            result.PatchPlanReport = ModuleReports.DescribePatchPlan(reportable, symbols, _structurer.GetResolvedBlockName);
            result.BuiltInDecorationReport = ModuleReports.DescribeBuiltInDecorations(reportable);
        }

        if (!string.IsNullOrWhiteSpace(options.DebugDumpDirectory))
        {
            try
            {
                result.DebugDumpDirectory = FailureDumpWriter.Write(
                    options.DebugDumpDirectory!, options.DebugDumpStem, binary, result, symbols);
            }
            catch (Exception dumpException)
            {
                Console.Error.WriteLine($"[ShaderDecompiler] Failed to write failure dump: {dumpException.Message}");
            }
        }

        return result;
    }
}
