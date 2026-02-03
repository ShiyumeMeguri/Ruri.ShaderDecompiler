using System;
using System.IO;
using Ruri.ShaderDecompiler;
using Ruri.ShaderDecompiler.Engine;
using Ruri.ShaderDecompiler.Utils;
using System.Linq;
using Newtonsoft.Json;
using Ruri.ShaderDecompiler.Intermediate;
using Ruri.ShaderDecompiler.Unreal;

namespace Ruri.ShaderDecompiler
{
    class Program
    {
        static int Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("Usage: ShaderDecompiler.exe <input> [mode] [output] [--keep-temps] [--mapping <path>]");
                return 1;
            }

            string inputPath = Path.GetFullPath(args[0]);
            string mode = args.Length > 1 ? args[1] : "";
            string? outputPath = args.Length > 2 ? args[2] : null;
            bool keepTemps = false;
            string? scanAssetsPath = null;
            string? mappingPath = null;

            var nameMap = new Dictionary<int, string>();
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--keep-temps") keepTemps = true;
                else if (args[i] == "--scan-assets" && i + 1 < args.Length)
                {
                    scanAssetsPath = args[i + 1];
                    i++; // Skip next arg
                }
                else if (args[i] == "--mapping" && i + 1 < args.Length)
                {
                    mappingPath = args[i + 1];
                    i++; 
                }
                else if (args[i] == "--restore-symbols" && i + 2 < args.Length)
                {
                    string matDir = args[i+1];
                    string arcDir = args[i+2];
                    nameMap = ShaderBindingExtractor.ScanAndRestore(matDir, arcDir);
                    i += 2;
                }
                else if (args[i].StartsWith("-")) mode = args[i];

                else if (args[i] != inputPath) outputPath = args[i];
            }

            if (!File.Exists(inputPath))
            {
                Console.Error.WriteLine($"Error: Input file '{inputPath}' not found.");
                return 1;
            }

            // Handle .ushaderlib
            if (inputPath.EndsWith(".ushaderlib", StringComparison.OrdinalIgnoreCase))
            {
                return ProcessUnrealLibrary(inputPath, outputPath, keepTemps, scanAssetsPath, mappingPath, nameMap);
            }

            // Legacy single file mode logic
            if(string.IsNullOrEmpty(mode))
            {
                 mode = "-unknown"; // Treat as unknown/auto
            }


            try
            {
                var format = ParseFormat(mode);
                var binary = File.ReadAllBytes(inputPath);
                
                using var decompiler = new ShaderDecompiler(); 
                
                var result = decompiler.Decompile(binary, format, null, 50);
                
                if (result.Success)
                {
                    if (outputPath != null) 
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                        File.WriteAllText(outputPath, result.HlslSource);
                    }
                    else Console.WriteLine(result.HlslSource);
                    return 0;
                }
                else
                {
                     Console.Error.WriteLine($"Decompilation failed: {result.ErrorMessage}");
                     return 1;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Fatal Error: {ex.Message}");
                return 1;
            }
        }

        static int ProcessUnrealLibrary(string inputPath, string? outputPath, bool keepTemps, string? scanAssetsPath, string? mappingPath, Dictionary<int, string>? nameMapInput = null)
        {
            try 
            {
                var nameMap = nameMapInput ?? new Dictionary<int, string>();
                var usageMap = new Dictionary<int, HashSet<string>>();

                // 1. Scan Assets (Legacy)
                if (!string.IsNullOrEmpty(scanAssetsPath))
                {
                    try
                    {
                        var manager = new MaterialShaderManager();
                        manager.ScanMaterials(scanAssetsPath);
                        foreach (var kv in manager.ShaderIndexToNameMap)
                        {
                            nameMap[kv.Key] = kv.Value;
                        }
                    }
                    catch(Exception ex)
                    {
                         Console.WriteLine($"[Warning] Material scan failed: {ex.Message}");
                    }
                }

                var lib = UnrealShaderLibraryReader.Read(inputPath);
                Console.WriteLine($"Read Library: {lib.Version} Version, {lib.ShaderEntries.Length} shaders.");

                // 2. Auto-Detect Mapping if not provided
                if (string.IsNullOrEmpty(mappingPath))
                {
                    var dir = Path.GetDirectoryName(inputPath);
                    while (dir != null)
                    {
                        var candidate = Path.Combine(dir, "ShaderMappings.json");
                        if (File.Exists(candidate))
                        {
                            mappingPath = candidate;
                            Console.WriteLine($"[Auto-Detect] Found mapping file: {mappingPath}");
                            break;
                        }
                        var parent = Directory.GetParent(dir);
                        if (parent == null) break;
                        dir = parent.FullName;
                    }
                }

                // 3. Load Shader Mappings (JSON)
                if (!string.IsNullOrEmpty(mappingPath) && File.Exists(mappingPath))
                {
                     try
                     {
                         Console.WriteLine($"Loading mapping from {mappingPath}...");
                         var json = File.ReadAllText(mappingPath);
                         var mapping = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(json);
                         
                         var hashToMats = new Dictionary<string, HashSet<string>>();
                         if(mapping != null)
                         {
                             foreach(var kvp in mapping)
                             {
                                 foreach(var hash in kvp.Value)
                                 {
                                     if (!hashToMats.ContainsKey(hash)) hashToMats[hash] = new HashSet<string>();
                                     // Store FULL path for precise mapping
                                     hashToMats[hash].Add(kvp.Key);
                                 }
                             }
                             
                             Console.WriteLine($"Loaded {mapping.Count} material mappings.");

                             int mapCount = Math.Min(lib.ShaderMapEntries.Length, lib.ShaderMapHashes.Count); 
                             int mappedShaders = 0;

                             for(int i=0; i<mapCount; i++)
                             {
                                 var hash = lib.ShaderMapHashes[i];
                                 if (hashToMats.TryGetValue(hash, out var mats))
                                 {
                                     var entry = lib.ShaderMapEntries[i];
                                     // "Use first material name" rule from user
                                     // NOTE: We must extract simple name for file naming, but keep full path in usedBy for mapper
                                     string fullMaterialPath = mats.FirstOrDefault() ?? "Unknown";
                                     var niceName = Path.GetFileNameWithoutExtension(fullMaterialPath);
                                     if (string.IsNullOrEmpty(niceName)) niceName = "UnknownMaterial"; 
                                     
                                     for(uint k=0; k<entry.NumShaders; k++)
                                     {
                                         long idxInternal = entry.ShaderIndicesOffset + k;
                                         if(idxInternal < lib.ShaderIndices.Length)
                                         {
                                             uint sIdx = lib.ShaderIndices[idxInternal];
                                             
                                             // Update Usage Map
                                             if (!usageMap.ContainsKey((int)sIdx)) usageMap[(int)sIdx] = new HashSet<string>();
                                             foreach (var m in mats) usageMap[(int)sIdx].Add(m);
                                             
                                             // Update Name Map (First wins)
                                             if(!nameMap.ContainsKey((int)sIdx))
                                             {
                                                 nameMap[(int)sIdx] = niceName;
                                                 mappedShaders++;
                                             }
                                         }
                                     }
                                 }
                             }
                             Console.WriteLine($"Mapped {mappedShaders} shaders using JSON mapping.");
                         }
                     }
                     catch(Exception ex) { Console.WriteLine($"[Warning] JSON Mapping failed: {ex.Message}"); }
                }

                // 4. Load Material Parameter Mappings (Precise)
                Ruri.ShaderDecompiler.Unreal.PreciseParameterMapper? preciseMapper = null;
                if (!string.IsNullOrEmpty(mappingPath))
                {
                    string paramMappingPath = Path.Combine(Path.GetDirectoryName(mappingPath)!, "MaterialParameterMappings.json");
                    if (File.Exists(paramMappingPath))
                    {
                        Console.WriteLine($"[信息] 加载参数映射文件: {paramMappingPath}");
                        preciseMapper = Ruri.ShaderDecompiler.Unreal.PreciseParameterMapper.LoadFromFile(paramMappingPath);
                    }
                    else
                    {
                        Console.WriteLine($"[警告] 未找到参数映射文件: {paramMappingPath} (将无法进行变量重命名)");
                    }
                }

                if (outputPath == null) 
                    outputPath = Path.Combine(Path.GetDirectoryName(inputPath)!, Path.GetFileNameWithoutExtension(inputPath) + "_Export");
                
                Directory.CreateDirectory(outputPath);

                using var decompiler = new ShaderDecompiler(outputPath);
                int successCount = 0;

                for(int i=0; i<lib.ShaderEntries.Length; i++)
                {
                    var code = lib.GetShaderCode(i);
                    var entry = lib.ShaderEntries[i];
                    Console.WriteLine($"Shader {i}: Size={entry.Size}, Uncompressed={entry.UncompressedSize}, Offset={entry.Offset}");
                    
                    if (code == null) continue;
                    
                    string typeSuffix = GetShaderFreqString(entry.Frequency);

                    try 
                    {
                        var res = decompiler.Decompile(code, ShaderFormat.Unknown, null, 50);
                        if (res.Success)
                        {
                            string finalName = "";
                            
                            // 1. Try Map
                            if (nameMap.ContainsKey(i)) finalName = nameMap[i];
                            // 2. Try embedded name
                            else if (!string.IsNullOrEmpty(res.ShaderName)) finalName = res.ShaderName;
                            else finalName = "UnknownShader";
                            
                            finalName = string.Join("_", finalName.Split(Path.GetInvalidFileNameChars()));
                            string outName = $"{finalName}_{typeSuffix}_{i}.hlsl";
                            
                            // Inject Header
                            if (usageMap.TryGetValue(i, out var usedBy))
                            {
                                 var sb = new System.Text.StringBuilder();
                                 sb.AppendLine("/*");
                                 sb.AppendLine(" * UE Shader Info");
                                 sb.AppendLine($" * Index: {i}");
                                 sb.AppendLine($" * Stage: {typeSuffix}");
                                 sb.AppendLine($" * Used by {usedBy.Count} Materials:");
                                 
                                 // Try to find a material with precise mapping
                                 MaterialMapping? bestMapping = null;
                                 string bestMaterialName = "";

                                 foreach(var m in usedBy) 
                                 {
                                     if (bestMapping == null && preciseMapper != null)
                                     {
                                         // Debug match attempts
                                         bestMapping = preciseMapper.GetByMaterialPath(m);
                                         if (bestMapping != null) 
                                         {
                                             bestMaterialName = m;
                                             Console.WriteLine($"[调试] 成功匹配: '{m}'");
                                         }
                                         else
                                         {
                                            // Only log first few failures to avoid spam
                                            if (usedBy.Count < 5) Console.WriteLine($"[调试] 匹配失败: '{m}'");
                                         }
                                     }
                                     
                                     // Limit list in header
                                     if (usedBy.Count <= 20 || m == bestMaterialName)
                                         sb.AppendLine($" *  - {m}");
                                 }
                                 
                                 if(usedBy.Count > 20) sb.AppendLine($" *  ... and {usedBy.Count-20} more");
                                 sb.AppendLine(" */");
                                 sb.AppendLine("");

                                 ShaderSymbolMetadata? injectionSymbols = null;

                                 // Inject Precise Parameter Mapping Header & Prepare Symbols
                                 if (bestMapping != null && preciseMapper != null)
                                 {
                                     Console.WriteLine($"[信息] Shader {i} 匹配到材质: {bestMaterialName} (包含参数映射)");
                                     string mappingHeader = preciseMapper.GenerateHlslHeader(bestMapping, bestMaterialName);
                                     sb.AppendLine(mappingHeader);

                                     // Prepare symbols for native injection
                                     injectionSymbols = preciseMapper.GetSymbolMetadata(bestMapping);
                                 }
                                 else
                                 {
                                     // Console.WriteLine($"[警告] Shader {i} 未找到精确材质映射");
                                 }

                                 // Re-decompile with symbols if available to get native variable names
                                 if (injectionSymbols != null)
                                 {
                                     try 
                                     {
                                         // Decompile again, this time with symbols which will be patched into SPIR-V
                                         var resWithSymbols = decompiler.Decompile(code, ShaderFormat.Unknown, injectionSymbols, 50);
                                         if (resWithSymbols.Success)
                                         {
                                              res = resWithSymbols;
                                         }
                                         else
                                         {
                                              Console.WriteLine($"[警告] 符号注入重编译失败 (HLSL生成错误): {resWithSymbols.ErrorMessage}");
                                         }
                                     }
                                     catch (Exception ex) 
                                     {
                                         Console.WriteLine($"[警告] 符号注入重编译异常: {ex.Message}");
                                     }
                                 }

                                 res.HlslSource = sb.ToString() + res.HlslSource;
                            }

                            File.WriteAllText(Path.Combine(outputPath, outName), res.HlslSource);
                            successCount++;
                        }
                        else
                        {
                            Console.WriteLine($"Shader {i}: Decompilation failed: {res.ErrorMessage}");
                        }
                    } 
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Warning: Failed to decompile shader {i}: {ex.Message}");
                    }
                }
                
                Console.WriteLine($"Extracted {lib.ShaderEntries.Length} shaders. Decompiled {successCount}.");
                Console.WriteLine($"Output: {outputPath}");
                return 0;
            }
            catch(Exception ex)
            {
                Console.Error.WriteLine($"Library Error: {ex.Message}");
                return 1;
            }
        }

        static string GetShaderFreqString(byte frequency)
        {
            return frequency switch
            {
                0 => "VS",
                1 => "HS",
                2 => "DS",
                3 => "PS",
                4 => "GS",
                5 => "CS",
                6 => "RG", // RayGen
                7 => "RM", // RayMiss
                8 => "RH", // RayHit
                9 => "RC", // RayCallable
                10 => "MS", // Mesh
                11 => "AS", // Amplification
                _ => $"Freq{frequency}"
            };
        }

        static ShaderFormat ParseFormat(string mode)
        {
             return mode.ToLower() switch {
                 "-dxbc" => ShaderFormat.Dxbc,
                 "-dxil" => ShaderFormat.Dxil,
                 "-spv" => ShaderFormat.SpirV,
                 "-spirv" => ShaderFormat.SpirV,
                 "-unknown" => ShaderFormat.Unknown,
                 _ => ShaderFormat.Unknown
             };
        }

        static (ShaderFormat format, int offset) SniffFormat(byte[] data)
        {
            if (data == null || data.Length < 25) return (ShaderFormat.Unknown, 0);
            
            // UE shader entries have a 21-byte header before the actual DXBC/SPIRV
            // Try offset 21 first, then fallback to other common offsets
            int[] offsets = { 21, 0, 4, 8, 12, 16 };
            foreach (var off in offsets)
            {
                if (off + 4 > data.Length) continue;
                uint magic = BitConverter.ToUInt32(data, off);
                if (magic == 0x43425844) return (ShaderFormat.Dxbc, off); // DXBC
                if (magic == 0x07230203) return (ShaderFormat.SpirV, off); // SPIR-V
            }
            return (ShaderFormat.Unknown, 0);
        }
    }
}
