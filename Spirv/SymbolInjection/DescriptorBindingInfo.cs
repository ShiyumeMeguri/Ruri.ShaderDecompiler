namespace Ruri.ShaderTools.Spirv.SymbolInjection;

/// <summary>
/// What a SPIR-V resource variable IS, as far as name injection cares.
///
/// Deliberately coarse. The distinction that matters is which D3D register class
/// a binding can legally pair with, and these five buckets are exactly that
/// partition — finer SPIR-V type detail would not change a single naming
/// decision.
/// </summary>
internal enum DescriptorKind
{
    Unknown,
    UniformBuffer,
    StorageBuffer,
    Sampler,
    SampledImage,
}

/// <summary>
/// One <c>(set, binding)</c> resource variable in a SPIR-V module: what kind of
/// descriptor it is, which struct type it backs, and where that struct's members
/// sit.
///
/// This is the join key between bytecode and engine symbols. The naming layer
/// matches these against the symbol table to decide which <c>OpName</c> and
/// <c>OpMemberName</c> to write.
/// </summary>
internal sealed class DescriptorBindingInfo
{
    public uint Id { get; set; }
    public int Set { get; set; }
    public int Binding { get; set; }
    public DescriptorKind Kind { get; set; }

    /// <summary>Backing struct type, for buffer-shaped bindings.</summary>
    public uint? StructTypeId { get; set; }

    public int StructMemberCount { get; set; }

    /// <summary>member index → byte offset, from <c>OpMemberDecorate Offset</c>.</summary>
    public Dictionary<int, uint> MemberOffsets { get; set; } = new();

    /// <summary>
    /// Member names ALREADY in the module, by member index.
    ///
    /// Needed because the injector must decide, per slot, between overriding with
    /// a symbol-derived name and preserving what the compiler baked in. Without
    /// seeing the existing decorations it could not deduplicate across them — and
    /// compilers do emit duplicate member names that collapse to the same
    /// identifier once sanitised, which is a hard compile error in the emitted
    /// source rather than a cosmetic problem.
    /// </summary>
    public Dictionary<int, string> CurrentMemberNames { get; set; } = new();

    /// <summary>The variable's existing <c>OpName</c>, if any.</summary>
    public string? CurrentName { get; set; }
}
