namespace Anima.Launcher.Core;

public sealed record ModelDownloadSnapshot(string OwnerId, string ModelId, DownloadTaskStatus Status, string Phase,
    long Received, long Total, double Speed, string Error, bool CanPause, bool CanResume, bool CanRetry, string[] TaskIds)
{
    public bool IsCompleted => Status == DownloadTaskStatus.Completed;
    public double Percent => IsCompleted ? 100 : Math.Min(99.9, 100d * Received / Math.Max(1, Total));
}

/// <summary>Pure per-model view over durable tasks; file completion alone never means package publication.</summary>
public static class ModelDownloadProjection
{
    public static ModelDownloadSnapshot? ForModel(string environmentRoot, ModelItem model, string destination,
        IEnumerable<DownloadOwnerRecord> owners, IEnumerable<DownloadTaskRecord> tasks)
    {
        if (string.IsNullOrWhiteSpace(environmentRoot) || string.IsNullOrWhiteSpace(destination)) return null;
        var allOwners = owners.ToArray();
        var byId = allOwners.ToDictionary(owner => owner.Id, StringComparer.Ordinal);
        var candidates = allOwners.Where(owner => owner.Kind == "models" && SamePath(owner.EnvironmentRoot, environmentRoot))
            .SelectMany(owner => owner.Models.Where(intent => SameResource(intent, model, destination)).Select(intent =>
            {
                var effective = owner.ResourceOwnerIds.TryGetValue(model.Id, out var childId) && byId.TryGetValue(childId, out var child)
                    ? child : owner;
                return (Owner: effective, Intent: intent);
            })).DistinctBy(item => item.Owner.Id)
            .OrderByDescending(item => item.Owner.Status is DownloadTaskStatus.Running or DownloadTaskStatus.Pausing or DownloadTaskStatus.Queued)
            .ThenByDescending(item => item.Owner.CreatedAt)
            .ThenBy(item => item.Owner.Models.Length)
            .ThenByDescending(item => item.Owner.Id, StringComparer.Ordinal).ToArray();
        if (candidates.Length == 0) return null;
        var (owner, intent) = candidates[0];
        var files = tasks.Where(task => task.OwnerIds.Contains(owner.Id) && MatchesFile(intent, task))
            .GroupBy(task => DownloadTaskIdentity.Artifact(task.Destination, task.Size, task.Sha256), StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(task => task.UpdatedAt).First()).ToArray();
        var status = owner.Status;
        if (owner.Status == DownloadTaskStatus.Completed || owner.CompletedModelIds.Contains(model.Id)) status = DownloadTaskStatus.Completed;
        else if (owner.CancellationRequested || owner.Status == DownloadTaskStatus.Canceled) status = DownloadTaskStatus.Canceled;
        else if (owner.Models.Length > 1)
        {
            // Unconverted legacy batches may contain completed, canceled and failed resources together.
            if (files.Any(file => file.CancellationRequested || file.Status == DownloadTaskStatus.Canceled)) status = DownloadTaskStatus.Canceled;
            else if (files.Any(file => file.Status == DownloadTaskStatus.NeedsReview)) status = DownloadTaskStatus.NeedsReview;
            else if (files.Any(file => file.Status == DownloadTaskStatus.Failed)) status = DownloadTaskStatus.Failed;
        }
        var running = status is DownloadTaskStatus.Running or DownloadTaskStatus.Queued;
        var received = status == DownloadTaskStatus.Completed ? model.Size : Math.Min(model.Size, files.Sum(file => Math.Clamp(file.Received, 0, file.Size)));
        var speed = running ? files.Where(file => file.Status == DownloadTaskStatus.Running).Sum(file => Math.Max(0, file.Speed ?? 0)) : 0;
        var phase = status == DownloadTaskStatus.Running
            ? files.FirstOrDefault(file => file.Status == DownloadTaskStatus.Running)?.Phase ?? owner.Phase : status.ToString();
        var error = status == DownloadTaskStatus.Completed ? "" : owner.Models.Length == 1 && owner.Error.Length > 0 ? owner.Error
            : files.FirstOrDefault(file => file.Status != DownloadTaskStatus.Completed && file.Error.Length > 0)?.Error ?? owner.Error;
        return new(owner.Id, model.Id, status, phase, received, model.Size, speed, error,
            running, status == DownloadTaskStatus.Paused, status is DownloadTaskStatus.Failed or DownloadTaskStatus.NeedsReview,
            files.Select(file => file.Id).ToArray());
    }

    public static bool MatchesFile(ModelDownloadIntent intent, DownloadTaskRecord task)
    {
        if (!intent.Model.IsPackage)
            return SamePath(intent.Destination, task.Destination) && task.Size == intent.Model.Size
                && string.Equals(task.Sha256, intent.Model.Sha256, StringComparison.OrdinalIgnoreCase);
        var staging = ModelPackages.Staging(intent.Model, intent.Destination);
        return intent.Model.Files.Any(file => SamePath(Safety.Under(staging, file.Path), task.Destination)
            && file.Size == task.Size && string.Equals(file.Sha256, task.Sha256, StringComparison.OrdinalIgnoreCase));
    }

    private static bool SameResource(ModelDownloadIntent intent, ModelItem model, string destination)
    {
        if (intent.Model.Id != model.Id || !SamePath(intent.Destination, destination) || intent.Model.Size != model.Size
            || intent.Model.IsPackage != model.IsPackage) return false;
        if (!model.IsPackage) return string.Equals(intent.Model.Sha256, model.Sha256, StringComparison.OrdinalIgnoreCase);
        return intent.Model.Revision == model.Revision && intent.Model.Files.Length == model.Files.Length
            && model.Files.All(file => intent.Model.Files.Any(original =>
                string.Equals(original.Path.Replace('\\', '/'), file.Path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)
                && original.Size == file.Size && string.Equals(original.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool SamePath(string left, string right) => !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right)
        && string.Equals(DownloadTaskIdentity.PathKey(left), DownloadTaskIdentity.PathKey(right), StringComparison.Ordinal);
}
