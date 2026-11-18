namespace TransubPlayer.Services;

/// <summary>Local MT GGUF preference. Extend <see cref="Selectable"/> and <see cref="ManagedLlmCatalog"/> together.</summary>
internal static class TranslateModels
{
    public const string TranslateGemma4B = "translategemma-4b-q4";
    public const string Murasaki4B = "murasaki-4b-iq4xs";

    private static readonly string[] TranslateGemmaFilenames =
    [
        "translategemma-4b-it-Q4_K_M.gguf",
        "translategemma-4b-it.Q4_K_M.gguf",
        "translategemma-4b-it-q4_k_m.gguf",
        "translategemma-4b-it-Q4_K_S.gguf",
        "translategemma-4b-it-Q5_K_M.gguf",
    ];

    private static readonly string[] MurasakiFilenames =
    [
        "Murasaki-4B-v0.3.IQ4_XS.gguf",
        "Murasaki-4B-v0.3-IQ4_XS.gguf",
        "murasaki-4b-v0.3.iq4_xs.gguf",
        "murasaki-4b-v0.3-iq4_xs.gguf",
    ];

    public static IReadOnlyList<string> Selectable { get; } = [TranslateGemma4B, Murasaki4B];

    /// <summary>All known MT filenames (discovery / any-GGUF probes).</summary>
    public static IReadOnlyList<string> PreferredFilenames { get; } =
        [.. TranslateGemmaFilenames, .. MurasakiFilenames];

    public static IReadOnlyList<string> PreferredFilenamesFor(string? modelId)
        => Normalize(modelId) switch
        {
            Murasaki4B => MurasakiFilenames,
            _ => TranslateGemmaFilenames,
        };

    public static string Normalize(string? raw)
    {
        var id = (raw ?? "").Trim();
        if (id.Length == 0)
            return TranslateGemma4B;
        if (string.Equals(id, Murasaki4B, StringComparison.OrdinalIgnoreCase)
            || id.Contains("murasaki", StringComparison.OrdinalIgnoreCase))
            return Murasaki4B;
        if (string.Equals(id, TranslateGemma4B, StringComparison.OrdinalIgnoreCase)
            || id.Contains("translategemma", StringComparison.OrdinalIgnoreCase))
            return TranslateGemma4B;
        if (IsLegacyRemoved(id))
            return TranslateGemma4B;
        return TranslateGemma4B;
    }

    public static bool IsTranslateGemma(string? raw)
        => Normalize(raw) == TranslateGemma4B;

    public static bool IsMurasaki(string? raw)
        => Normalize(raw) == Murasaki4B;

    public static ManagedGgufSpec ResolveSpec(string? raw)
        => Normalize(raw) switch
        {
            Murasaki4B => ManagedLlmCatalog.Murasaki4B,
            _ => ManagedLlmCatalog.TranslateGemma4B,
        };

    public static bool MatchesFamily(string pathOrName, string? modelId)
    {
        var name = Path.GetFileName(pathOrName);
        if (string.IsNullOrWhiteSpace(name))
            return false;
        return Normalize(modelId) switch
        {
            Murasaki4B => name.Contains("murasaki", StringComparison.OrdinalIgnoreCase),
            _ => name.Contains("translategemma", StringComparison.OrdinalIgnoreCase),
        };
    }

    /// <summary>
    /// Murasaki is JA→Chinese (ACGN) only; TranslateGemma covers all Player MT routes.
    /// Auto is not supported until scene sense resolves to Japanese (avoids JA prompts on EN/etc.).
    /// </summary>
    public static bool SupportsRoute(string? modelId, MtRoute route)
    {
        if (route.IsOff)
            return false;
        if (!IsMurasaki(modelId))
            return true;

        var src = SourceLanguages.Normalize(route.Source);
        if (!TranslateTargets.IsChinese(route.Target))
            return false;
        return src == SourceLanguages.Ja;
    }

    /// <summary>
    /// 按语言对解析生效模型（不改动用户设置的选择），返回 null 表示沿用用户选择：
    /// Murasaki 遇到非日→中路由时自动改用 TranslateGemma（全覆盖，避免"不支持"卡死）；
    /// JA→ZH 且用户选 TranslateGemma 时优先已安装的 Murasaki（ACGN 专用质量更好）。
    /// </summary>
    public static string? ResolveForRoute(string? selectedId, MtRoute route, Func<string?, bool> isInstalled)
    {
        if (route.IsOff)
            return null;
        var selected = Normalize(selectedId);
        if (IsTranslateGemma(selected)
            && SourceLanguages.Normalize(route.Source) == SourceLanguages.Ja
            && TranslateTargets.IsChinese(route.Target)
            && isInstalled(Murasaki4B))
            return Murasaki4B;
        if (IsMurasaki(selected)
            && !SupportsRoute(selected, route)
            && isInstalled(TranslateGemma4B))
            return TranslateGemma4B;
        return null;
    }

    public static string LabelKey(string? modelId)
        => IsMurasaki(modelId) ? "TranslateModel.Murasaki" : "TranslateModel.TranslateGemma";

    public static string HintKey(string? modelId)
        => IsMurasaki(modelId) ? "TranslateModel.Murasaki.Hint" : "TranslateModel.TranslateGemma.Hint";

    private static bool IsLegacyRemoved(string id)
        => string.Equals(id, "hy-mt2-1.8b-q4", StringComparison.OrdinalIgnoreCase)
           || id.StartsWith("hy-mt", StringComparison.OrdinalIgnoreCase);
}
