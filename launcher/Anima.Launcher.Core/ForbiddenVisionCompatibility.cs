using System.Security.Cryptography;
using System.Text;

namespace Anima.Launcher.Core;

public static class ForbiddenVisionCompatibility
{
    public const string NodeId = "forbidden-vision";
    public const string SupportedCommit = "5617d4395fa6be9b0e57d74a56e2943ede9ef192";

    public static void ValidatePlannedVersion(string nodeId, string commit)
    {
        if (nodeId == NodeId && !commit.Equals(SupportedCommit, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("当前 Forbidden Vision 最新版本尚未通过安装下载兼容验证，请使用支持该版本的启动器再检查；尚未开始安装，也未改动现有环境。");
    }

    // Only these files belong to the validated compatibility transaction. Other
    // files in the backup directory can be user-owned and must still be preserved.
    public static bool IsManagedArtifact(string relative) => relative.Replace('\\', '/') is
        ".anima-managed-models-v1/receipt.json" or
        ".anima-managed-models-v1/__init__.py" or
        ".anima-managed-models-v1/src/model_manager.py";

    public static string? ResolveDirectory(string comfyRoot)
    {
        var custom = Path.Combine(Path.GetFullPath(comfyRoot), "custom_nodes");
        if (!Directory.Exists(custom)) return null;
        var matches = Directory.EnumerateDirectories(custom).Where(path => !Path.GetFileName(path).StartsWith('.')
            && !new[] { ".disabled", ".anima-installing", ".extracting" }.Any(suffix => path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            && File.Exists(Path.Combine(path, "__init__.py")) && File.Exists(Path.Combine(path, "src", "model_manager.py"))
            && File.ReadAllText(Path.Combine(path, "src", "model_manager.py")).Contains("class ForbiddenVisionModelManager")).ToArray();
        if (matches.Length > 1) throw new InvalidDataException("发现多个 Forbidden Vision 节点，请只保留一个启用的安装。");
        return matches.SingleOrDefault();
    }

    public static bool IsOnlyCompatibilityChange(string status)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal) { "__init__.py", "src/model_manager.py" };
        var lines = status.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length > 0 && lines.All(line => line.StartsWith(" M ", StringComparison.Ordinal) && paths.Contains(line[3..]));
    }

    public static async Task CheckOrApply(LauncherConfig config, string stateDir, string nodeDirectory, bool apply, CancellationToken ct)
    {
        using var resource = typeof(ForbiddenVisionCompatibility).Assembly.GetManifestResourceStream("Anima.Launcher.Core.patch_forbidden_vision.py")
            ?? throw new InvalidOperationException("安装包缺少 Forbidden Vision 下载兼容脚本。");
        using var reader = new StreamReader(resource, Encoding.UTF8);
        var script = await reader.ReadToEndAsync(ct);
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script)))[..16];
        var directory = Path.Combine(stateDir, "tools");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "patch-forbidden-vision-" + identity + ".py");
        if (!File.Exists(path)) await File.WriteAllTextAsync(path, script, new UTF8Encoding(false), ct);
        try { await Commands.Run(config.Python, [path, "--root", nodeDirectory, apply ? "--apply" : "--check"], config.ComfyRoot, null, ct, timeout: TimeSpan.FromSeconds(30)); }
        catch (InvalidOperationException error)
        {
            throw new InvalidOperationException("Forbidden Vision 下载管理兼容检查未通过；已保留源码和原版本。请查看安装日志中的具体原因。", error);
        }
    }
}
