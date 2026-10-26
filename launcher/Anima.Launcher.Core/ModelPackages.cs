using System.Collections.Concurrent;

namespace Anima.Launcher.Core;

/// <summary>A model package is published only after every locked file has passed SHA-256.</summary>
public static class ModelPackages
{
    private static readonly ConcurrentDictionary<string, (long Length, long Write, long Created, string Sha)> verified = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, (long Length, long Write, long Created, string Sha)> invalid = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> publications = new(StringComparer.OrdinalIgnoreCase);

    public static string Staging(ModelItem model, string destination) => Safety.Under(
        Path.GetDirectoryName(Path.GetFullPath(destination))! + ".anima-packages", model.Id + "-" + model.Revision);

    public static async Task<string> State(ModelItem model, string directory, CancellationToken ct, bool forceVerification = false)
    {
        if (!Directory.Exists(directory)) return "缺失";
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return "文件不完整";
        if (model.Category == "wd_taggers" && new[] { "timm_arch.py", "preprocess.json" }
            .Any(name => File.Exists(Path.Combine(directory, name)) && !model.Files.Any(file => file.Path == name)))
            return "文件不完整";
        foreach (var file in model.Files)
        {
            ct.ThrowIfCancellationRequested();
            var path = Safety.Under(directory, file.Path);
            if (!File.Exists(path)) return "文件不完整";
            var info = new FileInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return "文件不完整";
            if (info.Length != file.Size) return "大小异常";
            var stamp = (info.Length, info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks, file.Sha256);
            // Timestamp equality is a listing cache hint, not proof of content identity.
            // Rehash small configuration/code files, and bypass all hints at verified
            // scans and publication boundaries, including same-size/same-time writes.
            if (forceVerification || file.Size <= 1024 * 1024 || invalid.ContainsKey(path)
                || !verified.TryGetValue(path, out var previous) || previous != stamp)
            {
                if (!await Downloads.Verify(path, file.Size, file.Sha256, ct))
                { verified.TryRemove(path, out _); invalid[path] = stamp; return "哈希异常"; }
                info.Refresh();
                if (stamp != (info.Length, info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks, file.Sha256)) return "文件不完整";
                verified[path] = stamp;
                invalid.TryRemove(path, out _);
            }
        }
        return "已校验";
    }

    public static long RemainingBytes(ModelItem model, string destination) => model.Files.Sum(file =>
        Downloads.RemainingBytes(Safety.Under(Staging(model, destination), file.Path), file.Size, file.Sha256));

    // This is a cheap space estimate; Fetch still verifies every completed member
    // before reuse and reserves a complete replacement if validation fails.
    public static long RemainingAllocationBytes(ModelItem model, string destination)
    {
        bool Complete(string path, ModelFile file)
        {
            if (!File.Exists(path)) return false;
            var info = new FileInfo(path);
            var stamp = (info.Length, info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks, file.Sha256);
            return (info.Attributes & FileAttributes.ReparsePoint) == 0 && info.Length == file.Size
                && (!invalid.TryGetValue(path, out var rejected) || rejected != stamp);
        }
        var unexpectedArchitecture = model.Category == "wd_taggers" && new[] { "timm_arch.py", "preprocess.json" }
            .Any(name => File.Exists(Path.Combine(destination, name)) && !model.Files.Any(file => file.Path == name));
        if (!unexpectedArchitecture && Directory.Exists(destination) && (File.GetAttributes(destination) & FileAttributes.ReparsePoint) == 0
            && model.Files.All(file => Complete(Safety.Under(destination, file.Path), file))) return 0;
        var staging = Staging(model, destination);
        return model.Files.Sum(file =>
        {
            var path = Safety.Under(staging, file.Path);
            return Complete(path, file) ? 0 : File.Exists(path) ? file.Size
                : DownloadDiskSpace.RemainingAllocationBytes(path, file.Size, file.Sha256);
        });
    }

    public static async Task Fetch(Downloads downloads, ModelItem model, string destination, LauncherConfig config,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (!model.IsPackage || !model.CanDownload || string.IsNullOrWhiteSpace(destination))
            throw new InvalidDataException("模型包或目标目录尚未确认，请重新扫描。");
        DownloadTaskIdentity.ValidateModel(new(model, destination));
        var publication = publications.GetOrAdd(Path.GetFullPath(destination), _ => new(1, 1));
        await publication.WaitAsync(ct);
        var leases = new List<DownloadTargetLease>();
        try
        {
            // The historical staging name is shared by model id/revision, even when
            // two callers publish to different folder names. Lock both identities.
            foreach (var path in new[] { Path.GetFullPath(destination), Staging(model, destination) }
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
                leases.Add(await DownloadTargetLease.AcquireAsync(path, ct));
            await FetchLocked(downloads, model, destination, config, progress, ct);
        }
        finally { foreach (var lease in leases.AsEnumerable().Reverse()) lease.Dispose(); publication.Release(); }
    }

    private static async Task FetchLocked(Downloads downloads, ModelItem model, string destination, LauncherConfig config,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (await State(model, destination, ct, forceVerification: true) == "已校验") return;
        var staging = Staging(model, destination);
        Safety.FreeSpace(staging, RemainingAllocationBytes(model, destination));
        var gate = new object();
        var received = model.Files.ToDictionary(file => file.Path, file => Math.Max(0, file.Size
            - Downloads.RemainingBytes(Safety.Under(staging, file.Path), file.Size, file.Sha256)), StringComparer.OrdinalIgnoreCase);
        // Preserve sequential legacy callers; the task dispatcher supplies the global two-file budget.
        var concurrency = downloads.Dispatcher is null ? 1 : 2;
        using var siblings = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await Parallel.ForEachAsync(model.Files, new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = siblings.Token }, async (file, token) =>
        {
            token.ThrowIfCancellationRequested();
            var target = Safety.Under(staging, file.Path);
            try
            {
                if (File.Exists(target) && !await Downloads.Verify(target, file.Size, file.Sha256, token))
                {
                    var retained = Safety.Under(Path.GetDirectoryName(staging)!, "backups/" + model.Id + "-file-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(Path.GetDirectoryName(retained)!);
                    File.Move(target, retained);
                    lock (gate) received[file.Path] = 0;
                }
                var aggregate = new InlineProgress(value =>
                {
                    lock (gate)
                    {
                        received[file.Path] = Math.Clamp(value.Received, 0, file.Size);
                        progress?.Report(value with { Name = model.Name, Received = received.Values.Sum(), Total = model.Size });
                    }
                });
                await downloads.Fetch(model.Name, DownloadSources.ModelCandidates(file, config), target, file.Size, file.Sha256, aggregate, token);
                lock (gate) received[file.Path] = file.Size;
            }
            catch { siblings.Cancel(); throw; }
        });
        if (await State(model, staging, ct, forceVerification: true) != "已校验") throw new InvalidDataException("模型包校验未通过，未发布到 ComfyUI。");
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string? backup = null;
        if (Directory.Exists(destination))
        {
            backup = Safety.Under(Path.GetDirectoryName(staging)!, "backups/" + model.Id + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            Directory.Move(destination, backup);
        }
        try { Directory.Move(staging, destination); }
        catch
        {
            if (backup is not null && !Directory.Exists(destination)) Directory.Move(backup, destination);
            throw;
        }
        progress?.Report(new(model.Name, model.Size, model.Size, backup is null ? "模型包已校验并就绪" : "模型包已修复，原目录已备份"));
    }

    // Pause/cancel retains verified files and partial files so retry resumes the whole package.
    private sealed class InlineProgress(Action<DownloadProgress> callback) : IProgress<DownloadProgress>
    { public void Report(DownloadProgress value) => callback(value); }
}
