namespace Ruri.ShaderTools;

// ============================================================================
// Unity Shader-asset mirror types. One-to-one with Unity's type tree (TypeID 48,
// SerializedShader, SerializedSubShader, SerializedPass, SerializedProgram,
// SerializedSubProgram, etc.). The naming convention here is:
//
//   * Unity's `m_Xxx` C++ field names → C# `Xxx` (drop the `m_`).
//   * Unity's lower-camel-case fields (`tags`, `lighting`, `culling`, `val`,
//     `first`, `srcBlend`, `rtBlend0`, ...) → C# PascalCase (`Tags`, ..., `Val`,
//     `First`, `SrcBlend`, `RtBlend0`).
//
// Two intentional Ruri extensions (NOT in Unity's wire format), both flagged
// inline at their declarations:
//
//   1. `SerializedSubProgram` carries decompile-output fields (`Success`,
//      `SourceLanguage`, `SourceFileExtension`, `SourceCode`, `ErrorMessage`).
//      They're populated AFTER the decompiler runs and consumed by
//      `UnityShaderLabWriter`. Filtering them out at JSON-export time is the
//      caller's responsibility.
//   2. `SerializedProgramData` (parameters payload) carries Ruri-only fields
//      (DescriptorSetParameters / EntryPoint / DebugName / UsedMaterials).
//
// Everything else mirrors the type tree exactly. Reference: 6000.5.0b1.json
// (TypeTreeDumps), Shader entry.
// ============================================================================

public sealed class UnityShaderMetadata
{
    public uint ObjectHideFlags { get; set; }
    public UnityPPtr CorrespondingSourceObject { get; set; } = new();
    public UnityPPtr PrefabInstance { get; set; } = new();
    public UnityPPtr PrefabAsset { get; set; } = new();
    public string Name { get; set; } = string.Empty;
    public UnitySerializedShader ParsedForm { get; set; } = new();
    public List<uint> Platforms { get; set; } = new();
    public List<List<uint>> Offsets { get; set; } = new();
    public List<List<uint>> CompressedLengths { get; set; } = new();
    public List<List<uint>> DecompressedLengths { get; set; } = new();
    public byte[] CompressedBlob { get; set; } = [];
}

public sealed class UnityPPtr
{
    public int FileID { get; set; }
    public long PathID { get; set; }
}

public sealed class UnitySerializedShader
{
    public UnitySerializedProperties PropInfo { get; set; } = new();
    public List<UnitySerializedSubShader> SubShaders { get; set; } = new();
    public List<string> KeywordNames { get; set; } = new();
    public List<byte> KeywordFlags { get; set; } = new();
    public string Name { get; set; } = string.Empty;
    public string CustomEditorName { get; set; } = string.Empty;
    public string FallbackName { get; set; } = string.Empty;
    public List<UnitySerializedShaderDependency> Dependencies { get; set; } = new();
    public List<UnitySerializedCustomEditorForRenderPipeline> CustomEditorForRenderPipelines { get; set; } = new();
    public bool DisableNoSubshadersMessage { get; set; }
}

public sealed class UnitySerializedShaderDependency
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
}

public sealed class UnitySerializedCustomEditorForRenderPipeline
{
    public string CustomEditorName { get; set; } = string.Empty;
    public string RenderPipelineType { get; set; } = string.Empty;
}

public sealed class UnitySerializedProperties
{
    public List<UnitySerializedProperty> Props { get; set; } = new();
}

public sealed class UnitySerializedProperty
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> Attributes { get; set; } = new();
    public int Type { get; set; }
    public uint Flags { get; set; }
    public float[] DefValue { get; set; } = new float[4];
    public UnitySerializedTextureProperty DefTexture { get; set; } = new();
}

public sealed class UnitySerializedTextureProperty
{
    public string DefaultName { get; set; } = string.Empty;
    public int TexDim { get; set; }
}

public sealed class UnitySerializedSubShader
{
    public List<UnitySerializedPass> Passes { get; set; } = new();
    public UnitySerializedTagMap Tags { get; set; } = new();
    public int LOD { get; set; }
}

public sealed class UnitySerializedPass
{
    public List<UnitySerializedNameIndex> NameIndices { get; set; } = new();
    public int Type { get; set; }
    public UnitySerializedShaderState State { get; set; } = new();
    public uint ProgramMask { get; set; }
    public UnitySerializedProgram? ProgVertex { get; set; }
    public UnitySerializedProgram? ProgFragment { get; set; }
    public UnitySerializedProgram? ProgGeometry { get; set; }
    public UnitySerializedProgram? ProgHull { get; set; }
    public UnitySerializedProgram? ProgDomain { get; set; }
    public UnitySerializedProgram? ProgRayTracing { get; set; }
    public bool HasInstancingVariant { get; set; }
    public bool HasProceduralInstancingVariant { get; set; }
    public string UseName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string TextureName { get; set; } = string.Empty;
    public UnitySerializedTagMap Tags { get; set; } = new();

    public IEnumerable<(string Stage, UnitySerializedProgram Program)> EnumerateProgramSlots()
    {
        if (ProgVertex is not null) yield return ("Vertex", ProgVertex);
        if (ProgFragment is not null) yield return ("Fragment", ProgFragment);
        if (ProgGeometry is not null) yield return ("Geometry", ProgGeometry);
        if (ProgHull is not null) yield return ("Hull", ProgHull);
        if (ProgDomain is not null) yield return ("Domain", ProgDomain);
        if (ProgRayTracing is not null) yield return ("RayTracing", ProgRayTracing);
    }

    public UnitySerializedProgram? GetProgramSlot(string stage) => stage switch
    {
        "Vertex" => ProgVertex,
        "Fragment" => ProgFragment,
        "Geometry" => ProgGeometry,
        "Hull" => ProgHull,
        "Domain" => ProgDomain,
        "RayTracing" => ProgRayTracing,
        _ => null,
    };

    public void SetProgramSlot(string stage, UnitySerializedProgram program)
    {
        switch (stage)
        {
            case "Vertex": ProgVertex = program; break;
            case "Fragment": ProgFragment = program; break;
            case "Geometry": ProgGeometry = program; break;
            case "Hull": ProgHull = program; break;
            case "Domain": ProgDomain = program; break;
            case "RayTracing": ProgRayTracing = program; break;
            default: throw new ArgumentException($"Unknown shader stage: {stage}", nameof(stage));
        }
    }
}

// Mirrors Unity's SerializedProgram (the `progVertex` / `progFragment` /
// `progGeometry` / `progHull` / `progDomain` / `progRayTracing` payload of
// SerializedPass).
public sealed class UnitySerializedProgram
{
    public List<UnitySerializedSubProgram> SubPrograms { get; set; } = new();
    public List<List<UnitySerializedPlayerSubProgram>> PlayerSubPrograms { get; set; } = new();
    public List<List<uint>> ParameterBlobIndices { get; set; } = new();
    public SerializedProgramData CommonParameters { get; set; } = new();
}

// Mirrors Unity's SerializedSubProgram. The first six fields are Unity wire;
// the trailing six are Ruri decompile-output extensions populated after the
// decompiler runs.
public sealed class UnitySerializedSubProgram
{
    public uint BlobIndex { get; set; }
    public List<ushort> KeywordIndices { get; set; } = new();
    public sbyte ShaderHardwareTier { get; set; }
    public sbyte GpuProgramType { get; set; }
    public SerializedProgramData Parameters { get; set; } = new();
    public long ShaderRequirements { get; set; }

    // ----- Ruri decompile-output extensions (NOT in Unity wire format) -----
    public bool Success { get; set; }
    public string SourceLanguage { get; set; } = "hlsl";
    public string SourceFileExtension { get; set; } = ".hlsl";
    public string? SourceCode { get; set; }
    public string? ErrorMessage { get; set; }
    public uint? ParameterBlobIndex { get; set; }
}

// Mirrors Unity's SerializedPlayerSubProgram (the per-platform variant entries
// PlayerSubPrograms holds in 2-D form).
public sealed class UnitySerializedPlayerSubProgram
{
    public uint BlobIndex { get; set; }
    public List<ushort> KeywordIndices { get; set; } = new();
    public long ShaderRequirements { get; set; }
    public sbyte GpuProgramType { get; set; }
}

public sealed class UnitySerializedNameIndex
{
    public string First { get; set; } = string.Empty;
    public int Second { get; set; }
}

public sealed class UnitySerializedShaderState
{
    public string Name { get; set; } = string.Empty;
    public UnitySerializedShaderRTBlendState RtBlend0 { get; set; } = new();
    public UnitySerializedShaderRTBlendState RtBlend1 { get; set; } = new();
    public UnitySerializedShaderRTBlendState RtBlend2 { get; set; } = new();
    public UnitySerializedShaderRTBlendState RtBlend3 { get; set; } = new();
    public UnitySerializedShaderRTBlendState RtBlend4 { get; set; } = new();
    public UnitySerializedShaderRTBlendState RtBlend5 { get; set; } = new();
    public UnitySerializedShaderRTBlendState RtBlend6 { get; set; } = new();
    public UnitySerializedShaderRTBlendState RtBlend7 { get; set; } = new();
    public bool RtSeparateBlend { get; set; }
    public UnitySerializedShaderFloatValue ZClip { get; set; } = new();
    public UnitySerializedShaderFloatValue ZTest { get; set; } = new();
    public UnitySerializedShaderFloatValue ZWrite { get; set; } = new();
    public UnitySerializedShaderFloatValue Culling { get; set; } = new();
    public UnitySerializedShaderFloatValue Conservative { get; set; } = new();
    public UnitySerializedShaderFloatValue OffsetFactor { get; set; } = new();
    public UnitySerializedShaderFloatValue OffsetUnits { get; set; } = new();
    public UnitySerializedShaderFloatValue AlphaToMask { get; set; } = new();
    public UnitySerializedStencilOp StencilOp { get; set; } = new();
    public UnitySerializedStencilOp StencilOpFront { get; set; } = new();
    public UnitySerializedStencilOp StencilOpBack { get; set; } = new();
    public UnitySerializedShaderFloatValue StencilReadMask { get; set; } = new();
    public UnitySerializedShaderFloatValue StencilWriteMask { get; set; } = new();
    public UnitySerializedShaderFloatValue StencilRef { get; set; } = new();
    public UnitySerializedShaderFloatValue FogStart { get; set; } = new();
    public UnitySerializedShaderFloatValue FogEnd { get; set; } = new();
    public UnitySerializedShaderFloatValue FogDensity { get; set; } = new();
    public UnitySerializedShaderVectorValue FogColor { get; set; } = new();
    public int FogMode { get; set; }
    public int GpuProgramID { get; set; }
    public UnitySerializedTagMap Tags { get; set; } = new();
    public int LOD { get; set; }
    public bool Lighting { get; set; }
}

public sealed class UnitySerializedShaderRTBlendState
{
    public UnitySerializedShaderFloatValue SrcBlend { get; set; } = new();
    public UnitySerializedShaderFloatValue DestBlend { get; set; } = new();
    public UnitySerializedShaderFloatValue SrcBlendAlpha { get; set; } = new();
    public UnitySerializedShaderFloatValue DestBlendAlpha { get; set; } = new();
    public UnitySerializedShaderFloatValue BlendOp { get; set; } = new();
    public UnitySerializedShaderFloatValue BlendOpAlpha { get; set; } = new();
    public UnitySerializedShaderFloatValue ColMask { get; set; } = new();
}

public sealed class UnitySerializedStencilOp
{
    public UnitySerializedShaderFloatValue Pass { get; set; } = new();
    public UnitySerializedShaderFloatValue Fail { get; set; } = new();
    public UnitySerializedShaderFloatValue ZFail { get; set; } = new();
    public UnitySerializedShaderFloatValue Comp { get; set; } = new();
}

public sealed class UnitySerializedShaderVectorValue
{
    public UnitySerializedShaderFloatValue X { get; set; } = new();
    public UnitySerializedShaderFloatValue Y { get; set; } = new();
    public UnitySerializedShaderFloatValue Z { get; set; } = new();
    public UnitySerializedShaderFloatValue W { get; set; } = new();
}

public sealed class UnitySerializedShaderFloatValue
{
    public float Val { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class UnitySerializedTagMap
{
    public List<UnityTagMapEntry> Tags { get; set; } = new();
}

public sealed class UnityTagMapEntry
{
    public string First { get; set; } = string.Empty;
    public string Second { get; set; } = string.Empty;
}
