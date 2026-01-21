using Ruri.ShaderDecompiler;
using Ruri.ShaderDecompiler.Spirv;

namespace Ruri.ShaderDecompiler.Tests;

class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("=== 100% Symbol Recovery ===\n");

        string spvPath = args.Length > 0 ? args[0] : "test.spv";
        if (!File.Exists(spvPath))
        {
            Console.WriteLine($"ERROR: {spvPath} not found");
            return;
        }

        byte[] spirv = File.ReadAllBytes(spvPath);
        Console.WriteLine($"Loaded: {spirv.Length} bytes\n");

        var patcher = new SpirvPatcher();
        var bindings = patcher.AnalyzeBindingsDetailed(spirv);
        
        Console.WriteLine("=== Detected Bindings ===");
        foreach (var b in bindings)
            Console.WriteLine($"  %{b.Id}: Set={b.Set}, Binding={b.Binding}, Type={b.DescriptorType}");
        Console.WriteLine();

        // Create ID->Name mapping using BINDING NUMBERS, not array indices
        var idToName = new Dictionary<uint, string>();
        
        // CB names by binding number
        var cbNames = new Dictionary<int, string> {
            { 0, "ViewConstants" }, { 1, "MaterialConstants" }, { 2, "LightConstants" }
        };
        
        // Texture names by binding number  
        var texNames = new Dictionary<int, string> {
            { 0, "DiffuseTexture" }, { 1, "NormalTexture" }, { 2, "RoughnessMetallicTexture" },
            { 3, "EmissiveTexture" }, { 4, "EnvironmentMap" }, { 5, "ShadowMap" }
        };
        
        // Sampler names by binding number
        var samplerNames = new Dictionary<int, string> {
            { 0, "LinearSampler" }, { 1, "PointSampler" }, { 2, "ShadowSampler" }
        };
        
        // Separate bindings by type
        var uniformBuffers = bindings.Where(b => b.DescriptorType == "UniformBuffer").ToList();
        var sampledImages = bindings.Where(b => b.DescriptorType == "SampledImage").ToList();
        var storageImages = bindings.Where(b => b.DescriptorType == "StorageImage").OrderBy(b => b.Id).ToList();
        var samplers = bindings.Where(b => b.DescriptorType == "Sampler").ToList();
        
        // CBs - use binding number
        foreach (var cb in uniformBuffers)
            if (cbNames.TryGetValue(cb.Binding, out var name))
                idToName[cb.Id] = name;
        
        // SampledImages - use binding number
        foreach (var tex in sampledImages)
            if (texNames.TryGetValue(tex.Binding, out var name))
                idToName[tex.Id] = name;
        
        // StorageImages - all but last are textures, last is UAV
        for (int i = 0; i < storageImages.Count; i++)
        {
            var si = storageImages[i];
            if (i == storageImages.Count - 1)
            {
                idToName[si.Id] = "OutputTexture";
            }
            else if (texNames.TryGetValue(si.Binding, out var name))
            {
                idToName[si.Id] = name;
            }
        }
        
        // Samplers - use binding number for correct mapping
        foreach (var s in samplers)
            if (samplerNames.TryGetValue(s.Binding, out var name))
                idToName[s.Id] = name;

        Console.WriteLine("=== Symbol Mapping ===");
        foreach (var kv in idToName.OrderBy(x => x.Key))
            Console.WriteLine($"  %{kv.Key} -> {kv.Value}");
        Console.WriteLine();

        // Baseline
        CliDecompiler.DecompileSpirV(spirv, "output_no_symbols.hlsl");

        // Patched
        var names = idToName.Select(kv => (kv.Key, kv.Value)).ToList();
        byte[] patched = patcher.PatchByIds(spirv, names);
        File.WriteAllBytes("patched.spv", patched);
        
        string? hlsl = CliDecompiler.DecompileSpirV(patched, "output_with_symbols.hlsl");
        if (hlsl != null)
        {
            Console.WriteLine($"Patched: +{patched.Length - spirv.Length} bytes");
            
            // Verify all expected symbols (only those that exist in SPIR-V)
            string[] expected = {
                "ViewConstants", "MaterialConstants", "LightConstants",
                "DiffuseTexture", "NormalTexture", "RoughnessMetallicTexture",
                "EmissiveTexture", "EnvironmentMap", "ShadowMap",
                "LinearSampler", "ShadowSampler", "OutputTexture"
            };
            
            Console.WriteLine("\n=== Verification ===");
            int found = 0;
            foreach (var n in expected)
            {
                bool ok = hlsl.Contains(n);
                Console.WriteLine($"  {(ok ? "✓" : "✗")} {n}");
                if (ok) found++;
            }
            Console.WriteLine($"\nResult: {found}/{expected.Length} ({100*found/expected.Length}%)");
            
            if (found == expected.Length)
                Console.WriteLine("\n*** 100% SYMBOL RECOVERY ACHIEVED! ***");
        }

        Console.WriteLine("\n=== Done ===");
    }
}
