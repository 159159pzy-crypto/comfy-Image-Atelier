using System.Diagnostics;

namespace Anima.Launcher.Core;

public static class GitFetchRecovery
{
    public static Task<string> Run(string git, IEnumerable<string> arguments, string root, Action<string>? log,
        CancellationToken ct, IDictionary<string, string>? environment = null, TimeSpan? timeout = null) =>
        ExecuteAsync(root, () => Commands.Run(git, arguments, root, log, ct, environment, timeout), log);

    /// <summary>Only preserves a newly created shallow lock from this interrupted fetch. Existing locks remain untouched.</summary>
    public static async Task<string> ExecuteAsync(string root, Func<Task<string>> fetch, Action<string>? log = null,
        Func<bool>? gitIsActive = null)
    {
        var path = Path.Combine(root, ".git", "shallow.lock");
        if (File.Exists(path)) throw new IOException("Git 浅克隆锁已存在，未改动：" + path + "。请先确认其他 Git 操作已结束，再检查该锁；启动器不会移动预先存在的锁。");
        var started = DateTime.UtcNow;
        try { return await fetch(); }
        catch (Exception failure) when (failure is TimeoutException or OperationCanceledException)
        {
            var ended = DateTime.UtcNow;
            if (!File.Exists(path)) throw;
            var active = gitIsActive ?? AnyGitActive;
            try
            {
                // A Windows Git wrapper can leave a remote helper after its parent
                // exits. Any surviving Git process makes recovery conservative.
                if (active()) throw new IOException("仍有 Git 进程运行");
                var created = File.GetCreationTimeUtc(path);
                if (created < started || created > ended || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("无法确认锁由本次下载创建");
                using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    if (active() || File.GetCreationTimeUtc(path) != created)
                        throw new IOException("锁或 Git 进程状态已变化");
                }
                if (active() || File.GetCreationTimeUtc(path) != created)
                    throw new IOException("锁或 Git 进程状态已变化");
                var saved = path + ".anima-interrupted-" + Guid.NewGuid().ToString("N");
                File.Move(path, saved);
                log?.Invoke("已保留本次中断下载的 Git 锁，可重试：" + saved);
            }
            catch (Exception recovery) when (recovery is IOException or UnauthorizedAccessException)
            {
                throw new IOException("Git 下载中断，浅克隆锁已保留：" + path + "。请关闭相关 Git 进程并检查锁后重试。原因：" + recovery.Message, failure);
            }
            throw;
        }
    }

    private static bool AnyGitActive()
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var name = process.ProcessName;
                    if (name.Equals("git", StringComparison.OrdinalIgnoreCase) || name.StartsWith("git-", StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { return true; }
            }
        }
        return false;
    }
}
