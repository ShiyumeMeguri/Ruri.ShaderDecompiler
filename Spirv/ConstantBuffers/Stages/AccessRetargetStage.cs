using Ruri.ShaderTools.Spirv;
using Ruri.ShaderTools.Spirv.Analysis;

namespace Ruri.ShaderTools.Spirv.ConstantBuffers.Stages;

/// <summary>
/// Points each rewritten variable at its new block type and retargets every
/// access chain into it.
///
/// Two outcomes per chain:
///   * direct translation — the chain's words are rewritten in place to address
///     the named member, and it is recorded WITH its translation.
///   * component-read path — the chain reads a whole register no member matches,
///     so its words are left untouched and it is recorded WITHOUT a translation,
///     for the lowering stage to fork per component read.
///
/// All pointer types are minted BEFORE the instruction walk, because minting one
/// inserts into the module and the walk must iterate a stable list. Pointer types
/// are pre-registered for every depth a translation can return — member, array
/// element, matrix column, component scalar, and the same four for nested struct
/// children — so a perfectly translatable chain is never skipped merely because
/// its result pointer type had not been created yet.
/// </summary>
internal static class AccessRetargetStage
{
    public static void Run(StructuringContext context)
    {
        if (context.Plans.Count == 0)
        {
            return;
        }

        // Two plans CAN name the same variable: a symbol table may list two
        // differently named constant buffers at one descriptor slot (alternative
        // fillings of the same slot). Which one the module means is not knowable
        // here, and a variable can only be rewritten once — so keep the first
        // deterministically rather than failing the whole shader over a naming
        // ambiguity that costs at most one wrong label.
        var planByVariable = new Dictionary<uint, BlockRewritePlan>();
        foreach (BlockRewritePlan plan in context.Plans)
        {
            planByVariable.TryAdd(plan.Block.VariableId, plan);
        }

        var pointerTypes = new Dictionary<uint, uint>();
        foreach (BlockRewritePlan plan in context.Plans)
        {
            foreach (uint memberTypeId in plan.MemberTypeIds)
            {
                EnsurePointerType(context, pointerTypes, memberTypeId);
            }

            foreach (BlockMemberLayout member in plan.Layout.Members)
            {
                EnsurePointerType(context, pointerTypes, member.ScalarTypeId);
                EnsurePointerType(context, pointerTypes, member.ColumnVectorTypeId);
                EnsurePointerType(context, pointerTypes, member.ArrayElementTypeId);

                if (member.Shape.StructMembers is null)
                {
                    continue;
                }

                foreach (BlockMemberLayout child in member.Shape.StructMembers)
                {
                    EnsurePointerType(context, pointerTypes, child.ResolvedTypeId);
                    EnsurePointerType(context, pointerTypes, child.ScalarTypeId);
                    EnsurePointerType(context, pointerTypes, child.ColumnVectorTypeId);
                    EnsurePointerType(context, pointerTypes, child.ArrayElementTypeId);
                }
            }
        }

        // Snapshot after type minting, before the rewrite walk. The walk only
        // edits access-chain operands, and the probe only inspects
        // load / bitcast / extract relationships, so this stays valid throughout.
        OperandUseIndex uses = OperandUseIndex.Build(context.Module);

        var retargeted = new Dictionary<uint, RetargetedChain>();
        List<SpirvInstruction> instructions = context.Module.Instructions;

        for (int i = 0; i < instructions.Count; i++)
        {
            SpirvInstruction instruction = instructions[i];

            if (instruction.OpCode == SpvOpCode.OpVariable)
            {
                if (instruction.WordCount >= 4 && planByVariable.TryGetValue(instruction[2], out BlockRewritePlan? owner))
                {
                    instruction[1] = owner.NewPointerTypeId;
                }
                continue;
            }

            if ((instruction.OpCode != SpvOpCode.OpAccessChain && instruction.OpCode != SpvOpCode.OpInBoundsAccessChain)
                || instruction.WordCount < 4)
            {
                continue;
            }

            if (!planByVariable.TryGetValue(instruction[3], out BlockRewritePlan? plan)
                || !FlatAccessChainParser.TryParse(instruction, context.Constants, context.Definitions, out FlatAccessPath path))
            {
                continue;
            }

            AccessTranslation? translation = AccessTranslator.Translate(plan.Layout, path, context.Constants);

            if (translation is null)
            {
                if (ComponentReadProbe.CanLowerAllReads(uses, instruction[2], plan.Layout, path, context.Constants))
                {
                    retargeted[instruction[2]] = new RetargetedChain
                    {
                        AccessChainResultId = instruction[2],
                        BaseVariableId = instruction[3],
                        InstructionOpCode = instruction.OpCode,
                        Plan = plan,
                        OriginalAccessPath = path.Clone(),
                    };
                }
                continue;
            }

            if (!pointerTypes.TryGetValue(translation.MemberTypeId, out uint pointerTypeId))
            {
                continue;
            }

            WriteChain(instruction, pointerTypeId, translation.Indices);

            retargeted[instruction[2]] = new RetargetedChain
            {
                AccessChainResultId = instruction[2],
                BaseVariableId = instruction[3],
                InstructionOpCode = instruction.OpCode,
                Plan = plan,
                OriginalAccessPath = path.Clone(),
                Translation = translation,
            };
        }

        context.RetargetedChains = retargeted;
        context.UniformPointerTypes = pointerTypes;
    }

    private static void EnsurePointerType(StructuringContext context, Dictionary<uint, uint> cache, uint typeId)
    {
        if (typeId == 0 || cache.ContainsKey(typeId))
        {
            return;
        }

        cache[typeId] = context.Types.InternUniformPointer(typeId);
    }

    private static void WriteChain(SpirvInstruction instruction, uint pointerTypeId, List<uint> indices)
    {
        int wordCount = 4 + indices.Count;
        Span<uint> words = wordCount <= 64 ? stackalloc uint[wordCount] : new uint[wordCount];

        words[0] = SpvOpCode.MakeInstructionWord(instruction.OpCode, (ushort)wordCount);
        words[1] = pointerTypeId;
        words[2] = instruction[2];
        words[3] = instruction[3];
        for (int i = 0; i < indices.Count; i++)
        {
            words[4 + i] = indices[i];
        }

        instruction.SetWords(words);
    }
}
