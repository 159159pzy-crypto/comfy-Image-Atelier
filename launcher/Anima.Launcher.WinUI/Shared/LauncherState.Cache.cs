using Anima.Launcher.Core;
using Microsoft.UI.Xaml;

namespace Anima.Launcher;

internal sealed partial class LauncherState
{
    private readonly CancellationTokenSource cacheLifetime = new();
    private readonly DispatcherTimer cacheTimer = new();
    private Task? cacheMaintenance;
    private bool cacheScheduled;

    public void ScheduleCacheMaintenance()
    {
        if (cacheLifetime.IsCancellationRequested) return;
        if (!cacheScheduled)
        {
            cacheTimer.Tick += async (_, _) => await MaintainCacheAsync();
            cacheScheduled = true;
        }
        cacheTimer.Stop();
        cacheTimer.Interval = TimeSpan.FromSeconds(30);
        cacheTimer.Start();
    }

    private async Task MaintainCacheAsync()
    {
        cacheTimer.Stop();
        if (cacheLifetime.IsCancellationRequested) return;
        // Recheck on the UI thread before handing an immutable context to the worker.
        var busy = Busy || HasActiveDownloads || PreparingModelDownloads || CheckingSoftwareUpdate
            || DownloadingSoftwareUpdate || SoftwareUpdateExitPending || shuttingDown;
        try
        {
            if (!busy && cacheMaintenance is not { IsCompleted: false })
            {
                var context = new LauncherCacheContext(Program.Assets, Program.AppRoot, Environment.ProcessPath!, Program.Version);
                cacheMaintenance = Task.Run(() => new LauncherCache(Program.StateDir, Program.Log).MaintainAsync(context, cacheLifetime.Token));
                await cacheMaintenance;
            }
        }
        catch (OperationCanceledException) when (cacheLifetime.IsCancellationRequested) { }
        catch (Exception error) { Program.Log("缓存维护已跳过：" + error.Message); }
        finally
        {
            if (!cacheLifetime.IsCancellationRequested)
            {
                // Core enforces the durable 24-hour limit; transient busy/lock conditions retry later.
                cacheTimer.Interval = busy ? TimeSpan.FromMinutes(1) : TimeSpan.FromHours(1);
                cacheTimer.Start();
            }
        }
    }

    private void DisposeCacheMaintenance()
    {
        cacheTimer.Stop();
        cacheLifetime.Cancel();
        // A worker can still be completing cancellation; do not dispose its source underneath it.
    }
}
