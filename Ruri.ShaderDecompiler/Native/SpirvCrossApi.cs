using System.Runtime.InteropServices;

namespace Ruri.ShaderDecompiler.Native;

/// <summary>
/// P/Invoke wrapper for spirv-cross-c-shared library.
/// </summary>
public static unsafe class SpirvCrossApi
{
    private const string LibName = "spirv-cross-c-shared";

    // === Context ===

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern SpvcResult spvc_context_create(out IntPtr context);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void spvc_context_destroy(IntPtr context);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr spvc_context_get_last_error_string(IntPtr context);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern SpvcResult spvc_context_parse_spirv(
        IntPtr context,
        uint* spirv,
        nuint wordCount,
        out IntPtr parsedIr);

    // === Compiler ===

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern SpvcResult spvc_context_create_compiler(
        IntPtr context,
        SpvcBackend backend,
        IntPtr parsedIr,
        SpvcCaptureMode captureMode,
        out IntPtr compiler);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern SpvcResult spvc_compiler_create_compiler_options(
        IntPtr compiler,
        out IntPtr options);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern SpvcResult spvc_compiler_options_set_uint(
        IntPtr options,
        SpvcCompilerOption option,
        uint value);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern SpvcResult spvc_compiler_install_compiler_options(
        IntPtr compiler,
        IntPtr options);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern SpvcResult spvc_compiler_compile(
        IntPtr compiler,
        out IntPtr source);

    // === Enums ===

    public enum SpvcResult
    {
        Success = 0,
        ErrorInvalidSpirv = -1,
        ErrorUnsupportedSpirv = -2,
        ErrorOutOfMemory = -3,
        ErrorInvalidArgument = -4,
    }

    public enum SpvcBackend
    {
        None = 0,
        Glsl = 1,
        Hlsl = 2,
        Msl = 3,
        Cpp = 4,
        Json = 5,
    }

    public enum SpvcCaptureMode
    {
        Copy = 0,
        TakeOwnership = 1,
    }

    public enum SpvcCompilerOption
    {
        // HLSL options
        HlslShaderModel = 71 | (1 << 24), // HLSL category
        HlslPointSizeCompat = 72 | (1 << 24),
        HlslPointCoordCompat = 73 | (1 << 24),
        HlslSupportNonzeroBaseVertexBaseInstance = 74 | (1 << 24),
        HlslForceSampleQualifier = 81 | (1 << 24),
        HlslEnableDecorationBinding = 84 | (1 << 24),
        HlslFlattenMatrixVertexInputSemantics = 97 | (1 << 24),
    }
}
