using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ruri.ShaderDecompiler.Intermediate;

namespace Ruri.ShaderDecompiler.Unreal
{
    /// <summary>
    /// Provides parameter name mappings from exported material properties.
    /// Uses MaterialParameterMappings.json exported by FModel Hook.
    /// 
    /// NOTE: This mapping is based on the assumption that Texture2D_i in shader
    /// corresponds to the i-th item in TextureParameterValues array in the material.
    /// </summary>
    public class PreciseParameterMapper
    {
        private readonly Dictionary<string, MaterialMapping> _materialMappings = new(StringComparer.OrdinalIgnoreCase);

        private PreciseParameterMapper() { }

        private static readonly Regex BindingPattern = new Regex(
            @"^(?:Material[_\.])?(?<prefix>Texture2D|TextureCube|Texture2DArray|TextureCubeArray|VolumeTexture|VirtualTexturePhysical|SparseVolumeTexturePageTable)_(?<index>\d+)(?<sampler>Sampler)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Load mappings from JSON file.
        /// </summary>
        public static PreciseParameterMapper LoadFromFile(string jsonPath)
        {
            var mapper = new PreciseParameterMapper();
            
            if (!File.Exists(jsonPath)) return mapper;

            try
            {
                var json = File.ReadAllText(jsonPath);
                var data = JsonSerializer.Deserialize<MaterialMappingCollection>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip
                });

                if (data?.Materials != null)
                {
                    foreach (var kv in data.Materials)
                    {
                        var key = kv.Key;
                        mapper._materialMappings[key] = kv.Value;

                        // Add Alias for Package Path (e.g. "/Game/Map.Map" -> "/Game/Map")
                        // This handles cases where lookups provide only the package path.
                        int dotIndex = key.LastIndexOf('.');
                        if (dotIndex >= 0)
                        {
                            var packagePath = key.Substring(0, dotIndex);
                            if (!mapper._materialMappings.ContainsKey(packagePath))
                            {
                                mapper._materialMappings[packagePath] = kv.Value;
                            }
                        }
                    }
                    
                    Console.WriteLine($"[PreciseParameterMapper] Loaded {data.Materials.Count} material mappings (Total aliases: {mapper._materialMappings.Count})");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PreciseParameterMapper] Failed to load mappings: {ex.Message}");
            }

            return mapper;
        }

        /// <summary>
        /// Get mapping for a material by path.
        /// </summary>
        public MaterialMapping GetByMaterialPath(string materialPath)
        {
            // Normalize separators
            var normalized = materialPath.Replace('\\', '/');
            
            // 1. Direct Lookup (Case Insensitive)
            if (_materialMappings.TryGetValue(normalized, out var mapping))
                return mapping;
            
            // 2. Try ensuring leading slash
            if (!normalized.StartsWith("/"))
            {
                if (_materialMappings.TryGetValue("/" + normalized, out mapping))
                    return mapping;
            }

            // 3. Robust Suffix Matching (Ignores MountPoint/Game prefix differences)
            // If input contains "/Content/", we use the part after it as a unique suffix.
            // Example Input: "Project/Content/Folder/Material"
            // Suffix: "Folder/Material"
            // Key: "/Game/Folder/Material.Material" (or alias "/Game/Folder/Material")
            // Both end with "Folder/Material"
            
            int contentIndex = normalized.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
            if (contentIndex >= 0)
            {
                // Extract "Folder/Material"
                string relativePath = normalized.Substring(contentIndex + 9); // +9 for "/Content/"
                
                // Find any key that ends with this relative path
                // We prioritize shortest match or exact suffix?
                // Given keys often have .ObjectName suffix, we should check:
                // Key ends with "/" + relativePath (to ensure full segment match)
                // OR Key ends with "/" + relativePath + "." (if key has extension) - handled by Contains probably
                
                string searchSuffix = "/" + relativePath;

                // Scan (Performance warning: O(N) where N is number of materials. Typically small < 5000)
                // Since N=140 here, it's instant.
                foreach (var key in _materialMappings.Keys)
                {
                    // Check if key ends with the relative path
                    // We handle both exact folder structure match
                    // Input: Folder/Match
                    // Key: /Game/Folder/Match
                    // Key: /Game/Folder/Match.Match
                    
                    if (key.EndsWith(searchSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        return _materialMappings[key];
                    }
                    
                    // Also check if matches package path logic (ignoring .ObjectName in key)
                    // If key is "/Game/Folder/Match.Match", remove extension and check
                     int dotIndex = key.LastIndexOf('.');
                     if (dotIndex > 0)
                     {
                         var keyWithoutExt = key.Substring(0, dotIndex);
                         if (keyWithoutExt.EndsWith(searchSuffix, StringComparison.OrdinalIgnoreCase))
                         {
                             return _materialMappings[key];
                         }
                     }
                }
            }

            return null;
        }

        /// <summary>
        /// Resolve a shader binding name (e.g., "Material.Texture2D_0") to its parameter name.
        /// Returns null if not found.
        /// </summary>
        public string ResolveBindingName(MaterialMapping mapping, string bindingName)
        {
            if (mapping == null || string.IsNullOrEmpty(bindingName))
                return null;

            var match = BindingPattern.Match(bindingName);
            if (!match.Success)
                return null;

            var prefix = match.Groups["prefix"].Value;
            var index = int.Parse(match.Groups["index"].Value);
            var isSampler = match.Groups["sampler"].Success;

            // Currently only handle Texture2D as generic textures because we operate on a flat list
            // If the shader asks for Texture2D_0, we give index 0
            // If the shader asks for TextureCube_0, we MIGHT give index 0 if it was the first texture... 
            // BUT usually they are separated. Since we only have a flat list, we assume standard index matching for now.
            // This is "best effort" precise naming.

            var entry = mapping.TextureParameters.Find(p => p.Index == index);
            if (entry != null)
            {
                return isSampler ? entry.Name + "_Sampler" : entry.Name;
            }

            return null;
        }

        /// <summary>
        /// Generate HLSL header comments with parameter mapping information.
        /// </summary>
        public string GenerateHlslHeader(MaterialMapping mapping, string materialPath = null)
        {
            if (mapping == null)
                return "// No parameter mapping available\n";

            var sb = new StringBuilder();
            sb.AppendLine("//==============================================================================");
            sb.AppendLine("// Parameter Mapping (from Material Properties)");
            if (!string.IsNullOrEmpty(materialPath))
                sb.AppendLine($"// Material: {materialPath}");
            sb.AppendLine("//==============================================================================");
            sb.AppendLine();

            // Texture parameters
            if (mapping.TextureParameters.Count > 0)
            {
                sb.AppendLine("// Texture Parameters:");
                foreach (var param in mapping.TextureParameters)
                {
                    sb.AppendLine($"//   Material.Texture2D_{param.Index} = \"{param.Name}\"");
                    if (!string.IsNullOrEmpty(param.TexturePath))
                    {
                        sb.AppendLine($"//     -> {param.TexturePath}");
                    }
                }
                sb.AppendLine();
            }

            // Numeric parameters
            if (mapping.NumericParameters.Count > 0)
            {
                sb.AppendLine("// Numeric Parameters:");
                foreach (var param in mapping.NumericParameters)
                {
                    sb.AppendLine($"//   {param.Name} ({param.Type}) = {param.DefaultValue}");
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }

        /// <summary>
        /// Convert mapping to unified ShaderSymbolMetadata for native injection.
        /// </summary>
        public ShaderSymbolMetadata GetSymbolMetadata(MaterialMapping mapping)
        {
            var metadata = new ShaderSymbolMetadata();

            if (mapping == null) return metadata;

            foreach (var param in mapping.TextureParameters)
            {
                // Map Texture (T#)
                metadata.Resources.Add(new ResourceSymbol
                {
                    Name = param.Name,
                    Binding = param.Index,
                    Set = 0, // UE Main Set
                    Type = ResourceType.Texture
                });

                // Map Sampler (S#)
                // In UE, sampler usually shares the same index/binding as the texture for shared samplers
                metadata.Resources.Add(new ResourceSymbol
                {
                    Name = param.Name + "_Sampler",
                    Binding = param.Index,
                    Set = 0,
                    Type = ResourceType.Sampler
                });
            }

            return metadata;
        }

        /// <summary>
        /// Generate inline comments for shader declarations.
        /// </summary>
        public Dictionary<string, string> BuildBindingToNameMap(MaterialMapping mapping)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (mapping?.TextureParameters == null)
                return result;

            foreach (var param in mapping.TextureParameters)
            {
                // We map to "Texture2D" by default as we don't know the exact shader type from the property list
                // But we generate bindings for various standard types to be safe
                
                var bindingNames = new[]
                {
                    $"Material_Texture2D_{param.Index}",
                    $"Texture2D_{param.Index}",
                    $"Material_TextureCube_{param.Index}",
                    $"TextureCube_{param.Index}",
                    $"Material_VirtualTexturePhysical_{param.Index}",
                    $"VirtualTexturePhysical_{param.Index}"
                };

                foreach (var name in bindingNames)
                {
                    result[name] = param.Name;
                    result[name + "Sampler"] = param.Name + "_Sampler";
                }
            }

            return result;
        }
    }

    #region JSON Data Structures

    public class MaterialMappingCollection
    {
        public Dictionary<string, MaterialMapping> Materials { get; set; }
    }

    public class MaterialMapping
    {
        public List<TextureParam> TextureParameters { get; set; } = new();
        public List<NumericParam> NumericParameters { get; set; } = new();
    }

    public class TextureParam
    {
        public int Index { get; set; }
        public string Name { get; set; }
        public string TexturePath { get; set; }
    }

    public class NumericParam
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public string DefaultValue { get; set; }
    }

    #endregion
}
