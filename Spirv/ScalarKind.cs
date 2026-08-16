namespace Ruri.ShaderTools.Spirv;

/// <summary>
/// The three 32-bit scalar component types this toolchain materialises.
/// Everything a constant buffer can hold reduces to a vector/matrix/array over
/// one of these; 16- and 64-bit widths never appear in a rewritten cbuffer
/// member because the source metadata cannot express them.
/// </summary>
internal enum ScalarKind
{
    Float,
    Int,
    UInt,
}
