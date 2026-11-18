using System.Globalization;
using System.Runtime.InteropServices;
using TransubPlayer.Localization;
using TransubPlayer.Native;

namespace TransubPlayer.Services;

/// <summary>
/// macOS 进程内 libmpv 播放引擎（Windows 对应物：MpvPlayer，mpv.exe 子进程 + JSON IPC）。
/// 语义与 MpvPlayer 一一镜像：命令面相同、事件在内部事件线程触发（UI 侧负责 marshal）。
/// 注意：vo=libmpv 下不能用 force-window（mpv_initialize 返回 -4）。
/// </summary>
internal sealed class MpvLibPlayer : IMpvEngine
{
    private IntPtr _mpv;
    private readonly object _gate = new();
    private Task? _eventLoopTask;

    private double _timePos;
    private double _duration;
    private bool _paused = true;
    private int _volume = 50;
    private bool _muted;
    private double _speed = 1.0;
    private bool _subVisible = true;
    private bool _pausedForCache;
    private double _cacheBufferingPercent;
    private int _width;
    private int _height;
    private string? _currentSub;
    private string? _pendingLoadSub;
    private double _pendingSeekOnLoad = double.NaN;
    private IReadOnlyDictionary<string, string>? _httpHeaders;

    public IntPtr RenderHandle => _mpv;
    public bool IsRunning => _mpv != IntPtr.Zero;
    public bool IpcFailed => false;
    public double TimePos => _timePos;
    public double Duration => _duration;
    public bool Paused => _paused;
    public int Volume => _volume;
    public bool Muted => _muted;
    public double Speed => _speed;
    public bool SubVisible => _subVisible;
    public bool IsBuffering => _pausedForCache;
    public double BufferingPercent => _cacheBufferingPercent;
    public bool LiveLightIpc => false;
    public string? RecordingPath { get; private set; }
    public string? RecordingFinalPath { get; private set; }
    public DateTime? RecordingStartedUtc { get; private set; }
    public bool IsRecording => !string.IsNullOrWhiteSpace(RecordingPath);
    public TimeSpan RecordingElapsed
        => RecordingStartedUtc is { } start ? DateTime.UtcNow - start : TimeSpan.Zero;

    public event Action<double>? TimeChanged;
    public event Action<double>? DurationChanged;
    public event Action<bool>? PauseChanged;
    public event Action<int>? VolumeChanged;
    public event Action<bool>? MuteChanged;
    public event Action<double>? SpeedChanged;
    public event Action<bool>? EofReached;
    public event Action<int, int>? VideoSizeChanged;
    public event Action<bool, double>? BufferingChanged;
    public event Action<string>? Log;

    public void Start(string mpvPath, IntPtr hwnd, AppSettings? settings = null)
        => Initialize(settings ?? new AppSettings());

    public Task StartAsync(
        string mpvPath,
        IntPtr hwnd,
        AppSettings? settings = null,
        CancellationToken ct = default,
        string? initialMedia = null,
        IReadOnlyDictionary<string, string>? httpHeaders = null,
        bool autoPlay = true)
    {
        // 进程内引擎复用同一 handle：Stop() 只停播放不销毁引擎，所以这里按需初始化。
        Initialize(settings ?? new AppSettings());
        if (!string.IsNullOrWhiteSpace(initialMedia))
            LoadFile(initialMedia, autoPlay, httpHeaders);
        return Task.CompletedTask;
    }

    public void Initialize(AppSettings settings)
    {
        lock (_gate)
        {
            if (_mpv != IntPtr.Zero) return;
            MpvNative.LibPath = MpvLibLocator.FindOrThrow();
            Log?.Invoke("libmpv: " + MpvNative.LibPath);

            _mpv = MpvNative.mpv_create();
            if (_mpv == IntPtr.Zero) throw new InvalidOperationException("mpv_create failed");

            Opt("vo", "libmpv");
            Opt("hwdec", NormalizeHwDec(settings.HwDec));
            Opt("keep-open", "yes");
            Opt("idle", "yes");
            Opt("cache", "yes");
            Opt("demuxer-readahead-secs", "60");
            Opt("input-default-bindings", "no");
            Opt("input-vo-keyboard", "no");
            Opt("ytdl", "no");
            Opt("network-timeout", "15");
            Opt("volume-max", "130");

            _volume = Math.Clamp(settings.Volume, 0, 130);
            _muted = settings.Muted;
            if (settings.Speed > 0) _speed = settings.Speed;
            Opt("volume", _volume.ToString("0", CultureInfo.InvariantCulture));
            Opt("mute", _muted ? "yes" : "no");

            ApplyVideoFit(settings.VideoFit);

            var font = MacSubFont(settings.SubFont);
            Opt("sub-auto", "no");
            Opt("sub-codepage", "utf-8");
            Opt("sub-font", font);
            Opt("sub-font-size", Math.Clamp(settings.SubFontSize, 16, 96).ToString("0", CultureInfo.InvariantCulture));
            Opt("sub-bold", settings.SubBold ? "yes" : "no");
            Opt("sub-border-size", Math.Clamp(settings.SubBorderSize, 0, 8).ToString("0", CultureInfo.InvariantCulture));
            Opt("sub-margin-y", Math.Clamp(settings.SubMarginY, 0, 200).ToString("0", CultureInfo.InvariantCulture));
            Opt("sub-delay", settings.SubDelaySec.ToString("0.###", CultureInfo.InvariantCulture));
            Opt("sub-color", "1/1/1/1");
            Opt("osd-font", font);
            Opt("osd-font-size", "32");
            Opt("osd-level", "1");
            Opt("sub-visibility", "yes");

            Check(MpvNative.mpv_initialize(_mpv), "mpv_initialize");

            Observe(1, "time-pos", MpvFormat.Double);
            Observe(2, "duration", MpvFormat.Double);
            Observe(3, "pause", MpvFormat.Flag);
            Observe(4, "eof-reached", MpvFormat.Flag);
            Observe(5, "volume", MpvFormat.Double);
            Observe(6, "mute", MpvFormat.Flag);
            Observe(7, "speed", MpvFormat.Double);
            Observe(8, "paused-for-cache", MpvFormat.Flag);
            Observe(9, "demuxer-cache-time", MpvFormat.Double);
            Observe(10, "width", MpvFormat.Int64);
            Observe(11, "height", MpvFormat.Int64);
            Observe(12, "media-title", MpvFormat.String);
            Observe(13, "sub-visibility", MpvFormat.Flag);

            _eventLoopTask = Task.Run(EventLoop);
        }
    }

    internal static string MacSubFont(string? subFont)
        => string.IsNullOrWhiteSpace(subFont) || subFont == "Microsoft YaHei"
            ? "PingFang SC"
            : subFont.Trim();

    private static string NormalizeHwDec(string? raw)
    {
        var v = (raw ?? "auto").Trim().ToLowerInvariant();
        return v switch
        {
            "auto" => "videotoolbox-auto",
            "no" or "nohwdec" => "no",
            _ when v.Contains("videotoolbox") => v,
            "auto-safe" => "videotoolbox",
            _ => "videotoolbox-auto",
        };
    }

    private void ApplyVideoFit(string? fit)
    {
        switch ((fit ?? "window").Trim().ToLowerInvariant())
        {
            case "cover":
                SetProperty("keepaspect", true);
                SetProperty("panscan", 1.0);
                break;
            case "stretch":
                SetProperty("keepaspect", false);
                SetProperty("panscan", 0.0);
                break;
            default:
                SetProperty("keepaspect", true);
                SetProperty("panscan", 0.0);
                break;
        }
    }

    // ---- 加载 ----

    public bool LoadFile(
        string mediaPath,
        bool autoPlay = true,
        IReadOnlyDictionary<string, string>? httpHeaders = null,
        string? osdText = null,
        string? subtitlePath = null)
    {
        if (_mpv == IntPtr.Zero) return false;
        _currentSub = null;
        _width = 0;
        _height = 0;
        _pendingSeekOnLoad = double.NaN;
        SetHttpHeaders(httpHeaders);

        // 与 Windows 版同思路：随 loadfile 一并挂外部字幕，避开 loadfile/sub-add 竞态。
        // 进程内命令行不便传带逗号的选项串，改用 sub-files 属性（作用于下一次 load）。
        if (!string.IsNullOrWhiteSpace(subtitlePath) && File.Exists(subtitlePath))
            SetPropertyString("sub-files", subtitlePath);
        else
            SetPropertyString("sub-files", "");

        if (!TryCommand("loadfile", mediaPath, "replace"))
        {
            Log?.Invoke("mpv loadfile 失败 · " + mediaPath);
            return false;
        }
        _pendingLoadSub = subtitlePath;

        SetPause(!autoPlay);
        ShowOsd(osdText ?? MediaSourceHelper.DisplayName(mediaPath));
        return true;
    }

    public void SetHttpHeaders(IReadOnlyDictionary<string, string>? headers)
    {
        _httpHeaders = headers;
        if (headers is not { Count: > 0 })
        {
            SetPropertyString("http-header-fields", "");
            return;
        }
        // key/value 列表选项按 mpv 文本形式 "Field: value" 逗号合并（流媒体场景 Phase 3 再细化）。
        var joined = string.Join(",", headers.Select(kv => $"{kv.Key}: {kv.Value}"));
        SetPropertyString("http-header-fields", joined);
    }

    // ---- 播放控制 ----

    public void SetPause(bool pause) => SetProperty("pause", pause);

    public void TogglePause()
    {
        SetProperty("pause", !_paused);
    }

    public void ReplayFromStart()
    {
        SetProperty("time-pos", 0.0);
        SetProperty("pause", false);
    }

    public void Seek(double seconds) => SetProperty("time-pos", seconds);

    public void SeekRelative(double delta) => TryCommand("seek", delta.ToString("0.###", CultureInfo.InvariantCulture), "relative");

    public void SeekPercent(double percent) => TryCommand("seek", Math.Clamp(percent, 0, 100).ToString("0.###", CultureInfo.InvariantCulture), "absolute-percent");

    public void StopPlayback() => TryCommand("stop");

    public void FrameStep(bool forward = true) => TryCommand(forward ? "frame-step" : "frame-back-step");

    public void SetVolume(int volume)
    {
        _volume = Math.Clamp(volume, 0, 130);
        SetProperty("volume", (double)_volume);
        VolumeChanged?.Invoke(_volume);
    }

    public void AdjustVolume(int delta) => SetVolume(_volume + delta);

    public void SetMute(bool mute)
    {
        _muted = mute;
        SetProperty("mute", mute);
        MuteChanged?.Invoke(mute);
    }

    public void ToggleMute() => SetMute(!_muted);

    public void SetSpeed(double speed)
    {
        _speed = Math.Clamp(Math.Round(speed, 2), 0.25, 4.0);
        SetProperty("speed", _speed);
        SpeedChanged?.Invoke(_speed);
        ShowOsd($"{_speed:0.##}x");
    }

    public void CycleSpeed()
    {
        double[] steps = [0.5, 0.75, 1.0, 1.25, 1.5, 2.0];
        var i = Array.FindIndex(steps, s => Math.Abs(s - _speed) < 0.01);
        SetSpeed(steps[(i + 1) % steps.Length]);
    }

    public void ResetSpeed() => SetSpeed(1.0);

    // ---- 字幕 ----

    public void ClearSubtitle()
    {
        TryCommand("sub-remove");
        _currentSub = null;
    }

    public void SetSubtitle(string? path, bool reloadIfSame = true)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;
        if (string.Equals(_currentSub, path, StringComparison.OrdinalIgnoreCase))
        {
            if (reloadIfSame)
                ReloadSubtitle();
            return;
        }

        TryCommand("sub-remove");
        if (!TryCommand("sub-add", path, "select"))
        {
            Log?.Invoke("mpv sub-add 失败 · " + Path.GetFileName(path));
            return;
        }

        _currentSub = path;
    }

    public void ReloadSubtitle()
    {
        if (string.IsNullOrWhiteSpace(_currentSub)) return;
        var path = _currentSub;
        TryCommand("sub-remove");
        if (!TryCommand("sub-add", path, "select"))
        {
            if (!TryCommand("sub-reload"))
            {
                Log?.Invoke("mpv 字幕重载失败 · " + Path.GetFileName(path));
                _currentSub = null;
                return;
            }
        }

        _currentSub = path;
    }

    public void SetSubVisible(bool visible, bool showOsd = true)
    {
        _subVisible = visible;
        SetProperty("sub-visibility", visible);
        if (showOsd)
            ShowOsd(visible ? Loc.Get("Main.Osd.SubVisibleOn") : Loc.Get("Main.Osd.SubVisibleOff"));
    }

    public void ToggleSubVisible() => SetSubVisible(!_subVisible);

    public void SetSubDelay(double seconds)
        => SetProperty("sub-delay", Math.Clamp(seconds, -30, 30));

    public void SetSubPos(int pos)
        => SetProperty("sub-pos", (double)Math.Clamp(pos, 0, 100));

    public void ApplySubtitleSettings(AppSettings settings)
    {
        var font = MacSubFont(settings.SubFont);
        SetPropertyString("sub-font", font);
        SetProperty("sub-font-size", (double)Math.Clamp(settings.SubFontSize, 16, 96));
        SetProperty("sub-bold", settings.SubBold);
        SetProperty("sub-border-size", (double)Math.Clamp(settings.SubBorderSize, 0, 8));
        SetProperty("sub-margin-y", (double)Math.Clamp(settings.SubMarginY, 0, 200));
        SetSubPos(settings.SubPos);
        SetSubDelay(settings.SubDelaySec);
        SetPropertyString("osd-font", font);
        ReloadSubtitle();
    }

    public void ApplyPlaybackSettings(AppSettings settings)
    {
        SetPropertyString("hwdec", NormalizeHwDec(settings.HwDec));
        ApplyVideoFit(settings.VideoFit);
        SetVolume(Math.Clamp(settings.Volume, 0, 130));
        if (settings.Speed > 0)
            SetSpeed(settings.Speed);
    }

    // ---- 录制（Phase 3：走 mpv CLI stream-record）----

    public Task StartStreamRecordAsync(string outputPath, CancellationToken ct = default)
        => throw new InvalidOperationException(Loc.Get("Main.Status.MpvMissing").Length >= 0
            ? "录制功能将在 macOS 后续版本提供"
            : "录制功能将在 macOS 后续版本提供");

    public Task<StreamRecordStopResult> StopStreamRecordAsync(CancellationToken ct = default)
        => Task.FromResult(new StreamRecordStopResult(false, "", 0, "当前没有在录制。"));

    // ---- 截图 / OSD / 属性 ----

    public string? Screenshot(string? directory = null)
    {
        var dir = directory ?? AppPaths.ScreenshotsDir;
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"shot-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
        TryCommand("screenshot-to-file", file, "video");
        Log?.Invoke("截图 " + file);
        return file;
    }

    public void ShowOsd(string text, int durationMs = 1200)
        => TryCommand("show-text", text, durationMs.ToString(CultureInfo.InvariantCulture));

    public Task<double?> GetDoubleAsync(string property, int timeoutMs = 900)
    {
        IntPtr mpv;
        lock (_gate) mpv = _mpv;
        if (mpv == IntPtr.Zero) return Task.FromResult<double?>(null);
        var rc = MpvNative.mpv_get_property(mpv, property, MpvFormat.Double, out double value);
        return Task.FromResult<double?>(rc >= 0 ? value : null);
    }

    public Task<bool> EnsureResponsiveAsync() => Task.FromResult(IsRunning);

    public void RecoverIfUnresponsive()
    {
        // 进程内引擎不存在 IPC 挂死；handle 消亡等价于应用退出。
    }

    public void Stop()
    {
        // 保持引擎存活（mac 渲染控件持有同一 handle 的 render context），
        // 仅停止当前播放，等价于 Windows 语义中的"回到空闲"。
        _currentSub = null;
        TryCommand("stop");
    }

    public void Shutdown()
    {
        IntPtr mpv;
        lock (_gate)
        {
            mpv = _mpv;
            _mpv = IntPtr.Zero;
        }
        if (mpv == IntPtr.Zero) return;
        try
        {
            // 顺序至关重要：先唤醒事件线程 → 等它退出 → 才能销毁 handle，
            // 否则 mpv_wait_event 会在已释放的 handle 上 SIGSEGV。
            MpvNative.mpv_wakeup(mpv);
            try { _eventLoopTask?.Wait(3000); } catch { /* ignore */ }
            TryCommandOn(mpv, "quit");
            MpvNative.mpv_terminate_destroy(mpv);
        }
        catch
        {
            // ignore
        }
    }

    public void Dispose() => Shutdown();

    // ---- 渲染控件 ----

    /// <summary>渲染前查询是否有更新帧（MPV_RENDER_UPDATE_FRAME）。</summary>
    public bool ShouldRender(IntPtr renderCtx)
        => renderCtx != IntPtr.Zero
           && (MpvNative.mpv_render_context_update(renderCtx) & MpvRender.UpdateFrame) != 0;

    // ---- 内部 ----

    private async Task EventLoop()
    {
        while (true)
        {
            IntPtr mpv;
            lock (_gate) mpv = _mpv;
            if (mpv == IntPtr.Zero) return;

            IntPtr evPtr = MpvNative.mpv_wait_event(mpv, 0.5);
            lock (_gate) mpv = _mpv;
            if (mpv == IntPtr.Zero) return;

            var ev = Marshal.PtrToStructure<MpvEvent>(evPtr);
            switch (ev.EventId)
            {
                case MpvEventId.Shutdown:
                    return;
                case MpvEventId.PropertyChange:
                    HandleProperty(ev);
                    break;
                case MpvEventId.FileLoaded:
                    if (!string.IsNullOrWhiteSpace(_pendingLoadSub))
                        _currentSub = _pendingLoadSub;
                    break;
            }
        }
    }

    private void HandleProperty(MpvEvent ev)
    {
        var p = Marshal.PtrToStructure<MpvEventProperty>(ev.Data);
        string? name = Marshal.PtrToStringUTF8(p.Name);
        if (name is null) return;

        switch (name)
        {
            case "time-pos":
                if (p.Format == MpvFormat.Double && p.Data != IntPtr.Zero)
                {
                    _timePos = Marshal.PtrToStructure<double>(p.Data);
                    TimeChanged?.Invoke(_timePos);
                }
                break;

            case "duration":
                if (p.Format == MpvFormat.Double && p.Data != IntPtr.Zero)
                {
                    _duration = Marshal.PtrToStructure<double>(p.Data);
                    if (_duration > 0 && !double.IsNaN(_pendingSeekOnLoad))
                    {
                        var target = Math.Clamp(_pendingSeekOnLoad, 0, Math.Max(0, _duration - 0.25));
                        _pendingSeekOnLoad = double.NaN;
                        Seek(target);
                    }
                    DurationChanged?.Invoke(_duration);
                }
                break;

            case "pause":
                if (p.Format == MpvFormat.Flag && p.Data != IntPtr.Zero)
                {
                    _paused = Marshal.PtrToStructure<int>(p.Data) != 0;
                    PauseChanged?.Invoke(_paused);
                }
                break;

            case "volume":
                if (p.Format == MpvFormat.Double && p.Data != IntPtr.Zero)
                {
                    _volume = (int)Math.Round(Marshal.PtrToStructure<double>(p.Data));
                    VolumeChanged?.Invoke(_volume);
                }
                break;

            case "mute":
                if (p.Format == MpvFormat.Flag && p.Data != IntPtr.Zero)
                {
                    _muted = Marshal.PtrToStructure<int>(p.Data) != 0;
                    MuteChanged?.Invoke(_muted);
                }
                break;

            case "speed":
                if (p.Format == MpvFormat.Double && p.Data != IntPtr.Zero)
                {
                    _speed = Marshal.PtrToStructure<double>(p.Data);
                    SpeedChanged?.Invoke(_speed);
                }
                break;

            case "sub-visibility":
                if (p.Format == MpvFormat.Flag && p.Data != IntPtr.Zero)
                    _subVisible = Marshal.PtrToStructure<int>(p.Data) != 0;
                break;

            case "eof-reached":
                if (p.Format == MpvFormat.Flag && p.Data != IntPtr.Zero)
                {
                    var ended = Marshal.PtrToStructure<int>(p.Data) != 0;
                    EofReached?.Invoke(ended);
                }
                break;

            case "paused-for-cache":
                if (p.Format == MpvFormat.Flag && p.Data != IntPtr.Zero)
                {
                    _pausedForCache = Marshal.PtrToStructure<int>(p.Data) != 0;
                    BufferingChanged?.Invoke(_pausedForCache, _cacheBufferingPercent);
                }
                break;

            case "demuxer-cache-time":
                if (p.Format == MpvFormat.Double && p.Data != IntPtr.Zero)
                {
                    _cacheBufferingPercent = Marshal.PtrToStructure<double>(p.Data);
                    BufferingChanged?.Invoke(_pausedForCache, _cacheBufferingPercent);
                }
                break;

            case "width":
                _width = p.Format == MpvFormat.Int64 && p.Data != IntPtr.Zero
                    ? (int)Marshal.PtrToStructure<long>(p.Data) : 0;
                NotifyVideoSize();
                break;

            case "height":
                _height = p.Format == MpvFormat.Int64 && p.Data != IntPtr.Zero
                    ? (int)Marshal.PtrToStructure<long>(p.Data) : 0;
                NotifyVideoSize();
                break;
        }
    }

    private void NotifyVideoSize()
    {
        if (_width > 0 && _height > 0)
            VideoSizeChanged?.Invoke(_width, _height);
    }

    private void Observe(ulong id, string name, MpvFormat fmt)
        => MpvNative.mpv_observe_property(_mpv, id, name, fmt);

    private void Opt(string k, string v)
    {
        int rc = MpvNative.mpv_set_option_string(_mpv, k, v);
        if (rc < 0)
            throw new InvalidOperationException($"mpv option {k}={v} rejected: mpv error {rc}");
    }

    private void SetProperty(string name, double value)
    {
        IntPtr mpv;
        lock (_gate) mpv = _mpv;
        if (mpv == IntPtr.Zero) return;
        MpvNative.mpv_set_property(mpv, name, MpvFormat.Double, ref value);
    }

    private void SetProperty(string name, bool value)
    {
        IntPtr mpv;
        lock (_gate) mpv = _mpv;
        if (mpv == IntPtr.Zero) return;
        int v = value ? 1 : 0;
        MpvNative.mpv_set_property(mpv, name, MpvFormat.Flag, ref v);
    }

    private void SetPropertyString(string name, string value)
    {
        IntPtr mpv;
        lock (_gate) mpv = _mpv;
        if (mpv == IntPtr.Zero) return;
        MpvNative.mpv_set_property_string(mpv, name, value);
    }

    private bool TryCommand(params string[] argv)
    {
        IntPtr mpv;
        lock (_gate) mpv = _mpv;
        if (mpv == IntPtr.Zero) return false;
        return TryCommandOn(mpv, argv);
    }

    private static bool TryCommandOn(IntPtr mpv, params string[] argv)
    {
        var ptrs = argv.Select(Marshal.StringToCoTaskMemUTF8).ToList();
        ptrs.Add(IntPtr.Zero);
        try
        {
            return MpvNative.mpv_command(mpv, ptrs.ToArray()) >= 0;
        }
        finally
        {
            foreach (var p in ptrs) if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
        }
    }

    private static void Check(int rc, string what)
    {
        if (rc < 0) throw new InvalidOperationException($"{what} failed: mpv error {rc}");
    }
}
