namespace Ruri.ShaderTools.Spirv;

/// <summary>
/// SPIR-V decoration ids, as they appear in operand slot 2 of
/// <c>OpDecorate</c> and slot 3 of <c>OpMemberDecorate</c>.
///
/// Split out of the opcode table because a decoration is a different concept
/// from an instruction: the same handful of ids drive resource binding
/// discovery, block layout, and matrix packing across every transform.
/// </summary>
public static class Decoration
{
    /// <summary>Layout decoration marking a uniform block (cbuffer).</summary>
    public const uint Block = 2;

    /// <summary>
    /// SPIR-V 1.0's way of marking a storage buffer: a <c>Uniform</c>-storage
    /// variable whose struct carries this. The dedicated <c>StorageBuffer</c>
    /// storage class only exists from SPIR-V 1.3 on, so without this check every
    /// SSBO in a 1.0 module — which is what Unity's Vulkan bytecode still is —
    /// reads as a uniform buffer and never matches its <c>t</c>/<c>u</c> register
    /// metadata.
    /// </summary>
    public const uint BufferBlock = 3;

    public const uint RowMajor = 4;
    public const uint ColMajor = 5;
    public const uint ArrayStride = 6;
    public const uint MatrixStride = 7;
    public const uint BuiltIn = 11;
    public const uint Location = 30;
    public const uint Binding = 33;
    public const uint DescriptorSet = 34;

    /// <summary>Byte offset of a struct member within its block.</summary>
    public const uint Offset = 35;
}
