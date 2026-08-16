using System.Text;

namespace Ruri.ShaderTools.Unity.ShaderLab;

/// <summary>
/// Renders the declarative parts of a ShaderLab document: the material property
/// block and tag maps.
/// </summary>
internal static class ShaderLabDeclarations
{
    /// <summary>
    /// One line of the <c>Properties { }</c> block.
    ///
    /// Attributes come from two places and both must be emitted: an explicit
    /// attribute list, and a flags bitfield the serializer uses for the built-in
    /// ones. Dropping either loses inspector behaviour that the original shader
    /// had.
    /// </summary>
    public static string BuildProperty(UnitySerializedProperty property)
    {
        var builder = new StringBuilder();

        foreach (string attribute in property.Attributes)
        {
            builder.Append('[').Append(attribute).Append("] ");
        }

        uint flags = property.Flags;
        if ((flags & 0x01u) != 0) builder.Append("[HideInInspector] ");
        if ((flags & 0x02u) != 0) builder.Append("[PerRendererData] ");
        if ((flags & 0x04u) != 0) builder.Append("[NoScaleOffset] ");
        if ((flags & 0x08u) != 0) builder.Append("[Normal] ");
        if ((flags & 0x10u) != 0) builder.Append("[HDR] ");
        if ((flags & 0x20u) != 0) builder.Append("[Gamma] ");

        string typeName = property.Type switch
        {
            0 => "Color",
            1 => "Vector",
            2 => "Float",
            3 => $"Range({RenderStateFormatter.Float(property.DefValue[1])}, {RenderStateFormatter.Float(property.DefValue[2])})",
            4 => TextureDimensionName(property.DefTexture.TexDim),
            5 => "Int",
            _ => "Float",
        };

        string defaultValue = property.Type switch
        {
            0 or 1 => $"({RenderStateFormatter.Float(property.DefValue[0])}, {RenderStateFormatter.Float(property.DefValue[1])}, " +
                      $"{RenderStateFormatter.Float(property.DefValue[2])}, {RenderStateFormatter.Float(property.DefValue[3])})",
            2 or 3 or 5 => RenderStateFormatter.Float(property.DefValue[0]),
            4 => $"\"{property.DefTexture.DefaultName}\" {{}}",
            _ => RenderStateFormatter.Float(property.DefValue[0]),
        };

        builder.Append($"{property.Name} (\"{property.Description}\", {typeName}) = {defaultValue}");
        return builder.ToString();
    }

    private static string TextureDimensionName(int dimension) => dimension switch
    {
        1 => "any",
        2 => "2D",
        3 => "3D",
        4 => "Cube",
        5 => "2DArray",
        6 => "CubeArray",
        _ => "2D",
    };

    public static void WriteTags(IndentedWriter writer, List<UnityTagMapEntry> tags)
    {
        if (tags.Count == 0)
        {
            return;
        }

        writer.Line("Tags {");
        writer.Indent();
        foreach (UnityTagMapEntry tag in tags)
        {
            writer.Line($"\"{tag.First}\"=\"{tag.Second}\"");
        }
        writer.Unindent();
        writer.Line("}");
    }

    /// <summary>
    /// Drop pass tags whose key AND value already appear on the SubShader.
    ///
    /// Pass tags inherit from SubShader tags, so a literal duplicate is dead text
    /// repeated once per pass. An entry whose key exists upstream with a DIFFERENT
    /// value survives — that is a real override, and removing it would change
    /// behaviour. Key matching is case-insensitive because ShaderLab treats tag
    /// keys that way.
    /// </summary>
    public static List<UnityTagMapEntry> WithoutInherited(List<UnityTagMapEntry> passTags, List<UnityTagMapEntry> subShaderTags)
    {
        if (passTags.Count == 0 || subShaderTags.Count == 0)
        {
            return passTags;
        }

        var inherited = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (UnityTagMapEntry tag in subShaderTags)
        {
            inherited[tag.First] = tag.Second;
        }

        var kept = new List<UnityTagMapEntry>(passTags.Count);
        foreach (UnityTagMapEntry tag in passTags)
        {
            if (inherited.TryGetValue(tag.First, out string? value) && string.Equals(value, tag.Second, StringComparison.Ordinal))
            {
                continue;
            }
            kept.Add(tag);
        }

        return kept;
    }
}
