using Ruri.ShaderTools.Pipeline.Frontend;

namespace Ruri.ShaderTools.Cli;

/// <summary>Parsed command line for a single-shader run.</summary>
internal sealed record DecompileCommandLine(
    string InputPath,
    string? OutputPath,
    string? SymbolsPath,
    string? DebugDumpDirectory,
    ShaderBinaryFormat Format,
    uint ShaderModel);

/// <summary>Parsed command line for a batch run.</summary>
internal sealed record BatchCommandLine(string Root, int MaxConcurrency);

/// <summary>
/// Argument parsing, kept apart from the work the arguments describe.
///
/// Returns null and prints the reason on bad input, rather than throwing — a CLI
/// misuse is a usage message, not a stack trace.
/// </summary>
internal static class CommandLine
{
    public static bool IsHelp(string argument) => argument is "-h" or "--help" or "/?" or "/help";

    public static DecompileCommandLine? ParseDecompile(string[] args)
    {
        string inputPath = Path.GetFullPath(args[0]);
        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"Error: input file not found: {inputPath}");
            return null;
        }

        string? outputPath = null;
        string? symbolsPath = null;
        string? debugDumpDirectory = null;
        ShaderBinaryFormat format = ShaderBinaryFormat.Unknown;
        uint shaderModel = 50;

        for (int i = 1; i < args.Length; i++)
        {
            string argument = args[i];

            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                if (outputPath is null)
                {
                    outputPath = argument;
                    continue;
                }

                Console.Error.WriteLine($"Error: unexpected positional argument: {argument}");
                return null;
            }

            switch (argument)
            {
                case "--symbols" or "--metadata" when i + 1 < args.Length:
                    symbolsPath = args[++i];
                    break;
                case "--format" when i + 1 < args.Length:
                    if (!TryParseFormat(args[++i], out format))
                    {
                        return null;
                    }
                    break;
                case "--shader-model" when i + 1 < args.Length && uint.TryParse(args[i + 1], out uint model):
                    shaderModel = model;
                    i++;
                    break;
                case "--debug-dump" when i + 1 < args.Length:
                    debugDumpDirectory = args[++i];
                    break;
                default:
                    Console.Error.WriteLine($"Error: unknown option: {argument}");
                    return null;
            }
        }

        return new DecompileCommandLine(inputPath, outputPath, symbolsPath, debugDumpDirectory, format, shaderModel);
    }

    public static BatchCommandLine? ParseBatch(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: --batch <dir> [--max-concurrency N]");
            return null;
        }

        string root = Path.GetFullPath(args[1]);
        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"Error: directory not found: {root}");
            return null;
        }

        int maxConcurrency = 0;
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--max-concurrency" && i + 1 < args.Length && int.TryParse(args[i + 1], out int value))
            {
                maxConcurrency = value;
                i++;
            }
        }

        return new BatchCommandLine(root, maxConcurrency);
    }

    private static bool TryParseFormat(string value, out ShaderBinaryFormat format)
    {
        switch (value.ToLowerInvariant())
        {
            case "dxbc": format = ShaderBinaryFormat.Dxbc; return true;
            case "dxil": format = ShaderBinaryFormat.Dxil; return true;
            case "spv" or "spirv": format = ShaderBinaryFormat.SpirV; return true;
            case "auto" or "unknown": format = ShaderBinaryFormat.Unknown; return true;
            default:
                Console.Error.WriteLine($"Error: unknown format: {value}. Use dxbc / dxil / spv / auto.");
                format = ShaderBinaryFormat.Unknown;
                return false;
        }
    }
}
