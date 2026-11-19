using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TransubPlayer.Localization;
using TransubPlayer.Mac.Views;
using TransubPlayer.Services;

namespace TransubPlayer.Mac;

public partial class MainWindow : Window, IPreviewHost
{
    private readonly AppSettings _settings;
    private readonly PreviewController _preview;
    private readonly PlaybackQueue _queue = new();
    private readonly List<string> _queueItems = new();
    private CancellationTokenSource? _openCts;
    private int _openGen;

    private DispatcherTimer? _osdTimer;
    private DispatcherTimer? _uiTimer;
    private DispatcherTimer? _chromeHideTimer;
    private bool _seekDragging;
    private bool _subDragActive;
    private bool _subDragMoved;
    private double _subDragStartY;
    private int _subDragStartPos;
    private int _subDragLastPos;
    private bool _pickerOpen;
    private bool _ended;
    private bool _uiReady;

    public MainWindow()
    {
        Console.Error.WriteLine("BOOT: MainWindow ctor enter");
        PlayerLog.Write("启动: MainWindow ctor 进入");
        InitializeComponent();
        Console.Error.WriteLine("BOOT: InitializeComponent done");
        PlayerLog.Write("启动: InitializeComponent 完成");

        // 标题栏图标：AssemblyName(TransubPlayer) ≠ 命名空间(TransubPlayer.Mac)，XAML 里的
        // avares URI 运行时基 URI 会解析成不存在的程序集名导致启动崩溃，改为代码里显式加载
        TitleBarIcon.Source = new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(
            new Uri("avares://TransubPlayer/Assets/app.png")));

        // 客户区延伸 + 系统 chrome（mac 红绿灯叠加）
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.PreferSystemChrome;
        ExtendClientAreaTitleBarHeightHint = 40;

        _settings = AppSettings.Load();
        Loc.Apply(_settings.UiLanguage);

        // IPreviewHost：进程内渲染无 HWND；dispatcher 用 Avalonia UI 线程
        _preview = new PreviewController(_settings, this, PublishPipelineStatus, PlayerLog.Write);
        _preview.StateChanged += () => Dispatcher.UIThread.Post(RefreshPipelineUi);
        _preview.MediaEnded += () => Dispatcher.UIThread.Post(OnMediaEnded);
        _preview.VideoSizeChanged += (_, _) => { };
        _preview.PacksChanged += () => Dispatcher.UIThread.Post(RefreshPipelineUi);
        _preview.OfferPresetSetupAsync = report =>
            Dialogs.ModelSetupDialog.ShowAsync(this, _settings, report, _preview.AsrEngine);

        // 引擎句柄提前建好，渲染控件立即可挂载（Initialize 幂等）
        if (_preview.Engine is MpvLibPlayer lib)
        {
            try { lib.Initialize(_settings); } catch (Exception ex) { PublishPipelineStatus(ex.Message); }
            Video.Attach(lib);
        }

        ApplyWindowBounds();
        WireUiEvents();
        InitBarCombos();
        ApplyStartupSettings();
        _uiReady = true;

        if (Program.StartupMedia.Count > 0)
            Dispatcher.UIThread.Post(() => OpenPaths(Program.StartupMedia), 200);
        SingleInstanceMac.Forwarded += paths =>
            Dispatcher.UIThread.Post(() => OpenPaths(paths));

        Closing += OnWindowClosing;
    }

    // ---- IPreviewHost ----

    IntPtr IPreviewHost.Hwnd => IntPtr.Zero;
    void IPreviewHost.EnsureHandle() { }
    void IPreviewHost.HookEmbeddedChildren() { }
    bool IPreviewHost.DispatcherCheckAccess() => Dispatcher.UIThread.CheckAccess();
    Task IPreviewHost.DispatcherInvokeAsync(Action action) => Dispatcher.UIThread.InvokeAsync(action).GetTask();

    // ---- 初始化 ----

    private void ApplyWindowBounds()
    {
        if (_settings.RememberWindowBounds)
        {
            if (_settings.WindowWidth > 320) Width = _settings.WindowWidth;
            if (_settings.WindowHeight > 240) Height = _settings.WindowHeight;
            if (_settings.WindowLeft is not null) Position = Position.WithX((int)_settings.WindowLeft.Value);
            if (_settings.WindowTop is not null) Position = Position.WithY((int)_settings.WindowTop.Value);
        }
    }

    private sealed record ComboItem(string Id, string Label)
    {
        public override string ToString() => Label;
    }

    private void InitBarCombos()
    {
        void Fill(ComboBox box, IReadOnlyList<(string Id, string Label)> items, Func<string, bool> selected)
        {
            var sel = 0;
            for (var i = 0; i < items.Count; i++)
            {
                box.Items.Add(new ComboItem(items[i].Id, items[i].Label));
                if (selected(items[i].Id))
                    sel = i;
            }
            box.SelectedIndex = sel;
        }

        Fill(CmbSourceLang, new[]
        {
            (SourceLanguages.Auto, "自动"),
            (SourceLanguages.Zh, "中文"),
            (SourceLanguages.Ja, "日语"),
            (SourceLanguages.Ko, "한국어"),
            (SourceLanguages.En, "英语"),
        }, id => id == SourceLanguages.Normalize(_settings.SourceLanguage));

        Fill(CmbTranslateTarget, new[]
        {
            (TranslateTargets.Auto, "自动"),
            (TranslateTargets.Zh, "简体中文"),
            (TranslateTargets.ZhHant, "繁體中文"),
            (TranslateTargets.En, "English"),
        }, id => id == TranslateTargets.Normalize(_settings.TranslateTarget));
    }

    private void ApplyStartupSettings()
    {
        VolumeSlider.Value = _settings.Volume;
        BtnMute.Content = _settings.Muted ? "🔇" : "🔊";
        RefreshModeButtons();
    }

    private void WireUiEvents()
    {
        // 进度条跳转：指针原语 + handledEventsToo=true（Slider 内部会把指针事件标记 handled，
        // 普通 += 注册的处理器会被抑制——必须用 AddHandler 强制接收）
        SeekSlider.AddHandler(InputElement.PointerPressedEvent, OnSeekPressed,
            RoutingStrategies.Bubble, handledEventsToo: true);
        SeekSlider.AddHandler(InputElement.PointerReleasedEvent, OnSeekPointerDoneReleased,
            RoutingStrategies.Bubble, handledEventsToo: true);
        SeekSlider.AddHandler(InputElement.PointerCaptureLostEvent, OnSeekPointerDoneCapture,
            RoutingStrategies.Bubble, handledEventsToo: true);
        TitleBar.PointerPressed += OnTitleBarPointerPressed;
        VideoInputLayer.PointerPressed += OnVideoPointerPressed;
        VideoInputLayer.PointerMoved += OnVideoPointerMoved;
        VideoInputLayer.PointerWheelChanged += OnVideoPointerWheel;
        VideoInputLayer.PointerReleased += OnVideoPointerReleased;
        VideoInputLayer.PointerCaptureLost += OnVideoPointerCaptureLost;
        VideoInputLayer.DoubleTapped += OnVideoDoubleTapped;

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        KeyDown += OnKeyDown;

        _osdTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.6) };
        _osdTimer.Tick += (_, _) => { OsdLabel.IsVisible = false; _osdTimer.Stop(); };

        // 全屏自动隐藏控制条
        _chromeHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(0.8, _settings.FullscreenHideDelaySec)) };
        _chromeHideTimer.Tick += (_, _) =>
        {
            _chromeHideTimer.Stop();
            if (WindowState == WindowState.FullScreen && _settings.HideChromeInFullscreen)
                SetChromeVisible(false);
        };
        PointerMoved += (_, _) =>
        {
            if (WindowState == WindowState.FullScreen && _settings.HideChromeInFullscreen)
            {
                SetChromeVisible(true);
                _chromeHideTimer.Stop();
                _chromeHideTimer.Start();
            }
        };

        // UI 轮询（时间/进度/字幕计数）——PreviewController 状态以 StateChanged + 轮询双通道刷新
        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _uiTimer.Tick += (_, _) => RefreshPlaybackUi();
        _uiTimer.Start();

        _queue.Changed += RefreshPlaylist;
    }

    // ---- 管线状态 ----

    private void PublishPipelineStatus(string text)
    {
        Dispatcher.UIThread.Post(() => PipelineLabel.Text = text);
    }

    private void RefreshPipelineUi()
    {
        // 未打开媒体时不展示管线引擎细节（EngineDetail 的初始值是"引擎未连接"，会误导）
        var detail = string.IsNullOrEmpty(_preview.MediaPath)
            ? "就绪 · 打开视频后自动生成字幕"
            : _preview.EngineDetail;
        var counts = $"字幕 {_preview.CueCount} · 译文 {_preview.TranslatedCount}";
        var mode = _preview.DisplayMode switch
        {
            SubtitleDisplayMode.Zh => "译文",
            SubtitleDisplayMode.Source => "原文",
            SubtitleDisplayMode.Dual => "双语",
            _ => "",
        };
        PipelineLabel.Text = $"{detail} · {counts} · {mode}".Trim(' ', '·');
        RefreshModeButtons();

        if (_preview.WaitingForZh)
        {
            GuideCard.IsVisible = true;
            GuideTitle.Text = _preview.WaitZhOverlayTitle;
            GuideAmberLine.Text = Loc.Format("Main.WaitZh.ProgressFirst",
                MediaTimeFormat.Format(_preview.ZhFrontier));
            GuideDetail.Text = Loc.Get("Main.WaitZh.Hint");
            BtnSkipWait.IsVisible = true;
        }
        else if (_preview.ShowOpeningBootstrap)
        {
            GuideCard.IsVisible = true;
            GuideTitle.Text = _preview.OpeningBootstrapTitle;
            GuideAmberLine.Text = "";
            GuideDetail.Text = _preview.OpeningBootstrapDetail;
            BtnSkipWait.IsVisible = false;
        }
        else
        {
            GuideCard.IsVisible = false;
        }
    }

    private void RefreshPlaybackUi()
    {
        var pos = _preview.Position;
        var dur = _preview.Duration;
        TimeCur.Text = MediaTimeFormat.Format(pos) + (_ended ? " ⏹" : "");
        TimeDur.Text = MediaTimeFormat.Format(dur);
        if (dur > 1)
        {
            SeekSlider.Maximum = dur;
            if (!_seekDragging)
                SeekSlider.Value = Math.Clamp(pos, 0, dur);
        }
        BtnPlay.Content = _preview.Paused ? "▶" : "⏸";
        var title = _queue.Current is { } cur ? Path.GetFileName(cur) : null;
        if (!string.IsNullOrWhiteSpace(title))
        {
            TitleLabel.Text = title;
            Title = title;
        }
    }

    private void RefreshModeButtons()
    {
        void Mark(Button b, bool active)
        {
            if (active) b.Classes.Add("ModeActive"); else b.Classes.Remove("ModeActive");
        }

        Mark(BtnModeZh, _preview.DisplayMode == SubtitleDisplayMode.Zh);
        Mark(BtnModeSource, _preview.DisplayMode == SubtitleDisplayMode.Source);
        Mark(BtnModeDual, _preview.DisplayMode == SubtitleDisplayMode.Dual);
    }

    // ---- 打开 ----

    private async void OnOpenClick(object? sender, RoutedEventArgs e) => await OpenViaPickerAsync(false);

    private async void OnSubtitleClick(object? sender, RoutedEventArgs e) => await OpenViaPickerAsync(true);

    private async Task OpenViaPickerAsync(bool subtitle)
    {
        if (_pickerOpen) return;
        _pickerOpen = true;
        try
        {
            await OpenViaPickerCoreAsync(subtitle);
        }
        finally
        {
            _pickerOpen = false;
        }
    }

    private async Task OpenViaPickerCoreAsync(bool subtitle)
    {
        var options = new FilePickerOpenOptions { AllowMultiple = true };
        if (subtitle)
        {
            options.Title = Loc.Get("Main.Menu.LocalSidecars");
            options.FileTypeFilter = new[]
            {
                new FilePickerFileType(Loc.Get("Main.SubFile.Tip"))
                {
                    Patterns = new[] { "*.srt", "*.ass", "*.ssa", "*.vtt" },
                },
            };
        }
        else
        {
            options.Title = Loc.Get("Main.Menu.OpenFile");
            options.FileTypeFilter = new[]
            {
                new FilePickerFileType(Loc.Get("Common.AllFiles"))
                {
                    Patterns = MediaFileTypes.PlaybackExtensions.Select(e => "*" + e).ToList(),
                },
                new FilePickerFileType(Loc.Get("Common.AllFiles")) { Patterns = new[] { "*.*" } },
            };
        }

        var files = await StorageProvider.OpenFilePickerAsync(options);
        var paths = files.Select(f => f.Path.LocalPath).Where(p => p.Length > 0).ToList();
        if (paths.Count == 0) return;

        if (subtitle)
        {
            _preview.Engine.SetSubtitle(paths[0]);
            ShowOsd(Path.GetFileName(paths[0]));
        }
        else
        {
            OpenPaths(paths);
        }
    }

    private void OpenPaths(IReadOnlyList<string> paths)
    {
        var media = paths.Where(p => MediaFileTypes.IsKnown(p) && !IsSubtitleExt(p)).ToList();
        if (media.Count == 0) return;

        _queueItems.Clear();
        _queue.Replace(media);
        _queueItems.AddRange(media);
        RefreshPlaylist();

        if (_settings.AddSameFolderToPlaylist && media.Count == 1)
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(media[0]));
            if (folder is not null)
            {
                var siblings = Directory.EnumerateFiles(folder)
                    .Where(p => MediaFileTypes.IsKnown(p) && !IsSubtitleExt(p))
                    .OrderBy(p => p, StringComparer.Ordinal)
                    .ToList();
                if (siblings.Count > 1)
                {
                    _queue.Append(siblings);
                    _queueItems.AddRange(siblings.Where(s => !_queueItems.Contains(s)));
                    RefreshPlaylist();
                }
            }
        }

        PlayCurrent();
    }

    private static bool IsSubtitleExt(string path)
    {
        var e = Path.GetExtension(path).ToLowerInvariant();
        return e is ".srt" or ".ass" or ".ssa" or ".vtt" or ".sub";
    }

    private void PlayCurrent()
    {
        var path = _queue.Current;
        if (path is null) return;

        _openCts?.Cancel();
        _openCts = new CancellationTokenSource();
        _openGen++;
        _ended = false;
        EmptyHint.IsVisible = false;
        TitleLabel.Text = Path.GetFileName(path);
        ShowOsd(MediaSourceHelper.DisplayName(path));
        SetStatusOpening(path);

        var gen = _openGen;
        var ct = _openCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await _preview.OpenMediaAsync(path, ct).ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (gen != Volatile.Read(ref _openGen)) return;
                    RecentFiles.Add(_settings, path);
                    _settings.Save();
                    _preview.SetVolume(_settings.Volume);
                    if (_settings.Speed > 0) _preview.SetSpeed(_settings.Speed);
                    if (_queue.Count > 1)
                        _preview.ShowOsd($"{_queue.Index + 1}/{_queue.Count}  {MediaSourceHelper.DisplayName(path)}");
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                PlayerLog.Write("打开失败: " + ex.Message);
                PublishPipelineStatus("打开失败 · " + ex.Message);
            }
        }, ct);
    }

    private void SetStatusOpening(string path)
    {
        PipelineLabel.Text = StreamMediaResolver.MayNeedResolve(path)
            ? Loc.Get("Main.Status.ResolvingStream")
            : Loc.Get("Main.Status.Opening");
    }

    private void OnMediaEnded()
    {
        _ended = true;
        if (_settings.AutoPlayNext && _queue.HasNext && _queue.TryMoveNext())
            PlayCurrent();
    }

    // ---- 控件事件 ----

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
        => BeginMoveDrag(e);

    private void OnVideoPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (string.IsNullOrEmpty(_preview.MediaPath) || _pickerOpen)
            return;
        if (!e.GetCurrentPoint(VideoInputLayer).Properties.IsLeftButtonPressed)
            return;
        _subDragActive = true;
        _subDragMoved = false;
        _subDragStartY = e.GetCurrentPoint(VideoInputLayer).Position.Y;
        _subDragStartPos = _subDragLastPos = Math.Clamp(_settings.SubPos, 0, 100);
        e.Pointer.Capture(VideoInputLayer);
        VideoInputLayer.Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
        PlayerLog.Write($"字幕拖动: 按下 y={_subDragStartY:0} 起始pos={_subDragStartPos}");
    }

    private void OnVideoPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_subDragActive)
            return;
        var dy = e.GetCurrentPoint(VideoInputLayer).Position.Y - _subDragStartY;
        if (!_subDragMoved && Math.Abs(dy) < 6)
            return;
        if (!_subDragMoved)
        {
            _subDragMoved = true;
            PlayerLog.Write("字幕拖动: 开始移动");
        }
        var h = VideoInputLayer.Bounds.Height;
        if (h <= 0)
            return;
        // 向上拖 → 字幕上移（sub-pos 变小），整个画面高度对应 0-100
        var pos = Math.Clamp((int)Math.Round(_subDragStartPos + dy / h * 100), 0, 100);
        if (pos != _subDragLastPos)
        {
            _subDragLastPos = pos;
            _preview.Engine?.SetSubPos(pos);
        }
    }

    private void OnVideoPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (string.IsNullOrEmpty(_preview.MediaPath) || _pickerOpen)
            return;
        if (Math.Abs(e.Delta.Y) < 0.01)
            return;
        // 滚轮上滚 → 字幕上移（pos 减小）；拖动之外的备用微调通道
        var pos = Math.Clamp((int)Math.Round(_settings.SubPos - e.Delta.Y * 3), 0, 100);
        if (pos == _settings.SubPos)
            return;
        _settings.SubPos = pos;
        _settings.SaveSoon();
        _preview.Engine?.SetSubPos(pos);
        ShowOsd(Loc.Format("Main.Osd.SubPos", pos));
        PlayerLog.Write("字幕滚轮: pos=" + pos);
        e.Handled = true;
    }

    private void OnVideoPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        => FinishSubDrag();

    private void OnVideoPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left)
            return;
        // 拖动过字幕位置：本次松开不触发播放/暂停
        if (FinishSubDrag())
            return;
        // 未加载视频时点击画面中间 → 直接弹出视频选择
        if (string.IsNullOrEmpty(_preview.MediaPath))
        {
            if (!_pickerOpen)
                _ = OpenViaPickerAsync(false);
            return;
        }
        _preview.TogglePause();
    }

    /// <summary>结束字幕拖动：确实拖动过 → 持久化位置并 OSD 反馈，返回 true。</summary>
    private bool FinishSubDrag()
    {
        VideoInputLayer.Cursor = Cursor.Default;
        if (!_subDragActive)
            return false;
        _subDragActive = false;
        if (!_subDragMoved)
            return false;
        _subDragMoved = false;
        _settings.SubPos = _subDragLastPos;
        _settings.SaveSoon();
        ShowOsd(Loc.Format("Main.Osd.SubPos", _subDragLastPos));
        PlayerLog.Write("字幕拖动: 提交 pos=" + _subDragLastPos);
        return true;
    }

    private void OnVideoDoubleTapped(object? sender, TappedEventArgs e)
    {
        // 未加载视频时双击中间区块同样打开选片（不做全屏切换）
        if (string.IsNullOrEmpty(_preview.MediaPath))
        {
            if (!_pickerOpen)
                _ = OpenViaPickerAsync(false);
            return;
        }
        ToggleFullscreen();
    }

    private void OnSeekPressed(object? sender, PointerPressedEventArgs e) => _seekDragging = true;

    private void OnSeekPointerDoneReleased(object? sender, PointerReleasedEventArgs e) => CommitSeek();

    private void OnSeekPointerDoneCapture(object? sender, PointerCaptureLostEventArgs e) => CommitSeek();

    private void CommitSeek()
    {
        // PointerReleased 与 PointerCaptureLost 只应提交一次（先到者赢）
        if (!_seekDragging)
            return;
        _seekDragging = false;
        PlayerLog.Write($"UI seek -> {SeekSlider.Value:F2}s");
        _preview.Seek(SeekSlider.Value);
    }

    private void OnVolumeChanged(object? sender, RoutedEventArgs e)
    {
        var v = (int)Math.Round(VolumeSlider.Value);
        VolLabel.Text = v.ToString();
        _preview.SetVolume(v);
        _settings.Volume = v;
        _settings.SaveSoon();
    }

    private void OnMuteClick(object? sender, RoutedEventArgs e)
    {
        _preview.Engine.ToggleMute();
    }

    private void OnPlayPauseClick(object? sender, RoutedEventArgs e) => _preview.TogglePause();

    private void OnModeZhClick(object? sender, RoutedEventArgs e)
        => _preview.SetDisplayMode(SubtitleDisplayMode.Zh, announce: true);

    private void OnModeSourceClick(object? sender, RoutedEventArgs e)
        => _preview.SetDisplayMode(SubtitleDisplayMode.Source, announce: true);

    private void OnModeDualClick(object? sender, RoutedEventArgs e)
        => _preview.SetDisplayMode(SubtitleDisplayMode.Dual, announce: true);

    private void OnScreenshotClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var dir = _settings.ScreenshotDir;
            if (string.IsNullOrWhiteSpace(dir)) dir = AppPaths.ScreenshotsDir;
            Directory.CreateDirectory(dir);
            var name = $"{Path.GetFileNameWithoutExtension(_queue.Current ?? "video")}_{DateTime.Now:yyyyMMdd-HHmmss}.png";
            var shotPath = _preview.Engine.Screenshot(dir);
            ShowOsd(shotPath is null ? "截图失败" : Loc.Format("Main.Status.ScreenshotSaved", Path.GetFileName(shotPath)));
        }
        catch (Exception ex)
        {
            ShowOsd(ex.Message);
        }
    }

    private void OnPrevClick(object? sender, RoutedEventArgs e)
    {
        if (_queue.HasPrev && _queue.TryMovePrev()) PlayCurrent();
    }

    private void OnNextClick(object? sender, RoutedEventArgs e)
    {
        if (_queue.HasNext && _queue.TryMoveNext()) PlayCurrent();
    }

    private void OnStopClick(object? sender, RoutedEventArgs e)
    {
        _preview.StopPlaybackImmediate();
        _ended = false;
    }

    private void OnSpeedClick(object? sender, RoutedEventArgs e)
    {
        _preview.Engine.CycleSpeed();
    }

    private void OnSkipWaitClick(object? sender, RoutedEventArgs e)
    {
        _preview.SkipWaitForFirstZh();
    }

    private void OnSourceLangChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || CmbSourceLang.SelectedItem is not ComboItem item) return;
        _ = _preview.SetSourceLanguageAsync(item.Id, CancellationToken.None);
    }

    private void OnTargetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || CmbTranslateTarget.SelectedItem is not ComboItem item) return;
        _settings.TranslateTarget = item.Id;
        _settings.Save();
        _ = Task.Run(async () =>
        {
            try { await _preview.ApplyTranslateModelChangeAsync(); }
            catch (Exception ex) { PlayerLog.Write("目标语言切换失败: " + ex.Message); }
        });
    }

    private void OnFullscreenClick(object? sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
        if (WindowState == WindowState.FullScreen && _settings.HideChromeInFullscreen)
        {
            SetChromeVisible(true);
            _chromeHideTimer.Stop();
            _chromeHideTimer.Start();
        }
        else
        {
            SetChromeVisible(true);
            _chromeHideTimer.Stop();
        }
    }

    private void SetChromeVisible(bool visible)
    {
        TitleBar.IsVisible = visible;
        if (TitleBar.Parent is Grid g)
        {
            if (g.Children.OfType<Border>().Skip(1).FirstOrDefault() is { } controls)
                controls.IsVisible = visible;
        }
    }

    // ---- 播放列表 ----

    private void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        var dlg = new Dialogs.SettingsWindow(_settings);
        dlg.Applied = s =>
        {
            Loc.Apply(s.UiLanguage);
            _preview.Engine.ApplySubtitleSettings(s);
            _preview.Engine.ApplyPlaybackSettings(s);
            RefreshPipelineUi();
            _ = Task.Run(async () =>
            {
                try { await _preview.ApplyTranslateModelChangeAsync(); }
                catch (Exception ex) { PlayerLog.Write("翻译模型切换失败: " + ex.Message); }
            });
        };
        dlg.ModelManageRequested = ManageModelsAsync;
        dlg.Show(this);
    }

    private async Task ManageModelsAsync()
    {
        var gaps = new List<PresetGap>();
        if (!File.Exists(Path.Combine(AppPaths.AppDataDir, "models", "asr", AsrModelStore.TurboFileName)))
            gaps.Add(new PresetGap(PresetGapKind.AsrModel, ModelPicker.Turbo,
                "Whisper turbo 识别模型", "约 1.5 GB · 转写质量优先", "约 1.5 GB", true));
        if (!ManagedLlmInstaller.HasLlamaRuntime(_settings))
            gaps.Add(new PresetGap(PresetGapKind.LlamaRuntime, "llama-server",
                "llama-server 翻译运行时", "约 40 MB · Metal 加速", "约 40 MB", true));
        if (!ManagedLlmInstaller.HasSelectedGguf(_settings))
            gaps.Add(new PresetGap(PresetGapKind.GgufModel, _settings.TranslateModelId,
                "翻译模型 GGUF", "约 2.5 GB", "约 2.5 GB", true));

        if (gaps.Count == 0)
        {
            ShowOsd("所有组件已就绪");
            return;
        }

        var report = new PresetGapReport
        {
            PresetId = _settings.PresetId,
            PresetName = "组件管理",
            PreferredAsr = ModelPicker.Turbo,
            FallbackAsr = "",
            Gaps = gaps,
        };
        await Dialogs.ModelSetupDialog.ShowAsync(this, _settings, report, _preview.AsrEngine);
    }

    private void OnPlaylistClick(object? sender, RoutedEventArgs e)
        => PlaylistPanel.IsVisible = !PlaylistPanel.IsVisible;

    private void RefreshPlaylist()
    {
        PlaylistList.Items.Clear();
        for (int i = 0; i < _queueItems.Count; i++)
        {
            var mark = i == _queue.Index ? "▶ " : "";
            PlaylistList.Items.Add(mark + Path.GetFileName(_queueItems[i]));
        }
    }

    private void OnPlaylistItemActivate(object? sender, TappedEventArgs e)
    {
        int idx = PlaylistList.SelectedIndex;
        if (idx >= 0 && _queue.TryActivate(idx))
            PlayCurrent();
    }

    private void OnPlaylistClearClick(object? sender, RoutedEventArgs e)
    {
        _queue.Clear();
        _queueItems.Clear();
        RefreshPlaylist();
    }

    private void OnPlaylistRemoveClick(object? sender, RoutedEventArgs e)
    {
        int idx = PlaylistList.SelectedIndex;
        if (idx >= 0 && idx < _queueItems.Count)
        {
            _queue.RemoveAt(idx);
            _queueItems.RemoveAt(idx);
            RefreshPlaylist();
        }
    }

    // ---- 拖放 ----

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains(DataFormats.Files)) return;
        var paths = e.Data.GetFiles()?.Select(f => f.Path.LocalPath).Where(p => p.Length > 0).ToList();
        if (paths is { Count: > 0 })
            OpenPaths(paths.ToList());
    }

    // ---- 键盘 ----

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var step = _settings.SeekStepSeconds;

        switch (e.Key, e.KeyModifiers)
        {
            case (Key.Space, KeyModifiers.None):
                _preview.TogglePause();
                e.Handled = true;
                break;
            case (Key.Left, KeyModifiers.None): _preview.SeekRelative(-step); e.Handled = true; break;
            case (Key.Right, KeyModifiers.None): _preview.SeekRelative(step); e.Handled = true; break;
            case (Key.Up, KeyModifiers.None):
                _preview.SetVolume(Math.Min(130, _preview.Engine.Volume + 5), clearMute: true);
                VolumeSlider.Value = _preview.Engine.Volume;
                ShowOsd(Loc.Format("Main.Status.VolumeOsd", _preview.Engine.Volume));
                e.Handled = true;
                break;
            case (Key.Down, KeyModifiers.None):
                _preview.SetVolume(Math.Max(0, _preview.Engine.Volume - 5));
                VolumeSlider.Value = _preview.Engine.Volume;
                ShowOsd(Loc.Format("Main.Status.VolumeOsd", _preview.Engine.Volume));
                e.Handled = true;
                break;
            case (Key.M, KeyModifiers.None): OnMuteClick(null!, e); e.Handled = true; break;
            case (Key.F, KeyModifiers.None) or (Key.F11, KeyModifiers.None) or (Key.Enter, KeyModifiers.None):
                ToggleFullscreen();
                e.Handled = true;
                break;
            case (Key.O, KeyModifiers.Meta): _ = OpenViaPickerAsync(false); e.Handled = true; break;
            case (Key.N, KeyModifiers.None):
                if (_queue.HasNext && _queue.TryMoveNext()) PlayCurrent();
                e.Handled = true;
                break;
            case (Key.P, KeyModifiers.None):
                if (_queue.HasPrev && _queue.TryMovePrev()) PlayCurrent();
                e.Handled = true;
                break;
            case (Key.L, KeyModifiers.None): PlaylistPanel.IsVisible = !PlaylistPanel.IsVisible; e.Handled = true; break;
            case (Key.S, KeyModifiers.None): OnScreenshotClick(null!, e); e.Handled = true; break;
            case (Key.V, KeyModifiers.None): _preview.Engine.ToggleSubVisible(); e.Handled = true; break;
            case (Key.D1, KeyModifiers.None): OnModeZhClick(null!, e); e.Handled = true; break;
            case (Key.D2, KeyModifiers.None): OnModeSourceClick(null!, e); e.Handled = true; break;
            case (Key.D3, KeyModifiers.None): OnModeDualClick(null!, e); e.Handled = true; break;
            case (Key.Escape, KeyModifiers.None) when WindowState == WindowState.FullScreen:
                ToggleFullscreen();
                e.Handled = true;
                break;
        }
    }

    // ---- OSD / 关闭 ----

    private void ShowOsd(string text)
    {
        OsdLabel.Text = text;
        OsdLabel.IsVisible = true;
        _osdTimer.Stop();
        _osdTimer.Start();
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        try
        {
            if (_settings.RememberWindowBounds)
            {
                _settings.WindowWidth = Width;
                _settings.WindowHeight = Height;
                _settings.WindowLeft = Position.X;
                _settings.WindowTop = Position.Y;
            }
            _settings.Volume = _preview.Engine.Volume;
            _settings.Muted = _preview.Engine.Muted;
            _settings.Save();
        }
        catch
        {
            // ignore
        }

        _openCts?.Cancel();
        Video.DetachRender();
        _ = _preview.ShutdownAsync();
    }
}
