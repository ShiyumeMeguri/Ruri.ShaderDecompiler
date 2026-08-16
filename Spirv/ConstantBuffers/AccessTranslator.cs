using Ruri.ShaderTools.Spirv.Analysis;

namespace Ruri.ShaderTools.Spirv.ConstantBuffers;

/// <summary>
/// Maps a flat <c>float4[register]</c> access onto the equivalent access into a
/// structured block: which member, which sub-indices, and — critically — what
/// SPIR-V type the resulting chain yields.
///
/// The four translation paths (static, dynamic-array, struct, dynamic-struct)
/// live together because they mutually recurse: a struct member's child is
/// translated by the same static path, and the dynamic path reuses it for the
/// element it lands on. Splitting them across files would only move the
/// recursion, not remove it.
///
/// Every path returns null rather than throwing. A null means "this access does
/// not correspond to a named member", which is a legitimate answer the caller
/// handles by trying the component-read path or by abandoning the buffer.
///
/// THE RECURRING BUG THIS CODE EXISTS TO PREVENT: the result type must match the
/// depth the chain walks. Handing back the member's own type after adding a
/// component index produces a chain that claims to yield a vec4 while addressing
/// one float, and spirv-cross dies on it with "Cannot subdivide a scalar value".
/// Hence the deliberate split between <c>ResolvedTypeId</c>,
/// <c>ArrayElementTypeId</c>, <c>ColumnVectorTypeId</c> and <c>ScalarTypeId</c>,
/// and the care taken below about which one each branch returns.
/// </summary>
internal static class AccessTranslator
{
    public static AccessTranslation? Translate(BlockLayout layout, FlatAccessPath path, ConstantValueMap constants)
    {
        int componentIndex = path.ExtraIndices.Count > 0 ? path.ExtraIndices[0] : 0;

        if (!path.Slot.IsStatic)
        {
            return TranslateDynamic(layout, path, constants, componentIndex);
        }

        int absoluteRegister = path.Slot.ConstantRegisterOffset;
        int absoluteByteOffset = (absoluteRegister * 16) + (componentIndex * 4);

        for (int memberIndex = 0; memberIndex < layout.Members.Count; memberIndex++)
        {
            BlockMemberLayout member = layout.Members[memberIndex];

            if (absoluteRegister < member.RegisterOffset || absoluteRegister >= member.RegisterOffset + member.RegisterCount)
            {
                continue;
            }

            if (!CoversByte(member, absoluteByteOffset, path.ExtraIndices))
            {
                continue;
            }

            AccessTranslation? inner = TranslateMember(member, absoluteRegister, componentIndex, path.ExtraIndices, constants);
            if (inner is null || !constants.TryGetId((uint)memberIndex, out uint memberIndexConstantId))
            {
                continue;
            }

            var indices = new List<uint>(inner.Indices.Count + 1) { memberIndexConstantId };
            indices.AddRange(inner.Indices);

            return new AccessTranslation { Indices = indices, MemberTypeId = inner.MemberTypeId };
        }

        return null;
    }

    // ---- index helpers -----------------------------------------------------

    public static AccessTranslation? Build(ConstantValueMap constants, uint memberTypeId)
        => new() { Indices = new List<uint>(), MemberTypeId = memberTypeId };

    public static AccessTranslation? Build(ConstantValueMap constants, uint memberTypeId, int index0)
        => constants.TryGetId((uint)index0, out uint id0)
            ? new AccessTranslation { Indices = new List<uint>(1) { id0 }, MemberTypeId = memberTypeId }
            : null;

    public static AccessTranslation? Build(ConstantValueMap constants, uint memberTypeId, int index0, int index1)
        => constants.TryGetId((uint)index0, out uint id0) && constants.TryGetId((uint)index1, out uint id1)
            ? new AccessTranslation { Indices = new List<uint>(2) { id0, id1 }, MemberTypeId = memberTypeId }
            : null;

    // ---- containment -------------------------------------------------------

    // Does `member` own this byte? Arrays, matrices and structs accept any byte
    // inside their declared span because the compiled chain addresses them by
    // register without necessarily emitting a component index; a lone
    // scalar/vector with no trailing index must match its start exactly, or a
    // neighbour packed into the same register would steal the access.
    private static bool CoversByte(BlockMemberLayout member, int absoluteByteOffset, List<int> extraIndices)
    {
        int start = member.ByteOffset;
        MemberShape shape = member.Shape;

        if (shape.Kind == MemberShapeKind.Struct)
        {
            int end = start + (shape.StructByteSize * Math.Max(shape.ArrayLength, 1));
            return absoluteByteOffset >= start && absoluteByteOffset < end;
        }

        int declaredEnd = start + Math.Max(shape.DeclaredByteSize, 4);

        if (shape.Kind == MemberShapeKind.Matrix || shape.ArrayLength > 1)
        {
            return absoluteByteOffset >= start && absoluteByteOffset < declaredEnd;
        }

        return extraIndices.Count == 0
            ? absoluteByteOffset == start
            : absoluteByteOffset >= start && absoluteByteOffset < declaredEnd;
    }

    // ---- static translation ------------------------------------------------

    private static AccessTranslation? TranslateMember(
        BlockMemberLayout member,
        int absoluteRegister,
        int componentIndex,
        List<int> extraIndices,
        ConstantValueMap constants)
    {
        MemberShape shape = member.Shape;
        int localRegister = absoluteRegister - member.RegisterOffset;
        int memberComponentOffset = (member.ByteOffset % 16) / 4;
        bool hasTrailingIndices = extraIndices.Count > 1;

        if (shape.Kind == MemberShapeKind.Struct)
        {
            return TranslateStructMember(member, absoluteRegister, componentIndex, extraIndices, constants);
        }

        if (shape.Kind == MemberShapeKind.Matrix)
        {
            if (localRegister < 0 || localRegister >= shape.Columns)
            {
                return null;
            }

            // matrix[col] yields a column VECTOR, not a matrix.
            if (extraIndices.Count == 0)
            {
                return member.ColumnVectorTypeId != 0
                    ? Build(constants, member.ColumnVectorTypeId, localRegister)
                    : null;
            }

            if (componentIndex < 0 || componentIndex >= shape.Rows || hasTrailingIndices || member.ScalarTypeId == 0)
            {
                return null;
            }

            // matrix[col][component] yields a scalar.
            return Build(constants, member.ScalarTypeId, localRegister, componentIndex);
        }

        if (member.RegisterCount == 1)
        {
            if (shape.Kind == MemberShapeKind.Scalar)
            {
                return componentIndex == memberComponentOffset && !hasTrailingIndices
                    ? Build(constants, member.ResolvedTypeId)
                    : null;
            }

            if (shape.Kind == MemberShapeKind.Vector)
            {
                if (hasTrailingIndices)
                {
                    return null;
                }

                // No component index: address the whole vector. Adding one here
                // would dive into the vector while keeping the vec4 result type —
                // an invalid chain spirv-cross rejects outright.
                if (extraIndices.Count == 0)
                {
                    return componentIndex == memberComponentOffset
                        ? Build(constants, member.ResolvedTypeId)
                        : null;
                }

                int relativeComponent = componentIndex - memberComponentOffset;
                if (relativeComponent < 0 || relativeComponent >= shape.Rows || member.ScalarTypeId == 0)
                {
                    return null;
                }

                return Build(constants, member.ScalarTypeId, relativeComponent);
            }
        }

        if (localRegister < 0 || localRegister >= member.RegisterCount || hasTrailingIndices)
        {
            return null;
        }

        // Multi-register fall-through: arrays of scalar / vector / matrix. The
        // result type depends on how deep the chain walks, NOT on the member's
        // full type — that still carries the array wrapper.
        if (shape.ArrayLength > 1 && member.ArrayElementTypeId != 0)
        {
            if (extraIndices.Count == 0)
            {
                return Build(constants, member.ArrayElementTypeId, localRegister);
            }

            // Per-component read of an element. Only meaningful for vector
            // elements: a scalar array cannot subdivide further, and a matrix
            // array would need an extra column index.
            if (shape.Kind == MemberShapeKind.Vector && member.ScalarTypeId != 0)
            {
                int relativeComponent = componentIndex - memberComponentOffset;
                return relativeComponent < 0 || relativeComponent >= shape.Rows
                    ? null
                    : Build(constants, member.ScalarTypeId, localRegister, relativeComponent);
            }

            return null;
        }

        return extraIndices.Count > 0
            ? Build(constants, member.ResolvedTypeId, localRegister, componentIndex)
            : Build(constants, member.ResolvedTypeId, localRegister);
    }

    private static AccessTranslation? TranslateStructMember(
        BlockMemberLayout member,
        int absoluteRegister,
        int componentIndex,
        List<int> extraIndices,
        ConstantValueMap constants)
    {
        MemberShape shape = member.Shape;
        List<BlockMemberLayout>? children = shape.StructMembers;
        if (children is null)
        {
            return null;
        }

        int localByteOffset = (absoluteRegister * 16) + (componentIndex * 4) - member.ByteOffset;
        int arrayLength = Math.Max(shape.ArrayLength, 1);
        int elementSize = Math.Max(shape.StructByteSize, 1);
        int elementIndex = localByteOffset / elementSize;
        int elementLocalByteOffset = localByteOffset % elementSize;

        if (elementIndex < 0 || elementIndex >= arrayLength)
        {
            return null;
        }

        for (int childIndex = 0; childIndex < children.Count; childIndex++)
        {
            BlockMemberLayout child = children[childIndex];

            if (elementLocalByteOffset < child.ByteOffset
                || elementLocalByteOffset >= child.ByteOffset + child.SpanBytes)
            {
                continue;
            }

            if (!CoversByte(child, elementLocalByteOffset, extraIndices))
            {
                continue;
            }

            // Rebase the register onto this struct ELEMENT so the child sees an
            // offset it can reason about independently of which element we are in.
            int elementRegisterSpan = elementSize / 16;
            int childRegister = member.RegisterOffset + ((absoluteRegister - member.RegisterOffset) - (elementIndex * elementRegisterSpan));

            AccessTranslation? childTranslation = TranslateMember(child, childRegister, componentIndex, extraIndices, constants);
            if (childTranslation is null || !constants.TryGetId((uint)childIndex, out uint childIndexConstantId))
            {
                continue;
            }

            var indices = new List<uint>(childTranslation.Indices.Count + 2);
            if (arrayLength > 1)
            {
                if (!constants.TryGetId((uint)elementIndex, out uint elementConstantId))
                {
                    continue;
                }
                indices.Add(elementConstantId);
            }

            indices.Add(childIndexConstantId);
            indices.AddRange(childTranslation.Indices);

            return new AccessTranslation { Indices = indices, MemberTypeId = childTranslation.MemberTypeId };
        }

        return null;
    }

    // ---- dynamic translation -----------------------------------------------

    private static AccessTranslation? TranslateDynamic(
        BlockLayout layout,
        FlatAccessPath path,
        ConstantValueMap constants,
        int componentIndex)
    {
        if (path.Slot.DynamicIndexId == 0 || path.Slot.DynamicIndexStride <= 0)
        {
            return null;
        }

        // Sweep 1 — dynamic indexing into an array member.
        for (int memberIndex = 0; memberIndex < layout.Members.Count; memberIndex++)
        {
            BlockMemberLayout member = layout.Members[memberIndex];
            if (member.Shape.Kind == MemberShapeKind.Struct || Math.Max(member.Shape.ArrayLength, 1) <= 1)
            {
                continue;
            }

            int elementRegisterStride = DynamicElementRegisterStride(member);
            if (elementRegisterStride != path.Slot.DynamicIndexStride)
            {
                continue;
            }

            int localRegisterOffset = path.Slot.ConstantRegisterOffset - member.RegisterOffset;
            if (localRegisterOffset < 0 || localRegisterOffset >= elementRegisterStride)
            {
                continue;
            }

            if (!constants.TryGetId((uint)memberIndex, out uint memberIndexConstantId))
            {
                continue;
            }

            AccessTranslation? inner = TranslateDynamicArrayMember(
                member, localRegisterOffset, componentIndex, path.ExtraIndices, path.Slot.DynamicIndexId, constants);
            if (inner is null)
            {
                continue;
            }

            var indices = new List<uint>(inner.Indices.Count + 1) { memberIndexConstantId };
            indices.AddRange(inner.Indices);
            return new AccessTranslation { Indices = indices, MemberTypeId = inner.MemberTypeId };
        }

        // Sweep 2 — dynamic indexing into a STRUCT array, i.e.
        // `Array[instanceId].field`. Separate sweep because a struct element's
        // stride and child resolution work nothing like an array element's.
        for (int memberIndex = 0; memberIndex < layout.Members.Count; memberIndex++)
        {
            BlockMemberLayout member = layout.Members[memberIndex];
            MemberShape shape = member.Shape;

            if (shape.Kind != MemberShapeKind.Struct || Math.Max(shape.ArrayLength, 1) <= 1)
            {
                continue;
            }

            int elementRegisterStride = Math.Max(1, (shape.StructByteSize + 15) / 16);
            int localRegisterOffset = path.Slot.ConstantRegisterOffset - member.RegisterOffset;

            if (localRegisterOffset < 0 || localRegisterOffset >= elementRegisterStride
                || elementRegisterStride != path.Slot.DynamicIndexStride)
            {
                continue;
            }

            if (path.ExtraIndices.Count > 1)
            {
                return null;
            }

            List<BlockMemberLayout>? children = shape.StructMembers;
            if (children is null)
            {
                return null;
            }

            int localByteOffset = (localRegisterOffset * 16) + (componentIndex * 4);
            for (int childIndex = 0; childIndex < children.Count; childIndex++)
            {
                BlockMemberLayout child = children[childIndex];
                if (localByteOffset < child.ByteOffset || localByteOffset >= child.ByteOffset + child.SpanBytes)
                {
                    continue;
                }

                if (!constants.TryGetId((uint)childIndex, out uint childIndexConstantId))
                {
                    continue;
                }

                AccessTranslation? childTranslation =
                    TranslateMember(child, localRegisterOffset, componentIndex, path.ExtraIndices, constants);
                if (childTranslation is null || !constants.TryGetId((uint)memberIndex, out uint memberIndexConstantId))
                {
                    continue;
                }

                var indices = new List<uint>(childTranslation.Indices.Count + 3)
                {
                    memberIndexConstantId,
                    path.Slot.DynamicIndexId,
                    childIndexConstantId,
                };
                indices.AddRange(childTranslation.Indices);
                return new AccessTranslation { Indices = indices, MemberTypeId = childTranslation.MemberTypeId };
            }
        }

        return null;
    }

    private static int DynamicElementRegisterStride(BlockMemberLayout member)
    {
        MemberShape shape = member.Shape;
        if (shape.Kind == MemberShapeKind.Matrix)
        {
            return Math.Max(1, shape.Columns);
        }

        int elementByteSize = shape.ArrayLength > 1
            ? Math.Max(4, shape.DeclaredByteSize / Math.Max(shape.ArrayLength, 1))
            : Math.Max(shape.DeclaredByteSize, 4);
        return Math.Max(1, (elementByteSize + 15) / 16);
    }

    private static AccessTranslation? TranslateDynamicArrayMember(
        BlockMemberLayout member,
        int localRegisterOffset,
        int componentIndex,
        List<int> extraIndices,
        uint dynamicIndexId,
        ConstantValueMap constants)
    {
        if (extraIndices.Count > 1)
        {
            return null;
        }

        MemberShape shape = member.Shape;

        if (shape.Kind == MemberShapeKind.Matrix)
        {
            if (localRegisterOffset < 0 || localRegisterOffset >= shape.Columns
                || !constants.TryGetId((uint)localRegisterOffset, out uint registerConstantId))
            {
                return null;
            }

            if (extraIndices.Count == 0)
            {
                return member.ColumnVectorTypeId == 0
                    ? null
                    : new AccessTranslation
                    {
                        Indices = new List<uint>(2) { dynamicIndexId, registerConstantId },
                        MemberTypeId = member.ColumnVectorTypeId,
                    };
            }

            if (componentIndex < 0 || componentIndex >= shape.Rows
                || member.ScalarTypeId == 0
                || !constants.TryGetId((uint)componentIndex, out uint componentConstantId))
            {
                return null;
            }

            return new AccessTranslation
            {
                Indices = new List<uint>(3) { dynamicIndexId, registerConstantId, componentConstantId },
                MemberTypeId = member.ScalarTypeId,
            };
        }

        if (localRegisterOffset != 0)
        {
            return null;
        }

        int memberComponentOffset = (member.ByteOffset % 16) / 4;

        // After ONE array index the result is the array's ELEMENT type. Using the
        // member's resolved type here inherits the array wrapper and produces a
        // chain spirv-cross cannot lower.
        if (shape.Kind == MemberShapeKind.Scalar)
        {
            uint elementType = member.ArrayElementTypeId != 0 ? member.ArrayElementTypeId : member.ResolvedTypeId;
            return componentIndex == memberComponentOffset
                ? new AccessTranslation { Indices = new List<uint>(1) { dynamicIndexId }, MemberTypeId = elementType }
                : null;
        }

        if (shape.Kind == MemberShapeKind.Vector)
        {
            if (extraIndices.Count == 0)
            {
                uint elementType = member.ArrayElementTypeId != 0 ? member.ArrayElementTypeId : member.ResolvedTypeId;
                return componentIndex == memberComponentOffset
                    ? new AccessTranslation { Indices = new List<uint>(1) { dynamicIndexId }, MemberTypeId = elementType }
                    : null;
            }

            int relativeComponent = componentIndex - memberComponentOffset;
            if (relativeComponent < 0 || relativeComponent >= shape.Rows
                || member.ScalarTypeId == 0
                || !constants.TryGetId((uint)relativeComponent, out uint componentConstantId))
            {
                return null;
            }

            return new AccessTranslation
            {
                Indices = new List<uint>(2) { dynamicIndexId, componentConstantId },
                MemberTypeId = member.ScalarTypeId,
            };
        }

        return null;
    }
}
