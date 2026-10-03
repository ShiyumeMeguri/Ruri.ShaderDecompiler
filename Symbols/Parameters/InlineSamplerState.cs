namespace Ruri.ShaderTools;

/// <summary>
/// The state of a sampler the shader source declares inline (<c>SamplerState sampler_LinearClamp</c>) rather than
/// taking it from a texture. Unity keeps it in a program's <c>m_Samplers[].sampler</c> as bit fields, read the way the
/// engine reads them back (<c>InlineSamplerType::Sanitize</c>): bits 0-1 the filter in the order of its own
/// <c>FilterMode</c>, a value past trilinear standing for point; bits 2-3 / 4-5 / 6-7 the U / V / W wrap in the order
/// of <c>TextureWrapMode</c>; bit 8 depth comparison; bits 9-11 the anisotropy as a power of two, zero for none.
/// Flags with any other bit set, or an anisotropy past the sixteen a name can state, are refused: a state this cannot
/// name is not named.
/// </summary>
public sealed record InlineSamplerState(
    InlineSamplerFilter Filter,
    InlineSamplerWrap WrapU,
    InlineSamplerWrap WrapV,
    InlineSamplerWrap WrapW,
    bool Compare,
    int Anisotropy)
{
    private const uint FilterMask = 0x3;
    private const uint WrapMask = 0x3;
    private const int WrapUShift = 2;
    private const int WrapVShift = 4;
    private const int WrapWShift = 6;
    private const uint CompareBit = 0x100;
    private const int AnisotropyShift = 9;
    private const uint AnisotropyMask = 0x7;
    private const uint MaxNamedAnisotropyExponent = 4;
    private const uint StatedBits = 0xFFF;

    public static InlineSamplerState FromUnityFlags(uint flags)
    {
        uint anisotropyExponent = (flags >> AnisotropyShift) & AnisotropyMask;
        if ((flags & ~StatedBits) != 0 || anisotropyExponent > MaxNamedAnisotropyExponent)
        {
            throw new NotSupportedException(
                $"inline sampler flags 0x{flags:X8} carry bits beyond filter, wrap, comparison and anisotropy.");
        }
        uint filter = flags & FilterMask;
        return new InlineSamplerState(
            filter > (uint)InlineSamplerFilter.Trilinear ? InlineSamplerFilter.Point : (InlineSamplerFilter)filter,
            (InlineSamplerWrap)((flags >> WrapUShift) & WrapMask),
            (InlineSamplerWrap)((flags >> WrapVShift) & WrapMask),
            (InlineSamplerWrap)((flags >> WrapWShift) & WrapMask),
            (flags & CompareBit) != 0,
            1 << (int)anisotropyExponent);
    }

    /// <summary>The name a host importer parses back to this same state: <c>sampler_&lt;Filter&gt;&lt;Wrap&gt;</c>,
    /// the wrap stated per axis when the axes differ, then <c>Compare</c> for a comparison sampler and
    /// <c>Aniso&lt;N&gt;</c> for an anisotropic one.</summary>
    public string Name
    {
        get
        {
            string wrap = WrapU == WrapV && WrapV == WrapW ? WrapU.ToString() : $"{WrapU}U_{WrapV}V_{WrapW}W";
            string compare = Compare ? "Compare" : string.Empty;
            string anisotropy = Anisotropy > 1 ? $"Aniso{Anisotropy}" : string.Empty;
            return $"sampler_{Filter}{wrap}{compare}{anisotropy}";
        }
    }
}

public enum InlineSamplerFilter
{
    Point,
    Linear,
    Trilinear,
}

public enum InlineSamplerWrap
{
    Repeat,
    Clamp,
    Mirror,
    MirrorOnce,
}
