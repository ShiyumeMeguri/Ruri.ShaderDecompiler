namespace Ruri.ShaderTools.Spirv.Analysis;

// The structural facts about a module that every constant-buffer transform
// needs: who is decorated with what binding, what each pointer points at, what
// shape each aggregate type has.
//
// Built by ONE forward walk. The rule for this type is: if a later stage needs a
// new structural fact, teach the walk to record it — never re-walk the module
// from a downstream stage. That single rule is what keeps the pipeline linear.
internal sealed class ModuleShape
{
    /// <summary>Decorated id → its <c>DescriptorSet</c> / <c>Binding</c>, either
    /// of which may be absent until both decorations have been seen.</summary>
    public Dictionary<uint, (int? Set, int? Binding)> SetBindingById { get; } = new();

    /// <summary>Pointer type id → (storage class, pointee type id).</summary>
    public Dictionary<uint, (uint StorageClass, uint TypeId)> PointerTypes { get; } = new();

    /// <summary>Variable id → its pointer type id.</summary>
    public Dictionary<uint, uint> VariablePointerTypes { get; } = new();

    /// <summary>Struct type id → its member type ids, in declaration order.</summary>
    public Dictionary<uint, uint[]> StructMembers { get; } = new();

    /// <summary>Vector type id → (component type id, component count).</summary>
    public Dictionary<uint, (uint ComponentTypeId, uint ComponentCount)> VectorShapes { get; } = new();

    /// <summary>Array type id → (element type id, length CONSTANT id).</summary>
    public Dictionary<uint, (uint ElementTypeId, uint LengthId)> ArrayTypes { get; } = new();

    /// <summary>OpConstant result id → its literal value.</summary>
    public Dictionary<uint, uint> Constants { get; } = new();

    /// <summary>Array type id → its <c>ArrayStride</c> decoration.</summary>
    public Dictionary<uint, uint> ArrayStrides { get; } = new();

    public static ModuleShape Build(SpirvModule module)
    {
        var shape = new ModuleShape();
        List<SpirvInstruction> instructions = module.Instructions;

        for (int i = 0; i < instructions.Count; i++)
        {
            SpirvInstruction instruction = instructions[i];
            Span<uint> words = instruction.Words;

            switch (instruction.OpCode)
            {
                case SpvOpCode.OpDecorate when words.Length >= 4:
                    shape.RecordDecoration(words[1], words[2], words[3]);
                    break;

                case SpvOpCode.OpTypePointer when words.Length >= 4:
                    shape.PointerTypes[words[1]] = (words[2], words[3]);
                    break;

                case SpvOpCode.OpVariable when words.Length >= 4:
                    shape.VariablePointerTypes[words[2]] = words[1];
                    break;

                case SpvOpCode.OpTypeStruct when words.Length >= 3:
                    shape.StructMembers[words[1]] = words[2..].ToArray();
                    break;

                case SpvOpCode.OpTypeVector when words.Length >= 4:
                    shape.VectorShapes[words[1]] = (words[2], words[3]);
                    break;

                case SpvOpCode.OpTypeArray when words.Length >= 4:
                    shape.ArrayTypes[words[1]] = (words[2], words[3]);
                    break;

                case SpvOpCode.OpConstant when words.Length >= 4:
                    shape.Constants[words[2]] = words[3];
                    break;
            }
        }

        return shape;
    }

    private void RecordDecoration(uint targetId, uint decoration, uint operand)
    {
        switch (decoration)
        {
            case Decoration.DescriptorSet:
            {
                (int? Set, int? Binding) existing = SetBindingById.TryGetValue(targetId, out var value) ? value : (null, null);
                SetBindingById[targetId] = ((int)operand, existing.Binding);
                break;
            }
            case Decoration.Binding:
            {
                (int? Set, int? Binding) existing = SetBindingById.TryGetValue(targetId, out var value) ? value : (null, null);
                SetBindingById[targetId] = (existing.Set, (int)operand);
                break;
            }
            case Decoration.ArrayStride:
                ArrayStrides[targetId] = operand;
                break;
        }
    }

    public bool TryGetVectorShape(uint vectorTypeId, out uint componentTypeId, out uint componentCount)
    {
        if (VectorShapes.TryGetValue(vectorTypeId, out (uint ComponentTypeId, uint ComponentCount) shape))
        {
            componentTypeId = shape.ComponentTypeId;
            componentCount = shape.ComponentCount;
            return true;
        }

        componentTypeId = 0;
        componentCount = 0;
        return false;
    }

    /// <summary>
    /// True when <paramref name="variableId"/> is the exact shape a compiled
    /// constant buffer takes on the way out of dxil-spirv: a Uniform-storage
    /// variable whose struct has a single fixed-length array member. Reports the
    /// array's element count.
    /// </summary>
    public bool TryGetUniformBlockArrayLength(uint variableId, out int arrayLength)
    {
        arrayLength = 0;
        return VariablePointerTypes.TryGetValue(variableId, out uint pointerTypeId)
            && PointerTypes.TryGetValue(pointerTypeId, out (uint StorageClass, uint TypeId) pointer)
            && pointer.StorageClass == StorageClass.Uniform
            && StructMembers.TryGetValue(pointer.TypeId, out uint[]? members)
            && members.Length == 1
            && ArrayTypes.TryGetValue(members[0], out (uint ElementTypeId, uint LengthId) array)
            && Constants.TryGetValue(array.LengthId, out uint length)
            && TrySetLength(length, out arrayLength);

        static bool TrySetLength(uint value, out int length)
        {
            length = checked((int)value);
            return true;
        }
    }
}
