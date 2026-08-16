using Newtonsoft.Json;

namespace Ruri.ShaderTools.Cli;

/// <summary>
/// Loads a symbol table from a JSON sidecar.
///
/// The CLI has no engine attached, so symbols have to arrive as a file — either
/// named explicitly or discovered next to the input. This is the same shape a
/// host produces in-process, which is what makes a dumped failure reproducible
/// from the command line.
/// </summary>
internal static class SymbolSidecar
{
    private const string DefaultSuffix = ".metadata.json";

    public static SerializedProgramData? Load(string inputPath, string? explicitPath)
    {
        string? path = explicitPath;

        if (string.IsNullOrWhiteSpace(path))
        {
            string sidecar = inputPath + DefaultSuffix;
            if (File.Exists(sidecar))
            {
                path = sidecar;
            }
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Symbol file not found: {fullPath}");
        }

        SerializedProgramData? symbols = JsonConvert.DeserializeObject<SerializedProgramData>(File.ReadAllText(fullPath));
        return symbols ?? throw new InvalidOperationException($"Failed to deserialize symbols: {fullPath}");
    }

    /// <summary>Sidecar path a batch run looks for beside <paramref name="stem"/>.</summary>
    public static string? FindBeside(string stem)
    {
        string suffixed = stem + DefaultSuffix;
        if (File.Exists(suffixed))
        {
            return suffixed;
        }

        string replaced = Path.ChangeExtension(stem, DefaultSuffix);
        return File.Exists(replaced) ? replaced : null;
    }
}
