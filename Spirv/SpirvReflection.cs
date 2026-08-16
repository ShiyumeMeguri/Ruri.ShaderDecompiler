namespace Ruri.ShaderTools.Spirv;

/// <summary>
/// One texture binding combined with one sampler binding by an
/// <c>OpSampledImage</c>.
///
/// Both descriptor sets are reported because nothing forces the two variables
/// into the same set — a host that only cares about its material set filters on
/// them rather than assuming.
/// </summary>
public readonly record struct SampledImageBinding(
    int TextureSet,
    int TextureBinding,
    int SamplerSet,
    int SamplerBinding);

/// <summary>
/// The public door on the SPIR-V layer: structural questions a HOST asks about a
/// module it did not compile.
///
/// Everything else under <c>Spirv</c> is internal on purpose — it is the mutable
/// module model the pipeline's own transforms rewrite in place, and handing that
/// out invites callers to depend on rewrite invariants they cannot see. What a
/// host legitimately needs is the opposite: read-only facts, already resolved,
/// in plain value types.
///
/// The split that keeps working: spec-level knowledge (header shape, opcode
/// packing, decoration ids, operand chains) lives here; what the resulting names
/// MEAN stays with the host that knows its engine's conventions.
/// </summary>
public static class SpirvReflection
{
    /// <summary>
    /// Every distinct (texture, sampler) descriptor pairing an
    /// <c>OpSampledImage</c> forms in the module, ordered by
    /// <c>(TextureSet, TextureBinding, SamplerSet, SamplerBinding)</c>.
    ///
    /// This is how a reflection-stripped blob still gives up which texture goes
    /// with which sampler: the cook can drop the names, but it cannot drop the
    /// instruction that combines the two variables.
    ///
    /// Only pairings whose BOTH operands trace back to a variable carrying
    /// DescriptorSet AND Binding are reported — without a descriptor identity
    /// there is nothing for a caller to match its own symbol table against.
    ///
    /// Returns empty rather than throwing on a blob that is not SPIR-V: callers
    /// are bulk-scanning shipped bytecode, where one bad entry must skip, not
    /// unwind the batch.
    /// </summary>
    public static IReadOnlyList<SampledImageBinding> ScanSampledImageBindings(byte[] spirv)
    {
        if (spirv == null || spirv.Length < SpirvModule.HeaderWordCount * 4)
        {
            return Array.Empty<SampledImageBinding>();
        }
        if (BitConverter.ToUInt32(spirv, 0) != SpirvModule.MagicNumber)
        {
            return Array.Empty<SampledImageBinding>();
        }

        SpirvModule module = SpirvModule.Parse(spirv);

        // OpSampledImage names the two LOADS, not the two variables, so the
        // result ids have to be walked back through OpLoad before the
        // decorations mean anything.
        Dictionary<uint, uint> pointerByLoad = new();
        Dictionary<uint, (int? Set, int? Binding)> setBindingById = new();
        List<(uint ImageLoadId, uint SamplerLoadId)> combines = new();

        List<SpirvInstruction> instructions = module.Instructions;
        for (int i = 0; i < instructions.Count; i++)
        {
            SpirvInstruction instruction = instructions[i];
            Span<uint> words = instruction.Words;

            switch (instruction.OpCode)
            {
                case SpvOpCode.OpDecorate when words.Length >= 4:
                {
                    uint targetId = words[1];
                    uint decoration = words[2];
                    (int? Set, int? Binding) existing = setBindingById.TryGetValue(targetId, out var value) ? value : (null, null);

                    if (decoration == Decoration.DescriptorSet)
                    {
                        setBindingById[targetId] = ((int)words[3], existing.Binding);
                    }
                    else if (decoration == Decoration.Binding)
                    {
                        setBindingById[targetId] = (existing.Set, (int)words[3]);
                    }
                    break;
                }

                case SpvOpCode.OpLoad when words.Length >= 4:
                    pointerByLoad[words[2]] = words[3];
                    break;

                case SpvOpCode.OpSampledImage when words.Length >= 5:
                    combines.Add((words[3], words[4]));
                    break;
            }
        }

        if (combines.Count == 0)
        {
            return Array.Empty<SampledImageBinding>();
        }

        // A sampler is typically combined with the same texture at every sample
        // site, so the raw list repeats heavily; the caller wants the relation,
        // not the call count.
        HashSet<SampledImageBinding> distinct = new();
        foreach ((uint imageLoadId, uint samplerLoadId) in combines)
        {
            if (!pointerByLoad.TryGetValue(imageLoadId, out uint imageVarId)
                || !pointerByLoad.TryGetValue(samplerLoadId, out uint samplerVarId))
            {
                continue;
            }
            if (!setBindingById.TryGetValue(imageVarId, out (int? Set, int? Binding) image)
                || !setBindingById.TryGetValue(samplerVarId, out (int? Set, int? Binding) sampler))
            {
                continue;
            }
            if (image.Set is not int imageSet || image.Binding is not int imageBinding
                || sampler.Set is not int samplerSet || sampler.Binding is not int samplerBinding)
            {
                continue;
            }

            distinct.Add(new SampledImageBinding(imageSet, imageBinding, samplerSet, samplerBinding));
        }

        // Sorted, because a caller resolving one-to-one pairings has to break
        // ties by iteration order, and dictionary order would make which name
        // wins depend on the SPIR-V revision that emitted the module.
        List<SampledImageBinding> pairings = new(distinct);
        pairings.Sort(static (left, right) =>
        {
            int byTextureSet = left.TextureSet.CompareTo(right.TextureSet);
            if (byTextureSet != 0) return byTextureSet;

            int byTextureBinding = left.TextureBinding.CompareTo(right.TextureBinding);
            if (byTextureBinding != 0) return byTextureBinding;

            int bySamplerSet = left.SamplerSet.CompareTo(right.SamplerSet);
            return bySamplerSet != 0 ? bySamplerSet : left.SamplerBinding.CompareTo(right.SamplerBinding);
        });

        return pairings;
    }
}
