using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Anima.Launcher.Core;

public static class ExternalRepairBackup
{
    private const string ReceiptName = "manifest.json";
    private const string InstructionsName = "恢复说明.txt";
    private sealed record Target(string Original, string Copy, bool Existed, bool IsDirectory, long Bytes);
    private sealed record Receipt(string Binding, DateTimeOffset Created, Target[] Targets);

    private static string Destination(InstallationPlan plan)
    {
        if (!Guid.TryParseExact(plan.ExternalRepairBackupId, "N", out _))
            throw new InvalidDataException("修复计划缺少备份身份，请重新检查后确认。");
        return Path.Combine(plan.RuntimeRoot, "external-repair-backups", plan.ExternalRepairBackupId);
    }

    public static string Preview(InstallationPlan plan) => !plan.Fresh && plan.CreateConfig().ExternalEnvironmentMode == "repair"
        ? "修复前会备份完整的独立 Python、所选节点和配置。手动恢复说明与备份保存在：" + Destination(plan) : "";

    internal static async Task<string> EnsureAsync(InstallationPlan plan, IReadOnlyList<NodeInstallPlan> nodes,
        IEnumerable<VolumeEstimate> installSpace, Action<string> log, CancellationToken ct)
    {
        var config = plan.CreateConfig();
        if (plan.Fresh || config.ExternalEnvironmentMode != "repair") throw new InvalidOperationException("备份必须来自已确认的外部修复计划。");
        var destination = Path.GetFullPath(Destination(plan));
        var python = ComfyUpdateCoordinator.IndependentPythonDirectory(config);
        var comfy = Path.GetFullPath(config.ComfyRoot);
        foreach (var protectedRoot in new[] { comfy, python })
            if (destination.Equals(protectedRoot, StringComparison.OrdinalIgnoreCase)
                || destination.StartsWith(Path.TrimEndingDirectorySeparator(protectedRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("修复备份目录必须位于 ComfyUI 和独立 Python 之外。");
        RejectLinkedParents(destination);
        var sources = new List<(string Original, string Copy)> { (python, "python") };
        foreach (var node in nodes.OrderBy(item => item.Repository.Id, StringComparer.Ordinal))
            sources.Add((node.Directory, "nodes/" + node.Repository.Id));
        foreach (var relative in ComfyUpdateCoordinator.ConfigurationPaths.Concat([".anima-constraints.txt", ".anima-revision.json",
            "custom_nodes/skip_download_model", "custom_nodes/" + ResourceInspectionExtension.DirectoryName]))
            sources.Add((Safety.Under(comfy, relative), "configuration/" + relative));
        var binding = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            plan.ExternalRepairBackupId, Root = comfy, Python = config.Python, plan.Purpose,
            Sources = sources.Select(item => new { item.Original, item.Copy }),
            Nodes = nodes.OrderBy(node => node.Repository.Id, StringComparer.Ordinal).Select(node => node.Repository),
        }, JsonFile.Options)));

        using var lease = await DownloadTargetLease.AcquireAsync(destination, ct);
        return await Task.Run(() => CreateOrReuse(), ct);

        string CreateOrReuse()
        {
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(destination))
            {
                var receipt = JsonFile.Read<Receipt>(Path.Combine(destination, ReceiptName));
                if (receipt.Binding != binding || !receipt.Targets.Select(item => (item.Original, item.Copy)).SequenceEqual(sources)
                    || !File.Exists(Path.Combine(destination, InstructionsName)))
                    throw new InvalidDataException("原修复备份与当前计划不匹配，请检查备份记录：" + destination);
                foreach (var item in receipt.Targets.Where(item => item.Existed))
                    if (Measure(Safety.Under(destination, item.Copy), item.IsDirectory, ct) != item.Bytes)
                        throw new InvalidDataException("原修复备份不完整：" + item.Copy);
                return destination;
            }
            var stage = destination + ".preparing";
            var owner = Path.Combine(stage, ".anima-backup-owner");
            if (Directory.Exists(stage))
            {
                RejectLinkedParents(owner);
                if (!File.Exists(owner) || File.ReadAllText(owner) != binding)
                    throw new IOException("未完成的备份位于其他任务目录，已保留：" + stage);
                ClearStaging(stage, destination);
            }
            var targets = sources.Select(item =>
            {
                RejectLinkedParents(item.Original);
                var isDirectory = Directory.Exists(item.Original);
                var exists = isDirectory || File.Exists(item.Original);
                return new Target(item.Original, item.Copy, exists, isDirectory, exists ? Measure(item.Original, isDirectory, ct) : 0);
            }).ToArray();
            var volume = DownloadDiskSpace.VolumeRoot(destination);
            var reserve = installSpace.FirstOrDefault(v => string.Equals(v.Volume, volume, StringComparison.OrdinalIgnoreCase))?.RequiredBytes ?? 256L * 1024 * 1024;
            var bytes = targets.Sum(item => item.Bytes);
            var needed = checked(bytes + reserve);
            var available = DownloadDiskSpace.AvailableForPlanningAsync(destination, ct).GetAwaiter().GetResult();
            if (available < needed)
                throw new IOException($"修复备份需 {bytes / 1073741824d:N2} GiB，加上安装空间共需 {needed / 1073741824d:N2} GiB，{volume} 可用 {available / 1073741824d:N2} GiB。请释放空间再继续。");
            log($"正在备份独立 Python、所选节点和配置（{bytes / 1073741824d:N2} GiB）：{destination}");
            Directory.CreateDirectory(stage);
            File.WriteAllText(owner, binding);
            try
            {
                foreach (var item in targets.Where(t => t.Existed))
                {
                    ct.ThrowIfCancellationRequested();
                    var copy = Safety.Under(stage, item.Copy);
                    Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                    if (item.IsDirectory) ComfyUpdateCoordinator.CopyDirectory(item.Original, copy, ct);
                    else File.Copy(item.Original, copy);
                    if (Measure(copy, item.IsDirectory, ct) != item.Bytes)
                        throw new IOException("备份过程中源内容发生变化，请关闭占用程序后重试：" + item.Original);
                }
                var receipt = new Receipt(binding, DateTimeOffset.UtcNow, targets);
                JsonFile.Write(Path.Combine(stage, ReceiptName), receipt);
                var instructions = new List<string>
                {
                    "外部 ComfyUI 修复前备份", "1. 关闭启动器、原整合包和使用此 Python 的所有服务。",
                    "2. 对下列原路径逐项操作。若原路径现在存在，先将其移到单独目录保存。对于有备份的项，将备份文件或整个目录复制回原路径。",
                    "3. 标为“原先不存在”的项仅移出原路径。处理完成后按原整合包方式启动并验证。"
                };
                instructions.AddRange(targets.Select(item => item.Existed
                    ? "备份 " + item.Copy + " → 原路径 " + item.Original : "原先不存在：" + item.Original));
                File.WriteAllLines(Path.Combine(stage, InstructionsName), instructions, Encoding.UTF8);
                ct.ThrowIfCancellationRequested();
                Directory.Move(stage, destination);
                return destination;
            }
            finally
            {
                if (Directory.Exists(stage)) ClearStaging(stage, destination);
            }
        }
    }

    private static void RejectLinkedParents(string path)
    {
        ComfyUpdateCoordinator.CheckParents(Path.GetPathRoot(Path.GetFullPath(path))!, path);
        if (File.Exists(path) || Directory.Exists(path)) ComfyUpdateCoordinator.RejectLink(path);
    }

    private static long Measure(string path, bool directory, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ComfyUpdateCoordinator.RejectLink(path);
        if (!directory) return new FileInfo(path).Length;
        long size = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            size = checked(size + Measure(entry, Directory.Exists(entry), ct));
        return size;
    }

    private static void ClearStaging(string stage, string destination)
    {
        if (Path.GetFullPath(stage) != Path.GetFullPath(destination) + ".preparing")
            throw new IOException("备份暂存路径不匹配。");
        RejectLinkedParents(stage);
        InstallationTemporaryDirectory.DeleteTree(stage, stage);
    }
}
