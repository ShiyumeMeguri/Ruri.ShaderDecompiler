using Ruri.ShaderTools.Spirv;
using Ruri.ShaderTools.Spirv.Analysis;

namespace Ruri.ShaderTools.Spirv.ConstantBuffers;

/// <summary>
/// The world one constant-buffer structuring run operates on.
///
/// Every stage reads what earlier stages produced and writes its own outputs
/// here. Nothing is passed between stages any other way, and no stage reaches
/// back into the module for a fact an earlier stage already recorded — that one
/// rule is what keeps the pipeline linear instead of quadratic.
///
/// Analysis snapshots (<see cref="Shape"/>, <see cref="Definitions"/>,
/// <see cref="Constants"/>) are built ONCE, before any mutation. Stages that
/// mutate and then need a fresh view build their own local index rather than
/// silently trusting a stale one.
/// </summary>
internal sealed class StructuringContext
{
    public StructuringContext(byte[] inputSpirv, SerializedProgramData symbols)
    {
        InputSpirv = inputSpirv;
        Symbols = symbols;
    }

    // --- inputs -------------------------------------------------------------
    public byte[] InputSpirv { get; }
    public SerializedProgramData Symbols { get; }

    /// <summary>
    /// Per-stage decision log, surfaced verbatim in the failure dump. A line here
    /// should say what a stage DECIDED and why — "[CB] rewrite planned with 12
    /// members", "[CB] access translation unsupported for resultId=…" — so a
    /// flat constant buffer in the output is always attributable.
    /// </summary>
    public List<string> Summary { get; } = new();

    // --- discovery ----------------------------------------------------------
    public SpirvModule Module { get; set; } = null!;
    public ModuleShape Shape { get; set; } = null!;
    public ConstantValueMap Constants { get; set; } = null!;
    public ResultIdTable Definitions { get; set; } = null!;
    public SpirvTypeInterner Types { get; set; } = null!;

    /// <summary>Symbol-matched constant buffers, before any layout work.</summary>
    public List<FlatBlockView> Blocks { get; set; } = new();

    // --- planning -----------------------------------------------------------
    /// <summary>
    /// Buffers that survived layout, type materialisation AND access admission.
    /// Stages narrow this list; nothing ever half-applies.
    /// </summary>
    public List<BlockRewritePlan> Plans { get; set; } = new();

    // --- emission -----------------------------------------------------------
    /// <summary>
    /// Access chains this run took ownership of, keyed by result id. A null
    /// <c>Translation</c> marks a chain left in place for component-read
    /// lowering.
    /// </summary>
    public Dictionary<uint, RetargetedChain> RetargetedChains { get; set; } = new();

    /// <summary>member type id → pointer-to-uniform type id.</summary>
    public Dictionary<uint, uint> UniformPointerTypes { get; set; } = new();

    /// <summary>Loads consuming a retargeted chain, keyed by result id.</summary>
    public Dictionary<uint, TrackedLoad> TrackedLoads { get; set; } = new();

    /// <summary>Bitcasts that took part in a lowering and are now dead.</summary>
    public Dictionary<uint, SpirvInstruction> ProcessedBitcasts { get; set; } = new();

    // --- outputs ------------------------------------------------------------
    /// <summary>
    /// <c>(set, binding)</c> → resolved buffer name. Consumed by symbol
    /// injection so a binding gets its friendly name even when its rewrite was
    /// abandoned.
    /// </summary>
    public Dictionary<(int Set, int Binding), string> ResolvedBlockNames { get; } = new();

    public bool RewriteApplied { get; set; }

    public void Note(string message) => Summary.Add(message);

    public void Note(BlockRewritePlan plan, string message) => Summary.Add($"[{plan.Name}] {message}");

    public void Note(FlatBlockView block, string message) => Summary.Add($"[{block.Binding.Name}] {message}");
}
