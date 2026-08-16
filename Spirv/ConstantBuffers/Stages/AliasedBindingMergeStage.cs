using Ruri.ShaderTools.Spirv;

namespace Ruri.ShaderTools.Spirv.ConstantBuffers.Stages;

/// <summary>
/// Collapses several uniform variables that share one <c>(set, binding)</c> onto
/// a single survivor.
///
/// Compilers sometimes emit ONE constant buffer as SEVERAL variables at the
/// identical <c>(set, binding)</c> — one carrying constant-index component reads
/// that drill to a scalar, another carrying whole-register reads. Vulkan only
/// ever has one physical resource at a given <c>(set, binding)</c>, so these are
/// redundant declarations of the same logical buffer.
///
/// Left alone, the matching stage claims only the first of them (its symbol
/// lookup is one-per-binding) and structures that one. The others keep their raw
/// flat pointer type, so the emitter produces a duplicate
/// <c>float4 _bN_&lt;name&gt;[len]</c> buffer and every code path reading through
/// them stays flat — losing the names for those registers even though the
/// rewrite "succeeded".
///
/// So the merge runs BEFORE any structuring: access chains are rebased onto the
/// survivor, then the aliases and their decorations are retired. The decorations
/// have to go too, not just the variables: an emitter produces a cbuffer for any
/// decorated uniform variable, referenced or not.
///
/// Survivor selection is the LONGEST flat array, so its layout covers every
/// register any twin could reach. All twins are identically-shaped
/// <c>struct { float4[N] }</c> wrappers, so a rebased index sequence stays valid
/// verbatim.
///
/// Fully generic — no engine-specific branch. The same duplicate artifact shows
/// up across unrelated engines and this clears all of them.
/// </summary>
internal static class AliasedBindingMergeStage
{
    public static void Run(StructuringContext context)
    {
        foreach (KeyValuePair<(int Set, int Binding), List<uint>> group in GroupRewritableUniforms(context))
        {
            List<uint> variables = group.Value;
            if (variables.Count < 2)
            {
                continue;
            }

            uint survivor = SelectSurvivor(context, variables);

            var aliases = new HashSet<uint>(variables);
            aliases.Remove(survivor);

            RebaseAccessChains(context.Module, aliases, survivor);
            RetireAliases(context, aliases);

            context.Note($"[merge set={group.Key.Set} binding={group.Key.Binding}] collapsed {aliases.Count} duplicate OpVariable(s) onto survivor %{survivor}");
        }
    }

    // Only the single-member-wrapper-around-a-fixed-float4-array shape is a
    // candidate. Anything else sharing a binding (there should be nothing) is
    // left strictly alone.
    private static Dictionary<(int Set, int Binding), List<uint>> GroupRewritableUniforms(StructuringContext context)
    {
        var byBinding = new Dictionary<(int Set, int Binding), List<uint>>();

        foreach (KeyValuePair<uint, (int? Set, int? Binding)> entry in context.Shape.SetBindingById)
        {
            if (!entry.Value.Set.HasValue || !entry.Value.Binding.HasValue)
            {
                continue;
            }

            if (!context.Shape.TryGetUniformBlockArrayLength(entry.Key, out _))
            {
                continue;
            }

            (int Set, int Binding) key = (entry.Value.Set.Value, entry.Value.Binding.Value);
            if (!byBinding.TryGetValue(key, out List<uint>? list))
            {
                list = new List<uint>();
                byBinding[key] = list;
            }
            list.Add(entry.Key);
        }

        return byBinding;
    }

    private static uint SelectSurvivor(StructuringContext context, List<uint> variables)
    {
        uint best = variables[0];
        int bestLength = context.Shape.TryGetUniformBlockArrayLength(best, out int length) ? length : 0;

        for (int i = 1; i < variables.Count; i++)
        {
            int candidateLength = context.Shape.TryGetUniformBlockArrayLength(variables[i], out int candidate) ? candidate : 0;
            if (candidateLength > bestLength)
            {
                best = variables[i];
                bestLength = candidateLength;
            }
        }

        return best;
    }

    private static void RebaseAccessChains(SpirvModule module, HashSet<uint> aliases, uint survivor)
    {
        List<SpirvInstruction> instructions = module.Instructions;
        for (int i = 0; i < instructions.Count; i++)
        {
            SpirvInstruction instruction = instructions[i];
            if ((instruction.OpCode != SpvOpCode.OpAccessChain && instruction.OpCode != SpvOpCode.OpInBoundsAccessChain)
                || instruction.WordCount < 4)
            {
                continue;
            }

            if (aliases.Contains(instruction[3]))
            {
                instruction[3] = survivor;
            }
        }
    }

    private static void RetireAliases(StructuringContext context, HashSet<uint> aliases)
    {
        List<SpirvInstruction> instructions = context.Module.Instructions;
        for (int i = 0; i < instructions.Count; i++)
        {
            SpirvInstruction instruction = instructions[i];
            bool targetsAlias = instruction.OpCode switch
            {
                SpvOpCode.OpVariable => instruction.WordCount >= 3 && aliases.Contains(instruction[2]),
                SpvOpCode.OpDecorate => instruction.WordCount >= 2 && aliases.Contains(instruction[1]),
                SpvOpCode.OpName => instruction.WordCount >= 2 && aliases.Contains(instruction[1]),
                _ => false,
            };

            if (targetsAlias)
            {
                instruction.MakeNop();
            }
        }

        // Drop them from the shape too, so the matching stage cannot re-claim one.
        foreach (uint alias in aliases)
        {
            context.Shape.SetBindingById.Remove(alias);
            context.Shape.VariablePointerTypes.Remove(alias);
        }
    }
}
