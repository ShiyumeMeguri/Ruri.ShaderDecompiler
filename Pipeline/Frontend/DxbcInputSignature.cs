using System.Buffers.Binary;
using System.Text;

namespace Ruri.ShaderTools.Pipeline.Frontend;

/// <summary>One entry of a shader's input signature.</summary>
/// <param name="Register">Input register index. Matches the SPIR-V <c>Location</c>
/// the translator assigns, which is what lets the two be joined.</param>
internal readonly record struct InputSignatureElement(string SemanticName, uint SemanticIndex, uint Register);

/// <summary>
/// Reads the input signature out of a DXBC container.
///
/// WHY THIS EXISTS: a vertex shader's real attribute semantics — <c>POSITION</c>,
/// <c>NORMAL</c>, <c>TANGENT</c>, <c>TEXCOORD3</c> — are recorded in the DXBC
/// container's signature chunk and survive every strip a shipping build applies.
/// Translation to SPIR-V drops them, keeping only a numeric <c>Location</c>, so
/// the emitted source names every input <c>TEXCOORD&lt;location&gt;</c>.
///
/// That is not cosmetic. Engines bind vertex buffers BY SEMANTIC NAME, so a
/// position declared as <c>TEXCOORD0</c> receives the first UV stream: the shader
/// compiles, and renders garbage. Recovering the signature turns a guess into a
/// lookup of data that was there all along.
/// </summary>
internal static class DxbcInputSignature
{
    private const uint DxbcMagic = 0x43425844;      // 'DXBC'
    private const uint FourCcIsgn = 0x4E475349;     // 'ISGN' — classic input signature
    private const uint FourCcIsg1 = 0x31475349;     // 'ISG1' — SM5.1 form, wider records

    private const int ChunkCountOffset = 28;
    private const int ChunkTableOffset = 32;
    private const int MaxPlausibleChunkCount = 256;

    /// <summary>Bytes per element: 6 DWORDs for ISGN, 8 for the SM5.1 form
    /// (a leading stream index and a trailing min-precision field).</summary>
    private const int IsgnElementSize = 24;
    private const int Isg1ElementSize = 32;

    /// <summary>Guard against a corrupt count driving a huge allocation.</summary>
    private const int MaxPlausibleElementCount = 128;

    /// <summary>
    /// Parse the input signature, or return an empty list when the binary is not
    /// a DXBC container or carries no signature chunk. Never throws — a malformed
    /// chunk means "no semantics recovered", which degrades to the previous
    /// behaviour rather than failing the shader.
    /// </summary>
    public static IReadOnlyList<InputSignatureElement> Read(ReadOnlySpan<byte> binary)
    {
        if (binary.Length < ChunkTableOffset
            || BinaryPrimitives.ReadUInt32LittleEndian(binary) != DxbcMagic)
        {
            return Array.Empty<InputSignatureElement>();
        }

        int chunkCount = BinaryPrimitives.ReadInt32LittleEndian(binary[ChunkCountOffset..]);
        if (chunkCount <= 0 || chunkCount > MaxPlausibleChunkCount
            || ChunkTableOffset + (chunkCount * 4) > binary.Length)
        {
            return Array.Empty<InputSignatureElement>();
        }

        for (int i = 0; i < chunkCount; i++)
        {
            int chunkOffset = BinaryPrimitives.ReadInt32LittleEndian(binary[(ChunkTableOffset + (i * 4))..]);
            if (chunkOffset < 0 || chunkOffset + 8 > binary.Length)
            {
                continue;
            }

            uint fourCc = BinaryPrimitives.ReadUInt32LittleEndian(binary[chunkOffset..]);
            if (fourCc != FourCcIsgn && fourCc != FourCcIsg1)
            {
                continue;
            }

            int chunkSize = BinaryPrimitives.ReadInt32LittleEndian(binary[(chunkOffset + 4)..]);
            int dataOffset = chunkOffset + 8;
            if (chunkSize < 8 || dataOffset + chunkSize > binary.Length)
            {
                continue;
            }

            return ReadElements(binary.Slice(dataOffset, chunkSize), fourCc == FourCcIsg1 ? Isg1ElementSize : IsgnElementSize);
        }

        return Array.Empty<InputSignatureElement>();
    }

    // Chunk data layout: [elementCount][uniqueKey][element × elementCount].
    // Every name offset is relative to the START OF THIS SPAN, which is why the
    // whole chunk is sliced before parsing rather than indexed in place.
    private static IReadOnlyList<InputSignatureElement> ReadElements(ReadOnlySpan<byte> data, int elementSize)
    {
        int elementCount = BinaryPrimitives.ReadInt32LittleEndian(data);
        if (elementCount <= 0 || elementCount > MaxPlausibleElementCount)
        {
            return Array.Empty<InputSignatureElement>();
        }

        // The SM5.1 record prepends a stream index, so every field shifts by one
        // DWORD; everything after that lines up with the classic layout.
        int fieldShift = elementSize == Isg1ElementSize ? 4 : 0;

        var elements = new List<InputSignatureElement>(elementCount);
        for (int i = 0; i < elementCount; i++)
        {
            int record = 8 + (i * elementSize);
            if (record + elementSize > data.Length)
            {
                break;
            }

            ReadOnlySpan<byte> fields = data[(record + fieldShift)..];
            int nameOffset = BinaryPrimitives.ReadInt32LittleEndian(fields);
            uint semanticIndex = BinaryPrimitives.ReadUInt32LittleEndian(fields[4..]);
            uint register = BinaryPrimitives.ReadUInt32LittleEndian(fields[16..]);

            string? name = ReadNulTerminated(data, nameOffset);
            if (name is null)
            {
                continue;
            }

            elements.Add(new InputSignatureElement(name, semanticIndex, register));
        }

        return elements;
    }

    private static string? ReadNulTerminated(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || offset >= data.Length)
        {
            return null;
        }

        ReadOnlySpan<byte> tail = data[offset..];
        int terminator = tail.IndexOf((byte)0);
        if (terminator <= 0)
        {
            return null;
        }

        return Encoding.ASCII.GetString(tail[..terminator]);
    }
}
