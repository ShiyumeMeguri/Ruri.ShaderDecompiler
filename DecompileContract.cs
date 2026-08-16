using Ruri.ShaderTools.Unity;

namespace Ruri.ShaderTools;

/// <summary>
/// Container format of a compiled shader binary.
///
/// Names the CONTAINER, not the shader model: a DXBC blob and a DXIL blob can
/// both arrive wrapped in a DXBC envelope, and which one it is decides which
/// front end runs.
/// </summary>
public enum ShaderBinaryFormat
{
    /// <summary>Detect from the binary's magic bytes.</summary>
    Unknown = 0,

    /// <summary>Legacy DXBC container (shader model 5.x bytecode).</summary>
    Dxbc,

    /// <summary>DXIL — raw LLVM bitcode, or a container holding a DXIL chunk.</summary>
    Dxil,

    /// <summary>SPIR-V, already in the pipeline's intermediate form.</summary>
    SpirV,
}

/// <summary>
/// Where a decompile got to. Reported on failure so a dump is attributable to a
/// stage without reading a stack trace.
/// </summary>
public enum DecompileStage
{
    NotStarted = 0,

    /// <summary>Compiled binary → SPIR-V.</summary>
    FrontendConversion,

    /// <summary>Scalar constant-buffer layout normalised to <c>float4</c>.</summary>
    ScalarLayoutNormalization,

    /// <summary>Flat constant buffers rewritten into named block members.</summary>
    ConstantBufferStructuring,

    /// <summary>Host callback enriching symbols with SPIR-V-derived facts.</summary>
    SymbolEnrichment,

    /// <summary>Recovered names written into the module's debug section.</summary>
    SymbolInjection,

    /// <summary>SPIR-V → high-level source.</summary>
    SourceEmission,

    Completed,
}

/// <summary>
/// Engine-agnostic decompile settings.
///
/// The decompiler knows nothing about any specific engine. A host builds a
/// complete <see cref="Symbols"/> table and passes it in; the pipeline then runs
/// the universal route — convert to SPIR-V, normalise layout, structure constant
/// buffers, inject symbols, emit source.
/// </summary>
public sealed class DecompileOptions
{
    /// <summary>Input container format. Detected from magic bytes when unset.</summary>
    public ShaderBinaryFormat Format { get; init; } = ShaderBinaryFormat.Unknown;

    /// <summary>Engine-supplied symbols. Without them the output compiles but is unnamed.</summary>
    public SerializedProgramData? Symbols { get; init; }

    /// <summary>Optional Unity shader-asset context, carried through to the result.</summary>
    public UnityShaderMetadata? UnityMetadata { get; init; }

    /// <summary>Target shader model for the source backend.</summary>
    public uint ShaderModel { get; init; } = 51;

    /// <summary>
    /// Escape hatch invoked after constant-buffer structuring and BEFORE symbol
    /// injection, with the structured SPIR-V (read-only) and the symbol table to
    /// mutate in place.
    ///
    /// It exists for symbol facts that can only be derived WITH SPIR-V context —
    /// inferring a texture's name from the sampled-image pair it forms with an
    /// already-named sampler, for instance, which is unanswerable from engine
    /// data alone. Name injection itself still happens inside the pipeline; this
    /// is purely for accumulating symbols.
    /// </summary>
    public Action<byte[], SerializedProgramData>? SymbolEnricher { get; init; }

    /// <summary>
    /// When set, a failed decompile writes every intermediate artifact here:
    /// input binary, SPIR-V after each stage, symbols, structuring log, captured
    /// native diagnostics, and the error.
    /// </summary>
    public string? DebugDumpDirectory { get; init; }

    /// <summary>File-name prefix for the dump.</summary>
    public string? DebugDumpStem { get; init; }
}

/// <summary>
/// Outcome of one decompile, including — on failure — every intermediate needed
/// to diagnose it offline.
///
/// The per-stage SPIR-V snapshots are populated on SUCCESS TOO, so a caller can
/// diff stages of a shader that decompiled but decompiled wrongly. That is the
/// more common failure in practice than an outright crash.
/// </summary>
public sealed class DecompileResult
{
    public bool Success { get; set; }

    public string? SourceCode { get; set; }
    public string SourceLanguage { get; set; } = "hlsl";
    public string SourceFileExtension { get; set; } = ".hlsl";

    public string? ErrorMessage { get; set; }

    /// <summary>How far the pipeline got.</summary>
    public DecompileStage FailedStage { get; set; } = DecompileStage.NotStarted;

    // --- per-stage SPIR-V ---------------------------------------------------

    /// <summary>Straight out of the front end, after layout normalisation.</summary>
    public byte[]? SpirvAfterFrontend { get; set; }

    /// <summary>After constant-buffer structuring. This is what
    /// <see cref="DecompileOptions.SymbolEnricher"/> sees.</summary>
    public byte[]? SpirvAfterStructuring { get; set; }

    /// <summary>After symbol injection — the module handed to the source backend.</summary>
    public byte[]? SpirvAfterSymbolInjection { get; set; }

    /// <summary>Deepest SPIR-V the run produced, whatever stage it reached.</summary>
    public byte[]? FinalSpirv { get; set; }

    // --- diagnostics --------------------------------------------------------

    /// <summary>Message from the last failed native tool call.</summary>
    public string? NativeToolDiagnostics { get; set; }

    /// <summary>What name injection planned to do.</summary>
    public string? PatchPlanReport { get; set; }

    /// <summary>Built-in decorations present in the module.</summary>
    public string? BuiltInDecorationReport { get; set; }

    /// <summary>Per-stage decision log from constant-buffer structuring.</summary>
    public string? StructuringLog { get; set; }

    /// <summary>Where the failure dump was written, if one was.</summary>
    public string? DebugDumpDirectory { get; set; }

    // --- symbols ------------------------------------------------------------

    /// <summary>The symbol table as it stood at the end, enrichment included.</summary>
    public SerializedProgramData? FinalSymbols { get; set; }

    public UnityShaderMetadata? FinalUnityMetadata { get; set; }
}
