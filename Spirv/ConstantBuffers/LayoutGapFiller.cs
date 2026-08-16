using Ruri.ShaderTools.Spirv;

namespace Ruri.ShaderTools.Spirv.ConstantBuffers;

/// <summary>
/// Bridges byte ranges the engine symbols do not describe but the compiled
/// shader still reads.
///
/// WHY THIS IS NECESSARY, not defensive: a constant buffer's symbol metadata is
/// per-permutation. A material family packs several mutually exclusive field
/// sets at the same byte range, and a given blob's metadata only names the
/// family that permutation compiled with — every other family's range is a
/// genuine hole. The bytecode still reads registers in those holes (the values
/// are in the binary, just not under a name this permutation knows). Without a
/// member covering them, access admission rejects the whole buffer and the
/// entire constant buffer degrades to an opaque <c>float4 _m0[N]</c>, losing the
/// names of every field that DID decode.
///
/// So the policy is: bridge the hole with an honest placeholder, keep the
/// hundred names that worked. The placeholder is deliberately loud — it appears
/// verbatim in the emitted source, so an unrecovered slot reads as "this seed
/// needs updating" rather than as padding or, worse, as a confident wrong name.
///
/// Two hole shapes, two fills:
///   * register-aligned bulk → one batched <c>float4[N]</c> member, because a
///     hole can be kilobytes wide and one filler per 4 bytes would be absurd.
///   * sub-register edges → one scalar per 4 bytes, because a hole can start or
///     end mid-register when it borders an unaligned scalar neighbour.
/// </summary>
internal static class LayoutGapFiller
{
    /// <summary>
    /// Name given to every synthesized member. Says what it is — a byte range
    /// no symbol maps to — rather than looking like an identifier, so a later
    /// reader cannot mistake it for an author name. Deduplication appends the
    /// byte offset when a block has several.
    /// </summary>
    public const string PlaceholderName = GeneratedNames.UnmappedRegion;

    /// <summary>
    /// Fill holes BETWEEN named members. Mutates <paramref name="members"/>
    /// in place and re-sorts by byte offset.
    /// </summary>
    public static void FillInteriorGaps(List<BlockMemberLayout> members)
    {
        var fillers = new List<BlockMemberLayout>();
        int cursor = 0;

        foreach (BlockMemberLayout member in members)
        {
            if (member.ByteOffset > cursor)
            {
                fillers.AddRange(CreateFillers(cursor, member.ByteOffset));
            }

            cursor = Math.Max(cursor, member.ByteOffset + member.SpanBytes);
        }

        members.AddRange(fillers);
        members.Sort(static (left, right) => left.ByteOffset.CompareTo(right.ByteOffset));
    }

    /// <summary>
    /// Extend the layout to cover the SPIR-V array's tail when the last named
    /// member stops short of it. Same reasoning as an interior hole: those
    /// registers are real engine fields the seed did not capture, and an access
    /// into them would otherwise sink the buffer.
    /// </summary>
    public static void FillTail(BlockLayout layout, int flatArrayLength)
    {
        if (layout.MaxUsedRegisterCount >= flatArrayLength)
        {
            return;
        }

        int tailStart = layout.MaxUsedRegisterCount;
        int tailRegisters = flatArrayLength - tailStart;

        layout.Members.Add(new BlockMemberLayout
        {
            Name = PlaceholderName,
            ByteOffset = tailStart * 16,
            Shape = VectorRun(tailRegisters),
            RegisterOffset = tailStart,
            RegisterCount = tailRegisters,
        });

        layout.RequiredRegisterCount = flatArrayLength;
    }

    private static IEnumerable<BlockMemberLayout> CreateFillers(int gapStart, int gapEnd)
    {
        int alignedStart = ((gapStart + 15) / 16) * 16;
        int alignedEnd = (gapEnd / 16) * 16;

        // Leading sub-register edge.
        for (int offset = gapStart; offset < Math.Min(alignedStart, gapEnd); offset += 4)
        {
            yield return ScalarFiller(offset);
        }

        // Register-aligned bulk.
        if (alignedEnd > alignedStart)
        {
            int registers = (alignedEnd - alignedStart) / 16;
            yield return new BlockMemberLayout
            {
                Name = PlaceholderName,
                ByteOffset = alignedStart,
                Shape = VectorRun(registers),
                RegisterOffset = alignedStart / 16,
                RegisterCount = registers,
            };
        }

        // Trailing sub-register edge.
        //
        // Resume from max(alignedStart, alignedEnd), NOT alignedEnd: when the gap
        // is narrower than a register and crosses no boundary at all (say bytes
        // [20,28) inside register 1), alignedStart(32) is ABOVE alignedEnd(16) and
        // the leading loop already consumed the whole gap. Resuming at alignedEnd
        // would re-walk those same bytes and emit a second filler at each offset.
        for (int offset = Math.Max(alignedStart, alignedEnd); offset < gapEnd; offset += 4)
        {
            yield return ScalarFiller(offset);
        }
    }

    private static BlockMemberLayout ScalarFiller(int byteOffset) => new()
    {
        Name = PlaceholderName,
        ByteOffset = byteOffset,
        Shape = new MemberShape
        {
            Kind = MemberShapeKind.Scalar,
            ScalarKind = ScalarKind.Float,
            Rows = 1,
            Columns = 1,
            ArrayLength = 1,
            DeclaredByteSize = 4,
            IsMatrix = false,
        },
        RegisterOffset = byteOffset / 16,
        RegisterCount = 1,
    };

    private static MemberShape VectorRun(int registers) => new()
    {
        Kind = MemberShapeKind.Vector,
        ScalarKind = ScalarKind.Float,
        Rows = 4,
        Columns = 1,
        ArrayLength = registers,
        DeclaredByteSize = registers * 16,
        IsMatrix = false,
    };
}
