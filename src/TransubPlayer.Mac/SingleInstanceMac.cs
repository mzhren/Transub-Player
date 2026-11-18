using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using TransubPlayer.Services;

namespace TransubPlayer.Mac;

/// <summary>
/// macOS 单实例：Unix domain socket（Windows 版为命名 Mutex + NamedPipe）。
/// 第一个实例 bind 成功；后续实例把文件路径发到 socket 后退出。
/// 收到的路径通过 <see cref="Forwarded"/> 事件回到第一实例（UI 侧自行 marshal）。
/// </summary>
internal static class SingleInstanceMac
{
    /// <summary>第一实例收到转发文件路径时触发（socket 接收线程）。</summary>
    public static event Action<string[]>? Forwarded;

    private static string SocketPath
        => Path.Combine(AppPaths.AppDataDir, "transub-player.sock");

    /// <returns>true = 本进程是第一实例；false = 已转发给现有实例，调用方应退出。</returns>
    public static bool TryAcquireFirst(string[] mediaPaths)
    {
        var socketPath = SocketPath;
        Directory.CreateDirectory(Path.GetDirectoryName(socketPath)!);

        try
        {
            if (File.Exists(socketPath) && TryForward(mediaPaths, socketPath))
                return false;
            try { File.Delete(socketPath); } catch { /* ignore */ }
        }
        catch
        {
            // 探测失败按第一实例处理
        }

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(2);
        }
        catch
        {
            return false;
        }

        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var client = await listener.AcceptAsync();
                    var buffer = new byte[64 * 1024];
                    var read = await client.ReceiveAsync(buffer);
                    var payload = Encoding.UTF8.GetString(buffer, 0, read);
                    var paths = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (paths.Length > 0)
                        Forwarded?.Invoke(paths);
                }
                catch
                {
                    // 客户端断开/转发失败 → 忽略，继续监听
                }
            }
        });

        return true;
    }

    private static bool TryForward(string[] mediaPaths, string socketPath)
    {
        try
        {
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            client.Connect(new UnixDomainSocketEndPoint(socketPath));
            var payload = string.Join("\n", mediaPaths);
            client.Send(Encoding.UTF8.GetBytes(payload));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
