using System.Text.RegularExpressions;

namespace Anima.Launcher.Core;

public enum DependencyProgressStatus { Running, Complete, Paused, Failed }

/// <summary>Transient dependency activity, separate from durable download and installation receipts.</summary>
public sealed record DependencyProgress(string Phase, string Detail)
{
    public string OperationId { get; init; } = "";
    public string CurrentPackage { get; init; } = "";
    public string Source { get; init; } = "";
    public long? Received { get; init; }
    public long? Total { get; init; }
    public double? BytesPerSecond { get; init; }
    public int? CompletedPackages { get; init; }
    public int? TotalPackages { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset LastOutputAt { get; init; }
    public DependencyProgressStatus Status { get; init; } = DependencyProgressStatus.Running;
}

/// <summary>Reports activity while retaining every original log line. Unrecognized output is still a heartbeat.</summary>
public sealed class DependencyProgressReporter(Action<DependencyProgress>? progress, Action<string>? log = null, TimeProvider? clock = null)
{
    private readonly object sync = new();
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly string operationId = Guid.NewGuid().ToString("N");
    private DependencyProgress? current;
    private static readonly Regex Ansi = new(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);
    private static readonly Regex Package = new(@"^(?:Collecting\s+|Building wheel for\s+|Building\s+|[+]\s+)([A-Za-z0-9][A-Za-z0-9_.-]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Wheel = new(@"^(?:Downloading|Using cached)\s+(?:https?://[^\s]+/)?([A-Za-z0-9][A-Za-z0-9_.]*?)-[0-9][^\s/]*\.whl(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public void Phase(string phase, string detail, string? source = null)
    {
        lock (sync)
        {
            var now = time.GetUtcNow();
            Publish(new(phase, detail)
            {
                OperationId = operationId, Source = source ?? current?.Source ?? "",
                StartedAt = current?.StartedAt ?? now, LastOutputAt = now,
            });
        }
    }

    public void Log(string text)
    {
        log?.Invoke(text);
        lock (sync)
        {
            if (current is not { Status: DependencyProgressStatus.Running } value) return;
            var package = value.CurrentPackage;
            foreach (var line in text.Replace('\r', '\n').Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var clean = Ansi.Replace(line.Trim(), "");
                var match = Package.Match(clean);
                if (!match.Success) match = Wheel.Match(clean);
                if (match.Success) package = match.Groups[1].Value;
            }
            // Candidate resolution, metadata downloads and build output do not prove an installed package count.
            Publish(value with { CurrentPackage = package, LastOutputAt = time.GetUtcNow() });
        }
    }

    public void Report(DependencyProgress value)
    {
        lock (sync)
        {
            if (current is { Status: not DependencyProgressStatus.Running } && value.Status == DependencyProgressStatus.Running) return;
            var now = time.GetUtcNow();
            Publish(value with
            {
                OperationId = value.OperationId.Length > 0 ? value.OperationId : operationId,
                StartedAt = value.StartedAt == default ? current?.StartedAt ?? now : value.StartedAt,
                LastOutputAt = value.LastOutputAt == default ? now : value.LastOutputAt,
            });
        }
    }

    public void Complete(string detail) => Finish(DependencyProgressStatus.Complete, detail);
    public void Fail(string detail) => Finish(DependencyProgressStatus.Failed, detail);
    public void Pause(string detail) => Finish(DependencyProgressStatus.Paused, detail);
    private void Finish(DependencyProgressStatus status, string detail)
    {
        lock (sync)
        {
            var now = time.GetUtcNow();
            Publish((current ?? new DependencyProgress("dependencies", detail) { OperationId = operationId, StartedAt = now })
                with { Detail = detail, Status = status, LastOutputAt = now });
        }
    }

    private void Publish(DependencyProgress value)
    {
        current = value with
        {
            Detail = Clean(value.Detail), CurrentPackage = Clean(value.CurrentPackage), Source = SourceLabel(value.Source),
            Received = value.Received is >= 0 ? value.Received : null,
            Total = value.Total is > 0 ? value.Total : null,
            BytesPerSecond = value.Phase == "downloading" && value.Status == DependencyProgressStatus.Running
                && value.BytesPerSecond is > 0 and var speed && double.IsFinite(speed) ? speed : null,
            CompletedPackages = value.CompletedPackages is >= 0 ? value.CompletedPackages : null,
            TotalPackages = value.TotalPackages is > 0 ? value.TotalPackages : null,
        };
        progress?.Invoke(current);
    }

    private static string Clean(string text)
    {
        var safe = Ansi.Replace(DiagnosticRedaction.Text(text), "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return safe.Length <= 300 ? safe : safe[..300] + "…";
    }

    private static string SourceLabel(string source) => Uri.TryCreate(source, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" ? uri.Host : Clean(source);
}

/// <summary>Rejects callbacks from a finished or superseded launcher task.</summary>
public sealed class DependencyProgressSession
{
    private readonly object sync = new();
    private long generation;
    private bool active;
    private DependencyProgress? current;
    public DependencyProgress? Current { get { lock (sync) return current; } }
    public long Generation { get { lock (sync) return generation; } }
    public long Begin()
    {
        lock (sync) { generation++; active = true; current = null; return generation; }
    }
    public bool TryReport(long expectedGeneration, DependencyProgress value)
    {
        lock (sync)
        {
            if (!active || generation != expectedGeneration) return false;
            current = value;
            return true;
        }
    }
    public void Clear() { lock (sync) current = null; }
    public void End(DependencyProgressStatus status, DateTimeOffset now)
    {
        lock (sync)
        {
            active = false;
            if (current is not null && (current.Status == DependencyProgressStatus.Running || status != DependencyProgressStatus.Complete))
                current = current with
                {
                    Status = status, LastOutputAt = now,
                    Detail = status == DependencyProgressStatus.Complete ? "依赖处理已完成" : current.Detail,
                };
        }
    }
}

public sealed record DependencyProgressPresentation(string Heading, string Detail, string QuietNotice, int Percent, bool Indeterminate)
{
    public static DependencyProgressPresentation Create(DependencyProgress value, DateTimeOffset now)
    {
        var ended = value.Status == DependencyProgressStatus.Running ? now : value.LastOutputAt;
        var elapsed = value.StartedAt == default ? TimeSpan.Zero : ended - value.StartedAt;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        var idle = now - value.LastOutputAt;
        var heading = value.Status switch
        {
            DependencyProgressStatus.Paused => "依赖处理已暂停",
            DependencyProgressStatus.Failed => "依赖处理未完成",
            _ => value.Detail + (value.CurrentPackage.Length > 0 ? " · " + value.CurrentPackage : ""),
        };
        var details = new List<string>();
        int? percent = null;
        if (value.Received is { } received && value.Total is > 0 and var total)
        {
            details.Add($"{received / 1048576d:N1} / {total / 1048576d:N1} MiB");
            percent = (int)Math.Clamp(received * 100d / total, 0, 99);
        }
        if (value.Phase == "downloading" && value.Status == DependencyProgressStatus.Running
            && value.BytesPerSecond is > 0 and var speed && double.IsFinite(speed)
            && value.LastOutputAt != default && idle < TimeSpan.FromSeconds(3))
            details.Add($"{speed / 1048576d:N1} MiB/s");
        if (value.CompletedPackages is { } complete && value.TotalPackages is > 0 and var packages)
        {
            details.Add($"已完成 {complete}/{packages} 个依赖包");
            percent ??= (int)Math.Clamp(complete * 100d / packages, 0, 99);
        }
        details.Add("已用时 " + Duration(elapsed));
        if (value.Source.Length > 0) details.Add("来源：" + value.Source);
        var quiet = value.Status != DependencyProgressStatus.Running || value.LastOutputAt == default || idle < TimeSpan.FromSeconds(30) ? ""
            : idle < TimeSpan.FromSeconds(90) ? "正在等待依赖任务的新输出，可查看日志。"
            : $"已 {Duration(idle)}没有新输出，依赖任务仍在运行；可查看日志或暂停。";
        return new(heading, string.Join(" · ", details), quiet,
            value.Status == DependencyProgressStatus.Complete ? 100 : percent ?? 0,
            value.Status == DependencyProgressStatus.Running && percent is null);
    }
    private static string Duration(TimeSpan elapsed) => elapsed.TotalHours >= 1
        ? $"{(int)elapsed.TotalHours} 小时 {elapsed.Minutes} 分 "
        : elapsed.TotalMinutes >= 1 ? $"{(int)elapsed.TotalMinutes} 分 {elapsed.Seconds} 秒 " : $"{(int)elapsed.TotalSeconds} 秒 ";
}
