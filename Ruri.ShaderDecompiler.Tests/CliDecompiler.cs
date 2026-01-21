using System.Diagnostics;

namespace Ruri.ShaderDecompiler.Tests;

/// <summary>
/// Fallback CLI-based decompiler for testing when native DLLs are unavailable.
/// </summary>
public static class CliDecompiler
{
    // spirv-cross.exe is in the old UniversalShaderToolkit bin folder
    private static readonly string ToolsDir = @"d:\Ruri\Github\FractalUE\UniversalShaderToolkit\Ruri.ShaderDecompiler\bin\Debug\net8.0";

    public static string? DecompileSpirV(byte[] spirv, string outputPath)
    {
        string spirvPath = Path.GetTempFileName();
        
        try
        {
            File.WriteAllBytes(spirvPath, spirv);

            string spirvCross = Path.Combine(ToolsDir, "spirv-cross.exe");
            if (!File.Exists(spirvCross))
            {
                Console.WriteLine($"[CliDecompiler] ERROR: spirv-cross.exe not found at {spirvCross}");
                return null;
            }

            var psi = new ProcessStartInfo
            {
                FileName = spirvCross,
                Arguments = $"\"{spirvPath}\" --hlsl --shader-model 60 --output \"{outputPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                Console.WriteLine("[CliDecompiler] ERROR: Failed to start spirv-cross");
                return null;
            }
            
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                Console.WriteLine($"[CliDecompiler] ERROR: spirv-cross failed (code {process.ExitCode})");
                if (!string.IsNullOrEmpty(stderr))
                    Console.WriteLine($"  {stderr.Trim()}");
                return null;
            }

            if (File.Exists(outputPath))
            {
                return File.ReadAllText(outputPath);
            }

            Console.WriteLine($"[CliDecompiler] ERROR: Output not created");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CliDecompiler] EXCEPTION: {ex.Message}");
            return null;
        }
        finally
        {
            try { if (File.Exists(spirvPath)) File.Delete(spirvPath); } catch { }
        }
    }
}
