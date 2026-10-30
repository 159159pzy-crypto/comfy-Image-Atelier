using System.Diagnostics;

namespace Anima.Launcher.Core;

public static class GitFetchRecovery
{
    public static Task<string> Run(string git, IEnumerable<string> arguments, string root, Action<string>? log,
        CancellationToken ct, IDictionary<string, string>? environment = null, TimeSpan? timeout = null) =>
        ExecuteAsync(root, () => Commands.Run(git, arguments, root, log, ct, environment, timeout), log);

    public static Task<string> RunTransfer(string git, IEnumerable<string> arguments, string root, Action<string>? log,
        CancellationToken ct, IDictionary<string, string>? environment = null, TimeSpan? timeout = null,
        TimeSpan? idleTimeout = null, TimeSpan? pollInterval = null) =>
        ExecuteTransferAsync(root, token => Commands.Run(git, arguments, root, log, token, environment, timeout), ct, log, idleTimeout, pollInterval);

    /// <summary>Bound silent Git transfers by actual object-file growth, while preserving the existing interrupted-lock contract.</summary>
    public static Task<string> ExecuteTransferAsync(string root, Func<CancellationToken, Task<string>> fetch, CancellationToken ct,
        Action<string>? log = null, TimeSpan? idleTimeout = null, TimeSpan? pollInterval = null, Func<bool>? gitIsActive = null)
    {
        var idle = idleTimeout ?? TimeSpan.FromSeconds(90);
        var poll = pollInterval ?? TimeSpan.FromSeconds(1);
        if (idle <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(idleTimeout));
        if (poll <= TimeSpan.Zero || poll > idle) throw new ArgumentOutOfRangeException(nameof(pollInterval));
        return ExecuteAsync(root, () => TransferAsync(root, fetch, ct, idle, poll), log, gitIsActive);
    }

    private static async Task<string> TransferAsync(string root, Func<CancellationToken, Task<string>> fetch,
        CancellationToken ct, TimeSpan idle, TimeSpan poll)
    {
        ct.ThrowIfCancellationRequested();
        using var transfer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var monitorStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var objectsDirectory = Path.Combine(root, ".git", "objects");
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        ReadObjectGrowth(objectsDirectory, sizes); // Existing objects are the baseline, never fresh transfer progress.
        var elapsed = Stopwatch.StartNew();
        var timedOut = 0;
        async Task Monitor()
        {
            while (true)
            {
                await Task.Delay(poll, monitorStop.Token);
                if (ReadObjectGrowth(objectsDirectory, sizes)) elapsed.Restart();
                if (elapsed.Elapsed < idle) continue;
                Interlocked.Exchange(ref timedOut, 1);
                transfer.Cancel();
                return;
            }
        }
        var monitor = Monitor();
        string TimeoutMessage() => $"Git 传输连续 {idle.TotalSeconds:G} 秒没有新的对象文件字节，已终止并等待下载子任务退出；可切换来源重试。";
        try
        {
            string result;
            try { result = await fetch(transfer.Token); }
            catch (Exception error) when (ct.IsCancellationRequested || Volatile.Read(ref timedOut) != 0)
            {
                // Commands.Run owns process termination. Await it above before exposing a timeout
                // to source fallback or allowing ExecuteAsync to recover this fetch's shallow lock.
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException(TimeoutMessage(), error);
            }
            ct.ThrowIfCancellationRequested();
            if (Volatile.Read(ref timedOut) != 0) throw new TimeoutException(TimeoutMessage());
            return result;
        }
        finally
        {
            monitorStop.Cancel();
            try { await monitor; }
            catch (OperationCanceledException) when (monitorStop.IsCancellationRequested) { }
        }
    }

    private static bool ReadObjectGrowth(string directory, Dictionary<string, long> sizes)
    {
        var grew = false;
        try
        {
            if (!Directory.Exists(directory)) return false;
            foreach (var path in Directory.EnumerateFiles(directory, "*", new EnumerationOptions
                { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true }))
            {
                try
                {
                    var info = new FileInfo(path);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    var length = info.Length;
                    var previous = sizes.GetValueOrDefault(path);
                    if (length > previous) { sizes[path] = length; grew = true; }
                }
                catch (IOException) { } // Git can rename its temporary pack between enumeration and stat.
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return grew;
    }

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
