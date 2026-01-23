using System;
using System.IO;
using Ruri.ShaderDecompiler;
using Ruri.ShaderDecompiler.Engine;
using Ruri.ShaderDecompiler.Utils;
using Ruri.ShaderDecompiler.Intermediate;

namespace Ruri.ShaderDecompiler
{
    class Program
    {
        static int Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("Usage: ShaderDecompiler.exe <input> [mode] [output] [--keep-temps]");
                return 1;
            }

            string inputPath = Path.GetFullPath(args[0]);
            string mode = args.Length > 1 ? args[1] : "";
            string? outputPath = args.Length > 2 ? args[2] : null;
            bool keepTemps = false;
            string? scanAssetsPath = null;

            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--keep-temps") keepTemps = true;
                else if (args[i] == "--scan-assets" && i + 1 < args.Length)
                {
                    scanAssetsPath = args[i + 1];
                    i++; // Skip next arg
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
                return ProcessUnrealLibrary(inputPath, outputPath, keepTemps, scanAssetsPath);
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

        static int ProcessUnrealLibrary(string inputPath, string? outputPath, bool keepTemps, string? scanAssetsPath)
        {
            try 
            {
                Dictionary<int, string> nameMap = new();
                if (!string.IsNullOrEmpty(scanAssetsPath))
                {
                    try
                    {
                        var manager = new MaterialShaderManager();
                        manager.ScanMaterials(scanAssetsPath);
                        nameMap = manager.ShaderIndexToNameMap;
                    }
                    catch(Exception ex)
                    {
                         Console.WriteLine($"[Warning] Material scan failed: {ex.Message}");
                    }
                }

                var lib = UnrealShaderLibraryReader.Read(inputPath);
                Console.WriteLine($"Read Library: {lib.Version} Version, {lib.ShaderEntries.Length} shaders.");
                
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
                    
                    var dumpPath = Path.Combine(outputPath, $"Shader_{i}.bin");
                    File.WriteAllBytes(dumpPath, code);

                    // Decompile using auto-detection
                    try 
                    {
                        var res = decompiler.Decompile(code, ShaderFormat.Unknown, null, 50);
                        if (res.Success)
                        {
                            string outName = $"Shader_{i}.hlsl";
                            
                            // Valid name sources order: 
                            // 1. Binary embedded name (Key 'n') - usually missing in Shipping
                            // 2. Material Asset Map
                            
                            if (!string.IsNullOrEmpty(res.ShaderName))
                            {
                                string safeName = string.Join("_", res.ShaderName.Split(Path.GetInvalidFileNameChars()));
                                outName = $"{safeName}.hlsl";
                            }
                            else if (nameMap.ContainsKey(i))
                            {
                                string mappedName = nameMap[i];
                                string safeName = string.Join("_", mappedName.Split(Path.GetInvalidFileNameChars()));
                                outName = $"{safeName}.hlsl";
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
