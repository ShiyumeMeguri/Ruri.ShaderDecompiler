using System.Runtime.InteropServices;
using System.Text;

namespace Ruri.ShaderTools.Pipeline.Native;

/// <summary>
/// In-process SPIR-V → HLSL/GLSL emission via the spirv-cross C API (<c>spvc_*</c>).
/// Drop-in replacement for shelling out to <c>spirv-cross.exe</c>: the SPIR-V words are handed
/// to the library directly and the emitted source is returned as a string — no temp <c>.spv</c>
/// input, no temp <c>.hlsl/.glsl</c> output, no process spawn.
///
/// Performance: the input is taken as <see cref="ReadOnlySpan{T}"/> and pinned in place (never
/// copied); the entry-point name is encoded into a <c>stackalloc</c> buffer; there are no
/// closures or delegates on the hot path. The single managed allocation is the returned source
/// string — the actual payload. The native binary (<c>spirv-cross.dll</c>) comes from the
/// <c>Silk.NET.SPIRV.Cross.Native</c> NuGet package and is resolved by <see cref="NativeLibraryResolver"/>.
/// Each call uses its own <c>spvc_context</c>, so the batch worker pool runs these fully in parallel.
/// </summary>
internal static unsafe class SpirvCrossLibrary
{
    private const string Lib = "spirv-cross";

    private enum Backend { Glsl = 1, Hlsl = 2 }   // spvc_backend
    private const int CaptureTakeOwnership = 1;     // spvc_capture_mode

    // spvc_compiler_option (spirv_cross_c.h). The category high-bits are load-bearing — a bare
    // ordinal silently targets the wrong option — so these mirror the header exactly:
    //   COMMON_BIT = 0x1000000, GLSL_BIT = 0x2000000, HLSL_BIT = 0x4000000.
    private const uint OPT_GLSL_VERSION              =  8u | 0x2000000u;
    private const uint OPT_GLSL_VULKAN_SEMANTICS     = 10u | 0x2000000u;
    private const uint OPT_HLSL_SHADER_MODEL         = 13u | 0x4000000u;
    private const uint OPT_FORCE_ZERO_INIT_VARIABLES = 54u | 0x1000000u;

    /// <summary>
    /// Emit HLSL. Mirrors
    /// <c>spirv-cross in.spv --hlsl --entry E --stage S --shader-model M --force-zero-initialized-variables</c>.
    /// Returns null on failure with the upstream message in <paramref name="error"/>.
    /// </summary>
    public static string? EmitHlsl(ReadOnlySpan<byte> spirv, string? entryPoint, uint executionModel, uint shaderModel, out string? error)
        => Emit(spirv, Backend.Hlsl, entryPoint, executionModel, shaderModel, glslVersion: 0, out error);

    /// <summary>
    /// Emit Vulkan GLSL. Mirrors
    /// <c>spirv-cross in.spv -V --version 460 --vulkan-semantics --entry E --stage S</c>.
    /// </summary>
    public static string? EmitGlsl(ReadOnlySpan<byte> spirv, string? entryPoint, uint executionModel, uint glslVersion, out string? error)
        => Emit(spirv, Backend.Glsl, entryPoint, executionModel, hlslShaderModel: 0, glslVersion, out error);

    private static string? Emit(ReadOnlySpan<byte> spirv, Backend backend, string? entryPoint, uint executionModel,
        uint hlslShaderModel, uint glslVersion, out string? error)
    {
        error = null;
        if (spirv.Length < 4 || (spirv.Length & 3) != 0)
        {
            error = $"SPIR-V byte length {spirv.Length} is not a positive multiple of 4.";
            return null;
        }

        IntPtr context = IntPtr.Zero;
        try
        {
            if (spvc_context_create(out context) != 0 || context == IntPtr.Zero)
            {
                error = "spvc_context_create failed.";
                return null;
            }

            IntPtr parsedIr;
            fixed (byte* words = spirv)   // pin in place, no copy
            {
                if (spvc_context_parse_spirv(context, (uint*)words, (nuint)(spirv.Length >> 2), out parsedIr) != 0)
                    return Fail(context, out error);
            }

            if (spvc_context_create_compiler(context, (int)backend, parsedIr, CaptureTakeOwnership, out IntPtr compiler) != 0)
                return Fail(context, out error);

            if (spvc_compiler_create_compiler_options(compiler, out IntPtr options) != 0)
                return Fail(context, out error);

            if (backend == Backend.Hlsl)
            {
                spvc_compiler_options_set_uint(options, OPT_HLSL_SHADER_MODEL, hlslShaderModel);
                spvc_compiler_options_set_bool(options, OPT_FORCE_ZERO_INIT_VARIABLES, 1);
            }
            else
            {
                spvc_compiler_options_set_uint(options, OPT_GLSL_VERSION, glslVersion);
                spvc_compiler_options_set_bool(options, OPT_GLSL_VULKAN_SEMANTICS, 1);
            }

            if (spvc_compiler_install_compiler_options(compiler, options) != 0)
                return Fail(context, out error);

            // --entry E --stage S combined: the raw SpvExecutionModel from the OpEntryPoint is
            // passed so KHR/NV raytracing aliases select the correct entry. Skipped when no
            // preferred entry — spirv-cross then uses the module's sole entry point.
            if (!string.IsNullOrEmpty(entryPoint))
                SetEntryPoint(compiler, entryPoint, executionModel);

            if (spvc_compiler_compile(compiler, out IntPtr source) != 0 || source == IntPtr.Zero)
                return Fail(context, out error);

            // Copy out before the context (which owns the string) is destroyed in finally.
            return Marshal.PtrToStringUTF8(source);
        }
        finally
        {
            if (context != IntPtr.Zero) spvc_context_destroy(context);
        }
    }

    // Stack-allocate the null-terminated UTF-8 name (shader entry identifiers are short) so the
    // hot path never allocates; only a pathological >511-byte name falls back to the heap.
    private static void SetEntryPoint(IntPtr compiler, string entryPoint, uint executionModel)
    {
        int needed = Encoding.UTF8.GetByteCount(entryPoint) + 1;
        byte[]? rented = needed > 512 ? new byte[needed] : null;
        Span<byte> name = rented ?? stackalloc byte[512];
        int written = Encoding.UTF8.GetBytes(entryPoint, name);
        name[written] = 0;
        fixed (byte* p = name)
            spvc_compiler_set_entry_point(compiler, (IntPtr)p, (int)executionModel);
    }

    private static string? Fail(IntPtr context, out string? error)
    {
        IntPtr message = spvc_context_get_last_error_string(context);
        error = message != IntPtr.Zero ? Marshal.PtrToStringUTF8(message) : "spirv-cross failed (no error string).";
        return null;
    }

    // === P/Invoke (spirv_cross_c.h). spvc_bool is unsigned char → byte. On win-x64 the single
    // unified calling convention makes Cdecl/Stdcall identical, so Cdecl is purely declarative. ===
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int spvc_context_create(out IntPtr context);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void spvc_context_destroy(IntPtr context);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr spvc_context_get_last_error_string(IntPtr context);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int spvc_context_parse_spirv(IntPtr context, uint* spirv, nuint wordCount, out IntPtr parsedIr);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int spvc_context_create_compiler(IntPtr context, int backend, IntPtr parsedIr, int captureMode, out IntPtr compiler);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int spvc_compiler_create_compiler_options(IntPtr compiler, out IntPtr options);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int spvc_compiler_options_set_uint(IntPtr options, uint option, uint value);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int spvc_compiler_options_set_bool(IntPtr options, uint option, byte value);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int spvc_compiler_install_compiler_options(IntPtr compiler, IntPtr options);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int spvc_compiler_set_entry_point(IntPtr compiler, IntPtr name, int model);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int spvc_compiler_compile(IntPtr compiler, out IntPtr source);
}
