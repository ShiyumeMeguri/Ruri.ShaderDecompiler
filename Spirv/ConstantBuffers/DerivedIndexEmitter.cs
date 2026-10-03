using Ruri.ShaderTools.Spirv;

namespace Ruri.ShaderTools.Spirv.ConstantBuffers;

/// <summary>
/// Builds a translation's derived element index right before the chain that uses it: <c>dynamic * multiplier</c>, then
/// <c>+ addend</c>, each step only when it changes the value, in the dynamic index's own integer type. The dynamic index is
/// defined ahead of the chain it fed and the constants are module-level, so both instructions dominate the chain they are
/// placed in front of.
/// </summary>
internal static class DerivedIndexEmitter
{
    /// <returns>How many instructions were inserted at <paramref name="position"/>.</returns>
    public static int Emit(StructuringContext context, List<SpirvInstruction> instructions, int position, AccessTranslation translation)
    {
        if (translation.Derived is not { } derived)
        {
            return 0;
        }

        SpirvInstruction definition = context.Definitions.DefinitionOf(derived.DynamicIndexId)
            ?? throw new InvalidOperationException($"derived index over %{derived.DynamicIndexId}, which no instruction defines");
        uint typeId = definition[1];
        uint value = derived.DynamicIndexId;
        int inserted = 0;

        if (derived.Multiplier != 1)
        {
            uint product = context.Module.AllocateId();
            instructions.Insert(position + inserted, context.Module.CreateInstruction(SpvOpCode.OpIMul,
            [
                SpvOpCode.MakeInstructionWord(SpvOpCode.OpIMul, 5), typeId, product, value, derived.MultiplierConstantId,
            ]));
            inserted++;
            value = product;
        }

        if (derived.Addend != 0)
        {
            uint sum = context.Module.AllocateId();
            instructions.Insert(position + inserted, context.Module.CreateInstruction(SpvOpCode.OpIAdd,
            [
                SpvOpCode.MakeInstructionWord(SpvOpCode.OpIAdd, 5), typeId, sum, value, derived.AddendConstantId,
            ]));
            inserted++;
            value = sum;
        }

        translation.Indices[derived.Position] = value;
        return inserted;
    }
}
