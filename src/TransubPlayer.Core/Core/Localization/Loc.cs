using System.Globalization;
using System.Reflection;

namespace TransubPlayer.Localization;

/// <summary>
/// 跨平台 UI 字符串查找（Windows 版基于 WPF ResourceDictionary，本版基于嵌入式 JSON）。
/// 数据源为 Strings.*.xaml 生成的 strings.*.json（tools 生成，单一事实来源仍是 WPF 的 XAML）。
/// API 与 Windows 版 <c>TransubPlayer.Localization.Loc</c> 完全一致：
/// <c>Loc.Get("Main.Status.Ready")</c>，回退包始终 zh-Hans。
/// </summary>
public static class Loc
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Dictionary<string, string>> Packs = new();
    private static string _appliedTag = UiLanguages.FallbackTag;

    /// <summary>Resolved catalog tag currently loaded (never <c>auto</c>).</summary>
    public static string CurrentTag
    {
        get { lock (Gate) return _appliedTag; }
    }

    public static CultureInfo Culture { get; private set; } = UiLanguages.ToCulture(UiLanguages.FallbackTag);

    /// <summary>Raised after dictionaries / UICulture change.</summary>
    public static event Action? Changed;

    /// <summary>Load catalog for the resolved tag. Safe to call before any UI exists.</summary>
    public static void Apply(string? preference)
    {
        var tag = UiLanguages.Resolve(preference);
        var culture = UiLanguages.ToCulture(tag);

        lock (Gate)
        {
            _appliedTag = tag;
            Culture = culture;
            _ = LoadPack(UiLanguages.FallbackTag);
            if (!tag.Equals(UiLanguages.FallbackTag, StringComparison.OrdinalIgnoreCase))
                _ = LoadPack(tag);
            try
            {
                CultureInfo.CurrentUICulture = culture;
                CultureInfo.DefaultThreadCurrentUICulture = culture;
            }
            catch
            {
                // invariant contexts
            }
        }

        Changed?.Invoke();
    }

    public static string Get(string key)
    {
        var resKey = ToResourceKey(key);
        lock (Gate)
        {
            if (Packs.TryGetValue(_appliedTag, out var pack)
                && pack.TryGetValue(resKey, out var s) && !string.IsNullOrEmpty(s))
                return s;
            if (_appliedTag != UiLanguages.FallbackTag
                && Packs.TryGetValue(UiLanguages.FallbackTag, out var fb)
                && fb.TryGetValue(resKey, out s) && !string.IsNullOrEmpty(s))
                return s;
        }

        return resKey;
    }

    public static string Format(string key, params object?[] args)
    {
        var template = Get(key);
        try
        {
            return string.Format(Culture, template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    /// <summary>Mirrors WPF LocExtension.ToResourceKey.</summary>
    private static string ToResourceKey(string key)
    {
        key = key.Trim();
        return key.StartsWith("Str.", StringComparison.Ordinal) ? key : "Str." + key;
    }

    private static Dictionary<string, string> LoadPack(string tag)
    {
        if (Packs.TryGetValue(tag, out var cached))
            return cached;

        var name = $"TransubPlayer.Localization.strings.{tag}.json";
        var asm = typeof(Loc).Assembly;
        using var stream = asm.GetManifestResourceStream(name);
        if (stream is null)
        {
            Packs[tag] = new Dictionary<string, string>();
            return Packs[tag];
        }

        var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new();
        Packs[tag] = dict;
        return dict;
    }
}
