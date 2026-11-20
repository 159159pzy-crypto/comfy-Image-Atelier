using System.Collections.Concurrent;

namespace Anima.Launcher.Core;

internal static class ModelFileVerification
{
    private sealed record Stamp(long Length, long Write, long Created, string Sha)
    {
        internal static Stamp Read(FileInfo file, string sha) =>
            new(file.Length, file.LastWriteTimeUtc.Ticks, file.CreationTimeUtc.Ticks, sha);
    }

    private static readonly ConcurrentDictionary<string, (Stamp Stamp, bool Valid)> checks = new(StringComparer.OrdinalIgnoreCase);

    internal static string? LastResult(FileInfo file, string sha)
    {
        if (!checks.TryGetValue(file.FullName, out var last) || last.Stamp != Stamp.Read(file, sha)) return null;
        return last.Valid ? "已校验" : "哈希异常";
    }

    internal static async Task<string> CheckAsync(FileInfo file, long size, string sha, bool force, CancellationToken ct)
    {
        var before = Stamp.Read(file, sha);
        // Metadata is a cache hint for routine listing. Publication and explicit integrity scans always read the bytes.
        if (!force && size > 1024 * 1024 && LastResult(file, sha) == "已校验") return "已校验";
        var valid = await Downloads.Verify(file.FullName, size, sha, ct);
        file.Refresh();
        if (!file.Exists || before != Stamp.Read(file, sha)) return "文件不完整";
        checks[file.FullName] = (before, valid);
        return valid ? "已校验" : "哈希异常";
    }
}
