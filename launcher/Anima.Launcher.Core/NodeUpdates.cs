using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using static Anima.Launcher.Core.ComfyUpdateCoordinator;

namespace Anima.Launcher.Core;

public sealed record NodeUpdateEntry(string Name, string Directory, string Url, string Branch,
    string BeforeSha, string TargetSha, bool CanUpdate, string Reason)
{
    public string StagedDirectory { get; init; } = "";
    public string StagedFingerprint { get; init; } = "";
}
public sealed record NodeUpdatePlan(string Root, string Python, string CoreCommit, NodeUpdateEntry[] Entries,
    ComfyDependencySnapshot? Dependencies, DateTimeOffset CreatedAt)
{
    public string Note { get; init; } = "";
    public string BeforeRequirementsIdentity { get; init; } = "";
    public string Sam2Commit { get; init; } = "";
    public bool CanApply => Dependencies?.Resolved == true && Entries.Any(entry => entry.CanUpdate);
}
public sealed record NodeUpdateBackup(string Root, string Python, string PythonDirectory, string CoreCommit,
    NodeUpdateEntry[] Entries, string Directory, string Status, DateTimeOffset CreatedAt,
    string ConfigurationJson, ComfyConfigurationEntry[] ConfigurationPaths, string? ConstraintsFile);

/// <summary>Explicit node-only transactions; ComfyUI and the GPU package versions remain fixed.</summary>
public sealed class NodeUpdateCoordinator(string stateDir, string git, Action<string>? log = null,
    Func<Repository, CancellationToken, Task<NodeRelease>>? resolveHead = null,
    Func<NodeUpdateEntry, CancellationToken, Task>? fetchCommit = null,
    Func<LauncherConfig, ComfyDependencySnapshot, IReadOnlyList<string>, IReadOnlyDictionary<string, string>, CancellationToken, Task<ComfyDependencySnapshot>>? resolveDependencies = null,
    Func<string, NodeUpdateEntry, CancellationToken, Task>? prepareSubmodules = null)
{
    private static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool Same(string a, string b) => Full(a).Equals(Full(b), StringComparison.OrdinalIgnoreCase);
    private static string SafeMessage(string message) => Regex.Replace(message, @"(?:https?://|git@)[^\s\""']+", "[来源地址]", RegexOptions.IgnoreCase);
    private void Log(string message) => log?.Invoke(SafeMessage(message));
    private string FileFor(LauncherConfig config, string kind) => Path.Combine(stateDir, kind,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Full(config.ComfyRoot).ToUpperInvariant()))).ToLowerInvariant() + ".json");
    private static IEnumerable<string> GitArguments(IEnumerable<string> arguments) =>
        new[] { "-c", "core.longpaths=true" }.Concat(arguments);
    private Task<string> Git(string root, IEnumerable<string> args, CancellationToken ct, bool progress = false,
        IDictionary<string, string>? environment = null) => Commands.Run(git, GitArguments(args), root, progress ? Log : null, ct, environment, TimeSpan.FromMinutes(3));
    public NodeUpdatePlan? LoadPlan(LauncherConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ComfyRoot) || string.IsNullOrWhiteSpace(config.Python)) return null;
        var file = FileFor(config, "node-update-plans");
        if (!File.Exists(file)) return null;
        var plan = JsonFile.Read<NodeUpdatePlan>(file);
        return Same(plan.Root, config.ComfyRoot) && Same(plan.Python, config.Python) ? plan : null;
    }
    public NodeUpdateBackup? Previous(LauncherConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ComfyRoot)) return null;
        var file = FileFor(config, "node-updates");
        return File.Exists(file) ? JsonFile.Read<NodeUpdateBackup>(file) : null;
    }
    private NodeUpdatePlan SavePlan(LauncherConfig config, NodeUpdatePlan plan) { JsonFile.Write(FileFor(config, "node-update-plans"), plan); return plan; }
    private void SaveBackup(LauncherConfig config, NodeUpdateBackup backup)
    { JsonFile.Write(Path.Combine(backup.Directory, "backup.json"), backup); JsonFile.Write(FileFor(config, "node-updates"), backup); }
    private void ClearPlan(LauncherConfig config)
    { var file = FileFor(config, "node-update-plans"); if (File.Exists(file)) File.Delete(file); }

    private static string ConfirmOrigin(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.Host != "github.com"
            || !Regex.IsMatch(uri.AbsolutePath, @"^/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/?$"))
            throw new InvalidOperationException("来源无法安全确认；仅自动更新不含凭据或查询参数的 GitHub HTTPS 仓库。");
        return value.Trim().TrimEnd('/');
    }
    private async Task<string> CoreCommit(LauncherConfig config, CancellationToken ct)
    {
        if (!Directory.Exists(Path.Combine(config.ComfyRoot, ".git"))) throw new InvalidOperationException("节点原地升级需要 Git 版 ComfyUI，以验证本体保持不变。");
        var result = (await Git(config.ComfyRoot, ["rev-parse", "HEAD"], ct)).Trim(); Safety.Commit(result); return result;
    }
    private async Task ValidateNode(LauncherConfig config, NodeUpdateEntry entry, string expected, CancellationToken ct)
    {
        var custom = Path.Combine(config.ComfyRoot, "custom_nodes");
        if (!Same(Path.GetDirectoryName(entry.Directory)!, custom)) throw new InvalidDataException("节点不属于当前环境。");
        CheckParents(config.ComfyRoot, entry.Directory); RejectLink(entry.Directory);
        if (!Directory.Exists(Path.Combine(entry.Directory, ".git"))) throw new InvalidOperationException("不是独立 Git 节点，已保留原目录。");
        RejectLink(Path.Combine(entry.Directory, ".git"));
        if (File.Exists(Path.Combine(entry.Directory, ".git", "objects", "info", "alternates"))) throw new InvalidOperationException("节点 Git 对象与外部仓库共享，无法完整备份。");
        if (!Same((await Git(entry.Directory, ["rev-parse", "--show-toplevel"], ct)).Trim(), entry.Directory)) throw new InvalidOperationException("节点不是独立 Git 根目录。");
        var head = (await Git(entry.Directory, ["rev-parse", "HEAD"], ct)).Trim();
        if (expected.Length > 0 && head != expected) throw new InvalidOperationException("检查后节点版本已变化，请重新检查。");
        var changes = await Git(entry.Directory, ["status", "--porcelain", "--untracked-files=no", "--ignore-submodules=untracked"], ct);
        if (!string.IsNullOrWhiteSpace(changes))
        {
            if (entry.Name != PromptAssistantCompatibility.NodeId || !PromptAssistantCompatibility.IsOnlyCompatibilityChange(changes))
                throw new InvalidOperationException("节点有本地源码修改，已跳过，未覆盖。");
            await PromptAssistantCompatibility.CheckOrApply(config, stateDir, entry.Directory, false, ct);
        }
    }

    public async Task<NodeUpdatePlan> CheckAsync(LauncherConfig config, RuntimeManifest runtime, CancellationToken ct)
    {
        config.Validate();
        var core = await CoreCommit(config, ct);
        var releases = new NodeReleases(resolveHead: resolveHead ?? ((repository, token) => NodeReleases.ResolveHeadWithGit(git, repository, stateDir, token)));
        var entries = new List<NodeUpdateEntry>();
        var custom = Path.Combine(config.ComfyRoot, "custom_nodes");
        foreach (var directory in Directory.Exists(custom) ? Directory.EnumerateDirectories(custom).Order(StringComparer.OrdinalIgnoreCase) : Enumerable.Empty<string>())
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(directory);
            if (name.StartsWith('.') || name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) || (File.GetAttributes(directory) & FileAttributes.Hidden) != 0) continue;
            var entry = new NodeUpdateEntry(name, directory, "", "", "", "", false, "");
            try
            {
                var marker = Path.Combine(directory, ".anima-revision.json");
                var managed = File.Exists(marker) ? JsonFile.Read<Repository>(marker) : null;
                entry = entry with { Name = managed?.Id ?? name };
                if (PromptAssistantCompatibility.ResolveDirectory(config.ComfyRoot) is { } assistant && Same(assistant, directory))
                    entry = entry with { Name = PromptAssistantCompatibility.NodeId };
                await ValidateNode(config, entry, "", ct);
                var before = (await Git(directory, ["rev-parse", "HEAD"], ct)).Trim();
                string origin;
                try { origin = (await Git(directory, ["config", "--get", "remote.origin.url"], ct)).Trim(); }
                catch (InvalidOperationException) { origin = managed?.Url ?? ""; }
                origin = ConfirmOrigin(origin);
                entry = entry with { BeforeSha = before, Url = origin };
                var repository = new Repository(entry.Name, origin, before, managed?.RequiredClasses ?? []);
                var release = resolveHead is null ? await releases.LatestAsync(repository, ct) : await resolveHead(repository, ct);
                var targetUrl = ConfirmOrigin(release.Url); Safety.Commit(release.Commit);
                entry = entry with { Url = targetUrl, Branch = release.Branch, TargetSha = release.Commit,
                    CanUpdate = release.Archived != true && before != release.Commit,
                    Reason = release.Archived == true ? "上游已归档，保留当前版本。" : before == release.Commit ? "已是默认分支最新提交。" : "可更新；目标提交已固定。" };
            }
            catch (Exception error) when (error is not OperationCanceledException) { entry = entry with { CanUpdate = false, Reason = SafeMessage(error.Message) }; }
            entries.Add(entry);
        }
        var plan = SavePlan(config, new(config.ComfyRoot, config.Python, core, entries.ToArray(), null, DateTimeOffset.UtcNow)
            { Sam2Commit = runtime.Sam2Commit, Note = entries.Any(entry => entry.CanUpdate) ? "待停止服务后预检依赖。" : "没有可更新的节点。" });
        if (!entries.Any(entry => entry.CanUpdate)) return plan;
        try { return await PrepareAsync(config, plan, runtime, ct); }
        catch (Exception error) when (error is not OperationCanceledException)
        { return SavePlan(config, plan with { Note = "版本已固定，依赖预检未完成：" + SafeMessage(error.Message) }); }
    }

    private async Task Fetch(NodeUpdateEntry entry, LauncherConfig config, CancellationToken ct)
    {
        if (fetchCommit is not null) { await fetchCommit(entry, ct); return; }
        Exception? last = null;
        foreach (var source in DownloadSources.GitCandidates(ConfirmOrigin(entry.Url), config))
        {
            try { await GitFetchRecovery.Run(git, GitArguments(["fetch", "--depth", "1", source, entry.TargetSha]), entry.Directory, Log, ct, timeout: TimeSpan.FromMinutes(3)); return; }
            catch (Exception error) when (!ct.IsCancellationRequested && Installer.IsSourceFailure(error)) { last = error; Log("节点来源失败，尝试下一来源。" + error.Message); }
        }
        throw new IOException("节点提交下载失败，活动源码未改变。", last);
    }
    private async Task Submodules(string stage, NodeUpdateEntry entry, LauncherConfig config, CancellationToken ct)
    {
        if ((await Git(stage, ["ls-tree", "-r", "HEAD"], ct)).Split('\n').Any(line => line.StartsWith("120000 ", StringComparison.Ordinal)))
            throw new InvalidDataException("节点或子模块包含符号链接，无法完整备份。");
        if (!File.Exists(Path.Combine(stage, ".gitmodules"))) return;
        var paths = await Git(stage, ["config", "-f", ".gitmodules", "--get-regexp", @"^submodule\..*\.path$"], ct);
        foreach (var line in paths.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var child = Safety.Under(stage, line[(line.IndexOf(' ') + 1)..].Trim());
            CheckParents(stage, child);
            if (Directory.Exists(child) || File.Exists(child)) RejectLink(child);
        }
        if (prepareSubmodules is not null)
        {
            await prepareSubmodules(stage, entry, ct);
            if ((await Git(stage, ["submodule", "status", "--recursive"], ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Any(line => line[0] != ' '))
                throw new InvalidDataException("子模块与父节点固定的 gitlink 不一致。");
            return;
        }
        // Validate each level before fetching its children. Default-branch tracking
        // in .gitmodules never overrides the parent's exact gitlink commit.
        var urls = await Git(stage, ["config", "-f", ".gitmodules", "--get-regexp", @"^submodule\..*\.url$"], ct);
        foreach (var line in urls.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var url = line[(line.IndexOf(' ') + 1)..].Trim();
            ConfirmOrigin(Uri.TryCreate(url, UriKind.Absolute, out _) ? url : new Uri(new Uri(entry.Url.TrimEnd('/') + "/"), url).AbsoluteUri);
        }
        Exception? last = null;
        foreach (var source in DownloadSources.GitCandidates("https://github.com/", config))
        {
            try
            {
                var environment = new Dictionary<string, string>(); Installer.ConfigureGitEnvironment(environment, source);
                await Git(stage, ["submodule", "update", "--init"], ct, true, environment); last = null; break;
            }
            catch (Exception error) when (!ct.IsCancellationRequested && Installer.IsSourceFailure(error)) { last = error; }
        }
        if (last is not null) throw new IOException("节点子模块下载失败。", last);
        foreach (var line in paths.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var child = Safety.Under(stage, line[(line.IndexOf(' ') + 1)..].Trim()); CheckParents(stage, child); RejectLink(child);
            var origin = ConfirmOrigin((await Git(child, ["config", "--get", "remote.origin.url"], ct)).Trim());
            await Submodules(child, entry with { Url = origin }, config, ct);
        }
        if ((await Git(stage, ["submodule", "status", "--recursive"], ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Any(line => line[0] != ' '))
            throw new InvalidDataException("子模块与父节点固定的 gitlink 不一致。");
    }
    private static string Fingerprint(string directory)
    {
        _ = DirectoryBytes(directory);
        var lines = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(directory, path).Split(Path.DirectorySeparatorChar).Contains(".git", StringComparer.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase).Select(path =>
            { using var stream = File.OpenRead(path); return Path.GetRelativePath(directory, path) + "=" + Convert.ToHexString(SHA256.HashData(stream)); });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines))));
    }
    private static (IReadOnlyList<string> Files, IReadOnlyDictionary<string, string> Map) Inputs(LauncherConfig config, NodeUpdatePlan plan)
    {
        var selected = plan.Entries.Where(entry => entry.CanUpdate).ToArray();
        var files = ComfyDependencyResolver.ExistingNodeRequirements(config).Where(path => !selected.Any(entry => Same(Path.GetDirectoryName(path)!, entry.Directory))).ToList();
        foreach (var entry in selected) if (File.Exists(Path.Combine(entry.StagedDirectory, "requirements.txt"))) files.Add(Path.Combine(entry.StagedDirectory, "requirements.txt"));
        return (files, selected.ToDictionary(entry => entry.StagedDirectory, entry => entry.Directory, StringComparer.OrdinalIgnoreCase));
    }
    private Task<ComfyDependencySnapshot> Resolve(LauncherConfig config, NodeUpdatePlan plan, ComfyDependencySnapshot snapshot,
        IReadOnlyList<string> files, IReadOnlyDictionary<string, string> map, CancellationToken ct) => resolveDependencies is not null
        ? resolveDependencies(config, snapshot, files, map, ct)
        : ComfyDependencyResolver.ResolveAsync(config, snapshot, Path.Combine(InstallationPlanner.ResolveRuntimeRoot(config, stateDir), "cache"), ct, Log,
            files, new Dictionary<string, string> { ["PATH"] = Path.GetDirectoryName(git) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH") }, plan.Sam2Commit, map);

    public async Task<NodeUpdatePlan> PrepareAsync(LauncherConfig config, NodeUpdatePlan plan, RuntimeManifest runtime, CancellationToken ct)
    {
        ValidateIdentity(config, plan);
        await new ComfyUpdateCoordinator(stateDir, git).ValidateEnvironmentAsync(config, ct);
        if (await CoreCommit(config, ct) != plan.CoreCommit) throw new InvalidOperationException("ComfyUI 本体已变化，请重新检查节点。");
        var runtimeRoot = InstallationPlanner.ResolveRuntimeRoot(config, stateDir);
        if (Same(runtimeRoot, config.ComfyRoot) || Full(runtimeRoot).StartsWith(Full(config.ComfyRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("节点预检与备份目录必须位于 ComfyUI 之外。");
        var stagingRoot = Path.Combine(runtimeRoot, "node-update-plans", Guid.NewGuid().ToString("N"));
        var entries = plan.Entries.ToArray();
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index]; if (!entry.CanUpdate) continue;
            await ValidateNode(config, entry, entry.BeforeSha, ct); await Fetch(entry, config, ct);
            if ((await Git(entry.Directory, ["rev-parse", "FETCH_HEAD"], ct)).Trim() != entry.TargetSha) throw new InvalidDataException("下载的节点与固定提交不一致。");
            if ((await Git(entry.Directory, ["ls-tree", "-r", entry.TargetSha], ct)).Split('\n').Any(line => line.StartsWith("120000 ", StringComparison.Ordinal)))
                throw new InvalidOperationException("目标节点包含符号链接，不能完整安全回退：" + entry.Name);
            var stage = Path.Combine(stagingRoot, index.ToString()); Directory.CreateDirectory(stagingRoot);
            await Git(stagingRoot, ["clone", "--no-hardlinks", "--no-checkout", entry.Directory, stage], ct);
            await Git(stage, ["remote", "set-url", "origin", entry.Url], ct);
            await Git(stage, ["checkout", "--detach", entry.TargetSha], ct);
            await Submodules(stage, entry, config, ct);
            if (entry.Name == PromptAssistantCompatibility.NodeId) await PromptAssistantCompatibility.CheckOrApply(config, stateDir, stage, true, ct);
            entries[index] = entry with { StagedDirectory = stage, StagedFingerprint = Fingerprint(stage) };
        }
        plan = plan with { Entries = entries, Sam2Commit = runtime.Sam2Commit,
            BeforeRequirementsIdentity = ComfyDependencyResolver.NodeRequirementsIdentity(ComfyDependencyResolver.ExistingNodeRequirements(config), runtime.Sam2Commit) };
        var (files, map) = Inputs(config, plan);
        var snapshot = new ComfyDependencySnapshot(plan.CoreCommit, File.ReadAllText(Path.Combine(config.ComfyRoot, "requirements.txt")),
            File.ReadAllText(Path.Combine(config.ComfyRoot, "manager_requirements.txt")), []);
        snapshot = await Resolve(config, plan, snapshot, files, map, ct);
        entries = entries.Select(entry => entry.CanUpdate ? entry with { StagedFingerprint = Fingerprint(entry.StagedDirectory) } : entry).ToArray();
        return SavePlan(config, plan with { Entries = entries, Dependencies = snapshot, Note = "目标节点与完整依赖已固定；可备份并更新。" });
    }
    private static void ValidateIdentity(LauncherConfig config, NodeUpdatePlan plan)
    { if (!Same(config.ComfyRoot, plan.Root) || !Same(config.Python, plan.Python)) throw new InvalidDataException("节点计划属于其他环境。"); }

    public async Task<NodeUpdateBackup> ApplyAsync(LauncherConfig config, NodeUpdatePlan plan,
        Func<LauncherConfig, ComfyDependencySnapshot, CancellationToken, Task> installDependencies,
        Func<LauncherConfig, CancellationToken, Task> validate, CancellationToken ct)
    {
        ValidateIdentity(config, plan);
        if (!plan.CanApply || plan.Dependencies is null) throw new InvalidOperationException("请先完成节点依赖预检。");
        if (Previous(config)?.Status is "updating" or "restoring" or "rollback-failed") throw new InvalidOperationException("上次节点更新未完成，请先回退。");
        if (new ComfyUpdateCoordinator(stateDir, git).Previous(config)?.Status is "updating" or "restoring" or "rollback-failed")
            throw new InvalidOperationException("上次 ComfyUI 更新尚未恢复，请先回退本体更新。");
        await new ComfyUpdateCoordinator(stateDir, git).ValidateEnvironmentAsync(config, ct);
        if (await CoreCommit(config, ct) != plan.CoreCommit) throw new InvalidOperationException("ComfyUI 本体已变化，请重新检查节点。");
        if (ComfyDependencyResolver.NodeRequirementsIdentity(ComfyDependencyResolver.ExistingNodeRequirements(config), plan.Sam2Commit) != plan.BeforeRequirementsIdentity)
            throw new InvalidOperationException("检查后节点依赖已变化，请重新检查。");
        var selected = plan.Entries.Where(entry => entry.CanUpdate).ToArray();
        var expectedStagingRoot = Path.Combine(InstallationPlanner.ResolveRuntimeRoot(config, stateDir), "node-update-plans");
        foreach (var entry in selected)
        {
            await ValidateNode(config, entry, entry.BeforeSha, ct);
            if (entry.StagedDirectory.Length == 0 || !Full(entry.StagedDirectory).StartsWith(Full(expectedStagingRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("节点预检目录不属于当前运行目录，请重新检查。");
            CheckParents(expectedStagingRoot, entry.StagedDirectory); RejectLink(entry.StagedDirectory);
            if (Fingerprint(entry.StagedDirectory) != entry.StagedFingerprint) throw new InvalidDataException("固定的节点预检文件已改变，请重新检查。");
            if ((await Git(entry.StagedDirectory, ["rev-parse", "HEAD"], ct)).Trim() != entry.TargetSha)
                throw new InvalidDataException("节点预检 Git 身份已改变，请重新检查。");
            if ((await Git(entry.StagedDirectory, ["submodule", "status", "--recursive"], ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Any(line => line[0] != ' '))
                throw new InvalidDataException("节点预检子模块身份已改变，请重新检查。");
            await TransferUserFiles(entry.Directory, entry.StagedDirectory, ct, copy: false);
        }
        var (files, map) = Inputs(config, plan);
        var snapshot = await Resolve(config, plan, plan.Dependencies, files, map, ct);
        var gpuPins = RuntimeDependencies.InstalledPackages(config.Python)
            .Where(pair => pair.Key is "torch" or "torchvision" or "torchaudio").ToDictionary(pair => pair.Key, pair => pair.Value);
        var python = IndependentPythonDirectory(config);
        var runtimeRoot = InstallationPlanner.ResolveRuntimeRoot(config, stateDir);
        if (Same(runtimeRoot, config.ComfyRoot) || Full(runtimeRoot).StartsWith(Full(config.ComfyRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("节点备份目录必须位于 ComfyUI 之外。");
        var configurations = ComfyUpdateCoordinator.ConfigurationPaths.Select(relative =>
        { var path = Safety.Under(config.ComfyRoot, relative); return new ComfyConfigurationEntry(relative, File.Exists(path) || Directory.Exists(path), Directory.Exists(path)); }).ToArray();
        var nodeBytes = selected.Sum(entry => DirectoryBytes(entry.Directory));
        var required = DirectoryBytes(python) + nodeBytes
            + configurations.Where(item => item.Existed).Sum(item => item.IsDirectory ? DirectoryBytes(Safety.Under(config.ComfyRoot, item.RelativePath)) : new FileInfo(Safety.Under(config.ComfyRoot, item.RelativePath)).Length);
        var replacementBytes = selected.Sum(entry => DirectoryBytes(entry.StagedDirectory)) + nodeBytes;
        if (string.Equals(Path.GetPathRoot(runtimeRoot), Path.GetPathRoot(python), StringComparison.OrdinalIgnoreCase))
            Safety.FreeSpace(runtimeRoot, checked(required * 2 + replacementBytes + 512L * 1024 * 1024));
        else
        {
            Safety.FreeSpace(runtimeRoot, checked(required + 512L * 1024 * 1024));
            Safety.FreeSpace(python, checked(required + replacementBytes + 512L * 1024 * 1024));
        }
        var backupDirectory = Path.Combine(runtimeRoot, "node-backups", Guid.NewGuid().ToString("N"));
        Log($"备份 {selected.Length} 个节点、完整 Python 与配置（{required / 1073741824d:N1} GiB）。");
        await Task.Run(() =>
        {
            CopyDirectory(python, Path.Combine(backupDirectory, "python"), ct);
            for (var i = 0; i < selected.Length; i++) CopyDirectory(selected[i].Directory, Path.Combine(backupDirectory, "nodes", i.ToString()), ct);
            foreach (var item in configurations.Where(item => item.Existed)) CopyConfiguration(config.ComfyRoot, Path.Combine(backupDirectory, "configuration"), item, ct);
        }, ct);
        var backup = new NodeUpdateBackup(config.ComfyRoot, config.Python, python, plan.CoreCommit, selected, backupDirectory, "updating", DateTimeOffset.UtcNow,
            JsonSerializer.Serialize(config, JsonFile.Options), configurations, ReadOptional(Path.Combine(config.ComfyRoot, ".anima-constraints.txt")));
        await new ComfyUpdateCoordinator(stateDir, git).EnsureStoppedAsync(config, ct); SaveBackup(config, backup);
        try
        {
            var transaction = Path.Combine(config.ComfyRoot, ".anima-node-apply-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(transaction);
            for (var index = 0; index < selected.Length; index++)
            {
                var entry = selected[index];
                // Use the complete independently prepared repository, including pinned
                // submodules, while retaining the old directory intact for recovery.
                var replacement = Path.Combine(transaction, "replacement-" + index);
                CopyDirectory(entry.StagedDirectory, replacement, ct);
                await TransferUserFiles(entry.Directory, replacement, ct, copy: true);
                var retired = Path.Combine(transaction, "previous-" + index);
                Directory.Move(entry.Directory, retired); Directory.Move(replacement, entry.Directory);
                var marker = Path.Combine(entry.Directory, ".anima-revision.json");
                if (File.Exists(marker)) { var repository = JsonFile.Read<Repository>(marker); JsonFile.Write(marker, repository with { Commit = entry.TargetSha, Url = entry.Url }); }
            }
            await installDependencies(config, snapshot, ct);
            async Task VerifyFixedState()
            {
                var installed = RuntimeDependencies.InstalledPackages(config.Python);
                if (gpuPins.Any(pair => !installed.TryGetValue(pair.Key, out var version) || version != pair.Value))
                    throw new InvalidOperationException("节点依赖改变了既有 PyTorch/GPU 版本，已停止并回退。");
                if (await CoreCommit(config, ct) != plan.CoreCommit) throw new InvalidOperationException("节点更新意外改变了 ComfyUI 本体。");
                foreach (var entry in selected) await ValidateNode(config, entry, entry.TargetSha, ct);
            }
            await VerifyFixedState();
            await validate(config, ct);
            await VerifyFixedState();
            backup = backup with { Status = "complete" }; SaveBackup(config, backup); ClearPlan(config);
            Log("节点更新完成；ComfyUI 本体保持原版本，完整旧环境可回退。"); return backup;
        }
        catch (Exception failure)
        {
            try { await Restore(config, backup, CancellationToken.None); }
            catch (Exception recovery) { SaveBackup(config, backup with { Status = "rollback-failed" }); throw new AggregateException("节点更新失败，完整备份已保留，请回退恢复。", failure, recovery); }
            if (failure is OperationCanceledException) throw;
            throw new IOException("节点更新失败，已恢复节点、Python 与配置：" + SafeMessage(failure.Message), failure);
        }
    }

    private async Task TransferUserFiles(string original, string replacement, CancellationToken ct, bool copy)
    {
        // Preserve ignored/user configuration. Target tracked paths always win; a
        // collision with a previously untracked path is refused before switching.
        var untracked = (await Git(original, ["ls-files", "--others", "-z"], ct)).TrimEnd('\r', '\n').Split('\0', StringSplitOptions.RemoveEmptyEntries);
        foreach (var relative in untracked)
        {
            var source = Safety.Under(original, relative); CheckParents(original, source);
            if (!File.Exists(source)) continue; RejectLink(source);
            var destination = Safety.Under(replacement, relative); CheckParents(replacement, destination);
            if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("新版节点与用户文件冲突，未覆盖：" + relative);
            if (copy) { Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(source, destination); }
        }
        if (!File.Exists(Path.Combine(original, ".gitmodules"))) return;
        var paths = await Git(original, ["config", "-f", ".gitmodules", "--get-regexp", @"^submodule\..*\.path$"], ct);
        foreach (var line in paths.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var relative = line[(line.IndexOf(' ') + 1)..].Trim();
            var source = Safety.Under(original, relative); var destination = Safety.Under(replacement, relative);
            CheckParents(original, source); CheckParents(replacement, destination);
            if (Directory.Exists(source) && (File.Exists(Path.Combine(source, ".git")) || Directory.Exists(Path.Combine(source, ".git"))))
                await TransferUserFiles(source, destination, ct, copy);
        }
    }
    private static void CopyConfiguration(string sourceRoot, string destinationRoot, ComfyConfigurationEntry item, CancellationToken ct)
    {
        var source = Safety.Under(sourceRoot, item.RelativePath); CheckParents(sourceRoot, source); RejectLink(source);
        var destination = Safety.Under(destinationRoot, item.RelativePath);
        if (item.IsDirectory) CopyDirectory(source, destination, ct);
        else { Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(source, destination); }
    }

    public async Task RollbackAsync(LauncherConfig config, CancellationToken ct)
    {
        var backup = Previous(config) ?? throw new InvalidOperationException("没有节点更新备份。");
        if (backup.Status == "rolled-back") throw new InvalidOperationException("节点备份已回退。");
        if (backup.Status == "complete") foreach (var entry in backup.Entries) await ValidateNode(config, entry, entry.TargetSha, ct);
        await Restore(config, backup, ct); ClearPlan(config);
    }
    private async Task Restore(LauncherConfig config, NodeUpdateBackup backup, CancellationToken ct)
    {
        if (!Same(config.ComfyRoot, backup.Root) || !Same(config.Python, backup.Python)) throw new InvalidDataException("节点备份属于其他环境。");
        await new ComfyUpdateCoordinator(stateDir, git).EnsureStoppedAsync(config, ct);
        if (await CoreCommit(config, ct) != backup.CoreCommit) throw new InvalidOperationException("ComfyUI 本体在节点更新后已改变，请先回退本体更新。");
        var saved = JsonSerializer.Deserialize<LauncherConfig>(backup.ConfigurationJson, JsonFile.Options) ?? throw new InvalidDataException("配置备份无效。");
        saved.Validate(); if (!Same(saved.ComfyRoot, backup.Root) || !Same(saved.Python, backup.Python)) throw new InvalidDataException("配置备份身份不符。");
        var expectedBackupRoot = Path.Combine(InstallationPlanner.ResolveRuntimeRoot(config, stateDir), "node-backups");
        if (!Full(backup.Directory).StartsWith(Full(expectedBackupRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("节点备份位置不合法。");
        CheckParents(expectedBackupRoot, backup.Directory); RejectLink(backup.Directory);
        var allowedPython = new[] { Path.Combine(config.ComfyRoot, ".venv"), Path.Combine(config.ComfyRoot, "venv") }
            .Concat(new[] { config.ComfyRoot, Path.GetDirectoryName(config.ComfyRoot)! }.SelectMany(root => new[] { Path.Combine(root, "python_embeded"), Path.Combine(root, "python_embedded") }));
        if (!allowedPython.Any(path => Same(path, backup.PythonDirectory)
            && Same(config.Python, Path.Combine(path, Path.GetFileName(path) is ".venv" or "venv" ? Path.Combine("Scripts", "python.exe") : "python.exe"))))
            throw new InvalidDataException("Python 备份位置不合法。");
        foreach (var entry in backup.Entries)
        { if (!Same(Path.GetDirectoryName(entry.Directory)!, Path.Combine(config.ComfyRoot, "custom_nodes"))) throw new InvalidDataException("节点备份路径不合法。"); CheckParents(config.ComfyRoot, entry.Directory); if (Directory.Exists(entry.Directory)) RejectLink(entry.Directory); }
        if (backup.ConfigurationPaths.Any(item => !ComfyUpdateCoordinator.ConfigurationPaths.Contains(item.RelativePath, StringComparer.Ordinal))
            || backup.ConfigurationPaths.Select(item => item.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != backup.ConfigurationPaths.Length)
            throw new InvalidDataException("配置备份路径不合法。");
        foreach (var item in backup.ConfigurationPaths)
        {
            var path = Safety.Under(config.ComfyRoot, item.RelativePath); CheckParents(config.ComfyRoot, path);
            if (Directory.Exists(path)) _ = DirectoryBytes(path); else if (File.Exists(path)) RejectLink(path);
        }
        CheckParents(Path.GetDirectoryName(config.ComfyRoot)!, backup.PythonDirectory);
        if (Directory.Exists(backup.PythonDirectory)) RejectLink(backup.PythonDirectory);
        var constraints = Path.Combine(config.ComfyRoot, ".anima-constraints.txt"); if (File.Exists(constraints)) RejectLink(constraints);
        var stage = Path.Combine(config.ComfyRoot, ".anima-node-restore-" + Guid.NewGuid().ToString("N"));
        var needed = DirectoryBytes(Path.Combine(backup.Directory, "python")) + DirectoryBytes(Path.Combine(backup.Directory, "nodes"))
            + (Directory.Exists(Path.Combine(backup.Directory, "configuration")) ? DirectoryBytes(Path.Combine(backup.Directory, "configuration")) : 0);
        Safety.FreeSpace(config.ComfyRoot, needed);
        await Task.Run(() =>
        {
            CopyDirectory(Path.Combine(backup.Directory, "python"), Path.Combine(stage, "python"), ct);
            CopyDirectory(Path.Combine(backup.Directory, "nodes"), Path.Combine(stage, "nodes"), ct);
            foreach (var item in backup.ConfigurationPaths.Where(item => item.Existed)) CopyConfiguration(Path.Combine(backup.Directory, "configuration"), Path.Combine(stage, "configuration"), item, ct);
        }, ct);
        await new ComfyUpdateCoordinator(stateDir, git).EnsureStoppedAsync(config, ct); ct.ThrowIfCancellationRequested();
        SaveBackup(config, backup with { Status = "restoring" });
        void Exchange(string source, string destination, string retiredName)
        {
            CheckParents(Path.GetDirectoryName(config.ComfyRoot)!, destination);
            if (Directory.Exists(destination) || File.Exists(destination))
            { RejectLink(destination); var retired = Path.Combine(stage, retiredName); if (Directory.Exists(destination)) Directory.Move(destination, retired); else File.Move(destination, retired); }
            if (Directory.Exists(source)) Directory.Move(source, destination); else if (File.Exists(source)) File.Move(source, destination);
        }
        Exchange(Path.Combine(stage, "python"), backup.PythonDirectory, "failed-python");
        for (var index = 0; index < backup.Entries.Length; index++) Exchange(Path.Combine(stage, "nodes", index.ToString()), backup.Entries[index].Directory, "failed-node-" + index);
        foreach (var item in backup.ConfigurationPaths) Exchange(Safety.Under(Path.Combine(stage, "configuration"), item.RelativePath), Safety.Under(config.ComfyRoot, item.RelativePath), "failed-config-" + item.RelativePath);
        RestoreOptional(constraints, backup.ConstraintsFile); JsonFile.Write(Path.Combine(stateDir, "launcher.json"), saved);
        SaveBackup(config, backup with { Status = "rolled-back" }); Log("节点、完整 Python 与用户配置已恢复；失败目录保留于 " + stage);
    }
}
