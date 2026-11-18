using System.IO;

namespace TransubPlayer.Services;

/// <summary>
/// 播放引擎抽象。Windows 实现为 <see cref="MpvPlayer"/>（mpv.exe 子进程 + JSON IPC），
/// macOS 实现为 TransubPlayer.Core 的 MpvLibPlayer（进程内 libmpv render API）。
/// 成员与 MpvPlayer 公开面一一对应；PreviewController 仅依赖此抽象。
/// </summary>
internal interface IMpvEngine : IDisposable
{
    event Action<double>? TimeChanged;
    event Action<double>? DurationChanged;
    event Action<bool>? PauseChanged;
    event Action<int>? VolumeChanged;
    event Action<bool>? MuteChanged;
    event Action<double>? SpeedChanged;
    event Action<bool>? EofReached;
    event Action<int, int>? VideoSizeChanged;
    event Action<bool, double>? BufferingChanged;
    event Action<string>? Log;

    bool IsRunning { get; }
    bool IpcFailed { get; }
    int Volume { get; }
    bool Muted { get; }
    double Speed { get; }
    bool SubVisible { get; }
    bool IsBuffering { get; }
    double BufferingPercent { get; }
    bool LiveLightIpc { get; }
    string? RecordingPath { get; }
    string? RecordingFinalPath { get; }
    DateTime? RecordingStartedUtc { get; }
    bool IsRecording { get; }
    TimeSpan RecordingElapsed { get; }

    void Start(string mpvPath, IntPtr hwnd, AppSettings? settings = null);

    Task StartAsync(
        string mpvPath,
        IntPtr hwnd,
        AppSettings? settings = null,
        CancellationToken ct = default,
        string? initialMedia = null,
        IReadOnlyDictionary<string, string>? httpHeaders = null,
        bool autoPlay = true);

    bool LoadFile(
        string mediaPath,
        bool autoPlay = true,
        IReadOnlyDictionary<string, string>? httpHeaders = null,
        string? osdText = null,
        string? subtitlePath = null);

    void SetHttpHeaders(IReadOnlyDictionary<string, string>? headers);

    void SetPause(bool pause);
    void TogglePause();
    void ReplayFromStart();
    void Seek(double seconds);
    void SeekRelative(double delta);
    void SeekPercent(double percent);
    void StopPlayback();
    void FrameStep(bool forward = true);
    void SetVolume(int volume);
    void AdjustVolume(int delta);
    void SetMute(bool mute);
    void ToggleMute();
    void SetSpeed(double speed);
    void CycleSpeed();
    void ResetSpeed();
    void ClearSubtitle();
    void SetSubtitle(string? path, bool reloadIfSame = true);
    void ReloadSubtitle();
    void SetSubVisible(bool visible, bool showOsd = true);
    void ToggleSubVisible();
    /// <summary>垂直移动字幕（mpv sub-pos：0=顶，100=底）；拖动画面字幕时实时调用。</summary>
    void SetSubPos(int pos);

    Task StartStreamRecordAsync(string outputPath, CancellationToken ct = default);
    Task<StreamRecordStopResult> StopStreamRecordAsync(CancellationToken ct = default);

    string? Screenshot(string? directory = null);
    void SetSubDelay(double seconds);
    void ApplySubtitleSettings(AppSettings settings);
    void ApplyPlaybackSettings(AppSettings settings);
    void ShowOsd(string text, int durationMs = 1200);
    Task<double?> GetDoubleAsync(string property, int timeoutMs = 900);
    Task<bool> EnsureResponsiveAsync();
    void RecoverIfUnresponsive();
    void Stop();
}
