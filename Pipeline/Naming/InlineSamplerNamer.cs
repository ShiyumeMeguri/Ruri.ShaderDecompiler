
namespace Ruri.ShaderTools.Pipeline.Naming;

/// <summary>
/// Synthesises sampler names a host shader importer will accept.
///
/// Engine symbol tables usually leave samplers unnamed and express the link the
/// other way round — each texture records which sampler slot it is read through.
/// Meanwhile a host importer rejects arbitrary sampler names outright
/// ("Unrecognized sampler 'sampler_N' — does not match any texture and is not a
/// recognized inline name"), so a name has to be MANUFACTURED, and it has to
/// follow one of the two forms the importer parses:
///
///   * <c>sampler&lt;TextureName&gt;</c>   — inherits that texture's import settings
///   * <c>sampler&lt;Filter&gt;&lt;Wrap&gt;</c> — a static sampler with the named state
///
/// The combined form used here — <c>sampler_LinearRepeat_Normal</c> — satisfies
/// the filter+wrap parser AND records the texture association, so the emitted
/// source stays self-documenting.
///
/// Any name the symbol table DID supply is overridden. Reflection-derived names
/// like <c>sampler_3</c> are accurate at the bytecode level and rejected by the
/// importer, so accuracy loses to acceptability here.
/// </summary>
internal sealed class InlineSamplerNamer
{
    /// <summary>
    /// Filter × wrap combinations a host importer parses verbatim to build static
    /// sampler state. Walked in order, so the first unpaired sampler gets the
    /// safest default and later ones move down the list — which also guarantees
    /// distinct names, since identical ones would be uniquified by the emitter
    /// into forms the importer then rejects.
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

    /// <summary>
    /// Name for one sampler binding: the next unused inline form, plus the paired
    /// texture's name when the symbols link one.
    /// </summary>
    public string Next(int set, int binding, SerializedProgramData symbols)
    {
        string inlineName = NextInline();
        _used.Add(inlineName);

        string? pairedTexture = PairedTextureSuffix(set, binding, symbols);
        return pairedTexture is null ? inlineName : $"{inlineName}_{pairedTexture}";
    }

    private string NextInline()
    {
        foreach (string candidate in Pool)
        {
            if (!_used.Contains(candidate))
            {
                return candidate;
            }
        }

        // Past the pool — twelve distinct unpaired samplers in one shader. The
        // anisotropic variants are also recognised, so keep growing there.
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

        // Beyond that, give up on recognisability but keep uniqueness: the
        // importer will still complain, but about a count rather than about a
        // silent collision.
        return $"sampler_LinearClamp_overflow_{_used.Count}";
    }

    /// <summary>
    /// Name of the texture read through this sampler slot, WITHOUT the
    /// <c>sampler_</c> prefix the caller supplies.
    ///
    /// Only when EXACTLY ONE texture targets the slot. A shared sampler has no
    /// single texture to be named after, and picking one anyway produces a name
    /// that actively lies: a reader meeting
    /// <c>_NormalMap.Sample(sampler_LinearClamp_OffsetTex, …)</c> reasonably
    /// concludes the two are related when the suffix merely names whichever
    /// texture happened to sort first. A shared sampler keeps the bare inline
    /// form, which is the honest description of what it is.
    /// </summary>
    private static string? PairedTextureSuffix(int set, int binding, SerializedProgramData symbols)
    {
        TextureParameter? only = null;

        foreach (TextureParameter texture in symbols.TextureParameters)
        {
            if (texture.SamplerIndex != binding
                || string.IsNullOrWhiteSpace(texture.Name)
                || symbols.GetSetIdFor(texture.Index, ShaderResourceType.Texture) != set)
            {
                continue;
            }

            if (only is not null)
            {
                return null;   // shared — no one texture owns this sampler
            }

            only = texture;
        }

        if (only is null)
        {
            return null;
        }

        return only.Name.StartsWith('_') ? only.Name[1..] : only.Name;
    }
}
