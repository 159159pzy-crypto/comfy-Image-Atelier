using System.Text.Json;

namespace Anima.Launcher.Core;

public static class TaggerRuntime
{
    public const string NodeId = "ComfyUI-WD-Timm-Tagger";
    public static string? ResolveNode(string root)
    {
        var nodes = Path.Combine(root, "custom_nodes");
        if (!Directory.Exists(nodes)) return null;
        var matches = Directory.EnumerateDirectories(nodes).Where(path =>
        {
            var name = Path.GetFileName(path);
            if (name.StartsWith('.') || name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".anima-installing", StringComparison.OrdinalIgnoreCase)) return false;
            var code = Path.Combine(path, "nodes.py");
            if (!File.Exists(code) || !File.Exists(Path.Combine(path, "__init__.py"))) return false;
            var content = File.ReadAllText(code);
            return content.Contains("class WDTimmTagger") && content.Contains("WD_TAGGER_DIR");
        }).ToArray();
        if (matches.Length > 1) throw new InvalidDataException("检测到多个本地反推节点目录，请只保留一个启用的安装。");
        return matches.SingleOrDefault();
    }

    public static async Task<ProbeResult> Probe(LauncherConfig config, string script, CancellationToken ct)
    {
        var probe = await Inventory.Probe(config.ComfyRoot, config.Python, script, ct, config.ComfyArguments);
        if (!await ServiceManager.PortOpen(config.ComfyPort, ct)) return probe;
        // A reused ComfyUI may have been started with different arguments. Its runtime is authoritative.
        try
        {
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
            using var stats = JsonDocument.Parse(await client.GetStringAsync(config.ComfyUrl + "/system_stats", ct));
            using var folders = JsonDocument.Parse(await client.GetStringAsync(config.ComfyUrl + "/internal/folder_paths", ct));
            var target = ResolveDirectory(folders.RootElement, stats.RootElement);
            probe.Paths["wd_taggers"] = target is null ? [] : [target];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { probe.Paths["wd_taggers"] = []; }
        return probe;
    }

    /// <summary>Match the WebUI's resolve_tagger_directory: runtime path and absolute startup root must agree.</summary>
    public static string? ResolveDirectory(JsonElement folders, JsonElement stats)
    {
        try
        {
            var paths = folders.GetProperty("wd_taggers").EnumerateArray().Select(value => value.GetString()).ToArray();
            var args = stats.GetProperty("system").GetProperty("argv").EnumerateArray().Select(value => value.GetString()).ToArray();
            if (paths.Length == 0 || args.Length == 0 || args.Any(value => value is null)
                || paths.Any(value => value is null || !Path.IsPathFullyQualified(value))) return null;
            var unique = paths.Select(value => ResolvePath(value!)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (unique.Length != 1) return null;
            var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < args.Length; i++)
            {
                var parts = args[i]!.Split('=', 2);
                if (parts[0] is not ("--base-directory" or "--models-directory")) continue;
                var value = parts.Length == 2 ? parts[1] : args.ElementAtOrDefault(i + 1);
                if (string.IsNullOrEmpty(value) || !Path.IsPathFullyQualified(value) || overrides.ContainsKey(parts[0])) return null;
                overrides[parts[0]] = ResolvePath(value);
            }
            string models;
            if (overrides.TryGetValue("--models-directory", out var explicitModels)) models = explicitModels;
            else if (overrides.TryGetValue("--base-directory", out var explicitBase)) models = Path.Combine(explicitBase, "models");
            else if (Path.IsPathFullyQualified(args[0]!) && string.Equals(Path.GetExtension(args[0]), ".py", StringComparison.OrdinalIgnoreCase))
                models = Path.Combine(Path.GetDirectoryName(ResolvePath(args[0]!))!, "models");
            else return null;
            var expected = ResolvePath(Path.Combine(models, "wd_taggers"));
            return string.Equals(expected, unique[0], StringComparison.OrdinalIgnoreCase) ? expected : null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException or IOException or UnauthorizedAccessException)
        { return null; }
    }

    private static string ResolvePath(string path)
    {
        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full)!;
        foreach (var part in full[current.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo? info = Directory.Exists(current) ? new DirectoryInfo(current) : File.Exists(current) ? new FileInfo(current) : null;
            if (info is not null && (info.Attributes & FileAttributes.ReparsePoint) != 0)
                current = info.ResolveLinkTarget(true)?.FullName ?? throw new IOException("无法解析模型目录链接。");
        }
        return Path.TrimEndingDirectorySeparator(current);
    }
}
