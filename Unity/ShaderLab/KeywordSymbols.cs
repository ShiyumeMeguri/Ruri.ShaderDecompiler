using System.Text;

namespace Ruri.ShaderTools.Unity.ShaderLab;

/// <summary>
/// Turns a shader's keyword indices into preprocessor symbols and conditions.
///
/// A compiled shader ships one bytecode blob per keyword combination. To put them
/// all back into one source file, each variant's body is guarded by a condition
/// over the keywords its combination had ON and OFF, and the pass declares the
/// keyword universe so the compiler actually generates the permutations.
///
/// The conditions are FULLY SPECIFIED — every keyword in the stage's universe
/// appears, negated when the variant did not have it. A partial condition would
/// let two variants match one permutation, and the compiler takes the first,
/// silently picking the wrong body.
/// </summary>
internal static class KeywordSymbols
{
    /// <summary>
    /// A condition matching exactly the variants whose keyword set is
    /// <paramref name="activeIndices"/>, over <paramref name="universe"/>.
    /// Null when the stage has no keywords at all.
    /// </summary>
    public static string? BuildCondition(List<string> keywordNames, List<ushort> universe, List<ushort> activeIndices)
    {
        if (universe.Count == 0)
        {
            return null;
        }

        var active = new HashSet<ushort>(activeIndices);
        var conditions = new List<string>(universe.Count);

        foreach (ushort keywordIndex in universe)
        {
            string keyword = ToSymbol(keywordNames, keywordIndex);
            conditions.Add(active.Contains(keywordIndex) ? $"defined({keyword})" : $"!defined({keyword})");
        }

        return string.Join(" && ", conditions);
    }

    /// <summary>Every keyword any variant of any stage in this pass references.</summary>
    public static List<string> ForPass(List<string> keywordNames, UnitySerializedPass pass)
    {
        var all = new List<UnitySerializedSubProgram>();
        foreach ((_, UnitySerializedProgram program) in pass.EnumerateProgramSlots())
        {
            all.AddRange(program.SubPrograms);
        }

        var symbols = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (ushort index in DistinctIndices(all))
        {
            string symbol = ToSymbol(keywordNames, index);
            if (seen.Add(symbol))
            {
                symbols.Add(symbol);
            }
        }

        return symbols;
    }

    /// <summary>Keyword indices used by these variants, in first-seen order.</summary>
    public static List<ushort> DistinctIndices(List<UnitySerializedSubProgram> subPrograms)
    {
        var result = new List<ushort>();
        var seen = new HashSet<ushort>();

        foreach (UnitySerializedSubProgram subProgram in subPrograms)
        {
            foreach (ushort keywordIndex in subProgram.KeywordIndices)
            {
                if (seen.Add(keywordIndex))
                {
                    result.Add(keywordIndex);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Space-joined keyword list for a variant's file header. Sorted so the
    /// header is stable across export runs, and plain-text searchable — which is
    /// why the combination lives in a comment rather than in the file name.
    /// </summary>
    public static string DescribeVariant(UnitySerializedSubProgram subProgram, List<string> keywordNames)
    {
        if (subProgram.KeywordIndices.Count == 0)
        {
            return "<none> (default / catch-all variant)";
        }

        var keywords = new List<string>(subProgram.KeywordIndices.Count);
        foreach (ushort index in subProgram.KeywordIndices)
        {
            keywords.Add(ToSymbol(keywordNames, index));
        }

        keywords.Sort(StringComparer.Ordinal);
        return string.Join(' ', keywords);
    }

    public static string ToSymbol(List<string> keywordNames, ushort keywordIndex)
    {
        if (keywordIndex >= keywordNames.Count)
        {
            return $"KEYWORD_{keywordIndex}";
        }

        string keyword = keywordNames[keywordIndex];
        return string.IsNullOrWhiteSpace(keyword) ? $"KEYWORD_{keywordIndex}" : Sanitize(keyword);
    }

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            builder.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        }

        if (builder.Length == 0)
        {
            return "KEYWORD";
        }

        if (char.IsDigit(builder[0]))
        {
            builder.Insert(0, '_');
        }

        return builder.ToString();
    }
}
