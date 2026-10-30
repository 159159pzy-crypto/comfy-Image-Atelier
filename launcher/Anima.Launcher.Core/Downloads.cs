using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Anima.Launcher.Core;

public sealed record DownloadProgress(string Name, long Received, long Total, string Stage)
{
    public string? TaskId { get; init; }
    public string? Phase { get; init; }
    public string? Engine { get; init; }
    public string? Source { get; init; }
    public DateTimeOffset? RetryAt { get; init; }
    public double? Speed { get; init; }
    public int? Connections { get; init; }
    public int? Attempt { get; init; }
    public string? FailureCode { get; init; }
}
public sealed record ResumeInfo(string Sha256, long Size, string Url, string? ETag);
public sealed record DownloadPolicy
{
    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan SlowWindow { get; init; } = TimeSpan.FromSeconds(20);
    public long MinimumBytesPerSecond { get; init; } = 256 * 1024;
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaximumAutomaticRetryDelay { get; init; } = TimeSpan.FromMinutes(5);
}
public sealed class SlowDownloadException(string message) : TimeoutException(message);

/// <summary>Content identity, transport selection, safe publication and a destination lease shared by all callers.</summary>
public sealed class Downloads : IDisposable
{
    private readonly Func<string, string?> credential;
    private readonly HttpMessageHandler? handler;
    private readonly DownloadPolicy policy;
    private readonly DownloadSourceHealth health;
    public IDownloadDispatcher? Dispatcher { get; set; }
    public DownloadEngineOptions EngineOptions { get; set; }

    public Downloads(Func<string, string?>? credential = null, HttpMessageHandler? handler = null, DownloadPolicy? policy = null,
        DownloadEngineOptions? engineOptions = null)
    {
        this.policy = policy ?? new();
        if (this.policy.ResponseTimeout <= TimeSpan.Zero || this.policy.IdleTimeout <= TimeSpan.Zero
            || this.policy.SlowWindow <= TimeSpan.Zero || this.policy.MinimumBytesPerSecond < 0 || this.policy.RetryDelay < TimeSpan.Zero
            || this.policy.MaximumAutomaticRetryDelay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(policy));
        this.credential = credential ?? (_ => null);
        this.handler = handler;
        health = handler is null ? DownloadSourceHealth.Shared : new();
        EngineOptions = engineOptions ?? new();
    }

    public static async Task<bool> Verify(string file, long size, string sha, CancellationToken ct)
    {
        if (!File.Exists(file) || new FileInfo(file).Length != size) return false;
        await using var stream = File.OpenRead(file);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).Equals(sha, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A persisted lower bound on bytes still needed. Sparse file length is never download progress.</summary>
    public static long RemainingBytes(string destination, long size, string sha) => size <= 0 || File.Exists(destination)
        ? 0 : Math.Max(0, size - DownloadCheckpoint.RetainedBytes(destination, size, sha));

    public Task Fetch(string name, string url, string destination, long size, string sha, IProgress<DownloadProgress>? progress, CancellationToken ct) =>
        Fetch(name, new[] { url }, destination, size, sha, progress, ct);

    public Task FetchDependencyAsync(DependencyPackage package, string destination, LauncherConfig config,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var urls = package.Sources.Length > 0 ? package.Sources : new[] { package.Url };
        var request = Normalize(new(package.Name + " " + package.Version, urls, destination, package.Size, package.Sha256)
        {
            Network = NetworkPolicySnapshot.Capture(config), TransferProfile = DownloadTransferProfile.Dependency,
            Dependency = new(package.Name, package.Version, package.FileName, package.Sha256, package.Size)
        });
        return Dispatcher is { } dispatcher ? dispatcher.FetchAsync(request, progress, ct) : FetchDirectAsync(request, progress, ct);
    }

    public Task Fetch(string name, IEnumerable<string> urls, string destination, long size, string sha,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        // Capture before dispatch so an unowned task cannot silently fall back to system proxy settings.
        var request = Normalize(new(name, urls.ToArray(), destination, size, sha)
        { Network = EngineOptions.NetworkPolicyResolver?.Invoke() ?? NetworkPolicySnapshot.Capture() });
        return Dispatcher is { } dispatcher ? dispatcher.FetchAsync(request, progress, ct) : FetchDirectAsync(request, progress, ct);
    }

    public async Task FetchDirectAsync(DownloadRequest request, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        request = Normalize(request);
        var options = EngineOptions;
        request = request with { Network = request.Network ?? options.NetworkPolicyResolver?.Invoke() ?? NetworkPolicySnapshot.Capture() };
        using var lease = await DownloadTargetLease.AcquireAsync(request.Destination, ct);
        ValidatePartialFiles(request.Destination);
        if (await Verify(request.Destination, request.Size, request.Sha256, ct))
        { progress?.Report(new(request.Name, request.Size, request.Size, "已校验，跳过") { TaskId = request.TaskId, Phase = "Completed" }); return; }
        if (File.Exists(request.Destination)) throw new IOException("已有文件校验不符，请先手动备份后重试：" + request.Destination);
        // An engine may have exited after completing its file but before the launcher committed it.
        if (await Verify(DownloadCheckpoint.AriaData(request.Destination), request.Size, request.Sha256, ct))
        {
            ct.ThrowIfCancellationRequested();
            File.Move(DownloadCheckpoint.AriaData(request.Destination), request.Destination, false);
            DownloadCheckpoint.Cleanup(request.Destination);
            progress?.Report(new(request.Name, request.Size, request.Size, "已恢复校验完成的下载") { TaskId = request.TaskId, Phase = "Completed", Engine = "aria2" });
            return;
        }
        string? aria2 = null;
        // Keep native checkpoints on their owning engine even when a new threshold would select HTTP.
        var useNative = request.Size >= DownloadTransferPolicy.NativeThreshold(request, options)
            || File.Exists(DownloadCheckpoint.AriaData(request.Destination))
            || File.Exists(DownloadCheckpoint.AriaState(request.Destination))
            || File.Exists(DownloadCheckpoint.AriaControl(request.Destination));
        if (!request.PreferHttpClient && useNative)
        {
            try { aria2 = options.Aria2Path ?? (options.Aria2PathResolver is { } resolver ? await resolver(ct) : null); }
            catch (Exception error) when (error is not OperationCanceledException)
            { progress?.Report(new(request.Name, 0, request.Size, "并行引擎不可用，使用兼容下载") { Engine = "HttpClient", Phase = "Connecting", FailureCode = "engine_unavailable", TaskId = request.TaskId }); }
            if (string.IsNullOrWhiteSpace(aria2) || !File.Exists(aria2)) aria2 = null;
        }
        if (aria2 is null && !request.PreferHttpClient)
            await DownloadCheckpoint.RestoreLegacyBeforeFallbackAsync(request, ct);
        await using var space = await DownloadDiskSpace.AcquireAsync(request.Destination, request.Size,
            ReusablePart(request, aria2 is not null), ct, options.DiskSpaceOptions);
        string Key(string url) => health.Key(url, credential, request.Network!);
        request = request with { Urls = health.Order(request.Urls, Key) };
        using var source = new SafeHttpSourceClient(credential, policy, request.Network!, handler, health);
        IDownloadTransport compatible = new HttpClientTransport(source, policy);
        IDownloadTransport? native = aria2 is null ? null : new Aria2Transport(source, policy, options, aria2);
        for (var index = 0; index < request.Urls.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            var url = request.Urls[index];
            var label = DownloadSources.Label(url);
            var sourceProgress = new DownloadInlineProgress(value => progress?.Report(value with
            { Stage = value.Stage + " · " + label, Source = label, TaskId = request.TaskId }));
            var last = index == request.Urls.Length - 1;
            try
            {
                if (health.Cooldown(Key(url)) is { } retryAt)
                    sourceProgress.Report(new(request.Name, DownloadCheckpoint.RetainedBytes(request.Destination, request.Size, request.Sha256), request.Size,
                        "等待来源冷却后继续") { Phase = "RetryWaiting", RetryAt = retryAt });
                sourceProgress.Report(new(request.Name, DownloadCheckpoint.RetainedBytes(request.Destination, request.Size, request.Sha256), request.Size, "连接来源") { Phase = "Connecting" });
                if (native is not null)
                {
                    try
                    {
                        await native.FetchAsync(request, url, sourceProgress, ct, new(!last, last ? 3 : 0, Space: space));
                        health.Succeeded(Key(url));
                        return;
                    }
                    catch (Exception error) when (error is DownloadSourceException { StatusCode: System.Net.HttpStatusCode.RequestedRangeNotSatisfiable }
                        || last && error is DownloadSourceRepresentationChangedException)
                    {
                        // Exactly one independent full GET. Sparse progress remains untouched and cannot become a HTTP prefix.
                        var changedRepresentation = error is DownloadSourceRepresentationChangedException;
                        sourceProgress.Report(new(request.Name, 0, request.Size, changedRepresentation
                            ? "来源分段标识不一致，保留原断点并尝试一次完整下载"
                            : "服务器拒绝并行断点，保留原断点并尝试一次完整下载")
                        { Engine = "HttpClient", Phase = "Connecting", FailureCode = changedRepresentation ? "source_representation_changed" : "range_rejected" });
                        await compatible.FetchAsync(request, url, sourceProgress, ct, new(!last, 0, RestartWithoutRange: true, Space: space));
                        health.Succeeded(Key(url));
                        // The independent file has passed the original size/SHA and was published under our target lease.
                        DownloadCheckpoint.Cleanup(request.Destination);
                        return;
                    }
                    catch (Exception error) when (error is DownloadEngineUnavailableException or RangeNotSupportedException)
                    {
                        if (error is RangeNotSupportedException && !last && File.Exists(DownloadCheckpoint.AriaData(request.Destination)))
                            throw;
                        await DownloadCheckpoint.RestoreLegacyBeforeFallbackAsync(request, ct);
                        sourceProgress.Report(new(request.Name, 0, request.Size, error is RangeNotSupportedException
                            ? "来源不支持并行分段，使用兼容下载" : "并行引擎不可用，使用兼容下载") { Phase = "Connecting", Engine = "HttpClient" });
                    }
                }
                await compatible.FetchAsync(request, url, sourceProgress, ct, new(!last, last ? 3 : 0, Space: space));
                health.Succeeded(Key(url));
                if (!request.PreferHttpClient && !File.Exists(DownloadCheckpoint.AriaData(request.Destination)))
                    DownloadCheckpoint.Cleanup(request.Destination, includeLegacy: false);
                return;
            }
            catch (Exception error) when (!ct.IsCancellationRequested && !last && IsSourceFailure(error))
            {
                if (error is not HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests }) health.Failure(Key(url));
                var reason = error switch
                {
                    SlowDownloadException => "持续低速",
                    TimeoutException => "连接超时",
                    DownloadSourceRepresentationChangedException => "来源分段标识不一致",
                    InvalidDataException => "内容校验失败",
                    HttpRequestException { StatusCode: { } status } => "HTTP " + (int)status,
                    _ => "连接中断"
                };
                progress?.Report(new(request.Name, DownloadCheckpoint.RetainedBytes(request.Destination, request.Size, request.Sha256), request.Size,
                    label + " " + reason + "，切换至 " + DownloadSources.Label(request.Urls[index + 1]))
                { TaskId = request.TaskId, Phase = "Connecting", Source = label, FailureCode = reason });
            }
        }
    }

    private static bool IsSourceFailure(Exception error) => error is HttpRequestException or TimeoutException or InvalidDataException
        or DownloadSourceRepresentationChangedException;
    private static string? ReusablePart(DownloadRequest request, bool native)
    {
        try
        {
            if (native && File.Exists(DownloadCheckpoint.AriaData(request.Destination)))
                return DownloadCheckpoint.Read(request.Destination, request.Size, request.Sha256) is not null
                    ? DownloadCheckpoint.AriaData(request.Destination) : null;
            var part = request.Destination + ".part";
            if (!File.Exists(part) || !File.Exists(part + ".json")) return null;
            var identity = JsonFile.Read<ResumeInfo>(part + ".json");
            return identity.Size == request.Size && identity.Sha256.Equals(request.Sha256, StringComparison.OrdinalIgnoreCase)
                && new FileInfo(part).Length <= request.Size ? part : null;
        }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { return null; }
    }
    private static DownloadRequest Normalize(DownloadRequest request)
    {
        if (request.Size <= 0 || request.Sha256 is null || !Regex.IsMatch(request.Sha256, "^[a-fA-F0-9]{64}$"))
            throw new InvalidDataException("下载需要确定的大小和 SHA-256。");
        DownloadTaskIdentity.ValidateDependency(request);
        // Both standalone and dispatched downloads persist resume metadata, so share the same stable-URL boundary.
        var urls = request.Urls.Select(DownloadTaskIdentity.StableUrl).Distinct(StringComparer.Ordinal).ToArray();
        if (urls.Length == 0) throw new InvalidDataException("没有可用的下载来源。");
        if (string.IsNullOrWhiteSpace(request.Destination)) throw new InvalidDataException("下载目标路径为空。");
        return request with { Urls = urls, Destination = Path.GetFullPath(request.Destination) };
    }

    public static IReadOnlyList<string> PartialFiles(string destination) => new[]
    {
        destination + ".part", destination + ".part.json", destination + ".part.json.tmp",
        destination + ".http-restart.part", destination + ".http-restart.part.json", destination + ".http-restart.part.json.tmp",
        DownloadCheckpoint.AriaData(destination), DownloadCheckpoint.AriaControl(destination), DownloadCheckpoint.AriaControl(destination) + "__temp",
        DownloadCheckpoint.AriaState(destination), DownloadCheckpoint.AriaState(destination) + ".tmp"
    };
    public static async Task CancelPartialAsync(string destination, CancellationToken ct = default)
    {
        using var lease = await DownloadTargetLease.AcquireAsync(destination, ct);
        DownloadCheckpoint.Cleanup(destination);
    }

    /// <summary>Task-scoped cleanup rechecks content identity after acquiring the cross-process destination lease.</summary>
    public static async Task CancelPartialAsync(string destination, long size, string sha, bool allowUnidentified,
        CancellationToken ct = default)
    {
        using var lease = await DownloadTargetLease.AcquireAsync(destination, ct);
        ValidatePartialOwnership(destination, size, sha, allowUnidentified);
        DownloadCheckpoint.Cleanup(destination);
    }

    internal static void ValidatePartialOwnership(string destination, long size, string sha, bool allowUnidentified)
    {
        ValidatePartialFiles(destination);
        foreach (var (metadata, paths) in new[]
        {
            (destination + ".part.json", new[] { destination + ".part", destination + ".part.json", destination + ".part.json.tmp" }),
            (destination + ".http-restart.part.json", new[] { destination + ".http-restart.part", destination + ".http-restart.part.json", destination + ".http-restart.part.json.tmp" }),
            (DownloadCheckpoint.AriaState(destination), new[] { DownloadCheckpoint.AriaData(destination), DownloadCheckpoint.AriaControl(destination),
                DownloadCheckpoint.AriaControl(destination) + "__temp", DownloadCheckpoint.AriaState(destination), DownloadCheckpoint.AriaState(destination) + ".tmp" })
        })
        {
            if (!paths.Any(File.Exists)) continue;
            PartialIdentity? identity = null;
            try { if (File.Exists(metadata)) identity = JsonFile.Read<PartialIdentity>(metadata); }
            catch (System.Text.Json.JsonException) { }
            if (identity is { Size: > 0 } && identity.Sha256 is { Length: 64 })
            {
                if (identity.Size != size || !string.Equals(identity.Sha256, sha, StringComparison.OrdinalIgnoreCase))
                    throw new DownloadCheckpointException("目标当前断点属于另一个内容版本，已拒绝从此任务清理；其他任务的文件保持不变。");
            }
            else if (!allowUnidentified)
                throw new DownloadCheckpointException("同一目标存在多个历史内容版本，当前断点缺少可确认身份，已保留文件供检查。");
        }
    }

    private sealed record PartialIdentity(long Size, string Sha256);
    private static void ValidatePartialFiles(string destination)
    {
        foreach (var path in PartialFiles(destination))
        {
            try
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                    throw new IOException("下载暂存路径不是普通文件，已停止以保留现有内容：" + path);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    public static void CancelPartial(string destination) => CancelPartialAsync(destination).GetAwaiter().GetResult();
    public void Dispose() => handler?.Dispose();
}
