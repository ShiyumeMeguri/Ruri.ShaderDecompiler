using System.Runtime.InteropServices;
using Ruri.ShaderTools.Spirv;

namespace Ruri.ShaderTools.Pipeline.Backend;

/// <summary>
/// Which source language a module is emitted in. Decided once, from what the
/// module declares, before any emission is attempted — never by trying HLSL and
/// catching the refusal.
///
/// HLSL is the product. The GLSL backend is used only for what spirv-cross's
/// HLSL backend has no spelling for, and a module that declares any of these
/// cannot be HLSL at all:
///
///   * a ray-tracing, task or mesh execution model — their built-ins have no
///     HLSL representation in the backend;
///   * a tessellation control or evaluation (hull or domain) execution model. The
///     backend this build links writes no hull or domain entry at all — no
///     <c>SV_OutputControlPointID</c>, <c>SV_DomainLocation</c>, <c>SV_TessFactor</c> or
///     <c>patchconstantfunc</c> anywhere in it — and refuses their built-ins with
///     "Unsupported builtin in HLSL: 8" (InvocationId) and ": 13" (TessCoord). Every
///     tessellated material of a shipped title failed that way. A geometry stage is
///     written in HLSL (<c>maxvertexcount</c>, stream <c>Append</c>/<c>RestartStrip</c>)
///     and stays there;
///   * inline ray tracing (ray query), a capability usable from any stage, which
///     fails deep inside constant emission with a message that blames constants;
///   * a physical addressing model (buffer device address), which the backend
///     refuses outright. By far the most common case: every ray-traced
///     reflection variant in a shipping build is compiled this way.
///
/// The scan reads only the header section, which by SPIR-V's layout rules holds
/// every OpCapability, OpExtension and the OpMemoryModel before the first entry
/// point, so it stops as soon as an OpEntryPoint proves the section is over. It
/// walks raw words rather than parsing the module: this runs for every variant
/// while the answer lives in the first handful of instructions.
/// </summary>
internal static class EmitLanguageSelector
{
    // Ray query has three spellings in the wild: the provisional capability that
    // shipped first, the final KHR one, and the extension string. Real modules
    // carry different combinations depending on which compiler produced them.
    private const uint RayQueryProvisionalKhr = 4472;
    private const uint RayQueryKhr = 4479;
    private const string RayQueryExtension = "SPV_KHR_ray_query";

    private const uint AddressingModelLogical = 0;

    public static EmitLanguage Select(byte[] spirv, EntryPointSelection entry)
        => StageNeedsGlsl(entry.Stage) || DeclaresGlslOnlyFeature(spirv) ? EmitLanguage.Glsl : EmitLanguage.Hlsl;

    private static bool StageNeedsGlsl(PipelineStage stage) => stage is
        PipelineStage.TessControl
        or PipelineStage.TessEvaluation
        or PipelineStage.RayGeneration
        or PipelineStage.Intersection
        or PipelineStage.AnyHit
        or PipelineStage.ClosestHit
        or PipelineStage.Miss
        or PipelineStage.Callable
        or PipelineStage.Task
        or PipelineStage.Mesh;

    private static bool DeclaresGlslOnlyFeature(byte[] spirv)
    {
        if (spirv.Length < SpirvModule.HeaderWordCount * sizeof(uint))
        {
            return false;
        }

        ReadOnlySpan<uint> words = MemoryMarshal.Cast<byte, uint>(spirv.AsSpan());
        if (words[0] != SpirvModule.MagicNumber)
        {
            return false;
        }

        int offset = SpirvModule.HeaderWordCount;
        while (offset < words.Length)
        {
            ushort opCode = SpvOpCode.GetOpCode(words[offset]);
            ushort wordCount = SpvOpCode.GetWordCount(words[offset]);

            if (wordCount == 0 || offset + wordCount > words.Length)
            {
                return false;
            }

            ReadOnlySpan<uint> operands = words.Slice(offset, wordCount);

            switch (opCode)
            {
                case SpvOpCode.OpCapability when wordCount >= 2:
                    if (operands[1] is RayQueryProvisionalKhr or RayQueryKhr)
                    {
                        return true;
                    }
                    break;

                case SpvOpCode.OpExtension when wordCount >= 2:
                    if (SpirvLiteral.ReadString(operands, 1) == RayQueryExtension)
                    {
                        return true;
                    }
                    break;

                case SpvOpCode.OpMemoryModel when wordCount >= 2:
                    if (operands[1] != AddressingModelLogical)
                    {
                        return true;
                    }
                    break;

                case SpvOpCode.OpEntryPoint:
                    return false;
            }

            offset += wordCount;
        }

        return false;
    }
}
