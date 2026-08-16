namespace Ruri.ShaderTools;

/// <summary>
/// Names this pipeline INVENTS, as opposed to names it recovers.
///
/// They live at the root because three unrelated layers need the same strings:
/// the layout builder that creates such members, the naming layer that assigns
/// them, and the source adapter that explains them to whoever opens the file. A
/// literal duplicated across those layers would drift, and the moment the
/// adapter's copy stops matching, the generated file silently loses its legend.
///
/// Every one of these reads as a STATEMENT rather than as an identifier. That is
/// the whole design rule: a reader who has never seen this codebase — a person
/// months later, or a model given only the file — must be able to tell at a
/// glance that the name was manufactured, and why. A terse placeholder like
/// <c>f_2032</c> fails that test; it looks exactly like a real author name, gets
/// quoted as one, and sends people hunting for a CPU-side property that was
/// never there.
/// </summary>
internal static class GeneratedNames
{
    /// <summary>
    /// A member whose name did not survive compilation. Always followed by
    /// <c>_&lt;byteOffset&gt;</c> — the member's only surviving identity, and what
    /// any future symbol source would be matched on.
    /// </summary>
    public const string StrippedSymbol = "Stripped";

    /// <summary>
    /// A block that could not be split into members at all; the single member
    /// spans the whole buffer. Distinct from <see cref="StrippedSymbol"/> because
    /// it reports a different fact — nothing was stripped, the structuring simply
    /// did not happen.
    /// </summary>
    public const string UnstructuredBlock = "Unstructured";

    /// <summary>
    /// A byte range no symbol describes at all, bridged so the members around it
    /// could still be named. Deduplication appends <c>_at_&lt;byteOffset&gt;</c>
    /// when a block contains several.
    /// </summary>
    public const string UnmappedRegion = "Unmapped";
}
