using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TransubPlayer.Localization;
using TransubPlayer.Services;

namespace TransubPlayer.Mac.Dialogs;

/// <summary>
/// macOS 紧凑设置窗口（WPF SettingsWindow 的核心子集）。
/// 保存后由 MainWindow 应用到引擎（字幕外观/硬解即时生效；模型切换走 ApplyTranslateModelChangeAsync）。
/// </summary>
internal sealed partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Func<Task> _applyAsync;

    private static readonly (string Id, string Label)[] UiLangs =
    {
        ("auto", "跟随系统"),
        ("zh-Hans", "简体中文"),
        ("en", "English"),
        ("ja", "日本語"),
        ("ko", "한국어"),
    };

    private static readonly (string Id, string Label)[] AsrQualitiesList =
    {
        (AsrQualities.Fast, "极速（fast）"),
        (AsrQualities.Better, "更准（better）"),
    };

    private static readonly (string Id, string Label)[] TranslateModelsList =
    {
        (TranslateModels.TranslateGemma4B, "TranslateGemma 4B（多语向）"),
        (TranslateModels.Murasaki4B, "Murasaki 4B（日→中）"),
    };

    private static readonly (string Id, string Label)[] TranslateTargetsList =
    {
        (TranslateTargets.Auto, "自动（源为中文→英，其他→中）"),
        (TranslateTargets.Zh, "中文"),
        (TranslateTargets.En, "English"),
    };

    private static readonly (string Id, string Label)[] SubtitleModesList =
    {
        ("zh", "译文"),
        ("source", "原文"),
        ("dual", "双语"),
    };

    private static readonly (string Id, string Label)[] HwDecList =
    {
        ("auto", "自动（VideoToolbox）"),
        ("videotoolbox", "VideoToolbox"),
        ("no", "关闭"),
    };

    private static readonly (string Id, string Label)[] VideoFitList =
    {
        ("window", "适应窗口"),
        ("cover", "填满"),
        ("stretch", "拉伸"),
    };

    public Action<AppSettings>? Applied { get; set; }
    public Func<Task>? ModelManageRequested { get; set; }

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        void Fill(ComboBox box, IReadOnlyList<(string Id, string Label)> items, Func<string, bool> selected)
        {
            foreach (var (id, label) in items)
                box.Items.Add(new ComboItem(id, label));
            box.SelectedIndex = items.Index(i => selected(i.Id));
        }

        Fill(CmbUiLang, UiLangs, id => id == UiLanguages.Resolve(settings.UiLanguage));
        Fill(CmbAsrQuality, AsrQualitiesList, id => id == settings.AsrQuality);
        Fill(CmbTranslateModel, TranslateModelsList, id => id == settings.TranslateModelId);
        Fill(CmbTranslateTarget, TranslateTargetsList, id => id == settings.TranslateTarget);
        Fill(CmbSubtitleMode, SubtitleModesList, id => id == settings.SubtitleMode);
        Fill(CmbHwDec, HwDecList, id => id == (string.IsNullOrWhiteSpace(settings.HwDec) ? "auto" : settings.HwDec));
        Fill(CmbVideoFit, VideoFitList, id => id == (string.IsNullOrWhiteSpace(settings.VideoFit) ? "window" : settings.VideoFit));

        TxtSubFont.Text = settings.SubFont;
        NumSubFontSize.Value = Math.Clamp(settings.SubFontSize, 16, 96);
        NumSubBorder.Value = Math.Clamp(settings.SubBorderSize, 0, 8);
        NumSubMargin.Value = Math.Clamp(settings.SubMarginY, 0, 200);
        NumSubDelay.Value = (decimal)Math.Clamp(settings.SubDelaySec, -30, 30);
        NumSeekStep.Value = Math.Clamp(settings.SeekStepSeconds, 1, 60);
        ChkAutoPlayNext.IsChecked = settings.AutoPlayNext;
        ChkSameFolder.IsChecked = settings.AddSameFolderToPlaylist;
        ChkRememberPos.IsChecked = settings.RememberPlaybackPosition;
        TxtHfEndpoint.Text = settings.HfEndpoint;

        BtnUpdate.IsEnabled = false;
        _ = CheckUpdateAsync();
        RefreshModelStatus();
        _filterWordsCount = PreviewTextSanitize.ApplyFilterWordsFile(settings.FilterWordsPath);
        RefreshFilterStatus();
    }

    private int _filterWordsCount;

    private void RefreshFilterStatus()
    {
        if (string.IsNullOrWhiteSpace(_settings.FilterWordsPath))
            FilterStatus.Text = "未导入（内置 Whisper 幻觉黑名单始终生效）";
        else if (_filterWordsCount < 0)
            FilterStatus.Text = $"文件不存在或无法读取：{_settings.FilterWordsPath}";
        else
            FilterStatus.Text = $"{Path.GetFileName(_settings.FilterWordsPath)} · 自定义短语 {_filterWordsCount} 条\n每行一个短语，# 开头为注释；匹配时忽略空白与标点";
    }

    private async void OnImportFilterClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择过滤词文本文件",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("文本文件") { Patterns = ["*.txt"], MimeTypes = ["text/plain"] }],
        });
        if (files.Count == 0)
            return;
        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
        {
            FilterStatus.Text = "无法读取所选文件";
            return;
        }

        _settings.FilterWordsPath = path;
        _filterWordsCount = PreviewTextSanitize.ApplyFilterWordsFile(path);
        _settings.Save();
        RefreshFilterStatus();
    }

    private void OnClearFilterClick(object? sender, RoutedEventArgs e)
    {
        _settings.FilterWordsPath = "";
        _filterWordsCount = PreviewTextSanitize.ApplyFilterWordsFile("");
        _settings.Save();
        RefreshFilterStatus();
    }

    private sealed record ComboItem(string Id, string Label)
    {
        public override string ToString() => Label;
    }

    private string SelectedId(ComboBox box)
        => (box.SelectedItem as ComboItem)?.Id ?? "";

    private void RefreshModelStatus()
    {
        var whisperOk = File.Exists(Path.Combine(AppPaths.AppDataDir, "models", "asr", AsrModelStore.TurboFileName));
        var llamaOk = ManagedLlmInstaller.HasLlamaRuntime(_settings);
        var ggufOk = ManagedLlmInstaller.HasSelectedGguf(_settings);
        ModelStatus.Text = $"识别模型：{(whisperOk ? "已安装" : "未安装")} · 翻译运行时：{(llamaOk ? "已安装" : "未安装")} · 翻译模型：{(ggufOk ? "已安装" : "未安装")}";
    }

    private async Task CheckUpdateAsync()
    {
        UpdateStatus.Text = "正在检查更新…";
        try
        {
            var r = await UpdateCheckMac.CheckAsync();
            UpdateStatus.Text = r.UpdateAvailable
                ? $"有新版本 {r.LatestVersion}（当前 {UpdateCheckMac.AppVersion()}）"
                : $"已是最新版本（{UpdateCheckMac.AppVersion()}）";
            BtnUpdate.Content = r.UpdateAvailable ? "前往下载页…" : "检查更新…";
            _updateAvailable = r.UpdateAvailable;
        }
        catch (Exception ex)
        {
            UpdateStatus.Text = "检查失败（网络）· " + ex.Message;
        }
        finally
        {
            BtnUpdate.IsEnabled = true;
        }
    }

    private bool _updateAvailable;

    private async void OnCheckUpdateClick(object? sender, RoutedEventArgs e)
    {
        if (_updateAvailable)
        {
            UpdateCheckMac.OpenReleasesPage();
            return;
        }
        await CheckUpdateAsync();
    }

    private void OnOpenModelDirClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var dir = AppPaths.AppDataDir;
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "open",
                ArgumentList = { dir },
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ModelStatus.Text = "无法打开目录 · " + ex.Message;
        }
    }

    private async void OnModelsClick(object? sender, RoutedEventArgs e)
    {
        if (ModelManageRequested is not null)
            await ModelManageRequested();
        RefreshModelStatus();
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        _settings.UiLanguage = SelectedId(CmbUiLang);
        _settings.AsrQuality = SelectedId(CmbAsrQuality);
        _settings.TranslateModelId = SelectedId(CmbTranslateModel);
        _settings.TranslateTarget = SelectedId(CmbTranslateTarget);
        _settings.SubtitleMode = SelectedId(CmbSubtitleMode);
        _settings.SubFont = TxtSubFont.Text?.Trim() ?? "";
        _settings.SubFontSize = (int)NumSubFontSize.Value;
        _settings.SubBorderSize = (int)NumSubBorder.Value;
        _settings.SubMarginY = (int)NumSubMargin.Value;
        _settings.SubDelaySec = (double)NumSubDelay.Value;
        _settings.HwDec = SelectedId(CmbHwDec);
        _settings.VideoFit = SelectedId(CmbVideoFit);
        _settings.SeekStepSeconds = (int)NumSeekStep.Value;
        _settings.AutoPlayNext = ChkAutoPlayNext.IsChecked == true;
        _settings.AddSameFolderToPlaylist = ChkSameFolder.IsChecked == true;
        _settings.RememberPlaybackPosition = ChkRememberPos.IsChecked == true;
        _settings.HfEndpoint = TxtHfEndpoint.Text?.Trim() ?? "";
        _settings.Save();

        Applied?.Invoke(_settings);
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}

internal static class ComboListExt
{
    public static int Index<T>(this IReadOnlyList<T> list, Func<T, bool> predicate)
    {
        for (var i = 0; i < list.Count; i++)
            if (predicate(list[i]))
                return i;
        return 0;
    }
}
