using Ruri.ShaderTools.Spirv;
using Ruri.ShaderTools.Spirv.Analysis;

namespace Ruri.ShaderTools.Spirv.ConstantBuffers.Stages;

/// <summary>
/// Retires the instructions the lowering stage made redundant: bitcasts, then
/// the loads that fed them, then the access chains that fed those.
///
/// This is NOT housekeeping — it is a correctness requirement. The retarget stage
/// changed each rewritten variable's pointer type to the new block. An old
/// flat-array access chain against that variable indexes a layout that no longer
/// exists, and leaving one behind makes source emission fail with "Cannot
/// subdivide a scalar value" pointing nowhere useful. Once nothing live consumes
/// it, removing it is the only safe option.
///
/// Cascade order matters: bitcasts first (their readers were already retired
/// during lowering), then loads (their readers were those bitcasts), then chains
/// (their readers were those loads). Each retirement immediately decrements the
/// live-use counts, so the next cascade sees the freed dependency without any
/// rescan.
/// </summary>
internal static class DeadAccessPruneStage
{
    public static void Run(StructuringContext context)
    {
        if (context.RetargetedChains.Count == 0)
        {
            return;
        }

        // Both snapshots are taken here, after every insertion the lowering stage
        // made, and the counter is maintained through each retirement below.
        LiveUseCounter live = LiveUseCounter.Build(context.Module);
        ResultIdTable definitions = ResultIdTable.Build(context.Module);

        foreach (KeyValuePair<uint, SpirvInstruction> entry in context.ProcessedBitcasts)
        {
            if (entry.Value.OpCode == SpvOpCode.OpBitcast && !live.HasLiveConsumer(entry.Key))
            {
                live.Retire(entry.Value);
            }
        }

        foreach (TrackedLoad load in context.TrackedLoads.Values)
        {
            SpirvInstruction instruction = load.Instruction;
            if (instruction.OpCode == SpvOpCode.OpLoad && instruction.WordCount >= 4 && !live.HasLiveConsumer(load.ResultId))
            {
                live.Retire(instruction);
            }
        }

        foreach (uint accessChainId in context.RetargetedChains.Keys)
        {
            if (live.HasLiveConsumer(accessChainId))
            {
                continue;
            }

            SpirvInstruction? chain = definitions.DefinitionOf(accessChainId);
            if (chain is not null
                && (chain.OpCode == SpvOpCode.OpAccessChain || chain.OpCode == SpvOpCode.OpInBoundsAccessChain))
            {
                live.Retire(chain);
            }
        }
    }
}
