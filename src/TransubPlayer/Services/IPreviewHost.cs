namespace TransubPlayer.Services;

/// <summary>
/// PreviewController 对宿主窗口的最小依赖抽象。
/// Windows：MainWindow 包装 MpvHost（HwndHost 嵌入 + WPF Dispatcher）。
/// macOS：MainWindow 包装 Avalonia Dispatcher（Hwnd 恒为 Zero，嵌入钩子为 no-op）。
/// </summary>
internal interface IPreviewHost
{
    /// <summary>mpv --wid 嵌入用窗口句柄；macOS 进程内渲染恒为 IntPtr.Zero。</summary>
    IntPtr Hwnd { get; }

    /// <summary>确保 HWND 已创建（Windows）。</summary>
    void EnsureHandle();

    /// <summary>mpv 启动后重新挂钩子窗口消息（Windows）；macOS no-op。</summary>
    void HookEmbeddedChildren();

    bool DispatcherCheckAccess();

    Task DispatcherInvokeAsync(Action action);
}
