using System.Collections.Concurrent;

namespace Anima.Launcher.Core;

/// <summary>A model package is published only after every locked file has passed SHA-256.</summary>
public static class ModelPackages
{
    private static readonly ConcurrentDictionary<string, (long Length, long Write, long Created, string Sha)> verified = new(StringComparer.OrdinalIgnoreCase);

    public static string Staging(ModelItem model, string destination) => Safety.Under(
        Path.GetDirectoryName(Path.GetFullPath(destination))! + ".anima-packages", model.Id + "-" + model.Revision);

    public static async Task<string> State(ModelItem model, string directory, CancellationToken ct)
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
            if (!verified.TryGetValue(path, out var previous) || previous != stamp)
            {
                if (!await Downloads.Verify(path, file.Size, file.Sha256, ct)) return "哈希异常";
                info.Refresh();
                if (stamp != (info.Length, info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks, file.Sha256)) return "文件不完整";
                verified[path] = stamp;
            }
        }
        return "已校验";
    }

    public static long RemainingBytes(ModelItem model, string destination) => model.Files.Sum(file =>
        Downloads.RemainingBytes(Safety.Under(Staging(model, destination), file.Path), file.Size, file.Sha256));

    public static async Task Fetch(Downloads downloads, ModelItem model, string destination, LauncherConfig config,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (!model.IsPackage || !model.CanDownload || string.IsNullOrWhiteSpace(destination))
            throw new InvalidDataException("模型包或目标目录尚未确认，请重新扫描。");
        if (await State(model, destination, ct) == "已校验") return;
        var staging = Staging(model, destination);
        Safety.FreeSpace(staging, RemainingBytes(model, destination));
        long complete = 0;
        foreach (var file in model.Files)
        {
            ct.ThrowIfCancellationRequested();
            var target = Safety.Under(staging, file.Path);
            if (File.Exists(target) && !await Downloads.Verify(target, file.Size, file.Sha256, ct))
            {
                var retained = Safety.Under(Path.GetDirectoryName(staging)!, "backups/" + model.Id + "-file-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.GetDirectoryName(retained)!);
                File.Move(target, retained);
            }
            var prefix = complete;
            var aggregate = new InlineProgress(value => progress?.Report(new(model.Name, prefix + value.Received, model.Size, value.Stage)));
            await downloads.Fetch(model.Name, DownloadSources.ModelCandidates(file, config), target, file.Size, file.Sha256, aggregate, ct);
            complete += file.Size;
        }
        if (await State(model, staging, ct) != "已校验") throw new InvalidDataException("模型包校验未通过，未发布到 ComfyUI。");
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
