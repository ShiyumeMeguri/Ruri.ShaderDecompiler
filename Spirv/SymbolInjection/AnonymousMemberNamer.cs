namespace Ruri.ShaderTools.Spirv.SymbolInjection;

/// <summary>
/// Gives a marker name to every constant-buffer member the module left unnamed —
/// including members of structs nested inside one.
///
/// WITHOUT THIS the emitter invents <c>_m0</c>, <c>_m1</c>… for them, and those
/// are indistinguishable from fields genuinely called that. Top-level members are
/// covered by the naming layer, which works from the symbol table; a member of a
/// NESTED struct is not, because the symbol table describes the buffer's own
/// layout and stops there. That left exactly one place still leaking machine
/// names into the output.
///
/// Only members with no existing name are touched, so a real name — whether it
/// came from the bytecode or from symbol injection running afterwards — always
/// wins. The offset used is the one the module itself declares, which for a
/// nested struct is relative to that struct.
///
/// Runs before symbol injection: anything this names that the naming layer also
/// has a real name for gets overwritten there, which is the intended order.
/// </summary>
internal static class AnonymousMemberNamer
{
    public static byte[] Apply(byte[] spirv)
    {
        SpirvModule module;
        try
        {
            module = SpirvModule.Parse(spirv);
        }
        catch
        {
            return spirv;
        }

        ModuleFacts facts = ModuleFacts.Collect(module);
        if (facts.BlockStructs.Count == 0)
        {
            return spirv;
        }

        // Walk out from every constant-buffer struct through its members, so a
        // struct only reachable as a member — the common shape for per-instance
        // data — is covered too.
        var pending = new Queue<uint>(facts.BlockStructs);
        var visited = new HashSet<uint>();
        var named = new List<(uint StructTypeId, uint MemberIndex, string Name)>();

        while (pending.Count > 0)
        {
            uint structTypeId = pending.Dequeue();
            if (!visited.Add(structTypeId) || !facts.StructMembers.TryGetValue(structTypeId, out uint[]? members))
            {
                continue;
            }

            for (int memberIndex = 0; memberIndex < members.Length; memberIndex++)
            {
                if (!facts.NamedMembers.Contains((structTypeId, memberIndex))
                    && facts.MemberOffsets.TryGetValue((structTypeId, memberIndex), out uint byteOffset))
                {
                    named.Add((structTypeId, (uint)memberIndex, $"{GeneratedNames.StrippedSymbol}_{byteOffset}"));
                }

                // Follow the member's type down to a struct, through any array or
                // pointer wrappers in between.
                uint reached = facts.ResolveStruct(members[memberIndex]);
                if (reached != 0)
                {
                    pending.Enqueue(reached);
                }
            }
        }

        if (named.Count == 0)
        {
            return spirv;
        }

        foreach ((uint structTypeId, uint memberIndex, string name) in named)
        {
            module.InsertDebugMemberName(structTypeId, memberIndex, name);
        }

        return module.ToBytes();
    }

    private sealed class ModuleFacts
    {
        public HashSet<uint> BlockStructs { get; } = new();
        public Dictionary<uint, uint[]> StructMembers { get; } = new();
        public Dictionary<(uint StructTypeId, int MemberIndex), uint> MemberOffsets { get; } = new();
        public HashSet<(uint StructTypeId, int MemberIndex)> NamedMembers { get; } = new();

        private readonly Dictionary<uint, uint> _arrayElement = new();
        private readonly Dictionary<uint, uint> _pointerTarget = new();

        /// <summary>Peel array and pointer wrappers off a member type and report
        /// the struct underneath, or 0 when there is none.</summary>
        public uint ResolveStruct(uint typeId)
        {
            for (int hops = 0; hops < 8; hops++)
            {
                if (StructMembers.ContainsKey(typeId))
                {
                    return typeId;
                }

                if (_arrayElement.TryGetValue(typeId, out uint element))
                {
                    typeId = element;
                    continue;
                }

                if (_pointerTarget.TryGetValue(typeId, out uint pointee))
                {
                    typeId = pointee;
                    continue;
                }

                return 0;
            }

            return 0;
        }

        public static ModuleFacts Collect(SpirvModule module)
        {
            var facts = new ModuleFacts();

            foreach (SpirvInstruction instruction in module.Instructions)
            {
                Span<uint> words = instruction.Words;

                switch (instruction.OpCode)
                {
                    // Both flavours of laid-out buffer are roots. A storage buffer
                    // is spelled Uniform + BufferBlock before SPIR-V 1.3 and
                    // StorageBuffer + Block after; its element struct has the same
                    // explicit offsets and the same anonymous members, so treating
                    // only cbuffers as roots leaves every structured buffer's
                    // element type emitting machine names.
                    case SpvOpCode.OpDecorate when words.Length >= 3
                        && (words[2] == Decoration.Block || words[2] == Decoration.BufferBlock):
                        facts.BlockStructs.Add(words[1]);
                        break;

                    case SpvOpCode.OpMemberDecorate when words.Length >= 5 && words[3] == Decoration.Offset:
                        facts.MemberOffsets[(words[1], (int)words[2])] = words[4];
                        break;

                    case SpvOpCode.OpMemberName when words.Length >= 4:
                    {
                        string existing = SpirvLiteral.ReadString(words, 3);
                        if (!string.IsNullOrWhiteSpace(existing))
                        {
                            facts.NamedMembers.Add((words[1], (int)words[2]));
                        }
                        break;
                    }

                    case SpvOpCode.OpTypeStruct when words.Length >= 2:
                        facts.StructMembers[words[1]] = words[2..].ToArray();
                        break;

                    case SpvOpCode.OpTypeArray when words.Length >= 3:
                        facts._arrayElement[words[1]] = words[2];
                        break;

                    case SpvOpCode.OpTypeRuntimeArray when words.Length >= 3:
                        facts._arrayElement[words[1]] = words[2];
                        break;

                    case SpvOpCode.OpTypePointer when words.Length >= 4:
                        facts._pointerTarget[words[1]] = words[3];
                        break;
                }
            }

            return facts;
        }
    }
}
