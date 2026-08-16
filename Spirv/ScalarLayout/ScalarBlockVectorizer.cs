using Ruri.ShaderTools.Spirv;
using Ruri.ShaderTools.Spirv.Analysis;

namespace Ruri.ShaderTools.Spirv.ScalarLayout;

/// <summary>
/// Normalises scalar-laid-out constant buffers into the <c>float4[N]</c> form
/// the rest of the pipeline understands.
///
/// WHY: one legacy-bytecode translation route lowers a constant buffer to a
/// single SCALAR array member — <c>OpTypeStruct { float _m0[4N] }</c> with
/// <c>ArrayStride 4</c> and scalar block layout. HLSL constant buffers are
/// float4-aligned and cannot express that, so source emission rejects the block
/// ("member 0 cannot be expressed with either HLSL packing layout or
/// packoffset"), falls back to GLSL, and the symbol injector — tuned to the
/// float4 shape the other route produces — matches nothing. The result is a
/// correct-looking decompile with every constant-buffer name lost.
///
/// The fix is representational, not semantic. The underlying buffer's byte
/// layout is untouched; only the SPIR-V type and index expressions move from
/// scalar granularity to vec4 granularity: scalar index <c>j</c> becomes
/// register <c>j &gt;&gt; 2</c>, component <c>j &amp; 3</c>. Constant indices fold
/// at rewrite time; dynamic ones get a shift/mask pair, which an emitter folds
/// straight back out.
///
/// No-op — returns the input unchanged — when no scalar uniform array exists, so
/// it is safe to run unconditionally ahead of the constant-buffer structurer.
///
/// Do NOT "solve" the underlying problem by letting the GLSL fallback take over.
/// That trades every recovered symbol for an emit that happens to succeed.
/// </summary>
internal static class ScalarBlockVectorizer
{
    /// <summary>The scalar-block stride that identifies the shape being fixed.</summary>
    private const uint ScalarArrayStride = 4;

    /// <summary>Canonical constant-buffer array stride.</summary>
    private const uint Vec4ArrayStride = 16;

    public static byte[] Vectorize(byte[] spirv)
    {
        SpirvModule module;
        try
        {
            module = SpirvModule.Parse(spirv);
        }
        catch
        {
            // Not parseable as SPIR-V: not this transform's problem to report.
            return spirv;
        }

        ScalarBlockSurvey survey = ScalarBlockSurvey.Collect(module);
        if (survey.IsEmpty)
        {
            return spirv;
        }

        var types = new SpirvTypeInterner(module);

        WidenMemberTypes(module, types, survey);

        List<IndexSplit> splits = PlanIndexSplits(module, survey);
        if (splits.Count == 0)
        {
            // Types are fixed but nothing indexes them — still a real change.
            return module.ToBytes();
        }

        ApplyIndexSplits(module, types, splits);
        return module.ToBytes();
    }

    // --- phase 1: widen float[4N] -> float4[N] ------------------------------

    private static void WidenMemberTypes(SpirvModule module, SpirvTypeInterner types, ScalarBlockSurvey survey)
    {
        uint vec4TypeId = types.EnsureVector(ScalarKind.Float, 4);
        var widened = new Dictionary<uint, uint>();

        foreach (KeyValuePair<(uint StructId, int Member), uint> entry in survey.ScalarMembers)
        {
            uint scalarArrayId = entry.Value;

            if (!widened.TryGetValue(scalarArrayId, out uint vec4ArrayId))
            {
                int vec4Length = (survey.ArrayLength(scalarArrayId) + 3) / 4;
                vec4ArrayId = types.InternModuleArray(vec4TypeId, vec4Length, Vec4ArrayStride);
                widened[scalarArrayId] = vec4ArrayId;
            }

            // Repoint the struct member at the widened array.
            survey.StructDeclarations[entry.Key.StructId][2 + entry.Key.Member] = vec4ArrayId;
        }
    }

    // --- phase 2: find the access chains that index those members -----------

    private static List<IndexSplit> PlanIndexSplits(SpirvModule module, ScalarBlockSurvey survey)
    {
        // The shape being split is exactly six words:
        //   [op, ptrType, result, base, memberIndex(const), scalarIndex]
        // i.e. two indices ending at a scalar float. Anything deeper is already
        // component-addressed and needs no split.
        const int ScalarChainWordCount = 6;

        var splits = new List<IndexSplit>();
        List<SpirvInstruction> instructions = module.Instructions;

        for (int i = 0; i < instructions.Count; i++)
        {
            SpirvInstruction instruction = instructions[i];

            if (instruction.OpCode is not (SpvOpCode.OpAccessChain or SpvOpCode.OpInBoundsAccessChain)
                || instruction.WordCount != ScalarChainWordCount
                || !survey.BlockStructByVariable.TryGetValue(instruction[3], out uint structId))
            {
                continue;
            }

            if (!survey.ConstantValues.TryGetValue(instruction[4], out uint memberIndex)
                || !survey.ScalarMembers.ContainsKey((structId, (int)memberIndex)))
            {
                continue;
            }

            uint scalarIndexId = instruction[5];
            bool isConstant = survey.ConstantValues.TryGetValue(scalarIndexId, out uint scalarIndex);
            splits.Add(new IndexSplit(instruction, scalarIndexId, isConstant, isConstant ? scalarIndex : 0));
        }

        return splits;
    }

    // --- phase 3: rewrite the indices ---------------------------------------

    private static void ApplyIndexSplits(SpirvModule module, SpirvTypeInterner types, List<IndexSplit> splits)
    {
        bool anyDynamic = false;
        foreach (IndexSplit split in splits)
        {
            if (!split.IsConstant)
            {
                anyDynamic = true;
                break;
            }
        }

        uint uintTypeId = types.EnsureUInt();
        uint shiftAmount = anyDynamic ? types.InternUIntConstant(2) : 0;   // j >> 2  → register
        uint componentMask = anyDynamic ? types.InternUIntConstant(3) : 0; // j & 3   → component

        // Dynamic splits need a shift/mask pair emitted immediately before the
        // chain that consumes them, so they are staged and spliced in one pass
        // rather than inserted mid-iteration.
        var pending = new Dictionary<SpirvInstruction, (SpirvInstruction Shift, SpirvInstruction Mask)>();

        foreach (IndexSplit split in splits)
        {
            uint registerId;
            uint componentId;

            if (split.IsConstant)
            {
                registerId = types.InternUIntConstant(split.ScalarIndex >> 2);
                componentId = types.InternUIntConstant(split.ScalarIndex & 3);
            }
            else
            {
                registerId = module.AllocateId();
                componentId = module.AllocateId();

                pending[split.Instruction] = (
                    module.CreateInstruction(SpvOpCode.OpShiftRightLogical,
                    [
                        SpvOpCode.MakeInstructionWord(SpvOpCode.OpShiftRightLogical, 5),
                        uintTypeId, registerId, split.ScalarIndexId, shiftAmount,
                    ]),
                    module.CreateInstruction(SpvOpCode.OpBitwiseAnd,
                    [
                        SpvOpCode.MakeInstructionWord(SpvOpCode.OpBitwiseAnd, 5),
                        uintTypeId, componentId, split.ScalarIndexId, componentMask,
                    ]));
            }

            SpirvInstruction chain = split.Instruction;
            Span<uint> rewritten = stackalloc uint[7];
            rewritten[0] = SpvOpCode.MakeInstructionWord(chain.OpCode, 7);
            rewritten[1] = chain[1];
            rewritten[2] = chain[2];
            rewritten[3] = chain[3];
            rewritten[4] = chain[4];
            rewritten[5] = registerId;
            rewritten[6] = componentId;
            chain.SetWords(rewritten);
        }

        if (pending.Count == 0)
        {
            return;
        }

        List<SpirvInstruction> instructions = module.Instructions;
        var rebuilt = new List<SpirvInstruction>(instructions.Count + (pending.Count * 2));

        foreach (SpirvInstruction instruction in instructions)
        {
            if (pending.TryGetValue(instruction, out (SpirvInstruction Shift, SpirvInstruction Mask) extra))
            {
                rebuilt.Add(extra.Shift);
                rebuilt.Add(extra.Mask);
            }
            rebuilt.Add(instruction);
        }

        instructions.Clear();
        instructions.AddRange(rebuilt);
    }

    /// <summary>One access chain whose scalar index must become (register, component).</summary>
    private readonly record struct IndexSplit(
        SpirvInstruction Instruction,
        uint ScalarIndexId,
        bool IsConstant,
        uint ScalarIndex);

    /// <summary>
    /// Everything phase 1 needs to know: which uniform block structs have a
    /// scalar-float array member, which variables point at them, and the
    /// constant values needed to fold indices.
    /// </summary>
    private sealed class ScalarBlockSurvey
    {
        public Dictionary<(uint StructId, int Member), uint> ScalarMembers { get; } = new();
        public Dictionary<uint, uint> BlockStructByVariable { get; } = new();
        public Dictionary<uint, SpirvInstruction> StructDeclarations { get; } = new();
        public Dictionary<uint, uint> ConstantValues { get; } = new();

        private readonly Dictionary<uint, uint> _arrayLengthConstant = new();

        public bool IsEmpty => ScalarMembers.Count == 0;

        public int ArrayLength(uint arrayTypeId)
            => _arrayLengthConstant.TryGetValue(arrayTypeId, out uint lengthId)
               && ConstantValues.TryGetValue(lengthId, out uint length)
                ? (int)length
                : 0;

        public static ScalarBlockSurvey Collect(SpirvModule module)
        {
            var survey = new ScalarBlockSurvey();

            var floatScalarTypes = new HashSet<uint>();
            var arrayElement = new Dictionary<uint, uint>();
            var arrayStride = new Dictionary<uint, uint>();
            var blockStructs = new HashSet<uint>();
            var pointerTarget = new Dictionary<uint, (uint StorageClass, uint Pointee)>();

            List<SpirvInstruction> instructions = module.Instructions;
            for (int i = 0; i < instructions.Count; i++)
            {
                SpirvInstruction instruction = instructions[i];
                Span<uint> words = instruction.Words;

                switch (instruction.OpCode)
                {
                    case SpvOpCode.OpTypeFloat when words.Length >= 3 && words[2] == 32:
                        floatScalarTypes.Add(words[1]);
                        break;
                    case SpvOpCode.OpConstant when words.Length >= 4:
                        survey.ConstantValues[words[2]] = words[3];
                        break;
                    case SpvOpCode.OpTypeArray when words.Length >= 4:
                        arrayElement[words[1]] = words[2];
                        survey._arrayLengthConstant[words[1]] = words[3];
                        break;
                    case SpvOpCode.OpTypeStruct:
                        survey.StructDeclarations[words[1]] = instruction;
                        break;
                    case SpvOpCode.OpTypePointer when words.Length >= 4:
                        pointerTarget[words[1]] = (words[2], words[3]);
                        break;
                    case SpvOpCode.OpDecorate when words.Length >= 3 && words[2] == Decoration.Block:
                        blockStructs.Add(words[1]);
                        break;
                    case SpvOpCode.OpDecorate when words.Length >= 4 && words[2] == Decoration.ArrayStride:
                        arrayStride[words[1]] = words[3];
                        break;
                }
            }

            for (int i = 0; i < instructions.Count; i++)
            {
                SpirvInstruction variable = instructions[i];
                if (variable.OpCode != SpvOpCode.OpVariable || variable.WordCount < 4)
                {
                    continue;
                }

                // Constant buffers only — an SSBO's scalar layout is legal and
                // must be left exactly as it is.
                if (variable[3] != StorageClass.Uniform
                    || !pointerTarget.TryGetValue(variable[1], out (uint StorageClass, uint Pointee) pointer))
                {
                    continue;
                }

                uint structId = pointer.Pointee;
                if (!blockStructs.Contains(structId)
                    || !survey.StructDeclarations.TryGetValue(structId, out SpirvInstruction? structDeclaration))
                {
                    continue;
                }

                bool matched = false;
                Span<uint> members = structDeclaration.Words;
                for (int member = 0; member < members.Length - 2; member++)
                {
                    uint memberTypeId = members[2 + member];
                    if (arrayElement.TryGetValue(memberTypeId, out uint element)
                        && floatScalarTypes.Contains(element)
                        && arrayStride.TryGetValue(memberTypeId, out uint stride)
                        && stride == ScalarArrayStride)
                    {
                        survey.ScalarMembers[(structId, member)] = memberTypeId;
                        matched = true;
                    }
                }

                if (matched)
                {
                    survey.BlockStructByVariable[variable[2]] = structId;
                }
            }

            return survey;
        }
    }
}
