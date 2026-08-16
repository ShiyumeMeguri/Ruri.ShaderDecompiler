using System.Diagnostics;
using Ruri.ShaderTools.Pipeline.Frontend;

namespace Ruri.ShaderTools.Cli;

/// <summary>
/// Re-runs a whole directory of shaders through the pool.
///
/// Built for the "I fixed something, does the failure set shrink?" loop: point it
/// at a tree of failure dumps and it decompiles every one without scripting a
/// per-shader invocation.
/// </summary>
internal static class BatchCommand
{
    /// <summary>
    /// Input stems a failure dump uses. Which one is present says whether the
    /// symbol resolver found anything for that shader; both share the same
    /// on-disk shape.
    /// </summary>
    private static readonly string[] FailureDumpStems = { "with-symbols", "no-symbols" };

    /// <summary>Shader model used for batch reruns.</summary>
    private const uint BatchShaderModel = 51;

    public static int Execute(BatchCommandLine command)
    {
        List<BatchInput> inputs = Discover(command.Root);
        if (inputs.Count == 0)
        {
            Console.Error.WriteLine($"No inputs found under {command.Root}.");
            return 1;
        }

        var requests = new (byte[] Binary, DecompileOptions Options)[inputs.Count];
        for (int i = 0; i < inputs.Count; i++)
        {
            BatchInput input = inputs[i];
            SerializedProgramData? symbols = input.SymbolsPath is null ? null : SymbolSidecar.Load(input.BinaryPath, input.SymbolsPath);

            requests[i] = (File.ReadAllBytes(input.BinaryPath), new DecompileOptions
            {
                Format = ShaderBinaryFormat.Unknown,
                Symbols = symbols,
                ShaderModel = BatchShaderModel,
            });
        }

        Console.WriteLine($"Batch: {inputs.Count} jobs (max concurrency = {(command.MaxConcurrency > 0 ? command.MaxConcurrency : "auto")})");

        int succeeded = 0;
        int failed = 0;
        Stopwatch stopwatch = Stopwatch.StartNew();

        using var decompiler = new ShaderDecompiler();
        DecompileResult[] results = decompiler.Decompile(
            requests,
            (_, result) =>
            {
                if (result.Success) Interlocked.Increment(ref succeeded);
                else Interlocked.Increment(ref failed);
            },
            maxConcurrency: command.MaxConcurrency);

        stopwatch.Stop();

        for (int i = 0; i < results.Length; i++)
        {
            DecompileResult result = results[i];
            if (!result.Success || string.IsNullOrEmpty(result.SourceCode))
            {
                continue;
            }

            string extension = string.IsNullOrEmpty(result.SourceFileExtension) ? ".hlsl" : result.SourceFileExtension;
            File.WriteAllText(inputs[i].BinaryPath + extension, result.SourceCode);
        }

        Console.WriteLine($"Batch done: {succeeded} ok / {failed} fail in {stopwatch.Elapsed.TotalSeconds:F1}s");
        return failed == 0 ? 0 : 2;
    }

    /// <summary>
    /// Two accepted layouts:
    ///   1. failure dumps — <c>&lt;root&gt;/&lt;shader&gt;/{with,no}-symbols.input.bin</c>
    ///   2. flat — <c>&lt;root&gt;/*.bin</c> with an optional sidecar
    ///
    /// A subdirectory whose repaired output already sits beside its input is
    /// skipped, so re-running over a partially fixed tree is a no-op for the parts
    /// already fixed rather than a full redo.
    /// </summary>
    private static List<BatchInput> Discover(string root)
    {
        var inputs = new List<BatchInput>();

        foreach (string subdirectory in Directory.GetDirectories(root))
        {
            foreach (string stem in FailureDumpStems)
            {
                string binary = Path.Combine(subdirectory, stem + ".input.bin");
                if (!File.Exists(binary))
                {
                    continue;
                }

                if (File.Exists(binary + ".hlsl") || File.Exists(binary + ".glsl"))
                {
                    break;   // already repaired
                }

                string symbols = Path.Combine(subdirectory, stem + ".metadata.json");
                inputs.Add(new BatchInput(binary, File.Exists(symbols) ? symbols : null));
                break;   // one stem per subdirectory
            }
        }

        if (inputs.Count > 0)
        {
            return inputs;
        }

        foreach (string binary in Directory.GetFiles(root, "*.bin"))
        {
            inputs.Add(new BatchInput(binary, SymbolSidecar.FindBeside(binary)));
        }

        return inputs;
    }

    private readonly record struct BatchInput(string BinaryPath, string? SymbolsPath);
}
