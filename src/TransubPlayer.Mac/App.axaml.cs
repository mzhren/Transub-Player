using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using TransubPlayer.Localization;
using TransubPlayer.Services;

namespace TransubPlayer.Mac;

public class App : Application
{
    public App() => Console.Error.WriteLine("BOOT: App ctor");

    public override void Initialize()
    {
        Console.Error.WriteLine("BOOT: Initialize begin");
        AvaloniaXamlLoader.Load(this);
        Console.Error.WriteLine("BOOT: Initialize done");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // UI 线程未处理异常兜底：记日志并吞掉，防止单个点击事件杀死整个应用
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            PlayerLog.Write("UI 未处理异常: " + e.Exception);
            Console.Error.WriteLine("UI unhandled: " + e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            PlayerLog.Write("进程未处理异常: " + e.ExceptionObject);
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Console.Error.WriteLine("BOOT: OFIC begin");
            PlayerLog.Write("启动: OnFrameworkInitializationCompleted 创建 MainWindow 前");
            desktop.MainWindow = new MainWindow();
            PlayerLog.Write("启动: MainWindow 创建完成");

            // macOS 菜单栏托盘：常驻入口 + 唤起主窗口
            var tray = new TrayIcon
            {
                ToolTipText = "Transub Player",
                IsVisible = true,
                Menu = new NativeMenu(),
            };
            try
            {
                using var s = Avalonia.Platform.AssetLoader.Open(
                    new Uri("avares://TransubPlayer/Assets/app.png"));
                tray.Icon = new WindowIcon(new Bitmap(s));
            }
            catch { /* 图标缺失不致命 */ }

            var openItem = new NativeMenuItem("打开 Transub Player");
            openItem.Click += (_, _) => ActivateMainWindow(desktop);
            tray.Menu.Items.Add(openItem);
            tray.Menu.Items.Add(new NativeMenuItemSeparator());
            var quitItem = new NativeMenuItem("退出");
            quitItem.Click += (_, _) => desktop.Shutdown();
            tray.Menu.Items.Add(quitItem);
            tray.Clicked += (_, _) => ActivateMainWindow(desktop);

            TrayIcon.SetIcons(this, new Avalonia.Controls.TrayIcons { tray });
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static void ActivateMainWindow(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var win = desktop.MainWindow;
        if (win is null) return;
        if (win.WindowState == WindowState.Minimized)
            win.WindowState = WindowState.Normal;
        win.Show();
        win.Activate();
    }
}
