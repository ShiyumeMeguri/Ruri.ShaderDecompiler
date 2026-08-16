
namespace Ruri.ShaderTools.Spirv.ConstantBuffers;

/// <summary>
/// Identity of a constant buffer once its engine symbol has been matched to a
/// SPIR-V variable: name plus the <c>(set, binding)</c> that found it.
/// </summary>
internal sealed class SymbolBlockBinding
{
    public string Name { get; set; } = string.Empty;
    public int Binding { get; set; }
    public int Set { get; set; }
}

/// <summary>
/// The SPIR-V side of a matched constant buffer, in the shape compilers emit it:
/// a Uniform variable pointing at a one-member wrapper struct whose member is a
/// fixed-length <c>float4</c> array.
///
/// <see cref="ArrayLength"/> and <see cref="ArrayStride"/> are the hard bounds
/// the structured layout must fit inside — a member past the array is an
/// out-of-bounds read in the rewritten module, and a stride other than 16 means
/// this was never compiled as a constant buffer in the first place.
/// </summary>
internal sealed class FlatBlockView
{
    public uint VariableId { get; set; }
    public uint PointerTypeId { get; set; }
    public uint StructTypeId { get; set; }
    public uint ArrayTypeId { get; set; }
    public uint ElementTypeId { get; set; }
    public int ArrayLength { get; set; }
    public int ArrayStride { get; set; }

    public SymbolBlockBinding Binding { get; set; } = null!;
    public ConstantBufferParameter Symbol { get; set; } = null!;
}

/// <summary>
/// One constant buffer's rewrite, accumulated across the pipeline: the layout
/// derived from symbols, the SPIR-V type ids materialised for each member, and
/// the freshly allocated block-struct and pointer ids.
///
/// A plan only reaches the emitting stages if it survived layout validation,
/// type materialisation AND access-chain admission — the pipeline drops plans
/// rather than half-applying them.
/// </summary>
internal sealed class BlockRewritePlan
{
    public FlatBlockView Block { get; set; } = null!;
    public BlockLayout Layout { get; set; } = null!;

    public uint NewStructTypeId { get; set; }
    public uint NewPointerTypeId { get; set; }

    /// <summary>Member type ids, positionally aligned with <c>Layout.Members</c>.</summary>
    public List<uint> MemberTypeIds { get; set; } = new();

    public string Name => Block.Binding.Name;
}
