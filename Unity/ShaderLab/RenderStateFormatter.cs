using System.Globalization;
using System.Text;

namespace Ruri.ShaderTools.Unity.ShaderLab;

/// <summary>
/// Renders a serialized render state back into ShaderLab commands.
///
/// Every emit below is conditional on the value DIFFERING FROM ITS DEFAULT, or on
/// the slot being driven by a material property. Emitting defaults would be
/// harmless but would bury the handful of commands that actually matter under a
/// wall of noise in every single pass.
///
/// A state slot can be either a literal or a property reference — the serialized
/// form carries both a value and an optional name — so each formatter has to
/// check for a name first and emit <c>[PropertyName]</c> instead of the baked
/// value. Getting that backwards silently freezes an animated state.
/// </summary>
internal static class RenderStateFormatter
{
    /// <summary>Placeholder the serializer writes for an unset property name.</summary>
    private const string UnsetName = "<noninit>";

    public static IEnumerable<string> BuildCommands(UnitySerializedShaderState state)
    {
        if (state.RtSeparateBlend)
        {
            foreach (string command in BlendCommands(state.RtBlend0, 0)) yield return command;
            foreach (string command in BlendCommands(state.RtBlend1, 1)) yield return command;
            foreach (string command in BlendCommands(state.RtBlend2, 2)) yield return command;
            foreach (string command in BlendCommands(state.RtBlend3, 3)) yield return command;
            foreach (string command in BlendCommands(state.RtBlend4, 4)) yield return command;
            foreach (string command in BlendCommands(state.RtBlend5, 5)) yield return command;
            foreach (string command in BlendCommands(state.RtBlend6, 6)) yield return command;
            foreach (string command in BlendCommands(state.RtBlend7, 7)) yield return command;
        }
        else
        {
            foreach (string command in BlendCommands(state.RtBlend0, -1)) yield return command;
        }

        if (state.AlphaToMask.Val > 0f || HasName(state.AlphaToMask))
        {
            yield return HasName(state.AlphaToMask) ? $"AlphaToMask [{state.AlphaToMask.Name}]" : "AlphaToMask On";
        }

        if ((int)state.ZClip.Val == 1 || HasName(state.ZClip))
        {
            yield return $"ZClip {NamedOr(state.ZClip, Toggle(state.ZClip.Val))}";
        }

        // ZTest 4 is LEqual, the default; 0 means "unset".
        if (((int)state.ZTest.Val != 0 && (int)state.ZTest.Val != 4) || HasName(state.ZTest))
        {
            yield return $"ZTest {NamedOr(state.ZTest, ZTest(state.ZTest.Val))}";
        }

        if ((int)state.ZWrite.Val != 1 || HasName(state.ZWrite))
        {
            yield return $"ZWrite {NamedOr(state.ZWrite, Toggle(state.ZWrite.Val))}";
        }

        // Cull 2 is Back, the default.
        if ((int)state.Culling.Val != 2 || HasName(state.Culling))
        {
            yield return $"Cull {NamedOr(state.Culling, CullMode(state.Culling.Val))}";
        }

        if ((int)state.Conservative.Val != 0 || HasName(state.Conservative))
        {
            yield return $"Conservative {NamedOr(state.Conservative, (int)state.Conservative.Val == 1 ? "True" : "False")}";
        }

        if (state.OffsetFactor.Val != 0f || state.OffsetUnits.Val != 0f || HasName(state.OffsetFactor) || HasName(state.OffsetUnits))
        {
            yield return $"Offset {NamedOrDecimal(state.OffsetFactor)}, {NamedOrDecimal(state.OffsetUnits)}";
        }

        foreach (string command in StencilCommands(state)) yield return command;
        foreach (string command in FogCommands(state)) yield return command;

        if (state.Lighting)
        {
            yield return "Lighting On";
        }
    }

    // --- blend --------------------------------------------------------------

    private static IEnumerable<string> BlendCommands(UnitySerializedShaderRTBlendState state, int index)
    {
        bool namedBlend = HasName(state.SrcBlend) || HasName(state.DestBlend)
                       || HasName(state.SrcBlendAlpha) || HasName(state.DestBlendAlpha);

        // Default blend is One/Zero on both colour and alpha.
        if ((int)state.SrcBlend.Val != 1 || (int)state.DestBlend.Val != 0
            || (int)state.SrcBlendAlpha.Val != 1 || (int)state.DestBlendAlpha.Val != 0
            || namedBlend)
        {
            string command = index >= 0 ? $"Blend {index} " : "Blend ";
            command += $"{NamedOr(state.SrcBlend, BlendMode(state.SrcBlend.Val))} {NamedOr(state.DestBlend, BlendMode(state.DestBlend.Val))}";

            bool separateAlpha = (int)state.SrcBlendAlpha.Val != 1 || (int)state.DestBlendAlpha.Val != 0
                              || HasName(state.SrcBlendAlpha) || HasName(state.DestBlendAlpha);
            if (separateAlpha)
            {
                command += $", {NamedOr(state.SrcBlendAlpha, BlendMode(state.SrcBlendAlpha.Val))} {NamedOr(state.DestBlendAlpha, BlendMode(state.DestBlendAlpha.Val))}";
            }

            yield return command;
        }

        if ((int)state.BlendOp.Val != 0 || (int)state.BlendOpAlpha.Val != 0 || HasName(state.BlendOp) || HasName(state.BlendOpAlpha))
        {
            string command = index >= 0 ? $"BlendOp {index} " : "BlendOp ";
            command += NamedOr(state.BlendOp, BlendOp(state.BlendOp.Val));

            if ((int)state.BlendOpAlpha.Val != 0 || HasName(state.BlendOpAlpha))
            {
                command += $", {NamedOr(state.BlendOpAlpha, BlendOp(state.BlendOpAlpha.Val))}";
            }

            yield return command;
        }

        // 15 = RGBA, the default.
        if ((int)state.ColMask.Val != 15 || HasName(state.ColMask))
        {
            string mask = HasName(state.ColMask)
                ? $"[{state.ColMask.Name}]"
                : (int)state.ColMask.Val == 0 ? "0" : ColorMask((int)state.ColMask.Val);

            yield return index >= 0 ? $"ColorMask {mask} {index}" : $"ColorMask {mask}";
        }
    }

    private static string ColorMask(int mask)
    {
        var builder = new StringBuilder(4);
        if ((mask & 2) != 0) builder.Append('R');
        if ((mask & 4) != 0) builder.Append('G');
        if ((mask & 8) != 0) builder.Append('B');
        if ((mask & 1) != 0) builder.Append('A');
        return builder.ToString();
    }

    // --- stencil ------------------------------------------------------------

    private static IEnumerable<string> StencilCommands(UnitySerializedShaderState state)
    {
        bool named = HasName(state.StencilRef) || HasName(state.StencilReadMask) || HasName(state.StencilWriteMask)
                  || HasStencilNames(state.StencilOp) || HasStencilNames(state.StencilOpFront) || HasStencilNames(state.StencilOpBack);

        bool nonDefault = state.StencilRef.Val != 0f || state.StencilReadMask.Val != 255f || state.StencilWriteMask.Val != 255f
                       || !IsDefaultStencilBlock(state.StencilOp, allowDisabledComp: false)
                       || !IsDefaultStencilBlock(state.StencilOpFront, allowDisabledComp: false)
                       || !IsDefaultStencilBlock(state.StencilOpBack, allowDisabledComp: false);

        if (!named && !nonDefault)
        {
            yield break;
        }

        yield return "Stencil {";

        if (state.StencilRef.Val != 0f || HasName(state.StencilRef)) yield return $"    Ref {NamedOrInt(state.StencilRef)}";
        if (state.StencilReadMask.Val != 255f || HasName(state.StencilReadMask)) yield return $"    ReadMask {NamedOrInt(state.StencilReadMask)}";
        if (state.StencilWriteMask.Val != 255f || HasName(state.StencilWriteMask)) yield return $"    WriteMask {NamedOrInt(state.StencilWriteMask)}";

        foreach (string command in StencilFace(state.StencilOp, string.Empty)) yield return command;
        foreach (string command in StencilFace(state.StencilOpFront, "Front")) yield return command;
        foreach (string command in StencilFace(state.StencilOpBack, "Back")) yield return command;

        yield return "}";
    }

    private static IEnumerable<string> StencilFace(UnitySerializedStencilOp op, string suffix)
    {
        if (IsDefaultStencilBlock(op, allowDisabledComp: true) && !HasStencilNames(op))
        {
            yield break;
        }

        yield return $"    Comp{suffix} {NamedOr(op.Comp, StencilComp(op.Comp.Val))}";
        yield return $"    Pass{suffix} {NamedOr(op.Pass, StencilOp(op.Pass.Val))}";
        yield return $"    Fail{suffix} {NamedOr(op.Fail, StencilOp(op.Fail.Val))}";
        yield return $"    ZFail{suffix} {NamedOr(op.ZFail, StencilOp(op.ZFail.Val))}";
    }

    private static bool HasStencilNames(UnitySerializedStencilOp op)
        => HasName(op.Pass) || HasName(op.Fail) || HasName(op.ZFail) || HasName(op.Comp);

    private static bool IsDefaultStencilBlock(UnitySerializedStencilOp op, bool allowDisabledComp)
    {
        int comp = (int)op.Comp.Val;
        bool defaultComp = comp == 8 || (allowDisabledComp && comp == 0);   // 8 = Always
        return (int)op.Pass.Val == 0 && (int)op.Fail.Val == 0 && (int)op.ZFail.Val == 0 && defaultComp;
    }

    // --- fog ----------------------------------------------------------------

    private static IEnumerable<string> FogCommands(UnitySerializedShaderState state)
    {
        bool anyFog = state.FogMode != -1
                   || state.FogDensity.Val != 0f || state.FogStart.Val != 0f || state.FogEnd.Val != 0f
                   || state.FogColor.X.Val != 0f || state.FogColor.Y.Val != 0f
                   || state.FogColor.Z.Val != 0f || state.FogColor.W.Val != 0f;

        if (!anyFog)
        {
            yield break;
        }

        yield return "Fog {";

        if (state.FogMode != -1)
        {
            yield return $"    Mode {FogMode(state.FogMode)}";
        }

        if (state.FogColor.X.Val != 0f || state.FogColor.Y.Val != 0f || state.FogColor.Z.Val != 0f || state.FogColor.W.Val != 0f)
        {
            yield return $"    Color ({Float(state.FogColor.X.Val)},{Float(state.FogColor.Y.Val)},{Float(state.FogColor.Z.Val)},{Float(state.FogColor.W.Val)})";
        }

        if (state.FogDensity.Val != 0f)
        {
            yield return $"    Density {Float(state.FogDensity.Val)}";
        }

        if (state.FogStart.Val != 0f || state.FogEnd.Val != 0f)
        {
            yield return $"    Range {Float(state.FogStart.Val)}, {Float(state.FogEnd.Val)}";
        }

        yield return "}";
    }

    // --- value formatting ---------------------------------------------------

    public static bool HasName(UnitySerializedShaderFloatValue value)
        => !string.IsNullOrWhiteSpace(value.Name) && !string.Equals(value.Name, UnsetName, StringComparison.Ordinal);

    private static string NamedOr(UnitySerializedShaderFloatValue value, string literal)
        => HasName(value) ? $"[{value.Name}]" : literal;

    private static string NamedOrInt(UnitySerializedShaderFloatValue value)
        => HasName(value) ? $"[{value.Name}]" : ((int)value.Val).ToString(CultureInfo.InvariantCulture);

    private static string NamedOrDecimal(UnitySerializedShaderFloatValue value)
        => HasName(value) ? $"[{value.Name}]" : Float(value.Val);

    public static string Float(float value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Toggle(float value) => (int)value == 0 ? "Off" : "On";

    private static string CullMode(float value) => (int)value switch
    {
        -1 => "Unknown",
        0 => "Off",
        1 => "Front",
        _ => "Back",
    };

    private static string ZTest(float value) => (int)value switch
    {
        0 => "None",
        1 => "Unknown",
        2 => "Less",
        3 => "Equal",
        4 => "LEqual",
        5 => "Greater",
        6 => "NotEqual",
        7 => "GEqual",
        8 => "Always",
        _ => ((int)value).ToString(CultureInfo.InvariantCulture),
    };

    private static string BlendMode(float value) => (int)value switch
    {
        0 => "Zero",
        1 => "One",
        2 => "DstColor",
        3 => "SrcColor",
        4 => "OneMinusDstColor",
        5 => "SrcAlpha",
        6 => "OneMinusSrcColor",
        7 => "DstAlpha",
        8 => "OneMinusDstAlpha",
        9 => "SrcAlphaSaturate",
        10 => "OneMinusSrcAlpha",
        _ => ((int)value).ToString(CultureInfo.InvariantCulture),
    };

    private static string BlendOp(float value) => (int)value switch
    {
        0 => "Add",
        1 => "Sub",
        2 => "RevSub",
        3 => "Min",
        4 => "Max",
        5 => "LogicalClear",
        6 => "LogicalSet",
        7 => "LogicalCopy",
        8 => "LogicalCopyInverted",
        9 => "LogicalNoop",
        10 => "LogicalInvert",
        11 => "LogicalAnd",
        12 => "LogicalNand",
        13 => "LogicalOr",
        14 => "LogicalNor",
        15 => "LogicalXor",
        16 => "LogicalEquivalence",
        17 => "LogicalAndReverse",
        18 => "LogicalAndInverted",
        19 => "LogicalOrReverse",
        20 => "LogicalOrInverted",
        21 => "Multiply",
        22 => "Screen",
        23 => "Overlay",
        24 => "Darken",
        25 => "Lighten",
        26 => "ColorDodge",
        27 => "ColorBurn",
        28 => "HardLight",
        29 => "SoftLight",
        30 => "Difference",
        31 => "Exclusion",
        32 => "HSLHue",
        33 => "HSLSaturation",
        34 => "HSLColor",
        35 => "HSLLuminosity",
        _ => ((int)value).ToString(CultureInfo.InvariantCulture),
    };

    private static string StencilComp(float value) => (int)value switch
    {
        -1 => "Unknown",
        0 => "Disabled",
        1 => "Never",
        2 => "Less",
        3 => "Equal",
        4 => "LEqual",
        5 => "Greater",
        6 => "NotEqual",
        7 => "GEqual",
        8 => "Always",
        _ => ((int)value).ToString(CultureInfo.InvariantCulture),
    };

    private static string StencilOp(float value) => (int)value switch
    {
        0 => "Keep",
        1 => "Zero",
        2 => "Replace",
        3 => "IncrSat",
        4 => "DecrSat",
        5 => "Invert",
        6 => "IncrWrap",
        7 => "DecrWrap",
        _ => ((int)value).ToString(CultureInfo.InvariantCulture),
    };

    private static string FogMode(int value) => value switch
    {
        -1 => "Unknown",
        0 => "Off",
        1 => "Linear",
        2 => "Exp",
        3 => "Exp2",
        _ => value.ToString(CultureInfo.InvariantCulture),
    };
}
