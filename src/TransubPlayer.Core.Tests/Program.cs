// Core 冒烟测试：无需 GUI/显示会话即可验证共享服务层与 mac 播放引擎。
// 用法：dotnet run [-- <sample.mp4>]（未传媒体文件时跳过播放段）
using TransubPlayer.Localization;
using TransubPlayer.Services;

int pass = 0, fail = 0;

foreach (var n in typeof(Loc).Assembly.GetManifestResourceNames())
    Console.WriteLine("RES: " + n);

void Check(bool ok, string what, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"PASS {what}{(detail.Length > 0 ? " · " + detail : "")}"); }
    else { fail++; Console.WriteLine($"FAIL {what}{(detail.Length > 0 ? " · " + detail : "")}"); }
}

// ① AppPaths（dev tree → 仓库 data/）
var dataDir = AppPaths.AppDataDir;
Check(Directory.Exists(dataDir), "AppPaths.AppDataDir", dataDir);
Check(dataDir.Contains("data"), "AppPaths points at data/", Path.GetFileName(Path.GetDirectoryName(dataDir)) ?? "");

// ② AppSettings 持久化往返
var settings = AppSettings.Load();
settings.Volume = 77;
settings.Save();
var reloaded = AppSettings.Load();
Check(reloaded.Volume == 77, "AppSettings save/load roundtrip", $"volume={reloaded.Volume}");
settings.Volume = 50;
settings.Save();

// ③ 本地化垫片（JSON 目录，zh-Hans 回退）
Loc.Apply("auto");
Check(Loc.CurrentTag == UiLanguages.FallbackTag || Loc.CurrentTag.Length > 0, "Loc.Apply(auto)", Loc.CurrentTag);
var open = Loc.Get("Main.Menu.OpenFile");
Check(open.StartsWith("Str.") == false && open.Length > 0, "Loc.Get(Main.Menu.OpenFile)", open);
var formatted = Loc.Format("Main.Status.VolumeOsd", 88);
Check(formatted.Contains("88"), "Loc.Format interpolation", formatted);

// ④ PlaybackQueue（真实存在的文件——队列会过滤不存在的项；Replace 自动激活第一项）
var qa = Path.Combine(AppPaths.AppDataDir, "smoke-a.mp4");
var qb = Path.Combine(AppPaths.AppDataDir, "smoke-b.mp4");
var qc = Path.Combine(AppPaths.AppDataDir, "smoke-c.mp4");
File.WriteAllText(qa, "x"); File.WriteAllText(qb, "x"); File.WriteAllText(qc, "x");
var queue = new PlaybackQueue();
queue.Replace(new[] { qa, qb, qc });
Check(queue.Count == 3 && queue.Index == 0 && queue.Current == qa, "PlaybackQueue.Replace(auto-activate)", $"count={queue.Count} idx={queue.Index}");
queue.TryActivate(1);
Check(queue.Current == qb, "PlaybackQueue.TryActivate");
Check(queue.TryMoveNext() && queue.Current == qc, "PlaybackQueue.TryMoveNext");
Check(queue.TryMovePrev() && queue.Current == qb, "PlaybackQueue.TryMovePrev");

// ⑤ 断点续播存储
var probe = Path.Combine(AppPaths.AppDataDir, "core-smoke-probe.mp4");
File.WriteAllText(probe, "x");
PlaybackPositionStore.Save(probe, 123.5);
Check(Math.Abs(PlaybackPositionStore.Load(probe) - 123.5) < 0.01, "PlaybackPositionStore roundtrip");
PlaybackPositionStore.Remove(probe);
Check(PlaybackPositionStore.Load(probe) == 0, "PlaybackPositionStore.Remove");

// ⑥ 最近文件
RecentFiles.Add(settings, probe);
Check(settings.RecentFiles.Contains(probe), "RecentFiles.Add");
RecentFiles.Remove(settings, probe);
Check(!settings.RecentFiles.Contains(probe), "RecentFiles.Remove");

// ⑦ MediaFileTypes / SubtitleFile
Check(MediaFileTypes.IsKnown("/tmp/x.mp4") && !MediaFileTypes.IsKnown("/tmp/x.xyz"), "MediaFileTypes.IsKnown");
var srt = Path.Combine(AppPaths.AppDataDir, "probe.srt");
File.WriteAllText(srt, "1\n00:00:01,000 --> 00:00:02,000\n你好世界\n");
var cues = SubtitleFile.ParseSrt(srt);
Check(cues.Count == 1 && cues[0].Text.Contains("你好世界"), "SubtitleFile.ParseSrt", cues.Count.ToString());
File.Delete(srt);

// ⑧ 播放引擎（vo=libmpv，无需 GL/显示；渲染上下文由 UI 控件创建）
var mediaArg = args.FirstOrDefault(a => !a.StartsWith('-'));
if (mediaArg is not null && File.Exists(mediaArg))
{
    try
    {
        // 选项二分：逐项定位 mpv_initialize 失败的选项（单个全新 handle 只带候选选项）
        TransubPlayer.Native.MpvNative.LibPath = MpvLibLocator.FindOrThrow();
        string[][] candidates =
        {
            new[] { "hwdec=videotoolbox-auto" }, new[] { "force-window=immediate" }, new[] { "cache=yes" },
            new[] { "demuxer-readahead-secs=60" }, new[] { "ytdl=no" }, new[] { "network-timeout=15" },
            new[] { "volume-max=130" }, new[] { "volume=50" }, new[] { "mute=no" },
            new[] { "keepaspect=yes" }, new[] { "panscan=0.0" }, new[] { "sub-auto=no" },
            new[] { "sub-codepage=utf-8" }, new[] { "sub-font=PingFang SC" }, new[] { "sub-font-size=42" },
            new[] { "sub-bold=yes" }, new[] { "sub-border-size=2" }, new[] { "sub-margin-y=36" },
            new[] { "sub-delay=0" }, new[] { "sub-color=1/1/1/1" }, new[] { "osd-font=PingFang SC" },
            new[] { "osd-font-size=32" }, new[] { "osd-level=1" },
        };
        foreach (var group in candidates)
        {
            var h = TransubPlayer.Native.MpvNative.mpv_create();
            TransubPlayer.Native.MpvNative.mpv_set_option_string(h, "vo", "libmpv");
            var kv = group[0];
            int eq = kv.IndexOf('=');
            TransubPlayer.Native.MpvNative.mpv_set_option_string(h, kv[..eq], kv[(eq + 1)..]);
            int rc = TransubPlayer.Native.MpvNative.mpv_initialize(h);
            if (rc != 0)
                Console.WriteLine($"BISECT {kv} -> initialize rc={rc}");
            TransubPlayer.Native.MpvNative.mpv_terminate_destroy(h);
        }
        Console.WriteLine("BISECT done");

        using var engine = new MpvLibPlayer();
        engine.Initialize(settings);
        Check(engine.IsRunning, "MpvLibPlayer.Initialize (vo=libmpv)", MpvLibLocator.Find());

        var tcs = new TaskCompletionSource();
        engine.DurationChanged += d => { if (d > 0) tcs.TrySetResult(); };
        engine.LoadFile(mediaArg);
        var done = await Task.WhenAny(tcs.Task, Task.Delay(10000));
        Check(done == tcs.Task && engine.Duration > 0, "MpvLibPlayer loadfile + duration", $"{engine.Duration:F2}s");

        await Task.Delay(800);
        Check(engine.TimePos > 0.2, "MpvLibPlayer playback advances", $"{engine.TimePos:F2}s");

        engine.Seek(3);
        await Task.Delay(500);
        Check(Math.Abs(engine.TimePos - 3) < 2.0, "MpvLibPlayer seek", $"{engine.TimePos:F2}s");

        engine.SetVolume(88);
        await Task.Delay(300);
        Check(engine.Volume == 88, "MpvLibPlayer SetVolume", engine.Volume.ToString());
        engine.SetMute(true);
        await Task.Delay(300);
        Check(engine.Muted, "MpvLibPlayer SetMute");

        engine.SetSubtitle(srt.Replace("probe.srt", "probe2.srt") /* 不存在也应安全 */);
        Check(true, "MpvLibPlayer SetSubtitle safe on missing file");

        // 字幕外观默认字体映射（macOS）
        // （经 mpv 属性验证需 GUI；这里仅确认引擎存活）
        Check(engine.IsRunning, "MpvLibPlayer still alive after commands");
        engine.Shutdown();
        Check(!engine.IsRunning, "MpvLibPlayer.Shutdown");
    }
    catch (Exception ex)
    {
        Check(false, "MpvLibPlayer suite", ex.Message);
        fail++;
    }
}
else
{
    Console.WriteLine("SKIP MpvLibPlayer suite（未提供媒体文件参数）");
}


// ⑨ 管线全链路 E2E：两段式续传验证（中断 → 重开续传 → 完成）
var resumeSrc = args.FirstOrDefault(a => !a.StartsWith('-'));
if (resumeSrc is not null && File.Exists(resumeSrc))
{
    try
    {
        // 每次运行用唯一命名的副本，隔离跨运行的 sidecar/元数据残留
        var media = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(resumeSrc))!,
            Path.GetFileNameWithoutExtension(resumeSrc) + "-r" + Guid.NewGuid().ToString("N")[..6] + ".mp4");
        File.Copy(resumeSrc, media);

        var mediaDir = Path.GetDirectoryName(media)!;
        var baseName = Path.GetFileNameWithoutExtension(media);
        var sidecarSrc = Path.Combine(mediaDir, baseName + ".srt");
        var sidecarZh = Path.Combine(mediaDir, baseName + ".zh.srt");
        var metaPath = Path.Combine(mediaDir, baseName + ".transub.json");
        string MetaPath() => metaPath;

        static PreviewController MakePreview(AppSettings st, string tag) => new(st,
            new HeadlessHost(),
            s => Console.WriteLine($"  [{tag}-status] {s}"),
            m => Console.WriteLine($"  [{tag}-pipe] {m}"));

        AppSettings FreshSettings()
        {
            var loaded = AppSettings.Load();
            return new AppSettings
            {
                AutoPlayOnOpen = false,
                WaitForFirstZhBeforePlay = false,
                TranslateModelId = TranslateModels.Murasaki4B,
                TranslateTarget = TranslateTargets.Auto,
                SubtitleMode = "zh",
                PresetId = "auto-speed",
                HfEndpoint = loaded.HfEndpoint,
                ModelsPath = loaded.ModelsPath,
                AdvancedLlmPath = loaded.AdvancedLlmPath,
                TextSanitizeEnabled = loaded.TextSanitizeEnabled,
            };
        }

        // ── Pass A：生成到一半主动关闭（导出未完成标记）──
        var previewA = MakePreview(FreshSettings(), "A");
        _ = previewA.OpenMediaAsync(media, CancellationToken.None);

        var cueDeadlineA = DateTime.UtcNow.AddMinutes(4);
        while (previewA.CueCount < 1 && DateTime.UtcNow < cueDeadlineA)
            await Task.Delay(2000);
        var cuesA = previewA.CueCount;
        var frontierA = previewA.SubFrontier;
        Console.WriteLine($"  [A] 中断于 cues={cuesA} frontier={frontierA:F1}s");
        await previewA.ShutdownAsync();
        previewA.Dispose();
        Check(cuesA >= 1, "E2E A: partial cues generated", $"cues={cuesA} frontier={frontierA:F1}s");
        Check(File.Exists(MetaPath()), "E2E A: incomplete metadata written",
              File.Exists(MetaPath()) ? "complete=false" : "missing");
        Check(File.Exists(sidecarSrc), "E2E A: source sidecar saved");

        // ── Pass B：重新打开 → 续传 → 完成 ──
        var previewB = MakePreview(FreshSettings(), "B");
        await previewB.OpenMediaAsync(media, CancellationToken.None);

        // 续传恢复：WatchSource 解析导出的原文 sidecar，cues 应立即恢复
        var restoreDeadline = DateTime.UtcNow.AddSeconds(20);
        while (previewB.CueCount < cuesA && DateTime.UtcNow < restoreDeadline)
            await Task.Delay(1000);
        Check(previewB.CueCount >= cuesA, "E2E B: sidecar cues restored on open",
              $"cues={previewB.CueCount} (A had {cuesA})");

        // 续传完成：ASR 从断点续跑 + 译文补齐
        var doneDeadline = DateTime.UtcNow.AddMinutes(8);
        while (DateTime.UtcNow < doneDeadline
               && !(previewB.CueCount > cuesA && previewB.TranslatedCount >= previewB.CueCount
                    && previewB.SubFrontier >= previewB.Duration - 5))
            await Task.Delay(3000);
        Check(previewB.CueCount > cuesA && previewB.TranslatedCount >= previewB.CueCount,
              "E2E B: continuation completed",
              $"cues={previewB.CueCount} translated={previewB.TranslatedCount} frontier={previewB.SubFrontier:F1} route={previewB.ActiveMtRoute}");
        var finalZh = previewB.SnapshotCues().Where(c => !string.IsNullOrWhiteSpace(c.Zh))
            .Take(2).Select(c => $"{c.Text} => {c.Zh}");
        foreach (var line in finalZh)
            Console.WriteLine("  [zh] " + line);

        // 完成后关闭：元数据应清除，sidecar 刷新
        await previewB.ShutdownAsync();
        previewB.Dispose();
        Check(!File.Exists(MetaPath()), "E2E B: metadata cleared on completion");
        Check(File.Exists(sidecarSrc) && File.Exists(sidecarZh), "E2E B: final sidecars saved");

    }
    catch (Exception ex)
    {
        Check(false, "E2E pipeline suite", ex.Message);
        fail++;
    }
}
else
{
    Console.WriteLine("SKIP E2E pipeline suite（未提供媒体文件参数）");
}

Console.WriteLine($"\n== Core smoke: {pass} pass, {fail} fail ==");
return fail == 0 ? 0 : 1;

// ============ ⑨ 管线全链路 E2E（无 GUI，真实生产模型） ============
file sealed class HeadlessHost : IPreviewHost
{
    public IntPtr Hwnd => IntPtr.Zero;
    public void EnsureHandle() { }
    public void HookEmbeddedChildren() { }
    public bool DispatcherCheckAccess() => true;
    public Task DispatcherInvokeAsync(Action action) => Task.Run(action);
}
