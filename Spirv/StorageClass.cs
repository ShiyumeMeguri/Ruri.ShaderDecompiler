namespace Ruri.ShaderTools.Spirv;

/// <summary>
/// SPIR-V storage classes — operand slot 2 of <c>OpTypePointer</c> and slot 3
/// of <c>OpVariable</c>.
///
/// Only the classes this toolchain discriminates on are listed. The one that
/// matters everywhere is <see cref="Uniform"/>: it is what makes a struct-backed
/// variable a candidate constant buffer, and (together with
/// <see cref="Decoration.BufferBlock"/>) what separates a cbuffer from an SSBO
/// in pre-1.3 modules.
/// </summary>
public static class StorageClass
{
    public const uint UniformConstant = 0;
    public const uint Input = 1;
    public const uint Uniform = 2;
    public const uint Output = 3;
    public const uint Function = 7;

    /// <summary>SPIR-V 1.3+ dedicated SSBO storage class.</summary>
    public const uint StorageBuffer = 12;
}
