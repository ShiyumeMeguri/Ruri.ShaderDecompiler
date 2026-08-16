using System.Text;
using Newtonsoft.Json;

namespace Ruri.ShaderTools.Cli;

/// <summary>Decompiles one shader binary.</summary>
internal static class DecompileCommand
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static int Execute(DecompileCommandLine command)
    {
        SerializedProgramData? symbols = SymbolSidecar.Load(command.InputPath, command.SymbolsPath);
        byte[] binary = File.ReadAllBytes(command.InputPath);

        using var decompiler = new ShaderDecompiler();
        DecompileResult result = decompiler.Decompile(binary, new DecompileOptions
        {
            Format = command.Format,
            Symbols = symbols,
            ShaderModel = command.ShaderModel,
            DebugDumpDirectory = command.DebugDumpDirectory,
            DebugDumpStem = command.DebugDumpDirectory is null ? null : Path.GetFileNameWithoutExtension(command.InputPath),
        });

        if (command.DebugDumpDirectory is not null)
        {
            DumpIntermediates(command.DebugDumpDirectory, command.InputPath, result, symbols);
        }

        if (!result.Success)
        {
            Console.Error.WriteLine($"Decompilation failed: {result.ErrorMessage}");
            return 1;
        }

        string source = result.SourceCode ?? string.Empty;

        if (command.OutputPath == "-")
        {
            Console.Out.Write(source);
            return 0;
        }

        string extension = string.IsNullOrWhiteSpace(result.SourceFileExtension) ? ".hlsl" : result.SourceFileExtension;
        string outputPath = command.OutputPath is not null
            ? Path.GetFullPath(command.OutputPath)
            : Path.ChangeExtension(command.InputPath, extension);

        string? parent = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        File.WriteAllText(outputPath, source);
        Console.WriteLine($"Wrote {outputPath}");
        return 0;
    }

    /// <summary>
    /// Dump the per-stage SPIR-V even on SUCCESS. A shader that decompiles but
    /// decompiles WRONGLY is the harder problem, and bisecting it needs the same
    /// artifacts a crash would have produced.
    /// </summary>
    private static void DumpIntermediates(string directory, string inputPath, DecompileResult result, SerializedProgramData? symbols)
    {
        Directory.CreateDirectory(directory);
        string stem = Path.GetFileNameWithoutExtension(inputPath);
        string At(string suffix) => Path.Combine(directory, stem + suffix);

        if (result.SpirvAfterFrontend is { Length: > 0 } frontend)
        {
            File.WriteAllBytes(At(".01.after-frontend.spv"), frontend);
        }
        if (result.SpirvAfterStructuring is { Length: > 0 } structured)
        {
            File.WriteAllBytes(At(".02.after-structuring.spv"), structured);
        }
        if (result.SpirvAfterSymbolInjection is { Length: > 0 } injected)
        {
            File.WriteAllBytes(At(".03.after-symbol-injection.spv"), injected);
        }
        if (!string.IsNullOrWhiteSpace(result.StructuringLog))
        {
            File.WriteAllText(At(".structuring-log.txt"), result.StructuringLog, Utf8NoBom);
        }
        if (symbols is not null)
        {
            File.WriteAllText(At(".symbols.json"), JsonConvert.SerializeObject(symbols, Formatting.Indented), Utf8NoBom);
        }

        Console.WriteLine($"Debug-dump → {directory}");
    }
}
