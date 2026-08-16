namespace Ruri.ShaderTools;

// The integer stored in SetBinding.DescriptorType. Mirrors Unity's wire-level
// enum for m_DescriptorType (Vulkan-flavoured). Lookups inside the C# layer go
// through the SerializedProgramData helpers — callers shouldn't depend on the
// numeric value directly. The enum is kept compact (only the kinds the
// shader-symbol layer actually classifies) rather than mirroring every Vulkan
// VK_DESCRIPTOR_TYPE_* constant; values that aren't classified land as Unknown.
public enum DescriptorBindingType
{
    Unknown = 0,
    Sampler = 1,
    SampledImage = 2,
    StorageImage = 3,
    UniformBuffer = 4,
    StorageBuffer = 5,
}
