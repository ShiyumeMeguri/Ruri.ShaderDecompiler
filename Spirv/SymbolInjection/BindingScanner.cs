using Ruri.ShaderTools.Spirv;

namespace Ruri.ShaderTools.Spirv.SymbolInjection;

/// <summary>
/// Finds every <c>(set, binding)</c> resource variable in a module and works out
/// what kind of descriptor each one is.
///
/// One forward walk collects the raw facts (decorations, type relationships,
/// existing names), then classification follows variable → pointer → pointee to
/// bucket each binding. Output is sorted by <c>(set, binding)</c> so downstream
/// iteration is deterministic across SPIR-V revisions.
/// </summary>
internal static class BindingScanner
{
    public static List<DescriptorBindingInfo> Scan(byte[] spirv) => Scan(SpirvModule.Parse(spirv));

    public static List<DescriptorBindingInfo> Scan(SpirvModule module)
    {
        var facts = ModuleFacts.Collect(module);

        var bindings = new List<DescriptorBindingInfo>();
        foreach (KeyValuePair<uint, (int? Set, int? Binding)> entry in facts.SetBindingById)
        {
            if (!entry.Value.Set.HasValue || !entry.Value.Binding.HasValue)
            {
                continue;
            }

            var info = new DescriptorBindingInfo
            {
                Id = entry.Key,
                Set = entry.Value.Set.Value,
                Binding = entry.Value.Binding.Value,
            };

            if (facts.NameById.TryGetValue(entry.Key, out string? currentName))
            {
                info.CurrentName = currentName;
            }

            Classify(facts, info);
            bindings.Add(info);
        }

        bindings.Sort(static (left, right) =>
        {
            int bySet = left.Set.CompareTo(right.Set);
            return bySet != 0 ? bySet : left.Binding.CompareTo(right.Binding);
        });

        return bindings;
    }

    private static void Classify(ModuleFacts facts, DescriptorBindingInfo info)
    {
        if (!facts.VariablePointerType.TryGetValue(info.Id, out uint pointerTypeId)
            || !facts.PointerTarget.TryGetValue(pointerTypeId, out (uint StorageClass, uint Pointee) pointer))
        {
            return;
        }

        uint pointee = pointer.Pointee;

        if (facts.StructTypeIds.Contains(pointee))
        {
            // A struct-backed Uniform is a cbuffer ONLY if it is not decorated
            // BufferBlock. SPIR-V gained a dedicated StorageBuffer storage class
            // in 1.3; before that an SSBO is spelled Uniform + BufferBlock. Miss
            // that and every storage buffer in a 1.0 module — which is what a lot
            // of shipped Vulkan bytecode still is — classifies as a uniform
            // buffer, never matches its t/u register symbol, and keeps a
            // synthetic name.
            bool isUniformBlock = pointer.StorageClass == StorageClass.Uniform
                                  && !facts.BufferBlockTypeIds.Contains(pointee);

            info.Kind = isUniformBlock ? DescriptorKind.UniformBuffer : DescriptorKind.StorageBuffer;
            info.StructTypeId = pointee;
            info.StructMemberCount = facts.StructMemberCount.TryGetValue(pointee, out int count) ? count : 0;

            if (facts.StructMemberOffsets.TryGetValue(pointee, out Dictionary<int, uint>? offsets))
            {
                info.MemberOffsets = offsets;
            }

            foreach (KeyValuePair<(uint StructTypeId, int MemberIndex), string> member in facts.MemberNames)
            {
                if (member.Key.StructTypeId == pointee)
                {
                    info.CurrentMemberNames[member.Key.MemberIndex] = member.Value;
                }
            }

            return;
        }

        if (facts.SamplerTypeIds.Contains(pointee))
        {
            info.Kind = DescriptorKind.Sampler;
            return;
        }

        // Textures may land as a bare OpTypeImage rather than the
        // OpTypeSampledImage shape a direct HLSL→SPIR-V compile produces. Both
        // mean "texture" here; treating only the latter as one leaves whole
        // translation routes stuck on machine names.
        if (facts.SampledImageTypeIds.Contains(pointee) || facts.ImageTypeIds.Contains(pointee))
        {
            info.Kind = DescriptorKind.SampledImage;
            return;
        }

        info.Kind = DescriptorKind.Unknown;
    }

    /// <summary>Raw facts gathered by the single scan, before classification.</summary>
    private sealed class ModuleFacts
    {
        public Dictionary<uint, (int? Set, int? Binding)> SetBindingById { get; } = new();
        public Dictionary<uint, (uint StorageClass, uint Pointee)> PointerTarget { get; } = new();
        public Dictionary<uint, uint> VariablePointerType { get; } = new();
        public HashSet<uint> StructTypeIds { get; } = new();
        public HashSet<uint> BufferBlockTypeIds { get; } = new();
        public Dictionary<uint, int> StructMemberCount { get; } = new();
        public Dictionary<uint, Dictionary<int, uint>> StructMemberOffsets { get; } = new();
        public HashSet<uint> ImageTypeIds { get; } = new();
        public HashSet<uint> SamplerTypeIds { get; } = new();
        public HashSet<uint> SampledImageTypeIds { get; } = new();
        public Dictionary<uint, string> NameById { get; } = new();
        public Dictionary<(uint StructTypeId, int MemberIndex), string> MemberNames { get; } = new();

        public static ModuleFacts Collect(SpirvModule module)
        {
            var facts = new ModuleFacts();
            List<SpirvInstruction> instructions = module.Instructions;

            for (int i = 0; i < instructions.Count; i++)
            {
                SpirvInstruction instruction = instructions[i];
                Span<uint> words = instruction.Words;

                switch (instruction.OpCode)
                {
                    // >= 3, not >= 4: an operand-less decoration such as
                    // BufferBlock is a 3-word instruction, and demanding a fourth
                    // word skips every one of them.
                    case SpvOpCode.OpDecorate when words.Length >= 3:
                        facts.RecordDecoration(words);
                        break;

                    case SpvOpCode.OpMemberDecorate when words.Length >= 5:
                        facts.RecordMemberDecoration(words);
                        break;

                    case SpvOpCode.OpTypePointer when words.Length >= 4:
                        facts.PointerTarget[words[1]] = (words[2], words[3]);
                        break;

                    case SpvOpCode.OpTypeStruct:
                        facts.StructTypeIds.Add(words[1]);
                        facts.StructMemberCount[words[1]] = words.Length >= 2 ? words.Length - 2 : 0;
                        break;

                    case SpvOpCode.OpTypeImage:
                        facts.ImageTypeIds.Add(words[1]);
                        break;

                    case SpvOpCode.OpTypeSampler:
                        facts.SamplerTypeIds.Add(words[1]);
                        break;

                    case SpvOpCode.OpTypeSampledImage:
                        facts.SampledImageTypeIds.Add(words[1]);
                        break;

                    case SpvOpCode.OpName when words.Length >= 3:
                    {
                        string name = SpirvLiteral.ReadString(words, 2);
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            facts.NameById[words[1]] = name;
                        }
                        break;
                    }

                    case SpvOpCode.OpMemberName when words.Length >= 4:
                        // Empty names are recorded too — an explicitly stripped
                        // slot is a real signal, and the dedup pass downstream
                        // replaces it with an offset placeholder.
                        facts.MemberNames[(words[1], (int)words[2])] = SpirvLiteral.ReadString(words, 3);
                        break;

                    case SpvOpCode.OpVariable when words.Length >= 4:
                        facts.VariablePointerType[words[2]] = words[1];
                        break;
                }
            }

            return facts;
        }

        private void RecordDecoration(Span<uint> words)
        {
            uint targetId = words[1];
            uint decoration = words[2];

            if (decoration == Decoration.BufferBlock)
            {
                BufferBlockTypeIds.Add(targetId);
                return;
            }

            if (words.Length < 4)
            {
                return;
            }

            if (decoration == Decoration.DescriptorSet)
            {
                (int? Set, int? Binding) existing = SetBindingById.TryGetValue(targetId, out var value) ? value : (null, null);
                SetBindingById[targetId] = ((int)words[3], existing.Binding);
            }
            else if (decoration == Decoration.Binding)
            {
                (int? Set, int? Binding) existing = SetBindingById.TryGetValue(targetId, out var value) ? value : (null, null);
                SetBindingById[targetId] = (existing.Set, (int)words[3]);
            }
        }

        private void RecordMemberDecoration(Span<uint> words)
        {
            if (words[3] != Decoration.Offset)
            {
                return;
            }

            uint structId = words[1];
            if (!StructMemberOffsets.TryGetValue(structId, out Dictionary<int, uint>? offsets))
            {
                offsets = new Dictionary<int, uint>();
                StructMemberOffsets[structId] = offsets;
            }

            offsets[(int)words[2]] = words[4];
        }
    }
}
