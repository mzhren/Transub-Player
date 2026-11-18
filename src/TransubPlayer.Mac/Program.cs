using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;

namespace TransubPlayer.Mac;

internal static class Program
{
    public static List<string> StartupMedia { get; } = new();

    [STAThread]
    public static void Main(string[] args)
    {
        // CLI 参数中的媒体文件直接打开（open -a / 拖到 Dock 图标 / 命令行）
        foreach (var p in args)
        {
            if (!p.StartsWith("-", StringComparison.Ordinal) && File.Exists(p))
                StartupMedia.Add(p);
        }

        // 单实例：已有实例时把文件转发过去并退出
        if (!SingleInstanceMac.TryAcquireFirst(StartupMedia.ToArray()))
            return;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();
}
