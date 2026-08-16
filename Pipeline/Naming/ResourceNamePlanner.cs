// The symbol table yields resource bindings as a named tuple; alias it so the
// planners read as if it were a type without inventing a parallel one.
using ResourceBinding = (string Name, int Binding, int Set, Ruri.ShaderTools.ShaderResourceType Type, char RegisterType);

using Ruri.ShaderTools.Spirv.SymbolInjection;

namespace Ruri.ShaderTools.Pipeline.Naming;

/// <summary>
/// Decides the name for every resource VARIABLE in a module — textures, buffers,
/// samplers, and the constant-buffer block types.
///
/// Two problems this solves that are not obvious from the name:
///
/// 1. CROSS-BINDING COLLISIONS. A shader can legitimately expose the same logical
///    name at two different binding points — a material constant buffer and a
///    bindless index table for the same material, say. Giving both struct types
///    the same alias makes the emitter inherit the first one's layout assumptions
///    for the second, and it fails with "member 0 cannot be expressed with HLSL
///    packing". Later occurrences are suffixed with their binding, leaving the
///    single-binding case — which is nearly everything — untouched.
///
/// 2. THE ALIAS/VARIABLE SPLIT. A constant buffer's struct type gets the
///    <c>type.&lt;Name&gt;</c> alias while its variable gets the bare name. The two
///    strings MUST differ: if they collide, the emitter's uniquify pass appends a
///    <c>_1</c> that then prefixes every member name.
///
/// The struct type is always named, including for buffers whose rewrite was
/// abandoned — that is the only chance those get a real block name instead of a
/// synthetic fallback.
/// </summary>
internal sealed class ResourceNamePlanner
{
    private readonly Func<int, int, string?> _resolveBlockName;

    /// <param name="resolveBlockName">
    /// <c>(set, binding) → structured name</c>, supplied by the constant-buffer
    /// structurer. Lets a built-in-bound slot recover its friendly name whether or
    /// not its rewrite succeeded.
    /// </param>
    public ResourceNamePlanner(Func<int, int, string?> resolveBlockName) => _resolveBlockName = resolveBlockName;

    public List<NamePatch> Plan(IReadOnlyList<DescriptorBindingInfo> bindings, SerializedProgramData symbols)
    {
        var patches = new List<NamePatch>();
        var patchedIds = new HashSet<uint>();
        var variableNameUses = new Dictionary<string, int>(StringComparer.Ordinal);
        var structAliasUses = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (ResourceBinding resource in symbols.EnumerateResourceBindings())
        {
            if (string.IsNullOrWhiteSpace(resource.Name))
            {
                continue;
            }

            foreach (DescriptorBindingInfo binding in Match(bindings, resource))
            {
                if (patchedIds.Contains(binding.Id))
                {
                    continue;
                }

                string baseName = ResolveName(resource, binding);

                patches.Add(new NamePatch(binding.Id, Disambiguate(variableNameUses, baseName, binding.Binding)));
                patchedIds.Add(binding.Id);

                if (binding.Kind != DescriptorKind.UniformBuffer || binding.StructTypeId is not > 0)
                {
                    continue;
                }

                string alias = Disambiguate(structAliasUses, "type." + baseName, binding.Binding);
                if (patchedIds.Add(binding.StructTypeId.Value))
                {
                    patches.Add(new NamePatch(binding.StructTypeId.Value, alias));
                }
            }
        }

        ApplySamplerNames(bindings, symbols, patches, patchedIds);
        return patches;
    }

    /// <summary>
    /// Samplers are named last and OVERRIDE anything the loop above wrote for
    /// them — see <see cref="InlineSamplerNamer"/> for why a bytecode-accurate
    /// sampler name is the wrong answer.
    /// </summary>
    private static void ApplySamplerNames(
        IReadOnlyList<DescriptorBindingInfo> bindings,
        SerializedProgramData symbols,
        List<NamePatch> patches,
        HashSet<uint> patchedIds)
    {
        var namer = new InlineSamplerNamer();

        foreach (DescriptorBindingInfo binding in bindings)
        {
            if (binding.Kind != DescriptorKind.Sampler)
            {
                continue;
            }

            string name = namer.Next(binding.Set, binding.Binding, symbols);

            if (patchedIds.Contains(binding.Id))
            {
                int existing = patches.FindIndex(patch => patch.Id == binding.Id);
                if (existing >= 0)
                {
                    patches.RemoveAt(existing);
                }
            }

            patches.Add(new NamePatch(binding.Id, name));
            patchedIds.Add(binding.Id);
        }
    }

    private static string Disambiguate(Dictionary<string, int> uses, string baseName, int binding)
    {
        string name = uses.TryGetValue(baseName, out int previous) && previous > 0
            ? $"{baseName}_b{binding}"
            : baseName;

        uses[baseName] = uses.GetValueOrDefault(baseName) + 1;
        return name;
    }

    private string ResolveName(ResourceBinding resource, DescriptorBindingInfo binding)
        => binding.Kind == DescriptorKind.UniformBuffer
            ? _resolveBlockName(resource.Set, resource.Binding) ?? resource.Name
            : resource.Name;

    public static IEnumerable<DescriptorBindingInfo> Match(IReadOnlyList<DescriptorBindingInfo> bindings, ResourceBinding resource)
    {
        foreach (DescriptorBindingInfo binding in bindings)
        {
            if (binding.Set == resource.Set && binding.Binding == resource.Binding && Matches(resource.RegisterType, binding.Kind))
            {
                yield return binding;
            }
        }
    }

    /// <summary>
    /// Which D3D register class a descriptor kind can pair with.
    ///
    /// <see cref="DescriptorKind.StorageBuffer"/> accepts BOTH <c>t</c> and
    /// <c>u</c>: read-only byte-address and structured buffers come through as
    /// storage buffers in SPIR-V but bind on <c>t</c>, not <c>u</c>. Accepting
    /// only <c>u</c> leaves every read-only structured buffer with a synthetic
    /// name while UAVs still match correctly via their own (set, binding).
    /// </summary>
    private static bool Matches(char registerType, DescriptorKind kind) => kind switch
    {
        DescriptorKind.UniformBuffer => registerType == 'b',
        DescriptorKind.Sampler => registerType == 's',
        DescriptorKind.SampledImage => registerType == 't',
        DescriptorKind.StorageBuffer => registerType is 't' or 'u',
        _ => false,
    };
}
