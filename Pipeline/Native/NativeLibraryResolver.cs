using System.Reflection;
using System.Runtime.InteropServices;

namespace Ruri.ShaderTools.Pipeline.Native;

/// <summary>
/// Resolves the in-process native libraries the decompiler P/Invokes into. There are no child
/// processes, no disk round-trips, and no loose binaries any more — both natives come from NuGet:
/// <list type="bullet">
///   <item><c>spirv-cross.dll</c> — <c>Silk.NET.SPIRV.Cross.Native</c> (SPIR-V → HLSL/GLSL).</item>
///   <item><c>dxil-spirv-c-shared.dll</c> — <c>AssetRipper.Bindings.DxilSpirV</c> (legacy DXBC and
///   DXIL → SPIR-V; its bundled dxbc-spirv handles SM5 DXBC directly, so no Microsoft dxilconv).</item>
/// </list>
/// Both are restored under <c>runtimes/&lt;rid&gt;/native</c>. A single
/// <see cref="NativeLibrary.SetDllImportResolver"/> hook probes that folder (plus the app base
/// dir) and loads each library by full path; on Windows a rooted-path load uses
/// <c>LOAD_WITH_ALTERED_SEARCH_PATH</c> so any transitive dependency resolves from the same dir.
/// </summary>
internal static class NativeLibraryResolver
{
    private static int _initialized;
    private static string[] _searchDirs = Array.Empty<string>();

    /// <summary>Registers the resolver once per process. Safe to call repeatedly / concurrently.</summary>
    public static void EnsureInitialized(string? toolsDir)
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0) return;

        string baseDir = AppContext.BaseDirectory;
        var dirs = new List<string>();
        void Add(string? d)
        {
            if (!string.IsNullOrWhiteSpace(d) && Directory.Exists(d) &&
                !dirs.Contains(d, StringComparer.OrdinalIgnoreCase))
                dirs.Add(d!);
        }

        // NuGet is the single source of truth for both in-process natives
        // (AssetRipper.Bindings.DxilSpirV → dxil-spirv-c-shared.dll, whose bundled dxbc-spirv
        // parses SM5 DXBC directly; Silk.NET.SPIRV.Cross.Native → spirv-cross.dll). Both restore
        // under runtimes/<rid>/native, so that folder MUST be probed FIRST — ahead of any legacy
        // Tools/ dir. A stale loose dxil-spirv-c-shared.dll left in Tools/ from the retired
        // dxilconv route is the older, DXIL-only build; if it shadows the NuGet native it parses
        // every DXBC blob as -4 (the whole archive collapses to a handful of decompiles).
        string rid = "win-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(); // win-x64 / win-arm64
        Add(Path.Combine(baseDir, "runtimes", rid, "native"));
        Add(Path.Combine(baseDir, "runtimes", "win-x64", "native"));
        Add(baseDir);
        // Legacy fallbacks (loose binaries) — only reached when the NuGet restore is absent.
        Add(toolsDir);
        Add(Path.Combine(baseDir, "Tools"));
        _searchDirs = dirs.ToArray();

        NativeLibrary.SetDllImportResolver(typeof(NativeLibraryResolver).Assembly, Resolve);
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        string fileName = libraryName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? libraryName
            : libraryName + ".dll";

        foreach (string dir in _searchDirs)
        {
            string full = Path.Combine(dir, fileName);
            if (File.Exists(full) && NativeLibrary.TryLoad(full, out IntPtr handle))
                return handle;
        }

        // Not one of ours (or not found here) — let the default resolver try.
        return IntPtr.Zero;
    }
}
