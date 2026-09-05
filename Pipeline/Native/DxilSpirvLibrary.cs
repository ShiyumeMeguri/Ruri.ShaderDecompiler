using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ruri.ShaderTools.Pipeline.Native;

/// <summary>
/// In-process DXIL/DXBC-container → SPIR-V conversion via the dxil-spirv C API
/// (<c>dxil_spv_*</c>, Hans-Kristian Arntzen's dxil-spirv, the same engine vkd3d-proton uses).
/// Drop-in replacement for shelling out to <c>dxil-spirv.exe</c>: no temp <c>.dxil</c>/<c>.spv</c>
/// files, no process spawn. There is no NuGet distribution of dxil-spirv, so the shared library
/// (<c>dxil-spirv-c-shared.dll</c>) ships under <c>Tools/</c> and is loaded by <see cref="NativeLibraryResolver"/>.
///
/// Behaviour is byte-for-byte equivalent to the previous CLI invocation
/// <c>dxil-spirv --ssbo-uav --ssbo-srv [--raw-llvm]</c>: the two SSBO flags are realised by the
/// SRV/UAV resource remappers below, which reproduce the CLI's <c>remap_srv</c>/<c>remap_uav</c>
/// exactly for this configuration (no <c>--bindless</c>, no root descriptors).
///
/// Performance: the input is pinned in place (no copy); the remapper callbacks are static
/// <c>[UnmanagedCallersOnly]</c> function pointers (no delegate allocation, no marshalling) and
/// touch only stack memory. The per-call thread-allocator scope both satisfies the API contract
/// and bounds native memory (each conversion's scratch is freed at scope end). The single managed
/// allocation is the returned SPIR-V byte[] — the payload. Independent contexts make it fully
/// parallel across the batch worker pool.
/// </summary>
internal static unsafe class DxilSpirvLibrary
{
    private const string Lib = "dxil-spirv-c-shared";

    static DxilSpirvLibrary() => NativeLibraryResolver.EnsureInitialized(null);

    // dxil_spv_resource_kind
    private const int KIND_TYPED_BUFFER = 10;
    private const int KIND_RAW_BUFFER = 11;
    private const int KIND_STRUCTURED_BUFFER = 12;
    // dxil_spv_vulkan_descriptor_type
    private const int DESC_IDENTITY = 0;
    private const int DESC_SSBO = 1;
    // dxil_spv_option (subset; values from dxil_spirv_c.h) — the three options the DXBC→SPIR-V
    // reference driver (dxbc_spirv_sandbox.cpp) enables before converter_run.
    private const int OPT_SHADER_DEMOTE_TO_HELPER = 1;
    private const int OPT_SSBO_ALIGNMENT = 9;
    private const int OPT_MIN_PRECISION_NATIVE_16BIT = 16;

    /// <summary>
    /// Convert a DXIL container (DXBC archive holding a DXIL chunk) or raw LLVM bitcode to SPIR-V.
    /// </summary>
    /// <param name="dxil">DXIL/DXBC bytes (when <paramref name="rawLlvm"/> is false) or raw LLVM bitcode.</param>
    /// <param name="rawLlvm">true → <c>dxil_spv_parse_dxil</c> (raw <c>BC\xC0\xDE</c> bitcode);
    /// false → <c>dxil_spv_parse_dxil_blob</c> (a DXBC/DXContainer-wrapped DXIL chunk).</param>
    public static byte[]? Convert(ReadOnlySpan<byte> dxil, bool rawLlvm, out string? error)
    {
        error = null;
        if (dxil.Length < 4) { error = "DXIL input too small."; return null; }

        // Capture dxil-spirv's own diagnostics for this conversion so a converter
        // failure carries the concrete reason instead of an opaque code.
        EnsureLogCallback();
        ResetThreadLog();

        // Required by the API: every dxil_spv_* call on this thread must run inside an allocator
        // context. Per-call begin/end keeps native scratch memory bounded across a long batch.
        dxil_spv_begin_thread_allocator_context();
        IntPtr blob = IntPtr.Zero, converter = IntPtr.Zero;
        try
        {
            int r;
            fixed (byte* p = dxil)
            {
                r = rawLlvm
                    ? dxil_spv_parse_dxil(p, (nuint)dxil.Length, out blob)
                    : dxil_spv_parse_dxil_blob(p, (nuint)dxil.Length, out blob);
            }
            if (r != 0 || blob == IntPtr.Zero) { error = $"dxil_spv_parse_dxil{(rawLlvm ? "" : "_blob")} failed ({r})."; return null; }

            if (dxil_spv_create_converter(blob, out converter) != 0 || converter == IntPtr.Zero)
            { error = "dxil_spv_create_converter failed."; return null; }

            // Converter options — reproduced from dxil-spirv's own DXBC→SPIR-V reference driver
            // (dxbc_spirv_sandbox.cpp), in its order. `ssbo_alignment = 1` is REQUIRED: dxil-spirv
            // tests `(resource_alignment & (ssbo_alignment - 1)) != 0` to decide an SSBO needs the
            // bindless-only offset buffer; the default 0 makes that mask 0xFFFFFFFF, so EVERY
            // directly-bound (non-bindless) structured/raw SSBO is rejected with "SSBO offset is
            // only supported for bindless SSBOs." — alignment 1 makes the mask 0, so a directly
            // bound SSBO never demands an offset buffer. demote-to-helper and native-16-bit mirror
            // the reference's remaining two options so SM5 discard / min-precision lower identically.
            AddOption(converter, new SsboAlignmentOption { Base = { Type = OPT_SSBO_ALIGNMENT }, Alignment = 1 });
            AddOption(converter, new BoolOption { Base = { Type = OPT_SHADER_DEMOTE_TO_HELPER }, Value = 1 });
            AddOption(converter, new BoolOption { Base = { Type = OPT_MIN_PRECISION_NATIVE_16BIT }, Value = 1 });

            // --ssbo-uav --ssbo-srv: route structured/raw SRV & UAV buffers through SSBO storage
            // (fixes "raw 64-bit load-store must be SSBO/UBO/BDA"). Textures keep IDENTITY.
            dxil_spv_converter_set_srv_remapper(converter, &RemapSrv, null);
            dxil_spv_converter_set_uav_remapper(converter, &RemapUav, null);

            if (dxil_spv_converter_run(converter) != 0)
            { error = $"dxil_spv_converter_run failed.{Detail()}"; return null; }

            if (dxil_spv_converter_get_compiled_spirv(converter, out CompiledSpirv compiled) != 0 ||
                compiled.Data == IntPtr.Zero || compiled.Size == 0)
            { error = "dxil_spv_converter_get_compiled_spirv returned no data."; return null; }

            var result = new byte[(int)compiled.Size];
            Marshal.Copy(compiled.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            if (converter != IntPtr.Zero) dxil_spv_converter_free(converter);
            if (blob != IntPtr.Zero) dxil_spv_parsed_blob_free(blob);
            dxil_spv_end_thread_allocator_context();
        }
    }

    // === Resource remappers — exact reproduction of dxil-spirv's CLI remap_srv/remap_uav for
    // the (bindless=false, root_descriptors=empty, ssbo_uav=ssbo_srv=true) configuration. ===

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int RemapSrv(void* userdata, D3DBinding* binding, SrvVulkanBinding* vk)
    {
        *vk = default;
        if (IsGlobalHeap(binding))
        {
            vk->Buffer.UseHeap = 1;
            vk->Buffer.Set = 0;
            vk->Buffer.Binding = 0;
        }
        else
        {
            vk->Buffer.UseHeap = 0;
            vk->Buffer.Set = binding->RegisterSpace;
            vk->Buffer.Binding = binding->RegisterIndex;
        }

        if (binding->Kind == KIND_STRUCTURED_BUFFER || binding->Kind == KIND_RAW_BUFFER)
            vk->Buffer.DescriptorType = DESC_SSBO;

        // "In case it's needed, place offset buffer here" — verbatim from the reference remapper.
        // With the converter's ssbo_alignment option = 1 (see Convert) the offset buffer is never
        // actually engaged for a directly-bound SSBO, so this is a harmless reservation.
        vk->Offset.Set = 15;
        vk->Offset.Binding = 0;
        return 1; // DXIL_SPV_TRUE
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int RemapUav(void* userdata, UavD3DBinding* binding, UavVulkanBinding* vk)
    {
        *vk = default;
        D3DBinding* d3d = &binding->D3D;
        if (IsGlobalHeap(d3d))
        {
            vk->Buffer.UseHeap = 1;
            vk->Buffer.Set = 0;
            vk->Buffer.Binding = 0;
        }
        else
        {
            vk->Buffer.UseHeap = 0;
            vk->Buffer.Set = d3d->RegisterSpace;
            vk->Buffer.Binding = d3d->RegisterIndex;
        }

        if (d3d->Kind == KIND_STRUCTURED_BUFFER || d3d->Kind == KIND_RAW_BUFFER)
            vk->Buffer.DescriptorType = DESC_SSBO;

        // "In case it's needed, place offset buffer here" — verbatim from the reference remapper.
        // ssbo_alignment = 1 (see Convert) means it is never engaged for a directly-bound SSBO.
        vk->Offset.Set = 15;
        vk->Offset.Binding = 0;

        if ((binding->HasCounter & 0xFF) != 0)
        {
            vk->Counter.UseHeap = 0;
            vk->Counter.Set = 7;
            vk->Counter.Binding = d3d->ResourceIndex;
        }
        return 1; // DXIL_SPV_TRUE
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsGlobalHeap(D3DBinding* b)
        => b->RegisterIndex == uint.MaxValue && b->RegisterSpace == uint.MaxValue && b->RangeSize == uint.MaxValue;

    // === Blittable struct mirrors of dxil_spirv_c.h (LayoutKind.Sequential matches the C ABI;
    // dxil_spv_bool modelled as int — its 4-byte slot/padding makes every later field offset
    // identical whether the C typedef is 1 or 4 bytes). ===

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DBinding   // dxil_spv_d3d_binding (28 bytes)
    {
        public int Stage;
        public int Kind;
        public uint ResourceIndex;
        public uint RegisterSpace;
        public uint RegisterIndex;
        public uint RangeSize;
        public uint Alignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VulkanBinding   // dxil_spv_vulkan_binding (24 bytes)
    {
        public uint Set;
        public uint Binding;
        public uint RootOrInputAttachmentIndex;   // union { root_constant_index; input_attachment_index; }
        public uint HeapRootOffset;                // bindless.heap_root_offset
        public int UseHeap;                        // bindless.use_heap (dxil_spv_bool)
        public int DescriptorType;                 // dxil_spv_vulkan_descriptor_type
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SrvVulkanBinding   // dxil_spv_srv_vulkan_binding (48 bytes)
    {
        public VulkanBinding Buffer;
        public VulkanBinding Offset;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UavD3DBinding   // dxil_spv_uav_d3d_binding (32 bytes)
    {
        public D3DBinding D3D;
        public int HasCounter;     // dxil_spv_bool
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UavVulkanBinding   // dxil_spv_uav_vulkan_binding (72 bytes)
    {
        public VulkanBinding Buffer;
        public VulkanBinding Counter;
        public VulkanBinding Offset;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CompiledSpirv   // dxil_spv_compiled_spirv
    {
        public IntPtr Data;
        public nuint Size;
    }

    // dxil_spv_option_base { dxil_spv_option type; } and the three concrete option structs the
    // DXBC reference driver builds. dxil_spv_converter_add_option copies the option, so passing
    // the address of a stack temporary is safe.
    [StructLayout(LayoutKind.Sequential)]
    private struct OptionBase { public int Type; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SsboAlignmentOption   // dxil_spv_option_ssbo_alignment { base; unsigned alignment; }
    {
        public OptionBase Base;
        public uint Alignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BoolOption   // shader_demote_to_helper / min_precision_native_16bit { base; dxil_spv_bool; }
    {
        public OptionBase Base;
        public int Value;
    }

    private static void AddOption<T>(IntPtr converter, T option) where T : unmanaged
        => dxil_spv_converter_add_option(converter, &option);

    // === P/Invoke (dxil_spirv_c.h). ===
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void dxil_spv_begin_thread_allocator_context();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void dxil_spv_end_thread_allocator_context();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int dxil_spv_parse_dxil_blob(byte* data, nuint size, out IntPtr blob);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int dxil_spv_parse_dxil(byte* data, nuint size, out IntPtr blob);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int dxil_spv_create_converter(IntPtr blob, out IntPtr converter);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int dxil_spv_converter_add_option(IntPtr converter, void* option);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void dxil_spv_converter_set_srv_remapper(
        IntPtr converter, delegate* unmanaged[Cdecl]<void*, D3DBinding*, SrvVulkanBinding*, int> remapper, void* userdata);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void dxil_spv_converter_set_uav_remapper(
        IntPtr converter, delegate* unmanaged[Cdecl]<void*, UavD3DBinding*, UavVulkanBinding*, int> remapper, void* userdata);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int dxil_spv_converter_run(IntPtr converter);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int dxil_spv_converter_get_compiled_spirv(IntPtr converter, out CompiledSpirv compiled);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void dxil_spv_converter_free(IntPtr converter);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void dxil_spv_parsed_blob_free(IntPtr blob);

    // dxil-spirv's per-thread diagnostic sink. Without it a converter failure is an
    // opaque non-zero return; with it dxil-spirv reports the concrete reason
    // ("Unsupported opcode ...", "Resource ... could not be allocated", etc.),
    // which is the only way to act on a `dxil_spv_converter_run` failure. This build
    // exports ONLY the thread-local setter (there is no global `dxil_spv_set_log_callback`),
    // which suits the parallel batch perfectly: every worker thread installs its own sink
    // into its own [ThreadStatic] buffer, so the shaders' diagnostics never interleave.
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void dxil_spv_set_thread_log_callback(delegate* unmanaged[Cdecl]<void*, int, byte*, void> callback, void* userdata);

    [ThreadStatic] private static System.Text.StringBuilder? _threadLog;
    [ThreadStatic] private static bool _threadCallbackInstalled;

    [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static void OnDxilSpvLog(void* userdata, int level, byte* message)
    {
        if (message == null) return;
        try
        {
            string? text = Marshal.PtrToStringUTF8((IntPtr)message);
            if (!string.IsNullOrWhiteSpace(text))
                (_threadLog ??= new System.Text.StringBuilder()).Append(text!.Trim()).Append("; ");
        }
        catch { /* a diagnostic must never throw across the native boundary */ }
    }

    // Install the log sink once, and return the messages captured on THIS thread
    // since the last reset (call ResetThreadLog() before the operation).
    private static void EnsureLogCallback()
    {
        if (_threadCallbackInstalled) return;
        _threadCallbackInstalled = true;   // mark first so a missing export can never retry-throw per call
        try { dxil_spv_set_thread_log_callback(&OnDxilSpvLog, null); }
        catch { /* a diagnostic sink is best-effort — never fail a conversion over it */ }
    }
    private static void ResetThreadLog() => _threadLog?.Clear();
    private static string ThreadLog() => _threadLog is { Length: > 0 } sb ? sb.ToString().Trim() : string.Empty;
    private static string Detail() { string l = ThreadLog(); return l.Length > 0 ? " dxil-spirv: " + l : string.Empty; }
}
