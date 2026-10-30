using Anima.Launcher.Core;
using Microsoft.UI.Dispatching;

namespace Anima.Launcher;

internal sealed partial class LauncherState
{
    private readonly DependencyProgressSession dependencySession = new();
    private DispatcherQueueTimer dependencyTimer = null!;
    private bool dependencyOwnsGlobalStatus;
    public DependencyProgressPresentation? DependencyProgressView { get; private set; }
    public DependencyProgress? DependencyActivity => dependencySession.Current;
    public event Action? DependencyProgressChanged;

    private Installer CreateInstaller(RuntimeManifest runtime) => new(Program.StateDir, runtime, Downloads,
        AppendLog, DownloadProgress(), InstallStageChanged, UpdateSetupProgress, DependencyProgressObserver());

    private Action<DependencyProgress> DependencyProgressObserver()
    {
        var generation = dependencySession.Generation;
        return progress =>
        {
            if (dependencySession.TryReport(generation, progress)) RequestPresentation(DisplayChange.Dependency);
        };
    }

    private void InitializeDependencyPresentation()
    {
        dependencyTimer = downloadUi.CreateTimer();
        dependencyTimer.Interval = TimeSpan.FromSeconds(1);
        dependencyTimer.IsRepeating = true;
        dependencyTimer.Tick += (_, _) => RequestPresentation(DisplayChange.Dependency);
    }

    private void ClearDependencyProgress()
    {
        dependencySession.Clear();
        RequestPresentation(DisplayChange.Dependency);
    }

    private void FlushDependencyPresentation()
    {
        var activity = dependencySession.Current;
        DependencyProgressView = activity is null ? null : DependencyProgressPresentation.Create(activity, DateTimeOffset.UtcNow);
        var running = Busy && activity?.Status == DependencyProgressStatus.Running;
        if (PresentationVisible && running)
        {
            if (!dependencyTimer.IsRunning) dependencyTimer.Start();
        }
        else dependencyTimer.Stop();
        if ((running || dependencyOwnsGlobalStatus && activity is not null) && DependencyProgressView is { } view)
        {
            SetStatus(view.Heading);
            dependencyOwnsGlobalStatus = true;
            ProgressText = view.Detail + (view.QuietNotice.Length > 0 ? "\n" + view.QuietNotice : "");
            SetupDownloadPercent = view.Indeterminate ? -1 : view.Percent;
            PublishPercent(SetupDownloadPercent);
            RequestPresentation(DisplayChange.ProgressText);
        }
        DependencyProgressChanged?.Invoke();
        if (SetupActive) NotifySetup();
    }
}
