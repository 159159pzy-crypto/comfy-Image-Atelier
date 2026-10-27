using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Anima.Launcher.Core;

public enum DownloadTaskStatus { Queued, Running, Pausing, Paused, Failed, Canceled, Completed, NeedsReview }

/// <summary>A frozen model and its actual destination. A package is completed by its owner, never by a child file.</summary>
public sealed record ModelDownloadIntent(ModelItem Model, string Destination, bool Required = true);
public sealed record DownloadCleanupPreview(string TaskId, string[] Paths, long Bytes)
{
    public long LogicalBytes { get; init; }
}

public sealed class DownloadTaskRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Destination { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public string[] Urls { get; set; } = [];
    public NetworkPolicySnapshot? Network { get; set; }
    public bool PreferHttpClient { get; set; }
    public string Fingerprint { get; set; } = "";
    public List<string> OwnerIds { get; set; } = [];
    public bool Required { get; set; } = true;
    public int TransportRevision { get; set; } = 1;
    public DownloadTaskStatus Status { get; set; } = DownloadTaskStatus.Queued;
    public bool CancellationRequested { get; set; }
    public string Phase { get; set; } = "Queued";
    public string AttemptId { get; set; } = "";
    public int Attempts { get; set; }
    public int SourceRetryCount { get; set; }
    public long Received { get; set; }
    public string? Engine { get; set; }
    public string? Source { get; set; }
    public DateTimeOffset? RetryAt { get; set; }
    public double? Speed { get; set; }
    public int? Connections { get; set; }
    public string Error { get; set; } = "";
    public string? FailureCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DownloadRequest Request() => new(Name, Urls.ToArray(), Destination, Size, Sha256) { TaskId = Id, Network = Network, PreferHttpClient = PreferHttpClient };
}

public sealed class DownloadOwnerRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public string EnvironmentRoot { get; set; } = "";
    public LauncherConfig Configuration { get; set; } = new();
    public ModelDownloadIntent[] Models { get; set; } = [];
    public List<string> CompletedModelIds { get; set; } = [];
    // Legacy multi-model owners become durable groups of independently controlled resources.
    // This relationship is state, not part of the frozen artifact fingerprint.
    public Dictionary<string, string> ResourceOwnerIds { get; set; } = new(StringComparer.Ordinal);
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsResourceGroup => ResourceOwnerIds.Count > 0;
    // Data for a known, explicitly registered UI coordinator. The service never executes payload commands.
    public JsonElement? Payload { get; set; }
    public string Fingerprint { get; set; } = "";
    public int TransportRevision { get; set; } = 1;
    public DownloadTaskStatus Status { get; set; } = DownloadTaskStatus.Paused;
    public bool CancellationRequested { get; set; }
    public string Error { get; set; } = "";
    public string Phase { get; set; } = "Paused";
    public string Detail { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class DownloadTaskDatabase
{
    public int SchemaVersion { get; set; } = 1;
    public List<DownloadTaskRecord> Tasks { get; set; } = [];
    public List<DownloadOwnerRecord> Owners { get; set; } = [];
}

internal static class DownloadTaskIdentity
{
    internal static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonFile.Options), JsonFile.Options)!;
    internal static string PathKey(string value) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)).ToUpperInvariant();
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    internal static string Artifact(string path, long size, string sha) => Hash(PathKey(path) + "\n" + size + "\n" + sha.ToLowerInvariant());
    internal static string Owner(DownloadOwnerRecord owner) => Hash(JsonSerializer.Serialize(new
    {
        owner.Kind, Environment = string.IsNullOrWhiteSpace(owner.EnvironmentRoot) ? "" : PathKey(owner.EnvironmentRoot),
        owner.Configuration.Python, owner.Configuration.RuntimeRoot, owner.Configuration.InstallRoot,
        owner.Configuration.SelectedProfile, owner.Configuration.CustomRepository,
        owner.Models, owner.Payload
    }, JsonFile.Options));

    internal static DownloadRequest Validate(DownloadRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Destination)
            || !Path.IsPathFullyQualified(request.Destination) || request.Size <= 0
            || !Regex.IsMatch(request.Sha256 ?? "", "^[A-Fa-f0-9]{64}$"))
            throw new InvalidDataException("下载任务需要确定的名称、绝对目标路径、大小和 SHA-256。");
        var urls = (request.Urls ?? []).Select(StableUrl).Distinct(StringComparer.Ordinal).ToArray();
        if (urls.Length == 0) throw new InvalidDataException("下载任务缺少已验证来源。");
        if (request.Network is { } network) NetworkPolicySnapshot.ValidateSettings(network.Mode, network.ProxyAddress);
        return request with { Destination = Path.GetFullPath(request.Destination), Sha256 = request.Sha256!.ToLowerInvariant(), Urls = urls };
    }

    internal static void ValidateModel(ModelDownloadIntent intent)
    {
        var model = intent.Model ?? throw new InvalidDataException("下载批次缺少模型。");
        if (string.IsNullOrWhiteSpace(intent.Destination) || !Path.IsPathFullyQualified(intent.Destination) || !model.CanDownload)
            throw new InvalidDataException("模型任务目标或固定资源信息无效。");
        if (!model.IsPackage) { Validate(new(model.Name, model.Urls, intent.Destination, model.Size, model.Sha256)); return; }
        Safety.Commit(model.Revision);
        Safety.Under(Path.GetTempPath(), model.Id);
        if (model.Files.Select(f => f.Path.Replace('\\', '/')).Distinct(StringComparer.OrdinalIgnoreCase).Count() != model.Files.Length)
            throw new InvalidDataException("模型包文件重复。");
        foreach (var file in model.Files)
            Validate(new(model.Name, file.Urls, Safety.Under(intent.Destination, file.Path), file.Size, file.Sha256));
    }

    internal static bool Terminal(DownloadTaskStatus status) => status is DownloadTaskStatus.Completed or DownloadTaskStatus.Canceled;

    internal static string StableUrl(string text)
    {
        var url = Safety.Https(text);
        foreach (var field in url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var key = Uri.UnescapeDataString(field.Split('=', 2)[0]);
            if (Regex.IsMatch(key, @"^(x-amz-.+|x-goog-.+|signature|sig|expires|expiry|credentials?|policy|key-pair-id|(?:api|access|private|secret)[_-]?key|(?:access|refresh|id|security|api)[_-]?token|client[_-]?secret|auth|authorization|token|password|passwd|pwd|secret|key|se|sp|sv)$", RegexOptions.IgnoreCase))
                throw new InvalidDataException("下载任务只能保存稳定来源地址，不能持久化签名或鉴权查询参数。");
        }
        return url.AbsoluteUri;
    }

    internal static void ValidatePayload(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            {
                if (Regex.IsMatch(property.Name, @"^(api[_-]?key|password|secret|access[_-]?token|authorization|private[_-]?key)$", RegexOptions.IgnoreCase)
                    && property.Value.ValueKind != JsonValueKind.Null && property.Value.ToString().Length > 0)
                    throw new InvalidDataException("下载任务内容不能包含凭据。");
                ValidatePayload(property.Value);
            }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) ValidatePayload(item);
        else if (value.ValueKind == JsonValueKind.String)
            foreach (Match match in Regex.Matches(value.GetString() ?? "", @"https://[^\s\""<>]+", RegexOptions.IgnoreCase))
                StableUrl(match.Value);
    }
}
