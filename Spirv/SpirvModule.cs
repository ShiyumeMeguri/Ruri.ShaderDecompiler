using System.Runtime.CompilerServices;

namespace Ruri.ShaderTools.Spirv;

// ============================================================================
// The SPIR-V "world": a data-oriented module representation.
//
// Storage model — chunked word arena + instruction handles:
//
//   chunk 0 : the parsed module's words, verbatim (one allocation, one copy)
//   chunk n : bump-allocated overflow for instructions whose word count grew
//
//   SpirvInstruction is a HANDLE — (chunk, start, length) plus the opcode.
//   It owns no array. Reading operands is a `Span<uint>` over the chunk, so a
//   full module walk allocates nothing at all.
//
// Why chunks and not one growable array: a `Span<uint>` handed out by
// `SpirvInstruction.Words` must stay valid while other instructions are
// resized. A single array would have to be reallocated on growth, silently
// invalidating every live span. Chunks are append-only and never moved, so a
// span obtained before an unrelated edit still points at live storage.
//
// What this replaces: the previous design gave every instruction its own
// `uint[]`, produced by `words.Skip(offset).Take(wordCount).ToArray()` during
// parse. A 10k-instruction module cost 10k arrays + 20k LINQ enumerators
// before a single transform ran. Now it costs one array and one memcpy.
//
// Mutation model:
//   * `Nop()`      — shrinks in place (length 1). Free, never touches the arena.
//   * `SetWords()` — same length rewrites in place; a longer instruction takes
//                    a fresh arena slice. Old storage is abandoned (arena is
//                    bump-only); serialization walks the instruction table, so
//                    abandoned words are never emitted.
//   * inserts      — plain `List<SpirvInstruction>` inserts at the three
//                    section anchors below.
//
// Byte-exactness contract: `ToBytes` writes the header followed by every
// instruction in table order, recomputing word 0 from (OpCode, word count) —
// including OpNop placeholders, which are emitted, not elided. Section anchors
// (`FindTypeSectionEndIndex` / `FindFirstTypeInstructionIndex` /
// `FindDebugInsertionIndex`) reproduce the original scan semantics exactly,
// because insertion ORDER at those anchors is load-bearing for the emitted
// byte layout (repeated inserts at the decoration anchor land in reverse call
// order; at the debug anchor they append in call order).
// ============================================================================
internal sealed class SpirvModule
{
    /// <summary>SPIR-V module magic number (word 0).</summary>
    public const uint MagicNumber = 0x07230203;

    /// <summary>Magic, version, generator, id bound, reserved.</summary>
    public const int HeaderWordCount = 5;

    /// <summary>Word 3 of the header: the exclusive upper bound on result ids.</summary>
    private const int IdBoundWord = 3;

    // Overflow chunks are sized so a typical rewrite never needs more than a
    // couple of them, while a pathological one-instruction-per-chunk case still
    // costs O(1) allocations per oversized instruction.
    private const int DefaultChunkWords = 8192;

    private readonly List<uint[]> _chunks = new(2);
    private int _tailChunk;
    private int _tailUsed;
    private uint _maxResultId;

    public uint[] Header { get; private set; } = Array.Empty<uint>();
    public List<SpirvInstruction> Instructions { get; } = new();

    private SpirvModule() { }

    // ---- parse / serialize -------------------------------------------------

    public static SpirvModule Parse(byte[] bytes) => Parse((ReadOnlySpan<byte>)bytes);

    public static SpirvModule Parse(ReadOnlySpan<byte> bytes)
    {
        int wordCount = bytes.Length / 4;
        if (wordCount < HeaderWordCount)
        {
            throw new ArgumentException("Invalid SPIR-V binary.");
        }

        uint[] words = new uint[wordCount];
        bytes[..(wordCount * 4)].CopyTo(System.Runtime.InteropServices.MemoryMarshal.AsBytes(words.AsSpan()));

        if (words[0] != MagicNumber)
        {
            throw new ArgumentException("Invalid SPIR-V binary.");
        }

        var module = new SpirvModule();
        module._chunks.Add(words);
        module._tailChunk = 0;
        module._tailUsed = words.Length;   // chunk 0 is fully occupied by the parse
        module.Header = words.AsSpan(0, HeaderWordCount).ToArray();

        int offset = HeaderWordCount;
        while (offset < words.Length)
        {
            uint word0 = words[offset];
            ushort opCode = SpvOpCode.GetOpCode(word0);
            ushort length = SpvOpCode.GetWordCount(word0);
            if (length == 0 || offset + length > words.Length)
            {
                break;
            }

            var instruction = new SpirvInstruction(module, opCode, chunk: 0, start: offset, length: length, offset: offset);
            module.Instructions.Add(instruction);
            module.ObserveResultId(instruction);
            offset += length;
        }

        return module;
    }

    public byte[] ToBytes()
    {
        int total = Header.Length;
        List<SpirvInstruction> instructions = Instructions;
        for (int i = 0; i < instructions.Count; i++)
        {
            total += instructions[i].Length;
        }

        byte[] bytes = new byte[total * 4];
        Span<uint> output = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(bytes.AsSpan());
        Header.CopyTo(output);

        int cursor = Header.Length;
        for (int i = 0; i < instructions.Count; i++)
        {
            SpirvInstruction instruction = instructions[i];
            Span<uint> source = instruction.Words;
            // Word 0 is authoritative-by-reconstruction: the opcode field and the
            // handle's length win over whatever the slot happens to hold, so an
            // in-place shrink (Nop) or a SetWords never leaves a stale header.
            source[0] = SpvOpCode.MakeInstructionWord(instruction.OpCode, (ushort)instruction.Length);
            source.CopyTo(output[cursor..]);
            cursor += source.Length;
        }

        return bytes;
    }

    // ---- id allocation -----------------------------------------------------

    // Matches the original `Math.Max(Header[3], FindMaxResultId() + 1)` exactly,
    // but the max-result-id side is maintained incrementally instead of being
    // recomputed by a full module walk on every single allocation.
    public uint AllocateId()
    {
        uint nextId = Math.Max(Header[IdBoundWord], _maxResultId + 1);
        Header[IdBoundWord] = nextId + 1;
        if (nextId > _maxResultId)
        {
            _maxResultId = nextId;
        }
        return nextId;
    }

    /// <summary>Exclusive upper bound on result ids — the sizing input for every
    /// id-indexed analysis table.</summary>
    public int IdBound => (int)Math.Max(Header[IdBoundWord], 1u);

    internal void ObserveResultId(SpirvInstruction instruction)
    {
        int? resultIndex = SpvInstructionTraits.GetResultIdIndex(instruction);
        if (resultIndex.HasValue)
        {
            uint id = instruction[resultIndex.Value];
            if (id > _maxResultId)
            {
                _maxResultId = id;
            }
        }
    }

    // ---- arena -------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Span<uint> Slice(int chunk, int start, int length) => _chunks[chunk].AsSpan(start, length);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal uint WordAt(int chunk, int index) => _chunks[chunk][index];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetWordAt(int chunk, int index, uint value) => _chunks[chunk][index] = value;

    // Reserve `length` contiguous words. Never moves previously handed-out
    // storage, so live spans stay valid across unrelated edits.
    private void Reserve(int length, out int chunk, out int start)
    {
        if (_chunks.Count == 0 || _tailUsed + length > _chunks[_tailChunk].Length)
        {
            int size = Math.Max(DefaultChunkWords, length);
            _chunks.Add(new uint[size]);
            _tailChunk = _chunks.Count - 1;
            _tailUsed = 0;
        }

        chunk = _tailChunk;
        start = _tailUsed;
        _tailUsed += length;
    }

    internal void Rewrite(SpirvInstruction instruction, ReadOnlySpan<uint> words)
    {
        if (words.Length <= instruction.Length)
        {
            // Fits (or shrinks) in place — the common case for Nop and for
            // access-chain retargeting that keeps its operand count.
            words.CopyTo(Slice(instruction.Chunk, instruction.Start, words.Length));
            instruction.Length = words.Length;
        }
        else
        {
            Reserve(words.Length, out int chunk, out int start);
            words.CopyTo(Slice(chunk, start, words.Length));
            instruction.Chunk = chunk;
            instruction.Start = start;
            instruction.Length = words.Length;
        }

        instruction.OpCode = SpvOpCode.GetOpCode(words[0]);
        ObserveResultId(instruction);
    }

    // Materialise a brand-new instruction. `Offset` stays 0 — diagnostics
    // distinguish parsed instructions (real stream offset) from synthesized
    // ones exactly as before.
    public SpirvInstruction CreateInstruction(ushort opCode, ReadOnlySpan<uint> words)
    {
        Reserve(words.Length, out int chunk, out int start);
        words.CopyTo(Slice(chunk, start, words.Length));
        var instruction = new SpirvInstruction(this, opCode, chunk, start, words.Length, offset: 0);
        ObserveResultId(instruction);
        return instruction;
    }

    // ---- section anchors ---------------------------------------------------
    //
    // SPIR-V's logical layout gives three insertion regions the transforms use.
    // Their scan semantics are reproduced verbatim from the original module
    // implementation because repeated inserts are stateful: each insert shifts
    // the anchor for the next one, and the resulting order is baked into the
    // emitted bytes.
    //
    //   [header][caps/ext/memory/entry/exec/source][DEBUG NAMES][DECORATIONS][TYPES…][functions]
    //                                                ^append       ^prepend     ^append

    // First OpType* instruction (opcode range OpTypeVoid..OpTypePointer).
    public int FindFirstTypeInstructionIndex()
    {
        List<SpirvInstruction> instructions = Instructions;
        for (int i = 0; i < instructions.Count; i++)
        {
            if (SpvOpCode.IsTypeDeclaration(instructions[i].OpCode))
            {
                return i;
            }
        }
        return instructions.Count;
    }

    // One past the LAST OpType* instruction. Note this can sit before trailing
    // constants / global variables — that is the original behaviour and the
    // reason newly emitted constants land where they do.
    public int FindTypeSectionEndIndex()
    {
        List<SpirvInstruction> instructions = Instructions;
        int lastTypeIndex = -1;
        for (int i = 0; i < instructions.Count; i++)
        {
            if (SpvOpCode.IsTypeDeclaration(instructions[i].OpCode))
            {
                lastTypeIndex = i;
            }
        }
        return lastTypeIndex >= 0 ? lastTypeIndex + 1 : instructions.Count;
    }

    // One past the last OpName / OpMemberName / OpExtInstImport in the LEADING
    // debug block — the scan stops at the first decoration or type instruction.
    public int FindDebugInsertionIndex()
    {
        List<SpirvInstruction> instructions = Instructions;
        int insertIndex = 0;
        for (int i = 0; i < instructions.Count; i++)
        {
            ushort op = instructions[i].OpCode;
            if (op == SpvOpCode.OpName || op == SpvOpCode.OpMemberName || op == SpvOpCode.OpExtInstImport)
            {
                insertIndex = i + 1;
                continue;
            }

            if (op == SpvOpCode.OpDecorate || op == SpvOpCode.OpMemberDecorate || op >= SpvOpCode.OpTypeVoid)
            {
                break;
            }
        }
        return insertIndex;
    }

    /// <summary>Append <paramref name="instruction"/> at the end of the type
    /// section — where every newly synthesized type / constant belongs.</summary>
    public void AppendType(SpirvInstruction instruction) => Instructions.Insert(FindTypeSectionEndIndex(), instruction);

    /// <summary>Insert at the head of the decoration run preceding the type
    /// section. Consecutive calls land in REVERSE call order; that ordering is
    /// part of the emitted byte layout, so do not "fix" it.</summary>
    public void PrependDecoration(SpirvInstruction instruction) => Instructions.Insert(FindDecorationInsertIndex(), instruction);

    /// <inheritdoc cref="PrependDecoration(SpirvInstruction)"/>
    public void PrependDecorations(IEnumerable<SpirvInstruction> instructions) => Instructions.InsertRange(FindDecorationInsertIndex(), instructions);

    // Start of the decoration run that immediately precedes the type section.
    // Consecutive inserts here land in REVERSE call order (each new decoration
    // extends the run backwards) — preserved deliberately.
    public int FindDecorationInsertIndex()
    {
        int index = FindFirstTypeInstructionIndex();
        while (index > 0)
        {
            ushort op = Instructions[index - 1].OpCode;
            if (op != SpvOpCode.OpDecorate && op != SpvOpCode.OpMemberDecorate)
            {
                break;
            }
            index--;
        }
        return index;
    }

    public void InsertDebugName(uint id, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        Instructions.Insert(FindDebugInsertionIndex(), SpirvDebugNames.CreateName(this, id, name));
    }

    public void InsertDebugMemberName(uint typeId, uint memberIndex, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        Instructions.Insert(FindDebugInsertionIndex(), SpirvDebugNames.CreateMemberName(this, typeId, memberIndex, name));
    }
}

// One instruction, as a handle into its module's word arena.
//
// Reference identity is part of the contract: transforms track instructions
// across insertions (`RewrittenLoadInfo.Instruction`, the processed-bitcast
// map, the vectorizer's per-instruction edit plan) precisely because a handle
// stays valid while the surrounding `List<SpirvInstruction>` shifts. Index-
// based tracking would go stale on the first insert.
//
// ⚠ `Words` is a live view. Do not hold one across a `SetWords` on the SAME
// instruction (the slice may move to a fresh chunk). Holding one across edits
// to OTHER instructions is safe — chunks are never reallocated.
internal sealed class SpirvInstruction
{
    private readonly SpirvModule _module;

    internal int Chunk;
    internal int Start;
    internal int Length;

    internal SpirvInstruction(SpirvModule module, ushort opCode, int chunk, int start, int length, int offset)
    {
        _module = module;
        OpCode = opCode;
        Chunk = chunk;
        Start = start;
        Length = length;
        Offset = offset;
    }

    public ushort OpCode { get; set; }

    /// Word offset of this instruction in the ORIGINAL parsed stream; 0 for
    /// instructions synthesized by a transform. Diagnostic only.
    public int Offset { get; }

    public int WordCount => Length;

    public Span<uint> Words
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _module.Slice(Chunk, Start, Length);
    }

    public uint this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _module.WordAt(Chunk, Start + index);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => _module.SetWordAt(Chunk, Start + index, value);
    }

    public void SetWords(ReadOnlySpan<uint> words) => _module.Rewrite(this, words);

    /// Collapse to a 1-word OpNop in place. Emitted (not elided) by
    /// serialization, exactly like the previous implementation.
    public void MakeNop()
    {
        OpCode = SpvOpCode.OpNop;
        Length = 1;
        this[0] = SpvOpCode.MakeInstructionWord(SpvOpCode.OpNop, 1);
    }

    /// Comma-joined raw words, for failure diagnostics. Cold path only.
    public string FormatWords()
    {
        Span<uint> words = Words;
        var builder = new System.Text.StringBuilder(words.Length * 6);
        for (int i = 0; i < words.Length; i++)
        {
            if (i > 0) builder.Append(',');
            builder.Append(words[i]);
        }
        return builder.ToString();
    }
}
