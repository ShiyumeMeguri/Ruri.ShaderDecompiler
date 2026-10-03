// The symbol table yields resource bindings as a named tuple; alias it so the
// planners read as if it were a type without inventing a parallel one.
using ResourceBinding = (string Name, int Binding, int Set, Ruri.ShaderTools.ShaderResourceType Type, char RegisterType);

using Ruri.ShaderTools.Spirv.SymbolInjection;

namespace Ruri.ShaderTools.Pipeline.Naming;

/// <summary>
/// Decides the name for every MEMBER of every constant-buffer block.
///
/// Names are chosen per member index in this priority order:
///   1. the engine symbol sitting at that byte offset,
///   2. the member name already in the module (sanitised),
///   3. a marker that STATES the name did not survive compilation
///      (<c>SymbolStripped_&lt;offset&gt;</c>) rather than one that merely looks
///      like an identifier — see <see cref="HlslIdentifier.PlaceholderAt"/>.
///
/// Then a whole-block deduplication pass runs, because two different sources can
/// collapse to one identifier — two author names that sanitise identically, or a
/// symbol name colliding with a compiler-baked one. A duplicate member name is a
/// compile error in the emitted source, so collisions get an offset suffix.
///
/// Applied uniformly to every block that has symbol data. No per-name special
/// cases, no known-block list.
/// </summary>
internal sealed class BlockMemberNamePlanner
{
    private readonly Func<int, int, string?> _resolveBlockName;

    public BlockMemberNamePlanner(Func<int, int, string?> resolveBlockName) => _resolveBlockName = resolveBlockName;

    public List<MemberNamePatch> Plan(IReadOnlyList<DescriptorBindingInfo> bindings, SerializedProgramData symbols)
    {
        var patches = new List<MemberNamePatch>();

        foreach (ResourceBinding resource in symbols.EnumerateResourceBindings())
        {
            if (resource.RegisterType != 'b' || string.IsNullOrWhiteSpace(resource.Name))
            {
                continue;
            }

            foreach (DescriptorBindingInfo binding in ResourceNamePlanner.Match(bindings, resource))
            {
                if (binding.Kind != DescriptorKind.UniformBuffer || binding.StructTypeId is not > 0)
                {
                    continue;
                }

                string? structuredName = _resolveBlockName(resource.Set, resource.Binding);
                ConstantBufferParameter? block = symbols.GetConstantBufferByName(structuredName ?? resource.Name);
                if (block is null)
                {
                    continue;
                }

                PlanBlock(binding, block, structuredName is not null, patches);
            }
        }

        return patches;
    }

    private static void PlanBlock(DescriptorBindingInfo binding, ConstantBufferParameter block, bool structured, List<MemberNamePatch> patches)
    {
        uint structTypeId = binding.StructTypeId!.Value;
        List<NumericShaderParameter> allNumeric = FlattenNumericMembers(block);

        // A block the structurer rewrote holds one member per field it placed, however few of the stated fields the
        // module's span reaches -- a program that reads only the first registers of a buffer declares only those, and its
        // one member is the field that starts there. Its members are named by offset below; the two single-member readings
        // that follow are about the compiler's own flat wrapper, which only an unstructured block still is.
        //
        // A single SPIR-V member that is a run of 4x4 matrices is the legitimate
        // "the whole block is one transform array" case — name it after all of
        // them joined.
        if (!structured && binding.StructMemberCount == 1 && allNumeric.Count > 0 && AllAre4x4Matrices(allNumeric))
        {
            string joined = HlslIdentifier.Sanitize(string.Join("_", allNumeric.Select(static p => p.Name ?? string.Empty)));
            patches.Add(new MemberNamePatch(structTypeId, 0u, string.IsNullOrEmpty(joined) ? HlslIdentifier.PlaceholderAt(0) : joined));
            return;
        }

        // A single SPIR-V member that is NOT that case, while the symbols describe
        // members at more than one offset, means this block's structuring was
        // abandoned and the emitter fell back to a flat wrapper — ONE opaque
        // member spanning the whole buffer, not a genuine one-field struct.
        //
        // The offset-keyed loop below would take whichever real field happens to
        // sit at offset 0 and stamp its name on the ENTIRE span: a legitimately
        // named single float ending up labelling twenty-three unrelated
        // registers. That is a confidently WRONG name, which is worse than a
        // missing one — so say what actually happened instead: the block is
        // unstructured and this member is the whole buffer.
        if (!structured && binding.StructMemberCount == 1 && DistinctFieldOffsetCount(block) > 1)
        {
            patches.Add(new MemberNamePatch(structTypeId, 0u, HlslIdentifier.UnstructuredBlockName));
            return;
        }

        Dictionary<int, string?> seedByOffset = BuildSeedIndex(block);
        var claimed = new HashSet<string>(StringComparer.Ordinal);

        // Member-index order, so the disambiguation suffix is deterministic and
        // lines up with emit order.
        foreach (KeyValuePair<int, uint> member in binding.MemberOffsets.OrderBy(static entry => entry.Key))
        {
            int memberIndex = member.Key;
            int byteOffset = (int)member.Value;

            string? candidate = null;
            if (seedByOffset.TryGetValue(byteOffset, out string? seeded))
            {
                candidate = seeded;
            }
            else if (binding.CurrentMemberNames.TryGetValue(memberIndex, out string? baked) && !string.IsNullOrEmpty(baked))
            {
                candidate = baked;
            }

            string sanitized = HlslIdentifier.Sanitize(candidate);
            if (string.IsNullOrEmpty(sanitized))
            {
                sanitized = HlslIdentifier.PlaceholderAt(byteOffset);
            }

            string final = sanitized;
            if (!claimed.Add(FlattenedIdentity(sanitized)))
            {
                final = HlslIdentifier.DisambiguateAt(sanitized, byteOffset);
                claimed.Add(FlattenedIdentity(final));
            }

            patches.Add(new MemberNamePatch(structTypeId, (uint)memberIndex, final));
        }
    }

    /// <summary>
    /// What two member names must differ in to stay two identifiers once the
    /// backend flattens the block. It joins the block name to each member with
    /// an underscore and collapses the run, so a member's leading underscores are
    /// not part of its identity there: <c>_Foo</c> and <c>Foo</c> are one name.
    /// </summary>
    private static string FlattenedIdentity(string member) => HlslIdentifier.CollapseUnderscores("_" + member);

    // Byte offset → author name. Struct fields first so a struct's own name wins
    // its offset over a numeric member that happens to share it; first entry per
    // offset wins within each source.
    private static Dictionary<int, string?> BuildSeedIndex(ConstantBufferParameter block)
    {
        var seedByOffset = new Dictionary<int, string?>();

        foreach (StructParameter structSymbol in block.StructParameters)
        {
            if (!string.IsNullOrWhiteSpace(structSymbol.Name))
            {
                seedByOffset.TryAdd(structSymbol.Index, structSymbol.Name);
            }
        }

        foreach (NumericShaderParameter numeric in block.AllNumericParameters)
        {
            if (!string.IsNullOrWhiteSpace(numeric.Name))
            {
                seedByOffset.TryAdd(numeric.Index, numeric.Name);
            }
        }

        return seedByOffset;
    }

    private static List<NumericShaderParameter> FlattenNumericMembers(ConstantBufferParameter block)
    {
        var all = new List<NumericShaderParameter>(block.AllNumericParameters);
        foreach (StructParameter structSymbol in block.StructParameters)
        {
            all.AddRange(structSymbol.AllNumericMembers);
        }
        return all;
    }

    private static bool AllAre4x4Matrices(List<NumericShaderParameter> symbols)
    {
        foreach (NumericShaderParameter symbol in symbols)
        {
            if (!symbol.IsMatrix || symbol.RowCount != 4 || symbol.ColumnCount != 4)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// How many DISTINCT byte offsets the symbols describe, drawn from the exact
    /// same two sources the seed index uses. More than one means the symbols know
    /// this block has several real fields even though SPIR-V collapsed it to a
    /// single flat member — the signal that structuring was abandoned rather than
    /// that the block is genuinely one field wide.
    /// </summary>
    private static int DistinctFieldOffsetCount(ConstantBufferParameter block)
    {
        var offsets = new HashSet<int>();

        foreach (StructParameter structSymbol in block.StructParameters)
        {
            offsets.Add(structSymbol.Index);
        }

        foreach (NumericShaderParameter numeric in block.AllNumericParameters)
        {
            offsets.Add(numeric.Index);
        }

        return offsets.Count;
    }
}
