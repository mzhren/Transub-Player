namespace TransubPlayer.Services;

/// <summary>
/// Windows 工厂：mpv.exe 子进程 + JSON IPC 引擎。
/// （macOS 的同名工厂位于 TransubPlayer.Core，返回进程内 libmpv 引擎；两者互不链接。）
/// </summary>
internal static class MpvEngineFactory
{
    public static IMpvEngine Create() => new MpvPlayer();
}
