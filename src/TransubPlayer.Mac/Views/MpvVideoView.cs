using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using TransubPlayer.Native;
using TransubPlayer.Services;

namespace TransubPlayer.Mac.Views;

/// <summary>
/// macOS 视频渲染控件：libmpv render API → Avalonia OpenGlControlBase。
/// 替代 Windows 版 Controls/MpvHost.cs（Win32 HwndHost + --wid 嵌入）。
/// 引擎（MpvLibPlayer）拥有 mpv_handle，本控件只拥有 mpv_render_context。
/// </summary>
internal sealed class MpvVideoView : OpenGlControlBase
{
    private MpvLibPlayer? _engine;
    private IntPtr _rctx;
    private bool _firstRenderLogged;

    private MpvNative.MpvOpenGLGetProcAddress? _procKeepAlive;
    private MpvNative.MpvRenderUpdateCallback? _updateKeepAlive;

    public void Attach(MpvLibPlayer engine)
    {
        _engine = engine;
        // 若 GL 已初始化（窗口重建等），立即建渲染上下文
        if (_glReady && _rctx == IntPtr.Zero)
            CreateRenderContext();
    }

    /// <summary>必须在 engine.Shutdown() 之前调用（释放顺序：render ctx → mpv handle）。</summary>
    public void DetachRender()
    {
        if (_rctx != IntPtr.Zero)
        {
            MpvNative.mpv_render_context_set_update_callback(_rctx, null!, IntPtr.Zero);
            MpvNative.mpv_render_context_free(_rctx);
            _rctx = IntPtr.Zero;
        }
        _engine = null;
    }

    private bool _glReady;

    protected override void OnOpenGlInit(GlInterface gl)
    {
        _glReady = true;
        CreateRenderContext(gl);
        RequestNextFrameRendering();
    }

    private void CreateRenderContext(GlInterface? gl = null)
    {
        var engine = _engine;
        if (engine is null || engine.RenderHandle == IntPtr.Zero) return;
        try
        {
            _procKeepAlive = (_, name) =>
            {
                string s = Marshal.PtrToStringUTF8(name) ?? "";
                return gl?.GetProcAddress(s) ?? IntPtr.Zero;
            };
            var init = new MpvOpenGLInitParams { GetProcAddress = _procKeepAlive, GetProcAddressCtx = IntPtr.Zero };
            int advanced = 1;
            IntPtr pApi = Marshal.StringToCoTaskMemUTF8(MpvRender.ApiTypeOpenGL);
            var pInit = MpvRender.FromStruct(MpvRenderParamType.OpenglInitParams, ref init);
            var pAdv = MpvRender.FromStruct(MpvRenderParamType.AdvancedControl, ref advanced);
            var args = new[]
            {
                new MpvRenderParam { Type = MpvRenderParamType.ApiType, Data = pApi },
                pInit, pAdv, MpvRenderParam.Terminal,
            };
            int rc = MpvNative.mpv_render_context_create(out _rctx, engine.RenderHandle, args);
            Marshal.FreeCoTaskMem(pApi);
            MpvRender.FreeAll(pInit, pAdv);
            if (rc < 0)
            {
                _rctx = IntPtr.Zero;
                throw new InvalidOperationException($"mpv_render_context_create failed: {rc}");
            }

            _updateKeepAlive = _ => Dispatcher.UIThread.Post(RequestNextFrameRendering);
            MpvNative.mpv_render_context_set_update_callback(_rctx, _updateKeepAlive, IntPtr.Zero);
            RequestNextFrameRendering();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("MpvVideoView init fail: " + ex);
        }
    }

    protected override void OnOpenGlRender(GlInterface gl, int framebuffer)
    {
        if (_rctx == IntPtr.Zero || _engine is null)
        {
            RequestNextFrameRendering();
            return;
        }
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int w = Math.Max(1, (int)(Bounds.Width * scale));
        int h = Math.Max(1, (int)(Bounds.Height * scale));
        var fbo = new MpvOpenGLFbo { Fbo = framebuffer, Width = w, Height = h, InternalFormat = 0 };
        int flip = 1;
        var pFbo = MpvRender.FromStruct(MpvRenderParamType.OpenglFbo, ref fbo);
        var pFlip = MpvRender.FromStruct(MpvRenderParamType.FlipY, ref flip);
        var args = new[] { pFbo, pFlip, MpvRenderParam.Terminal };
        int rc = MpvNative.mpv_render_context_render(_rctx, args);
        MpvRender.FreeAll(pFbo, pFlip);

        if (rc < 0)
        {
            // 渲染失败不宜刷屏，仅首帧记录
            if (!_firstRenderLogged)
                Console.Error.WriteLine("render rc=" + rc);
        }
        if (!_firstRenderLogged)
        {
            _firstRenderLogged = true;
            Console.WriteLine($"FIRST_RENDER {w}x{h} fbo={framebuffer} scale={scale:F2}");
        }

        MpvNative.mpv_render_context_report_swap(_rctx);
        RequestNextFrameRendering();
    }

    protected override void OnOpenGlDeinit(GlInterface gl) => DetachRender();
}
