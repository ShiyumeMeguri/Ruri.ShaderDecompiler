namespace Ruri.ShaderTools;

public sealed class MatrixParameter : NumericShaderParameter
{
    public MatrixParameter() { }

    public MatrixParameter(string name, ShaderParamType type, int index, int rowCount, int columnCount)
    {
        Name = name;
        NameIndex = -1;
        Index = index;
        ArraySize = 0;
        Type = type;
        RowCount = unchecked((byte)rowCount);
        ColumnCount = unchecked((byte)columnCount);
        IsMatrix = true;
    }

    public MatrixParameter(string name, ShaderParamType type, int index, int arraySize, int rowCount, int columnCount) : this(name, type, index, rowCount, columnCount)
    {
        ArraySize = arraySize;
    }
}
