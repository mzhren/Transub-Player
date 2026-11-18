using System.Runtime.InteropServices;

namespace TransubPlayer.Native;

/// <summary>
/// Minimal P/Invoke surface of libmpv (client API + render API).
/// libmpv.dylib is loaded explicitly via the DllImport resolver so the same
/// code works with any located dylib path (brew / bundled).
/// </summary>
public static class MpvNative
{
    private const string Lib = "libmpv";

    /// <summary>Override before first P/Invoke call to point at a different dylib.</summary>
    public static string LibPath { get; set; } = "/opt/homebrew/lib/libmpv.dylib";

    static MpvNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(MpvNative).Assembly, (name, assembly, searchPath) =>
        {
            if (name != Lib)
                return IntPtr.Zero;
            return NativeLibrary.TryLoad(LibPath, out IntPtr handle) ? handle : IntPtr.Zero;
        });
    }

    // ---- client API (client.h) ----

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong mpv_client_api_version();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_create();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_initialize(IntPtr ctx);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_terminate_destroy(IntPtr ctx);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_option_string(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_property_string(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_property(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        MpvFormat format, ref double data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_property(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        MpvFormat format, ref int data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_get_property(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        MpvFormat format, out double data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_get_property(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        MpvFormat format, out long data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_get_property_string(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    /// <summary>argv-style UTF8 string array, null-terminated (last entry IntPtr.Zero).</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_command(IntPtr ctx, IntPtr[] args);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_observe_property(IntPtr ctx, ulong replyId, [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        MpvFormat format);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_free(IntPtr ptr);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_wakeup(IntPtr ctx);

    // ---- render API (render.h) ----

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_render_context_create(out IntPtr res, IntPtr mpv, MpvRenderParam[] args);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_render_context_render(IntPtr ctx, MpvRenderParam[] args);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_render_context_set_update_callback(IntPtr ctx,
        MpvRenderUpdateCallback callback, IntPtr callbackCtx);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong mpv_render_context_update(IntPtr ctx);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_render_context_report_swap(IntPtr ctx);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_render_context_free(IntPtr ctx);

    /// <summary>Delegate kept alive by caller for the lifetime of the render context.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void MpvRenderUpdateCallback(IntPtr callbackCtx);

    /// <summary>GL symbol lookup passed via MPV_RENDER_PARAM_OPENGL_INIT_PARAMS.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr MpvOpenGLGetProcAddress(IntPtr ctx, IntPtr name);
}

public enum MpvFormat
{
    None = 0,
    String = 1,
    OsdString = 2,
    Flag = 3,
    Int64 = 4,
    Double = 5,
}

public enum MpvEventId
{
    None = 0,
    Shutdown = 1,
    LogMessage = 2,
    GetPropertyReply = 3,
    SetPropertyReply = 4,
    CommandReply = 5,
    StartFile = 6,
    EndFile = 7,
    FileLoaded = 8,
    Idle = 11,
    VideoReconfig = 15,
    AudioReconfig = 16,
    Seek = 20,
    PlaybackRestart = 21,
    PropertyChange = 22,
    QueueOverflow = 24,
    Hook = 25,
}

public enum MpvRenderParamType
{
    Invalid = 0,
    ApiType = 1,
    OpenglInitParams = 2,
    OpenglFbo = 3,
    FlipY = 4,
    Depth = 5,
    IccProfile = 6,
    MaxFps = 7,
    AdvancedControl = 8,
    NextFrameInfo = 9,
    BlockTargetTime = 10,
    SwSize = 11,
    SwFormat = 12,
    SwStride = 13,
    SwPointer = 14,
}

/// <summary>mpv_event: { event_id, error, reply_userdata, data }.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct MpvEvent
{
    public MpvEventId EventId;
    public int Error;
    public ulong ReplyUserdata;
    public IntPtr Data;
}

/// <summary>mpv_event_property: { name, format, data }.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct MpvEventProperty
{
    public IntPtr Name;
    public MpvFormat Format;
    public IntPtr Data;
}

[StructLayout(LayoutKind.Sequential)]
public struct MpvRenderParam
{
    public MpvRenderParamType Type;
    public IntPtr Data;

    public static MpvRenderParam Terminal => default;
}

/// <summary>mpv_opengl_init_params: { get_proc_address, get_proc_address_ctx }.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct MpvOpenGLInitParams
{
    public MpvNative.MpvOpenGLGetProcAddress GetProcAddress;
    public IntPtr GetProcAddressCtx;
}

/// <summary>mpv_opengl_fbo: { fbo, w, h, internal_format }.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct MpvOpenGLFbo
{
    public int Fbo;
    public int Width;
    public int Height;
    public int InternalFormat;
}

public static class MpvRender
{
    public const string ApiTypeOpenGL = "opengl";
    public const ulong UpdateFrame = 1 << 0;

    /// <summary>Helper to build a param whose data is a marshalled struct; frees after use.</summary>
    public static MpvRenderParam FromStruct<T>(MpvRenderParamType type, ref T value) where T : struct
    {
        IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
        Marshal.StructureToPtr(value, ptr, fDeleteOld: false);
        return new MpvRenderParam { Type = type, Data = ptr };
    }

    public static void FreeAll(params MpvRenderParam[] args)
    {
        foreach (var p in args)
        {
            if (p.Data != IntPtr.Zero)
                Marshal.FreeHGlobal(p.Data);
        }
    }
}
