namespace Ruri.ShaderTools;

// Mirrors Unity SerializedProgramParameters.DescriptorSetParameter.SetBinding.
// One entry per (set, bindingIndex, descriptorType) tuple — the Vulkan
// descriptor view of a resource. The corresponding D3D-style record lives in
// the matching BufferBinding / TextureParameter / SamplerParameter /
// UAVParameter list and is matched by BindingIndex + DescriptorType.
public sealed record class SetBinding
{
    public SetBinding() { }

    public SetBinding(string name, int bindingIndex, DescriptorBindingType descriptorType)
    {
        Name = name;
        NameIndex = -1;
        BindingIndex = bindingIndex;
        DescriptorType = (int)descriptorType;
    }

    public string Name { get; set; } = string.Empty;
    public int NameIndex { get; set; }
    public int BindingIndex { get; set; }
    public int DescriptorType { get; set; }
    public uint PackedBinding { get; set; }
    public uint PackedInfo { get; set; }
}
