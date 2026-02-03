using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Ruri.ShaderDecompiler.Unreal
{
    /// <summary>
    /// Parses material JSON exports to extract all parameter values.
    /// Based on UE 5.4.4 source: MaterialInstance.h:617-643
    /// 
    /// Supports all 7 runtime parameter types:
    /// - ScalarParameterValues (float)
    /// - VectorParameterValues (FLinearColor)
    /// - DoubleVectorParameterValues (FVector4d)
    /// - TextureParameterValues (UTexture*)
    /// - RuntimeVirtualTextureParameterValues (URuntimeVirtualTexture*)
    /// - SparseVolumeTextureParameterValues (USparseVolumeTexture*)
    /// - FontParameterValues (UFont* + int32)
    /// </summary>
    public class MaterialParameterExtractor
    {
        /// <summary>
        /// Extract all material parameters from a material JSON file.
        /// </summary>
        /// <param name="jsonPath">Path to material .json file</param>
        /// <returns>Collection of all extracted parameters, or null if parsing fails</returns>
        public static FMaterialParameterCollection ExtractFromJson(string jsonPath)
        {
            if (!File.Exists(jsonPath))
                return null;

            try
            {
                var jsonString = File.ReadAllText(jsonPath);
                using var doc = JsonDocument.Parse(jsonString);
                var root = doc.RootElement;

                // FModel exports as array, get first element
                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
                    return null;

                var materialObj = root[0];
                if (!materialObj.TryGetProperty("Properties", out var props))
                    return null;

                var collection = new FMaterialParameterCollection();

                // Parse all parameter arrays
                ParseScalarParameters(props, collection);
                ParseVectorParameters(props, collection);
                ParseDoubleVectorParameters(props, collection);
                ParseTextureParameters(props, collection);
                ParseRuntimeVirtualTextureParameters(props, collection);
                ParseSparseVolumeTextureParameters(props, collection);
                ParseFontParameters(props, collection);

                return collection;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MaterialParameterExtractor] Error parsing {jsonPath}: {ex.Message}");
                return null;
            }
        }

        #region Scalar Parameters

        private static void ParseScalarParameters(JsonElement props, FMaterialParameterCollection collection)
        {
            if (!props.TryGetProperty("ScalarParameterValues", out var array) || array.ValueKind != JsonValueKind.Array)
                return;

            foreach (var elem in array.EnumerateArray())
            {
                var param = new FScalarParameterValue();

                // ParameterInfo
                if (elem.TryGetProperty("ParameterInfo", out var info))
                    param.ParameterInfo = ParseParameterInfo(info);
                else if (elem.TryGetProperty("ParameterName", out var name)) // Legacy format
                    param.ParameterInfo.Name = name.GetString() ?? "";

                // ParameterValue
                if (elem.TryGetProperty("ParameterValue", out var val))
                    param.ParameterValue = val.GetSingle();

                // ExpressionGUID
                if (elem.TryGetProperty("ExpressionGUID", out var guid) && Guid.TryParse(guid.GetString(), out var g))
                    param.ExpressionGUID = g;

                collection.ScalarParameterValues.Add(param);
            }
        }

        #endregion

        #region Vector Parameters

        private static void ParseVectorParameters(JsonElement props, FMaterialParameterCollection collection)
        {
            if (!props.TryGetProperty("VectorParameterValues", out var array) || array.ValueKind != JsonValueKind.Array)
                return;

            foreach (var elem in array.EnumerateArray())
            {
                var param = new FVectorParameterValue();

                if (elem.TryGetProperty("ParameterInfo", out var info))
                    param.ParameterInfo = ParseParameterInfo(info);
                else if (elem.TryGetProperty("ParameterName", out var name))
                    param.ParameterInfo.Name = name.GetString() ?? "";

                if (elem.TryGetProperty("ParameterValue", out var val))
                    param.ParameterValue = ParseLinearColor(val);

                if (elem.TryGetProperty("ExpressionGUID", out var guid) && Guid.TryParse(guid.GetString(), out var g))
                    param.ExpressionGUID = g;

                collection.VectorParameterValues.Add(param);
            }
        }

        #endregion

        #region DoubleVector Parameters

        private static void ParseDoubleVectorParameters(JsonElement props, FMaterialParameterCollection collection)
        {
            if (!props.TryGetProperty("DoubleVectorParameterValues", out var array) || array.ValueKind != JsonValueKind.Array)
                return;

            foreach (var elem in array.EnumerateArray())
            {
                var param = new FDoubleVectorParameterValue();

                if (elem.TryGetProperty("ParameterInfo", out var info))
                    param.ParameterInfo = ParseParameterInfo(info);
                else if (elem.TryGetProperty("ParameterName", out var name))
                    param.ParameterInfo.Name = name.GetString() ?? "";

                if (elem.TryGetProperty("ParameterValue", out var val))
                    param.ParameterValue = ParseVector4d(val);

                if (elem.TryGetProperty("ExpressionGUID", out var guid) && Guid.TryParse(guid.GetString(), out var g))
                    param.ExpressionGUID = g;

                collection.DoubleVectorParameterValues.Add(param);
            }
        }

        #endregion

        #region Texture Parameters

        private static void ParseTextureParameters(JsonElement props, FMaterialParameterCollection collection)
        {
            if (!props.TryGetProperty("TextureParameterValues", out var array) || array.ValueKind != JsonValueKind.Array)
                return;

            foreach (var elem in array.EnumerateArray())
            {
                var param = new FTextureParameterValue();

                if (elem.TryGetProperty("ParameterInfo", out var info))
                    param.ParameterInfo = ParseParameterInfo(info);
                else if (elem.TryGetProperty("ParameterName", out var name))
                    param.ParameterInfo.Name = name.GetString() ?? "";

                if (elem.TryGetProperty("ParameterValue", out var val))
                    param.TexturePath = GetObjectPath(val);

                if (elem.TryGetProperty("ExpressionGUID", out var guid) && Guid.TryParse(guid.GetString(), out var g))
                    param.ExpressionGUID = g;

                collection.TextureParameterValues.Add(param);
            }
        }

        #endregion

        #region RuntimeVirtualTexture Parameters

        private static void ParseRuntimeVirtualTextureParameters(JsonElement props, FMaterialParameterCollection collection)
        {
            if (!props.TryGetProperty("RuntimeVirtualTextureParameterValues", out var array) || array.ValueKind != JsonValueKind.Array)
                return;

            foreach (var elem in array.EnumerateArray())
            {
                var param = new FRuntimeVirtualTextureParameterValue();

                if (elem.TryGetProperty("ParameterInfo", out var info))
                    param.ParameterInfo = ParseParameterInfo(info);
                else if (elem.TryGetProperty("ParameterName", out var name))
                    param.ParameterInfo.Name = name.GetString() ?? "";

                if (elem.TryGetProperty("ParameterValue", out var val))
                    param.TexturePath = GetObjectPath(val);

                if (elem.TryGetProperty("ExpressionGUID", out var guid) && Guid.TryParse(guid.GetString(), out var g))
                    param.ExpressionGUID = g;

                collection.RuntimeVirtualTextureParameterValues.Add(param);
            }
        }

        #endregion

        #region SparseVolumeTexture Parameters

        private static void ParseSparseVolumeTextureParameters(JsonElement props, FMaterialParameterCollection collection)
        {
            if (!props.TryGetProperty("SparseVolumeTextureParameterValues", out var array) || array.ValueKind != JsonValueKind.Array)
                return;

            foreach (var elem in array.EnumerateArray())
            {
                var param = new FSparseVolumeTextureParameterValue();

                if (elem.TryGetProperty("ParameterInfo", out var info))
                    param.ParameterInfo = ParseParameterInfo(info);
                else if (elem.TryGetProperty("ParameterName", out var name))
                    param.ParameterInfo.Name = name.GetString() ?? "";

                if (elem.TryGetProperty("ParameterValue", out var val))
                    param.TexturePath = GetObjectPath(val);

                if (elem.TryGetProperty("ExpressionGUID", out var guid) && Guid.TryParse(guid.GetString(), out var g))
                    param.ExpressionGUID = g;

                collection.SparseVolumeTextureParameterValues.Add(param);
            }
        }

        #endregion

        #region Font Parameters

        private static void ParseFontParameters(JsonElement props, FMaterialParameterCollection collection)
        {
            if (!props.TryGetProperty("FontParameterValues", out var array) || array.ValueKind != JsonValueKind.Array)
                return;

            foreach (var elem in array.EnumerateArray())
            {
                var param = new FFontParameterValue();

                if (elem.TryGetProperty("ParameterInfo", out var info))
                    param.ParameterInfo = ParseParameterInfo(info);
                else if (elem.TryGetProperty("ParameterName", out var name))
                    param.ParameterInfo.Name = name.GetString() ?? "";

                if (elem.TryGetProperty("FontValue", out var val))
                    param.FontPath = GetObjectPath(val);

                if (elem.TryGetProperty("FontPage", out var page))
                    param.FontPage = page.GetInt32();

                if (elem.TryGetProperty("ExpressionGUID", out var guid) && Guid.TryParse(guid.GetString(), out var g))
                    param.ExpressionGUID = g;

                collection.FontParameterValues.Add(param);
            }
        }

        #endregion

        #region Helper Methods

        private static FMaterialParameterInfo ParseParameterInfo(JsonElement elem)
        {
            var info = new FMaterialParameterInfo();

            if (elem.TryGetProperty("Name", out var name))
                info.Name = name.GetString() ?? "";

            if (elem.TryGetProperty("Association", out var assoc))
            {
                var assocStr = assoc.GetString() ?? "";
                if (assocStr.Contains("Layer")) info.Association = EMaterialParameterAssociation.LayerParameter;
                else if (assocStr.Contains("Blend")) info.Association = EMaterialParameterAssociation.BlendParameter;
                else info.Association = EMaterialParameterAssociation.GlobalParameter;
            }

            if (elem.TryGetProperty("Index", out var idx))
                info.Index = idx.GetInt32();

            return info;
        }

        private static FLinearColor ParseLinearColor(JsonElement elem)
        {
            float r = 0, g = 0, b = 0, a = 1;

            if (elem.TryGetProperty("R", out var rVal)) r = rVal.GetSingle();
            if (elem.TryGetProperty("G", out var gVal)) g = gVal.GetSingle();
            if (elem.TryGetProperty("B", out var bVal)) b = bVal.GetSingle();
            if (elem.TryGetProperty("A", out var aVal)) a = aVal.GetSingle();

            return new FLinearColor(r, g, b, a);
        }

        private static FVector4d ParseVector4d(JsonElement elem)
        {
            double x = 0, y = 0, z = 0, w = 1;

            if (elem.TryGetProperty("X", out var xVal)) x = xVal.GetDouble();
            if (elem.TryGetProperty("Y", out var yVal)) y = yVal.GetDouble();
            if (elem.TryGetProperty("Z", out var zVal)) z = zVal.GetDouble();
            if (elem.TryGetProperty("W", out var wVal)) w = wVal.GetDouble();

            return new FVector4d(x, y, z, w);
        }

        private static string GetObjectPath(JsonElement elem)
        {
            if (elem.TryGetProperty("ObjectPath", out var path))
            {
                var p = path.GetString() ?? "";
                int dot = p.LastIndexOf('.');
                return dot > 0 ? p.Substring(0, dot) : p;
            }
            if (elem.TryGetProperty("ObjectName", out var name))
                return name.GetString() ?? "";
            return "";
        }

        #endregion

        #region Batch Processing

        /// <summary>
        /// Scan a directory for all material JSONs and extract parameters.
        /// </summary>
        /// <param name="directory">Root directory to scan</param>
        /// <returns>Dictionary mapping material name -> parameters</returns>
        public static Dictionary<string, FMaterialParameterCollection> ScanDirectory(string directory)
        {
            var result = new Dictionary<string, FMaterialParameterCollection>();
            var files = Directory.GetFiles(directory, "*.json", SearchOption.AllDirectories);

            foreach (var file in files)
            {
                if (file.Contains("ShaderArchive") || file.Contains("ShaderMappings")) continue;

                var collection = ExtractFromJson(file);
                if (collection != null && collection.TotalParameterCount > 0)
                {
                    var matName = Path.GetFileNameWithoutExtension(file);
                    result[matName] = collection;
                    Console.WriteLine($"[MaterialParameterExtractor] {matName}: {collection}");
                }
            }

            Console.WriteLine($"[MaterialParameterExtractor] Found {result.Count} materials with parameters");
            return result;
        }

        #endregion
    }
}
