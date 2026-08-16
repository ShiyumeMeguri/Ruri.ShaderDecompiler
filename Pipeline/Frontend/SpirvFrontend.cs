using Ruri.ShaderTools.Pipeline.Native;

namespace Ruri.ShaderTools.Pipeline.Frontend;

/// <summary>
/// Compiled shader binary → SPIR-V, the pipeline's single intermediate form.
///
/// Everything downstream — layout structuring, symbol injection, source emission
/// — speaks only SPIR-V. This is the one place that knows any other format
/// exists, which is why adding a new input container means adding a case here and
/// nothing else.
///
/// Legacy DXBC needs no separate conversion step: the translator detects the
/// container and routes it through its own bundled legacy path.
/// </summary>
public sealed class SpirvFrontend
{
    /// <summary>
    /// Convert a shader binary to pipeline-normalised SPIR-V, detecting its
    /// container format.
    ///
    /// The public entry point for hosts that need SPIR-V for their OWN analysis
    /// rather than a full decompile — recovering register assignments from a
    /// reflection-stripped blob, say. Returns null instead of throwing, because a
    /// host doing bulk analysis wants to skip a bad blob, not unwind.
    ///
    /// NORMALISATION IS INCLUDED, and that is the point of this method existing.
    /// The legacy DXBC front end lowers a constant buffer to a SCALAR float array:
    /// what looks like a register read is really <c>scalarIndex &gt;&gt; 2</c> with
    /// <c>scalarIndex &amp; 3</c> selecting the component. A caller that converts
    /// and then reads indices as if they were already vec4 registers is off by a
    /// factor of four — while declared byte sizes, which do not depend on the
    /// indexing scheme, still look plausible. Requiring every caller to remember a
    /// second step invites exactly that bug, so the step is not optional here.
    ///
    /// Thread-safe and context-per-call: safe to run across a parallel loop.
    /// </summary>
    public static byte[]? TryConvert(byte[] binary, out string? error)
    {
        error = null;
        if (binary is null || binary.Length == 0)
        {
            error = "Shader binary is empty.";
            return null;
        }

        try
        {
            var frontend = new SpirvFrontend();
            byte[] spirv = frontend.Convert(ShaderBinaryFormatDetector.Detect(ShaderBinaryFormat.Unknown, binary), binary);
            return Spirv.ScalarLayout.ScalarBlockVectorizer.Vectorize(spirv);
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return null;
        }
    }

    /// <summary>Upstream message from the most recent failed conversion.</summary>
    public string? LastFailure { get; private set; }

    public byte[] Convert(ShaderBinaryFormat format, byte[] binary) => format switch
    {
        ShaderBinaryFormat.Dxbc => ConvertDxbc(binary),
        ShaderBinaryFormat.Dxil => RecoverInputSemantics(ConvertDxil(binary, ShaderBinaryFormatDetector.IsRawLlvmBitcode(binary)), binary),
        ShaderBinaryFormat.SpirV => binary,
        _ => throw new InvalidOperationException($"Unsupported shader format: {format}"),
    };

    private byte[] ConvertDxbc(byte[] dxbc)
    {
        if (!ShaderBinaryFormatDetector.IsDxbc(dxbc))
        {
            throw new InvalidOperationException("Input does not contain a valid DXBC payload.");
        }

        return RecoverInputSemantics(ConvertDxil(dxbc, rawLlvm: false), dxbc);
    }

    /// <summary>
    /// Put the vertex attribute semantics back, from the container's own input
    /// signature.
    ///
    /// Translation keeps only a numeric location for each input, so without this
    /// every attribute emits as <c>TEXCOORD&lt;location&gt;</c> — and an engine
    /// that binds vertex buffers by semantic name then feeds the first UV stream
    /// into the position slot. The shader compiles and renders garbage, which is
    /// far harder to notice than a failure.
    ///
    /// The signature is authoritative data that survives every strip a shipping
    /// build applies; it was simply being discarded. No-op for a non-DXBC
    /// container or a shader with no signature chunk.
    /// </summary>
    private static byte[] RecoverInputSemantics(byte[] spirv, ReadOnlySpan<byte> container)
        => InputSemanticNamer.Apply(spirv, DxbcInputSignature.Read(container));

    private byte[] ConvertDxil(byte[] dxil, bool rawLlvm)
    {
        byte[]? spirv = DxilSpirvLibrary.Convert(dxil, rawLlvm, out string? error);
        if (spirv is null)
        {
            LastFailure = error;
            throw new InvalidOperationException($"dxil-spirv did not produce a SPIR-V module. {error}");
        }

        return spirv;
    }
}
