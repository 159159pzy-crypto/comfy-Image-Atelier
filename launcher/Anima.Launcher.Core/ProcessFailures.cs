using System.Text.RegularExpressions;

namespace Anima.Launcher.Core;

public enum ProcessFailureKind { Unknown, Transport, ServerUnavailable, RateLimited, AuthenticationRequired, Forbidden,
    MissingResource, PackageUnavailable, DiskFull, AccessDenied, PathTooLong, FileInUse, DependencyConflict, BuildFailure, Canceled, Timeout }

public static class DiagnosticRedaction
{
    private static readonly Regex Url = new(@"https?://[^\s<>""']+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Secret = new(@"(?i)(authorization\s*[:=]\s*(?:bearer\s+|basic\s+)?|(?:api[_-]?key|access[_-]?token|refresh[_-]?token|password|proxy[_-]?password)\s*[:=]\s*)[^\s,;""']+", RegexOptions.Compiled);
    public static string Text(string text) => Secret.Replace(Url.Replace(text, match =>
    {
        if (!Uri.TryCreate(match.Value, UriKind.Absolute, out var uri)) return "[地址已隐藏]";
        var builder = new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" };
        return builder.Uri.GetLeftPart(UriPartial.Path) + (uri.Query.Length > 0 ? "?[已隐藏]" : "");
    }), "$1[已隐藏]");
}

public sealed class CommandFailureException : InvalidOperationException
{
    public string Executable { get; }
    public int ExitCode { get; }
    public string StandardOutput { get; }
    public string StandardError { get; }
    public ProcessFailureKind Kind { get; }
    public CommandFailureException(string executable, int exitCode, string stdout, string stderr)
        : base(BuildMessage(executable, exitCode, stdout, stderr))
    {
        Executable = Path.GetFileName(executable); ExitCode = exitCode;
        StandardOutput = Tail(DiagnosticRedaction.Text(stdout), 65536);
        StandardError = Tail(DiagnosticRedaction.Text(stderr), 65536);
        Kind = Classify(stdout + "\n" + stderr);
    }
    private static string BuildMessage(string exe, int exit, string stdout, string stderr)
    {
        var detail = Tail(DiagnosticRedaction.Text(stderr.Length > 0 ? stderr : stdout).TrimEnd(), 1800);
        return $"{Path.GetFileName(exe)} 失败，退出码 {exit}（{Classify(stdout + "\n" + stderr)}）。" + (detail.Length > 0 ? "末尾输出：" + detail : "请查看日志。");
    }
    private static string Tail(string text, int maximum) => text.Length <= maximum ? text : text[^maximum..];
    public static ProcessFailureKind Classify(string text)
    {
        var message = text.ToLowerInvariant();
        bool Has(params string[] values) => values.Any(message.Contains);
        bool HttpStatus(string code) => Regex.IsMatch(message,
            @"(?:http(?: error)?(?: status(?: code)?)?\s*[:=]?\s*|returned error:\s*|status code:?\s*)" + code + @"\b|\b" + code + @"\s+(?:(?:client|server)\s+error|unauthorized|forbidden|not found|too many requests)\b");
        if (Has("no space left", "not enough space", "disk full", "winerror 112")) return ProcessFailureKind.DiskFull;
        if (Has("filename too long", "file name too long", "winerror 206")) return ProcessFailureKind.PathTooLong;
        if (Has("being used by another process", "winerror 32")) return ProcessFailureKind.FileInUse;
        if (Has("permission denied", "access is denied", "winerror 5")) return ProcessFailureKind.AccessDenied;
        if (Has("resolutionimpossible", "conflicting dependencies", "dependency conflict")) return ProcessFailureKind.DependencyConflict;
        if (Has("failed building wheel", "subprocess-exited-with-error", "error: command", "compiler error", "checkout failed")) return ProcessFailureKind.BuildFailure;
        if (HttpStatus("401") || Has("authentication failed", "could not read username", "authentication required")) return ProcessFailureKind.AuthenticationRequired;
        if (HttpStatus("403") || Has("access forbidden")) return ProcessFailureKind.Forbidden;
        if (HttpStatus("429") || Has("too many requests", "rate limit exceeded")) return ProcessFailureKind.RateLimited;
        if (HttpStatus("404") || Has("not our ref", "repository not found")) return ProcessFailureKind.MissingResource;
        if (Has("could not find a version", "no matching distribution")) return ProcessFailureKind.PackageUnavailable;
        if (HttpStatus(@"5\d\d")) return ProcessFailureKind.ServerUnavailable;
        if (Has("timed out", "timeout", "connection", "resolve host", "name resolution", "network is unreachable", "ssl", "tls", "remote end hung", "early eof", "fetch failed", "failed to download")) return ProcessFailureKind.Transport;
        return ProcessFailureKind.Unknown;
    }
}
