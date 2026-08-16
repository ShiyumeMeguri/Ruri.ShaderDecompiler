using System.Runtime.InteropServices;
using Ruri.ShaderTools.Spirv;

namespace Ruri.ShaderTools.Pipeline.Backend;

/// <summary>
/// Features a module declares that decide, on their own, that HLSL emission
/// cannot work — regardless of which stage the entry point runs at.
///
/// This is the companion to <see cref="ShaderStageClassifier.RequiresGlsl"/>, and
/// it exists because that test asks the wrong question for one real case. Stage
/// tells you a RAY-TRACING shader cannot be HLSL. It tells you nothing about a
/// FRAGMENT shader doing inline ray tracing — ray query is a capability, usable
/// from any stage, and such a module reaches the HLSL backend looking perfectly
/// ordinary. It then fails deep inside constant emission with "Invalid constant
/// expression basetype", a message that points at constants when the actual cause
/// is a ray-query type the backend has no spelling for. That reads like a defect
/// in this pipeline; it is not one, and a whole investigation is the cost of
/// finding that out.
///
/// Detecting it up front turns a misleading error into a stated decision. The
/// output does not change — GLSL either way — but the log now says what happened
/// and why, and a doomed emission attempt is skipped.
///
/// The addressing model is here for the same reason, and it is by far the more
/// common case: a module built for buffer-device-address cannot be HLSL at all,
/// and this archive contains 1131 such variants. Each one was making an emission
/// attempt whose only possible outcome was the same refusal.
///
/// The scan reads only the header section, which by SPIR-V's layout rules holds
/// every OpCapability, OpExtension and the OpMemoryModel before the first entry
/// point, so it stops as soon as an OpEntryPoint proves the section is over.
///
/// A REASON rather than a bool: the caller states the cause in the log, and
/// "which of these applied" is exactly what someone reading it needs.
/// </summary>
internal static class ModuleFeatureScan
{
    // Ray query has three spellings in the wild: the provisional capability that
    // shipped first, the final KHR one, and the SPV_KHR_ray_query extension
    // string. Real modules carry different combinations depending on which
    // compiler produced them, so all three are checked — matching only the final
    // capability would miss the provisional modules this archive actually
    // contains.
    private const uint RayQueryProvisionalKhr = 4472;
    private const uint RayQueryKhr = 4479;
    private const string RayQueryExtension = "SPV_KHR_ray_query";

    /// <summary>Addressing model operand of <c>OpMemoryModel</c>. Anything other
    /// than Logical means pointers are real addresses, which HLSL has no notion
    /// of — the backend refuses such a module outright.</summary>
    private const uint AddressingModelLogical = 0;

    /// <summary>Why HLSL emission cannot work for this module, or null when
    /// nothing rules it out.</summary>
    /// <summary>
    /// Walks raw words instead of parsing the module, deliberately. This runs for
    /// EVERY variant — tens of thousands of them — while the answer lives in the
    /// first handful of instructions, so building an instruction table and then
    /// reading three of its entries would cost more than the emission this test
    /// exists to avoid.
    /// </summary>
    public static string? GlslOnlyReason(byte[] spirv)
    {
        if (spirv.Length < SpirvModule.HeaderWordCount * sizeof(uint))
        {
            return null;
        }

        ReadOnlySpan<uint> words = MemoryMarshal.Cast<byte, uint>(spirv.AsSpan());
        if (words[0] != SpirvModule.MagicNumber)
        {
            return null;
        }

        int offset = SpirvModule.HeaderWordCount;
        while (offset < words.Length)
        {
            ushort opCode = SpvOpCode.GetOpCode(words[offset]);
            ushort wordCount = SpvOpCode.GetWordCount(words[offset]);

            // A zero length would not advance; a run past the end means the blob
            // is truncated. Either way there is nothing trustworthy left to read.
            if (wordCount == 0 || offset + wordCount > words.Length)
            {
                return null;
            }

            ReadOnlySpan<uint> operands = words.Slice(offset, wordCount);

            switch (opCode)
            {
                case SpvOpCode.OpCapability when wordCount >= 2:
                    if (operands[1] is RayQueryProvisionalKhr or RayQueryKhr)
                    {
                        return "it uses inline ray tracing (ray query)";
                    }
                    break;

                case SpvOpCode.OpExtension when wordCount >= 2:
                    if (SpirvLiteral.ReadString(operands, 1) == RayQueryExtension)
                    {
                        return "it uses inline ray tracing (ray query)";
                    }
                    break;

                case SpvOpCode.OpMemoryModel when wordCount >= 2:
                    if (operands[1] != AddressingModelLogical)
                    {
                        return "it uses a physical addressing model (buffer device address)";
                    }
                    break;

                // Everything above is required to precede the entry points, so
                // reaching one means there is nothing left to find.
                case SpvOpCode.OpEntryPoint:
                    return null;
            }

            offset += wordCount;
        }

        return null;
    }
}
