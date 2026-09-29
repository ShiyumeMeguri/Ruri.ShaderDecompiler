using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

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
///
/// The queue is fed from the caller's sequence while the workers drain it, and never holds
/// more than a couple of requests per worker, so a sequence that prepares each request as it
/// is read runs ahead of the workers by exactly that much. Each result goes to the caller the
/// moment it exists and is kept nowhere here: what a batch occupies is the work in flight and
/// whatever the caller chooses to keep, never the batch.
/// </summary>
internal static class ShaderDecompileBatch
{
    /// <summary>How many requests wait in the queue per worker, so none of them starves while the sequence prepares the next.</summary>
    private const int QueuedPerWorker = 2;

    public static void Run(
        IEnumerable<(byte[] Binary, DecompileOptions Options)> requests,
        Action<int, DecompileResult> onResult,
        int maxConcurrency,
        int cpuUsageCapPercent,
        CancellationToken cancellationToken)
    {
        // Decompiling is compute from end to end; a worker per core is the whole machine, and two
        // per core only made them take turns.
        int workerCount = maxConcurrency > 0 ? maxConcurrency : Math.Max(1, Environment.ProcessorCount);

        using var queue = new BlockingCollection<(int Index, byte[] Binary, DecompileOptions Options)>(workerCount * QueuedPerWorker);
        using var gate = new CpuAdmissionGate(workerCount, cpuUsageCapPercent, cancellationToken);
        using var halt = CancellationTokenSource.CreateLinkedTokenSource(gate.MonitorToken);

        var workers = new Task[workerCount];
        for (int i = 0; i < workerCount; i++)
        {
            workers[i] = Task.Run(() => Work(queue, gate, onResult, halt), CancellationToken.None);
        }

        ExceptionDispatchInfo? feedFailure = Feed(requests, queue, halt);
        Task.WaitAll(workers, cancellationToken);
        feedFailure?.Throw();
    }

    /// <summary>
    /// The caller's sequence into the queue, in order, on the calling thread. A sequence that
    /// throws stops the workers and is rethrown once they are gone; a worker that throws stops
    /// the sequence, and the wait for the workers reports it.
    /// </summary>
    private static ExceptionDispatchInfo? Feed(
        IEnumerable<(byte[] Binary, DecompileOptions Options)> requests,
        BlockingCollection<(int Index, byte[] Binary, DecompileOptions Options)> queue,
        CancellationTokenSource halt)
    {
        try
        {
            int index = 0;
            foreach ((byte[] binary, DecompileOptions options) in requests)
            {
                queue.Add((index++, binary, options), halt.Token);
            }
            return null;
        }
        catch (OperationCanceledException) when (halt.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception)
        {
            halt.Cancel();
            return ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            queue.CompleteAdding();
        }
    }

    private static void Work(
        BlockingCollection<(int Index, byte[] Binary, DecompileOptions Options)> queue,
        CpuAdmissionGate gate,
        Action<int, DecompileResult> onResult,
        CancellationTokenSource halt)
    {
        using var decompiler = new ShaderDecompiler();

        try
        {
            foreach ((int index, byte[] binary, DecompileOptions options) in queue.GetConsumingEnumerable(halt.Token))
            {
                if (!gate.Acquire(halt.Token))
                {
                    return;
                }

                DecompileResult result;
                try
                {
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

                onResult(index, result);
            }
        }
        catch (OperationCanceledException) when (halt.IsCancellationRequested)
        {
        }
        catch
        {
            halt.Cancel();
            throw;
        }
    }
}
