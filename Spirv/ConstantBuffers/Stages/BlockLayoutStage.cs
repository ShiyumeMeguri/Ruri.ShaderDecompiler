namespace Ruri.ShaderTools.Spirv.ConstantBuffers.Stages;

/// <summary>
/// Turns each matched buffer into a rewrite plan carrying its symbol-derived
/// member layout.
///
/// The fit check is intentionally strict. A stride other than 16 means the
/// SPIR-V buffer was never compiled as a constant buffer at all, and a layout
/// whose members all sit past the flat array would produce out-of-bounds reads
/// in the rewritten module. Either way the buffer is dropped rather than
/// rewritten into something plausible-looking.
///
/// NOTHING IS PRUNED HERE, deliberately. A layout can carry members no access
/// chain reaches — gap fillers bridging holes the symbols do not describe, or
/// members the symbols name that this permutation never reads. Dropping them
/// would make the emitted block describe less than the buffer actually holds,
/// and a decompiled constant buffer is a record of the buffer, not of one
/// variant's usage of it. Data completeness wins over tidiness.
/// </summary>
internal static class BlockLayoutStage
{
    public static void Run(StructuringContext context)
    {
        foreach (FlatBlockView block in context.Blocks)
        {
            BlockLayout? layout = BlockLayoutBuilder.Build(block);
            if (layout is null)
            {
                context.Note(block, "layout build failed");
                continue;
            }

            if (!FitsFlatBuffer(block, layout))
            {
                context.Note(block, $"layout does not fit flat buffer: stride={block.ArrayStride}, arrayLength={block.ArrayLength}, usedRegisters={layout.MaxUsedRegisterCount}");
                continue;
            }

            context.Plans.Add(new BlockRewritePlan { Block = block, Layout = layout });
        }
    }

    private static bool FitsFlatBuffer(FlatBlockView block, BlockLayout layout)
    {
        if (block.ArrayStride != 16)
        {
            return false;
        }

        if (layout.Members.Count == 0)
        {
            return false;
        }

        foreach (BlockMemberLayout member in layout.Members)
        {
            if (member.RegisterOffset < block.ArrayLength)
            {
                return true;
            }
        }

        return false;
    }
}
