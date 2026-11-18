using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace TransubPlayer.Mac;

/// <summary>
/// macOS 更新检查：查询 GitHub Releases（与 Windows 版 AppUpdateService 同源），
/// 比较 mac 资产版本；本阶段不做自替换——确认后打开发布页由用户拖拽升级。
/// </summary>
internal static class UpdateCheckMac
{
    private const string ReleasesApi = "https://api.github.com/repos/dlsandy/Transub-Player/releases/latest";
    private const string ReleasesPage = "https://github.com/dlsandy/Transub-Player/releases/latest";

    public sealed record Result(bool UpdateAvailable, string LatestVersion, string? DmgUrl, string? Notes);

    public static async Task<Result> CheckAsync()
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TransubPlayer-Mac");
        http.Timeout = TimeSpan.FromSeconds(15);

        using var resp = await http.GetAsync(ReleasesApi);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        var body = root.TryGetProperty("body", out var b) ? b.GetString() : null;

        string? dmg = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                if (a.TryGetProperty("browser_download_url", out var u))
                {
                    var url = u.GetString() ?? "";
                    if (url.Contains("mac", StringComparison.OrdinalIgnoreCase)
                        && (url.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase)
                            || url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
                    {
                        dmg = url;
                        break;
                    }
                }
            }
        }

        var latest = NormalizeVersion(tag);
        var current = AppVersion();
        var available = !string.IsNullOrEmpty(latest)
                        && string.CompareOrdinal(latest, current) > 0;
        return new Result(available, latest, dmg, body);
    }

    /// <summary>CFBundleShortVersionString（打包 Info.plist）或程序集版本。</summary>
    public static string AppVersion()
    {
        var bundleVersion = NSBundleShortVersion();
        if (!string.IsNullOrWhiteSpace(bundleVersion))
            return bundleVersion;
        var asm = typeof(UpdateCheckMac).Assembly.GetName().Version;
        return asm is null ? "" : $"{asm.Major}.{asm.Minor}.{asm.Build}";
    }

    /// <summary>去除前缀 v / build 号，取 x.y.z 形式（.NET 版本比较用 ordinal 字符串序即可）。</summary>
    private static string NormalizeVersion(string tag)
    {
        var v = tag.TrimStart('v', 'V');
        var dash = v.IndexOf('-');
        if (dash > 0) v = v[..dash];
        return v;
    }

    private static string? NSBundleShortVersion()
    {
        try
        {
            // 通过 Avalonia Native 层不可行；直接读 Info.plist（打包态）
            var plistPath = System.IO.Path.Combine(AppContext.BaseDirectory, "..", "Info.plist");
            if (!File.Exists(plistPath))
                return "";
            using var stream = File.OpenRead(plistPath);
            var doc = System.Xml.Linq.XDocument.Load(stream);
            var dict = doc.Root?.Element("dict");
            if (dict is null) return "";
            var keys = dict.Elements("key");
            foreach (var k in keys)
            {
                if (k.Value == "CFBundleShortVersionString")
                {
                    var next = k.NextNode as System.Xml.Linq.XElement;
                    return next?.Value;
                }
            }
            return "";
        }
        catch
        {
            return "";
        }
    }

    public static void OpenReleasesPage()
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = ReleasesPage,
            UseShellExecute = true,
        });
    }
}
