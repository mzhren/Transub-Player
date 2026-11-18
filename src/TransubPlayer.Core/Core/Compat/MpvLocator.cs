namespace TransubPlayer.Services;

/// <summary>
/// macOS 版 mpv CLI 二进制定位（Windows 版为 mpv.exe 探测）。
/// mpv CLI 用于音频提取（AsrAudioExtract）、时长探测与流录制 remux；
/// 播放主链路走进程内 libmpv（见 MpvLibLocator）。
/// </summary>
internal static class MpvLocator
{
    private static string? _cachedPath;
    private static bool _cached;
    private static DateTime _cachedUtc;

    public static string? Find()
    {
        if (_cached && (DateTime.UtcNow - _cachedUtc).TotalSeconds < 60)
            return _cachedPath;

        string? found = null;
        foreach (var candidate in Candidates())
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                found = Path.GetFullPath(candidate);
                break;
            }
        }

        _cachedPath = found;
        _cached = true;
        _cachedUtc = DateTime.UtcNow;
        return found;
    }

    public static void Invalidate()
    {
        _cached = false;
        _cachedPath = null;
    }

    public static IEnumerable<string> Candidates()
    {
        var env = Environment.GetEnvironmentVariable("TRANSUB_MPV");
        if (!string.IsNullOrWhiteSpace(env))
            yield return env;

        // .app bundle: Contents/Resources/mpv / Contents/MacOS/mpv
        yield return Path.Combine(AppContext.BaseDirectory, "mpv");
        yield return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources", "mpv"));

        // Homebrew / 系统路径
        yield return "/opt/homebrew/bin/mpv";
        yield return "/usr/local/bin/mpv";

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string full;
            try { full = Path.Combine(dir.Trim(), "mpv"); }
            catch { continue; }
            yield return full;
        }
    }
}
