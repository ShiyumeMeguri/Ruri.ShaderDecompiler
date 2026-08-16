using Ruri.ShaderTools.Pipeline.Native;

namespace Ruri.ShaderTools.Pipeline.Backend;

/// <summary>Emitted high-level source plus the language it came out as.</summary>
internal readonly record struct EmittedSource(string Text, string Language, string FileExtension)
{
    public static EmittedSource Hlsl(string text) => new(text, "hlsl", ".hlsl");
    public static EmittedSource Glsl(string text) => new(text, "glsl", ".glsl");
}

/// <summary>
/// SPIR-V → high-level source, and the HLSL-then-GLSL fallback policy.
///
/// The policy exists because the HLSL backend has real, documented gaps that the
/// GLSL backend does not:
///   * tessellation / geometry built-ins (invocation id, tess levels, tess coord,
///     patch-constant emission)
///   * ray-tracing and mesh/task built-ins
///   * block members whose array stride is under 16 bytes — bindless index tables
///     compile to <c>uint _m0[N]</c> at stride 4, which HLSL packing cannot express
///   * 8- and 16-bit storage that is legal SPIR-V but not legal shader-model-5 HLSL
///   * inline ray tracing (ray query), which is a CAPABILITY rather than a stage
///     and so turns up in an otherwise ordinary fragment shader
///
/// The HLSL attempt is skipped outright when it cannot succeed, which takes two
/// tests, not one: the stage (ray-tracing, mesh/task) and the module's declared
/// features (ray query). For everything else HLSL is tried first and GLSL catches
/// whatever it rejects. The chosen language travels back with the source so the
/// caller writes the right file extension.
///
/// A failed HLSL attempt is deliberately quiet at the call site but its upstream
/// message is surfaced, because "why did this shader come out as GLSL" is
/// unanswerable without it.
/// </summary>
internal sealed class SourceEmitter
{
    /// <summary>
    /// Lowest GLSL version that accepts every feature the fallback might need to
    /// emit — ray-tracing built-ins, mesh shading, descriptor indexing, shader
    /// clock. Vulkan semantics are always on; the input is Vulkan-flavoured.
    /// </summary>
    private const uint VulkanGlslVersion = 460;

    /// <summary>Upstream message from the most recent failed emit attempt.</summary>
    public string? LastFailure { get; private set; }

    public EmittedSource Emit(byte[] spirv, string? preferredEntryPoint, uint shaderModel)
    {
        EntryPointSelection entry = EntryPointResolver.Resolve(spirv, preferredEntryPoint);

        string? glslOnly = ShaderStageClassifier.RequiresGlsl(entry.Stage)
            ? "the HLSL backend cannot represent raytracing/mesh builtins"
            : ModuleFeatureScan.GlslOnlyReason(spirv);

        if (glslOnly is not null)
        {
            Console.Error.WriteLine($"[source-emit] {entry.Stage}: emitting GLSL because {glslOnly}.");
        }
        else
        {
            if (TryEmitHlsl(spirv, entry, shaderModel, out string? hlsl))
            {
                return EmittedSource.Hlsl(hlsl!);
            }

            // The attempt above was silent so a successful fallback stays quiet in
            // the log. Now that it failed, surface the real reason — otherwise the
            // only evidence is a .glsl file where a .hlsl was expected.
            if (!string.IsNullOrEmpty(LastFailure))
            {
                Console.Error.WriteLine($"[source-emit] HLSL emission failed for {entry.Stage} — falling back to GLSL. Underlying cause:");
                Console.Error.WriteLine(LastFailure);
            }
        }

        if (TryEmitGlsl(spirv, entry, out string? glsl))
        {
            return EmittedSource.Glsl(glsl!);
        }

        throw new InvalidOperationException("Failed to decompile patched SPIR-V.");
    }

    private bool TryEmitHlsl(byte[] spirv, EntryPointSelection entry, uint shaderModel, out string? source)
    {
        source = SpirvCrossLibrary.EmitHlsl(spirv, entry.Name, entry.ExecutionModel, shaderModel, out string? error);
        if (source is not null)
        {
            return true;
        }

        LastFailure = $"spirv-cross HLSL emission failed: {error}";
        return false;
    }

    private bool TryEmitGlsl(byte[] spirv, EntryPointSelection entry, out string? source)
    {
        source = SpirvCrossLibrary.EmitGlsl(spirv, entry.Name, entry.ExecutionModel, VulkanGlslVersion, out string? error);
        if (source is not null)
        {
            return true;
        }

        LastFailure = $"spirv-cross GLSL emission failed: {error}";
        Console.Error.WriteLine(LastFailure);
        return false;
    }
}
