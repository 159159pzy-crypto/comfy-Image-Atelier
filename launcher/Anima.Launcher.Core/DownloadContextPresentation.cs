using System.Text.Json;

namespace Anima.Launcher.Core;

public sealed record DownloadContextProgress(string OwnerId, string Title, DownloadTaskStatus Status,
    string Detail, string Error, long Received, long Total, double Speed, double Percent, bool Indeterminate);

/// <summary>Read-only projections for the original installation and software-update pages.</summary>
public static class DownloadContextPresentation
{
    public static DownloadOwnerRecord? InstallationOwner(DownloadTaskDatabase snapshot, string environmentRoot) =>
        snapshot.Owners.Where(owner => (owner.Kind is "install" or "setup") && SamePath(owner.EnvironmentRoot, environmentRoot))
            .OrderByDescending(owner => owner.Status is DownloadTaskStatus.Running or DownloadTaskStatus.Pausing)
            .ThenByDescending(owner => owner.UpdatedAt).FirstOrDefault();

    public static DownloadOwnerRecord? RecoverableSoftwareOwner(DownloadTaskDatabase snapshot, string currentVersion, LauncherRelease? availableRelease = null)
    {
        var canceled = snapshot.Tasks.Where(task => task.Status == DownloadTaskStatus.Canceled || task.CancellationRequested)
            .SelectMany(task => task.OwnerIds).ToHashSet();
        var owner = DownloadIntegrationPolicy.LatestSoftwareOwner(snapshot.Owners.Where(owner => !canceled.Contains(owner.Id)), currentVersion);
        if (SoftwareRelease(owner) is { } recovery && availableRelease is not null)
        {
            // A successful release check remains authoritative for a newer version or changed artifact.
            if (DownloadIntegrationPolicy.IsNewerSoftwareVersion(availableRelease.Version, recovery.Version)
                || availableRelease.Version == recovery.Version && !availableRelease.Sha256.Equals(recovery.Sha256, StringComparison.OrdinalIgnoreCase))
                return null;
        }
        return owner;
    }

    public static LauncherRelease? SoftwareRelease(DownloadOwnerRecord? owner)
    {
        if (owner?.Kind != "software") return null;
        try
        {
            var release = owner.Payload?.Deserialize<LauncherRelease>(JsonFile.Options);
            if (release is null) return null;
            LauncherUpdates.ValidateRelease(release);
            return release;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException or InvalidOperationException or NullReferenceException or KeyNotFoundException)
        { return null; }
    }

    public static DownloadContextProgress Progress(DownloadTaskDatabase snapshot, DownloadOwnerRecord owner)
    {
        var files = snapshot.Tasks.Where(task => task.OwnerIds.Contains(owner.Id)).ToArray();
        var total = files.Sum(file => Math.Max(0, file.Size));
        var received = files.Sum(file => Math.Clamp(file.Received, 0, Math.Max(0, file.Size)));
        var speed = files.Where(file => file.Status == DownloadTaskStatus.Running).Sum(file => Math.Max(0, file.Speed ?? 0));
        var working = owner.Status is DownloadTaskStatus.Running or DownloadTaskStatus.Pausing;
        // File completion is not package publication or installation completion.
        var indeterminate = working && (total == 0 || files.All(file => file.Status == DownloadTaskStatus.Completed));
        var percent = owner.Status == DownloadTaskStatus.Completed ? 100 : total > 0 ? Math.Min(99.9, 100d * received / total) : 0;
        var error = owner.Error.Length > 0 ? owner.Error : files.FirstOrDefault(file => file.Error.Length > 0)?.Error ?? "";
        return new(owner.Id, owner.Title, owner.Status, owner.Detail, error, received, total, speed, percent, indeterminate);
    }

    private static bool SamePath(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}
