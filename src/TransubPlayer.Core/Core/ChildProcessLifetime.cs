using System.Collections.Concurrent;
using System.Diagnostics;

namespace TransubPlayer.Services;

/// <summary>
/// macOS 版子进程托管（Windows 版为 Job Object KILL_ON_JOB_CLOSE）。
/// 正常退出走 ProcessExit → KillRemaining；父进程被 SIGKILL 时子进程可能残留，
/// 由 mpv/llama-server 自身的 idle 守护与用户手动清理兜底（Phase 2 可再评估 posix 进程组方案）。
/// </summary>
internal static class ChildProcessLifetime
{
    public static readonly TimeSpan HttpBudget = TimeSpan.FromSeconds(2);
    public const int ProcessWaitMs = 4000;
    public const int MpvQuitWaitMs = 800;

    private static readonly ConcurrentDictionary<int, byte> Pids = new();
    private static readonly ConcurrentBag<Process> Kept = new();
    private static bool _exitHooked;

    public static void Track(Process process)
    {
        if (process is null) return;
        try
        {
            try
            {
                if (process.HasExited) return;
            }
            catch (InvalidOperationException)
            {
                return;
            }

            process.EnableRaisingEvents = true;
            var pid = process.Id;
            Pids[pid] = 0;
            process.Exited += (_, _) => Pids.TryRemove(pid, out _);
            HookProcessExit();
        }
        catch (Exception ex)
        {
            PlayerLog.Write("跟踪子进程失败：" + ex.Message);
        }
    }

    /// <summary>
    /// Keep redirected IO handles alive without killing. Call when another owner
    /// (or app exit) will stop the process later.
    /// </summary>
    public static void KeepAlive(Process process)
    {
        if (process is null) return;
        Kept.Add(process);
    }

    public static void Stop(ref Process? process, int waitMs = ProcessWaitMs)
    {
        var p = process;
        process = null;
        if (p is null) return;
        try
        {
            var pid = 0;
            try { pid = p.Id; } catch { /* handle closed / not started */ }
            if (pid != 0) Pids.TryRemove(pid, out _);
            var alive = false;
            try { alive = !p.HasExited; } catch (InvalidOperationException) { /* not started */ }
            if (alive)
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(waitMs);
            }
        }
        catch
        {
            // ignore
        }
        finally
        {
            try { p.Dispose(); } catch { /* ignore */ }
        }
    }

    /// <summary>Last-resort: kill remaining spawned processes on app exit.</summary>
    public static void KillRemaining()
    {
        foreach (var pid in Pids.Keys)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (!p.HasExited)
                    p.Kill(entireProcessTree: true);
            }
            catch
            {
                // already gone
            }

            Pids.TryRemove(pid, out _);
        }

        while (Kept.TryTake(out var kept))
        {
            try { kept.Dispose(); } catch { /* ignore */ }
        }
    }

    private static void HookProcessExit()
    {
        if (_exitHooked) return;
        _exitHooked = true;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => KillRemaining();
    }
}
