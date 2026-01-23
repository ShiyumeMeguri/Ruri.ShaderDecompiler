using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ruri.ShaderDecompiler.Engine;

/// <summary>
/// Parses UE ShaderArchive JSON exported by FModel.
/// </summary>
public class UnrealShaderArchiveParser
{
    public class SerializedShadersData
    {
        [JsonPropertyName("ShaderMapHashes")]
        public List<string> ShaderMapHashes { get; set; } = new();

        [JsonPropertyName("ShaderHashes")]
        public List<string> ShaderHashes { get; set; } = new();
    }

    public class ShaderArchiveRoot
    {
        [JsonPropertyName("SerializedShaders")]
        public SerializedShadersData SerializedShaders { get; set; } = new();
    }

    /// <summary>
    /// Parses the shader archive JSON.
    /// </summary>
    public static ShaderArchiveRoot? Parse(string jsonContent)
    {
        try
        {
            return JsonSerializer.Deserialize<ShaderArchiveRoot>(jsonContent);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error parsing ShaderArchive JSON: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Checks if the hash is a valid SHA-1 hash (base64 encoded 20 bytes).
    /// </summary>
    public static bool IsSha1Hash(string base64Hash)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64Hash);
            return bytes.Length == 20;
        }
        catch
        {
            return false;
        }
    }
}
