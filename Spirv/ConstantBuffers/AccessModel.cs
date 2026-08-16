using Ruri.ShaderTools.Spirv;

namespace Ruri.ShaderTools.Spirv.ConstantBuffers;

/// <summary>
/// A register index expression, decomposed into
/// <c>dynamicIndex * stride + constantOffset</c>.
///
/// Compilers rarely emit a bare index. <c>(instanceId &lt;&lt; 4) | 3</c>,
/// <c>(i * 256) + 5</c> and chains of those all mean "element i of a strided
/// array, plus a fixed offset", and recognising that shape independently of how
/// it was spelled is what lets dynamically indexed arrays be rewritten at all.
///
/// When nothing recognisable is found, the whole expression becomes an opaque
/// <see cref="DynamicIndexId"/> with stride 1 — deliberately permissive, so an
/// unusual index pattern degrades to "no member matched" instead of failing the
/// entire buffer.
/// </summary>
internal sealed class SlotExpression
{
    public int ConstantRegisterOffset { get; set; }
    public uint DynamicIndexId { get; set; }
    public int DynamicIndexStride { get; set; }

    public bool IsStatic => DynamicIndexId == 0;
}

/// <summary>
/// A parsed <c>cb._m0[register].component…</c> access, split into the register
/// slot and any trailing literal indices.
///
/// Forkable: the component-read lowering path clones a path and appends the
/// indices of a downstream <c>OpCompositeExtract</c>, turning "load the whole
/// register, then take .y" into "address .y directly".
/// </summary>
internal sealed class FlatAccessPath
{
    public SlotExpression Slot { get; set; } = new();
    public List<int> ExtraIndices { get; set; } = new();

    public FlatAccessPath Clone() => new()
    {
        Slot = new SlotExpression
        {
            ConstantRegisterOffset = Slot.ConstantRegisterOffset,
            DynamicIndexId = Slot.DynamicIndexId,
            DynamicIndexStride = Slot.DynamicIndexStride,
        },
        ExtraIndices = new List<int>(ExtraIndices),
    };

    /// <summary>Fork this path with <paramref name="extra"/> appended.</summary>
    public FlatAccessPath With(ReadOnlySpan<uint> extra)
    {
        FlatAccessPath forked = Clone();
        for (int i = 0; i < extra.Length; i++)
        {
            forked.ExtraIndices.Add(checked((int)extra[i]));
        }
        return forked;
    }
}

/// <summary>
/// The result of translating a flat access through a block layout: the index
/// list to feed back into <c>OpAccessChain</c>, plus the SPIR-V type of what it
/// addresses.
///
/// <see cref="Indices"/> is complete — block member index first, then any
/// array / column / component indices, with dynamic ids passed through in place.
/// </summary>
internal sealed class AccessTranslation
{
    public List<uint> Indices { get; set; } = new();
    public uint MemberTypeId { get; set; }
}

/// <summary>
/// An access chain the retarget stage has taken ownership of.
///
/// <see cref="Translation"/> is null for chains that took the component-read
/// path: the chain itself addresses a whole register that no single member
/// matches, so it stays as written and the lowering stage rewrites its
/// downstream extracts instead. The original path is retained precisely so that
/// stage can fork it per extract.
/// </summary>
internal sealed class RetargetedChain
{
    public uint AccessChainResultId { get; set; }
    public uint BaseVariableId { get; set; }
    public ushort InstructionOpCode { get; set; }
    public BlockRewritePlan Plan { get; set; } = null!;
    public FlatAccessPath OriginalAccessPath { get; set; } = null!;
    public AccessTranslation? Translation { get; set; }
}

/// <summary>
/// A load reading a retargeted access chain.
///
/// Holds the INSTRUCTION, never its index: the lowering stage inserts while it
/// iterates, so any cached position goes stale immediately. A handle stays valid
/// across insertions by construction.
/// </summary>
internal sealed class TrackedLoad
{
    public SpirvInstruction Instruction { get; set; } = null!;
    public uint ResultId { get; set; }
    public uint OriginalResultTypeId { get; set; }
    public bool HasComponentReaders { get; set; }
    public RetargetedChain AccessChain { get; set; } = null!;
}
