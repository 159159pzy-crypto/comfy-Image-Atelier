using System.Security.Cryptography;

namespace Anima.Launcher.Core;

public sealed record ExtensionInstallResult(bool Available, bool Changed, string Message);
internal sealed record ResourceExtensionReceipt(int Version, Dictionary<string, string> Files);

/// <summary>Installs only our bundled, hash-owned extension; no third-party tree is rewritten.</summary>
public static class ResourceInspectionExtension
{
    public const string DirectoryName = "ComfyUI-Atelier-Resources";
    private const string ReceiptName = ".atelier-managed.json";

    public static ExtensionInstallResult Ensure(LauncherConfig config, bool running)
    {
        if (string.IsNullOrWhiteSpace(config.ComfyRoot) || !File.Exists(Path.Combine(config.ComfyRoot, "main.py")))
            return new(false, false, "ComfyUI 目录尚未就绪，模型检查扩展未安装。");
        var source = Path.Combine(config.AppRoot, "anima_webui", "comfy_extension", "atelier_resources");
        if (!File.Exists(Path.Combine(source, "__init__.py"))) return new(false, false, "当前工作台未包含模型检查扩展。请使用兼容版本。");
        var custom = Path.Combine(Path.GetFullPath(config.ComfyRoot), "custom_nodes");
        var target = Path.Combine(custom, DirectoryName);
        var expected = Files(source);
        if (Directory.Exists(target))
        {
            if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                return new(false, false, "模型检查扩展目录是链接，已保留，请在原环境管理此扩展。");
            var receipt = Path.Combine(target, ReceiptName);
            ResourceExtensionReceipt? owned;
            try { owned = File.Exists(receipt) ? JsonFile.Read<ResourceExtensionReceipt>(receipt) : null; }
            catch (Exception error) when (error is System.Text.Json.JsonException or IOException) { owned = null; }
            Dictionary<string, string> actual;
            try { actual = Files(target); }
            catch (IOException) { return new(false, false, "已有模型检查扩展含链接或无法读取，保留原文件。"); }
            if (owned is not { Version: 1 } || !Matches(actual, owned.Files))
                return new(false, false, "已有模型检查扩展不属于启动器或已被修改，保留原文件；请手动处理后重试。");
            if (Matches(actual, expected)) return new(true, false, "模型检查扩展已就绪。");
            if (running) return new(true, false, "模型检查扩展有更新，正常停止 ComfyUI 后下次启动会更新。");
        }
        if (running) return new(false, false, "模型检查扩展待安装，正常停止 ComfyUI 后下次启动会安装。");
        Directory.CreateDirectory(custom);
        var staging = Path.Combine(custom, ".atelier-resources-" + Guid.NewGuid().ToString("N") + ".disabled");
        Directory.CreateDirectory(staging);
        foreach (var relative in expected.Keys)
        {
            var destination = Safety.Under(staging, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Safety.Under(source, relative), destination);
        }
        if (!Matches(Files(staging), expected)) throw new IOException("模型检查扩展暂存校验失败，原安装保持不变。");
        JsonFile.Write(Path.Combine(staging, ReceiptName), new ResourceExtensionReceipt(1, expected));
        string? backup = null;
        if (Directory.Exists(target))
        {
            // Keep backups outside custom_nodes so ComfyUI cannot register duplicate routes.
            var backups = Path.Combine(config.ComfyRoot, ".anima-extension-backups");
            Directory.CreateDirectory(backups);
            backup = Path.Combine(backups, DirectoryName + "-" + Guid.NewGuid().ToString("N"));
            Directory.Move(target, backup);
        }
        try { Directory.Move(staging, target); }
        catch { if (backup is not null && !Directory.Exists(target)) Directory.Move(backup, target); throw; }
        return new(true, true, "模型检查扩展已安装并通过内容校验。");
    }

    private static Dictionary<string, string> Files(string root)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        void Visit(string directory)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (relative.Split('/').Contains("__pycache__") || relative == ReceiptName || relative.EndsWith(".pyc", StringComparison.Ordinal)) continue;
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("模型检查扩展包含链接，已保留原文件。");
                if (Directory.Exists(path)) { Visit(path); continue; }
                using var stream = File.OpenRead(path);
                result[relative] = Convert.ToHexString(SHA256.HashData(stream));
            }
        }
        Visit(root);
        return result;
    }
    private static bool Matches(Dictionary<string, string> actual, Dictionary<string, string> expected) => actual.Count == expected.Count
        && expected.All(item => actual.TryGetValue(item.Key, out var hash) && hash == item.Value);
}
