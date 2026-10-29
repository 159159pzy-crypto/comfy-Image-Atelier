using System.Diagnostics;
using System.Text.Json;

namespace Anima.Launcher.Core;

/// <summary>Runs before WinUI in a separate copy of the single EXE. Never overwrites a running binary.</summary>
public sealed class LauncherUpdateCoordinator(LauncherUpdates updates, Action<string>? logger = null)
{
    private readonly Action<string> log = text => { try { logger?.Invoke(text); } catch { } };
    private LauncherUpdateStore Store => updates.Store;
    public static string ExitEventName(UpdateTransaction transaction) => "Local\\AnimaLauncherUpdateExit-" + transaction.Id + "-" + transaction.Token[..16];

    // Windows mutex ownership is thread-affine: keep acquisition/release on this synchronous thread.
    public int Run(string id, string token, bool recoverOnly = false)
    {
        using var cacheUse = LauncherCache.AcquireUseAsync(Store.StateDirectory).GetAwaiter().GetResult();
        using var mutex = new Mutex(false, Store.MutexName);
        bool acquired;
        try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) return 2;
        try { return RunAsync(id, token, recoverOnly).GetAwaiter().GetResult(); }
        catch (Exception ex) { log("更新协调器失败：" + ex.Message); return 1; }
        finally { mutex.ReleaseMutex(); }
    }
    private async Task<int> RunAsync(string id, string token, bool recoverOnly)
    {
        var transaction = Store.ReadTransaction(id, token);
        var state = Store.Load();
        if (state.Active?.Sha256 == transaction.Candidate.Sha256 && state.Active.Version == transaction.Candidate.Version)
        {
            transaction.Phase = "committed"; Store.WriteTransaction(transaction);
            state.PendingTransactionId = null; Store.Save(state); return 0;
        }
        if (state.PendingTransactionId != transaction.Id) throw new InvalidDataException("此更新事务已失效。");
        transaction.Coordinator = LauncherUpdateStore.Identity(Process.GetCurrentProcess());
        Store.WriteTransaction(transaction);
        if (recoverOnly) return await Rollback(transaction, "上一次更新中断，已进入恢复流程。");
        var committed = false;
        try
        {
            await WaitExit(transaction.Parent, TimeSpan.FromSeconds(60));
            if (!await Downloads.Verify(transaction.Candidate.Executable, transaction.Candidate.Size, transaction.Candidate.Sha256, default))
                throw new InvalidDataException("待启动的新版本文件校验失败。");
            var receipt = Store.ReceiptPath(id);
            if (File.Exists(receipt)) File.Delete(receipt);
            using var child = Process.Start(updates.ProcessInfo(transaction.Candidate.Executable, "--update-candidate", transaction))
                ?? throw new IOException("无法打开新版本启动器。");
            transaction.CandidateProcess = LauncherUpdateStore.Identity(child);
            transaction.Phase = "candidate"; Store.WriteTransaction(transaction);
            var ready = await WaitReady(transaction, child, TimeSpan.FromMinutes(8));
            if (!ready.Success) throw new IOException(ready.Error ?? "新版本初始化失败。");
            var config = JsonFile.Read<LauncherConfig>(Path.Combine(Store.StateDirectory, "launcher.json"));
            config.Validate();
            if (config.AppRoot != ready.AppRoot || config.PinnedAppRoot.Length != 0) throw new IOException("新版本尚未完成工作台切换。");
            var versions = Path.GetFullPath(Path.Combine(Store.StateDirectory, "versions")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(config.AppRoot).StartsWith(versions, StringComparison.OrdinalIgnoreCase)) throw new IOException("新工作台目录不属于启动器快照。");
            if (transaction.WorkbenchWasRunning)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var health = await ServiceManager.Json(config.WebUrl + "/api/launcher-health", deadline.Token);
                if (!ServiceManager.IsOurWebUi(health, config)) throw new IOException("新工作台健康验证未通过。");
            }
            state = Store.Load();
            state.Previous = transaction.Previous; state.Active = transaction.Candidate; state.PendingTransactionId = null;
            state.Failed.RemoveAll(x => x.Version == transaction.Candidate.Version && x.Sha256 == transaction.Candidate.Sha256);
            Store.Save(state); // Commit point: normal entries can now select the new EXE.
            committed = true;
            transaction.Phase = "committed"; Store.WriteTransaction(transaction);
            log("启动器和工作台更新成功：" + transaction.Candidate.Version);
            return 0;
        }
        catch (Exception ex)
        {
            if (committed) { log("更新已提交，完成记录稍后恢复：" + ex.Message); return 0; }
            return await Rollback(transaction, ex.Message);
        }
    }
    private async Task<int> Rollback(UpdateTransaction transaction, string reason)
    {
        transaction.Phase = "rolling-back"; transaction.Error = reason; Store.WriteTransaction(transaction);
        try
        {
            if (LauncherUpdateStore.IsRunning(transaction.CandidateProcess))
            {
                if (OperatingSystem.IsWindows() && EventWaitHandle.TryOpenExisting(ExitEventName(transaction), out var exit)) { using (exit) exit.Set(); }
                await WaitExit(transaction.CandidateProcess, TimeSpan.FromSeconds(30));
            }
            // A candidate may have started WebUI before its GUI failed. Never restore the
            // configuration while that service is busy or cannot be safely identified.
            var configPath = Path.Combine(Store.StateDirectory, "launcher.json");
            if (File.Exists(configPath))
            {
                var current = JsonFile.Read<LauncherConfig>(configPath);
                if (await ServiceManager.PortOpen(current.WebPort))
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    var health = await ServiceManager.Json(current.WebUrl + "/api/launcher-health", deadline.Token);
                    var live = current.Clone();
                    if (!ServiceManager.IsWorkbenchHealth(health) || !health!.Value.TryGetProperty("app_dir", out var liveRoot)
                        || liveRoot.ValueKind != JsonValueKind.String) throw new IOException("不能确认运行中的工作台归属，保持现状。");
                    live.AppRoot = liveRoot.GetString()!;
                    if (!ServiceManager.IsOurWebUi(health, live)) throw new IOException("工作台端口属于其他环境，保持现状。");
                    if (!LauncherUpdateStore.SamePath(live.AppRoot, transaction.PreviousConfig.AppRoot))
                    {
                    var services = new ServiceManager(Store.StateDirectory, log); services.RecoverOwned();
                    var stopped = await services.TryStopWebUiWhenIdleAsync(live, Path.Combine(Store.StateDirectory, "versions"));
                    if (stopped.ToString() != "Stopped") throw new IOException("新版工作台尚不能安全停止，暂不覆盖配置；请等待任务结束后重新打开启动器。");
                    }
                }
            }
            if (transaction.PreviousConfigText is not null)
            {
                var temp = configPath + ".update-restore.tmp"; File.WriteAllText(temp, transaction.PreviousConfigText); File.Move(temp, configPath, true);
            }
            else if (File.Exists(configPath)) File.Delete(configPath);
            var state = Store.Load();
            state.Active = transaction.Previous; state.PendingTransactionId = null;
            state.Failed.RemoveAll(x => x.Version == transaction.Candidate.Version && x.Sha256 == transaction.Candidate.Sha256);
            state.Failed.Add(new(transaction.Candidate.Version, transaction.Candidate.Sha256, reason, DateTimeOffset.UtcNow));
            if (state.Failed.Count > 20) state.Failed.RemoveRange(0, state.Failed.Count - 20);
            Store.Save(state);
            transaction.Phase = "failed"; Store.WriteTransaction(transaction);
            if (!LauncherUpdateStore.IsRunning(transaction.Parent))
            {
                var launch = updates.ProcessInfo(transaction.Previous.Executable, "--recover-update", transaction);
                if (transaction.PreviousConfigText is null && !transaction.WorkbenchWasRunning)
                {
                    // A never-configured launcher should recreate its own defaults,
                    // not persist an empty synthetic snapshot from the handoff.
                    launch.ArgumentList.Clear(); launch.ArgumentList.Add("--show");
                }
                using var previous = Process.Start(launch);
                if (previous is null) throw new IOException("配置已恢复，但旧启动器未能重新打开。");
            }
            log("更新未完成，已保留原版本：" + reason);
        }
        catch (Exception ex)
        {
            transaction.Phase = "recovery-required";
            transaction.Error = reason + "\n" + ex.Message;
            Store.WriteTransaction(transaction);
            log("更新恢复需要重试：" + transaction.Error);
        }
        return 1;
    }
    private async Task<UpdateReady> WaitReady(UpdateTransaction transaction, Process child, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (child.HasExited) throw new IOException("新启动器在完成验证前退出。");
            var path = Store.ReceiptPath(transaction.Id);
            if (File.Exists(path))
            {
                var ready = JsonFile.Read<UpdateReady>(path);
                if (ready.Token != transaction.Token || ready.Pid != child.Id) throw new InvalidDataException("新启动器确认消息身份无效。");
                return ready;
            }
            await Task.Delay(150);
        }
        throw new TimeoutException("新启动器未能在规定时间内完成窗口与工作台验证。");
    }
    private static async Task WaitExit(OwnedProcess? identity, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (LauncherUpdateStore.IsRunning(identity))
        {
            if (clock.Elapsed > timeout) throw new IOException("启动器进程仍在运行，保持当前文件和配置。");
            await Task.Delay(100);
        }
    }
}
