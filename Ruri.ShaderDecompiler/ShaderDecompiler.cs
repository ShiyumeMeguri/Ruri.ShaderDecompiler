using System;
using System.IO;
using System.Runtime.InteropServices;
using Ruri.ShaderDecompiler.Native;
using Ruri.ShaderDecompiler.Spirv;
using Ruri.ShaderDecompiler.Utils;
using Ruri.ShaderDecompiler.Intermediate;
using System.Collections.Generic;
using System.Linq;

namespace Ruri.ShaderDecompiler;

/// <summary>
/// The input shader binary format.
/// </summary>
public enum ShaderFormat
{
    Unknown = 0,
    Dxbc,   // Shader Model 5.x and below
    Dxil,   // Shader Model 6.x
    SpirV,  // Vulkan SPIR-V
}

/// <summary>
/// Result of a decompilation operation.
/// </summary>
public class DecompileResult
{
    public bool Success { get; set; }
    public string? HlslSource { get; set; }
    public string? ErrorMessage { get; set; }
    public byte[]? IntermediateSpirv { get; set; }
}

/// <summary>
/// Main shader decompiler class. Pure C# with native interop (no CLI calls).
/// </summary>
public unsafe class ShaderDecompiler : IDisposable
{
    private readonly SpirvPatcher _patcher = new();
    private bool _disposed;
    
    // Paths to external tools for DXBC fallback
    private readonly string _baseDir;
    private readonly string _dxbc2dxilPath;
    
    // Temp directory for intermediate files
    public string TempDir { get; set; }

    public ShaderDecompiler(string? tempDir = null)
    {
        _baseDir = AppDomain.CurrentDomain.BaseDirectory;
        _dxbc2dxilPath = Path.Combine(_baseDir, "dxbc2dxil.exe");
        TempDir = tempDir ?? _baseDir;
    }

    /// <summary>
    /// Decompiles a shader binary to HLSL with optional symbol injection.
    /// </summary>
    public DecompileResult Decompile(
        byte[] binary,
        ShaderFormat format,
        ShaderSymbolMetadata? symbols = null,
        uint shaderModel = 50)
    {
        string? tempDxbc = null;
        string? tempDxil = null;
        string? tempSpv = null;
        string? tempHlsl = null;

        try
        {
            var bundle = Ruri.ShaderDecompiler.Unreal.UnrealShaderParser.Parse(binary);
            byte[] processingBinary = bundle.NativeCode;
            
            // Auto-detect format if unknown
            if (format == ShaderFormat.Unknown)
            {
                if (bundle.Architecture == Ruri.ShaderDecompiler.Intermediate.ShaderArchitecture.Dxbc) format = ShaderFormat.Dxbc;
                else if (bundle.Architecture == Ruri.ShaderDecompiler.Intermediate.ShaderArchitecture.Dxil) format = ShaderFormat.Dxil;
                else if (bundle.Architecture == Ruri.ShaderDecompiler.Intermediate.ShaderArchitecture.SpirV) format = ShaderFormat.SpirV;
            }

            tempDxbc = Path.Combine(TempDir, $"temp_{Guid.NewGuid():N}.dxbc");
            tempDxil = Path.Combine(TempDir, $"temp_{Guid.NewGuid():N}.dxil");
            tempSpv = Path.Combine(TempDir, $"temp_{Guid.NewGuid():N}.spv");
            tempHlsl = Path.Combine(TempDir, $"temp_{Guid.NewGuid():N}.hlsl");

            byte[] spirv;

            // Step 1: Normalize to SPIR-V using CLI tools
            if (format == ShaderFormat.Dxbc)
            {
                byte[] repairedDxbc = DxbcRepair.ExtractAndRepairDxbc(processingBinary) ?? processingBinary;
                File.WriteAllBytes(tempDxbc, repairedDxbc);

                // DXBC -> DXIL (LLVM BC)
                string args1 = $"\"{tempDxbc}\" -o \"{tempDxil}\" -emit-bc";
                int res = ProcessUtils.RunProcess(Path.Combine(_baseDir, "dxbc2dxil.exe"), args1, out _, out var err);
                if (res != 0) throw new Exception($"dxbc2dxil failed: {err}");

                // DXIL -> SPIR-V
                string args2 = $"\"{tempDxil}\" --output \"{tempSpv}\" --raw-llvm";
                res = ProcessUtils.RunProcess(Path.Combine(_baseDir, "dxil-spirv.exe"), args2, out _, out err);
                if (res != 0) throw new Exception($"dxil-spirv failed: {err}");

                spirv = File.ReadAllBytes(tempSpv);
            }
            else if (format == ShaderFormat.Dxil)
            {
                File.WriteAllBytes(tempDxil, processingBinary);

                // DXIL -> SPIR-V
                string args = $"\"{tempDxil}\" --output \"{tempSpv}\"";
                int res = ProcessUtils.RunProcess(Path.Combine(_baseDir, "dxil-spirv.exe"), args, out _, out var err);
                if (res != 0) throw new Exception($"dxil-spirv failed: {err}");

                spirv = File.ReadAllBytes(tempSpv);
            }
            else if (format == ShaderFormat.SpirV)
            {
                spirv = processingBinary;
            }
            else
            {
                throw new ArgumentException($"Unsupported shader format: {format}");
            }

            // Step 2: Patch SPIR-V with symbols
            byte[] patchedSpirv = spirv;
            var finalSymbols = symbols ?? bundle.Symbols;

            if (finalSymbols != null && finalSymbols.Resources.Count > 0)
            {
                Console.WriteLine($"[Debug] Attempting to patch {finalSymbols.Resources.Count} symbols...");
                var detailedBindings = _patcher.AnalyzeBindingsDetailed(spirv);
                var patches = new List<(uint Id, string Name)>();

                foreach (var r in finalSymbols.Resources)
                {
                    // Find matching binding(s)
                    // We can have multiple variables for the same binding in some weird cases, 
                    // but usually it's one. We filter by type to be sure.
                    var matches = detailedBindings.Where(b => b.Set == r.Set && b.Binding == r.Binding).ToList();
                    
                    if (matches.Count == 0)
                    {
                        Console.WriteLine($"[Debug] Could not find SPIR-V binding for {r.Name} at (Set {r.Set}, Binding {r.Binding})");
                        continue;
                    }

                    foreach (var m in matches)
                    {
                        bool typeMatch = false;
                        switch(r.Type)
                        {
                            case ResourceType.UniformBuffer: typeMatch = m.DescriptorType == "UniformBuffer"; break;
                            case ResourceType.StructuredBuffer: case ResourceType.RWBuffer: typeMatch = m.DescriptorType == "StorageBuffer"; break;
                            case ResourceType.Sampler: typeMatch = m.DescriptorType == "Sampler" || m.DescriptorType == "SampledImage"; break;
                            case ResourceType.Texture: typeMatch = m.DescriptorType == "SampledImage" || m.DescriptorType == "StorageImage" || m.DescriptorType == "Image"; break;
                            case ResourceType.RWTexture: case ResourceType.UAV: typeMatch = m.DescriptorType == "StorageImage" || m.DescriptorType == "StorageBuffer"; break;
                            default: typeMatch = true; break; // Unknown or other
                        }

                        if (typeMatch)
                        {
                            Console.WriteLine($"[Debug] Mapping {r.Name} to Id {m.Id} ({m.DescriptorType})");
                            patches.Add((m.Id, r.Name));
                            
                            // If it's a UniformBuffer, also name the struct type
                            if (m.DescriptorType == "UniformBuffer" && m.StructTypeId.HasValue && m.StructTypeId.Value != 0)
                            {
                                patches.Add((m.StructTypeId.Value, r.Name));
                            }
                        }
                        else
                        {
                             Console.WriteLine($"[Debug] Skipping name {r.Name} for Id {m.Id} because type mismatch ({r.Type} vs {m.DescriptorType})");
                        }
                    }
                }

                if (patches.Count > 0)
                {
                    patchedSpirv = _patcher.PatchByIds(spirv, patches);
                    Console.WriteLine($"[Debug] Patched {patches.Count} entries into SPIR-V.");
                }
            }

            // Step 3: SPIR-V -> HLSL via CLI
            File.WriteAllBytes(tempSpv, patchedSpirv);
            string crossArgs = $"\"{tempSpv}\" --output \"{tempHlsl}\" --hlsl --shader-model {shaderModel}";
            int crossRes = ProcessUtils.RunProcess(Path.Combine(_baseDir, "spirv-cross.exe"), crossArgs, out _, out var crossErr);
            if (crossRes != 0) throw new Exception($"spirv-cross failed: {crossErr}");

            string hlsl = File.ReadAllText(tempHlsl);

            return new DecompileResult
            {
                Success = true,
                HlslSource = hlsl,
                IntermediateSpirv = patchedSpirv
            };
        }
        catch (Exception ex)
        {
            return new DecompileResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
        finally
        {
            // Cleanup temp files if they exist
            if (tempDxbc != null && File.Exists(tempDxbc)) File.Delete(tempDxbc);
            if (tempDxil != null && File.Exists(tempDxil)) File.Delete(tempDxil);
            if (tempSpv != null && File.Exists(tempSpv)) File.Delete(tempSpv);
            if (tempHlsl != null && File.Exists(tempHlsl)) File.Delete(tempHlsl);
        }
    }

    private byte[] ConvertDxbcToSpirv(byte[] rawDxbc) => throw new NotImplementedException();
    private byte[] ConvertDxilToSpirv(byte[] dxil) => throw new NotImplementedException();
    private string CompileSpirVToHlsl(byte[] spirv, uint shaderModel) => throw new NotImplementedException();
    private static void CheckResult(IntPtr context, SpirvCrossApi.SpvcResult result, string message) { }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
