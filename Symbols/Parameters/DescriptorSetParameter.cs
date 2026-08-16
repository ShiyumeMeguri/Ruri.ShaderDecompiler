namespace Ruri.ShaderTools;

// Mirrors Unity SerializedProgramParameters.DescriptorSetParameter. One entry
// per descriptor set; Bindings holds one SetBinding per (bindingIndex,
// descriptorType) tuple inside that set.
//
// This type is the *only* place SetId lives now. Per-resource records
// (BufferBinding / TextureParameter / SamplerParameter / UAVParameter) carry
// the binding slot but no longer carry the set id — callers read it back via
// SerializedProgramData.GetSetIdFor(bindingIndex, kind).
public sealed class DescriptorSetParameter
{
    public DescriptorSetParameter() { }

    public DescriptorSetParameter(string name, int setId)
    {
        Name = name;
        NameIndex = -1;
        SetId = setId;
    }

    public string Name { get; set; } = string.Empty;
    public int NameIndex { get; set; }
    public int SetId { get; set; }
    public int MaxBindingIndex { get; set; }
    public List<SetBinding> Bindings { get; set; } = new();
}
