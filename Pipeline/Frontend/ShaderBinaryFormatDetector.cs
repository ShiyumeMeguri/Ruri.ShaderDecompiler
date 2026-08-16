using Ruri.ShaderTools.Spirv;

namespace Ruri.ShaderTools.Pipeline.Frontend;

/// <summary>
/// Identifies a shader binary from its bytes.
///
/// The one non-obvious rule: DXIL WINS over DXBC. Shader model 6 blobs ship
/// inside a DXBC container so the runtime's existing chunk loader can pick them
/// up, so "starts with DXBC" says nothing on its own — the chunk table has to be
/// walked. Getting this backwards routes SM6 bytecode through the legacy
/// translator, which fails in ways that look like a corrupt input.
/// </summary>
internal static class ShaderBinaryFormatDetector
{
    /// <summary>Offset of the chunk count in a DXBC container header.</summary>
    private const int ChunkCountOffset = 28;

    /// <summary>Offset of the chunk offset table.</summary>
    private const int ChunkTableOffset = 32;

    /// <summary>Guard against a corrupt header driving an absurd table walk.</summary>
    private const int MaxPlausibleChunkCount = 256;

    public static ShaderBinaryFormat Detect(ShaderBinaryFormat declared, ReadOnlySpan<byte> binary) => declared switch
    {
        // Even an explicit "this is DXBC" defers to a DXIL chunk if one is there.
        ShaderBinaryFormat.Dxbc when IsDxil(binary) => ShaderBinaryFormat.Dxil,
        ShaderBinaryFormat.Dxbc or ShaderBinaryFormat.Dxil or ShaderBinaryFormat.SpirV => declared,

        _ when IsDxil(binary) => ShaderBinaryFormat.Dxil,
        _ when IsDxbc(binary) => ShaderBinaryFormat.Dxbc,
        _ when IsSpirv(binary) => ShaderBinaryFormat.SpirV,
        _ => ShaderBinaryFormat.Unknown,
    };

    public static bool IsDxbc(ReadOnlySpan<byte> data)
        => data.Length >= 4 && data[0] == 'D' && data[1] == 'X' && data[2] == 'B' && data[3] == 'C';

    public static bool IsSpirv(ReadOnlySpan<byte> data)
        => data.Length >= 4 && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data) == SpirvModule.MagicNumber;

    /// <summary>
    /// Bare LLVM bitcode magic. Rare — it means a tool handed us a DXIL module
    /// outside its container — but the translator has a dedicated entry point for
    /// it, so it is worth telling apart.
    /// </summary>
    public static bool IsRawLlvmBitcode(ReadOnlySpan<byte> data)
        => data.Length >= 4 && data[0] == 0x42 && data[1] == 0x43 && data[2] == 0xC0 && data[3] == 0xDE;

    public static bool IsDxil(ReadOnlySpan<byte> data)
    {
        if (IsRawLlvmBitcode(data))
        {
            return true;
        }

        if (!IsDxbc(data) || data.Length < ChunkTableOffset)
        {
            return false;
        }

        int chunkCount = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data[ChunkCountOffset..]);
        if (chunkCount <= 0 || chunkCount > MaxPlausibleChunkCount || ChunkTableOffset + (chunkCount * 4) > data.Length)
        {
            return false;
        }

        for (int i = 0; i < chunkCount; i++)
        {
            int offset = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data[(ChunkTableOffset + (i * 4))..]);
            if (offset < 0 || offset + 4 > data.Length)
            {
                continue;
            }

            if (data[offset] == 'D' && data[offset + 1] == 'X' && data[offset + 2] == 'I' && data[offset + 3] == 'L')
            {
                return true;
            }

            // ILDB / ILDN accompany DXIL for debug info. Their presence is an
            // equally strong signal of a shader-model-6 container, and the
            // translator handles those containers fine.
            if (data[offset] == 'I' && data[offset + 1] == 'L' && data[offset + 2] == 'D'
                && (data[offset + 3] == 'B' || data[offset + 3] == 'N'))
            {
                return true;
            }
        }

        return false;
    }
}
