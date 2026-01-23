using System.Collections.Generic;

namespace Ruri.ShaderDecompiler.Intermediate
{
    public enum ShaderArchitecture
    {
        Unknown,
        Dxbc,
        Dxil,
        SpirV
    }

    public class ResourceBinding
    {
        public string Name { get; set; }
        public int Slot { get; set; }
        public int Space { get; set; }
        public ResourceType Type { get; set; }
    }

    public enum ResourceType
    {
        UniformBuffer,
        Texture,
        Sampler,
        UAV,
        StructuredBuffer,
        RWTexture,
        RWBuffer
    }

    public class ShaderBundle
    {
        public byte[] NativeCode { get; set; }
        public ShaderArchitecture Architecture { get; set; }
        
        // Unified symbol metadata
        public ShaderSymbolMetadata Symbols { get; set; } = new();
        
        // Raw engine metadata for reference
        public object? EngineMetadata { get; set; } 
    }
}
