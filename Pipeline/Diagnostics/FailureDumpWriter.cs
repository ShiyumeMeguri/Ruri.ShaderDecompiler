using System.Text;
using Newtonsoft.Json;

namespace Ruri.ShaderTools.Pipeline.Diagnostics;

/// <summary>
/// Writes everything needed to reproduce and diagnose one failed decompile,
/// offline, without the original asset.
///
/// The set is deliberately complete: input binary, SPIR-V after each stage, the
/// exact symbol table that was used, the structuring log, the planned name
/// patches, the built-in decorations, and the error with whatever the native
/// tools said. Between the input and the staged SPIR-V, a failure can be bisected
/// to a single stage; between the symbols and the patch plan, a WRONG-but-
/// successful decompile can be explained.
/// </summary>
internal static class FailureDumpWriter
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <returns>The dump's path prefix — every file shares it.</returns>
    public static string Write(
        string directory,
        string? stem,
        byte[] inputBinary,
        DecompileResult result,
        SerializedProgramData symbols)
    {
        Directory.CreateDirectory(directory);

        string basePath = Path.Combine(directory, SanitizeStem(stem) ?? $"shader_{DateTime.Now:yyyyMMdd_HHmmss_fff}");

        File.WriteAllBytes(basePath + ".input.bin", inputBinary);
        WriteIfPresent(basePath + ".01.after-frontend.spv", result.SpirvAfterFrontend);
        WriteIfPresent(basePath + ".02.after-structuring.spv", result.SpirvAfterStructuring);
        WriteIfPresent(basePath + ".03.after-symbol-injection.spv", result.SpirvAfterSymbolInjection);

        var error = new StringBuilder();
        error.AppendLine($"Failed stage: {result.FailedStage}");
        error.AppendLine();
        error.AppendLine("Error:");
        error.AppendLine(result.ErrorMessage ?? "<no message>");
        if (!string.IsNullOrWhiteSpace(result.NativeToolDiagnostics))
        {
            error.AppendLine();
            error.AppendLine("Native tool diagnostics:");
            error.AppendLine(result.NativeToolDiagnostics);
        }
        File.WriteAllText(basePath + ".error.txt", error.ToString(), Utf8NoBom);

        WriteTextIfPresent(basePath + ".patch-plan.txt", result.PatchPlanReport);
        WriteTextIfPresent(basePath + ".builtin-decorations.txt", result.BuiltInDecorationReport);
        WriteTextIfPresent(basePath + ".structuring-log.txt", result.StructuringLog);

        try
        {
            File.WriteAllText(basePath + ".symbols.json", JsonConvert.SerializeObject(symbols, Formatting.Indented), Utf8NoBom);
        }
        catch
        {
            // A symbol table can carry host types that will not serialize. That is
            // not a reason to lose the rest of the dump.
        }

        return basePath;
    }

    private static void WriteIfPresent(string path, byte[]? bytes)
    {
        if (bytes is { Length: > 0 })
        {
            File.WriteAllBytes(path, bytes);
        }
    }

    private static void WriteTextIfPresent(string path, string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            File.WriteAllText(path, text, Utf8NoBom);
        }
    }

    /// <summary>
    /// Shader names arrive from asset paths and pass names, so they routinely
    /// contain characters no file system accepts.
    /// </summary>
    public static string? SanitizeStem(string? stem)
    {
        if (string.IsNullOrWhiteSpace(stem))
        {
            return null;
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(stem.Length);
        foreach (char c in stem)
        {
            builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }
        return builder.ToString();
    }
}
