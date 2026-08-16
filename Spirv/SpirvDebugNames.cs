using System.Text;

namespace Ruri.ShaderTools.Spirv;

// Encoder for the two debug-name instructions the symbol pipeline emits.
//
// SPIR-V literal strings are NUL-terminated and zero-padded up to a 4-byte word
// boundary. Both entry points below produce the complete instruction including
// its opcode/word-count header at word 0.
//
// Single implementation on purpose: the encoding previously existed twice — once
// producing a loose `uint[]` for the patch splicer, once producing a module-owned
// instruction for the rewriter — and the two had to be kept byte-identical by
// hand. They now share one encoder; the splicer takes the raw words, the rewriter
// takes a module-allocated instruction.
internal static class SpirvDebugNames
{
    // Longest name we encode without touching the heap. Shader symbol names are
    // author identifiers; 256 bytes covers every real one (CJK material params
    // included) with room to spare.
    private const int StackNameBudget = 256;

    public static uint[] EncodeName(uint targetId, string name)
    {
        int payloadWords = PaddedWordCount(name);
        uint[] instruction = new uint[2 + payloadWords];
        WriteName(instruction, targetId, name, payloadWords);
        return instruction;
    }

    public static uint[] EncodeMemberName(uint structTypeId, uint memberIndex, string name)
    {
        int payloadWords = PaddedWordCount(name);
        uint[] instruction = new uint[3 + payloadWords];
        WriteMemberName(instruction, structTypeId, memberIndex, name, payloadWords);
        return instruction;
    }

    public static SpirvInstruction CreateName(SpirvModule module, uint targetId, string name)
    {
        int payloadWords = PaddedWordCount(name);
        int total = 2 + payloadWords;
        Span<uint> words = total <= 64 ? stackalloc uint[total] : new uint[total];
        WriteName(words, targetId, name, payloadWords);
        return module.CreateInstruction(SpvOpCode.OpName, words);
    }

    public static SpirvInstruction CreateMemberName(SpirvModule module, uint structTypeId, uint memberIndex, string name)
    {
        int payloadWords = PaddedWordCount(name);
        int total = 3 + payloadWords;
        Span<uint> words = total <= 64 ? stackalloc uint[total] : new uint[total];
        WriteMemberName(words, structTypeId, memberIndex, name, payloadWords);
        return module.CreateInstruction(SpvOpCode.OpMemberName, words);
    }

    private static void WriteName(Span<uint> words, uint targetId, string name, int payloadWords)
    {
        words[0] = SpvOpCode.MakeInstructionWord(SpvOpCode.OpName, (ushort)(2 + payloadWords));
        words[1] = targetId;
        WritePayload(words[2..], name, payloadWords);
    }

    private static void WriteMemberName(Span<uint> words, uint structTypeId, uint memberIndex, string name, int payloadWords)
    {
        words[0] = SpvOpCode.MakeInstructionWord(SpvOpCode.OpMemberName, (ushort)(3 + payloadWords));
        words[1] = structTypeId;
        words[2] = memberIndex;
        WritePayload(words[3..], name, payloadWords);
    }

    private static int PaddedWordCount(string name) => (Encoding.UTF8.GetByteCount(name) + 1 + 3) / 4;

    private static void WritePayload(Span<uint> destination, string name, int payloadWords)
    {
        int byteCount = payloadWords * 4;
        Span<byte> padded = byteCount <= StackNameBudget ? stackalloc byte[byteCount] : new byte[byteCount];
        padded.Clear();
        Encoding.UTF8.GetBytes(name, padded);
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(padded).CopyTo(destination[..payloadWords]);
    }
}
