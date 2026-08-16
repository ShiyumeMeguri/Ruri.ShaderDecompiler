using System.Text;

namespace Ruri.ShaderTools.Spirv;

// SPIR-V literal-string decoding and raw word/byte conversion.
//
// A SPIR-V literal string is packed little-endian into consecutive words and
// NUL-terminated; the tail of the final word is zero padding. Every reader in
// the codebase previously rolled its own variant of this loop (three of them,
// with subtly different bounds handling). One implementation, span-based, no
// allocation on the normal path.
internal static class SpirvLiteral
{
    // Entry-point names, resource names and member names are identifiers; 256
    // bytes covers every real one. Longer strings fall back to the heap.
    private const int StackBudget = 256;

    /// Decode the NUL-terminated literal starting at <paramref name="start"/>.
    public static string ReadString(ReadOnlySpan<uint> words, int start)
    {
        if (start >= words.Length)
        {
            return string.Empty;
        }

        ReadOnlySpan<byte> bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(words[start..]);
        int terminator = bytes.IndexOf((byte)0);
        if (terminator < 0)
        {
            terminator = bytes.Length;
        }
        return Encoding.UTF8.GetString(bytes[..terminator]);
    }

    /// Decode a literal that is known to occupy exactly <paramref name="wordCount"/>
    /// words (the trailing-operand form used by OpName / OpMemberName scans).
    public static string ReadString(ReadOnlySpan<uint> words, int start, int wordCount)
    {
        int available = Math.Min(wordCount, words.Length - start);
        return available <= 0 ? string.Empty : ReadString(words.Slice(0, start + available), start);
    }

    public static uint[] BytesToWords(ReadOnlySpan<byte> bytes)
    {
        uint[] words = new uint[bytes.Length / 4];
        bytes[..(words.Length * 4)].CopyTo(System.Runtime.InteropServices.MemoryMarshal.AsBytes(words.AsSpan()));
        return words;
    }

    public static byte[] WordsToBytes(ReadOnlySpan<uint> words)
    {
        byte[] bytes = new byte[words.Length * 4];
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(words).CopyTo(bytes);
        return bytes;
    }

    /// UTF-8 byte budget check used by callers that want to avoid the heap.
    public static bool FitsOnStack(string value) => Encoding.UTF8.GetByteCount(value) < StackBudget;
}
