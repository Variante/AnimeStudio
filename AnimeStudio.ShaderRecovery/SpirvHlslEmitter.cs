using System.Runtime.InteropServices;
using System.Text;

namespace AnimeStudio.ShaderRecovery;

/// <summary>
/// Independent SPIR-V to HLSL boundary. The native implementation is the
/// Apache-2.0 SPIRV-Cross package; no external decompiler source or assembly
/// is involved.
/// </summary>
public static unsafe class SpirvHlslEmitter
{
    private const string Library = "spirv-cross";
    private const int BackendHlsl = 2;
    private const int CaptureTakeOwnership = 1;
    private const uint HlslShaderModel = 13u | 0x4000000u;
    private const uint ForceZeroInit = 54u | 0x1000000u;

    public static bool TryEmit(
        ReadOnlySpan<byte> spirv,
        string? entryPoint,
        uint executionModel,
        uint shaderModel,
        out string source,
        out string diagnostic)
    {
        source = string.Empty;
        diagnostic = string.Empty;
        if (spirv.Length < 4 || (spirv.Length & 3) != 0)
        {
            diagnostic = $"SPIR-V byte length {spirv.Length} is not a positive multiple of 4.";
            return false;
        }

        IntPtr context = IntPtr.Zero;
        try
        {
            if (spvc_context_create(out context) != 0 || context == IntPtr.Zero)
            {
                diagnostic = "spvc_context_create failed.";
                return false;
            }

            IntPtr parsed;
            fixed (byte* bytes = spirv)
            {
                if (spvc_context_parse_spirv(context, (uint*)bytes, (nuint)(spirv.Length / 4), out parsed) != 0)
                {
                    diagnostic = LastError(context);
                    return false;
                }
            }

            if (spvc_context_create_compiler(context, BackendHlsl, parsed, CaptureTakeOwnership, out var compiler) != 0)
            {
                diagnostic = LastError(context);
                return false;
            }
            if (spvc_compiler_create_compiler_options(compiler, out var options) != 0)
            {
                diagnostic = LastError(context);
                return false;
            }

            shaderModel = shaderModel == 0 ? 50u : shaderModel;
            spvc_compiler_options_set_uint(options, HlslShaderModel, shaderModel);
            spvc_compiler_options_set_bool(options, ForceZeroInit, 1);
            if (spvc_compiler_install_compiler_options(compiler, options) != 0)
            {
                diagnostic = LastError(context);
                return false;
            }

            if (!string.IsNullOrWhiteSpace(entryPoint))
                SetEntryPoint(compiler, entryPoint!, executionModel);

            if (spvc_compiler_compile(compiler, out var compiled) != 0 || compiled == IntPtr.Zero)
            {
                diagnostic = LastError(context);
                return false;
            }

            source = ShaderRecoveryContract.NormalizeText(Marshal.PtrToStringUTF8(compiled));
            return source.Length > 0;
        }
        catch (DllNotFoundException ex)
        {
            diagnostic = $"SPIRV-Cross native library unavailable: {ex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            diagnostic = ex.Message;
            return false;
        }
        finally
        {
            if (context != IntPtr.Zero)
                spvc_context_destroy(context);
        }
    }

    private static string LastError(IntPtr context)
    {
        var message = spvc_context_get_last_error_string(context);
        return message == IntPtr.Zero
            ? "SPIRV-Cross failed without a diagnostic."
            : Marshal.PtrToStringUTF8(message) ?? "SPIRV-Cross failed without a diagnostic.";
    }

    private static void SetEntryPoint(IntPtr compiler, string entryPoint, uint executionModel)
    {
        var bytes = Encoding.UTF8.GetBytes(entryPoint + "\0");
        fixed (byte* name = bytes)
            spvc_compiler_set_entry_point(compiler, (IntPtr)name, (int)executionModel);
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int spvc_context_create(out IntPtr context);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void spvc_context_destroy(IntPtr context);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr spvc_context_get_last_error_string(IntPtr context);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int spvc_context_parse_spirv(IntPtr context, uint* spirv, nuint wordCount, out IntPtr parsedIr);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int spvc_context_create_compiler(IntPtr context, int backend, IntPtr parsedIr, int captureMode, out IntPtr compiler);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int spvc_compiler_create_compiler_options(IntPtr compiler, out IntPtr options);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int spvc_compiler_options_set_uint(IntPtr options, uint option, uint value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int spvc_compiler_options_set_bool(IntPtr options, uint option, byte value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int spvc_compiler_install_compiler_options(IntPtr compiler, IntPtr options);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int spvc_compiler_set_entry_point(IntPtr compiler, IntPtr name, int executionModel);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int spvc_compiler_compile(IntPtr compiler, out IntPtr source);
}
