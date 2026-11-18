using TransubPlayer.Localization;

namespace TransubPlayer.Services;

internal enum EngineKind
{
    Embedded,
}

internal sealed record EngineRoot(string Path, EngineKind Kind, string Label);

/// <summary>Embedded ASR model root resolution.</summary>
internal static class EngineLocator
{
    static EngineLocator()
    {
        // 一次性路径探针：打包/便携/dev 各形态的目录解析一目了然
        try
        {
            var cwd = Environment.CurrentDirectory;
            var settings = AppSettings.Load();
            PlayerLog.Write(
                $"paths · cwd={cwd} · appData={AppPaths.AppDataDir} · modelsRoot={ResolveModelsRoot(settings)}"
                + $" · portable={AppPaths.IsPortable} · devTree={AppPaths.IsDevTree} · base={AppContext.BaseDirectory}");
        }
        catch (Exception ex)
        {
            PlayerLog.Write("paths probe failed · " + ex.Message);
        }
    }

    public static void Invalidate() => PresetReadiness.InvalidateDiskProbe();

    public static EngineRoot? Find(AppSettings settings)
    {
        var root = AsrModelStore.ResolveModelsRoot(settings);
        if (!AsrModelStore.IsTurboInstalled(root))
            return null;
        return new EngineRoot(root, EngineKind.Embedded, Loc.Get("Settings.Engine.Embedded"));
    }

    public static string ResolveModelsRoot(AppSettings settings, EngineRoot? root = null)
        => root?.Path ?? AsrModelStore.ResolveModelsRoot(settings);

    public static bool IsAsrInstalled(string modelsRoot, string modelId)
        => AsrModelStore.IsInstalled(modelsRoot, modelId);
}
