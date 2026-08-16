using Ruri.ShaderTools.Spirv;
using Ruri.ShaderTools.Spirv.Analysis;

namespace Ruri.ShaderTools.Spirv.ConstantBuffers;

/// <summary>
/// <see cref="BlockMemberLayout"/> → a concrete SPIR-V type id, minting whatever
/// the module is missing.
///
/// Also pre-resolves the three DEPTH-DEPENDENT type ids on every member
/// (<c>ScalarTypeId</c>, <c>ColumnVectorTypeId</c>, <c>ArrayElementTypeId</c>).
/// Those are not conveniences: an access chain's result type must describe what
/// the chain actually addresses, so a per-component read of a vec4 needs a
/// pointer-to-scalar, not a pointer-to-vec4 that happens to have one more index
/// on it. Resolving them here — including for children of nested structs — is
/// what lets the translator stay allocation-free and never re-enter the type
/// factory mid-translation.
/// </summary>
internal static class MemberTypeMaterializer
{
    public static uint Materialize(SpirvModule module, SpirvTypeInterner types, BlockMemberLayout member)
    {
        MemberShape shape = member.Shape;

        uint baseTypeId = shape.Kind switch
        {
            MemberShapeKind.Scalar => types.EnsureScalar(shape.ScalarKind),
            MemberShapeKind.Vector => types.EnsureVector(shape.ScalarKind, shape.Rows),
            MemberShapeKind.Matrix => types.EnsureMatrix(shape.Rows, shape.Columns),
            MemberShapeKind.Struct => MaterializeStruct(module, types, member),
            _ => 0,
        };

        if (baseTypeId == 0)
        {
            return 0;
        }

        if (shape.ArrayLength <= 1)
        {
            return baseTypeId;
        }

        // HLSL cbuffer rule: every array element starts on a 16-byte boundary
        // whatever its size, so `float arr[8]` occupies eight vec4 slots with
        // arr[i] living in .x of each. A "tight" 4/8/12-byte stride is always
        // wrong here and makes spirv-cross reject the block with "cannot be
        // expressed with either HLSL packing layout or packoffset".
        int stride = shape.Kind switch
        {
            MemberShapeKind.Struct => shape.StructByteSize,
            MemberShapeKind.Matrix => shape.Columns * 16,
            _ => 16,
        };

        return types.InternDecoratedArray(baseTypeId, shape.ArrayLength, Math.Max(stride, 16));
    }

    /// <summary>
    /// Fill in <c>ScalarTypeId</c> / <c>ColumnVectorTypeId</c> /
    /// <c>ArrayElementTypeId</c> for one member. Shared by the top-level pass and
    /// by nested-struct children so both get identical treatment — a matrix
    /// nested inside a struct array with no column-vector type resolved is what
    /// used to make instancing buffers fail to structure at all.
    /// </summary>
    public static void ResolveAccessTypes(SpirvModule module, SpirvTypeInterner types, BlockMemberLayout member)
    {
        MemberShape shape = member.Shape;

        // The deepest scalar. For a scalar ARRAY this is the element scalar, not
        // the array type — using the member's own id there would inherit the
        // array wrapper into every per-element access.
        uint scalarTypeId = shape.Kind is MemberShapeKind.Scalar or MemberShapeKind.Vector or MemberShapeKind.Matrix
            ? types.EnsureScalar(shape.ScalarKind)
            : 0;
        member.ScalarTypeId = scalarTypeId;

        if (shape.Kind == MemberShapeKind.Matrix)
        {
            member.ColumnVectorTypeId = types.EnsureVector(shape.ScalarKind, shape.Rows);
        }

        if (shape.ArrayLength > 1)
        {
            member.ArrayElementTypeId = shape.Kind switch
            {
                MemberShapeKind.Scalar => scalarTypeId,
                MemberShapeKind.Vector => types.EnsureVector(shape.ScalarKind, shape.Rows),
                MemberShapeKind.Matrix => types.EnsureMatrix(shape.Rows, shape.Columns),
                _ => 0,
            };
        }
    }

    private static uint MaterializeStruct(SpirvModule module, SpirvTypeInterner types, BlockMemberLayout member)
    {
        if (member.ResolvedTypeId != 0)
        {
            return member.ResolvedTypeId;
        }

        List<BlockMemberLayout>? children = member.Shape.StructMembers;
        if (children is null || children.Count == 0)
        {
            return 0;
        }

        var childTypeIds = new List<uint>(children.Count);
        foreach (BlockMemberLayout child in children)
        {
            uint childTypeId = Materialize(module, types, child);
            if (childTypeId == 0)
            {
                return 0;
            }

            child.ResolvedTypeId = childTypeId;
            ResolveAccessTypes(module, types, child);
            childTypeIds.Add(childTypeId);
        }

        uint structTypeId = module.AllocateId();

        // Decorations must precede the type they describe, so they go to the
        // decoration anchor while the type goes to the end of the type section.
        var decorations = new List<SpirvInstruction>(children.Count);
        for (int childIndex = 0; childIndex < children.Count; childIndex++)
        {
            BlockMemberLayout child = children[childIndex];

            decorations.Add(module.CreateInstruction(SpvOpCode.OpMemberDecorate,
            [
                SpvOpCode.MakeInstructionWord(SpvOpCode.OpMemberDecorate, 5),
                structTypeId, (uint)childIndex, Decoration.Offset, (uint)child.ByteOffset,
            ]));

            if (child.Shape.Kind != MemberShapeKind.Matrix)
            {
                continue;
            }

            decorations.Add(module.CreateInstruction(SpvOpCode.OpMemberDecorate,
            [
                SpvOpCode.MakeInstructionWord(SpvOpCode.OpMemberDecorate, 4),
                structTypeId, (uint)childIndex, Decoration.RowMajor,
            ]));
            decorations.Add(module.CreateInstruction(SpvOpCode.OpMemberDecorate,
            [
                SpvOpCode.MakeInstructionWord(SpvOpCode.OpMemberDecorate, 5),
                structTypeId, (uint)childIndex, Decoration.MatrixStride, 16,
            ]));
        }

        module.PrependDecorations(decorations);

        int structWordCount = 2 + childTypeIds.Count;
        Span<uint> structWords = structWordCount <= 128 ? stackalloc uint[structWordCount] : new uint[structWordCount];
        structWords[0] = SpvOpCode.MakeInstructionWord(SpvOpCode.OpTypeStruct, (ushort)structWordCount);
        structWords[1] = structTypeId;
        for (int i = 0; i < childTypeIds.Count; i++)
        {
            structWords[2 + i] = childTypeIds[i];
        }
        module.AppendType(module.CreateInstruction(SpvOpCode.OpTypeStruct, structWords));

        if (!string.IsNullOrWhiteSpace(member.Name))
        {
            module.InsertDebugName(structTypeId, member.Name);
        }

        // Every child is named, including the ones the symbols left blank.
        //
        // Skipping the blank ones leaves the emitter's own `_m0`, `_m1`… in place,
        // which is indistinguishable from a field genuinely called that — a reader
        // cannot tell the name was lost. Top-level members already get an explicit
        // marker for exactly this reason; nested members were the one place that
        // still leaked machine names. The offset is struct-RELATIVE, because that
        // is what identifies a field inside the struct regardless of which array
        // element it belongs to.
        for (int childIndex = 0; childIndex < children.Count; childIndex++)
        {
            BlockMemberLayout child = children[childIndex];
            string name = string.IsNullOrWhiteSpace(child.Name)
                ? $"{GeneratedNames.StrippedSymbol}_{child.ByteOffset}"
                : child.Name;

            module.InsertDebugMemberName(structTypeId, (uint)childIndex, name);
        }

        member.ResolvedTypeId = structTypeId;
        return structTypeId;
    }
}
