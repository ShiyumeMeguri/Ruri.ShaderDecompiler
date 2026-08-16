using Ruri.ShaderTools.Pipeline;
using Ruri.ShaderTools.Spirv.ConstantBuffers.Stages;

namespace Ruri.ShaderTools.Spirv.ConstantBuffers;

/// <summary>
/// Rewrites compiled constant buffers from the opaque
/// <c>float4 _m0[N]</c> form back into named, typed block members.
///
/// This is the transform that makes decompiled shader source readable. Everything
/// else in the pipeline either feeds it (symbols in, SPIR-V in) or consumes what
/// it produced (name injection, source emission).
///
/// The stage order below IS the algorithm — discover, plan, admit, emit, lower,
/// prune — and it is declared here rather than encoded in file names, so
/// inserting or reordering a stage is a one-line edit that cannot drift out of
/// sync with anything.
///
/// Two halt points, both all-or-nothing on purpose:
///   * nothing matched  — no symbol names a buffer this module declares.
///   * nothing admitted — every candidate had an access chain that could not be
///     translated, and a half-rewritten buffer is worse than an unrewritten one.
///
/// Both halts still RE-SERIALIZE the module rather than handing back the input
/// bytes. The aliased-binding merge is unconditional, metadata-independent
/// cleanup that has already landed by then; returning the pristine input would
/// silently revert it and the emitter would reproduce the duplicate variables
/// verbatim under auto-generated names.
/// </summary>
internal sealed class ConstantBufferStructurer
{
    private static readonly StageSchedule<StructuringContext> Schedule = new(
        StageSchedule<StructuringContext>.Stage(StageNames.ModuleShapeScan, ModuleShapeScanStage.Run),
        StageSchedule<StructuringContext>.Stage(StageNames.AliasedBindingMerge, AliasedBindingMergeStage.Run),
        StageSchedule<StructuringContext>.Stage(StageNames.SymbolBufferMatch, SymbolBufferMatchStage.Run),
        StageSchedule<StructuringContext>.Stage(StageNames.BlockLayout, BlockLayoutStage.Run),
        StageSchedule<StructuringContext>.Stage(StageNames.MemberType, MemberTypeStage.Run),
        StageSchedule<StructuringContext>.Stage(StageNames.AccessAdmission, AccessAdmissionStage.Run),
        StageSchedule<StructuringContext>.Stage(StageNames.BlockTypeEmit, BlockTypeEmitStage.Run),
        StageSchedule<StructuringContext>.Stage(StageNames.AccessRetarget, AccessRetargetStage.Run),
        StageSchedule<StructuringContext>.Stage(StageNames.ComponentReadLowering, ComponentReadLoweringStage.Run),
        StageSchedule<StructuringContext>.Stage(StageNames.DeadAccessPrune, DeadAccessPruneStage.Run));

    private static class StageNames
    {
        public const string ModuleShapeScan = "module-shape-scan";
        public const string AliasedBindingMerge = "aliased-binding-merge";
        public const string SymbolBufferMatch = "symbol-buffer-match";
        public const string BlockLayout = "block-layout";
        public const string MemberType = "member-type";
        public const string AccessAdmission = "access-admission";
        public const string BlockTypeEmit = "block-type-emit";
        public const string AccessRetarget = "access-retarget";
        public const string ComponentReadLowering = "component-read-lowering";
        public const string DeadAccessPrune = "dead-access-prune";
    }

    private readonly Dictionary<(int Set, int Binding), string> _resolvedBlockNames = new();

    /// <summary>True when at least one constant buffer was actually structured.</summary>
    public bool LastRewriteApplied { get; private set; }

    /// <summary>Per-stage decision log from the most recent run. Surfaced in the
    /// failure dump so an unstructured buffer is always attributable.</summary>
    public string LastRewriteSummary { get; private set; } = string.Empty;

    /// <summary>
    /// The resolved buffer name for a binding, if this run claimed it. Symbol
    /// injection uses it to give a block its friendly name even when the rewrite
    /// itself was abandoned.
    /// </summary>
    public string? GetResolvedBlockName(int set, int binding)
        => _resolvedBlockNames.TryGetValue((set, binding), out string? name) ? name : null;

    public byte[] Rewrite(byte[] spirv, SerializedProgramData symbols)
    {
        LastRewriteApplied = false;
        _resolvedBlockNames.Clear();

        var context = new StructuringContext(spirv, symbols);
        string? haltedAt = Schedule.Execute(context);

        LastRewriteApplied = haltedAt is null && context.Plans.Count > 0;
        LastRewriteSummary = context.Summary.Count == 0
            ? DescribeEmptyRun(haltedAt)
            : string.Join(Environment.NewLine, context.Summary);

        foreach (KeyValuePair<(int Set, int Binding), string> entry in context.ResolvedBlockNames)
        {
            _resolvedBlockNames[entry.Key] = entry.Value;
        }

        return context.Module.ToBytes();
    }

    private static string DescribeEmptyRun(string? haltedAt) => haltedAt switch
    {
        StageNames.SymbolBufferMatch => "No flat uniform buffers matched metadata bindings.",
        StageNames.AccessAdmission => "No rewrites planned.",
        _ => "No rewrites planned.",
    };
}
