using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Anima.Launcher.Core;

public sealed record InstallJournal(string Root, string Stage, string Status, DateTime UpdatedUtc, string? Error = null);
public sealed record NodeInstallPlan(Repository Repository, string Directory, bool PreserveExisting);

public sealed class Installer(string stateDir, RuntimeManifest manifest, Downloads downloads, Action<string> log, IProgress<DownloadProgress>? progress, Action<string>? stageChanged = null, Action<SetupProgress>? setupProgress = null)
{
    public static IReadOnlyList<NodeInstallPlan> PlanNodes(string root, RuntimeManifest runtime)
    {
        var directories = Inventory.ResolveNodeDirectories(root, runtime);
        return runtime.Nodes.Select(repo => new NodeInstallPlan(repo, directories[repo.Id], Directory.Exists(directories[repo.Id]) && !File.Exists(Path.Combine(directories[repo.Id], ".anima-revision.json")))).ToArray();
    }
    private static readonly Regex Sam2Line = new(@"^(?:sam[-_.]?2(?:\[[^\]]*\])?\s*@\s*)?git\+https://github\.com/facebookresearch/sam2(?:\.git)?(?:@[\w./\-]+)?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private string? git;
    private string? uv;
    private ComfyDependencySnapshot? activeDependencies;
    private Dictionary<string, string> Env(LauncherConfig config, string? destination = null)
    {
        var environment = new Dictionary<string, string>
        {
        ["PATH"] = (git is null ? "" : Path.GetDirectoryName(git) + Path.PathSeparator) + Environment.GetEnvironmentVariable("PATH"),
        ["UV_PYTHON_INSTALL_DIR"] = Path.Combine(InstallationPlanner.ResolveRuntimeRoot(config, stateDir), "python"),
        ["UV_PYTHON_BIN_DIR"] = Path.Combine(InstallationPlanner.ResolveRuntimeRoot(config, stateDir), "bin"),
        ["UV_CACHE_DIR"] = Path.Combine(InstallationPlanner.ResolveRuntimeRoot(config, stateDir), "cache", "uv"),
        ["TEMP"] = InstallationTemporaryDirectory.CurrentPath(InstallationPlanner.ResolveRuntimeRoot(config, stateDir)),
        ["TMP"] = InstallationTemporaryDirectory.CurrentPath(InstallationPlanner.ResolveRuntimeRoot(config, stateDir)),
        ["PIP_CACHE_DIR"] = Path.Combine(InstallationPlanner.ResolveRuntimeRoot(config, stateDir), "cache", "pip"),
        ["PIP_INDEX_URL"] = DownloadSources.PipIndexes(config)[0],
        ["PIP_EXTRA_INDEX_URL"] = "",
        ["PIP_CONFIG_FILE"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
        ["UV_DEFAULT_INDEX"] = DownloadSources.PipIndexes(config)[0],
        ["UV_EXTRA_INDEX_URL"] = "",
        ["UV_INDEX"] = "",
        ["UV_HTTP_TIMEOUT"] = "30",
        ["UV_HTTP_RETRIES"] = "1",
        ["GIT_HTTP_LOW_SPEED_LIMIT"] = "1024",
        ["GIT_HTTP_LOW_SPEED_TIME"] = "30",
        ["PIP_CONSTRAINT"] = Path.Combine(config.ComfyRoot, ".anima-constraints.txt"),
        ["COMFYUI_PATH"] = config.ComfyRoot,
        ["COMFYUI_MODEL_PATH"] = Path.Combine(config.ComfyRoot, "models"),
        ["GIT_CONFIG_COUNT"] = DownloadSources.GitCandidates("https://github.com/", config)[0] != "https://github.com/" ? "2" : "1",
        ["GIT_CONFIG_KEY_0"] = "core.longpaths",
        ["GIT_CONFIG_VALUE_0"] = "true",
        ["GIT_CONFIG_KEY_1"] = "url." + DownloadSources.GitCandidates("https://github.com/", config)[0] + ".insteadOf",
        ["GIT_CONFIG_VALUE_1"] = "https://github.com/"
        };
        NetworkPolicySnapshot.Capture(config).AddEnvironment(environment, destination is null ? null : new Uri(destination));
        return environment;
    }
    public static void ConfigureGitEnvironment(IDictionary<string, string> environment, string source)
    {
        environment["GIT_CONFIG_COUNT"] = source == "https://github.com/" ? "1" : "2";
        environment["GIT_CONFIG_KEY_0"] = "core.longpaths";
        environment["GIT_CONFIG_VALUE_0"] = "true";
        environment["GIT_CONFIG_KEY_1"] = "url." + source + ".insteadOf";
        environment["GIT_CONFIG_VALUE_1"] = "https://github.com/";
    }
    public static void ExtractZip(string zip, string destination, CancellationToken ct = default)
    {
        Directory.CreateDirectory(destination);
        using var archive = ZipFile.OpenRead(zip);
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var path = Safety.Under(destination, entry.FullName);
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("压缩包包含符号链接。");
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, true);
        }
    }
    private async Task<string> Tool(string name, LauncherConfig config, CancellationToken ct)
    {
        var tool = manifest.Tools.Single(t => t.Id == name);
        var runtime = InstallationPlanner.ResolveRuntimeRoot(config, stateDir);
        var target = ToolInstallation.DirectoryFor(runtime, tool);
        var exe = Safety.Under(target, tool.Executable);
        if (await Task.Run(() => ToolInstallation.IsReady(runtime, tool, ct), ct)) return exe;
        var zip = BundledTools.Contains(tool) && BundledTools.IsAvailable(tool)
            ? await BundledTools.MaterializeArchiveAsync(tool, runtime, ct)
            : await ArtifactCache.FetchAsync(runtime, tool, config, downloads, progress, ct);
        return await ToolInstallation.InstallVerifiedArchive(runtime, tool, zip, ct);
    }
    private async Task Stage(LauncherConfig config, string stageId, string name, Func<Task> action)
    {
        stageChanged?.Invoke(name);
        setupProgress?.Invoke(new(stageId, name, SetupStageStatus.Running));
        var file = Path.Combine(stateDir, "install-state.json");
        try
        {
            JsonFile.Write(file, new InstallJournal(config.ComfyRoot, name, "running", DateTime.UtcNow)); log(name);
            await action();
            InstallationPlanner.MarkStageComplete(config, manifest, stateDir, name);
            JsonFile.Write(file, new InstallJournal(config.ComfyRoot, name, "complete", DateTime.UtcNow));
            setupProgress?.Invoke(new(stageId, name, SetupStageStatus.Complete));
        }
        catch (Exception ex)
        {
            setupProgress?.Invoke(new(stageId, name + "：" + ex.Message,
                ex is OperationCanceledException ? SetupStageStatus.Paused : SetupStageStatus.Failed));
            JsonFile.Write(file, new InstallJournal(config.ComfyRoot, name, "interrupted", DateTime.UtcNow, ex.Message));
            throw;
        }
    }
    private async Task Clone(Repository repo, string target, LauncherConfig config, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Safety.Commit(repo.Commit);
        var marker = Path.Combine(target, ".anima-revision.json");
        if (File.Exists(marker))
        {
            await ValidateClone(repo, target, config, ct);
            return;
        }
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()) throw new IOException($"目标已存在，不能覆盖：{target}");
        var staging = target + ".anima-installing";
        Directory.CreateDirectory(staging);
        var stageMarker = Path.Combine(staging, ".anima-install.json");
        // A crash between writing the revision marker and moving the directory leaves
        // a completed clone behind; adopt it instead of demanding manual cleanup.
        var finalized = File.Exists(Path.Combine(staging, ".anima-revision.json")) && !File.Exists(stageMarker);
        if (!finalized)
        {
            if (!File.Exists(stageMarker) && Directory.EnumerateFileSystemEntries(staging).Any()) throw new IOException("临时安装目录已被其他内容占用。");
            if (File.Exists(stageMarker)) ValidateRepositoryIdentity(JsonFile.Read<Repository>(stageMarker), repo, staging);
            JsonFile.Write(stageMarker, repo);
            await Commands.Run(git!, ["init"], staging, log, ct);
            await WithSources("Git " + repo.Id, DownloadSources.GitCandidates(repo.Url, config), async source =>
            {
                var env = Env(config, source);
                ConfigureGitEnvironment(env, "https://github.com/");
                await GitFetchRecovery.Run(git!, ["fetch", "--depth", "1", source, repo.Commit], staging, log, ct, env);
            }, ct);
            await Commands.Run(git!, ["checkout", "--detach", "FETCH_HEAD"], staging, log, ct, Env(config));
            var actual = (await Commands.Run(git!, ["rev-parse", "HEAD"], staging, null, ct)).Trim();
            if (actual != repo.Commit) throw new InvalidDataException("Git 来源提交与锁定版本不同。");
            await WithSources("Git 子模块", DownloadSources.GitCandidates("https://github.com/", config), async source =>
            {
                var env = Env(config, source);
                ConfigureGitEnvironment(env, source);
                await Commands.Run(git!, ["submodule", "update", "--init", "--recursive"], staging, log, ct, env);
            }, ct);
            JsonFile.Write(Path.Combine(staging, ".anima-revision.json"), repo);
            await ValidateClone(repo, staging, config, ct);
            File.Delete(stageMarker);
        }
        else await ValidateClone(repo, staging, config, ct);
        ct.ThrowIfCancellationRequested();
        if (Directory.Exists(target)) Directory.Delete(target); // Only an empty target reaches this point.
        Directory.Move(staging, target);
    }

    private static void ValidateRepositoryIdentity(Repository saved, Repository requested, string path)
    {
        if (saved.Id != requested.Id || saved.Commit != requested.Commit || saved.Url != requested.Url)
            throw new InvalidDataException($"安装目录的仓库身份或锁定提交与当前清单不同，已保留：{path}。请使用原清单恢复或另选空目录。");
    }

    private async Task ValidateClone(Repository repo, string path, LauncherConfig config, CancellationToken ct)
    {
        ValidateRepositoryIdentity(JsonFile.Read<Repository>(Path.Combine(path, ".anima-revision.json")), repo, path);
        // rev-parse HEAD alone may accidentally inspect a containing repository.
        var root = (await Commands.Run(git!, ["rev-parse", "--show-toplevel"], path, null, ct)).Trim();
        if (!Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"目录不是独立的受管理 Git 仓库：{path}");
        var head = (await Commands.Run(git!, ["rev-parse", "HEAD"], path, null, ct)).Trim();
        if (head != repo.Commit) throw new IOException($"受管理仓库的版本已被手动更改：{path}。请导入为现有环境或另选空目录。");
        var changes = await Commands.Run(git!, ["status", "--porcelain", "--untracked-files=no", "--ignore-submodules=untracked"], path, null, ct);
        if (!string.IsNullOrWhiteSpace(changes))
        {
            if (repo.Id != PromptAssistantCompatibility.NodeId || !PromptAssistantCompatibility.IsOnlyCompatibilityChange(changes))
                throw new IOException($"受管理仓库有源代码修改，已保留：{path}。请先备份处理后再继续安装。");
            await PromptAssistantCompatibility.CheckOrApply(config, stateDir, path, false, ct);
        }
        var submodules = await Commands.Run(git!, ["submodule", "status", "--recursive"], path, null, ct);
        if (submodules.Split('\n', StringSplitOptions.RemoveEmptyEntries).Any(line => line[0] != ' '))
            throw new InvalidDataException($"子模块尚未完成或与锁定提交不同，已保留：{path}。请先恢复子模块后继续安装。");
    }
    /// <summary>Pin any SAM2 source line in a requirements file to the locked commit. Returns the rewritten content, or null when no SAM2 line exists.</summary>
    public static string? PinSam2(string content, string commit)
    {
        var found = false;
        // Select is lazy: materialize before reading `found`, or this always returns null.
        var lines = content.Replace("\r\n", "\n").Split('\n').Select(line =>
        {
            if (!Sam2Line.IsMatch(line.Trim())) return line;
            found = true;
            return "SAM-2 @ git+https://github.com/facebookresearch/sam2@" + commit;
        }).ToArray();
        return found ? string.Join("\n", lines) : null;
    }
    private bool ManagedRoot(LauncherConfig config) => File.Exists(Path.Combine(config.ComfyRoot, ".anima-revision.json"));
    /// <summary>The pip constraint file mirrors the full lock for managed installs and freezes only the imported torch ABI otherwise.</summary>
    public static string[] ConstraintsFor(bool managedRoot, RuntimeManifest manifest, string importedPins) => managedRoot ? manifest.PythonConstraints : importedPins.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private void EnsureConstraints(LauncherConfig config, string importedPins)
    {
        var file = Path.Combine(config.ComfyRoot, ".anima-constraints.txt");
        File.WriteAllLines(file, activeDependencies?.Constraints ?? ConstraintsFor(ManagedRoot(config), manifest, importedPins));
    }
    private async Task Pip(LauncherConfig config, IEnumerable<string> args, CancellationToken ct)
    {
        var baseArgs = new List<string> { "-m", "pip", "install", "--disable-pip-version-check" };
        var arguments = args.ToArray();
        for (var i = 0; i + 1 < arguments.Length; i++)
        {
            if (arguments[i] != "-r") continue;
            var requirements = arguments[i + 1];
            var rewritten = ComfyDependencyResolver.PrepareNodeRequirements(requirements,
                Path.Combine(InstallationPlanner.ResolveRuntimeRoot(config, stateDir), "cache", "requirements"), manifest.Sam2Commit);
            if (rewritten != requirements)
            {
                arguments[i + 1] = rewritten;
                if (!baseArgs.Contains("--no-build-isolation")) baseArgs.Add("--no-build-isolation");
            }
        }
        await WithSources("Python 包", DownloadSources.PipIndexes(config), async source =>
        {
            var env = Env(config, source);
            env["PIP_INDEX_URL"] = source;
            env["UV_DEFAULT_INDEX"] = source;
            var requirementsFiles = arguments.Where((arg, i) => i > 0 && arguments[i - 1] == "-r");
            var needsGit = arguments.Any(arg => arg.StartsWith("git+https://github.com/", StringComparison.OrdinalIgnoreCase))
                || ComfyDependencyResolver.RequirementFiles(requirementsFiles).Any(path => File.ReadAllText(path).Contains("git+https://github.com/", StringComparison.OrdinalIgnoreCase));
            if (needsGit)
                await WithSources("Git Python 依赖", DownloadSources.GitCandidates("https://github.com/", config), async gitSource =>
                {
                    ConfigureGitEnvironment(env, gitSource);
                    await Commands.Run(config.Python, baseArgs.Concat(["--timeout", "20", "--retries", "1"]).Concat(arguments), config.ComfyRoot, log, ct, env);
                }, ct);
            else await Commands.Run(config.Python, baseArgs.Concat(["--timeout", "20", "--retries", "1"]).Concat(arguments), config.ComfyRoot, log, ct, env);
        }, ct);
    }

    public static bool IsSourceFailure(Exception error)
    {
        if (error is CommandFailureException command) return command.Kind is ProcessFailureKind.Transport or ProcessFailureKind.ServerUnavailable
            or ProcessFailureKind.RateLimited or ProcessFailureKind.MissingResource or ProcessFailureKind.PackageUnavailable;
        if (error is TimeoutException or HttpRequestException) return true;
        if (error is not InvalidOperationException) return false;
        var message = error.Message.ToLowerInvariant();
        if (new[] { "no space left", "not enough space", "disk full", "permission denied", "access is denied", "being used by another process",
            "filename too long", "file name too long", "winerror 206", "checkout failed", "resolutionimpossible", "conflicting dependencies", "dependency conflict" }.Any(message.Contains)) return false;
        // A requirement's https URL is not an HTTP error. pip often repeats that URL in
        // local build failures; only transport evidence permits trying another mirror.
        return new[] { "timed out", "timeout", "connection", "resolve host", "name resolution", "network is unreachable", "ssl", "tls",
            "could not find a version", "no matching distribution", "remote end hung", "early eof", "fetch failed", "failed to download", "not our ref" }.Any(message.Contains)
            || Regex.IsMatch(message, @"(?:http(?: error)?(?: status(?: code)?)?\s*[:=]?\s*|returned error:\s*|status code:?\s*)[45]\d\d\b|\b[45]\d\d\s+(?:client|server)\s+error\b");
    }

    private async Task WithSources(string name, IEnumerable<string> sources, Func<string, Task> action, CancellationToken ct)
    {
        var candidates = sources.Distinct().ToArray();
        for (var i = 0; i < candidates.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            log($"{name} · 来源：{candidates[i]}");
            try { await action(candidates[i]); return; }
            catch (Exception ex) when (!ct.IsCancellationRequested && i + 1 < candidates.Length && IsSourceFailure(ex))
            {
                log($"{name} 当前来源不可用，切换下一来源：{DiagnosticRedaction.Text(ex.Message)}");
                await Task.Delay(ex is CommandFailureException { Kind: ProcessFailureKind.RateLimited } ? TimeSpan.FromSeconds(15)
                    : TimeSpan.FromSeconds(Math.Min(8, 1 << i)), ct);
            }
        }
    }

    private async Task InstallPython(LauncherConfig config, CancellationToken ct)
    {
        var runtimeRoot = InstallationPlanner.ResolveRuntimeRoot(config, stateDir);
        var runtime = PythonRuntimeInstallation.Resolve(manifest);
        var archive = await ArtifactCache.FetchAsync(runtimeRoot, runtime.Artifact, config, downloads, progress, ct);
        var installedPython = await PythonRuntimeInstallation.InstallAsync(uv!, runtimeRoot, runtime, archive, log, ct, Env(config));
        config.Python = Path.Combine(config.ComfyRoot, ".venv", "Scripts", "python.exe");
        config.PythonExplicit = false;
        // A failed --seed can leave python.exe but no pip behind; rerun seeding on retry.
        await WithSources("Python 基础包", DownloadSources.PipIndexes(config), async source =>
        {
            var env = Env(config, source);
            env["UV_DEFAULT_INDEX"] = source;
            env["PIP_INDEX_URL"] = source;
            env["UV_PYTHON_DOWNLOADS"] = "never";
            env["UV_PYTHON"] = installedPython;
            if (!File.Exists(config.Python))
                await Commands.Run(uv!, ["venv", "--seed", "--no-config", "--no-python-downloads", "--python", installedPython, Path.Combine(config.ComfyRoot, ".venv")], stateDir, log, ct, env);
            else
            {
                try { await Commands.Run(config.Python, ["-m", "pip", "--version"], config.ComfyRoot, null, ct, env); }
                catch (InvalidOperationException) { await Commands.Run(uv!, ["pip", "install", "--python", config.Python, "pip"], stateDir, log, ct, env); }
            }
        }, ct);
    }

    public async Task RepairDependencies(LauncherConfig config, ComfyDependencySnapshot? dependencies, CancellationToken ct)
    {
        config.Validate();
        _ = ComfyUpdateCoordinator.IndependentPythonDirectory(config);
        manifest = InstallationPlanner.PreserveExistingNodeVersions(config.ComfyRoot, manifest);
        await new ComfyUpdateCoordinator(stateDir, git ?? "git.exe").EnsureStoppedAsync(config, ct);
        if (await ServiceManager.PortOpen(config.ComfyPort, ct) || OwnedComfyRunning())
            throw new IOException("修复依赖前请先停止此环境的 ComfyUI，避免运行中改动 Python 包。");
        Directory.CreateDirectory(stateDir);
        var runtime = InstallationPlanner.ResolveRuntimeRoot(config, stateDir);
        using var temporary = InstallationTemporaryDirectory.Enter(runtime);
        log("安装临时目录：" + temporary.Path);
        git = await ResolveGit(config, ct);
        var gpuPins = await Commands.Run(config.Python, ["-B", "-c", "import importlib.metadata as m; print(chr(10).join(n+'=='+m.version(n) for n in ['torch','torchvision','torchaudio'] if m.packages_distributions().get(n)))"], config.ComfyRoot, null, ct);
        activeDependencies = dependencies;
        await InstallCombinedDependencies(config, gpuPins, requiredManager: true, ct);
        await CompleteNodeInstallers(config, PlanNodes(config.ComfyRoot, manifest).Where(node => Directory.Exists(node.Directory)), ct);
        await RepairOptionalDependencies(config, ct);
        await Commands.Run(config.Python, ["-m", "pip", "check"], config.ComfyRoot, log, ct);
    }

    private async Task InstallCombinedDependencies(LauncherConfig config, string gpuPins, bool requiredManager, CancellationToken ct)
    {
        var requirements = Path.Combine(config.ComfyRoot, "requirements.txt");
        var manager = Path.Combine(config.ComfyRoot, "manager_requirements.txt");
        if (!File.Exists(manager))
        {
            const string message = "此版本未提供 Manager 依赖清单，请先更新 ComfyUI 稳定版。";
            if (requiredManager) throw new InvalidOperationException(message);
            log(message + " 已保留原环境，继续补齐其他依赖。");
        }
        var nodes = ComfyDependencyResolver.ExistingNodeRequirements(config);
        if (activeDependencies is null && RuntimeDependencies.NodeCommit(config.ComfyRoot) is { } commit)
            activeDependencies = new(commit, File.ReadAllText(requirements), File.Exists(manager) ? File.ReadAllText(manager) : "", []);
        // Resolve the complete graph with only the GPU ABI frozen. An old full core-only
        // lock must not prevent a compatible node from selecting a different numpy, etc.
        File.WriteAllLines(Path.Combine(config.ComfyRoot, ".anima-constraints.txt"), ConstraintsFor(false, manifest, gpuPins));
        if (ComfyDependencyResolver.RequirementFiles(nodes).Any(file => PinSam2(File.ReadAllText(file), manifest.Sam2Commit) is not null))
            await Pip(config, ["setuptools", "wheel"], ct);
        await Stage(config, "dependencies", "安装 ComfyUI 依赖", async () =>
        {
            if (activeDependencies is not null)
            {
                activeDependencies = await ComfyDependencyResolver.ResolveAsync(config, activeDependencies,
                    Path.Combine(InstallationPlanner.ResolveRuntimeRoot(config, stateDir), "cache"), ct, log, nodes, Env(config), manifest.Sam2Commit);
                EnsureConstraints(config, gpuPins);
            }
            else log("此环境没有可验证的 Git 提交；使用现有源码依赖联合安装并保留 GPU 版本，不复用依赖快照。");
            var args = new List<string> { "-r", requirements };
            if (File.Exists(manager)) { args.Add("-r"); args.Add(manager); }
            foreach (var node in nodes) { args.Add("-r"); args.Add(node); }
            await Pip(config, args, ct);
        });
        if (File.Exists(manager))
            await Stage(config, "dependencies", "安装并验证 Manager 依赖", () => RuntimeDependencies.ValidateManager(config, ct));
    }

    private async Task CompleteNodeInstallers(LauncherConfig config, IEnumerable<NodeInstallPlan> nodes, CancellationToken ct)
    {
        var probe = await RuntimeDependencies.Probe(config, ct);
        var commit = activeDependencies?.ComfyCommit ?? RuntimeDependencies.NodeCommit(config.ComfyRoot) ?? manifest.Comfy.Commit;
        foreach (var node in nodes)
        {
            await Stage(config, "nodes", "验证节点 " + node.Repository.Id, async () =>
            {
                var complete = Path.Combine(node.Directory, ".anima-dependencies-complete");
                var install = Path.Combine(node.Directory, "install.py");
                if (!node.PreserveExisting && File.Exists(install) && !RuntimeDependencies.NodeReceiptMatches(complete, config, node.Directory, commit))
                {
                    await ValidateClone(node.Repository, node.Directory, config, ct);
                    await Commands.Run(config.Python, [install], node.Directory, log, ct, Env(config));
                }
                JsonFile.Write(complete, RuntimeDependencies.CreateNodeReceipt(config, node.Directory, probe, commit));
            });
        }
    }

    public async Task RepairOptionalDependencies(LauncherConfig config, CancellationToken ct)
    {
        _ = ComfyUpdateCoordinator.IndependentPythonDirectory(config);
        await new ComfyUpdateCoordinator(stateDir, git ?? "git.exe").EnsureStoppedAsync(config, ct);
        if (await ServiceManager.PortOpen(config.ComfyPort, ct) || OwnedComfyRunning())
            throw new IOException("修复可选依赖前请先停止 ComfyUI。");
        var runtime = InstallationPlanner.ResolveRuntimeRoot(config, stateDir);
        using var temporary = InstallationTemporaryDirectory.Enter(runtime);
        var statusPath = Path.Combine(runtime, "optional-dependencies.json");
        try
        {
            var probe = await RuntimeDependencies.Probe(config, ct);
            if (probe.TritonImportable)
            {
                try
                {
                    await RuntimeDependencies.ValidateTriton(config, runtime, ct, log);
                    var detail = $"现有 Triton {probe.Triton} GPU kernel 验证通过，已保留原版本。";
                    log(detail);
                    JsonFile.Write(statusPath, new OptionalDependencyStatus("PatchTritonVAE", true, detail));
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not InsufficientSpaceException)
                { log("现有 Triton 验证失败，检查可用的锁定修复包：" + ex.Message); }
            }
            var wheel = RuntimeDependencies.SelectTriton(probe, out var reason);
            if (wheel is null)
            {
                log(reason);
                JsonFile.Write(statusPath, new OptionalDependencyStatus("PatchTritonVAE", false, reason));
                return;
            }
            // The existing package was absent or failed a real GPU kernel. Reinstall even
            // when its metadata already matches, so missing files are actually repaired.
            {
                var cache = Path.Combine(runtime, "cache", "wheels");
                Directory.CreateDirectory(cache);
                var destination = Path.Combine(cache, wheel.Filename);
                if (!await Downloads.Verify(destination, wheel.Size, wheel.Sha256, ct))
                {
                    if (File.Exists(destination)) File.Move(destination, destination + ".invalid-" + Guid.NewGuid().ToString("N"));
                    var requirement = Path.Combine(cache, "triton-" + wheel.Python + ".txt");
                    await File.WriteAllTextAsync(requirement, $"triton-windows=={wheel.Version} --hash=sha256:{wheel.Sha256}\n", ct);
                    await WithSources("Triton Windows", DownloadSources.PipIndexes(config), async source =>
                    {
                        var env = Env(config);
                        env["PIP_INDEX_URL"] = source;
                        env["PIP_CONSTRAINT"] = "";
                        await Commands.Run(config.Python, ["-m", "pip", "download", "--no-deps", "--only-binary=:all:", "--require-hashes", "--timeout", "20", "--retries", "1", "-r", requirement, "--dest", cache], config.ComfyRoot, log, ct, env);
                    }, ct);
                    if (!await Downloads.Verify(destination, wheel.Size, wheel.Sha256, ct)) throw new InvalidDataException("Triton wheel 大小或 SHA-256 不符。");
                }
                var installEnv = Env(config);
                installEnv["PIP_CONSTRAINT"] = "";
                await Commands.Run(config.Python, ["-m", "pip", "install", "--force-reinstall", "--no-deps", destination], config.ComfyRoot, log, ct, installEnv);
            }
            await RuntimeDependencies.ValidateTriton(config, runtime, ct, log);
            JsonFile.Write(statusPath, new OptionalDependencyStatus("PatchTritonVAE", true, "Triton GPU kernel 已验证；重启后检查节点加载。"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not InsufficientSpaceException)
        {
            var message = "可选节点 PatchTritonVAE 未就绪：" + ex.Message + "；基础生图不受影响，可在环境页重试修复。";
            log(message);
            JsonFile.Write(statusPath, new OptionalDependencyStatus("PatchTritonVAE", false, message));
        }
    }
    /// <summary>True when a ComfyUI owned by this launcher is still running; its venv must not be modified underneath it.</summary>
    private bool OwnedComfyRunning()
    {
        try
        {
            var file = Path.Combine(stateDir, "processes.json");
            if (!File.Exists(file)) return false;
            return JsonFile.Read<OwnedProcess[]>(file).Where(r => r.Role == "ComfyUI").Any(r =>
            {
                try { var p = Process.GetProcessById(r.Pid); return !p.HasExited && p.StartTime.ToUniversalTime().Ticks == r.StartTimeUtcTicks; }
                catch { return false; }
            });
        }
        catch { return false; }
    }
    private async Task<string> ResolveGit(LauncherConfig config, CancellationToken ct)
    {
        var found = Commands.Find("git.exe");
        if (found is not null)
        {
            try { await Commands.Run(found, ["--version"], stateDir, null, ct, timeout: TimeSpan.FromSeconds(30)); return found; }
            catch (Exception ex) when (ex is not OperationCanceledException) { log($"PATH 中的 Git 不可用（{ex.Message}），改用隔离工具。"); }
        }
        var resolved = await Tool("git", config, ct);
        await Commands.Run(resolved, ["--version"], stateDir, null, ct, timeout: TimeSpan.FromSeconds(30));
        return resolved;
    }
    public async Task Install(LauncherConfig config, bool fresh, CancellationToken ct)
    {
        // Resolve ambiguous/incomplete Anima Tools installations before Python discovery
        // or preflight, preserving the established fail-fast contract of this entrypoint.
        var target = fresh && !string.IsNullOrWhiteSpace(config.InstallRoot) ? Path.Combine(config.InstallRoot, "ComfyUI") : config.ComfyRoot;
        if (!string.IsNullOrWhiteSpace(target)) PlanNodes(target, manifest);
        var installed = await Install(InstallationPlanner.Create(config, manifest, stateDir, fresh), ct);
        config.InstallRoot = installed.InstallRoot;
        config.RuntimeRoot = installed.RuntimeRoot;
        config.ComfyRoot = installed.ComfyRoot;
        config.Python = installed.Python;
        config.PythonExplicit = installed.PythonExplicit;
        config.ManagedComfy = installed.ManagedComfy;
        config.ComfyArguments = installed.ComfyArguments;
    }
    public async Task<LauncherConfig> Install(InstallationPlan plan, CancellationToken ct)
    {
        plan = await InstallationPlanner.ResolveStableAsync(plan, manifest, stateDir, ct);
        manifest = plan.ResolvedRuntime ?? manifest;
        activeDependencies = plan.Dependencies;
        var config = plan.CreateConfig();
        var fresh = plan.Fresh;
        config.Validate();
        Safety.ValidateRuntime(manifest);
        if (string.IsNullOrWhiteSpace(config.ComfyRoot)) throw new InvalidDataException("请先选择 ComfyUI 目录：新建选空目录，导入选含 main.py 的目录。");
        config.ComfyRoot = Path.GetFullPath(config.ComfyRoot);
        // Resolve aliases before any external command or install mutation. An existing
        // ZIP install must never trigger a second clone into the canonical folder.
        var nodePlans = PlanNodes(config.ComfyRoot, manifest);
        await new ComfyUpdateCoordinator(stateDir, git ?? "git.exe").EnsureStoppedAsync(config, ct);
        if (await ServiceManager.PortOpen(config.ComfyPort, ct) || OwnedComfyRunning()) throw new IOException("安装依赖前请先停止此环境的 ComfyUI，避免运行过程中改动 Python 包。");
        var preflight = await InstallationPlanner.PreflightAsync(plan, ct);
        if (!preflight.CanInstall) throw new InvalidOperationException(string.Join(Environment.NewLine, preflight.Issues.Where(i => i.Blocking).Select(i => i.Message)));
        var runtimeDirectory = InstallationPlanner.ResolveRuntimeRoot(config, stateDir);
        Directory.CreateDirectory(runtimeDirectory);
        using var temporary = InstallationTemporaryDirectory.Enter(runtimeDirectory);
        log("安装临时目录：" + temporary.Path);
        Directory.CreateDirectory(Path.Combine(runtimeDirectory, "bin"));
        InstallationPlanner.PreserveCompletedStages(config, manifest, stateDir);
        var file = Path.Combine(stateDir, "install-state.json");
        if (File.Exists(file))
        {
            try { var last = JsonFile.Read<InstallJournal>(file); if (last.Status == "interrupted" && Path.GetFullPath(last.Root).Equals(config.ComfyRoot, StringComparison.OrdinalIgnoreCase)) log($"上次安装中断于「{last.Stage}」，本次从幂等步骤续跑。"); } catch { }
        }
        if (!fresh)
        {
            if (string.IsNullOrWhiteSpace(config.Python)) throw new InvalidDataException("未找到 Python，请先选择该环境的 python.exe。");
            _ = ComfyUpdateCoordinator.IndependentPythonDirectory(config);
            // Fail fast on a bad interpreter instead of at the first pip call.
            await Commands.Run(config.Python, ["--version"], config.ComfyRoot, log, ct, timeout: TimeSpan.FromSeconds(30));
        }
        await Stage(config, "tools", "准备隔离安装工具", async () => { git = await ResolveGit(config, ct); if (fresh) uv = await Tool("uv", config, ct); });
        var importedPins = "";
        if (fresh)
        {
            await Stage(config, "comfy", "拉取锁定版本的 ComfyUI", () => Clone(manifest.Comfy with { Url = config.CustomRepository.Length > 0 ? config.CustomRepository : manifest.Comfy.Url }, config.ComfyRoot, config, ct));
            if (activeDependencies is not null)
            {
                static string Normalize(string text) => text.Replace("\r\n", "\n").Trim();
                if (Normalize(File.ReadAllText(Path.Combine(config.ComfyRoot, "requirements.txt"))) != Normalize(activeDependencies.RequirementsText)
                    || Normalize(File.ReadAllText(Path.Combine(config.ComfyRoot, "manager_requirements.txt"))) != Normalize(activeDependencies.ManagerRequirementsText))
                    throw new InvalidDataException("检出的 ComfyUI 依赖文件与已检查的稳定版快照不同，已停止安装。");
            }
            await Stage(config, "python", "安装 Python 并创建独立虚拟环境", () => InstallPython(config, ct));
            config.ManagedComfy = true;
            EnsureConstraints(config, importedPins); // PIP_CONSTRAINT is injected into every pip call; the file must exist first.
            await Stage(config, "torch", "安装锁定 CUDA / PyTorch", async () =>
            {
                await Pip(config, ["setuptools", "wheel"], ct);
                var wheels = new List<string>();
                foreach (var wheel in manifest.TorchWheels)
                {
                    var destination = await ArtifactCache.FetchAsync(InstallationPlanner.ResolveRuntimeRoot(config, stateDir), wheel, config, downloads, progress, ct);
                    wheels.Add(destination);
                }
                await Pip(config, wheels, ct);
            });
            importedPins = await Commands.Run(config.Python, ["-B", "-c", "import importlib.metadata as m; print(chr(10).join(n+'=='+m.version(n) for n in ['torch','torchvision','torchaudio'] if m.packages_distributions().get(n)))"], config.ComfyRoot, null, ct);
            if (!config.LaunchArgumentsConfigured && !ComfyLaunchArguments.ManagerEnabled(config.ComfyArguments)) config.ComfyArguments.Add("--enable-manager");
        }
        else
        {
            // Adding a missing node must not silently replace an imported GPU runtime.
            importedPins = await Commands.Run(config.Python, ["-c", "import importlib.metadata as m; print(chr(10).join(n+'=='+m.version(n) for n in ['torch','torchvision','torchaudio'] if m.packages_distributions().get(n)))"], config.ComfyRoot, null, ct);
            File.WriteAllLines(Path.Combine(config.ComfyRoot, ".anima-constraints.txt"), ConstraintsFor(false, manifest, importedPins));
        }
        var custom = Path.Combine(config.ComfyRoot, "custom_nodes");
        Directory.CreateDirectory(custom);
        // Supported by Impact's installers and prestartup; avoid unselected model downloads.
        var skip = Path.Combine(custom, "skip_download_model");
        var hadSkip = File.Exists(skip);
        if (!hadSkip) File.WriteAllText(skip, "Model downloads are managed by Anima Random Studio.\n");
        foreach (var node in nodePlans)
        {
            var repo = node.Repository;
            var target = node.Directory;
            await Stage(config, "nodes", "安装节点 " + repo.Id, async () =>
            {
                if (node.PreserveExisting) log($"保留已有节点源码 {repo.Id}：{target}；检查并补齐声明的依赖。");
                else await Clone(repo, target, config, ct);
            });
        }
        await InstallCombinedDependencies(config, importedPins, fresh || ComfyLaunchArguments.ManagerEnabled(config.ComfyArguments), ct);
        await CompleteNodeInstallers(config, nodePlans, ct);
        await Stage(config, "nodes", "检查可选 Triton 加速依赖", () => RepairOptionalDependencies(config, ct));
        var assistantDirectory = PromptAssistantCompatibility.ResolveDirectory(config.ComfyRoot);
        if (assistantDirectory is not null)
            await Stage(config, "nodes", "验证提示词助手纯模板兼容", () => PromptAssistantCompatibility.CheckOrApply(config, stateDir, assistantDirectory, true, ct));
        setupProgress?.Invoke(new("nodes", "必要节点已准备就绪", SetupStageStatus.Complete));
        await Stage(config, "validation", "验证运行依赖", async () =>
        {
            await Commands.Run(config.Python, ["-c", "import aiohttp,yaml,torch; print('PyTorch:',torch.__version__); print('GPU:',torch.cuda.is_available())" + (fresh ? "; assert torch.cuda.is_available()" : "")], config.ComfyRoot, log, ct);
            if (fresh) await Commands.Run(config.Python, ["-m", "pip", "check"], config.ComfyRoot, log, ct);
        });
        InstallationPlanner.MarkStableInstallComplete(config, stateDir);
        return config;
    }
}
