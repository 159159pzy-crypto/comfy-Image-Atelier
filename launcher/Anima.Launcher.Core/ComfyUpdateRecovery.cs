using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Anima.Launcher.Core;

public sealed partial class ComfyUpdateCoordinator
{
    private static readonly string[] LightConfigurationPaths =
    ["extra_model_paths.yaml", "extra_model_paths.yml", "config.yaml", "config.yml", "config.json", "comfy.settings.json",
        ".anima-revision.json", ".anima-constraints.txt"];

    public static bool RequiresRecovery(ComfyUpdateBackup? backup) => backup is not null &&
        (backup.Status is not ("complete" or "rolled-back") || !IsLight(backup) && !(backup.FormatVersion == 0 && backup.RecoveryStrategy == "full"));
    private static bool IsLight(ComfyUpdateBackup backup) => backup.FormatVersion == 1 && backup.RecoveryStrategy == "light";
    private static void ValidateLegacyFormat(ComfyUpdateBackup backup)
    {
        if (backup.FormatVersion != 0 || backup.RecoveryStrategy != "full")
            throw new InvalidDataException("不支持此 ComfyUI 恢复记录版本，原记录已保留。");
    }

    public async Task<ComfyUpdateBackup> UpdateAsync(LauncherConfig config, ComfyRelease release, RuntimeManifest runtime,
        ComfyDependencySnapshot dependencies, Func<LauncherConfig, ComfyDependencySnapshot, CancellationToken, Task> installDependencies,
        Func<LauncherConfig, CancellationToken, Task> validate, CancellationToken ct,
        Func<LauncherConfig, ComfyDependencySnapshot, CancellationToken, Task>? prepareDependencies = null,
        Func<LauncherConfig, CancellationToken, Task>? validateRecovery = null,
        Func<LauncherConfig, string, CancellationToken, Task>? validateTargetSource = null)
    {
        Safety.Commit(release.Commit);
        if (!dependencies.Resolved || dependencies.ComfyCommit != release.Commit)
            throw new InvalidOperationException("更新前必须完成目标版本的依赖解析。");
        if (new NodeUpdateCoordinator(stateDir, git).Previous(config)?.Status is "updating" or "restoring" or "rollback-failed")
            throw new InvalidOperationException("上次节点更新尚未恢复，请先回退节点更新。");
        if (RequiresRecovery(Previous(config))) throw new InvalidOperationException("上次更新尚未恢复，请先回退或修复依赖。");
        await ValidateEnvironmentAsync(config, ct);
        var root = Full(config.ComfyRoot);
        var previous = (await Git(root, ["rev-parse", "HEAD"], ct)).Trim();
        Safety.Commit(previous);
        if (previous == release.Commit) throw new InvalidOperationException("此环境已经是所选稳定版本。");

        Progress?.Invoke("fetching");
        Exception? fetchError = null;
        if (fetchCommit is not null) await fetchCommit(root, release.Commit, ct);
        else foreach (var url in DownloadSources.GitCandidates(config.CustomRepository.Length > 0 ? config.CustomRepository : ComfyReleases.RepositoryUrl, config))
        {
            try
            {
                log?.Invoke("ComfyUI 稳定版 · 来源：" + url);
                await Git(root, ["fetch", "--depth", "1", url, release.Commit], ct); fetchError = null; break;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && Installer.IsSourceFailure(ex))
            { fetchError = ex; log?.Invoke("当前 Git 来源未完成下载，将尝试下一来源：" + ex.Message); }
        }
        if (fetchError is not null) throw new IOException("下载稳定版提交失败，原环境保持不变。", fetchError);
        if ((await Git(root, ["rev-parse", "FETCH_HEAD"], ct)).Trim() != release.Commit)
            throw new InvalidDataException("下载的 Git 提交与固定稳定版本不一致。");
        if ((await Git(root, ["ls-tree", "-r", release.Commit], ct)).Split('\n').Any(line => line.StartsWith("120000 ", StringComparison.Ordinal) || line.StartsWith("160000 ", StringComparison.Ordinal)))
            throw new InvalidOperationException("目标版本包含符号链接或子模块，暂不支持原地更新。");
        foreach (var (name, expected) in new[] { ("requirements.txt", dependencies.RequirementsText), ("manager_requirements.txt", dependencies.ManagerRequirementsText) })
            if (NormalizeText(await Git(root, ["show", release.Commit + ":" + name], ct)) != NormalizeText(expected))
                throw new InvalidDataException("稳定版依赖清单与已验证 Git 提交不一致：" + name);
        if (validateTargetSource is not null)
            await validateTargetSource(config, await Git(root, ["show", release.Commit + ":comfy/cli_args.py"], ct), ct);
        var files = SplitFiles(await Git(root, ["ls-tree", "-r", "--name-only", "-z", previous], ct));
        var targets = SplitFiles(await Git(root, ["ls-tree", "-r", "--name-only", "-z", release.Commit], ct));
        ValidateSourceCollisions(root, files, targets);

        // Download and build every reviewed artifact before creating the recovery point
        // or changing source. This callback must not install into the selected Python.
        if (prepareDependencies is not null)
        {
            log?.Invoke("正在准备更新依赖；ComfyUI 源码与 Python 保持不变。");
            Progress?.Invoke("preparing");
            await prepareDependencies(config, dependencies, ct);
        }
        ct.ThrowIfCancellationRequested();
        await ValidateEnvironmentAsync(config, ct);
        if ((await Git(root, ["rev-parse", "HEAD"], ct)).Trim() != previous)
            throw new InvalidOperationException("准备期间 ComfyUI 版本发生变化，请重新检查；没有切换源码。");
        ValidateSourceCollisions(root, files, targets);
        var runtimeRoot = InstallationPlanner.ResolveRuntimeRoot(config, stateDir);
        if (Same(runtimeRoot, root) || Full(runtimeRoot).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("恢复记录目录必须位于 ComfyUI 目录之外。");
        var id = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N");
        var backupRoot = Path.Combine(runtimeRoot, "comfy-backups", id);
        var configurations = LightConfigurationPaths.Select(relative => SnapshotConfiguration(root, relative)).ToArray();
        var bytes = configurations.Where(item => item.Existed).Sum(item => new FileInfo(Safety.Under(root, item.RelativePath)).Length);
        Safety.FreeSpace(runtimeRoot, checked(bytes + 16L * 1024 * 1024));
        // A source checkout needs room for its changed files, not a second Python/Git tree.
        var targetBytes = (await Git(root, ["ls-tree", "-r", "-l", release.Commit], ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Sum(line => { var fields = line.Split((char[]?)null, 5, StringSplitOptions.RemoveEmptyEntries); return fields.Length >= 4 && long.TryParse(fields[3], out var length) ? length : 0; });
        Safety.FreeSpace(root, checked(targetBytes + 64L * 1024 * 1024));
        Directory.CreateDirectory(backupRoot);
        foreach (var item in configurations.Where(item => item.Existed))
        {
            ct.ThrowIfCancellationRequested();
            var source = Safety.Under(root, item.RelativePath);
            var destination = Safety.Under(Path.Combine(backupRoot, "configuration"), item.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
            if (FileHash(destination) != item.Sha256 || FileHash(source) != item.Sha256)
                throw new IOException("配置在建立恢复点时发生变化，请重新检查：" + item.RelativePath);
        }
        var branch = (await Git(root, ["rev-parse", "--abbrev-ref", "HEAD"], ct)).Trim();
        var expectedConfigurations = configurations.ToDictionary(item => item.RelativePath, item => item.Existed ? item.Sha256 : null, StringComparer.Ordinal);
        foreach (var item in configurations)
        {
            if (targets.Contains(item.RelativePath, StringComparer.Ordinal))
                expectedConfigurations[item.RelativePath] = "git:" + (await Git(root, ["rev-parse", release.Commit + ":" + item.RelativePath], ct)).Trim();
            else if (files.Contains(item.RelativePath, StringComparer.Ordinal)) expectedConfigurations[item.RelativePath] = null;
        }
        var currentConfiguration = CurrentConfigurationHashes(root);
        if (configurations.Any(item => currentConfiguration[item.RelativePath] != (item.Existed ? item.Sha256 : null)))
            throw new IOException("配置在建立恢复点时发生变化，请重新检查；未切换源码。");
        var recoveryRef = "refs/anima/comfy-recovery/" + id;
        await Git(root, ["update-ref", recoveryRef, previous, new string('0', 40)], ct);
        var backup = new ComfyUpdateBackup(root, config.Python, previous, release.Commit, backupRoot, files, targets, "updating", DateTimeOffset.UtcNow)
        {
            FormatVersion = 1, RecoveryStrategy = "light", PreviousBranch = branch == "HEAD" ? "" : branch,
            RecoveryRef = recoveryRef, Phase = "prepared", PythonDirectory = IndependentPythonDirectory(config),
            ConfigurationJson = JsonSerializer.Serialize(config, JsonFile.Options), ConfigurationPaths = configurations,
            RevisionMarker = ReadOptional(Path.Combine(root, ".anima-revision.json")), ConstraintsFile = ReadOptional(Path.Combine(root, ".anima-constraints.txt")),
            EnvironmentPackages = dependencies.EnvironmentPackages.ToArray(), AppliedConfigurationHashes = expectedConfigurations,
        };
        Save(config, backup);
        log?.Invoke($"已建立轻量恢复点（配置 {bytes / 1024d:N1} KiB）；不复制 Python、Git、模型或用户数据库。");
        try
        {
            await EnsureStoppedAsync(config, ct);
            backup = backup with { Phase = "source-checkout" }; Save(config, backup);
            await Git(root, ["checkout", "--no-overwrite-ignore", "--detach", release.Commit], ct);
            await ValidateConfigurationStateAsync(root, backup, ct);
            backup = backup with { Phase = "dependencies", DependenciesMayHaveChanged = true }; Save(config, backup);
            await installDependencies(config, dependencies, ct);
            await ValidateConfigurationStateAsync(root, backup, ct);
            backup = backup with { Phase = "health-check" }; Save(config, backup);
            await validate(config, ct);
            if ((await Git(root, ["rev-parse", "HEAD"], ct)).Trim() != release.Commit
                || !string.IsNullOrWhiteSpace(await Git(root, ["status", "--porcelain", "--untracked-files=no"], ct)))
                throw new InvalidOperationException("更新期间源码发生变化，未将当前状态标记为更新成功。");
            await ValidateConfigurationStateAsync(root, backup, ct);
            if (backup.RevisionMarker is not null && !files.Contains(".anima-revision.json", StringComparer.Ordinal)
                && !targets.Contains(".anima-revision.json", StringComparer.Ordinal))
            {
                var revision = runtime.Comfy with { Commit = release.Commit, Url = config.CustomRepository.Length > 0 ? config.CustomRepository : runtime.Comfy.Url };
                var expected = new Dictionary<string, string?>(backup.AppliedConfigurationHashes, StringComparer.Ordinal)
                    { [".anima-revision.json"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(revision, JsonFile.Options)))).ToLowerInvariant() };
                backup = backup with { AppliedConfigurationHashes = expected }; Save(config, backup);
                JsonFile.Write(Path.Combine(root, ".anima-revision.json"), revision);
            }
            backup = backup with { Status = "complete", Phase = "complete", CoreVerified = true };
            Save(config, backup);
            log?.Invoke("ComfyUI 已更新至 " + release.Tag + "；轻量恢复点已保留，回退不恢复完整 Python 环境。");
            return backup;
        }
        catch (Exception error)
        {
            try
            {
                await RestoreLightAsync(config, backup, validateRecovery, CancellationToken.None);
            }
            catch (Exception restoreError)
            {
                Save(config, (Previous(config) ?? backup) with { Status = "rollback-failed", RecoveryError = restoreError.Message, CoreVerified = false });
                throw new AggregateException("更新失败，源码恢复未完成；已保留恢复点和现有文件，请处理冲突后重试回退。", error, restoreError);
            }
            var recovered = Previous(config)!;
            if (error is OperationCanceledException) throw;
            throw new IOException((recovered.Status == "needs-repair"
                ? "更新失败，源码与必要配置已恢复，依赖待修复；请点击修复依赖。"
                : recovered.CoreVerified ? "更新失败，源码与必要配置已恢复并通过核心检查。"
                : "更新失败，源码与必要配置已恢复；未执行核心启动检查。") + " 原因：" + error.Message, error);
        }
    }

    private static void ValidateSourceCollisions(string root, string[] files, string[] targets)
    {
        var tracked = files.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var added in targets.Except(files, StringComparer.Ordinal))
        {
            var path = Safety.Under(root, added);
            CheckParents(root, path);
            var conflict = File.Exists(path);
            if (Directory.Exists(path))
            {
                _ = DirectoryBytes(path);
                conflict = !files.Any(file => file.StartsWith(added.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase))
                    || Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Any(file => !tracked.Contains(Path.GetRelativePath(root, file).Replace('\\', '/')));
            }
            if (conflict) throw new IOException("目标源码与已有未跟踪文件冲突，已保留原文件：" + path);
        }
    }

    private static string FileHash(string path)
    {
        RejectLink(path);
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }

    private static ComfyConfigurationEntry SnapshotConfiguration(string root, string relative)
    {
        var path = Safety.Under(root, relative);
        CheckParents(root, path);
        if (Directory.Exists(path)) throw new IOException("必要配置应为文件，未更新：" + path);
        return new(relative, File.Exists(path), false) { Sha256 = File.Exists(path) ? FileHash(path) : "" };
    }

    private static Dictionary<string, string?> CurrentConfigurationHashes(string root) => LightConfigurationPaths.ToDictionary(path => path,
        path => { var item = SnapshotConfiguration(root, path); return item.Existed ? item.Sha256 : null; }, StringComparer.Ordinal);

    private async Task ValidateConfigurationStateAsync(string root, ComfyUpdateBackup backup, CancellationToken ct)
    {
        foreach (var item in backup.ConfigurationPaths)
            await ValidateConfigurationItemAsync(root, backup, item, ct);
    }

    private async Task ValidateConfigurationItemAsync(string root, ComfyUpdateBackup backup, ComfyConfigurationEntry item, CancellationToken ct)
    {
        var currentItem = SnapshotConfiguration(root, item.RelativePath);
        var current = currentItem.Existed ? currentItem.Sha256 : null;
        var original = item.Existed ? item.Sha256 : null;
        if (!backup.AppliedConfigurationHashes.TryGetValue(item.RelativePath, out var expected))
            throw new InvalidDataException("配置恢复记录缺少预期状态：" + item.RelativePath);
        var matches = current == original || current == expected;
        if (!matches && expected?.StartsWith("git:", StringComparison.Ordinal) == true && current is not null)
            matches = (await Git(root, ["hash-object", "--path=" + item.RelativePath, Safety.Under(root, item.RelativePath)], ct)).Trim() == expected[4..];
        if (!matches) throw new IOException("配置在更新后已被修改，未覆盖：" + item.RelativePath);
    }

    private async Task RestoreLightAsync(LauncherConfig config, ComfyUpdateBackup backup,
        Func<LauncherConfig, CancellationToken, Task>? validateRecovery, CancellationToken ct)
    {
        ValidateLightRecord(config, backup);
        await EnsureStoppedAsync(config, ct);
        var root = Full(config.ComfyRoot);
        if (!Directory.Exists(Path.Combine(root, ".git"))) throw new IOException("Git 仓库不存在；轻量恢复点不能重建整个 Git 目录。");
        RejectLink(root); RejectLink(Path.Combine(root, ".git"));
        if (!Same((await Git(root, ["rev-parse", "--show-toplevel"], ct)).Trim(), root)) throw new InvalidDataException("恢复目标不是原 Git 根目录。");
        var head = (await Git(root, ["rev-parse", "HEAD"], ct)).Trim();
        if (head != backup.PreviousCommit && head != backup.TargetCommit) throw new InvalidOperationException("ComfyUI 已被切换到其他提交，未覆盖当前版本。");
        if (!string.IsNullOrWhiteSpace(await Git(root, ["status", "--porcelain", "--untracked-files=no"], ct)))
            throw new InvalidOperationException("ComfyUI 源码存在本地修改或未完成切换，未覆盖；请处理后重试回退。");
        if ((await Git(root, ["rev-parse", "--verify", backup.RecoveryRef + "^{commit}"], ct)).Trim() != backup.PreviousCommit)
            throw new InvalidDataException("轻量恢复引用已变化，未回退。");
        ValidateSourceCollisions(root, head == backup.TargetCommit ? backup.TargetFiles : backup.SourceFiles, backup.SourceFiles);
        await ValidateConfigurationStateAsync(root, backup, ct);
        foreach (var item in backup.ConfigurationPaths)
        {
            if (item.Existed)
            {
                var saved = Safety.Under(Path.Combine(backup.Directory, "configuration"), item.RelativePath);
                CheckParents(backup.Directory, saved);
                if (!File.Exists(saved) || FileHash(saved) != item.Sha256) throw new IOException("配置恢复文件缺失或已变化：" + item.RelativePath);
            }
        }
        backup = backup with { Status = "restoring", Phase = "restoring-source", RecoveryError = "", CoreVerified = false }; Save(config, backup);
        // Never reset/clean/force. Git protects tracked edits and untracked collisions,
        // including ignored files through --no-overwrite-ignore.
        if (head != backup.PreviousCommit) await Git(root, ["checkout", "--no-overwrite-ignore", "--detach", backup.PreviousCommit], ct);
        if (backup.PreviousBranch.Length > 0)
        {
            string branchCommit;
            try { branchCommit = (await Git(root, ["rev-parse", "--verify", "refs/heads/" + backup.PreviousBranch], ct)).Trim(); }
            catch (InvalidOperationException) { branchCommit = ""; }
            if (branchCommit == backup.PreviousCommit) await Git(root, ["checkout", "--no-overwrite-ignore", backup.PreviousBranch], ct);
            else log?.Invoke("原分支已移动或不存在；已恢复旧提交并保持脱离分支状态。");
        }
        await ValidateConfigurationStateAsync(root, backup, ct);
        backup = backup with { Phase = "restoring-configuration" }; Save(config, backup);
        foreach (var item in backup.ConfigurationPaths)
        {
            await ValidateConfigurationItemAsync(root, backup, item, ct);
            var path = Safety.Under(root, item.RelativePath);
            CheckParents(root, path);
            if (File.Exists(path) || Directory.Exists(path)) RejectLink(path);
            // A configuration tracked by Git may already have been restored by checkout.
            if (File.Exists(path) && FileHash(path) == item.Sha256 && item.Existed) continue;
            if (item.Existed)
            {
                var staged = path + ".anima-restore-" + Guid.NewGuid().ToString("N");
                File.Copy(Safety.Under(Path.Combine(backup.Directory, "configuration"), item.RelativePath), staged);
                if (FileHash(staged) != item.Sha256) throw new IOException("配置恢复文件在复制时发生变化，原文件未覆盖：" + item.RelativePath);
                await ValidateConfigurationItemAsync(root, backup, item, ct);
                File.Move(staged, path, true);
            }
            else if (File.Exists(path)) File.Delete(path);
        }
        // Launcher settings are captured for diagnosis only. Source/dep updates do
        // not edit them, and rollback must not revert newer theme/network choices.
        backup = backup with { Phase = "recovery-check" }; Save(config, backup);
        await FinishRecoveryAsync(config, backup, validateRecovery, ct);
    }

    private void ValidateLightRecord(LauncherConfig config, ComfyUpdateBackup backup)
    {
        if (!IsLight(backup) || !Same(backup.Root, config.ComfyRoot) || !Same(backup.Python, config.Python))
            throw new InvalidDataException("轻量恢复记录属于其他环境或格式无效。");
        Safety.Commit(backup.PreviousCommit); Safety.Commit(backup.TargetCommit);
        if (!Regex.IsMatch(backup.RecoveryRef, @"^refs/anima/comfy-recovery/[0-9]{8}T[0-9]{9}-[a-f0-9]{32}$"))
            throw new InvalidDataException("轻量恢复引用格式无效。");
        var savedConfig = JsonSerializer.Deserialize<LauncherConfig>(backup.ConfigurationJson, JsonFile.Options)
            ?? throw new InvalidDataException("轻量恢复环境配置缺失。");
        savedConfig.Validate();
        if (!Same(savedConfig.ComfyRoot, backup.Root) || !Same(savedConfig.Python, backup.Python)) throw new InvalidDataException("恢复配置身份不匹配。");
        var expectedRoot = Path.Combine(InstallationPlanner.ResolveRuntimeRoot(savedConfig, stateDir), "comfy-backups");
        if (!Same(Path.GetDirectoryName(Full(backup.Directory))!, expectedRoot)) throw new InvalidDataException("恢复文件目录超出允许范围。");
        CheckParents(expectedRoot, backup.Directory); RejectLink(backup.Directory);
        if (backup.ConfigurationPaths.Length != LightConfigurationPaths.Length
            || !backup.ConfigurationPaths.Select(item => item.RelativePath).Order().SequenceEqual(LightConfigurationPaths.Order())
            || backup.ConfigurationPaths.Any(item => item.IsDirectory || item.Existed && !Regex.IsMatch(item.Sha256, "^[a-f0-9]{64}$")))
            throw new InvalidDataException("轻量配置恢复清单无效。");
        foreach (var path in backup.SourceFiles.Concat(backup.TargetFiles)) _ = Safety.Under(backup.Root, path);
        if (backup.PreviousBranch.StartsWith('-') || backup.PreviousBranch.Any(character => char.IsControl(character)))
            throw new InvalidDataException("原分支记录无效。");
    }

    private async Task FinishRecoveryAsync(LauncherConfig config, ComfyUpdateBackup backup,
        Func<LauncherConfig, CancellationToken, Task>? validateRecovery, CancellationToken ct)
    {
        if (!backup.DependenciesMayHaveChanged && validateRecovery is null)
        {
            Save(config, backup with { Status = "rolled-back", Phase = "recovered", RecoveryError = "" });
            return;
        }
        if (validateRecovery is null)
        {
            Save(config, backup with { Status = "needs-repair", Phase = "dependency-review", RecoveryError = "尚未完成恢复后的依赖检查。" });
            log?.Invoke("源码与必要配置已恢复，Python 包未回退；请检查并修复依赖。");
            return;
        }
        try { await validateRecovery(config, ct); }
        catch (Exception error)
        {
            Save(config, backup with { Status = "needs-repair", Phase = "dependency-review", RecoveryError = error.Message });
            log?.Invoke("源码与必要配置已恢复，依赖检查未通过；请点击修复依赖。" + error.Message);
            if (error is OperationCanceledException && ct.IsCancellationRequested) throw;
            return;
        }
        Save(config, backup with { Status = "rolled-back", Phase = "recovered", RecoveryError = "", CoreVerified = true });
        log?.Invoke("源码与必要配置已恢复，依赖与核心运行检查通过；未还原完整 Python 环境。");
    }

    public async Task CompleteDependencyRepairAsync(LauncherConfig config,
        Func<LauncherConfig, CancellationToken, Task> validateRecovery, CancellationToken ct)
    {
        var backup = Previous(config) ?? throw new InvalidOperationException("没有待完成的恢复记录。");
        ValidateLightRecord(config, backup);
        if (backup.Status != "needs-repair") throw new InvalidOperationException("当前恢复记录不需要完成依赖修复。");
        await ValidateEnvironmentAsync(config, ct);
        if ((await Git(config.ComfyRoot, ["rev-parse", "HEAD"], ct)).Trim() != backup.PreviousCommit)
            throw new InvalidOperationException("源码版本已变化，不能确认原恢复记录完成。");
        try { await validateRecovery(config, ct); }
        catch (Exception error)
        {
            Save(config, backup with { RecoveryError = error.Message, CoreVerified = false });
            throw;
        }
        Save(config, backup with { Status = "rolled-back", Phase = "recovered", RecoveryError = "", CoreVerified = true });
    }
}
