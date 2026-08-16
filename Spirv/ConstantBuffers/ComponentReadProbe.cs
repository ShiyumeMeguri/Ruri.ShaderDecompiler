using Ruri.ShaderTools.Spirv;
using Ruri.ShaderTools.Spirv.Analysis;

namespace Ruri.ShaderTools.Spirv.ConstantBuffers;

/// <summary>
/// Answers: "this access chain reads a whole register that matches no member —
/// but do its downstream component reads each land on one?"
///
/// The shape that triggers it: the chain loads a full vec4 register while the
/// symbols only name sub-components of it (a lone <c>_UseDitherClip</c> at
/// byte 68, i.e. register 4's <c>.y</c>, with nothing occupying the whole
/// register). The vector-level access has nowhere to go, but each downstream
/// <c>OpCompositeExtract</c> picks a specific component — and that component
/// usually IS a named member. So the chain stays as written and the extracts get
/// rewritten into direct member loads instead.
///
/// Two chain shapes are recognised:
///   OpLoad → OpCompositeExtract                 (<c>cb._m0[N].y</c>)
///   OpLoad → OpBitcast → OpCompositeExtract     (<c>asuint(cb._m0[N]).y</c>)
///
/// The bitcast hop matters more than it looks: it is how bool members stored as
/// uint are read, it preserves vector width and only changes the element type,
/// and without following it the load appears to have no component readers at all
/// — which sank whole constant buffers to an opaque <c>_m0[N]</c>.
///
/// Cost: driven entirely by <see cref="OperandUseIndex"/>. The scan this replaces
/// walked the instruction list twice-nested per candidate chain, so a buffer with
/// many unmatched registers turned into quadratic work over the whole module.
/// </summary>
internal static class ComponentReadProbe
{
    public static bool CanLowerAllReads(
        OperandUseIndex uses,
        uint accessChainResultId,
        BlockLayout layout,
        FlatAccessPath accessPath,
        ConstantValueMap constants)
    {
        bool foundLoad = false;

        foreach (SpirvInstruction load in uses.UsersOf(accessChainResultId))
        {
            if (load.OpCode != SpvOpCode.OpLoad || load.WordCount < 4 || load[3] != accessChainResultId)
            {
                continue;
            }

            foundLoad = true;
            uint loadResultId = load[2];

            bool hasComponentReaders = false;
            if (!ProbeReadersOf(uses, loadResultId, layout, accessPath, constants, ref hasComponentReaders))
            {
                return false;
            }

            // Follow every bitcast of this load and probe its readers too.
            foreach (SpirvInstruction bitcast in uses.UsersOf(loadResultId))
            {
                if (bitcast.OpCode != SpvOpCode.OpBitcast || bitcast.WordCount < 4 || bitcast[3] != loadResultId)
                {
                    continue;
                }

                if (!ProbeReadersOf(uses, bitcast[2], layout, accessPath, constants, ref hasComponentReaders))
                {
                    return false;
                }
            }

            // A load with no component readers cannot be lowered — leaving it
            // pointing at a rewritten variable would be an invalid module.
            if (!hasComponentReaders)
            {
                return false;
            }
        }

        return foundLoad;
    }

    private static bool ProbeReadersOf(
        OperandUseIndex uses,
        uint sourceId,
        BlockLayout layout,
        FlatAccessPath accessPath,
        ConstantValueMap constants,
        ref bool hasComponentReaders)
    {
        foreach (SpirvInstruction extract in uses.UsersOf(sourceId))
        {
            if (extract.OpCode != SpvOpCode.OpCompositeExtract || extract.WordCount < 5 || extract[3] != sourceId)
            {
                continue;
            }

            hasComponentReaders = true;

            FlatAccessPath directPath = accessPath.With(extract.Words[4..]);
            if (AccessTranslator.Translate(layout, directPath, constants) is null)
            {
                return false;
            }
        }

        return true;
    }
}
