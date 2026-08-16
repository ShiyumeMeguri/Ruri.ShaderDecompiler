using System;

namespace Ruri.ShaderTools;

// Process-wide singleton access to the live ShaderDecompilerSettings.
// Each host (FModel hook, AssetRipper hook, CLI tool) calls Initialize()
// with the path it owns; settings hooks then read / mutate via Current.
//
// Decoupled from Ruri.Hook so the ShaderDecompiler assembly stays
// dependency-light: the host injects HookModuleSettings load/save
// callbacks (or just the deserialised payload directly via Replace())
// rather than referencing Ruri.Hook from inside Ruri.ShaderDecompiler.
//
// Threadsafe-by-replacement: mutating the live instance isn't supported;
// callers build a new ShaderDecompilerSettings, optionally Save() it,
// then call Replace() to swap the singleton atomically.
public static class ShaderDecompilerSettingsAccess
{
    private static ShaderDecompilerSettings _current = new();
    private static Action<ShaderDecompilerSettings>? _saver;

    /// <summary>
    /// Live snapshot. Always non-null. Initialised to defaults until a
    /// host calls Replace() with a loaded payload.
    /// </summary>
    public static ShaderDecompilerSettings Current => System.Threading.Volatile.Read(ref _current);

    /// <summary>
    /// Atomically replace the live snapshot. Used both by the host's
    /// startup loader AND by the settings-UI commit path. The optional
    /// saver, when present, persists the new value through whatever
    /// channel the host registered (typically a JSON file write).
    /// </summary>
    public static void Replace(ShaderDecompilerSettings settings, bool persist = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        System.Threading.Volatile.Write(ref _current, settings);
        if (persist) _saver?.Invoke(settings);
    }

    /// <summary>
    /// Wire a saver callback the host owns. Called from Replace(persist:true)
    /// after the singleton swap. Pass null to detach (e.g. host shutdown).
    /// </summary>
    public static void RegisterSaver(Action<ShaderDecompilerSettings>? saver)
    {
        _saver = saver;
    }
}
