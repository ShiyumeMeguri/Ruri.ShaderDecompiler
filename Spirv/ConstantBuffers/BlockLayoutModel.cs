using Ruri.ShaderTools.Spirv;

namespace Ruri.ShaderTools.Spirv.ConstantBuffers;

/// <summary>What a constant-buffer member fundamentally is.</summary>
internal enum MemberShapeKind
{
    Scalar,
    Vector,
    Matrix,
    Struct,
}

/// <summary>
/// The logical shape of one constant-buffer member, derived from engine symbol
/// metadata and independent of SPIR-V.
///
/// This is the pivot of the whole rewrite: the engine describes members in
/// terms of type and byte offset, the bytecode addresses them as
/// <c>float4[register]</c>, and the shape is what lets one be expressed as the
/// other. Type materialisation reads it to mint SPIR-V types; access
/// translation reads it to decide whether a given byte lands on a vector
/// component, a matrix column, or an array element.
/// </summary>
internal sealed class MemberShape
{
    public MemberShapeKind Kind { get; set; }
    public ScalarKind ScalarKind { get; set; }

    /// <summary>Component count for vectors, row count for matrices, 1 for scalars.</summary>
    public int Rows { get; set; }

    /// <summary>Column count for matrices, 1 otherwise.</summary>
    public int Columns { get; set; }

    /// <summary>Element count; 1 means "not an array".</summary>
    public int ArrayLength { get; set; }

    public int SecondaryArrayLength { get; set; }

    /// <summary>Total byte span the member declares, arrays included.</summary>
    public int DeclaredByteSize { get; set; }

    /// <summary>Byte offset this shape was derived from. Diagnostic.</summary>
    public int SourceByteOffset { get; set; }

    public bool IsMatrix { get; set; }

    // --- struct-only ------------------------------------------------------
    public List<BlockMemberLayout>? StructMembers { get; set; }

    /// <summary>Byte size of ONE struct element.</summary>
    public int StructByteSize { get; set; }

    public string StructName { get; set; } = string.Empty;
}

/// <summary>
/// One member's placement inside a rewritten constant-buffer block: where it
/// starts, how many 16-byte registers it covers, and the SPIR-V type ids needed
/// to address it at every depth.
///
/// The three extra type ids exist because an access chain's RESULT TYPE has to
/// match how deep the chain actually walks. Reusing the member's own type for a
/// sub-component access produces a chain that claims to yield a vec4 while
/// addressing a single float — spirv-cross rejects that with "Cannot subdivide a
/// scalar value", which was the single most common failure mode this model was
/// built to eliminate.
/// </summary>
internal sealed class BlockMemberLayout
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Byte offset within the block.</summary>
    public int ByteOffset { get; set; }

    /// <summary>Engine symbol this member came from; null for synthesized fillers.</summary>
    public NumericShaderParameter Metadata { get; set; } = null!;

    public MemberShape Shape { get; set; } = null!;

    public int RegisterOffset { get; set; }
    public int RegisterCount { get; set; }

    /// <summary>Full member type — array wrapper included.</summary>
    public uint ResolvedTypeId { get; set; }

    /// <summary>Component scalar. Result type of a per-component access.</summary>
    public uint ScalarTypeId { get; set; }

    /// <summary>Column vector of a matrix. Result type of <c>matrix[col]</c>.</summary>
    public uint ColumnVectorTypeId { get; set; }

    /// <summary>Element type of an array member. Result type after ONE array index.</summary>
    public uint ArrayElementTypeId { get; set; }

    /// <summary>
    /// Bytes this member occupies from <see cref="ByteOffset"/> onwards.
    ///
    /// Not simply <c>Shape.DeclaredByteSize</c>: a struct spans its element size
    /// times its array length, and a matrix spans a full 16-byte register per
    /// column regardless of row count (cbuffer packing rule), so both need their
    /// own arithmetic.
    /// </summary>
    public int SpanBytes => Shape.Kind switch
    {
        MemberShapeKind.Struct => Shape.StructByteSize * Math.Max(Shape.ArrayLength, 1),
        MemberShapeKind.Matrix => Shape.Columns * 16 * Math.Max(Shape.ArrayLength, 1),
        _ => Shape.DeclaredByteSize,
    };
}

/// <summary>
/// The full member list of one rewritten constant buffer, ordered by byte
/// offset and gap-free (see the gap filler).
/// </summary>
internal sealed class BlockLayout
{
    public List<BlockMemberLayout> Members { get; } = new();

    /// <summary>
    /// Registers needed to cover every member including padding. Asserts the new
    /// block still fits inside the original flat array.
    /// </summary>
    public int RequiredRegisterCount { get; set; }

    /// <summary>
    /// Highest register any REAL member touches, ignoring synthesized padding.
    /// Compared against the SPIR-V array length to decide whether the tail needs
    /// bridging.
    /// </summary>
    public int MaxUsedRegisterCount { get; set; }
}
