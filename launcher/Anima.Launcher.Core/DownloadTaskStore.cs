namespace Anima.Launcher.Core;

/// <summary>Atomic task intent storage. Loading never starts engines or contacts a source.</summary>
public sealed class DownloadTaskStore
{
    private readonly object sync = new();
    public string FilePath { get; }
    public string? LoadError { get; private set; }
    public DownloadTaskStore(string stateDirectory) => FilePath = Path.Combine(Path.GetFullPath(stateDirectory), "download-tasks.json");

    public DownloadTaskDatabase Load()
    {
        lock (sync)
        {
            if (!File.Exists(FilePath)) return new();
            try
            {
                var result = JsonFile.Read<DownloadTaskDatabase>(FilePath);
                Validate(result);
                return result;
            }
            catch (Exception ex)
            {
                LoadError = "下载任务记录无法读取，原文件已保留，暂不覆盖：" + ex.Message;
                return new();
            }
        }
    }

    public void Save(DownloadTaskDatabase value)
    {
        lock (sync)
        {
            if (LoadError is not null) throw new IOException(LoadError);
            Validate(value);
            JsonFile.Write(FilePath, value);
        }
    }

    private static void Validate(DownloadTaskDatabase value)
    {
        if (value.SchemaVersion != 1 || value.Tasks is null || value.Owners is null
            || value.Tasks.Count > 10000 || value.Owners.Count > 10000)
            throw new InvalidDataException("不支持的下载任务记录。");
        var ownerIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var owner in value.Owners)
        {
            if (owner is null || !Guid.TryParseExact(owner.Id, "N", out _) || !ownerIds.Add(owner.Id)
                || !Enum.IsDefined(owner.Status) || string.IsNullOrWhiteSpace(owner.Kind)
                || owner.Configuration is null || owner.Models is null || owner.CompletedModelIds is null || owner.ResourceOwnerIds is null)
                throw new InvalidDataException("下载批次记录无效。");
            if (owner.Models.Select(m => m.Model.Id).Distinct(StringComparer.Ordinal).Count() != owner.Models.Length
                || owner.CompletedModelIds.Distinct(StringComparer.Ordinal).Count() != owner.CompletedModelIds.Count
                || owner.CompletedModelIds.Any(id => !owner.Models.Any(m => m.Model.Id == id)))
                throw new InvalidDataException("下载批次的模型身份或完成记录无效。");
            owner.Configuration.Validate();
            DownloadTaskIdentity.ValidatePayload(System.Text.Json.JsonSerializer.SerializeToElement(owner.Configuration, JsonFile.Options));
            if (owner.Payload is { } payload) DownloadTaskIdentity.ValidatePayload(payload);
            foreach (var model in owner.Models) DownloadTaskIdentity.ValidateModel(model);
            DownloadTaskIdentity.ValidatePayload(System.Text.Json.JsonSerializer.SerializeToElement(owner.Models, JsonFile.Options));
            if (owner.Fingerprint != DownloadTaskIdentity.Owner(owner)) throw new InvalidDataException("下载批次固定内容发生变化，请重新确认。");
        }
        foreach (var owner in value.Owners.Where(owner => owner.IsResourceGroup))
        {
            if (owner.Kind != "models" || owner.Models.Length <= 1 || owner.ResourceOwnerIds.Count != owner.Models.Length
                || owner.ResourceOwnerIds.Values.Distinct(StringComparer.Ordinal).Count() != owner.Models.Length)
                throw new InvalidDataException("模型资源批次关联无效。");
            foreach (var intent in owner.Models)
            {
                if (!owner.ResourceOwnerIds.TryGetValue(intent.Model.Id, out var childId))
                    throw new InvalidDataException("模型资源批次缺少子任务关联。");
                var child = value.Owners.SingleOrDefault(item => item.Id == childId);
                var expected = new DownloadOwnerRecord { Kind = "models", EnvironmentRoot = owner.EnvironmentRoot,
                    Configuration = owner.Configuration, Models = [intent], Payload = owner.Payload };
                if (child is null || child.IsResourceGroup || child.Fingerprint != DownloadTaskIdentity.Owner(expected))
                    throw new InvalidDataException("模型资源子任务与原批次的固定内容不一致。");
            }
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in value.Tasks)
        {
            if (task is null || !Guid.TryParseExact(task.Id, "N", out _) || !ids.Add(task.Id)
                || !Enum.IsDefined(task.Status) || task.Attempts < 0 || task.SourceRetryCount < 0
                || task.OwnerIds is null || task.OwnerIds.Any(id => !ownerIds.Contains(id)))
                throw new InvalidDataException("下载文件任务记录无效。");
            DownloadTaskIdentity.Validate(task.Request());
            if (task.Fingerprint != DownloadTaskIdentity.Artifact(task.Request()))
                throw new InvalidDataException("下载任务的目标或内容发生变化，请重新确认。");
        }
    }
}
