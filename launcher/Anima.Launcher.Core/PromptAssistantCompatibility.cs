using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Anima.Launcher.Core;

public static class PromptAssistantCompatibility
{
    public const string NodeId = "prompt-assistant";
    public const string SupportedCommit = "e0587e6d50939ae05e92922f1ed2fce008c7f622";

    public static void ValidatePlannedVersion(string nodeId, string commit)
    {
        if (nodeId == NodeId && !commit.Equals(SupportedCommit, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("当前提示词助手最新版本尚未通过纯模板兼容验证，请使用更新的启动器再检查；尚未开始安装，也未改动现有环境。");
    }
    public static string? ResolveDirectory(string comfyRoot)
    {
        var custom = Path.Combine(Path.GetFullPath(comfyRoot), "custom_nodes");
        if (!Directory.Exists(custom)) return null;
        var matches = Directory.EnumerateDirectories(custom).Where(path =>
            !Path.GetFileName(path).StartsWith('.') &&
            !new[] { ".disabled", ".anima-installing", ".extracting" }.Any(suffix => path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) &&
            File.Exists(Path.Combine(path, "node", "expand_node.py")) &&
            File.Exists(Path.Combine(path, "services", "llm.py")) &&
            File.ReadAllText(Path.Combine(path, "node", "expand_node.py")).Contains("class PromptExpand(")).ToArray();
        if (matches.Length > 1) throw new InvalidDataException("发现多个 Prompt Assistant，请保留一个有效安装后重试。");
        return matches.SingleOrDefault();
    }

    public static bool IsOnlyCompatibilityChange(string gitStatus)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal) { "node/expand_node.py", "services/llm.py" };
        var lines = gitStatus.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length > 0 && lines.All(line => line.StartsWith(" M ", StringComparison.Ordinal) && paths.Contains(line[3..]));
    }

    public static async Task CheckOrApply(LauncherConfig config, string stateDir, string pluginDirectory, bool apply, CancellationToken ct)
    {
        var assembly = typeof(PromptAssistantCompatibility).Assembly;
        using var resource = assembly.GetManifestResourceStream("Anima.Launcher.Core.patch_prompt_assistant.py")
            ?? throw new InvalidOperationException("启动器缺少提示词助手兼容脚本，请重新构建完整发布包。");
        using var reader = new StreamReader(resource, Encoding.UTF8);
        var script = await reader.ReadToEndAsync(ct);
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script)))[..16];
        var directory = Path.Combine(stateDir, "tools");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "patch-prompt-assistant-" + identity + ".py");
        if (!File.Exists(path)) await File.WriteAllTextAsync(path, script, new UTF8Encoding(false), ct);
        try
        {
            await Commands.Run(config.Python, [path, "--root", pluginDirectory, apply ? "--apply" : "--check"],
                config.ComfyRoot, null, ct, timeout: TimeSpan.FromSeconds(30));
        }
        catch (InvalidOperationException)
        {
            throw new InvalidOperationException("Prompt Assistant 纯模板兼容检查未通过。已保留未知或修改过的源码；请使用支持的插件版本并在停止 ComfyUI 后执行安装/修复。");
        }
    }
}
