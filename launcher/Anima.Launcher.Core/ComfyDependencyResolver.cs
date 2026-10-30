using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Anima.Launcher.Core;

public static class ComfyDependencyResolver
{
    private static readonly Regex IncludeLine = new(@"(?m)^(\s*(?:-r\s*|--requirement[= ]+|-c\s*|--constraint[= ]+))([^\r\n]+)$", RegexOptions.Compiled);
    public static IReadOnlyList<string> ExistingNodeRequirements(LauncherConfig config)
    {
        var custom = Path.Combine(config.ComfyRoot, "custom_nodes");
        if (!Directory.Exists(custom)) return [];
        return Directory.EnumerateDirectories(custom).Where(path => !Path.GetFileName(path).StartsWith('.')
            && !Path.GetFileName(path).EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
            && (File.GetAttributes(path) & FileAttributes.Hidden) == 0)
            .Select(path => Path.Combine(path, "requirements.txt")).Where(File.Exists).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string? IncludePath(string source, string value)
    {
        value = Regex.Replace(value, @"\s+#.*$", "").Trim().Trim('"', '\'');
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http") return null;
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, value));
    }

    public static IReadOnlyList<string> RequirementFiles(IEnumerable<string> requirements)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Read(string path)
        {
            path = Path.GetFullPath(path);
            if (!files.Add(path)) return;
            foreach (Match include in IncludeLine.Matches(File.ReadAllText(path)))
                if (IncludePath(path, include.Groups[2].Value) is { } child) Read(child);
        }
        foreach (var file in requirements) Read(file);
        return files.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public static string NormalizeName(string name) => Regex.Replace(name, "[-_.]+", "-").ToLowerInvariant();
    public static string[] MergeConstraints(IEnumerable<string> installed, JsonElement report)
    {
        var pins = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in installed)
        {
            var split = line.Split("==", 2);
            if (split.Length == 2 && Regex.IsMatch(split[0], "^[A-Za-z0-9_.-]+$") && Regex.IsMatch(split[1], "^[A-Za-z0-9.+!-]+$"))
                pins[NormalizeName(split[0])] = split[1];
        }
        foreach (var item in report.GetProperty("install").EnumerateArray())
        {
            var metadata = item.GetProperty("metadata");
            pins[NormalizeName(metadata.GetProperty("name").GetString()!)] = metadata.GetProperty("version").GetString()!;
        }
        return pins.Select(pair => pair.Key + "==" + pair.Value).ToArray();
    }

    public static string NodeRequirementsIdentity(IEnumerable<string> requirements, string? sam2Commit = null,
        IReadOnlyDictionary<string, string>? logicalPaths = null)
    {
        var files = RequirementFiles(requirements);
        if (files.Count == 0) return "";
        string Logical(string path)
        {
            if (logicalPaths is null) return path;
            foreach (var pair in logicalPaths.OrderByDescending(pair => pair.Key.Length))
            {
                var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pair.Key));
                if (path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return Path.GetFullPath(Path.Combine(pair.Value, Path.GetRelativePath(root, path)));
            }
            return path;
        }
        var inputs = files.Select(path => (Path: Logical(path), Text: File.ReadAllText(path))).OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (inputs.Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != inputs.Length)
            throw new InvalidDataException("节点预检路径映射发生冲突。");
        var input = string.Join("\n", inputs.Select(item => item.Path + "\n" + item.Text)) + "\nSAM2=" + sam2Commit;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }

    public static string PrepareNodeRequirements(string path, string directory, string? sam2Commit, string? sam2Directory = null)
    {
        var files = RequirementFiles([path]);
        if (sam2Commit is null || !files.Any(file => Installer.PinSam2(File.ReadAllText(file), sam2Commit) is not null)) return path;
        Directory.CreateDirectory(directory);
        // Rewrite the whole include graph only when SAM2 needs pinning. Absolute include paths
        // preserve its original meaning, including nested files and cyclic references.
        var destinations = files.ToDictionary(file => file, file => Path.Combine(directory,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file)))[..16] + ".txt"), StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var content = File.ReadAllText(file);
            var pinned = Installer.PinSam2(content, sam2Commit) ?? content;
            if (sam2Directory is not null)
                pinned = pinned.Replace("SAM-2 @ git+" + DependencySources.Sam2Url + "@" + sam2Commit,
                    "SAM-2 @ " + new Uri(Path.GetFullPath(sam2Directory) + Path.DirectorySeparatorChar).AbsoluteUri);
            pinned = IncludeLine.Replace(pinned, match => IncludePath(file, match.Groups[2].Value) is { } child
                ? match.Groups[1].Value + '"' + destinations[child] + '"' : match.Value);
            File.WriteAllText(destinations[file], pinned);
        }
        return destinations[Path.GetFullPath(path)];
    }

    public static async Task<ComfyDependencySnapshot> ResolveAsync(LauncherConfig config, ComfyDependencySnapshot snapshot,
        string cacheRoot, CancellationToken ct, Action<string>? log = null, IEnumerable<string>? nodeRequirements = null,
        IDictionary<string, string>? environment = null, string? sam2Commit = null, IReadOnlyDictionary<string, string>? logicalPaths = null,
        Action<DependencyProgress>? dependencyProgress = null)
    {
        Safety.Commit(snapshot.ComfyCommit);
        var reporter = new DependencyProgressReporter(dependencyProgress, log);
        reporter.Phase("resolving", "正在检查已安装依赖与固定版本");
        var inventory = await Commands.Run(config.Python, ["-B", "-c", "import sys,importlib.metadata as m; print(sys.version.split()[0]); print(chr(10).join(sorted(d.metadata['Name']+'=='+d.version for d in m.distributions() if d.metadata['Name'])))"], config.ComfyRoot, null, ct, timeout: TimeSpan.FromSeconds(30));
        var lines = inventory.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var gpu = lines.Skip(1).Where(line => Regex.IsMatch(line, @"^(torch|torchvision|torchaudio)==", RegexOptions.IgnoreCase)).ToArray();
        var identity = Path.GetFullPath(config.Python).ToUpperInvariant() + "\n" + lines[0] + "\n" + string.Join("\n", gpu.Order());
        var additional = (nodeRequirements ?? []).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var nodeIdentity = NodeRequirementsIdentity(additional, sam2Commit, logicalPaths);
        if (snapshot.Resolved && snapshot.NodeRequirementsIdentity == nodeIdentity)
        {
            if (snapshot.PythonIdentity != identity) throw new InvalidOperationException("Python 或 GPU 版本已变化，请重新检查依赖计划。");
            DependencyPlans.ValidateEnvironment(snapshot, lines.Skip(1));
            reporter.Complete("已复用固定依赖计划");
            return snapshot;
        }
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity + "\n" + snapshot.ComfyCommit + "\n" + snapshot.RequirementsText + "\n" + snapshot.ManagerRequirementsText
            + (nodeIdentity.Length == 0 ? "" : "\n" + nodeIdentity)))).ToLowerInvariant();
        var root = Path.Combine(cacheRoot, "dependency-plans", key);
        var saved = Path.Combine(root, "resolved.json");
        // Older installers placed both plans and pip downloads one extra level down. Reuse
        // their frozen plan rather than resolving the same reviewed inputs a second time.
        var legacySaved = Path.Combine(cacheRoot, "dependencies", "dependency-plans", key, "resolved.json");
        var existingPath = File.Exists(saved) ? saved : legacySaved;
        if (File.Exists(existingPath))
        {
            var existing = JsonFile.Read<ComfyDependencySnapshot>(existingPath);
            if (!existing.Resolved || existing.PythonIdentity != identity || existing.ComfyCommit != snapshot.ComfyCommit
                || existing.NodeRequirementsIdentity != nodeIdentity)
                throw new InvalidDataException("已有依赖快照身份不匹配，已保留原文件。");
            try
            {
                DependencyPlans.ValidateEnvironment(existing, lines.Skip(1));
                reporter.Complete("已复用缓存中的固定依赖计划");
                return existing;
            }
            catch (InvalidOperationException) when (!snapshot.Resolved)
            {
                // A new review may follow a deliberate environment change. Keep the old approved plan,
                // and give the new baseline its own cache key instead of asking to recheck forever.
                key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key + "\n" + string.Join("\n", lines.Skip(1))))).ToLowerInvariant();
                root = Path.Combine(cacheRoot, "dependency-plans", key);
                saved = Path.Combine(root, "resolved.json");
                if (File.Exists(saved))
                {
                    var reviewed = JsonFile.Read<ComfyDependencySnapshot>(saved);
                    if (reviewed.PythonIdentity != identity || reviewed.NodeRequirementsIdentity != nodeIdentity || reviewed.ComfyCommit != snapshot.ComfyCommit)
                        throw new InvalidDataException("新检查的依赖快照身份不符。");
                    DependencyPlans.ValidateEnvironment(reviewed, lines.Skip(1));
                    reporter.Complete("已复用当前环境的依赖计划");
                    return reviewed;
                }
            }
        }
        Directory.CreateDirectory(root);
        var requirements = Path.Combine(root, "requirements.txt");
        var manager = Path.Combine(root, "manager_requirements.txt");
        var constraints = Path.Combine(root, "gpu-constraints.txt");
        File.WriteAllText(requirements, snapshot.RequirementsText);
        File.WriteAllText(manager, snapshot.ManagerRequirementsText);
        File.WriteAllLines(constraints, gpu.Length > 0 ? gpu : snapshot.Constraints.Where(line => Regex.IsMatch(line, @"^(torch|torchvision|torchaudio)==", RegexOptions.IgnoreCase)));
        string? sam2Directory = null;
        if (sam2Commit is not null && RequirementFiles(additional).Any(file => Installer.PinSam2(File.ReadAllText(file), sam2Commit) is not null))
        {
            reporter.Phase("preparing", "正在准备固定版本的 SAM-2 源码");
            sam2Directory = await DependencySources.PrepareAsync(DependencySources.Sam2Url, sam2Commit, cacheRoot, config, ct, reporter.Log, environment);
        }
        var additionalFiles = additional.Select(path => PrepareNodeRequirements(path, Path.Combine(root, "nodes"), sam2Commit, sam2Directory)).ToArray();
        var needsGit = RequirementFiles(additionalFiles).Any(path => File.ReadAllText(path).Contains("git+https://github.com/", StringComparison.OrdinalIgnoreCase));
        var reportPath = Path.Combine(root, "report.json");
        using var temporary = InstallationTemporaryDirectory.Enter(Path.GetDirectoryName(Path.GetFullPath(cacheRoot))!);
        var offline = environment?.TryGetValue("PIP_NO_INDEX", out var noIndex) == true && noIndex == "1";
        var indexes = offline ? DownloadSources.PipIndexes(config) : await DependencyPackageSources.SelectIndexesAsync(config,
            new[] { requirements, manager }.Concat(additionalFiles), ct, reporter.Log);
        var result = await WithPackageSourcesAsync(indexes,
            needsGit ? DownloadSources.GitCandidates("https://github.com/", config) : new[] { "https://github.com/" }, async (index, gitSource) =>
        {
            reporter.Phase("resolving", "正在解析 ComfyUI、Manager 与节点依赖", index);
            reporter.Log("解析当前稳定版依赖（保留已安装 GPU 运行时）：" + index);
            var env = environment is null ? new Dictionary<string, string>() : new Dictionary<string, string>(environment);
            env["PIP_INDEX_URL"] = index; env["PIP_EXTRA_INDEX_URL"] = ""; env["PIP_CONSTRAINT"] = constraints;
            env["PIP_CONFIG_FILE"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
            env["PIP_CACHE_DIR"] = Path.Combine(cacheRoot, "pip"); env["TEMP"] = temporary.Path; env["TMP"] = temporary.Path;
            Installer.ConfigureGitEnvironment(env, gitSource);
            NetworkPolicySnapshot.Capture(config).AddEnvironment(env, new Uri(index));
            var arguments = new List<string> { "-m", "pip", "install", "--dry-run", "--disable-pip-version-check", "--timeout", "20", "--retries", "1", "--report", reportPath,
                "--index-url", index, "-r", requirements, "-r", manager };
            if (additionalFiles.Where((file, i) => file != additional[i]).Any()) arguments.Add("--no-build-isolation");
            foreach (var file in additionalFiles) { arguments.Add("-r"); arguments.Add(file); }
            await Commands.Run(config.Python, arguments, config.ComfyRoot, reporter.Log, ct, env);
            using var report = JsonDocument.Parse(File.ReadAllText(reportPath));
            var pins = MergeConstraints(lines.Skip(1), report.RootElement);
            foreach (var pin in gpu) if (!pins.Contains(pin, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("依赖解析试图改变已有 GPU 版本，已中止。");
            var packages = await DependencyPlans.ReadReportAsync(report.RootElement, ct, sam2Directory, sam2Commit);
            var resolved = snapshot with { Constraints = pins, Resolved = true, PythonIdentity = identity, NodeRequirementsIdentity = nodeIdentity,
                PlanVersion = 1, PackageIndex = index, GitSource = gitSource, EnvironmentPackages = lines.Skip(1).ToArray(), Packages = packages,
                TransportIdentity = DependencyPlans.TransportIdentity(config) };
            JsonFile.Write(saved, resolved);
            return resolved;
        }, ct, log, config);
        reporter.Complete("依赖解析完成，已固定版本与文件身份");
        return result;
    }

    public static async Task<T> WithPackageSourcesAsync<T>(IEnumerable<string> indexes, IEnumerable<string> gitSources,
        Func<string, string, Task<T>> action, CancellationToken ct, Action<string>? log = null, LauncherConfig? config = null)
    {
        Exception? last = null;
        var packageCandidates = indexes.Distinct().ToArray();
        var gitCandidates = gitSources.Distinct().ToArray();
        var packageIndex = 0;
        var gitIndex = 0;
        while (packageIndex < packageCandidates.Length && gitIndex < gitCandidates.Length)
        {
            ct.ThrowIfCancellationRequested();
            try { return await action(packageCandidates[packageIndex], gitCandidates[gitIndex]); }
            catch (Exception ex) when (!ct.IsCancellationRequested && (Installer.IsSourceFailure(ex)
                || config is not null && Installer.IsPublicGitMirrorFailure(ex, gitCandidates[gitIndex], config)))
            {
                last = ex;
                var gitFailure = config is not null && Installer.IsPublicGitMirrorFailure(ex, gitCandidates[gitIndex], config)
                    || ex is CommandFailureException command && command.Kind != ProcessFailureKind.PackageUnavailable
                    && (command.Executable.StartsWith("git", StringComparison.OrdinalIgnoreCase)
                        || Regex.IsMatch(command.StandardError, @"(?im)(?:fatal:|unable to access|git (?:clone|fetch).*did not run successfully)"));
                log?.Invoke((gitFailure ? "Git 来源不可用，切换 Git 来源：" : "Python 包来源不可用，切换包来源：") + DiagnosticRedaction.Text(ex.Message));
                if (gitFailure) gitIndex++; else packageIndex++;
            }
        }
        throw new InvalidOperationException("当前稳定版依赖无法在保留 GPU 版本的条件下解析。", last);
    }
}
