namespace Ruri.ShaderTools;

// The integer stored in SetBinding.DescriptorType: the engine writes
// m_DescriptorType as the VkDescriptorType of the slot, so the values are
// Vulkan's own. Unknown is outside Vulkan's range and only ever asks a lookup
// not to filter by type.
public enum DescriptorBindingType
{
    Unknown = -1,
    Sampler = 0,
    CombinedImageSampler = 1,
    SampledImage = 2,
    StorageImage = 3,
    UniformTexelBuffer = 4,
    StorageTexelBuffer = 5,
    UniformBuffer = 6,
    StorageBuffer = 7,
    UniformBufferDynamic = 8,
    StorageBufferDynamic = 9,
    InputAttachment = 10,
    AccelerationStructure = 1000150000,
}
