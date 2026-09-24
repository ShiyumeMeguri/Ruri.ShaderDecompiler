namespace Ruri.ShaderTools;

/// <summary>
/// The state of a sampler the shader source declares inline (<c>SamplerState sampler_LinearClamp</c>) rather than
/// taking it from a texture. Unity keeps it in a program's <c>m_Samplers[].sampler</c> as bit fields, in the order
/// of its own <c>FilterMode</c> and <c>TextureWrapMode</c>: bits 0-1 the filter, bits 2-3 / 4-5 / 6-7 the U / V / W
/// wrap, bit 8 depth comparison. Flags with any other bit set are refused: a state this cannot name is not named.
/// </summary>
public sealed record InlineSamplerState(
    InlineSamplerFilter Filter,
    InlineSamplerWrap WrapU,
    InlineSamplerWrap WrapV,
    InlineSamplerWrap WrapW,
    bool Compare)
{
    private const uint FilterMask = 0x3;
    private const uint WrapMask = 0x3;
    private const int WrapUShift = 2;
    private const int WrapVShift = 4;
    private const int WrapWShift = 6;
    private const uint CompareBit = 0x100;
    private const uint StatedBits = 0x1FF;

    public static InlineSamplerState FromUnityFlags(uint flags)
    {
        if ((flags & ~StatedBits) != 0 || (flags & FilterMask) > (uint)InlineSamplerFilter.Trilinear)
        {
            throw new NotSupportedException(
                $"inline sampler flags 0x{flags:X8} carry bits beyond filter, wrap and comparison.");
        }
        return new InlineSamplerState(
            (InlineSamplerFilter)(flags & FilterMask),
            (InlineSamplerWrap)((flags >> WrapUShift) & WrapMask),
            (InlineSamplerWrap)((flags >> WrapVShift) & WrapMask),
            (InlineSamplerWrap)((flags >> WrapWShift) & WrapMask),
            (flags & CompareBit) != 0);
    }

    /// <summary>The name a host importer parses back to this same state: <c>sampler_&lt;Filter&gt;&lt;Wrap&gt;</c>,
    /// the wrap stated per axis when the axes differ, then <c>Compare</c> for a comparison sampler.</summary>
    public string Name
    {
        get
        {
            string wrap = WrapU == WrapV && WrapV == WrapW ? WrapU.ToString() : $"{WrapU}U_{WrapV}V_{WrapW}W";
            return $"sampler_{Filter}{wrap}{(Compare ? "Compare" : string.Empty)}";
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
