using Anima.Launcher.Core;

namespace Anima.Launcher;

internal sealed partial class LauncherState
{
    public DownloadContextProgress? InstallationDownloadProgress
    {
        get
        {
            var snapshot = DownloadTasks.GetSnapshot();
            var owner = DownloadContextPresentation.InstallationOwner(snapshot, Config.ComfyRoot);
            return owner is null ? null : DownloadContextPresentation.Progress(snapshot, owner);
        }
    }

    public async Task PauseInstallationDownloadAsync(string ownerId)
    {
        var owner = DownloadTasks.Owners.FirstOrDefault(item => item.Id == ownerId);
        if (owner is null || owner.Kind is not ("install" or "setup")) return;
        // The UI coordinator also owns Git, dependency installation, and unpacking.
        if (Busy && activeDownloadOwner == ownerId) PauseTask();
        await DownloadTasks.PauseOwnerAsync(ownerId);
    }

    private string? softwareRecoveryOwnerId;
    public bool SoftwareDownloadNeedsRetry { get; private set; }

    public string? SoftwareDownloadOwnerId
    {
        get
        {
            var snapshot = DownloadTasks.GetSnapshot();
            var active = snapshot.Owners.Where(owner => owner.Kind == "software"
                    && owner.Status is DownloadTaskStatus.Running or DownloadTaskStatus.Pausing)
                .OrderByDescending(owner => owner.UpdatedAt).FirstOrDefault();
            if (active is not null) return active.Id;
            if (downloadedSoftware is not null)
                return snapshot.Owners.Where(owner => DownloadContextPresentation.SoftwareRelease(owner) is { } release
                        && release.Version == downloadedSoftware.Version && release.Sha256.Equals(downloadedSoftware.Sha256, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(owner => owner.UpdatedAt).FirstOrDefault()?.Id;
            return softwareRecoveryOwnerId ?? snapshot.Owners.Where(owner => owner.Kind == "software")
                .OrderByDescending(owner => owner.UpdatedAt).FirstOrDefault()?.Id;
        }
    }

    private void RefreshSoftwareDownloadPresentation()
    {
        if (!softwareInitialized || DownloadingSoftwareUpdate || CheckingSoftwareUpdate) return;
        if (SoftwareUpdateReady)
        {
            softwareRecoveryOwnerId = null;
            SoftwareDownloadPaused = false;
            SoftwareDownloadNeedsRetry = false;
            SoftwareUpdatePercent = 100;
            NotifySoftwareUpdate();
            return;
        }
        var snapshot = DownloadTasks.GetSnapshot();
        var owner = DownloadContextPresentation.RecoverableSoftwareOwner(snapshot, Program.Version, softwareRelease);
        var release = DownloadContextPresentation.SoftwareRelease(owner);
        var previous = softwareRecoveryOwnerId;
        softwareRecoveryOwnerId = owner?.Id;
        SoftwareDownloadPaused = owner is not null;
        SoftwareDownloadNeedsRetry = owner?.Status is DownloadTaskStatus.Failed or DownloadTaskStatus.NeedsReview;
        if (owner is not null && release is not null)
        {
            softwareRelease = release;
            var progress = DownloadContextPresentation.Progress(snapshot, owner);
            SoftwareUpdatePercent = progress.Percent;
            SoftwareUpdateStatus = SoftwareDownloadNeedsRetry ? "更新下载未完成，可重试。" : "更新下载待继续，已下载内容保留。";
            if (progress.Total > 0) SoftwareUpdateStatus += $" {progress.Received / 1048576d:N1} / {progress.Total / 1048576d:N1} MiB";
            if (progress.Error.Length > 0) SoftwareUpdateStatus += "\n" + progress.Error;
        }
        else if (previous is not null)
        {
            SoftwareUpdatePercent = -1;
            SoftwareUpdateStatus = softwareRelease is { } available
                ? $"发现 {available.Version}。可下载更新；原任务记录保留在下载详情中。"
                : "更新任务已停止。可在下载详情中查看记录，或重新下载更新。";
        }
        NotifySoftwareUpdate();
    }
}
