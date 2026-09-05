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
/// Both are restored under <c>runtimes/&lt;rid&gt;/native</c> beside whichever assembly the
/// restore was for. This assembly's own folder is probed first: a host that loads the decompiler
/// as a module from a folder other than its own base directory has the natives beside the
/// module, not beside the host. The app base directory follows, then any extra directory a host
/// names. A single <see cref="NativeLibrary.SetDllImportResolver"/> hook loads each library by
/// full path; on Windows a rooted-path load uses <c>LOAD_WITH_ALTERED_SEARCH_PATH</c> so any
/// transitive dependency resolves from the same dir. The bindings register the hook themselves
/// before their first call, so no caller has to remember to.
/// </summary>
internal static class NativeLibraryResolver
{
    private static readonly object Gate = new();
    private static bool registered;
    private static string[] searchDirectories = Array.Empty<string>();

    /// <summary>Registers the resolver once per process and adds one more directory to probe, in either order. Safe to call repeatedly / concurrently.</summary>
    public static void EnsureInitialized(string? toolsDir)
    {
        lock (Gate)
        {
            if (!registered)
            {
                registered = true;
                searchDirectories = DefaultDirectories();
                NativeLibrary.SetDllImportResolver(typeof(NativeLibraryResolver).Assembly, Resolve);
            }
            if (!string.IsNullOrWhiteSpace(toolsDir) && Directory.Exists(toolsDir) && !searchDirectories.Contains(toolsDir, StringComparer.OrdinalIgnoreCase))
            {
                searchDirectories = [.. searchDirectories, toolsDir];
            }
        }
    }

    /// <summary>
    /// NuGet is the single source of truth for both in-process natives, restored under
    /// runtimes/&lt;rid&gt;/native, so those folders are probed first — ahead of any legacy Tools/
    /// dir. A stale loose dxil-spirv-c-shared.dll left in Tools/ from the retired dxilconv route
    /// is the older, DXIL-only build; if it shadowed the NuGet native it would parse every DXBC
    /// blob as -4 (the whole archive collapses to a handful of decompiles).
    /// </summary>
    private static string[] DefaultDirectories()
    {
        string rid = "win-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        List<string> directories = new();
        void Add(string? directory)
        {
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory) && !directories.Contains(directory, StringComparer.OrdinalIgnoreCase))
            {
                directories.Add(directory);
            }
        }
        string assemblyLocation = typeof(NativeLibraryResolver).Assembly.Location;
        string? assemblyDirectory = assemblyLocation.Length > 0 ? Path.GetDirectoryName(assemblyLocation) : null;
        foreach (string? root in new[] { assemblyDirectory, AppContext.BaseDirectory })
        {
            if (root is null)
            {
                continue;
            }
            Add(Path.Combine(root, "runtimes", rid, "native"));
            Add(Path.Combine(root, "runtimes", "win-x64", "native"));
            Add(root);
        }
        Add(Path.Combine(AppContext.BaseDirectory, "Tools"));
        return directories.ToArray();
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        string fileName = libraryName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? libraryName
            : libraryName + ".dll";

        foreach (string dir in searchDirectories)
        {
            string full = Path.Combine(dir, fileName);
            if (File.Exists(full) && NativeLibrary.TryLoad(full, out IntPtr handle))
                return handle;
        }

        // Not one of ours (or not found here) — let the default resolver try.
        return IntPtr.Zero;
    }
}
