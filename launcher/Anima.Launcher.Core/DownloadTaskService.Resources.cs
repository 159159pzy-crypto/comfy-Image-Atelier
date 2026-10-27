namespace Anima.Launcher.Core;

public sealed partial class DownloadTaskService
{
    /// <summary>
    /// Converts a stopped legacy model batch to independent model owners without starting networking.
    /// The complete relationship and existing file associations are committed together, or rolled back.
    /// Returned canceled/completed resources retain their state; callers must not automatically resume them.
    /// </summary>
    public DownloadOwnerRecord[] EnsureResourceOwners(string ownerId)
    {
        DownloadOwnerRecord[] result;
        lock (sync)
        {
            EnsureWritable();
            var parent = Owner(ownerId);
            if (parent.Kind != "models") throw new InvalidOperationException("只有模型下载批次可以按资源拆分。");
            if (parent.IsResourceGroup)
                return parent.Models.Select(model => DownloadTaskIdentity.Copy(Owner(parent.ResourceOwnerIds[model.Model.Id]))).ToArray();
            if (parent.Models.Length <= 1) return [DownloadTaskIdentity.Copy(parent)];
            if (ownerExecutions.ContainsKey(ownerId) || parent.Status is DownloadTaskStatus.Running or DownloadTaskStatus.Pausing)
                throw new InvalidOperationException("请先暂停旧模型批次，再分别管理其中的模型。");

            var before = DownloadTaskIdentity.Copy(database);
            try
            {
                foreach (var intent in parent.Models)
                {
                    var files = database.Tasks.Where(task => task.OwnerIds.Contains(parent.Id)
                        && ModelDownloadProjection.MatchesFile(intent, task)).ToArray();
                    var completed = parent.Status == DownloadTaskStatus.Completed || parent.CompletedModelIds.Contains(intent.Model.Id);
                    var canceled = !completed && (parent.CancellationRequested || parent.Status == DownloadTaskStatus.Canceled
                        || files.Any(file => file.CancellationRequested || file.Status == DownloadTaskStatus.Canceled));
                    var state = completed ? DownloadTaskStatus.Completed : canceled ? DownloadTaskStatus.Canceled
                        : files.Any(file => file.Status == DownloadTaskStatus.NeedsReview) ? DownloadTaskStatus.NeedsReview
                        : files.Any(file => file.Status == DownloadTaskStatus.Failed) ? DownloadTaskStatus.Failed
                        : DownloadTaskStatus.Paused;
                    var child = new DownloadOwnerRecord
                    {
                        Kind = "models", Title = "模型 · " + intent.Model.Name,
                        EnvironmentRoot = parent.EnvironmentRoot,
                        Configuration = DownloadTaskIdentity.Copy(parent.Configuration),
                        Models = [DownloadTaskIdentity.Copy(intent)], Payload = parent.Payload?.Clone(),
                        Status = state, Phase = state.ToString(), CancellationRequested = canceled,
                        CompletedModelIds = completed ? [intent.Model.Id] : [],
                        Error = completed ? "" : canceled ? "此模型已取消，已下载文件保留。"
                            : files.FirstOrDefault(file => file.Status != DownloadTaskStatus.Completed && file.Error.Length > 0)?.Error
                                ?? (files.Length == 0 || files.All(file => file.Status == DownloadTaskStatus.Completed) ? parent.Error : ""),
                        // Migration is not a new user request and must not hide a newer explicit request.
                        CreatedAt = parent.CreatedAt, UpdatedAt = parent.UpdatedAt,
                        TransportRevision = parent.TransportRevision,
                    };
                    child.Fingerprint = DownloadTaskIdentity.Owner(child);
                    // A newer explicit single-model request may already own this content.
                    // Reuse its coordinator; migration must not create a second runnable copy.
                    // Terminal historical intent never borrows an active owner's status.
                    var existing = database.Owners.Where(candidate => !candidate.IsResourceGroup && candidate.Models.Length == 1
                            && candidate.Fingerprint == child.Fingerprint && (completed ? candidate.Status == DownloadTaskStatus.Completed
                                : canceled ? candidate.Status == DownloadTaskStatus.Canceled
                                : candidate.Status != DownloadTaskStatus.Canceled && !candidate.CancellationRequested))
                        .OrderByDescending(candidate => candidate.Status is DownloadTaskStatus.Running or DownloadTaskStatus.Queued or DownloadTaskStatus.Pausing)
                        .ThenByDescending(candidate => candidate.CreatedAt).FirstOrDefault();
                    if (existing is not null) child = existing;
                    else database.Owners.Add(child);
                    parent.ResourceOwnerIds.Add(intent.Model.Id, child.Id);
                    foreach (var file in files)
                        if (!file.OwnerIds.Contains(child.Id)) file.OwnerIds.Add(child.Id);
                }
                Save();
                result = parent.Models.Select(model => DownloadTaskIdentity.Copy(Owner(parent.ResourceOwnerIds[model.Model.Id]))).ToArray();
            }
            catch
            {
                database.Tasks = before.Tasks;
                database.Owners = before.Owners;
                throw;
            }
        }
        Notify();
        return result;
    }

    private void RefreshResourceGroups()
    {
        foreach (var parent in database.Owners.Where(owner => owner.IsResourceGroup))
        {
            var children = parent.ResourceOwnerIds.Values.Select(Owner).ToArray();
            parent.CompletedModelIds = parent.Models.Where(intent =>
            {
                var child = Owner(parent.ResourceOwnerIds[intent.Model.Id]);
                return child.Status == DownloadTaskStatus.Completed || child.CompletedModelIds.Contains(intent.Model.Id);
            }).Select(intent => intent.Model.Id).ToList();
            parent.Status = children.All(child => child.Status == DownloadTaskStatus.Completed) ? DownloadTaskStatus.Completed
                : children.Any(child => child.Status is DownloadTaskStatus.Running or DownloadTaskStatus.Queued) ? DownloadTaskStatus.Running
                : children.Any(child => child.Status == DownloadTaskStatus.Pausing) ? DownloadTaskStatus.Pausing
                : children.Any(child => child.Status == DownloadTaskStatus.NeedsReview) ? DownloadTaskStatus.NeedsReview
                : children.Any(child => child.Status == DownloadTaskStatus.Failed) ? DownloadTaskStatus.Failed
                : children.Any(child => child.Status == DownloadTaskStatus.Paused) ? DownloadTaskStatus.Paused
                : DownloadTaskStatus.Canceled;
            parent.Phase = parent.Status.ToString();
            parent.CancellationRequested = parent.Status == DownloadTaskStatus.Canceled;
            parent.Error = parent.Status == DownloadTaskStatus.Completed ? ""
                : string.Join("\n", children.Where(child => child.Status != DownloadTaskStatus.Completed && child.Error.Length > 0)
                    .Select(child => child.Models[0].Model.Name + "：" + child.Error));
            parent.Detail = $"模型 {parent.CompletedModelIds.Count}/{parent.Models.Length} 项已完成；各模型可分别暂停或继续。";
            parent.UpdatedAt = children.Max(child => child.UpdatedAt);
        }
    }
}
