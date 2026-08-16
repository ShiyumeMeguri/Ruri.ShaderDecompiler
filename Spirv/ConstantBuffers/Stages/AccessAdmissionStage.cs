using Ruri.ShaderTools.Pipeline;
using Ruri.ShaderTools.Spirv;
using Ruri.ShaderTools.Spirv.Analysis;

namespace Ruri.ShaderTools.Spirv.ConstantBuffers.Stages;

/// <summary>
/// Gate: can EVERY access chain into this buffer be rewritten?
///
/// All-or-nothing by design. A partial rewrite leaves the variable's pointer
/// type describing the new block while some access chain still indexes the old
/// flat array — an invalid module that fails much later, deep inside source
/// emission, with an error that points nowhere near the real cause.
///
/// Two acceptable outcomes per chain:
///   1. it translates directly to a member, or
///   2. it reads a whole register no member matches, but every downstream
///      component read DOES translate — the lowering stage will handle it.
///
/// Failure lines carry the raw instruction words on purpose: that is enough to
/// disassemble the dumped module and see exactly which access pattern needs
/// support, without having to reproduce the shader.
/// </summary>
internal static class AccessAdmissionStage
{
    public static StageVerdict Run(StructuringContext context)
    {
        // One index for the whole stage. Admission only reads — it never mutates —
        // so a single snapshot serves every plan.
        OperandUseIndex uses = OperandUseIndex.Build(context.Module);

        var survived = new List<BlockRewritePlan>(context.Plans.Count);
        foreach (BlockRewritePlan plan in context.Plans)
        {
            if (!AllChainsRewritable(context, uses, plan, out string? failure))
            {
                context.Note(plan, $"rewrite validation failed: {failure}");
                continue;
            }

            survived.Add(plan);
        }

        context.Plans = survived;

        // Silent halt — every dropped plan already logged its own reason.
        return context.Plans.Count == 0 ? StageVerdict.Halt : StageVerdict.Continue;
    }

    private static bool AllChainsRewritable(
        StructuringContext context,
        OperandUseIndex uses,
        BlockRewritePlan plan,
        out string? failure)
    {
        failure = null;
        int chainCount = 0;

        foreach (SpirvInstruction instruction in uses.UsersOf(plan.Block.VariableId))
        {
            if ((instruction.OpCode != SpvOpCode.OpAccessChain && instruction.OpCode != SpvOpCode.OpInBoundsAccessChain)
                || instruction.WordCount < 4
                || instruction[3] != plan.Block.VariableId)
            {
                continue;
            }

            chainCount++;

            if (!FlatAccessChainParser.TryParse(instruction, context.Constants, context.Definitions, out FlatAccessPath path))
            {
                failure = $"unsupported access chain parse for resultId={instruction[2]} op={instruction.OpCode} words=[{instruction.FormatWords()}]";
                return false;
            }

            bool direct = AccessTranslator.Translate(plan.Layout, path, context.Constants) is not null;
            if (direct)
            {
                continue;
            }

            if (ComponentReadProbe.CanLowerAllReads(uses, instruction[2], plan.Layout, path, context.Constants))
            {
                continue;
            }

            failure = $"unsupported access translation for resultId={instruction[2]} " +
                      $"slotConst={path.Slot.ConstantRegisterOffset} slotDynamic={path.Slot.DynamicIndexId} " +
                      $"stride={path.Slot.DynamicIndexStride} extra=[{string.Join(",", path.ExtraIndices)}] " +
                      $"op={instruction.OpCode} words=[{instruction.FormatWords()}]";
            return false;
        }

        if (chainCount == 0)
        {
            failure = "no access chains found for variable";
            return false;
        }

        return true;
    }
}
