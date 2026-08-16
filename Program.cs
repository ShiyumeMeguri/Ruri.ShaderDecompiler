using Ruri.ShaderTools.Cli;

namespace Ruri.ShaderTools;

/// <summary>
/// Command-line entry point: dispatch and usage, nothing else.
///
/// The CLI exists for the debug loop — take a dumped shader plus its symbols,
/// reproduce the failure, inspect the intermediates. Bulk library decompilation
/// runs IN-PROCESS from the host that owns the engine data, because that host is
/// the only thing that can build a symbol table in the first place.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || CommandLine.IsHelp(args[0]))
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        try
        {
            if (args[0] == "--batch")
            {
                BatchCommandLine? batch = CommandLine.ParseBatch(args);
                return batch is null ? 1 : BatchCommand.Execute(batch);
            }

            DecompileCommandLine? command = CommandLine.ParseDecompile(args);
            if (command is null)
            {
                PrintUsage();
                return 1;
            }

            return DecompileCommand.Execute(command);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Fatal error: {exception.Message}");
            return 1;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Ruri.ShaderDecompiler — compiled shader binary to readable source");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  ShaderDecompiler <input> [output] [--symbols <path>] [--format dxbc|dxil|spv|auto]");
        Console.WriteLine("                           [--shader-model 50] [--debug-dump <dir>]");
        Console.WriteLine("  ShaderDecompiler --batch <dir> [--max-concurrency N]");
        Console.WriteLine();
        Console.WriteLine("  <input>          shader binary (DXBC, DXIL, or SPIR-V).");
        Console.WriteLine("  [output]         output path. Defaults to <input>.hlsl/.glsl. Use '-' for stdout.");
        Console.WriteLine("  --symbols        engine symbol table (JSON). Auto-loaded from '<input>.metadata.json'.");
        Console.WriteLine("  --format         override container detection (default: auto-detect from magic bytes).");
        Console.WriteLine("  --shader-model   source backend shader model (default: 50).");
        Console.WriteLine("  --debug-dump     directory for per-stage SPIR-V, symbols and the structuring log.");
        Console.WriteLine("  --batch          re-run every shader under a directory through the worker pool.");
    }
}
