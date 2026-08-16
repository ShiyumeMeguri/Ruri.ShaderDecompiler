using Ruri.ShaderTools.Spirv;

namespace Ruri.ShaderTools.Spirv.SymbolInjection;

/// <summary>Give the variable / type <paramref name="Id"/> this name.</summary>
internal readonly record struct NamePatch(uint Id, string Name);

/// <summary>Give member <paramref name="MemberIndex"/> of struct type
/// <paramref name="StructTypeId"/> this name.</summary>
internal readonly record struct MemberNamePatch(uint StructTypeId, uint MemberIndex, string Name);

/// <summary>
/// Writes recovered symbol names into a module's debug section.
///
/// The critical detail is that this REPLACES rather than appends. Compilers
/// routinely emit machine-generated debug names (<c>_8</c>, <c>_14</c>,
/// <c>type_0</c>) before any of this runs. Appending a better name alongside one
/// leaves the emitter's name-uniquify pass free to prefer the original, and the
/// recovered name silently loses the race — the shader still decompiles, it just
/// decompiles with the useless names. So every superseded <c>OpName</c> /
/// <c>OpMemberName</c> is deleted outright, guaranteeing the injected name is the
/// only one for its target.
///
/// Deleted, not blanked: an <c>OpNop</c> in its place would change the emitted
/// byte layout for no benefit.
/// </summary>
internal static class DebugNameInjector
{
    /// <summary>
    /// The contiguous <c>OpSource..OpExtension</c> block — source, name, string,
    /// line and extension declarations. The insertion scan walks forward while it
    /// sees these and stops at the first decoration or type declaration, so
    /// injected names land at the END of the debug block: where an emitter
    /// expects them, and without splitting the type block.
    ///
    /// Opcodes outside the range but before the stop (capability, ext-inst
    /// import, memory model, entry point, execution mode) neither extend the
    /// block nor end the scan — they are simply stepped over.
    /// </summary>
    private static bool IsDebugBlockOpCode(ushort opCode) => opCode >= SpvOpCode.OpSource && opCode <= SpvOpCode.OpExtension;

    public static byte[] Inject(
        byte[] spirv,
        IReadOnlyList<NamePatch> names,
        IReadOnlyList<MemberNamePatch> memberNames)
    {
        if (names.Count == 0 && memberNames.Count == 0)
        {
            // Nothing to replace and nothing to add — hand back the input
            // untouched rather than churning the buffer through a round-trip.
            return spirv;
        }

        SpirvModule module = SpirvModule.Parse(spirv);

        RemoveSupersededNames(module, names, memberNames);

        var injected = new List<SpirvInstruction>(names.Count + memberNames.Count);
        foreach (NamePatch patch in names)
        {
            injected.Add(SpirvDebugNames.CreateName(module, patch.Id, patch.Name));
        }
        foreach (MemberNamePatch patch in memberNames)
        {
            injected.Add(SpirvDebugNames.CreateMemberName(module, patch.StructTypeId, patch.MemberIndex, patch.Name));
        }

        module.Instructions.InsertRange(FindInsertionIndex(module), injected);
        return module.ToBytes();
    }

    private static void RemoveSupersededNames(
        SpirvModule module,
        IReadOnlyList<NamePatch> names,
        IReadOnlyList<MemberNamePatch> memberNames)
    {
        var replacedIds = new HashSet<uint>(names.Count);
        foreach (NamePatch patch in names)
        {
            replacedIds.Add(patch.Id);
        }

        var replacedMembers = new HashSet<(uint StructTypeId, uint MemberIndex)>(memberNames.Count);
        foreach (MemberNamePatch patch in memberNames)
        {
            replacedMembers.Add((patch.StructTypeId, patch.MemberIndex));
        }

        module.Instructions.RemoveAll(instruction => instruction.OpCode switch
        {
            SpvOpCode.OpName => instruction.WordCount >= 2 && replacedIds.Contains(instruction[1]),
            SpvOpCode.OpMemberName => instruction.WordCount >= 3 && replacedMembers.Contains((instruction[1], instruction[2])),
            _ => false,
        });
    }

    private static int FindInsertionIndex(SpirvModule module)
    {
        List<SpirvInstruction> instructions = module.Instructions;
        int end = 0;

        for (int i = 0; i < instructions.Count; i++)
        {
            ushort opCode = instructions[i].OpCode;

            if (IsDebugBlockOpCode(opCode))
            {
                end = i + 1;
                continue;
            }

            if (opCode == SpvOpCode.OpDecorate || opCode >= SpvOpCode.OpTypeVoid)
            {
                break;
            }
        }

        return end;
    }
}
