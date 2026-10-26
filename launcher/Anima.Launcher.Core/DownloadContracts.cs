namespace Anima.Launcher.Core;

public sealed record DownloadRequest(string Name, string[] Urls, string Destination, long Size, string Sha256)
{
    public string? TaskId { get; init; }
    public NetworkPolicySnapshot? Network { get; init; }
    public bool PreferHttpClient { get; init; }
}

public interface IDownloadDispatcher
{
    Task FetchAsync(DownloadRequest request, IProgress<DownloadProgress>? progress, CancellationToken ct);
}

/// <summary>One transfer engine; the facade owns destination locking, source order and the frozen request identity.</summary>
public interface IDownloadTransport
{
    Task FetchAsync(DownloadRequest request, string sourceUrl, IProgress<DownloadProgress>? progress,
        CancellationToken ct, DownloadTransportOptions options);
}

public sealed record DownloadTransportOptions(bool DetectSlow = false, int Retries = 3, bool RestartWithoutRange = false,
    DownloadDiskSpaceReservation? Space = null);

public sealed record DownloadEngineOptions
{
    public DownloadEngineOptions() { }
    public DownloadEngineOptions(string aria2Path) => Aria2Path = aria2Path;
    public string? Aria2Path { get; init; }
    public Func<CancellationToken, Task<string?>>? Aria2PathResolver { get; init; }
    public Func<NetworkPolicySnapshot>? NetworkPolicyResolver { get; init; }
    public DownloadDiskSpaceOptions? DiskSpaceOptions { get; init; }
    public int ConnectionsPerFile { get; init; } = 4;
    public long MinimumAria2Bytes { get; init; } = 64L * 1024 * 1024;
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

internal sealed class DownloadInlineProgress(Action<DownloadProgress> callback) : IProgress<DownloadProgress>
{
    public void Report(DownloadProgress value) => callback(value);
}

public sealed class DownloadSourceException(string message, System.Net.HttpStatusCode status, DateTimeOffset? retryAt = null)
    : HttpRequestException(message, null, status)
{
    public DateTimeOffset? RetryAt { get; } = retryAt;
}

public sealed class DownloadEngineUnavailableException(string message, Exception? inner = null) : IOException(message, inner);
internal sealed class DownloadEngineCrashedException(string message, Exception? inner = null) : IOException(message, inner);
public sealed class DownloadCheckpointException(string message, Exception? inner = null) : IOException(message, inner);
internal sealed class RangeNotSupportedException(string message) : HttpRequestException(message);
