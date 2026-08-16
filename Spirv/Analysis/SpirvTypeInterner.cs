namespace Ruri.ShaderTools.Spirv.Analysis;

// Find-or-create for SPIR-V type and constant declarations, backed by an index
// instead of a module scan.
//
// Every `Ensure*` below used to be a full walk of the instruction list. Two of
// them ran inside loops — the uint-constant lookup is called once per integer in
// `[0, maxTranslationIndex]` for every rewritten constant buffer, and the
// uniform-pointer lookup once per member type per plan — which is where a large
// UB (a 256-element light array, say) turned a linear job into a quadratic one.
//
// TWO INTERNING POLICIES coexist here, and the difference is load-bearing:
//
//   * scalars / vectors / matrices  — LAST declaration in the module wins.
//   * constants / pointers / module arrays — FIRST declaration in the module wins.
//
// That asymmetry is not an accident of this class: it reproduces how the two
// original lookup families behaved (a seeding walk that overwrote as it went,
// versus a scan that returned on first match). Since the returned id ends up in
// emitted access chains, changing either policy changes output bytes.
//
// Emission goes through the module's type-section anchor, so newly minted types
// land exactly where the previous implementation put them.
internal sealed class SpirvTypeInterner
{
    private readonly SpirvModule _module;

    // Last-wins caches, seeded from the module's own declarations.
    private uint _floatTypeId;
    private uint _intTypeId;
    private uint _uintTypeId;
    private readonly Dictionary<(ScalarKind Kind, int Components), uint> _vectors = new();
    private readonly Dictionary<(int Rows, int Columns), uint> _matrices = new();

    // Vector shape BY ID, needed to classify a matrix's column type. Kept
    // separate from `_vectors` because that one is keyed by shape and therefore
    // only remembers the last id for a given shape.
    private readonly Dictionary<uint, (uint ComponentTypeId, uint Components)> _vectorShapesById = new();

    // First-wins caches.
    private readonly Dictionary<(uint TypeId, uint Value), uint> _constants = new();
    private readonly Dictionary<(uint StorageClass, uint Pointee), uint> _pointers = new();
    private readonly Dictionary<(uint ElementTypeId, uint LengthConstantId), uint> _moduleArrays = new();

    // Self-populated only — see InternDecoratedArray.
    private readonly Dictionary<(uint ElementTypeId, uint LengthConstantId, int Stride), uint> _decoratedArrays = new();

    public SpirvTypeInterner(SpirvModule module)
    {
        _module = module;
        Seed();
    }

    public uint FloatTypeId => _floatTypeId;
    public uint IntTypeId => _intTypeId;
    public uint UIntTypeId => _uintTypeId;

    private void Seed()
    {
        List<SpirvInstruction> instructions = _module.Instructions;

        // A vector's element kind is decided against the scalar ids known SO FAR
        // in this same forward walk — matching the original single-pass seeding.
        for (int i = 0; i < instructions.Count; i++)
        {
            SpirvInstruction instruction = instructions[i];
            Span<uint> words = instruction.Words;

            switch (instruction.OpCode)
            {
                case SpvOpCode.OpTypeFloat when words.Length >= 3 && words[2] == 32:
                    _floatTypeId = words[1];
                    break;

                case SpvOpCode.OpTypeInt when words.Length >= 4 && words[2] == 32:
                    if (words[3] == 1) _intTypeId = words[1];
                    else _uintTypeId = words[1];
                    break;

                case SpvOpCode.OpTypeVector when words.Length >= 4:
                    _vectorShapesById[words[1]] = (words[2], words[3]);
                    if (TryClassifyScalar(words[2], out ScalarKind kind))
                    {
                        _vectors[(kind, (int)words[3])] = words[1];
                    }
                    break;

                case SpvOpCode.OpTypePointer when words.Length >= 4:
                    _pointers.TryAdd((words[2], words[3]), words[1]);
                    break;

                case SpvOpCode.OpConstant when words.Length >= 4:
                    _constants.TryAdd((words[1], words[3]), words[2]);
                    break;

                case SpvOpCode.OpTypeArray when words.Length >= 4:
                    _moduleArrays.TryAdd((words[2], words[3]), words[1]);
                    break;
            }
        }

        // Matrices resolve their row count through the by-id vector table, so a
        // matrix whose column type is a shadowed duplicate still classifies.
        // Only float matrices participate — the layout model never produces an
        // integer matrix member.
        for (int i = 0; i < instructions.Count; i++)
        {
            SpirvInstruction instruction = instructions[i];
            if (instruction.OpCode != SpvOpCode.OpTypeMatrix)
            {
                continue;
            }

            Span<uint> words = instruction.Words;
            if (words.Length < 4
                || !_vectorShapesById.TryGetValue(words[2], out (uint ComponentTypeId, uint Components) shape)
                || shape.ComponentTypeId != _floatTypeId)
            {
                continue;
            }

            _matrices[(checked((int)shape.Components), checked((int)words[3]))] = words[1];
        }
    }

    private bool TryClassifyScalar(uint componentTypeId, out ScalarKind kind)
    {
        if (componentTypeId != 0 && componentTypeId == _floatTypeId) { kind = ScalarKind.Float; return true; }
        if (componentTypeId != 0 && componentTypeId == _intTypeId) { kind = ScalarKind.Int; return true; }
        if (componentTypeId != 0 && componentTypeId == _uintTypeId) { kind = ScalarKind.UInt; return true; }
        kind = default;
        return false;
    }

    // ---- scalars -----------------------------------------------------------

    public uint EnsureScalar(ScalarKind kind) => kind switch
    {
        ScalarKind.Float => EnsureFloat(),
        ScalarKind.Int => EnsureInt(),
        ScalarKind.UInt => EnsureUInt(),
        _ => 0,
    };

    public uint EnsureFloat()
    {
        if (_floatTypeId != 0) return _floatTypeId;
        uint id = _module.AllocateId();
        _module.AppendType(_module.CreateInstruction(SpvOpCode.OpTypeFloat,
            [SpvOpCode.MakeInstructionWord(SpvOpCode.OpTypeFloat, 3), id, 32]));
        return _floatTypeId = id;
    }

    public uint EnsureInt()
    {
        if (_intTypeId != 0) return _intTypeId;
        uint id = _module.AllocateId();
        _module.AppendType(_module.CreateInstruction(SpvOpCode.OpTypeInt,
            [SpvOpCode.MakeInstructionWord(SpvOpCode.OpTypeInt, 4), id, 32, 1]));
        return _intTypeId = id;
    }

    public uint EnsureUInt()
    {
        if (_uintTypeId != 0) return _uintTypeId;
        uint id = _module.AllocateId();
        _module.AppendType(_module.CreateInstruction(SpvOpCode.OpTypeInt,
            [SpvOpCode.MakeInstructionWord(SpvOpCode.OpTypeInt, 4), id, 32, 0]));
        return _uintTypeId = id;
    }

    // ---- aggregates --------------------------------------------------------

    public uint EnsureVector(ScalarKind kind, int components)
    {
        if (components == 1)
        {
            return EnsureScalar(kind);
        }

        if (_vectors.TryGetValue((kind, components), out uint existing) && existing != 0)
        {
            return existing;
        }

        uint componentTypeId = EnsureScalar(kind);
        uint id = _module.AllocateId();
        _module.AppendType(_module.CreateInstruction(SpvOpCode.OpTypeVector,
            [SpvOpCode.MakeInstructionWord(SpvOpCode.OpTypeVector, 4), id, componentTypeId, (uint)components]));
        _vectors[(kind, components)] = id;
        return id;
    }

    public uint EnsureMatrix(int rows, int columns)
    {
        if (_matrices.TryGetValue((rows, columns), out uint existing) && existing != 0)
        {
            return existing;
        }

        uint columnVectorTypeId = EnsureVector(ScalarKind.Float, rows);
        uint id = _module.AllocateId();
        _module.AppendType(_module.CreateInstruction(SpvOpCode.OpTypeMatrix,
            [SpvOpCode.MakeInstructionWord(SpvOpCode.OpTypeMatrix, 4), id, columnVectorTypeId, (uint)columns]));
        _matrices[(rows, columns)] = id;
        return id;
    }

    /// <summary>
    /// Array type for a rewritten cbuffer member, carrying an explicit
    /// <c>ArrayStride</c>.
    ///
    /// Deliberately does NOT reuse an array that was already in the module, even
    /// when element type and length match: an existing array may have no stride
    /// decoration at all (a Private-scope lookup table, say), and handing that id
    /// to a block member makes spirv-cross reject the module with "Struct member
    /// does not have ArrayStride set". Only arrays this interner minted — and
    /// therefore knows are correctly decorated — are reused.
    /// </summary>
    public uint InternDecoratedArray(uint elementTypeId, int length, int stride)
    {
        uint lengthConstantId = InternUIntConstant(checked((uint)length));

        (uint, uint, int) key = (elementTypeId, lengthConstantId, stride);
        if (_decoratedArrays.TryGetValue(key, out uint cached) && cached != 0)
        {
            return cached;
        }

        uint id = _module.AllocateId();
        _module.PrependDecoration(_module.CreateInstruction(SpvOpCode.OpDecorate,
            [SpvOpCode.MakeInstructionWord(SpvOpCode.OpDecorate, 4), id, Decoration.ArrayStride, (uint)stride]));
        _module.AppendType(_module.CreateInstruction(SpvOpCode.OpTypeArray,
            [SpvOpCode.MakeInstructionWord(SpvOpCode.OpTypeArray, 4), id, elementTypeId, lengthConstantId]));

        _decoratedArrays[key] = id;
        return id;
    }

    /// <summary>
    /// Array type for the scalar-block vectoriser, which DOES reuse a matching
    /// array already present in the module. Safe there because the vectoriser
    /// only ever produces the canonical <c>float4[N]</c> stride-16 shape, so a
    /// pre-existing match is layout-compatible by construction.
    /// </summary>
    public uint InternModuleArray(uint elementTypeId, int length, uint stride)
    {
        uint lengthConstantId = InternUIntConstant(checked((uint)length));

        if (_moduleArrays.TryGetValue((elementTypeId, lengthConstantId), out uint existing) && existing != 0)
        {
            return existing;
        }

        uint id = _module.AllocateId();
        _module.PrependDecoration(_module.CreateInstruction(SpvOpCode.OpDecorate,
            [SpvOpCode.MakeInstructionWord(SpvOpCode.OpDecorate, 4), id, Decoration.ArrayStride, stride]));
        _module.AppendType(_module.CreateInstruction(SpvOpCode.OpTypeArray,
            [SpvOpCode.MakeInstructionWord(SpvOpCode.OpTypeArray, 4), id, elementTypeId, lengthConstantId]));

        _moduleArrays[(elementTypeId, lengthConstantId)] = id;
        return id;
    }

    // ---- pointers ----------------------------------------------------------

    public uint InternUniformPointer(uint pointeeTypeId) => InternPointer(StorageClass.Uniform, pointeeTypeId);

    public uint InternPointer(uint storageClass, uint pointeeTypeId)
    {
        if (_pointers.TryGetValue((storageClass, pointeeTypeId), out uint existing) && existing != 0)
        {
            return existing;
        }

        uint id = _module.AllocateId();
        _module.AppendType(_module.CreateInstruction(SpvOpCode.OpTypePointer,
            [SpvOpCode.MakeInstructionWord(SpvOpCode.OpTypePointer, 4), id, storageClass, pointeeTypeId]));
        _pointers[(storageClass, pointeeTypeId)] = id;
        return id;
    }

    // ---- constants ---------------------------------------------------------

    public uint InternUIntConstant(uint value)
    {
        uint uintTypeId = EnsureUInt();
        if (_constants.TryGetValue((uintTypeId, value), out uint existing) && existing != 0)
        {
            return existing;
        }

        uint id = _module.AllocateId();
        _module.AppendType(_module.CreateInstruction(SpvOpCode.OpConstant,
            [SpvOpCode.MakeInstructionWord(SpvOpCode.OpConstant, 4), uintTypeId, id, value]));
        _constants[(uintTypeId, value)] = id;
        return id;
    }
}
