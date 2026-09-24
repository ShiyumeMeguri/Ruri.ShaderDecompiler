
namespace Ruri.ShaderTools.Pipeline.Naming;

/// <summary>
/// Names each sampler binding after what it really samples with, in a form a host shader importer parses back to
/// that same thing:
///
///   * an inline sampler -- a Unity program's <c>m_Samplers</c> entry states its filter, wrap and comparison -- is
///     named by that state: <c>sampler_LinearRepeat</c>, <c>sampler_TrilinearClamp</c>,
///     <c>sampler_LinearClampCompare</c> (see <see cref="InlineSamplerState"/>);
///   * a texture's own sampler -- the texture's <c>m_SamplerIndex</c> names the slot -- takes that texture's import
///     settings, and is named <c>sampler&lt;TextureName&gt;</c>, the form that inherits them;
///   * a sampler the symbol table states nothing about -- an Unreal table carries a bind index where Unity keeps
///     the state -- gets the next unused filter+wrap form from <see cref="Pool"/>, because a host importer rejects
///     any other name outright ("Unrecognized sampler 'sampler_N'"). Such a name records no state.
///
/// Two slots that come out under one name are kept apart by their set and binding, which the importer still
/// parses.
/// </summary>
internal sealed class InlineSamplerNamer
{
    /// <summary>
    /// Filter × wrap combinations a host importer parses verbatim, handed out in order to the samplers whose
    /// state the symbol table does not state.
    /// </summary>
    private static readonly string[] Pool =
    {
        "sampler_LinearClamp",
        "sampler_LinearRepeat",
        "sampler_LinearMirror",
        "sampler_LinearMirrorOnce",
        "sampler_PointClamp",
        "sampler_PointRepeat",
        "sampler_PointMirror",
        "sampler_PointMirrorOnce",
        "sampler_TrilinearClamp",
        "sampler_TrilinearRepeat",
        "sampler_TrilinearMirror",
        "sampler_TrilinearMirrorOnce",
    };

    private readonly HashSet<string> _used = new(StringComparer.Ordinal);

    /// <summary>Name for one sampler binding.</summary>
    public string Next(int set, int binding, SerializedProgramData symbols)
    {
        string name = TruthfulName(set, binding, symbols) ?? NextPooled();
        if (!_used.Add(name))
        {
            name = $"{name}_s{set}_b{binding}";
            _used.Add(name);
        }
        return name;
    }

    /// <summary>
    /// What the symbol table says sits in this slot. The candidates are every inline sampler state stated for
    /// the binding number and every texture whose own sampler it is. A table with descriptor sets keeps a slot's
    /// set only in its set bindings, so there the set binding's own name picks the candidate -- the Unity
    /// decoding writes it from the program's own lists, before any names are shared between programs. A table
    /// without sets has one register space, where one binding number is one slot and two different candidates
    /// for it are refused.
    /// </summary>
    private static string? TruthfulName(int set, int binding, SerializedProgramData symbols)
    {
        string[] candidates = symbols.SamplerParameters
            .Where(sampler => sampler.InlineState is not null && sampler.BindPoint == binding)
            .Select(sampler => sampler.InlineState!.Name)
            .Concat(symbols.TextureParameters
                .Where(texture => texture.SamplerIndex == binding && !string.IsNullOrWhiteSpace(texture.Name))
                .Select(texture => "sampler" + texture.Name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (symbols.DescriptorSetParameters.Count > 0)
        {
            string? declared = symbols.NameOfSetBinding(set, binding, ShaderResourceType.Sampler);
            return declared is not null && candidates.Contains(declared, StringComparer.Ordinal) ? declared : null;
        }
        return candidates.Length switch
        {
            0 => null,
            1 => candidates[0],
            _ => throw new InvalidDataException(
                $"sampler register {binding} is stated as {string.Join(" and ", candidates)} at once."),
        };
    }

    private string NextPooled()
    {
        foreach (string candidate in Pool)
        {
            if (!_used.Contains(candidate))
            {
                return candidate;
            }
        }

        // Past the pool -- twelve distinct unstated samplers in one shader. The anisotropic variants are also
        // recognised, so keep growing there.
        for (int aniso = 2; aniso <= 16; aniso *= 2)
        {
            foreach (string filterWrap in Pool)
            {
                string candidate = $"{filterWrap}_aniso{aniso}";
                if (!_used.Contains(candidate))
                {
                    return candidate;
                }
            }
        }

        // Beyond that, give up on recognisability but keep uniqueness: the importer will still complain, but
        // about a count rather than about a silent collision.
        return $"sampler_LinearClamp_overflow_{_used.Count}";
    }
}
