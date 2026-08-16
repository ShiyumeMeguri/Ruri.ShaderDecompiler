using System.Collections.Concurrent;

namespace Ruri.ShaderTools.Pipeline.Batching;

/// <summary>
/// Runs many decompiles across a worker pool.
///
/// Each worker owns its OWN <see cref="ShaderDecompiler"/>. That is not
/// defensive: a decompiler carries per-call state (the structuring log, the last
/// native diagnostic, resolved block names), so sharing one across threads
/// silently cross-contaminates diagnostics between shaders. A private instance
/// per worker costs nothing — construction resolves no resources.
///
/// Work is pulled from one queue rather than partitioned up front, because
/// per-shader cost varies by orders of magnitude and a static split leaves most
/// workers idle behind one pathological shader.
/// </summary>
internal static class ShaderDecompileBatch
{
    public static void Run(
        IReadOnlyList<(byte[] Binary, DecompileOptions Options)> requests,
        DecompileResult[] results,
        Action<int, DecompileResult>? onProgress,
        int maxConcurrency,
        int cpuUsageCapPercent,
        CancellationToken cancellationToken)
    {
        int workerCount = maxConcurrency > 0 ? maxConcurrency : Math.Max(1, Environment.ProcessorCount * 2);

        var queue = new BlockingCollection<int>(boundedCapacity: requests.Count);
        for (int i = 0; i < requests.Count; i++)
        {
            queue.Add(i);
        }
        queue.CompleteAdding();

        using var gate = new CpuAdmissionGate(workerCount, cpuUsageCapPercent, cancellationToken);

        var workers = new Task[workerCount];
        for (int i = 0; i < workerCount; i++)
        {
            workers[i] = Task.Run(() => Work(requests, results, queue, gate, onProgress, gate.MonitorToken), CancellationToken.None);
        }

        try
        {
            Task.WaitAll(workers, cancellationToken);
        }
        finally
        {
            queue.Dispose();
        }
    }

    private static void Work(
        IReadOnlyList<(byte[] Binary, DecompileOptions Options)> requests,
        DecompileResult[] results,
        BlockingCollection<int> queue,
        CpuAdmissionGate gate,
        Action<int, DecompileResult>? onProgress,
        CancellationToken cancellationToken)
    {
        using var decompiler = new ShaderDecompiler();

        while (!cancellationToken.IsCancellationRequested)
        {
            int index;
            try
            {
                if (!queue.TryTake(out index, Timeout.Infinite, cancellationToken))
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (InvalidOperationException)
            {
                return;   // queue completed and drained
            }

            if (!gate.Acquire(cancellationToken))
            {
                return;
            }

            DecompileResult result;
            try
            {
                (byte[] binary, DecompileOptions options) = requests[index];
                result = decompiler.Decompile(binary, options);
            }
            catch (Exception exception)
            {
                // A worker must never take the batch down with it — one broken
                // shader out of thousands is a result, not a crash.
                result = new DecompileResult
                {
                    Success = false,
                    ErrorMessage = $"Worker exception: {exception}",
                    FailedStage = DecompileStage.NotStarted,
                };
            }
            finally
            {
                gate.Release();
            }

            results[index] = result;
            onProgress?.Invoke(index, result);
        }
    }
}
