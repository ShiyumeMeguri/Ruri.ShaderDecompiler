using System.Runtime.InteropServices;

namespace Ruri.ShaderDecompiler.Native;

/// <summary>
/// P/Invoke wrapper for dxil-spirv-c-shared library (DXIL to SPIR-V conversion).
/// </summary>
public static unsafe class DxilSpirvApi
{
    private const string LibName = "dxil-spirv-c-shared";

    /// <summary>
    /// Converts DXIL bytecode to SPIR-V.
    /// </summary>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "dxil_spirv_convert")]
    public static extern DxilSpirvResult Convert(
        byte* dxilData,
        nuint dxilSize,
        byte** spirvData,
        nuint* spirvSize,
        DxilSpirvConvertOptions* options);

    /// <summary>
    /// Frees memory allocated by dxil_spirv_convert.
    /// </summary>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "dxil_spirv_free")]
    public static extern void Free(byte* spirvData);

    /// <summary>
    /// Gets the last error message.
    /// </summary>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "dxil_spirv_get_last_error")]
    public static extern IntPtr GetLastError();

    // === Result ===
    public enum DxilSpirvResult
    {
        Success = 0,
        Error = -1,
    }

    // === Options ===
    [StructLayout(LayoutKind.Sequential)]
    public struct DxilSpirvConvertOptions
    {
        public uint ShaderModel;    // e.g., 60 for SM 6.0
        public byte RawLlvm;        // 1 if input is raw LLVM bitcode (from dxbc2dxil)
        public byte Padding1;
        public byte Padding2;
        public byte Padding3;
    }
}
