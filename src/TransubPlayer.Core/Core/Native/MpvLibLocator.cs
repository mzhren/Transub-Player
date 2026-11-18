using TransubPlayer.Localization;

namespace TransubPlayer.Services;

/// <summary>
/// 定位 macOS libmpv.dylib：环境变量 → .app Contents/Frameworks → app 目录 → Homebrew → 系统路径。
/// 播放主链路为进程内 libmpv（Windows 版为 mpv.exe 子进程 + IPC）。
/// </summary>
internal static class MpvLibLocator
{
    private static string? _cachedPath;
    private static bool _cached;

    public static string? Find()
    {
        if (_cached) return _cachedPath;
        if (_envFile is not null)
        {
            _cachedPath = _envFile;
            _cached = true;
            return _cachedPath;
        }

        string? found = null;
        foreach (var dir in CandidateDirs())
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                continue;
            // 打包后 Frameworks 里是版本化真实名（libmpv.2.dylib）；brew 是符号链接 libmpv.dylib
            var hit = Directory.EnumerateFiles(dir, "libmpv*.dylib")
                .OrderBy(n => n == Path.Combine(dir, "libmpv.dylib") ? 0 : 1)
                .FirstOrDefault();
            if (hit is not null)
            {
                found = Path.GetFullPath(hit);
                break;
            }
        }

        _cachedPath = found;
        _cached = true;
        return found;
    }

    public static void Invalidate()
    {
        _cached = false;
        _cachedPath = null;
    }

    public static string FindOrThrow()
        => Find() ?? throw new InvalidOperationException(Loc.Get("Main.Status.MpvMissing"));

    private static IEnumerable<string> CandidateDirs()
    {
        var env = Environment.GetEnvironmentVariable("TRANSUB_LIBMPV");
        if (!string.IsNullOrWhiteSpace(env))
        {
            yield return Path.GetDirectoryName(Path.GetFullPath(env)) ?? "";
            // env 指向文件本体时优先直接使用
            if (File.Exists(env))
                _envFile = env;
        }

        // .app bundle: TransubPlayer.app/Contents/Frameworks/
        yield return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Frameworks"));
        // side-by-side（开发输出 / 便携布局）
        yield return AppContext.BaseDirectory;
        yield return AppPaths.NativeRoot;

        // Homebrew (Apple Silicon / Intel) 与系统路径
        yield return "/opt/homebrew/lib";
        yield return "/usr/local/lib";
    }

    private static string? _envFile;
}
