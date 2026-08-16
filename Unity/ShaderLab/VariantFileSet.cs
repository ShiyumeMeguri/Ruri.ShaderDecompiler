using System.Text;

namespace Ruri.ShaderTools.Unity.ShaderLab;

/// <summary>
/// Collects per-variant source files that a <c>.shader</c> pulls in with
/// <c>#include</c>.
///
/// Splitting matters at scale: a modern pipeline shader has dozens of variants
/// per stage per pass, and inlining every body produces a single file large
/// enough to stall an editor on import. Split, each variant is independently
/// diffable and the <c>.shader</c> stays navigable.
///
/// FILE NAMES ARE DELIBERATELY SHORT — <c>Sub0_Pass2_Fragment_b31</c> — and the
/// keyword combination lives in a comment INSIDE the file instead. Baking the
/// combination into the name ran to hundreds of characters on heavily
/// multi-compiled shaders and pushed paths past the platform filename limit,
/// failing the export outright. A plain-text search for a keyword still finds the
/// file.
/// </summary>
internal sealed class VariantFileSet
{
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

    /// <summary>Folder name the <c>#include</c> paths are relative to; null keeps
    /// every variant body inline.</summary>
    public string? FolderName { get; }

    public VariantFileSet(string? folderName) => FolderName = folderName;

    public bool IsSplitting => !string.IsNullOrEmpty(FolderName);

    public IReadOnlyDictionary<string, string> Files => _files;

    /// <summary>
    /// Store one variant body and return the <c>#include</c> path for it.
    /// </summary>
    public string Add(int subShaderIndex, int passIndex, string stage, UnitySerializedSubProgram subProgram, string keywordList, string body, string? legend)
    {
        string extension = string.IsNullOrWhiteSpace(subProgram.SourceFileExtension) ? ".hlsl" : subProgram.SourceFileExtension;
        string fileName = ResolveUniqueName(BuildKey(subShaderIndex, passIndex, stage, subProgram), extension);

        _files[fileName] = BuildContent(stage, subProgram, keywordList, body, legend);
        return $"{FolderName}/{fileName}";
    }

    /// <summary>
    /// <c>Sub&lt;N&gt;_Pass&lt;M&gt;_&lt;Stage&gt;_b&lt;Blob&gt;</c> — short and bounded.
    ///
    /// Every variant file for one shader shares a folder, so subshader, pass and
    /// stage must all stay in the name to avoid cross-pass clashes; the blob index
    /// separates the distinct binaries within one of those.
    /// </summary>
    private static string BuildKey(int subShaderIndex, int passIndex, string stage, UnitySerializedSubProgram subProgram)
        => SanitizeStem($"Sub{subShaderIndex}_Pass{passIndex}_{stage}_b{subProgram.BlobIndex}");

    /// <summary>
    /// The key is unique per emitted variant in practice; the one way two collide
    /// is when the engine deduplicated two keyword combinations onto one bytecode
    /// blob. Append a counter so neither body is silently dropped.
    /// </summary>
    private string ResolveUniqueName(string stem, string extension)
    {
        string candidate = stem + extension;
        if (!_files.ContainsKey(candidate))
        {
            return candidate;
        }

        for (int n = 2; ; n++)
        {
            candidate = $"{stem}_v{n}{extension}";
            if (!_files.ContainsKey(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// Two header lines and the body.
    ///
    /// Every field the old eight-line block carried is still here — the variant
    /// key is the file's own name, so it is not repeated — just packed onto one
    /// line, with the keyword list on its own because it is the only unbounded
    /// one. At tens of thousands of variant files the header is paid for that many
    /// times over, and the rules made of equals signs were the majority of it.
    ///
    /// The builder is sized for the whole result up front. The body dominates by
    /// three orders of magnitude, so letting the builder discover that by doubling
    /// would copy it repeatedly for nothing.
    /// </summary>
    private static string BuildContent(string stage, UnitySerializedSubProgram subProgram, string keywordList, string body, string? legend)
    {
        var builder = new StringBuilder(body.Length + 256);

        builder.Append("// Stage: ").Append(stage)
               .Append("  Blob: ").Append(subProgram.BlobIndex)
               .Append("  ParamBlob: ");

        if (subProgram.ParameterBlobIndex.HasValue)
        {
            builder.Append(subProgram.ParameterBlobIndex.Value);
        }
        else
        {
            builder.Append("<none>");
        }

        builder.Append("  Language: ").AppendLine(subProgram.SourceLanguage);
        builder.Append("// Keywords: ").AppendLine(keywordList);

        if (legend is not null)
        {
            builder.AppendLine(legend);
        }

        builder.AppendLine();
        builder.Append(body);

        if (!body.EndsWith('\n'))
        {
            builder.AppendLine();
        }

        return builder.ToString();
    }

    /// <summary>
    /// Belt-and-braces filename sanitisation. The key is already alphanumeric plus
    /// underscore, but a future stage enum could inject a separator.
    /// </summary>
    private static string SanitizeStem(string raw)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(raw.Length);
        foreach (char c in raw)
        {
            builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }
        return builder.ToString();
    }
}
