namespace Ruri.ShaderTools.Unity.ShaderLab;

/// <summary>
/// A rendered ShaderLab document: the <c>.shader</c> text plus the per-variant
/// source files it includes.
///
/// The caller writes <see cref="VariantFiles"/> into a folder named after the
/// <c>.shader</c> stem — the include paths in <see cref="ShaderText"/> assume
/// exactly that layout.
/// </summary>
public sealed record ShaderLabDocument(string ShaderText, IReadOnlyDictionary<string, string> VariantFiles);

/// <summary>
/// Reassembles a decompiled Unity shader asset into ShaderLab source.
///
/// Structure only. Render-state formatting, property declarations, keyword
/// conditions, variant files and the HLSL-for-Unity fixups each live in their own
/// type — this walks the asset and delegates. Keep it that way: these concerns
/// change for unrelated reasons and used to sit in one 1600-line file where a
/// blend-mode tweak and a vertex-semantic fix touched the same source.
/// </summary>
public static class ShaderLabWriter
{
    /// <summary>Render with every variant body inline.</summary>
    public static string Write(UnityShaderMetadata metadata)
        => Render(metadata, new VariantFileSet(folderName: null)).ShaderText;

    /// <summary>
    /// Render with each multi-variant stage's bodies split into
    /// <c>&lt;folder&gt;/&lt;variant&gt;.hlsl</c> files, referenced by
    /// <c>#include</c>.
    ///
    /// Preferred for real pipeline shaders: a pass can carry dozens of variants
    /// per stage, and inlining them all produces a file large enough to stall an
    /// editor import.
    /// </summary>
    public static ShaderLabDocument WriteSplit(UnityShaderMetadata metadata, string variantFolderName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(variantFolderName);
        return Render(metadata, new VariantFileSet(variantFolderName));
    }

    private static ShaderLabDocument Render(UnityShaderMetadata metadata, VariantFileSet variants)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var writer = new IndentedWriter();
        writer.Line($"Shader \"{metadata.Name}\" {{");
        writer.Indent();

        WriteProperties(writer, metadata.ParsedForm.PropInfo);

        for (int subShaderIndex = 0; subShaderIndex < metadata.ParsedForm.SubShaders.Count; subShaderIndex++)
        {
            WriteSubShader(writer, metadata, metadata.ParsedForm.SubShaders[subShaderIndex], subShaderIndex, variants);
        }

        if (!string.IsNullOrWhiteSpace(metadata.ParsedForm.FallbackName))
        {
            writer.Line($"Fallback \"{metadata.ParsedForm.FallbackName}\"");
        }

        if (!string.IsNullOrWhiteSpace(metadata.ParsedForm.CustomEditorName))
        {
            writer.Line($"CustomEditor \"{metadata.ParsedForm.CustomEditorName}\"");
        }

        writer.Unindent();
        writer.Line("}");

        return new ShaderLabDocument(writer.ToString(), variants.Files);
    }

    private static void WriteProperties(IndentedWriter writer, UnitySerializedProperties properties)
    {
        if (properties.Props.Count == 0)
        {
            return;
        }

        writer.Line("Properties {");
        writer.Indent();

        foreach (UnitySerializedProperty property in properties.Props)
        {
            string declaration = ShaderLabDeclarations.BuildProperty(property);
            if (!string.IsNullOrWhiteSpace(declaration))
            {
                writer.Line(declaration);
            }
        }

        writer.Unindent();
        writer.Line("}");
    }

    private static void WriteSubShader(
        IndentedWriter writer,
        UnityShaderMetadata metadata,
        UnitySerializedSubShader subShader,
        int subShaderIndex,
        VariantFileSet variants)
    {
        writer.Line("SubShader {");
        writer.Indent();

        ShaderLabDeclarations.WriteTags(writer, subShader.Tags.Tags);
        if (subShader.LOD != 0)
        {
            writer.Line($"LOD {subShader.LOD}");
        }

        for (int passIndex = 0; passIndex < subShader.Passes.Count; passIndex++)
        {
            WritePass(writer, metadata, subShader, subShader.Passes[passIndex], subShaderIndex, passIndex, variants);
        }

        writer.Unindent();
        writer.Line("}");
    }

    private static void WritePass(
        IndentedWriter writer,
        UnityShaderMetadata metadata,
        UnitySerializedSubShader subShader,
        UnitySerializedPass pass,
        int subShaderIndex,
        int passIndex,
        VariantFileSet variants)
    {
        if (!string.IsNullOrWhiteSpace(pass.UseName))
        {
            writer.Line($"UsePass \"{pass.UseName}\"");
            return;
        }

        writer.Line("Pass {");
        writer.Indent();

        if (!string.IsNullOrWhiteSpace(pass.State.Name))
        {
            writer.Line($"Name \"{pass.State.Name}\"");
        }

        if (pass.State.LOD != 0)
        {
            writer.Line($"LOD {pass.State.LOD}");
        }

        foreach (string command in RenderStateFormatter.BuildCommands(pass.State))
        {
            if (!string.IsNullOrWhiteSpace(command))
            {
                writer.Line(command);
            }
        }

        // SubShader tags inherit into every pass, so a pass only declares what it
        // adds or overrides. Both tag maps get the same treatment.
        ShaderLabDeclarations.WriteTags(writer, ShaderLabDeclarations.WithoutInherited(pass.State.Tags.Tags, subShader.Tags.Tags));
        if (pass.Tags.Tags.Count > 0)
        {
            ShaderLabDeclarations.WriteTags(writer, ShaderLabDeclarations.WithoutInherited(pass.Tags.Tags, subShader.Tags.Tags));
        }

        if (HasAnyProgram(pass))
        {
            VariantChainWriter.WriteProgram(writer, metadata.ParsedForm.KeywordNames, pass, subShaderIndex, passIndex, variants);
        }

        writer.Unindent();
        writer.Line("}");
    }

    private static bool HasAnyProgram(UnitySerializedPass pass)
    {
        foreach ((_, UnitySerializedProgram program) in pass.EnumerateProgramSlots())
        {
            if (program.SubPrograms.Count > 0)
            {
                return true;
            }
        }
        return false;
    }
}
