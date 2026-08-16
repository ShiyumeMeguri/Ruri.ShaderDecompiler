namespace Ruri.ShaderTools.Pipeline.Backend;

/// <summary>Pipeline stage a SPIR-V entry point runs at.</summary>
internal enum ShaderStage
{
    Unknown = 0,
    Vertex,
    TessControl,
    TessEvaluation,
    Geometry,
    Fragment,
    Compute,
    RayGeneration,
    Intersection,
    AnyHit,
    ClosestHit,
    Miss,
    Callable,
    Task,
    Mesh,
}

internal static class ShaderStageClassifier
{
    /// <summary>
    /// Map a raw SPIR-V execution model to a stage.
    ///
    /// The ray-tracing models appear TWICE in the numbering: 5267..5272 are the
    /// KHR values and 5313..5318 the NV-flavoured aliases with identical
    /// semantics. Real-world modules carry the NV numbers while disassemblers
    /// print the KHR friendly names, so the discrepancy is invisible unless you
    /// read raw words — and handling only one set silently misroutes every
    /// ray-tracing shader.
    /// </summary>
    public static ShaderStage FromExecutionModel(uint model) => model switch
    {
        0 => ShaderStage.Vertex,
        1 => ShaderStage.TessControl,
        2 => ShaderStage.TessEvaluation,
        3 => ShaderStage.Geometry,
        4 => ShaderStage.Fragment,
        5 => ShaderStage.Compute,
        5267 or 5313 => ShaderStage.RayGeneration,
        5268 or 5314 => ShaderStage.Intersection,
        5269 or 5315 => ShaderStage.AnyHit,
        5270 or 5316 => ShaderStage.ClosestHit,
        5271 or 5317 => ShaderStage.Miss,
        5272 or 5318 => ShaderStage.Callable,
        5364 => ShaderStage.Task,
        5365 => ShaderStage.Mesh,
        _ => ShaderStage.Unknown,
    };

    /// <summary>
    /// Stages whose built-ins an HLSL backend cannot represent at all, so the
    /// HLSL attempt is skipped rather than made and discarded.
    /// </summary>
    public static bool RequiresGlsl(ShaderStage stage) => stage is
        ShaderStage.RayGeneration
        or ShaderStage.Intersection
        or ShaderStage.AnyHit
        or ShaderStage.ClosestHit
        or ShaderStage.Miss
        or ShaderStage.Callable
        or ShaderStage.Task
        or ShaderStage.Mesh;
}
