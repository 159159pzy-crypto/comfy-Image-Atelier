namespace Anima.Launcher.Core;

public enum DownloadTransferProfile { Standard, Dependency }

/// <summary>The package coordinate is frozen with its content; changing sources cannot select a different wheel.</summary>
public sealed record DependencyDownloadIdentity(string Name, string Version, string FileName, string Sha256, long Size);

public sealed record DownloadRequest(string Name, string[] Urls, string Destination, long Size, string Sha256)
{
    public string? TaskId { get; init; }
    public NetworkPolicySnapshot? Network { get; init; }
    public bool PreferHttpClient { get; init; }
    public DownloadTransferProfile TransferProfile { get; init; }
    public DependencyDownloadIdentity? Dependency { get; init; }
}

public static class DownloadTransferPolicy
{
    public static long NativeThreshold(DownloadRequest request, DownloadEngineOptions options) =>
        request.TransferProfile == DownloadTransferProfile.Dependency ? 16L * 1024 * 1024 : options.MinimumAria2Bytes;

    public static int Connections(DownloadRequest request, DownloadEngineOptions options) =>
        request.Size < (request.TransferProfile == DownloadTransferProfile.Dependency ? 16L : 64L) * 1024 * 1024
            ? 1 : Math.Clamp(options.ConnectionsPerFile, 1, 4);

    public static string MinimumSplitSize(DownloadRequest request) =>
        request.TransferProfile == DownloadTransferProfile.Dependency ? "4M" : "32M";
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
/// <summary>A source returned different strong validators for concurrent ranges of one locked artifact.</summary>
public sealed class DownloadSourceRepresentationChangedException(string message) : IOException(message);
internal sealed class RangeNotSupportedException(string message) : HttpRequestException(message);
