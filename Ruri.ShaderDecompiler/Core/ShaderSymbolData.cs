namespace Ruri.ShaderDecompiler;

/// <summary>
/// Represents the type of a shader resource binding.
/// Aligned with UE's EUniformBufferBaseType and EShaderCodeResourceBindingType.
/// </summary>
public enum ShaderResourceType
{
    Unknown = 0,
    
    // Basic types (matching UE's UBMT_*)
    Texture,                // UBMT_TEXTURE
    SampledImage,           // UBMT_TEXTURE (SPIR-V combined image+sampler)
    SRV,                    // UBMT_SRV (ShaderResourceView)
    UAV,                    // UBMT_UAV (UnorderedAccessView)
    Sampler,                // UBMT_SAMPLER
    SamplerComparison,      // UBMT_SAMPLER (comparison variant)
    ConstantBuffer,         // Uniform buffer / cbuffer
    
    // Buffer subtypes (matching UE's EShaderCodeResourceBindingType)
    Buffer,                 // Buffer<T>
    StructuredBuffer,       // StructuredBuffer<T>
    ByteAddressBuffer,      // ByteAddressBuffer
    RWBuffer,               // RWBuffer<T>
    RWStructuredBuffer,     // RWStructuredBuffer<T>
    RWByteAddressBuffer,    // RWByteAddressBuffer
    
    // Texture subtypes
    Texture2D,              // Texture2D<T>
    Texture2DArray,         // Texture2DArray<T>
    Texture3D,              // Texture3D<T>
    TextureCube,            // TextureCube<T>
    TextureCubeArray,       // TextureCubeArray<T>
    Texture2DMS,            // Texture2DMS<T>
    
    // RW Texture subtypes
    RWTexture2D,            // RWTexture2D<T>
    RWTexture2DArray,       // RWTexture2DArray<T>
    RWTexture3D,            // RWTexture3D<T>
    
    // Special types
    RaytracingAccelerationStructure,
    StorageImage,           // SPIR-V storage image
    StorageBuffer,          // SPIR-V storage buffer
    InputAttachment,        // Vulkan input attachment
}

/// <summary>
/// Represents a single resource binding with its name and location info.
/// </summary>
public class ResourceBinding
{
    /// <summary>
    /// The name of the resource (e.g., "DiffuseTexture", "SceneParams").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The binding point (register number in HLSL terms, binding in Vulkan).
    /// </summary>
    public int Binding { get; set; }

    /// <summary>
    /// The descriptor set (Vulkan). Default 0 for DX12/HLSL.
    /// </summary>
    public int Set { get; set; } = 0;

    /// <summary>
    /// The type of resource.
    /// </summary>
    public ShaderResourceType Type { get; set; } = ShaderResourceType.Unknown;

    /// <summary>
    /// For ConstantBuffer: member names and their offsets.
    /// </summary>
    public List<StructMember>? Members { get; set; }

    /// <summary>
    /// Optional tag for storing extra info (e.g., SPIR-V ID).
    /// </summary>
    public int Tag { get; set; }
    
    /// <summary>
    /// HLSL register type: 'b' for cbuffer, 't' for SRV, 'u' for UAV, 's' for sampler.
    /// </summary>
    public char RegisterType { get; set; }
}

/// <summary>
/// A member of a struct/cbuffer.
/// </summary>
public class StructMember
{
    public string Name { get; set; } = string.Empty;
    public int Index { get; set; }
    public int ByteOffset { get; set; }
    public int ByteSize { get; set; }
    public string TypeName { get; set; } = string.Empty;
}

/// <summary>
/// Contains all symbol data for a shader, provided by the external engine (Unity/UE).
/// This is the "Unified Receiver" class.
/// </summary>
public class ShaderSymbolData
{
    /// <summary>
    /// List of resource bindings (textures, CBs, UAVs, etc.).
    /// </summary>
    public List<ResourceBinding> Resources { get; set; } = new();

    /// <summary>
    /// Entry point name (e.g., "main", "PSMain").
    /// </summary>
    public string EntryPoint { get; set; } = "main";

    /// <summary>
    /// Shader stage (e.g., Vertex, Pixel, Compute).
    /// </summary>
    public ShaderStage Stage { get; set; } = ShaderStage.Unknown;
    
    /// <summary>
    /// Original shader name/path if available.
    /// </summary>
    public string? DebugName { get; set; }
}

/// <summary>
/// Shader pipeline stage.
/// </summary>
public enum ShaderStage
{
    Unknown = 0,
    Vertex,
    Pixel,      // Fragment
    Compute,
    Geometry,
    TessellationControl,    // Hull
    TessellationEvaluation, // Domain
    RayGeneration,
    RayClosestHit,
    RayMiss,
    RayAnyHit,
    RayIntersection,
    Callable,
    Task,       // Amplification
    Mesh,
}
