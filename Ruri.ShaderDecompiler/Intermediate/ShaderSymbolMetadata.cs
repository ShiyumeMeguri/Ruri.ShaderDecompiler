using System.Collections.Generic;

namespace Ruri.ShaderDecompiler.Intermediate
{
    /// <summary>
    /// Unified metadata for shader resources to be injected back into source.
    /// Acts as a bridge between engine-specific metadata (UE SRT, Unity Bindings) and SPIR-V/HLSL.
    /// </summary>
    public class ShaderSymbolMetadata
    {
        public List<ResourceSymbol> Resources { get; set; } = new();
        
        // Potential for future expansion like entry point name, etc.
        public string? EntryPoint { get; set; }
    }

    public class ResourceSymbol
    {
        public string Name { get; set; }
        
        // Logic mapping to SPIR-V decorations
        public int Set { get; set; }
        public int Binding { get; set; }
        
        public ResourceType Type { get; set; }
        
        // Optional: original engine slot
        public int? Slot { get; set; }
    }
}
