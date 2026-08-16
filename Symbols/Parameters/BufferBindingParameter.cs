namespace Ruri.ShaderTools;

// Mirrors Unity's BufferBindingParameter (type tree TypeName=BufferBindingParameter).
// Used for both m_BufferParams (SRV/UAV-style buffer slots) and m_ConstantBufferBindings
// (the per-CB bind-slot record next to m_ConstantBuffers).
//
// Unity wire fields: m_NameIndex, m_Index, m_ArraySize.
// `Name` is a Ruri extension carrying the resolved string from the pass NameIndices map.
public sealed record class BufferBindingParameter
{
    public BufferBindingParameter() { }

    public BufferBindingParameter(string name, int index)
    {
        Name = name;
        NameIndex = -1;
        Index = index;
        ArraySize = 0;
    }

    public string Name { get; set; } = string.Empty;
    public int NameIndex { get; set; }
    public int Index { get; set; }
    public int ArraySize { get; set; }
}
