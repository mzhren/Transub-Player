namespace TransubPlayer.Services;

/// <summary>
/// macOS 工厂：进程内 libmpv 引擎。
/// （Windows 的同名工厂位于 WPF 工程，返回 mpv.exe 子进程引擎；两者互不链接。）
/// </summary>
internal static class MpvEngineFactory
{
    public static IMpvEngine Create() => new MpvLibPlayer();
}
