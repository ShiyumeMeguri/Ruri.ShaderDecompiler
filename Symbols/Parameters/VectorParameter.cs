namespace Ruri.ShaderTools;

public sealed class VectorParameter : NumericShaderParameter
{
    public VectorParameter() { }

    public VectorParameter(string name, ShaderParamType type, int index, int columns)
    {
        Name = name;
        NameIndex = -1;
        Index = index;
        ArraySize = 0;
        Type = type;
        Dim = unchecked((byte)columns);
        ColumnCount = 1;
        IsMatrix = false;
    }

    public VectorParameter(string name, ShaderParamType type, int index, int arraySize, int columns) : this(name, type, index, columns)
    {
        ArraySize = arraySize;
    }

    public byte Dim
    {
        get => RowCount;
        set => RowCount = value;
    }
}
