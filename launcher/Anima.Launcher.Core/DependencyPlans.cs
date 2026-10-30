using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Anima.Launcher.Core;

public static class DependencyPlans
{
    public static string TransportIdentity(LauncherConfig config) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
        config.SourceId + "\n" + config.PipIndex + "\n" + config.NetworkMode + "\n" + config.ProxyAddress + "\n" + config.GitMirror))).ToLowerInvariant();
    public static async Task<string[]> InventoryAsync(LauncherConfig config, CancellationToken ct)
    {
        var output = await Commands.Run(config.Python, ["-B", "-c", "import importlib.metadata as m; print(chr(10).join(sorted(d.metadata['Name']+'=='+d.version for d in m.distributions() if d.metadata['Name'])))"],
            config.ComfyRoot, null, ct, timeout: TimeSpan.FromSeconds(30));
        return output.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public static Dictionary<string, string> Versions(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var pair = line.Split("==", 2);
            if (pair.Length != 2) throw new InvalidDataException("依赖版本记录无效。");
            var name = ComfyDependencyResolver.NormalizeName(pair[0]);
            if (result.TryGetValue(name, out var version) && version != pair[1]) throw new InvalidDataException("环境中有重复且冲突的包：" + name);
            result[name] = pair[1];
        }
        return result;
    }

    // Called only immediately after a launcher-owned node/optional installation has
    // stopped. Record its additions without accepting changes to the reviewed graph.
    public static ComfyDependencySnapshot RecordAuthorizedAdditions(ComfyDependencySnapshot snapshot, string[] current)
    {
        if (snapshot.PlanVersion != 1) return snapshot;
        var known = Versions(snapshot.EnvironmentPackages).Keys
            .Concat(snapshot.Packages.Select(p => ComfyDependencyResolver.NormalizeName(p.Name)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        ValidateEnvironment(snapshot, current.Where(line => known.Contains(ComfyDependencyResolver.NormalizeName(line.Split("==", 2)[0]))), installed: true);
        _ = Versions(current);
        return snapshot with { EnvironmentPackages = current };
    }

    /// <summary>Only changes explicitly authorized by the frozen plan may appear on resume.</summary>
    public static void ValidateEnvironment(ComfyDependencySnapshot snapshot, IEnumerable<string> current, bool installed = false)
    {
        if (snapshot.PlanVersion == 0) return;
        if (snapshot.PlanVersion != 1) throw new InvalidDataException("不支持此依赖下载计划版本。");
        var baseline = Versions(snapshot.EnvironmentPackages);
        var actual = Versions(current);
        var targets = snapshot.Packages.ToDictionary(p => ComfyDependencyResolver.NormalizeName(p.Name), p => p.Version, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in baseline)
        {
            if (targets.TryGetValue(pair.Key, out var next))
            {
                if (actual.TryGetValue(pair.Key, out var found) && found != pair.Value && found != next)
                    throw new InvalidOperationException("依赖计划检查后包版本发生变化，请重新检查：" + pair.Key);
            }
            else if (!actual.TryGetValue(pair.Key, out var found) || found != pair.Value)
                throw new InvalidOperationException("依赖计划检查后原环境发生变化，请重新检查：" + pair.Key);
        }
        foreach (var pair in actual)
            if (!baseline.ContainsKey(pair.Key) && (!targets.TryGetValue(pair.Key, out var next) || pair.Value != next))
                throw new InvalidOperationException("依赖计划检查后增加了其他包，请重新检查：" + pair.Key);
        if (installed)
            foreach (var pair in targets)
                if (!actual.TryGetValue(pair.Key, out var version) || version != pair.Value)
                    throw new InvalidDataException("依赖安装结果与固定版本不同：" + pair.Key + "==" + pair.Value);
        foreach (var gpu in new[] { "torch", "torchvision", "torchaudio" })
            if (baseline.TryGetValue(gpu, out var version) && (!actual.TryGetValue(gpu, out var found) || found != version))
                throw new InvalidOperationException("GPU 运行时发生变化，已中止：" + gpu);
    }

    public static async Task<DependencyPackage[]> ReadReportAsync(JsonElement report, CancellationToken ct,
        string? sam2Directory = null, string? sam2Commit = null)
    {
        if (!report.TryGetProperty("version", out var schema) || schema.GetString() != "1") throw new InvalidDataException("不支持的 pip 解析报告版本。");
        var packages = new List<DependencyPackage>();
        foreach (var item in report.GetProperty("install").EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            var metadata = item.GetProperty("metadata");
            var info = item.GetProperty("download_info");
            var url = info.GetProperty("url").GetString()!;
            var uri = new Uri(url);
            var name = ComfyDependencyResolver.NormalizeName(metadata.GetProperty("name").GetString()!);
            var version = metadata.GetProperty("version").GetString()!;
            if (!Regex.IsMatch(name, "^[a-z0-9][a-z0-9.-]*$") || !Regex.IsMatch(version, "^[A-Za-z0-9.+!-]+$")) throw new InvalidDataException("解析的包身份无效。");
            var filename = Uri.UnescapeDataString(uri.AbsolutePath.Split('/').Last());
            var hash = "";
            if (info.TryGetProperty("archive_info", out var archive) && archive.TryGetProperty("hashes", out var hashes) && hashes.TryGetProperty("sha256", out var sha)) hash = sha.GetString() ?? "";
            if (hash.Length > 0 && !Regex.IsMatch(hash, "^[a-fA-F0-9]{64}$")) throw new InvalidDataException("解析报告中的 SHA-256 无效。");
            var kind = filename.EndsWith(".whl", StringComparison.OrdinalIgnoreCase) ? "wheel" : "archive";
            var commit = ""; var sourceDirectory = ""; long size = 0;
            var subdirectory = info.TryGetProperty("subdirectory", out var subdir) ? subdir.GetString() ?? "" : "";
            if (subdirectory.Length > 0)
            {
                if (Path.IsPathRooted(subdirectory) || subdirectory.Contains('\\') || subdirectory.Split('/').Any(part => part is ".." or "." or ""))
                    throw new InvalidDataException("依赖源码子目录无效。");
            }
            if (info.TryGetProperty("vcs_info", out var vcs))
            {
                if (vcs.GetProperty("vcs").GetString() != "git") throw new InvalidDataException("暂不支持该源码版本控制类型。");
                kind = "git"; commit = vcs.GetProperty("commit_id").GetString()!; Safety.Commit(commit);
            }
            else if (info.TryGetProperty("dir_info", out _))
            {
                if (!uri.IsFile) throw new InvalidDataException("本地源码目录地址无效。");
                sourceDirectory = uri.LocalPath; kind = "directory";
                if (name == "sam-2" && sam2Directory is not null && Path.GetFullPath(sourceDirectory).TrimEnd(Path.DirectorySeparatorChar)
                    .Equals(Path.GetFullPath(sam2Directory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                { kind = "git"; url = DependencySources.Sam2Url; commit = sam2Commit!; Safety.Commit(commit); }
            }
            else if (uri.IsFile)
            {
                var path = uri.LocalPath;
                size = new FileInfo(path).Length;
                await using var input = File.OpenRead(path);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).ToLowerInvariant();
                if (hash.Length > 0 && !hash.Equals(actual, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("本地依赖文件校验不一致。");
                hash = actual;
            }
            else Safety.Https(url);
            packages.Add(new() { Name = name, Version = version, Kind = kind, Url = url, FileName = filename,
                Sha256 = hash.ToLowerInvariant(), Size = size, Sources = uri.Scheme == "https" ? [url] : [], Commit = commit,
                SourceDirectory = sourceDirectory, Subdirectory = subdirectory, Direct = item.TryGetProperty("is_direct", out var direct) && direct.GetBoolean() });
        }
        if (packages.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != packages.Count) throw new InvalidDataException("解析报告包含重复包。");
        return packages.ToArray();
    }
}
