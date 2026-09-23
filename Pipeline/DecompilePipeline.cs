using Ruri.ShaderTools.Pipeline.Backend;
using Ruri.ShaderTools.Pipeline.Diagnostics;
using Ruri.ShaderTools.Pipeline.Frontend;
using Ruri.ShaderTools.Pipeline.Naming;
using Ruri.ShaderTools.Spirv.ConstantBuffers;
using Ruri.ShaderTools.Spirv.Interstage;
using Ruri.ShaderTools.Spirv.ScalarLayout;
using Ruri.ShaderTools.Spirv.SymbolInjection;
using Ruri.ShaderTools.Unity;

namespace Ruri.ShaderTools.Pipeline;

/// <summary>
/// The decompile route, end to end:
///
/// <code>
///   binary → SPIR-V → scalar-layout normalise → structure constant buffers
///          → [host symbol enrichment] → inject symbols → plan emission → emit HLSL
/// </code>
///
/// Every step is engine-agnostic. Engine knowledge enters only through the
/// symbol table the caller builds, which is what lets one pipeline serve
/// unrelated engines without a branch anywhere in it.
///
/// NOT thread-safe: it holds per-call state (the structurer's resolved names and
/// log, the driver's last failure). Each worker owns its own instance — which is
/// cheap, since construction resolves no resources.
/// </summary>
internal sealed class DecompilePipeline
{
    /// <summary>
    /// Shader model floor for the source backend.
    ///
    /// The backend uses this value to gate WHICH INTRINSICS IT IS WILLING TO
    /// EMIT — never to validate input. Every gate is "emitting X requires SM ≥ N",
    /// so raising it can only unlock: wave ops, non-float texture sampling, mesh
    /// and variable-rate emission all fire on inputs compiled for older models.
    /// A caller asking for a HIGHER model keeps it — the floor only raises.
    /// </summary>
    private const uint MinimumEmitShaderModel = 67;

    private readonly ConstantBufferStructurer _structurer = new();
    private readonly SpirvCrossDriver _driver = new();
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
            FrontendOutput frontend = _frontend.Convert(format, binary);

            stage = DecompileStage.ScalarLayoutNormalization;
            byte[] spirv = ScalarBlockVectorizer.Vectorize(frontend.Spirv);

            stage = DecompileStage.InterstageSlotAssignment;
            spirv = InterstageSlotAssigner.Assign(spirv);
            result.SpirvAfterFrontend = spirv;

            stage = DecompileStage.ConstantBufferStructuring;
            byte[] structured = Structure(spirv, symbols);
            result.SpirvAfterStructuring = structured;

            stage = DecompileStage.SymbolEnrichment;
            Enrich(options, structured, symbols);

            stage = DecompileStage.SymbolInjection;
            (byte[] injected, List<FlattenedBlock> flattened) = Inject(structured, symbols);
            result.SpirvAfterSymbolInjection = injected;

            stage = DecompileStage.SourceEmission;
            EntryPointSelection entry = EntryPointResolver.Resolve(injected, symbols.EntryPoint);
            EmitLanguage language = EmitLanguageSelector.Select(injected, entry);
            EmissionPlan plan = BuildPlan(language, entry, shaderModel, frontend.InputSignature, options.VertexInputs, flattened);
            string source = Emit(injected, symbols, plan);

            result.Success = true;
            result.FailedStage = DecompileStage.Completed;
            result.SourceCode = source;
            result.SourceLanguage = language == EmitLanguage.Glsl ? "glsl" : "hlsl";
            result.SourceFileExtension = language == EmitLanguage.Glsl ? ".glsl" : ".hlsl";
            result.Stage = entry.Stage;
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
    // the module state that explains it. A bare "emission failed" is unactionable;
    // the same message with the patch plan and built-in decorations attached
    // usually is not.

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

    private (byte[] Spirv, List<FlattenedBlock> Flattened) Inject(byte[] spirv, SerializedProgramData symbols)
    {
        try
        {
            // Mark the members nothing will ever have a name for FIRST, so the
            // scan below sees them and the real names injected afterwards
            // overwrite whichever of them turn out to be recoverable.
            spirv = AnonymousMemberNamer.Apply(spirv);

            var flattened = new List<FlattenedBlock>();
            if (symbols.GetResourceBindingCount() == 0)
            {
                return (spirv, flattened);
            }

            List<DescriptorBindingInfo> bindings = BindingScanner.Scan(spirv);
            List<NamePatch> names = new ResourceNamePlanner(_structurer.GetResolvedBlockName).Plan(bindings, symbols);
            List<MemberNamePatch> members = new BlockMemberNamePlanner(_structurer.GetResolvedBlockName).Plan(bindings, symbols);

            CollectFlattenedBlocks(bindings, names, members, flattened);
            return (DebugNameInjector.Inject(spirv, names, members), flattened);
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

    /// <summary>
    /// For every constant buffer whose variable this pass named, the member
    /// indices that received a recovered symbol rather than a generated marker.
    /// The backend will flatten each block to <c>&lt;variable&gt;_&lt;member&gt;</c>
    /// identifiers; the plan records which of those the driver restores to the
    /// bare symbol, by id, so the spelling is read back from the backend rather
    /// than predicted.
    /// </summary>
    private static void CollectFlattenedBlocks(
        List<DescriptorBindingInfo> bindings,
        List<NamePatch> names,
        List<MemberNamePatch> members,
        List<FlattenedBlock> flattened)
    {
        foreach (DescriptorBindingInfo binding in bindings)
        {
            if (binding.Kind != DescriptorKind.UniformBuffer || binding.StructTypeId is not uint structTypeId || !IsNamed(names, binding.Id))
            {
                continue;
            }

            var authored = new List<uint>();
            foreach (MemberNamePatch patch in members)
            {
                if (patch.StructTypeId == structTypeId && !GeneratedNames.IsGenerated(patch.Name))
                {
                    authored.Add(patch.MemberIndex);
                }
            }

            if (authored.Count > 0)
            {
                flattened.Add(new FlattenedBlock(binding.Id, structTypeId, authored));
            }
        }
    }

    private static bool IsNamed(List<NamePatch> names, uint id)
    {
        foreach (NamePatch patch in names)
        {
            if (patch.Id == id)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The HLSL facts — vertex semantics and flattened blocks — are planned only
    /// for an HLSL emission; GLSL keeps its locations and never flattens a block.
    ///
    /// Vertex semantics come from the container's own input signature when there
    /// is one. A bare SPIR-V input has no signature — it carries only locations,
    /// numbered over the inputs the shader declares, so which channel a location is
    /// is the engine's binding and arrives in <see cref="DecompileOptions.VertexInputs"/>.
    /// System values are skipped: the backend already emits their built-in semantic
    /// and a remap would only collide with it.
    /// </summary>
    private static EmissionPlan BuildPlan(
        EmitLanguage language,
        EntryPointSelection entry,
        uint shaderModel,
        IReadOnlyList<InputSignatureElement> signature,
        IReadOnlyList<VertexInputBinding>? vertexInputs,
        List<FlattenedBlock> flattened)
    {
        var plan = new EmissionPlan { Language = language, EntryPoint = entry, ShaderModel = shaderModel };
        if (language != EmitLanguage.Hlsl)
        {
            return plan;
        }

        plan.FlattenedBlocks.AddRange(flattened);

        if (entry.Stage != PipelineStage.Vertex)
        {
            return plan;
        }

        if (signature.Count > 0)
        {
            foreach (InputSignatureElement element in signature)
            {
                if (element.SemanticName.StartsWith("SV_", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                plan.VertexAttributes.Add(new VertexAttributeSemantic(element.Register, element.SemanticName + element.SemanticIndex));
            }
        }
        else if (vertexInputs is not null)
        {
            foreach (VertexInputBinding input in vertexInputs)
            {
                plan.VertexAttributes.Add(new VertexAttributeSemantic(input.Location, input.Semantic));
            }
        }

        return plan;
    }

    private string Emit(byte[] spirv, SerializedProgramData symbols, EmissionPlan plan)
    {
        string? source = _driver.Emit(spirv, plan);
        if (source is not null)
        {
            return source;
        }

        throw new InvalidOperationException(
            $"Source emission failed after symbol injection. {_driver.LastFailure}{Environment.NewLine}" +
            $"{ModuleReports.DescribePatchPlan(spirv, symbols, _structurer.GetResolvedBlockName)}{Environment.NewLine}" +
            $"{ModuleReports.DescribeBuiltInDecorations(spirv)}");
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
        result.NativeToolDiagnostics = _frontend.LastFailure ?? _driver.LastFailure;

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
