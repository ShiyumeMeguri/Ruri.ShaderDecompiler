using Newtonsoft.Json;

namespace Ruri.ShaderTools;

// Mirrors Unity's StructParameter (type tree TypeName=StructParameter,
// the entry type of ConstantBufferParameter.m_StructParams).
//
// Unity wire fields: m_NameIndex, m_Index, m_ArraySize, m_StructSize,
// m_VectorMembers, m_MatrixMembers.
//
// `Index` is the byte offset of this struct within the parent CB (Unity wire
// name kept for compat — semantically a byte offset, not a bind slot).
public sealed class StructParameter
{
    public StructParameter() { }

    public StructParameter(string name, int index, int arraySize, int structSize, VectorParameter[] vectors, MatrixParameter[] matrices)
    {
        Name = name;
        NameIndex = -1;
        Index = index;
        ArraySize = arraySize;
        StructSize = structSize;
        VectorMembers = vectors;
        MatrixMembers = matrices;
    }

    public string Name { get; set; } = string.Empty;
    public int NameIndex { get; set; }
    public int Index { get; set; }
    public int ArraySize { get; set; }
    public int StructSize { get; set; }
    public VectorParameter[] VectorMembers { get; set; } = Array.Empty<VectorParameter>();
    public MatrixParameter[] MatrixMembers { get; set; } = Array.Empty<MatrixParameter>();

    [JsonIgnore]
    public NumericShaderParameter[] AllNumericMembers
    {
        get
        {
            NumericShaderParameter[] result = new NumericShaderParameter[MatrixMembers.Length + VectorMembers.Length];
            MatrixMembers.CopyTo(result, 0);
            VectorMembers.CopyTo(result, MatrixMembers.Length);
            return result;
        }
    }
}
