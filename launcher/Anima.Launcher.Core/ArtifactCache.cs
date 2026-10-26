namespace Anima.Launcher.Core;

/// <summary>Content-addressed download cache. Legacy files are adopted only after identity validation.</summary>
public static class ArtifactCache
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    public static string FileName(Artifact artifact)
    {
        var name = artifact.FileName.Length > 0 ? artifact.FileName : Uri.UnescapeDataString(new Uri(artifact.Url).AbsolutePath.Split('/').Last());
        if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || name.Contains('/') || name.Contains('\\') || name.Contains(':') || name is "." or "..")
            throw new InvalidDataException("资源需要安全的原始文件名。");
        return name;
    }

    public static string PathFor(string runtime, Artifact artifact)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(artifact.Sha256 ?? "", "^[a-fA-F0-9]{64}$")) throw new InvalidDataException("缓存资源缺少 SHA-256。");
        return Safety.Under(Path.Combine(runtime, "cache", "artifacts"), artifact.Sha256!.ToLowerInvariant() + "/" + FileName(artifact));
    }

    public static string[] LegacyPaths(string runtime, Artifact artifact) =>
        [Path.Combine(runtime, "cache", artifact.Id + ".zip"), Path.Combine(runtime, "cache", "wheels", artifact.Executable)];

    public static long RemainingBytes(string runtime, Artifact artifact)
    {
        var current = Downloads.RemainingBytes(PathFor(runtime, artifact), artifact.Size, artifact.Sha256);
        return LegacyPaths(runtime, artifact).Aggregate(current,
            (remaining, path) => Math.Min(remaining, Downloads.RemainingBytes(path, artifact.Size, artifact.Sha256)));
    }

    public static async Task<string> PrepareAsync(string runtime, Artifact artifact, CancellationToken ct = default)
    {
        var destination = PathFor(runtime, artifact);
        var gate = Gates.GetOrAdd(Path.GetFullPath(destination), _ => new(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            using var targetLease = await DownloadTargetLease.AcquireAsync(destination, ct);
            return await PrepareLockedAsync(runtime, artifact, destination, ct);
        }
        finally { gate.Release(); }
    }

    private static async Task<string> PrepareLockedAsync(string runtime, Artifact artifact, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (await Downloads.Verify(destination, artifact.Size, artifact.Sha256, ct)) return destination;
        if (File.Exists(destination))
        {
            RequireRegularFile(destination);
            File.Move(destination, destination + ".invalid-" + Guid.NewGuid().ToString("N"));
        }
        foreach (var legacy in LegacyPaths(runtime, artifact).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (legacy.Equals(destination, StringComparison.OrdinalIgnoreCase)) continue;
            if (File.Exists(legacy))
            {
                RequireRegularFile(legacy);
                if (await Downloads.Verify(legacy, artifact.Size, artifact.Sha256, ct))
                {
                    File.Move(legacy, destination);
                    return destination;
                }
            }
            var part = legacy + ".part";
            if (File.Exists(destination + ".part") || !File.Exists(part) || !File.Exists(part + ".json")) continue;
            RequireRegularFile(part);
            RequireRegularFile(part + ".json");
            ResumeInfo? resume;
            try { resume = JsonFile.Read<ResumeInfo>(part + ".json"); } catch { continue; }
            if (resume.Size != artifact.Size || !resume.Sha256.Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase)
                || new FileInfo(part).Length > artifact.Size) continue;
            // Publish metadata first, then atomically move bytes on this runtime volume.
            // An interruption before the move leaves the original pair intact.
            JsonFile.Write(destination + ".part.json", resume);
            ct.ThrowIfCancellationRequested();
            File.Move(part, destination + ".part");
            // Keep the old sidecar for diagnosis; an orphan sidecar is never treated as data.
        }
        return destination;
    }

    public static async Task<string> FetchAsync(string runtime, Artifact artifact, LauncherConfig config,
        Downloads downloads, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var destination = await PrepareAsync(runtime, artifact, ct);
        await downloads.Fetch(artifact.Id, DownloadSources.ArtifactCandidates(artifact, config), destination,
            artifact.Size, artifact.Sha256, progress, ct);
        return destination;
    }

    private static void RequireRegularFile(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("缓存文件是链接，不能自动迁移。");
    }
}
