using Newtonsoft.Json;

namespace Ruri.ShaderTools;

// Mirrors Unity's ConstantBufferParameter (type tree TypeName=ConstantBufferParameter,
// the entry type of m_ConstantBuffers).
//
// Unity wire fields: m_NameIndex, m_MatrixParams, m_VectorParams, m_StructParams,
// m_Size, m_IsPartialCB.
//
// Two storage buckets for numeric members:
//   MatrixParameters  matrices (IsMatrix=true)
//   VectorParameters  scalars + vectors (IsMatrix=false, RowCount=1..4)
// AllNumericParameters is a computed getter (Matrix + Vector copy), not a third
// storage. [JsonIgnore] is our local extension so the array doesn't double up
// in metadata.json — the wire format is just MatrixParameters + VectorParameters.
public sealed class ConstantBufferParameter
{
    public ConstantBufferParameter() { }

    public ConstantBufferParameter(string name, MatrixParameter[] matrices, VectorParameter[] vectors, StructParameter[] structs, int usedSize)
    {
        Name = name;
        NameIndex = -1;
        MatrixParameters = matrices;
        VectorParameters = vectors;
        StructParameters = structs;
        Size = usedSize;
        IsPartialCB = false;
    }

    public string Name { get; set; } = string.Empty;
    public int NameIndex { get; set; }
    public MatrixParameter[] MatrixParameters { get; set; } = Array.Empty<MatrixParameter>();
    public VectorParameter[] VectorParameters { get; set; } = Array.Empty<VectorParameter>();
    public StructParameter[] StructParameters { get; set; } = Array.Empty<StructParameter>();
    public int Size { get; set; }
    public bool IsPartialCB { get; set; }

    [JsonIgnore]
    public NumericShaderParameter[] AllNumericParameters
    {
        get
        {
            NumericShaderParameter[] result = new NumericShaderParameter[MatrixParameters.Length + VectorParameters.Length];
            MatrixParameters.CopyTo(result, 0);
            VectorParameters.CopyTo(result, MatrixParameters.Length);
            return result;
        }
    }
}
