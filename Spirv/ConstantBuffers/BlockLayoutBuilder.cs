using System.Linq;
using Ruri.ShaderTools.Spirv;

namespace Ruri.ShaderTools.Spirv.ConstantBuffers;

/// <summary>
/// Engine symbols → <see cref="BlockLayout"/>. Pure: knows nothing about SPIR-V,
/// produces no ids, touches no module.
///
/// Members the symbol table declares beyond the SPIR-V array's length are
/// dropped silently. That is intentional — metadata routinely describes fields a
/// given permutation never compiled in, and forcing them to fit would mean
/// either inventing padding (wrong) or abandoning the rewrite (worse: every
/// name in the buffer is lost to save a field the shader never reads).
/// </summary>
internal static class BlockLayoutBuilder
{
    public static BlockLayout? Build(FlatBlockView block)
    {
        ConstantBufferParameter symbol = block.Symbol;

        bool hasNumeric = symbol.VectorParameters.Length > 0 || symbol.MatrixParameters.Length > 0;
        if (!hasNumeric && symbol.StructParameters.Length == 0)
        {
            return null;
        }

        int byteCeiling = block.ArrayLength * 16;
        var members = new List<BlockMemberLayout>();

        foreach (NumericShaderParameter numeric in symbol.AllNumericParameters.OrderBy(static p => p.Index))
        {
            BlockMemberLayout? member = TryCreateNumericMember(numeric, byteCeiling);
            if (member != null)
            {
                members.Add(member);
            }
        }

        foreach (StructParameter structSymbol in symbol.StructParameters.OrderBy(static p => p.Index))
        {
            BlockMemberLayout? member = TryCreateStructMember(structSymbol, byteCeiling);
            if (member != null)
            {
                members.Add(member);
            }
        }

        members.Sort(static (left, right) => left.ByteOffset.CompareTo(right.ByteOffset));
        if (members.Count == 0)
        {
            return null;
        }

        LayoutGapFiller.FillInteriorGaps(members);

        var layout = new BlockLayout();
        int maxUsedByteOffset = 0;
        int maxReferencedByteOffset = 0;

        foreach (BlockMemberLayout member in members)
        {
            layout.Members.Add(member);
            maxUsedByteOffset = Math.Max(maxUsedByteOffset, member.ByteOffset + member.SpanBytes);
            maxReferencedByteOffset = Math.Max(maxReferencedByteOffset, ReferencedByteEnd(member));
        }

        layout.RequiredRegisterCount = Math.Max(1, (maxUsedByteOffset + 15) / 16);
        layout.MaxUsedRegisterCount = Math.Max(1, (maxReferencedByteOffset + 15) / 16);

        LayoutGapFiller.FillTail(layout, block.ArrayLength);
        return layout;
    }

    /// <summary>
    /// Registers a member starting at <paramref name="byteOffset"/> occupies.
    /// Matrices are counted by column because cbuffer packing gives each column
    /// its own register regardless of row count.
    /// </summary>
    public static int RequiredRegisterCount(int byteOffset, MemberShape shape)
    {
        if (shape.Kind == MemberShapeKind.Matrix)
        {
            return Math.Max(1, shape.Columns * Math.Max(shape.ArrayLength, 1));
        }

        int startRegister = byteOffset / 16;
        int endByteOffset = byteOffset + Math.Max(shape.DeclaredByteSize, 4);
        int endRegister = Math.Max(startRegister + 1, (endByteOffset + 15) / 16);
        return endRegister - startRegister;
    }

    // Highest byte a REAL member reaches. Explicit padding members contribute
    // nothing, so trailing padding never inflates the "how much of the flat array
    // is genuinely described" figure that drives tail filling.
    private static int ReferencedByteEnd(BlockMemberLayout member)
    {
        if (IsExplicitPadding(member))
        {
            return 0;
        }

        List<BlockMemberLayout>? children = member.Shape.StructMembers;
        if (member.Shape.Kind != MemberShapeKind.Struct || children is null || children.Count == 0)
        {
            return member.ByteOffset + member.SpanBytes;
        }

        int childEnd = 0;
        foreach (BlockMemberLayout child in children)
        {
            childEnd = Math.Max(childEnd, ReferencedByteEnd(child));
        }
        return member.ByteOffset + childEnd;
    }

    private static bool IsExplicitPadding(BlockMemberLayout member)
        => !string.IsNullOrWhiteSpace(member.Name) && member.Name.StartsWith("_pad", StringComparison.Ordinal);

    private static BlockMemberLayout? TryCreateNumericMember(NumericShaderParameter symbol, int byteCeiling)
    {
        if (symbol.Index < 0 || symbol.Index >= byteCeiling)
        {
            return null;
        }

        MemberShape? shape = TryCreateShape(symbol);
        if (shape is null)
        {
            return null;
        }

        return new BlockMemberLayout
        {
            Name = symbol.Name ?? string.Empty,
            ByteOffset = symbol.Index,
            Metadata = symbol,
            Shape = shape,
            RegisterOffset = symbol.Index / 16,
            RegisterCount = RequiredRegisterCount(symbol.Index, shape),
        };
    }

    private static BlockMemberLayout? TryCreateStructMember(StructParameter symbol, int byteCeiling)
    {
        bool hasMembers = symbol.VectorMembers.Length > 0 || symbol.MatrixMembers.Length > 0;
        if (symbol.Index < 0 || symbol.Index >= byteCeiling || !hasMembers)
        {
            return null;
        }

        // Children carry PARENT-relative byte offsets on the wire; rebase them to
        // struct-relative here so the access translator can reason about a struct
        // element independently of where the struct sits.
        var children = new List<BlockMemberLayout>();
        int structEnd = Math.Min(byteCeiling, symbol.Index + Math.Max(symbol.StructSize, 0));

        foreach (NumericShaderParameter child in symbol.AllNumericMembers.OrderBy(static p => p.Index))
        {
            if (child.Index < symbol.Index || child.Index >= structEnd)
            {
                continue;
            }

            MemberShape? childShape = TryCreateShape(child);
            if (childShape is null)
            {
                // A child we cannot type means we cannot type the struct — a
                // partially-typed struct would silently mis-address every member
                // after the unknown one.
                return null;
            }

            int localOffset = child.Index - symbol.Index;
            children.Add(new BlockMemberLayout
            {
                Name = child.Name ?? string.Empty,
                ByteOffset = localOffset,
                Metadata = child,
                Shape = childShape,
                RegisterOffset = localOffset / 16,
                RegisterCount = RequiredRegisterCount(localOffset, childShape),
            });
        }

        if (children.Count == 0)
        {
            return null;
        }

        children.Sort(static (left, right) => left.ByteOffset.CompareTo(right.ByteOffset));

        int elementSize = children.Max(static c => c.ByteOffset + c.SpanBytes);
        int arrayLength = Math.Max(symbol.ArraySize, 1);

        var shape = new MemberShape
        {
            Kind = MemberShapeKind.Struct,
            StructName = symbol.Name,
            StructByteSize = elementSize,
            StructMembers = children,
            ArrayLength = arrayLength,
            DeclaredByteSize = arrayLength * elementSize,
        };

        return new BlockMemberLayout
        {
            Name = symbol.Name,
            ByteOffset = symbol.Index,
            Shape = shape,
            RegisterOffset = symbol.Index / 16,
            RegisterCount = Math.Max(1, ((shape.StructByteSize * arrayLength) + 15) / 16),
        };
    }

    private static MemberShape? TryCreateShape(NumericShaderParameter symbol)
    {
        if (symbol.RowCount <= 0 || symbol.ColumnCount <= 0)
        {
            return null;
        }

        ScalarKind? scalarKind = TryResolveScalarKind(symbol.Type);
        if (scalarKind is null)
        {
            return null;
        }

        return new MemberShape
        {
            Kind = symbol.IsMatrix
                ? MemberShapeKind.Matrix
                : symbol.RowCount == 1 ? MemberShapeKind.Scalar : MemberShapeKind.Vector,
            ScalarKind = scalarKind.Value,
            Rows = symbol.RowCount,
            Columns = symbol.ColumnCount,
            ArrayLength = Math.Max(symbol.ArraySize, 1),
            DeclaredByteSize = DeclaredByteSize(symbol),
            SourceByteOffset = symbol.Index,
            IsMatrix = symbol.IsMatrix,
        };
    }

    // Half / Short have no 32-bit block representation here — a member declared
    // with one cannot be materialised, so the whole buffer stays flat rather than
    // being given a silently wrong width.
    private static ScalarKind? TryResolveScalarKind(ShaderParamType type) => type switch
    {
        ShaderParamType.Float => ScalarKind.Float,
        ShaderParamType.Int => ScalarKind.Int,
        ShaderParamType.Bool => ScalarKind.UInt,
        ShaderParamType.UInt => ScalarKind.UInt,
        _ => null,
    };

    private static int DeclaredByteSize(NumericShaderParameter symbol)
    {
        int arrayLength = Math.Max(symbol.ArraySize, 1);
        return symbol.IsMatrix
            ? symbol.ColumnCount * 16 * arrayLength
            : symbol.RowCount * symbol.ColumnCount * arrayLength * 4;
    }
}
