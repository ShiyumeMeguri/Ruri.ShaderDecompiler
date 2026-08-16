using System.Text;

namespace Ruri.ShaderTools.Pipeline.Naming;

/// <summary>
/// Turns an author-supplied symbol name into an HLSL identifier, MATCHING what
/// the source emitter will do to it.
///
/// The matching part is the whole point. The emitter replaces non-alphanumeric
/// characters with underscores AND collapses runs of them. Sanitising here
/// without collapsing means the collision check runs on strings the emitter will
/// then fuse together: a CJK parameter name yielding <c>AO_________</c> and
/// another yielding <c>AO__</c> look distinct at dedup time, both come out of the
/// emitter as <c>AO_</c>, and the generated source has two identically named
/// members — a hard compile error in the artifact the user actually opens.
///
/// Encoding-agnostic by construction: CJK, Arabic, emoji, punctuation and
/// whitespace all take the same replace-then-collapse path. No character lists.
/// </summary>
internal static class HlslIdentifier
{
    /// <summary>
    /// Sanitise, collapse underscore runs, trim both ends, and guard a leading
    /// digit. Returns empty when nothing survives — the caller substitutes an
    /// offset-based placeholder rather than emitting a nameless member.
    /// </summary>
    public static string Sanitize(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(raw.Length);
        bool lastWasUnderscore = false;

        foreach (char c in raw)
        {
            bool isAlphanumeric = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
            if (isAlphanumeric)
            {
                builder.Append(c);
                lastWasUnderscore = false;
            }
            else if (!lastWasUnderscore)
            {
                builder.Append('_');
                lastWasUnderscore = true;
            }
            // else: part of a run — collapsed.
        }

        // Trim BOTH ends. The emitter also collapses underscores across the
        // variable-prefix boundary, so a member named "_AO" comes out as
        // `<Block>_AO` with the leading underscore fused into the separator.
        // Trimming here makes dedup see what will actually be produced.
        int start = 0;
        while (start < builder.Length && builder[start] == '_')
        {
            start++;
        }

        int end = builder.Length;
        while (end > start && builder[end - 1] == '_')
        {
            end--;
        }

        if (end == start)
        {
            return string.Empty;
        }

        string body = builder.ToString(start, end - start);

        // Leading-digit guard. Applied uniformly, so dedup still sees the same
        // collision shape it would have without it.
        return body[0] >= '0' && body[0] <= '9' ? "_" + body : body;
    }

    /// <summary>
    /// Marker for a member whose name did not survive compilation.
    ///
    /// The name STATES WHAT HAPPENED rather than looking like an identifier. A
    /// bare <c>f_2032</c> reads as an ordinary variable, so a later reader — human
    /// or model — has no way to tell it apart from a real author name, may quote
    /// it as one, and will waste time hunting for a matching CPU-side property
    /// that does not exist. Spelling out "symbol stripped" removes that whole
    /// class of mistake at zero cost.
    ///
    /// The byte offset is kept because it is the member's ONLY surviving
    /// identity: it is what the metadata is keyed on, what a future symbol source
    /// would be matched against, and what makes the name stable across runs.
    /// </summary>
    public static string PlaceholderAt(int byteOffset) => $"{StrippedSymbolMarker}_{byteOffset}";

    /// <summary>
    /// Marker for a block that could not be split into members at all — the one
    /// member spans the whole buffer. Deliberately NOT the offset form: nothing
    /// was stripped at offset 0, the structuring simply did not happen, and
    /// saying so is a different fact.
    /// </summary>
    public static string UnstructuredBlockName => GeneratedNames.UnstructuredBlock;

    private const string StrippedSymbolMarker = GeneratedNames.StrippedSymbol;

    /// <summary>Disambiguator appended when two members sanitise to one identifier.</summary>
    public static string DisambiguateAt(string sanitized, int byteOffset) => $"{sanitized}_at_{byteOffset}";
}
