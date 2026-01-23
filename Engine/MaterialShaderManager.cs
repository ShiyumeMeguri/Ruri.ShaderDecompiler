using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using CUE4Parse.Utils;

namespace Ruri.ShaderDecompiler.Engine
{
    public class MaterialShaderManager
    {
        public Dictionary<int, string> ShaderIndexToNameMap { get; private set; } = new();

        public void ScanMaterials(string contentDir)
        {
            Console.WriteLine($"[Scan] Scanning materials in: {contentDir}");
            
            // Initialize Provider using correct constructor
            // DefaultFileProvider(DirectoryInfo directory, SearchOption searchOption, VersionContainer? versions = null, StringComparer? pathComparer = null)
            var version = new VersionContainer(EGame.GAME_UE5_4);
            var provider = new DefaultFileProvider(new DirectoryInfo(contentDir), SearchOption.AllDirectories, version, StringComparer.OrdinalIgnoreCase); 
            provider.Initialize();
            provider.ReadShaderMaps = true;
            
            // Allow reading shader maps
            // Note: CUE4Parse implementation of DeserializeInlineShaderMaps relies on this? 
            // Actually UMaterial.cs checks "Owner.Provider.ReadShaderMaps".
            // We need to set it if exposed, or ensure we pass the right context.
            // CUE4Parse ProviderOptions isn't directly settable on DefaultFileProvider in all versions, 
            // but let's check if we can set it.
            // Accessing internal provider?
            
            // Actually, let's just try loading. CUE4Parse usually defaults to skipping heavy data.
            // UMaterial checking: if (Ar is { Game: >= EGame.GAME_UE4_25, Owner.Provider.ReadShaderMaps: true })
            // We need to ensure we can set ReadShaderMaps.
            // If we can't easily, we might need a custom provider or modified CUE4Parse.
            // BUT, for now, let's assume we can or hack it.
            // Wait, DefaultFileProvider has no public "ReadShaderMaps" property usually.
            // However, we can try to rely on Defaults?
            // "DefaultFileProvider" inherits "AbstractFileProvider".
            // Let's check if we can generic load.

            int count = 0;
            Console.WriteLine($"[Scan] Found {provider.Files.Count} files in provider.");

            foreach (var kvp in provider.Files)
            {
                if (!kvp.Value.Path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) continue;

                // Simple check if it's a material without full load first?
                // Or just TryLoad and check type.
                try
                {
                    var obj = provider.LoadPackageObject(kvp.Value.Path);
                    if (obj is UMaterial material)
                    {
                        ProcessMaterial(material, kvp.Value.Path);
                        count++;
                        if (count % 100 == 0) Console.Write(".");
                    }
                    else if (obj is UMaterialInstance materialInst)
                    {
                        // Material Instances might also have shader maps if they override enough?
                        // Usually they reference the Parent, but if they have static parameters, they might have a cooked shader map?
                        // UMaterialInstance : UMaterialInterface
                        // UMaterialInterface has DeserializeInlineShaderMaps.
                        // So yes.
                        ProcessMaterialInterface(materialInst, kvp.Value.Path);
                        count++;
                        if (count % 100 == 0) Console.Write(".");
                    }
                }
                catch (Exception)
                {
                    // Ignore load errors (encryption, version mismatch, etc)
                }
            }
            Console.WriteLine();
            Console.WriteLine($"[Scan] Processed {count} material assets.");
            Console.WriteLine($"[Scan] Mapped {ShaderIndexToNameMap.Count} unique shader indices.");
        }

        private void ProcessMaterial(UMaterial material, string path)
        {
            ProcessMaterialInterface(material, path);
        }

        private void ProcessMaterialInterface(UMaterialInterface material, string path)
        {
             // Ensure inline shader maps are loaded
             // access material.LoadedMaterialResources
             if (material.LoadedMaterialResources == null) return;

             string assetName = Path.GetFileNameWithoutExtension(path);

             foreach (var resource in material.LoadedMaterialResources)
             {
                 if (resource.LoadedShaderMap == null) continue;
                 
                 // Access shaders in the map
                 // FMaterialShaderMap -> GetShaderList? Or direct access to content?
                 // CUE4Parse FMaterialShaderMap might abstract content.
                 // We need to access the "Code" or "Content" to find ResourceIndices.

                 var content = resource.LoadedShaderMap.Content; // FShaderMapContent
                 if (content == null) continue;

                 // Iterate Shaders
                 if (content.Shaders != null)
                 {
                     foreach (var shader in content.Shaders)
                     {
                         if (shader == null) continue;
                         // shader.ResourceIndex is the key into the .ushaderlib
                         // shader.Type is the type index? No, FShader has Type ptr.
                         // But we want a name.
                         
                         // We can construct a name from AssetName + ShaderType
                         // But getting ShaderType name might be hard if it's just a Hash/Index.
                         // FShader.Type is 'ulong' (TIndexedPtr).
                         
                         int idx = shader.ResourceIndex;
                         if (idx >= 0 && !ShaderIndexToNameMap.ContainsKey(idx))
                         {
                             // Create a descriptive name
                             // Ideally: MaterialName_VertexFactory_ShaderType
                             // We might not have full strings for VF/Type if they are HashedNames?
                             // But let's check FShader definition in CUE4Parse again.
                             
                             // For now, use AssetName + "_Shader_" + idx as a fallback, 
                             // but we really want the shader type.
                             // FShaderMapContent has 'ShaderTypes' array of FHashedName?
                             
                             string extra = "";
                             // Try to match shader.Type index to ShaderTypes array?
                             // FShaderMapContent: public FHashedName[] ShaderTypes;
                             // But FShader.Type is ulong? CUE4Parse implementation details...
                             
                             // Let's just use AssetName for now, it's a huge improvement.
                             string name = $"{assetName}_Shader_{idx}";
                             ShaderIndexToNameMap[idx] = name;
                         }
                     }
                 }
                 
                 // Also Pipelines?
                 if (content.ShaderPipelines != null)
                 {
                     foreach(var pipeline in content.ShaderPipelines)
                     {
                         if (pipeline?.Shaders == null) continue;
                         foreach(var shader in pipeline.Shaders)
                         {
                             if (shader == null) continue;
                             int idx = shader.ResourceIndex;
                             if (idx >= 0 && !ShaderIndexToNameMap.ContainsKey(idx))
                             {
                                 string name = $"{assetName}_Pipeline_{idx}";
                                 ShaderIndexToNameMap[idx] = name;
                             }
                         }
                     }
                 }
             }
        }
    }
}
