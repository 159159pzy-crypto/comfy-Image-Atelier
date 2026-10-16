namespace Anima.Launcher.Core;

/// <summary>Find the Anima data provider without depending on a ZIP extraction folder name.</summary>
public static class AnimaToolsDiscovery
{
    public const string NodeId = "Comfyui-Anima-Tools";
    public static readonly string[] DataFiles = ["character_data.js", "clothing_data.js", "pose_data.js", "background_data.js"];
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static string? Resolve(string comfyRoot, string? explicitPath = null)
    {
        var selected = new[] { explicitPath, Environment.GetEnvironmentVariable("ANIMA_TOOLS_DIR"), Environment.GetEnvironmentVariable("COMFYUI_ANIMA_TOOLS") }
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (selected is not null)
        {
            var path = PhysicalPath(selected.Trim());
            var missing = MissingFiles(path);
            if (missing.Length > 0) throw new InvalidDataException($"指定的 Anima Tools 目录无效：{path}；缺少 {string.Join("、", missing.Select(name => "js/" + name))}。请修正目录或清除 ANIMA_TOOLS_DIR / COMFYUI_ANIMA_TOOLS；不会改用其他安装。");
            return path;
        }

        var custom = Path.Combine(Path.GetFullPath(comfyRoot), "custom_nodes");
        if (!Directory.Exists(custom)) return null;
        var directories = Directory.EnumerateDirectories(custom).Where(IsEnabledCandidate).OrderBy(path => path, PathComparer).ToArray();
        var matches = directories.Where(path => MissingFiles(path).Length == 0).Select(PhysicalPath).Distinct(PathComparer).ToArray();
        if (matches.Length > 1) throw new InvalidDataException("找到多个有效的 Anima Tools 目录：" + string.Join("；", matches) + "。请通过 ANIMA_TOOLS_DIR 指定一个目录，或移走重复安装；不会自动选择或重复安装。");
        if (matches.Length == 1) return matches[0];

        var incomplete = directories.Where(path =>
            new[] { NodeId, NodeId + "-main", NodeId + "-master" }.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            || DataFiles.Any(name => File.Exists(Path.Combine(path, "js", name)))).ToArray();
        if (incomplete.Length > 0) throw new InvalidDataException("Anima Tools 目录不完整：" + string.Join("；", incomplete.Select(path => path + "（缺少 " + string.Join("、", MissingFiles(path).Select(name => "js/" + name)) + "）")) + "。请修复或移走该目录；不会在旁边安装副本。");
        return null;
    }

    private static string[] MissingFiles(string path) => DataFiles.Where(name => !File.Exists(Path.Combine(path, "js", name))).ToArray();

    private static bool IsEnabledCandidate(string path)
    {
        var name = Path.GetFileName(path);
        return !name.StartsWith('.') && !new[] { ".disabled", ".anima-installing", ".extracting" }.Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    private static string PhysicalPath(string path)
    {
        var full = Path.GetFullPath(path);
        var directory = new DirectoryInfo(full);
        if (directory.Exists && directory.LinkTarget is not null) full = directory.ResolveLinkTarget(true)?.FullName ?? full;
        return Path.TrimEndingDirectorySeparator(full);
    }
}
