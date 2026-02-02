using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Ruri.ShaderDecompiler.Unreal;

namespace Ruri.ShaderDecompiler
{
    public class ShaderBindingExtractor
    {
        public static Dictionary<int, string> ScanAndRestore(string materialExportsDir, string shaderArchiveDir)
        {
            Console.WriteLine($"[ShaderBindingExtractor] Scanning materials in {materialExportsDir}...");
            Console.WriteLine($"[ShaderBindingExtractor] Loading Shader Archive from {shaderArchiveDir}...");

            // 1. Load Archive Hashes
            var shaderMapHashes = LoadShaderArchive(shaderArchiveDir);
            if (shaderMapHashes.Count == 0)
            {
                Console.WriteLine("[ShaderBindingExtractor] No shader map hashes found or loaded.");
                return new Dictionary<int, string>();
            }

            // 2. Scan Material JSONs
            var materialFiles = Directory.GetFiles(materialExportsDir, "*.json", SearchOption.AllDirectories);
            int restoredCount = 0;
            
            // Dictionary to return: ShaderIndex -> MaterialName
            var nameMap = new Dictionary<int, string>();
            var restoredMappingsForJson = new Dictionary<string, List<int>>();
            var textureGuidCache = new ConcurrentDictionary<string, Guid>();

            foreach (var file in materialFiles)
            {
                if (file.Contains("ShaderArchive") || file.EndsWith(".json.json") || file.Contains("ShaderMappings.json")) continue; 

                try
                {
                    var id = ExtractMaterialShaderMapId(file, materialExportsDir, textureGuidCache);
                    if (id == null) continue;

                    // Compute Hash
                    byte[] hashBytes;
                    id.GetMaterialHash(out hashBytes);
                    var hashString = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();

                    var matName = Path.GetFileNameWithoutExtension(file);

                    // Lookup
                    if (shaderMapHashes.ContainsKey(hashString))
                    {
                        Console.WriteLine($"[MATCH] Material: {matName} -> Hash: {hashString}");
                        var shaderIndices = shaderMapHashes[hashString];
                        
                        if (!restoredMappingsForJson.ContainsKey(matName)) restoredMappingsForJson[matName] = new List<int>();
                        restoredMappingsForJson[matName].AddRange(shaderIndices);

                        foreach (var idx in shaderIndices)
                        {
                            nameMap[idx] = matName;
                        }

                        restoredCount++;
                    }
                    else
                    {
                        // Console.WriteLine($"[MISS] Material: {matName} -> Hash: {hashString}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ShaderBindingExtractor] Error processing {file}: {ex.Message}");
                }
            }

            // Save Result
            var outputPath = Path.Combine(materialExportsDir, "ShaderMappings.json");
            var output = new Dictionary<string, object>
            {
                { "RestoredCount", restoredCount },
                { "Mappings", restoredMappingsForJson }
            };
            
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(outputPath, JsonSerializer.Serialize(output, options));

            Console.WriteLine($"[ShaderBindingExtractor] scan complete. Restored {restoredCount} material bindings.");
            Console.WriteLine($"[ShaderBindingExtractor] Mappings saved to {outputPath}");

            return nameMap;
        }

        private static Dictionary<string, List<int>> LoadShaderArchive(string dir)
        {
            var map = new Dictionary<string, List<int>>();
            var files = Directory.GetFiles(dir, "ShaderArchive-*.json");
            Console.WriteLine($"[ShaderBindingExtractor] Found {files.Length} archive JSONs.");
            foreach (var f in files)
            {
                Console.WriteLine($"[ShaderBindingExtractor] Loading {Path.GetFileName(f)}...");
                try {
                    var jsonString = File.ReadAllText(f);
                    using var doc = JsonDocument.Parse(jsonString);
                    var root = doc.RootElement;
                    
                    if (root.ValueKind == JsonValueKind.Object)
                    {
                        JsonElement shaderSection = root;
                        if (root.TryGetProperty("SerializedShaders", out var ser))
                        {
                            shaderSection = ser;
                        }

                        if (shaderSection.TryGetProperty("ShaderMapHashes", out var hashesProp) && hashesProp.ValueKind == JsonValueKind.Array)
                        {
                            int index = 0;
                            foreach (var h in hashesProp.EnumerateArray())
                            {
                                var b64 = h.GetString();
                                if (!string.IsNullOrEmpty(b64))
                                {
                                    try
                                    {
                                        byte[] bytes = Convert.FromBase64String(b64);
                                        string hex = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
                                        if (!map.ContainsKey(hex)) map[hex] = new List<int>();
                                        map[hex].Add(index);
                                    }
                                    catch (FormatException)
                                    {
                                        var hash = b64.ToLowerInvariant();
                                        if (!map.ContainsKey(hash)) map[hash] = new List<int>();
                                        map[hash].Add(index);
                                    }
                                }
                                index++;
                            }
                        }
                    }
                }
                catch (Exception ex) {
                    Console.WriteLine($"[ShaderBindingExtractor] Failed to load {f}: {ex.Message}");
                }
            }
            return map;
        }

        private static FMaterialShaderMapId ExtractMaterialShaderMapId(string jsonPath, string rootDir, ConcurrentDictionary<string, Guid> textureCache)
        {
            var jsonString = File.ReadAllText(jsonPath);
            using var doc = JsonDocument.Parse(jsonString);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) return null;
            
            var materialObj = root[0];
            if (!materialObj.TryGetProperty("Type", out var typeProp)) return null;
            var type = typeProp.GetString();
            if (type != "Material" && type != "MaterialInstanceConstant") return null;

            if (!materialObj.TryGetProperty("Properties", out var props)) return null;

            var id = new FMaterialShaderMapId();
            id.Usage = EMaterialShaderMapUsage.Default; 

            // 1. Quality & Feature Level
            id.QualityLevel = EMaterialQualityLevel.High; 
            if (props.TryGetProperty("QualityLevel", out var qualProp))
            {
                string val = qualProp.GetString() ?? "";
                if (val.Contains("Low")) id.QualityLevel = EMaterialQualityLevel.Low;
                else if (val.Contains("Medium")) id.QualityLevel = EMaterialQualityLevel.Medium;
                else if (val.Contains("High")) id.QualityLevel = EMaterialQualityLevel.High;
                else if (val.Contains("Epic")) id.QualityLevel = EMaterialQualityLevel.Epic;
            }
            
            id.FeatureLevel = (int)ERHIFeatureLevel.SM6;
            if (props.TryGetProperty("FeatureLevel", out var featProp))
            {
               string val = featProp.GetString() ?? "";
               if (val.Contains("SM5")) id.FeatureLevel = (int)ERHIFeatureLevel.SM5;
               else if (val.Contains("SM6")) id.FeatureLevel = (int)ERHIFeatureLevel.SM6;
            }

            // 2. BaseMaterialId (StateId)
            Guid stateId = Guid.Empty;
            if (props.TryGetProperty("StateId", out var sProp))
            {
                Guid.TryParse(sProp.GetString(), out stateId);
            }
            
            if (stateId == Guid.Empty)
            {
                if (props.TryGetProperty("Parent", out var parentProp))
                {
                    var parentPath = GetPathFromObject(parentProp);
                    if (!string.IsNullOrEmpty(parentPath))
                    {
                        stateId = ResolveParentStateId(parentPath, rootDir);
                    }
                }
            }
            id.BaseMaterialId = stateId;

            // 3. Referenced Textures (for LightingGuid)
            var refTextures = new List<Guid>();
            if (props.TryGetProperty("ReferencedTextures", out var refTexProp) && refTexProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var tex in refTexProp.EnumerateArray())
                {
                    var texPath = GetPathFromObject(tex);
                    if (string.IsNullOrEmpty(texPath)) continue;
                    var g = GetTextureLightingGuid(texPath, rootDir, textureCache);
                    if (g != Guid.Empty) refTextures.Add(g);
                }
            }
            // Use CachedExpressionData if available
            if (materialObj.TryGetProperty("CachedExpressionData", out var cachedData) && cachedData.TryGetProperty("ReferencedTextures", out var refTexProp2))
            {
                 foreach (var tex in refTexProp2.EnumerateArray())
                {
                    var texPath = GetPathFromObject(tex);
                    if (string.IsNullOrEmpty(texPath)) continue;
                    var g = GetTextureLightingGuid(texPath, rootDir, textureCache);
                    if (g != Guid.Empty && !refTextures.Contains(g)) refTextures.Add(g);
                }
            }
            id.ReferencedTextures = refTextures;
            
            return id;
        }

        private static string GetPathFromObject(JsonElement obj)
        {
            if (obj.TryGetProperty("ObjectPath", out var pathProp))
            {
                var path = pathProp.GetString();
                if (string.IsNullOrEmpty(path)) return "";
                int dot = path.LastIndexOf('.');
                return dot > 0 ? path.Substring(0, dot) : path;
            }
            return "";
        }

        private static Guid ResolveParentStateId(string parentPath, string rootDir)
        {
            // parentPath: /Game/Oni_Project/Materials/M_Clouds
            string localPath = parentPath.Replace("/Game/", "");
            string fullPath = Path.Combine(rootDir, localPath + ".json");
            
            if (!File.Exists(fullPath)) return Guid.Empty;
            
            try {
                var json = File.ReadAllText(fullPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
                {
                    var materialObj = root[0];
                    if (materialObj.TryGetProperty("Properties", out var props))
                    {
                        if (props.TryGetProperty("StateId", out var sProp))
                        {
                            if (Guid.TryParse(sProp.GetString(), out var g)) return g;
                        }
                        // Recurse if parent is also MI
                        if (materialObj.TryGetProperty("Type", out var typeProp) && typeProp.GetString() == "MaterialInstanceConstant")
                        {
                            if (props.TryGetProperty("Parent", out var nextParent))
                            {
                                var nextPath = GetPathFromObject(nextParent);
                                if (!string.IsNullOrEmpty(nextPath)) return ResolveParentStateId(nextPath, rootDir);
                            }
                        }
                    }
                }
            } catch {}
            return Guid.Empty;
        }

        private static Guid GetTextureLightingGuid(string objectPath, string rootDir, ConcurrentDictionary<string, Guid> cache)
        {
            if (cache.TryGetValue(objectPath, out var g)) return g;

            string localPath = objectPath.Replace("/Game/", "");
            string fullPath = Path.Combine(rootDir, localPath + ".json");
            
            if (!File.Exists(fullPath)) return Guid.Empty;

            try
            {
                var json = File.ReadAllText(fullPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
                {
                    var texObj = root[0];
                    if (texObj.TryGetProperty("Properties", out var props) && props.TryGetProperty("LightingGuid", out var lg))
                    {
                        if (Guid.TryParse(lg.GetString(), out var guid))
                        {
                            cache[objectPath] = guid;
                            return guid;
                        }
                    }
                }
            }
            catch {}
            return Guid.Empty;
        }
    }
}
