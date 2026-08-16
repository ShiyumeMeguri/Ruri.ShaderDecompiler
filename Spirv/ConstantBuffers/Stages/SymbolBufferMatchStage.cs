using Ruri.ShaderTools.Pipeline;
using Ruri.ShaderTools.Spirv;

namespace Ruri.ShaderTools.Spirv.ConstantBuffers.Stages;

/// <summary>
/// Pairs each engine-declared constant buffer with the SPIR-V variable that
/// implements it.
///
/// Matching is <c>(set, binding)</c> plus a SHAPE check. A compiled constant
/// buffer arrives as:
/// <code>
///   %CBnUBO = OpTypeStruct %_arr_v4float_uint_N
///   %var    = OpVariable %_ptr_Uniform_CBnUBO Uniform
/// </code>
/// — a one-member wrapper struct around a fixed-length <c>float4</c> array.
/// Nothing else is rewritten, because nothing else has a flat register space to
/// translate FROM.
///
/// Every rejection is logged with its reason. That matters more than it looks:
/// a constant buffer that stays flat in the output is otherwise
/// indistinguishable from one that was never tried, and "why is this buffer
/// still <c>_m0[N]</c>" is the question this pipeline gets asked most.
/// </summary>
internal static class SymbolBufferMatchStage
{
    public static StageVerdict Run(StructuringContext context)
    {
        var matched = new List<FlatBlockView>();

        foreach (BufferBindingParameter binding in context.Symbols.BufferBindingParameters)
        {
            ConstantBufferParameter? symbol = context.Symbols.GetConstantBufferByName(binding.Name);
            if (symbol is null)
            {
                context.Note($"[{binding.Name}] no constant buffer symbol found");
                continue;
            }

            int set = context.Symbols.GetSetIdFor(binding.Index, ShaderResourceType.ConstantBuffer, binding.Name);

            uint variableId = FindUniformVariable(context, set, binding.Index);
            if (variableId == 0)
            {
                context.Note($"[{binding.Name}] no decorated id for set={set} binding={binding.Index}");
                continue;
            }

            if (!context.Shape.VariablePointerTypes.TryGetValue(variableId, out uint pointerTypeId))
            {
                context.Note($"[{binding.Name}] decorated id {variableId} is not an OpVariable");
                continue;
            }

            if (!context.Shape.PointerTypes.TryGetValue(pointerTypeId, out (uint StorageClass, uint TypeId) pointer)
                || pointer.StorageClass != StorageClass.Uniform)
            {
                context.Note($"[{binding.Name}] variable {variableId} is not a uniform pointer");
                continue;
            }

            if (!context.Shape.StructMembers.TryGetValue(pointer.TypeId, out uint[]? wrapperMembers) || wrapperMembers.Length != 1)
            {
                context.Note($"[{binding.Name}] variable {variableId} is not a single-member wrapper struct");
                continue;
            }

            uint arrayTypeId = wrapperMembers[0];
            if (!context.Shape.ArrayTypes.TryGetValue(arrayTypeId, out (uint ElementTypeId, uint LengthId) array)
                || !context.Shape.Constants.TryGetValue(array.LengthId, out uint arrayLength))
            {
                context.Note($"[{binding.Name}] wrapper member is not a fixed array type");
                continue;
            }

            matched.Add(new FlatBlockView
            {
                VariableId = variableId,
                PointerTypeId = pointerTypeId,
                StructTypeId = pointer.TypeId,
                ArrayTypeId = arrayTypeId,
                ElementTypeId = array.ElementTypeId,
                ArrayLength = checked((int)arrayLength),
                ArrayStride = context.Shape.ArrayStrides.TryGetValue(arrayTypeId, out uint stride) ? checked((int)stride) : 16,
                Binding = new SymbolBlockBinding { Name = binding.Name, Binding = binding.Index, Set = set },
                Symbol = symbol,
            });
        }

        context.Blocks = matched;

        // Halting silently is deliberate: the per-buffer rejection reasons above
        // already say why nothing matched, and the driver supplies a summary
        // fallback for the (unreachable in practice) case of an empty log.
        return matched.Count == 0 ? StageVerdict.Halt : StageVerdict.Continue;
    }

    private static uint FindUniformVariable(StructuringContext context, int set, int binding)
    {
        foreach (KeyValuePair<uint, (int? Set, int? Binding)> entry in context.Shape.SetBindingById)
        {
            if (entry.Value.Set != set || entry.Value.Binding != binding)
            {
                continue;
            }

            if (context.Shape.VariablePointerTypes.TryGetValue(entry.Key, out uint pointerTypeId)
                && context.Shape.PointerTypes.TryGetValue(pointerTypeId, out (uint StorageClass, uint TypeId) pointer)
                && pointer.StorageClass == StorageClass.Uniform)
            {
                return entry.Key;
            }
        }

        return 0;
    }
}
