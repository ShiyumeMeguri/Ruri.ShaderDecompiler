using Ruri.ShaderTools.Spirv;

namespace Ruri.ShaderTools.Spirv.ConstantBuffers.Stages;

/// <summary>
/// Turns "load the whole register, then extract component N" into "address
/// member N directly".
///
/// For every tracked load whose pointer is a retargeted chain, each downstream
/// <c>OpCompositeExtract</c> names a specific component; combined with the
/// chain's register that is a precise byte offset, which usually maps to a named
/// member. The extract is then replaced by a fresh access-chain + load pair that
/// reads the member directly, and — crucially — the new load INHERITS the
/// extract's result id, so every existing consumer keeps working untouched.
///
/// The bitcast hop (<c>Load v4float → Bitcast v4uint → CompositeExtract uint</c>)
/// is followed transparently; it is how bool members stored as uint are read.
/// The now-dead bitcast is left for the prune stage.
///
/// Loads with no component readers at all are simply re-typed in place, when
/// their chain translated to a vector-shaped member. That keeps the load valid
/// against the new pointer type without needing a downstream extract to hang the
/// rewrite on.
/// </summary>
internal static class ComponentReadLoweringStage
{
    public static void Run(StructuringContext context)
    {
        if (context.RetargetedChains.Count == 0)
        {
            return;
        }

        var trackedLoads = new Dictionary<uint, TrackedLoad>();

        // Bitcast result id → (its source load, the bitcast itself). SSA order
        // guarantees a bitcast follows its source load in module order, so this
        // single forward pass is sound.
        var bitcastSources = new Dictionary<uint, (TrackedLoad Load, SpirvInstruction Bitcast)>();

        var processedBitcasts = new Dictionary<uint, SpirvInstruction>();
        context.TrackedLoads = trackedLoads;
        context.ProcessedBitcasts = processedBitcasts;

        List<SpirvInstruction> instructions = context.Module.Instructions;

        for (int index = 0; index < instructions.Count; index++)
        {
            SpirvInstruction instruction = instructions[index];

            if (instruction.OpCode == SpvOpCode.OpLoad && instruction.WordCount >= 4
                && context.RetargetedChains.TryGetValue(instruction[3], out RetargetedChain? chain))
            {
                trackedLoads[instruction[2]] = new TrackedLoad
                {
                    Instruction = instruction,
                    ResultId = instruction[2],
                    OriginalResultTypeId = instruction[1],
                    HasComponentReaders = false,
                    AccessChain = chain,
                };
                continue;
            }

            if (instruction.OpCode == SpvOpCode.OpBitcast && instruction.WordCount >= 4
                && trackedLoads.TryGetValue(instruction[3], out TrackedLoad? bitcastSource))
            {
                bitcastSources[instruction[2]] = (bitcastSource, instruction);
                continue;
            }

            if (instruction.OpCode != SpvOpCode.OpCompositeExtract || instruction.WordCount < 5)
            {
                continue;
            }

            uint compositeId = instruction[3];
            TrackedLoad? load;

            if (trackedLoads.TryGetValue(compositeId, out TrackedLoad? direct))
            {
                load = direct;
            }
            else if (bitcastSources.TryGetValue(compositeId, out (TrackedLoad Load, SpirvInstruction Bitcast) viaBitcast))
            {
                load = viaBitcast.Load;
                processedBitcasts[compositeId] = viaBitcast.Bitcast;
            }
            else
            {
                continue;
            }

            load.HasComponentReaders = true;

            FlatAccessPath componentPath = load.AccessChain.OriginalAccessPath.With(instruction.Words[4..]);
            AccessTranslation? translation = AccessTranslator.Translate(load.AccessChain.Plan.Layout, componentPath, context.Constants);

            if (translation is null || !context.UniformPointerTypes.TryGetValue(translation.MemberTypeId, out uint pointerTypeId))
            {
                continue;
            }

            // Capture before the extract is retired — the new load adopts its id.
            uint extractResultId = instruction[2];
            uint pointerResultId = context.Module.AllocateId();
            ushort chainOpCode = load.AccessChain.InstructionOpCode;

            instructions.Insert(index, BuildChain(context.Module, chainOpCode, pointerTypeId, pointerResultId,
                load.AccessChain.BaseVariableId, translation.Indices));

            instructions.Insert(index + 1, context.Module.CreateInstruction(SpvOpCode.OpLoad,
            [
                SpvOpCode.MakeInstructionWord(SpvOpCode.OpLoad, 4),
                translation.MemberTypeId, extractResultId, pointerResultId,
            ]));

            // Two instructions inserted ahead of the extract; step past both so
            // the loop resumes at the instruction that followed it.
            index += 2;
            instruction.MakeNop();
        }

        // Loads whose only reader consumed the whole vector never entered the
        // rewrite branch above. Re-type them so they stay valid against the new
        // pointer type.
        foreach (TrackedLoad load in trackedLoads.Values)
        {
            SpirvInstruction instruction = load.Instruction;
            if (instruction.OpCode != SpvOpCode.OpLoad || instruction.WordCount < 4)
            {
                continue;
            }

            if (!load.HasComponentReaders && load.AccessChain.Translation is not null)
            {
                instruction[1] = load.AccessChain.Translation.MemberTypeId;
            }
        }
    }

    private static SpirvInstruction BuildChain(
        SpirvModule module, ushort opCode, uint pointerTypeId, uint resultId, uint baseVariableId, List<uint> indices)
    {
        int wordCount = 4 + indices.Count;
        Span<uint> words = wordCount <= 64 ? stackalloc uint[wordCount] : new uint[wordCount];

        words[0] = SpvOpCode.MakeInstructionWord(opCode, (ushort)wordCount);
        words[1] = pointerTypeId;
        words[2] = resultId;
        words[3] = baseVariableId;
        for (int i = 0; i < indices.Count; i++)
        {
            words[4 + i] = indices[i];
        }

        return module.CreateInstruction(opCode, words);
    }
}
