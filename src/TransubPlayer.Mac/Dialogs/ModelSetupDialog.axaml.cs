using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Interactivity;
using TransubPlayer.Localization;
using TransubPlayer.Services;

namespace TransubPlayer.Mac.Dialogs;

/// <summary>
/// 缺失组件对话框（预置包引导）。镜像 WPF PresetSetupDialog 的职责：
/// 展示缺口 → 自动安装（带进度）→ 返回选择，控制器随后复检。
/// </summary>
internal sealed partial class ModelSetupDialog : Window
{
    private readonly AppSettings _settings;
    private readonly PresetGapReport _report;
    private readonly AsrPipeline _asr;
    private readonly CancellationTokenSource _installCts = new();
    private PresetSetupChoice _choice = PresetSetupChoice.Cancel;

    public ModelSetupDialog(AppSettings settings, PresetGapReport report, AsrPipeline asr)
    {
        InitializeComponent();
        _settings = settings;
        _report = report;
        _asr = asr;

        Title = Loc.Get("Main.Deps.DialogTitle");
        Headline.Text = Loc.Get("Main.Deps.DialogHeading");

        foreach (var gap in report.Gaps)
        {
            var size = string.IsNullOrWhiteSpace(gap.SizeHint) ? "" : $"（{gap.SizeHint}）";
            GapList.Items.Add($"{gap.Title}{size}" + (gap.CanAutoInstall ? "" : "  · 需手动安装"));
        }

        BtnInstall.IsEnabled = report.CanAutoInstallAny;
        UpdateProgress(0, Loc.Get("Main.Deps.DialogAutoHint"));
    }

    private void UpdateProgress(int percent, string text)
    {
        Progress.IsIndeterminate = percent < 0;
        if (percent >= 0)
            Progress.Value = percent;
        ProgressText.Text = text;
    }

    private async void OnInstallClick(object? sender, RoutedEventArgs e)
    {
        if (BtnInstall.IsEnabled == false) return;
        BtnInstall.IsEnabled = false;
        BtnCancel.Content = Loc.Get("Main.Deps.CancelInstall");

        try
        {
            var installer = new PresetDependencyInstaller(
                _settings,
                s => Dispatcher.UIThread.Post(() => UpdateProgress(-1, s)),
                _ => { });

            var gaps = _report.Gaps.Where(g => g.CanAutoInstall).ToList();
            var total = Math.Max(1, gaps.Count);
            var done = 0;
            UpdateProgress(-1, Loc.Get("Main.Deps.InstallStarting"));

            // 安装器按 report 循环安装；这里为展示进度逐个 gap 调用
            foreach (var gap in gaps)
            {
                UpdateProgress(-1, $"{gap.Title} · 下载中…");
                await InstallOneAsync(gap);
                done++;
                UpdateProgress((int)(done * 100.0 / total), $"{gap.Title} · 完成");
            }

            UpdateProgress(100, Loc.Get("Main.Status.DepsMtReady"));
            _choice = PresetSetupChoice.AutoInstall;
            await Task.Delay(400);
            Close();
        }
        catch (OperationCanceledException)
        {
            _choice = PresetSetupChoice.Cancel;
            Close();
        }
        catch (Exception ex)
        {
            UpdateProgress(0, "安装失败 · " + ex.Message);
            BtnInstall.IsEnabled = _report.CanAutoInstallAny;
        }
    }

    private Task InstallOneAsync(PresetGap gap) => gap.Kind switch
    {
        PresetGapKind.AsrModel => _asr.DownloadModelsAsync([gap.Id], _installCts.Token),
        PresetGapKind.LlamaRuntime => ManagedLlmInstaller.EnsureLlamaRuntimeAsync(
            _settings, s => Dispatcher.UIThread.Post(() => UpdateProgress(-1, s)),
            _ => { }, _installCts.Token),
        PresetGapKind.GgufModel => ManagedLlmInstaller.EnsureGgufAsync(
            _settings.HfEndpoint, s => Dispatcher.UIThread.Post(() => UpdateProgress(-1, s)),
            _ => { }, _installCts.Token, modelId: gap.Id, settings: _settings),
        _ => Task.CompletedTask,
    };

    private void OnManualClick(object? sender, RoutedEventArgs e)
    {
        _choice = PresetSetupChoice.ManualInstall;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _installCts.Cancel();
        _choice = PresetSetupChoice.Cancel;
        Close();
    }

    /// <summary>展示对话框并返回用户选择（安装完成后控制器复检缺口）。</summary>
    public static async Task<PresetSetupChoice> ShowAsync(
        Window owner, AppSettings settings, PresetGapReport report, AsrPipeline asr)
    {
        var dialog = new ModelSetupDialog(settings, report, asr);
        await dialog.ShowDialog(owner);
        return dialog._choice;
    }
}
