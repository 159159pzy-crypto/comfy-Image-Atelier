using Anima.Launcher.Core;
using Microsoft.UI.Dispatching;
using System.Diagnostics;

namespace Anima.Launcher;

internal sealed partial class LauncherState
{
    [Flags]
    private enum DisplayChange { Downloads = 1, Tasks = 2, Setup = 4, Batch = 8, Progress = 16, ProgressText = 32, Software = 64, Status = 128, Dependency = 256 }
    private readonly PresentationRefreshGate displayGate = new();
    private readonly Stopwatch displayClock = Stopwatch.StartNew();
    private DispatcherQueueTimer displayTimer = null!;
    private DownloadTaskDatabase? downloadView;
    private int displayDispatchPending;
    private int displayedPercent = -1;
    private string displayedStatus = "";
    private bool displayDisposed;
    private volatile bool presentationVisible = true;
    public bool PresentationVisible => presentationVisible;
    public event Action<bool>? PresentationVisibilityChanged;
    // Shared by every presenter in one refresh. Commands must use DownloadTasks instead.
    public DownloadTaskDatabase DownloadView => downloadView ??= DownloadTasks.GetSnapshot();

    private void InitializePresentation()
    {
        displayTimer = downloadUi.CreateTimer();
        displayTimer.IsRepeating = false;
        displayTimer.Tick += (_, _) => FlushPresentation();
        InitializeDependencyPresentation();
    }

    private void RequestPresentation(DisplayChange changes)
    {
        displayGate.Request((int)changes);
        if (displayDisposed || !PresentationVisible || Interlocked.Exchange(ref displayDispatchPending, 1) != 0) return;
        if (!downloadUi.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref displayDispatchPending, 0);
            if (displayDisposed || displayTimer.IsRunning) return;
            SchedulePresentation();
        })) Interlocked.Exchange(ref displayDispatchPending, 0);
    }

    private void SchedulePresentation()
    {
        if (displayDisposed || displayGate.Delay(displayClock.Elapsed) is not { } delay) return;
        displayTimer.Interval = delay > TimeSpan.Zero ? delay : TimeSpan.FromMilliseconds(1);
        displayTimer.Start();
    }

    public void SetPresentationVisible(bool visible)
    {
        if (PresentationVisible == visible || displayDisposed) return;
        presentationVisible = visible;
        displayGate.SetVisible(visible);
        displayTimer.Stop();
        dependencyTimer.Stop();
        if (visible)
        {
            displayGate.Request((int)(DisplayChange.Downloads | DisplayChange.Tasks | DisplayChange.Setup | DisplayChange.Software | DisplayChange.Status | DisplayChange.Progress | DisplayChange.ProgressText | DisplayChange.Batch | DisplayChange.Dependency));
            FlushPresentation(force: true);
        }
        PresentationVisibilityChanged?.Invoke(visible);
    }

    private void FlushPresentation(bool force = false)
    {
        if (displayDisposed) return;
        var changes = (DisplayChange)displayGate.Take(displayClock.Elapsed, force);
        if (changes == 0) return;
        try
        {
            if (changes.HasFlag(DisplayChange.Downloads))
            {
                downloadView = DownloadTasks.GetSnapshot();
                RefreshModelDownloadRows();
                UpdateDurableModelProgress();
                DownloadsChanged?.Invoke();
            }
            if (changes.HasFlag(DisplayChange.Dependency)) FlushDependencyPresentation();
            changes |= (DisplayChange)displayGate.Take(displayClock.Elapsed, force: true, mask: ~(int)(DisplayChange.Downloads | DisplayChange.Dependency));
            if (changes.HasFlag(DisplayChange.Tasks)) TaskStateChanged?.Invoke();
            if (changes.HasFlag(DisplayChange.Setup)) SetupChanged?.Invoke();
            if (changes.HasFlag(DisplayChange.Batch)) BatchProgressChanged?.Invoke();
            if (changes.HasFlag(DisplayChange.Progress)) ProgressChanged?.Invoke(displayedPercent);
            if (changes.HasFlag(DisplayChange.ProgressText)) TaskProgressChanged?.Invoke(ProgressText);
            if (changes.HasFlag(DisplayChange.Software)) SoftwareUpdateChanged?.Invoke();
            if (changes.HasFlag(DisplayChange.Status)) StatusChanged?.Invoke(displayedStatus);
        }
        finally { SchedulePresentation(); }
    }

    private void PublishPercent(int value)
    {
        displayedPercent = value;
        RequestPresentation(DisplayChange.Progress);
    }
    private void NotifyDownloads() => RequestPresentation(DisplayChange.Downloads | DisplayChange.Tasks);
    private void DisposePresentation()
    {
        displayDisposed = true;
        displayTimer.Stop();
        dependencyTimer.Stop();
    }
}
