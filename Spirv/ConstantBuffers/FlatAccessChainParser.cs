using Ruri.ShaderTools.Spirv;
using Ruri.ShaderTools.Spirv.Analysis;

namespace Ruri.ShaderTools.Spirv.ConstantBuffers;

/// <summary>
/// Reads an <c>OpAccessChain</c> into a <see cref="FlatAccessPath"/>: which
/// register it selects, and which literal indices it walks afterwards.
///
/// Parsing is a genuinely separate job from translating — one reads bytecode,
/// the other reads a layout — and keeping them apart is what lets the
/// component-read path fork a parsed path and re-translate it without touching
/// the module again.
/// </summary>
internal static class FlatAccessChainParser
{
    /// <summary>
    /// Operand layout: <c>[header, resultType, result, base, index0, index1…]</c>,
    /// so the first index sits at slot 4.
    /// </summary>
    private const int FirstIndexSlot = 4;

    public static bool TryParse(
        SpirvInstruction instruction,
        ConstantValueMap constants,
        ResultIdTable definitions,
        out FlatAccessPath accessPath)
    {
        accessPath = null!;
        if (instruction.WordCount < FirstIndexSlot)
        {
            return false;
        }

        // The compiled form is `wrapper[0].arr[register]`, so slot 4 is usually a
        // constant zero selecting the wrapper struct's only member and the real
        // register slot is slot 5. Detect that leading all-zero index and step
        // past it; a chain that starts directly at the register (no wrapper hop)
        // is left alone.
        int slotIndex = FirstIndexSlot;
        if (instruction.WordCount >= 6
            && SlotExpressionDecomposer.TryParse(instruction[FirstIndexSlot], constants, definitions, out SlotExpression leading)
            && leading.DynamicIndexId == 0
            && leading.DynamicIndexStride == 0
            && leading.ConstantRegisterOffset == 0)
        {
            slotIndex = FirstIndexSlot + 1;
        }

        if (!SlotExpressionDecomposer.TryParse(instruction[slotIndex], constants, definitions, out SlotExpression slot))
        {
            return false;
        }

        // Everything past the register slot must be a literal index — a dynamic
        // component index cannot be resolved against a named member, and letting
        // one through would produce an access chain addressing the wrong depth.
        var extraIndices = new List<int>();
        for (int operand = slotIndex + 1; operand < instruction.WordCount; operand++)
        {
            if (!constants.TryGetValue(instruction[operand], out uint value))
            {
                return false;
            }

            extraIndices.Add(checked((int)value));
        }

        accessPath = new FlatAccessPath { Slot = slot, ExtraIndices = extraIndices };
        return true;
    }
}
