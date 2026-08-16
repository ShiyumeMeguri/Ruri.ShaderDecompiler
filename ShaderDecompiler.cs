using Ruri.ShaderTools.Pipeline.Batching;
using Ruri.ShaderTools.Pipeline.Frontend;
using Ruri.ShaderTools.Pipeline.Native;
using Ruri.ShaderTools.Pipeline;

namespace Ruri.ShaderTools;

/// <summary>
/// Compiled shader binary → readable high-level source, with the engine's own
/// symbol names restored.
///
/// The premise: shader bytecode does not destroy semantic information, it merely
/// strips the names. Engines must keep a name → binding mapping to set parameters
/// at runtime, so the names still exist somewhere — just not in the bytecode.
/// This library takes that mapping (a <see cref="SerializedProgramData"/> the host
/// builds from whatever the engine exposes), injects it back into the shader's
/// intermediate form, and emits source that reads the way it was written.
///
/// Nothing here knows about any specific engine. Everything engine-shaped lives
/// upstream, in whoever fills in the symbol table.
///
/// This type is a FAÇADE. It owns no algorithm — the route lives in
/// <see cref="DecompilePipeline"/>, and each step of it in its own namespace
/// (<c>Frontend</c>, <c>Transforms</c>, <c>Naming</c>, <c>Backend</c>). Keep it
/// that way: this file existing at 1000 lines was the original architectural
/// problem.
///
/// One instance is NOT thread-safe. The batch overload manages a pool for you.
/// </summary>
public sealed class ShaderDecompiler : IDisposable
{
    /// <summary>
    /// Stop admitting new concurrent work once system CPU passes this. Leaves
    /// headroom on a machine someone is actually using.
    /// </summary>
    private const int DefaultCpuCapPercent = 80;

    private readonly DecompilePipeline _pipeline = new();
    private bool _disposed;

    /// <param name="nativeLibraryDirectory">
    /// Extra directory to probe for the native translation libraries. Normally
    /// unnecessary — they resolve from the package-restored runtime folder — and
    /// only useful for a host that ships them somewhere unusual.
    /// </param>
    public ShaderDecompiler(string? nativeLibraryDirectory = null)
        => NativeLibraryResolver.EnsureInitialized(nativeLibraryDirectory);

    /// <summary>Decompile one shader.</summary>
    public DecompileResult Decompile(byte[] binary, DecompileOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return binary is null || binary.Length == 0
            ? new DecompileResult { Success = false, ErrorMessage = "Shader binary is empty." }
            : _pipeline.Run(binary, options);
    }

    /// <summary>Decompile one shader with the common options inline.</summary>
    public DecompileResult Decompile(
        byte[] binary,
        ShaderBinaryFormat format = ShaderBinaryFormat.Unknown,
        SerializedProgramData? symbols = null,
        uint shaderModel = 51)
        => Decompile(binary, new DecompileOptions { Format = format, Symbols = symbols, ShaderModel = shaderModel });

    /// <summary>
    /// Decompile any number of shaders. A single request runs inline on this
    /// instance; several fan out across a worker pool whose concurrency grows
    /// while the machine has CPU headroom, so a bulk export does not take the
    /// machine with it.
    /// </summary>
    /// <param name="requests">Result index matches request index.</param>
    /// <param name="onProgress">
    /// Fires per completion, ON A WORKER THREAD. Serialise any external state
    /// yourself.
    /// </param>
    /// <param name="maxConcurrency">Hard ceiling on concurrent jobs. ≤ 0 → processor count × 2.</param>
    /// <param name="cpuUsageCapPercent">Stop admitting work above this system CPU usage.</param>
    public DecompileResult[] Decompile(
        IReadOnlyList<(byte[] Binary, DecompileOptions Options)> requests,
        Action<int, DecompileResult>? onProgress = null,
        int maxConcurrency = 0,
        int cpuUsageCapPercent = DefaultCpuCapPercent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);

        if (requests.Count == 0)
        {
            return Array.Empty<DecompileResult>();
        }

        var results = new DecompileResult[requests.Count];

        // Single request: reuse this instance and skip the pool entirely.
        if (requests.Count == 1)
        {
            (byte[] binary, DecompileOptions options) = requests[0];
            results[0] = Decompile(binary, options);
            onProgress?.Invoke(0, results[0]);
            return results;
        }

        ShaderDecompileBatch.Run(requests, results, onProgress, maxConcurrency, cpuUsageCapPercent, cancellationToken);
        return results;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
