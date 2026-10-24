using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Anima.Launcher.Core;

public sealed record ComfyConfigurationEntry(string RelativePath, bool Existed, bool IsDirectory);
public sealed record ComfyUpdateBackup(string Root, string Python, string PreviousCommit, string TargetCommit,
    string Directory, string[] SourceFiles, string[] TargetFiles, string Status, DateTimeOffset CreatedAt)
{
    public string? RevisionMarker { get; init; }
    public string? ConstraintsFile { get; init; }
    public string PythonDirectory { get; init; } = "";
    public string ConfigurationJson { get; init; } = "";
    public ComfyConfigurationEntry[] ConfigurationPaths { get; init; } = [];
}

/// <summary>Manual updates preserve models, custom nodes and user data, and back up Git plus the complete venv.</summary>
public sealed class ComfyUpdateCoordinator(string stateDir, string git, Action<string>? log = null,
    Func<string, string, CancellationToken, Task>? fetchCommit = null)
{
    private static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool Same(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try { return Full(a).Equals(Full(b), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return false; }
    }
    private string Pointer(LauncherConfig config) => Path.Combine(stateDir, "comfy-updates", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Full(config.ComfyRoot).ToUpperInvariant()))).ToLowerInvariant() + ".json");
    public ComfyUpdateBackup? Previous(LauncherConfig config) => File.Exists(Pointer(config)) ? JsonFile.Read<ComfyUpdateBackup>(Pointer(config)) : null;
    private Task<string> Git(string root, IEnumerable<string> args, CancellationToken ct)
    {
        var arguments = args.ToArray();
        // Keep fetch progress and state changes visible without flooding the UI
        // with complete source trees and requirements during read-only checks.
        var progress = arguments.FirstOrDefault() is "fetch" or "checkout" ? log : null;
        return arguments.FirstOrDefault() == "fetch"
            ? GitFetchRecovery.Run(git, arguments, root, progress, ct, timeout: TimeSpan.FromMinutes(3))
            : Commands.Run(git, arguments, root, progress, ct, timeout: TimeSpan.FromMinutes(3));
    }
    internal static readonly string[] ConfigurationPaths = ["user", "extra_model_paths.yaml", "extra_model_paths.yml", "config.yaml", "config.yml", "config.json", "comfy.settings.json"];
    public static bool CommandTargetsEnvironment(string? command, LauncherConfig config)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        try
        {
            return ComfyLaunchArguments.Parse(command).Where(Path.IsPathRooted).Any(argument =>
                Same(argument, config.Python) || !string.IsNullOrWhiteSpace(config.ComfyRoot) && Same(argument, Path.Combine(config.ComfyRoot, "main.py")));
        }
        catch (ArgumentException) { return false; }
        catch (FormatException) { return false; }
        catch (InvalidDataException) { return false; }
    }

    public static string IndependentPythonDirectory(LauncherConfig config)
    {
        var root = Full(config.ComfyRoot);
        foreach (var name in new[] { ".venv", "venv" })
        {
            var directory = Path.Combine(root, name);
            if (!Same(config.Python, Path.Combine(directory, "Scripts", "python.exe"))) continue;
            var cfg = Path.Combine(directory, "pyvenv.cfg");
            RejectLink(directory);
            CheckParents(directory, config.Python);
            if (File.Exists(config.Python)) RejectLink(config.Python);
            if (File.Exists(cfg)) RejectLink(cfg);
            if (!File.Exists(cfg) || !File.ReadLines(cfg).Any(line => line.Replace(" ", "").Equals("include-system-site-packages=false", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("无法确认虚拟环境独立，未更新。");
            return directory;
        }
        foreach (var parent in new[] { root, Path.GetDirectoryName(root)! }.Distinct(StringComparer.OrdinalIgnoreCase))
        foreach (var name in new[] { "python_embeded", "python_embedded" })
        {
            var directory = Path.Combine(parent, name);
            if (!Same(config.Python, Path.Combine(directory, "python.exe"))) continue;
            if (!Directory.Exists(directory) || !Directory.EnumerateFiles(directory, "python*._pth").Any())
                throw new InvalidOperationException("便携 Python 缺少独立路径配置，未更新。");
            RejectLink(directory);
            CheckParents(directory, config.Python);
            if (File.Exists(config.Python)) RejectLink(config.Python);
            foreach (var pth in Directory.EnumerateFiles(directory, "python*._pth")) RejectLink(pth);
            if (!Same(parent, root) && Directory.EnumerateDirectories(parent).Any(sibling => !Same(sibling, root)
                && !Same(sibling, directory) && File.Exists(Path.Combine(sibling, "main.py"))))
                throw new InvalidOperationException("便携 Python 旁存在多个 ComfyUI 目录，可能共享依赖，未更新。");
            return directory;
        }
        throw new InvalidOperationException("更新需要本环境内的 .venv、venv 或独立便携 Python；无法确认专用的共享或外部解释器不会被修改。");
    }

    public async Task ValidateEnvironmentAsync(LauncherConfig config, CancellationToken ct, bool clean = true)
    {
        config.Validate();
        var root = Full(config.ComfyRoot);
        if (!Directory.Exists(Path.Combine(root, ".git")) || !File.Exists(Path.Combine(root, "main.py")) || !File.Exists(config.Python))
            throw new InvalidOperationException("更新需要独立 Git 仓库与有效的专用 Python；工作树或源码压缩包请使用原有方式更新。");
        _ = IndependentPythonDirectory(config);
        RejectLink(root);
        RejectLink(Path.Combine(root, ".git"));
        if (File.Exists(Path.Combine(root, ".git", "objects", "info", "alternates")))
            throw new InvalidOperationException("此 Git 仓库共享其他目录的对象，无法完整备份，请先转换为独立克隆。");
        if (!Same((await Git(root, ["rev-parse", "--show-toplevel"], ct)).Trim(), root)) throw new InvalidOperationException("目标不是独立 Git 根目录。");
        if ((await Git(root, ["ls-files", "--stage"], ct)).Split('\n').Any(line => line.StartsWith("160000 ", StringComparison.Ordinal)))
            throw new InvalidOperationException("环境包含 Git 子模块，无法完整回滚，未更新。");
        if (clean && !string.IsNullOrWhiteSpace(await Git(root, ["status", "--porcelain", "--untracked-files=no"], ct)))
            throw new InvalidOperationException("ComfyUI 源码有本地修改，请先保存修改后再更新。");
        await EnsureStoppedAsync(config, ct);
    }

    public async Task EnsureStoppedAsync(LauncherConfig config, CancellationToken ct)
    {
        if (await ServiceManager.PortOpen(config.ComfyPort, ct) || await ServiceManager.PortOpen(config.WebPort, ct))
            throw new InvalidOperationException("请先停止此环境的 ComfyUI 和工作台，再更新或回退；外部进程不会被自动停止。");
        var processIds = new List<int>();
        foreach (var process in Process.GetProcessesByName("python").Concat(Process.GetProcessesByName("pythonw")))
        {
            using (process)
            {
                ct.ThrowIfCancellationRequested();
                string? executable = null;
                try { executable = process.MainModule?.FileName; } catch { }
                if (executable is not null && Same(executable, config.Python))
                    throw new InvalidOperationException($"此 Python 环境仍有进程 {process.Id} 运行，请先关闭后重试。");
                processIds.Add(process.Id);
            }
        }
        var commands = await ReadPythonCommands(processIds, ct);
        foreach (var (pid, command) in commands)
        {
                if (CommandTargetsEnvironment(command, config))
                    throw new InvalidOperationException($"发现此环境的外部进程 {pid}（可能使用其他端口），请先关闭后重试；未停止该进程。");
        }
        var file = Path.Combine(stateDir, "processes.json");
        if (!File.Exists(file)) return;
        foreach (var record in JsonFile.Read<OwnedProcess[]>(file).Where(record => Same(record.Executable, config.Python)))
        {
            try
            {
                using var process = Process.GetProcessById(record.Pid);
                if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == record.StartTimeUtcTicks)
                    throw new IOException("此 Python 环境仍有服务进程运行，请先停止后重试。");
            }
            catch (ArgumentException) { }
        }
    }

    private static async Task<Dictionary<int, string?>> ReadPythonCommands(List<int> processIds, CancellationToken ct)
    {
        if (processIds.Count == 0) return [];
        var powershell = Commands.Find("powershell.exe");
        if (powershell is not null)
        {
            try
            {
                const string script = "ConvertTo-Json -Compress -InputObject @(Get-CimInstance Win32_Process -Filter \"Name='python.exe' OR Name='pythonw.exe'\" | Select-Object ProcessId,CommandLine)";
                var json = await Commands.Run(powershell, ["-NoProfile", "-NonInteractive", "-Command", script], Environment.CurrentDirectory, null, ct, timeout: TimeSpan.FromSeconds(15));
                using var document = JsonDocument.Parse(json);
                return document.RootElement.EnumerateArray().ToDictionary(row => row.GetProperty("ProcessId").GetInt32(),
                    row => row.GetProperty("CommandLine").ValueKind == JsonValueKind.String ? row.GetProperty("CommandLine").GetString() : null);
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }
        var commands = new Dictionary<int, string?>();
        foreach (var pid in processIds) commands[pid] = await ServiceManager.ReadProcessCommandLineAsync(pid, ct);
        return commands;
    }

    public async Task<ComfyUpdateBackup> UpdateAsync(LauncherConfig config, ComfyRelease release, RuntimeManifest runtime,
        ComfyDependencySnapshot dependencies, Func<LauncherConfig, ComfyDependencySnapshot, CancellationToken, Task> installDependencies,
        Func<LauncherConfig, CancellationToken, Task> validate, CancellationToken ct)
    {
        Safety.Commit(release.Commit);
        if (!dependencies.Resolved || dependencies.ComfyCommit != release.Commit) throw new InvalidOperationException("更新前必须完成目标版本的依赖解析。");
        if (new NodeUpdateCoordinator(stateDir, git).Previous(config)?.Status is "updating" or "restoring" or "rollback-failed")
            throw new InvalidOperationException("上次节点更新尚未恢复，请先回退节点更新。");
        await ValidateEnvironmentAsync(config, ct);
        var root = Full(config.ComfyRoot);
        var pythonDirectory = IndependentPythonDirectory(config);
        var previous = (await Git(root, ["rev-parse", "HEAD"], ct)).Trim();
        if (previous == release.Commit) throw new InvalidOperationException("此环境已经是所选稳定版本。");
        var previousBackup = Previous(config);
        if (previousBackup?.Status is "updating" or "restoring" or "rollback-failed") throw new InvalidOperationException("上次更新未完成，请先点击回退恢复环境。");
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
        var fetched = (await Git(root, ["rev-parse", "FETCH_HEAD"], ct)).Trim();
        if (fetched != release.Commit) throw new InvalidDataException("下载的 Git 提交与固定稳定版本不一致。");
        if ((await Git(root, ["ls-tree", "-r", release.Commit], ct)).Split('\n').Any(line => line.StartsWith("120000 ", StringComparison.Ordinal) || line.StartsWith("160000 ", StringComparison.Ordinal)))
            throw new InvalidOperationException("目标版本包含符号链接或子模块，无法完整回滚，未更新。");
        // Check the remotely read dependency instructions against Git's verified objects.
        foreach (var (name, expected) in new[] { ("requirements.txt", dependencies.RequirementsText), ("manager_requirements.txt", dependencies.ManagerRequirementsText) })
            if (NormalizeText(await Git(root, ["show", release.Commit + ":" + name], ct)) != NormalizeText(expected))
                throw new InvalidDataException("稳定版依赖清单与已验证 Git 提交不一致：" + name);
        var files = SplitFiles(await Git(root, ["ls-tree", "-r", "--name-only", "-z", previous], ct));
        var targets = SplitFiles(await Git(root, ["ls-tree", "-r", "--name-only", "-z", release.Commit], ct));
        foreach (var added in targets.Except(files, StringComparer.Ordinal))
        {
            var path = Safety.Under(root, added);
            CheckParents(root, path);
            var conflict = File.Exists(path);
            if (Directory.Exists(path))
            {
                _ = DirectoryBytes(path); // Reject linked descendants before enumerating.
                var tracked = files.ToHashSet(StringComparer.OrdinalIgnoreCase);
                conflict = !files.Any(file => file.StartsWith(added.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase))
                    || Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Any(file =>
                    !tracked.Contains(Path.GetRelativePath(root, file).Replace('\\', '/')));
            }
            if (conflict)
                throw new IOException("新版源码与已有未跟踪文件冲突，已保留原文件：" + path);
        }
        var runtimeRoot = InstallationPlanner.ResolveRuntimeRoot(config, stateDir);
        if (Same(runtimeRoot, root) || Full(runtimeRoot).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("更新备份目录必须位于 ComfyUI 目录之外。");
        var backupRoot = Path.Combine(runtimeRoot, "comfy-backups", DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + previous[..12] + "-" + Guid.NewGuid().ToString("N")[..6]);
        var configurations = ConfigurationPaths.Select(relative =>
        {
            var path = Safety.Under(root, relative);
            if (File.Exists(path) || Directory.Exists(path)) { CheckParents(root, path); RejectLink(path); }
            return new ComfyConfigurationEntry(relative, File.Exists(path) || Directory.Exists(path), Directory.Exists(path));
        }).ToArray();
        var required = DirectoryBytes(Path.Combine(root, ".git")) + DirectoryBytes(pythonDirectory)
            + files.Sum(path => File.Exists(Safety.Under(root, path)) ? new FileInfo(Safety.Under(root, path)).Length : 0)
            + configurations.Where(item => item.Existed).Sum(item => item.IsDirectory ? DirectoryBytes(Safety.Under(root, item.RelativePath)) : new FileInfo(Safety.Under(root, item.RelativePath)).Length);
        // Rollback stages replacement trees on the environment drive, which may
        // differ from the user's chosen backup/cache drive.
        if (string.Equals(Path.GetPathRoot(Full(runtimeRoot)), Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase))
            Safety.FreeSpace(runtimeRoot, checked(required * 2 + 512L * 1024 * 1024));
        else
        {
            Safety.FreeSpace(runtimeRoot, checked(required + 512L * 1024 * 1024));
            Safety.FreeSpace(root, checked(required + 512L * 1024 * 1024));
        }
        log?.Invoke($"备份完整 Git 与 Python 环境（{required / 1073741824d:N1} GiB）：{backupRoot}");
        await Task.Run(() =>
        {
            Directory.CreateDirectory(backupRoot);
            CopyDirectory(Path.Combine(root, ".git"), Path.Combine(backupRoot, "git"), ct);
            CopyDirectory(pythonDirectory, Path.Combine(backupRoot, "venv"), ct);
            foreach (var item in configurations.Where(item => item.Existed))
            {
                var source = Safety.Under(root, item.RelativePath);
                var destination = Safety.Under(Path.Combine(backupRoot, "configuration"), item.RelativePath);
                if (item.IsDirectory) CopyDirectory(source, destination, ct);
                else { Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(source, destination); }
            }
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var source = Safety.Under(root, file);
                CheckParents(root, source);
                if (!File.Exists(source)) continue;
                RejectLink(source);
                var destination = Safety.Under(Path.Combine(backupRoot, "source"), file);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination);
            }
        }, ct);
        var backup = new ComfyUpdateBackup(root, config.Python, previous, release.Commit, backupRoot, files, targets, "updating", DateTimeOffset.UtcNow)
        {
            RevisionMarker = ReadOptional(Path.Combine(root, ".anima-revision.json")),
            ConstraintsFile = ReadOptional(Path.Combine(root, ".anima-constraints.txt")),
            PythonDirectory = pythonDirectory,
            ConfigurationJson = JsonSerializer.Serialize(config, JsonFile.Options),
            ConfigurationPaths = configurations,
        };
        await EnsureStoppedAsync(config, ct);
        Save(config, backup);
        try
        {
            await Git(root, ["checkout", "--detach", release.Commit], ct);
            await installDependencies(config, dependencies, ct);
            await validate(config, ct);
            if (backup.RevisionMarker is not null) JsonFile.Write(Path.Combine(root, ".anima-revision.json"), runtime.Comfy with
                { Commit = release.Commit, Url = config.CustomRepository.Length > 0 ? config.CustomRepository : runtime.Comfy.Url });
            backup = backup with { Status = "complete" };
            Save(config, backup);
            log?.Invoke("ComfyUI 已更新至 " + release.Tag + "，完整旧环境已保留，可手动回退。");
            return backup;
        }
        catch (Exception error)
        {
            try { await RestoreAsync(config, backup, CancellationToken.None); }
            catch (Exception restoreError)
            {
                Save(config, backup with { Status = "rollback-failed" });
                throw new AggregateException("更新失败，自动回退未完成；完整备份已保留，请关闭服务后点击回退。", error, restoreError);
            }
            if (error is OperationCanceledException) throw;
            throw new IOException("更新失败，已恢复原 Git 源码和完整 Python 环境：" + error.Message, error);
        }
    }

    public async Task RollbackAsync(LauncherConfig config, CancellationToken ct)
    {
        var backup = Previous(config) ?? throw new InvalidOperationException("此环境没有可回退的更新备份。");
        if (backup.Status == "rolled-back") throw new InvalidOperationException("此备份已经回退。");
        var nodes = new NodeUpdateCoordinator(stateDir, git).Previous(config);
        if (nodes?.Status is "updating" or "restoring" or "rollback-failed" || nodes?.Status == "complete" && nodes.CreatedAt > backup.CreatedAt)
            throw new InvalidOperationException("节点更新发生在此 ComfyUI 备份之后，请先回退节点更新，再回退 ComfyUI。");
        if (backup.Status == "complete") await ValidateEnvironmentAsync(config, ct);
        await EnsureStoppedAsync(config, ct);
        await RestoreAsync(config, backup, ct);
    }

    private async Task RestoreAsync(LauncherConfig config, ComfyUpdateBackup backup, CancellationToken ct)
    {
        if (!Same(backup.Root, config.ComfyRoot) || !Same(backup.Python, config.Python)) throw new InvalidDataException("备份属于其他环境。");
        await EnsureStoppedAsync(config, ct);
        var root = Full(config.ComfyRoot);
        RejectLink(root);
        foreach (var name in new[] { ".anima-revision.json", ".anima-constraints.txt" })
        {
            var path = Path.Combine(root, name);
            if (File.Exists(path)) RejectLink(path);
        }
        LauncherConfig? savedConfiguration = null;
        if (backup.ConfigurationJson.Length > 0)
        {
            savedConfiguration = JsonSerializer.Deserialize<LauncherConfig>(backup.ConfigurationJson, JsonFile.Options) ?? throw new InvalidDataException("配置备份无效。");
            savedConfiguration.Validate();
            if (!Same(savedConfiguration.ComfyRoot, backup.Root) || !Same(savedConfiguration.Python, backup.Python)) throw new InvalidDataException("配置备份的环境身份不匹配。");
        }
        if (backup.ConfigurationPaths.Select(item => item.RelativePath).Distinct(StringComparer.Ordinal).Count() != backup.ConfigurationPaths.Length
            || backup.ConfigurationPaths.Any(item => !ConfigurationPaths.Contains(item.RelativePath, StringComparer.Ordinal)))
            throw new InvalidDataException("备份配置路径超出允许范围或重复。");
        foreach (var item in backup.ConfigurationPaths)
        {
            var path = Safety.Under(root, item.RelativePath);
            CheckParents(root, path);
            if (Directory.Exists(path)) _ = DirectoryBytes(path);
            else if (File.Exists(path)) RejectLink(path);
        }
        foreach (var file in backup.SourceFiles.Concat(backup.TargetFiles).Distinct(StringComparer.Ordinal))
        {
            var source = Safety.Under(Path.Combine(backup.Directory, "source"), file);
            CheckParents(backup.Directory, source);
            if (File.Exists(source)) RejectLink(source);
            var destination = Safety.Under(root, file);
            CheckParents(root, destination);
            if (File.Exists(destination) || Directory.Exists(destination)) RejectLink(destination);
        }
        var pythonDirectory = backup.PythonDirectory.Length > 0 ? backup.PythonDirectory : Path.Combine(root, ".venv");
        // A crash between directory renames can leave the live Python directory
        // absent. Validate its recorded layout without requiring its files to exist.
        var recognizedPython = new[] { Path.Combine(root, ".venv"), Path.Combine(root, "venv") }
            .Any(directory => Same(directory, pythonDirectory) && Same(config.Python, Path.Combine(directory, "Scripts", "python.exe")))
            || new[] { root, Path.GetDirectoryName(root)! }.SelectMany(parent => new[] { "python_embeded", "python_embedded" }.Select(name => Path.Combine(parent, name)))
                .Any(directory => Same(directory, pythonDirectory) && Same(config.Python, Path.Combine(directory, "python.exe")));
        if (!recognizedPython) throw new InvalidDataException("Python 目录与备份不一致，未回退。");
        CheckParents(Path.GetDirectoryName(root)!, pythonDirectory);
        if (Directory.Exists(pythonDirectory)) RejectLink(pythonDirectory);
        if (Directory.Exists(Path.Combine(root, ".git"))) RejectLink(Path.Combine(root, ".git"));
        if (!Directory.Exists(Path.Combine(backup.Directory, "git")) || !Directory.Exists(Path.Combine(backup.Directory, "venv")))
            throw new IOException("完整更新备份不齐全，未回退。");
        // Copy both replacement trees completely before replacing the active ones.
        var staged = Path.Combine(root, ".anima-restore-" + Guid.NewGuid().ToString("N"));
        var restoreBytes = DirectoryBytes(Path.Combine(backup.Directory, "git")) + DirectoryBytes(Path.Combine(backup.Directory, "venv"))
            + (Directory.Exists(Path.Combine(backup.Directory, "configuration")) ? DirectoryBytes(Path.Combine(backup.Directory, "configuration")) : 0);
        Safety.FreeSpace(root, restoreBytes);
        await Task.Run(() =>
        {
            CopyDirectory(Path.Combine(backup.Directory, "git"), Path.Combine(staged, "git"), ct);
            CopyDirectory(Path.Combine(backup.Directory, "venv"), Path.Combine(staged, "venv"), ct);
            foreach (var item in backup.ConfigurationPaths.Where(item => item.Existed))
            {
                var source = Safety.Under(Path.Combine(backup.Directory, "configuration"), item.RelativePath);
                var destination = Safety.Under(Path.Combine(staged, "configuration"), item.RelativePath);
                CheckParents(backup.Directory, source);
                RejectLink(source);
                if (item.IsDirectory) CopyDirectory(source, destination, ct);
                else { Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(source, destination); }
            }
        }, ct);
        ct.ThrowIfCancellationRequested();
        await EnsureStoppedAsync(config, ct);
        Save(config, backup with { Status = "restoring" });
        // Once swaps begin, restoration is deliberately non-cancellable.
        void RetireSource(string path)
        {
            CheckParents(root, path);
            RejectLink(path);
            var retired = Safety.Under(Path.Combine(staged, "changed-source"), Path.GetRelativePath(root, path)) + "-" + Guid.NewGuid().ToString("N")[..6];
            Directory.CreateDirectory(Path.GetDirectoryName(retired)!);
            if (Directory.Exists(path)) Directory.Move(path, retired); else File.Move(path, retired);
        }
        foreach (var (destination, sourceName, retiredName) in new[] { (Path.Combine(root, ".git"), "git", "failed-git"), (pythonDirectory, "venv", "failed-python") })
        {
            var retired = Path.Combine(staged, retiredName);
            if (Directory.Exists(destination)) Directory.Move(destination, retired);
            Directory.Move(Path.Combine(staged, sourceName), destination);
        }
        foreach (var file in backup.TargetFiles.Except(backup.SourceFiles, StringComparer.Ordinal))
        {
            var target = Safety.Under(root, file);
            CheckParents(root, target);
            if (File.Exists(target)) RetireSource(target);
        }
        foreach (var file in backup.SourceFiles)
        {
            var source = Safety.Under(Path.Combine(backup.Directory, "source"), file);
            CheckParents(backup.Directory, source);
            if (!File.Exists(source)) continue;
            RejectLink(source);
            var destination = Safety.Under(root, file);
            CheckParents(root, destination);
            if (Directory.Exists(destination)) RetireSource(destination);
            else if (File.Exists(destination)) RejectLink(destination);
            var parent = Path.GetDirectoryName(destination);
            while (parent is not null && !Same(parent, root))
            {
                if (File.Exists(parent)) RetireSource(parent);
                parent = Path.GetDirectoryName(parent);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, true);
        }
        RestoreOptional(Path.Combine(root, ".anima-revision.json"), backup.RevisionMarker);
        RestoreOptional(Path.Combine(root, ".anima-constraints.txt"), backup.ConstraintsFile);
        foreach (var item in backup.ConfigurationPaths)
        {
            var destination = Safety.Under(root, item.RelativePath);
            CheckParents(root, destination);
            if (File.Exists(destination) || Directory.Exists(destination)) RetireSource(destination);
            if (!item.Existed) continue;
            var source = Safety.Under(Path.Combine(staged, "configuration"), item.RelativePath);
            if (item.IsDirectory) Directory.Move(source, destination); else File.Move(source, destination);
        }
        if (savedConfiguration is not null) JsonFile.Write(Path.Combine(stateDir, "launcher.json"), savedConfiguration);
        Save(config, backup with { Status = "rolled-back" });
        // Failed trees are retained for diagnosis; never remove the only recovery copy.
        log?.Invoke("已回退源码与完整 Python 环境；失败版本保留于 " + staged);
    }

    private void Save(LauncherConfig config, ComfyUpdateBackup backup)
    {
        JsonFile.Write(Path.Combine(backup.Directory, "backup.json"), backup);
        JsonFile.Write(Pointer(config), backup);
    }
    private static string NormalizeText(string text) => text.Replace("\r\n", "\n").TrimEnd();
    private static string[] SplitFiles(string text) => text.TrimEnd('\r', '\n').Split('\0', StringSplitOptions.RemoveEmptyEntries);
    internal static string? ReadOptional(string file) => File.Exists(file) ? File.ReadAllText(file) : null;
    internal static void RestoreOptional(string file, string? content) { if (content is null) { if (File.Exists(file)) File.Delete(file); } else File.WriteAllText(file, content); }
    internal static void RejectLink(string path) { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("更新备份不支持符号链接或连接目录：" + path); }
    internal static void CheckParents(string root, string path)
    {
        var current = Path.GetDirectoryName(Path.GetFullPath(path));
        var boundary = Full(root);
        while (current is not null && (Same(current, boundary) || current.StartsWith(boundary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
        {
            if (Directory.Exists(current)) RejectLink(current);
            if (Same(current, boundary)) break;
            current = Path.GetDirectoryName(current);
        }
    }
    internal static long DirectoryBytes(string path)
    {
        RejectLink(path);
        long size = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            RejectLink(entry);
            size = checked(size + (Directory.Exists(entry) ? DirectoryBytes(entry) : new FileInfo(entry).Length));
        }
        return size;
    }
    internal static void CopyDirectory(string source, string destination, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RejectLink(source);
        Directory.CreateDirectory(destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            ct.ThrowIfCancellationRequested();
            RejectLink(entry);
            var target = Path.Combine(destination, Path.GetFileName(entry));
            if (Directory.Exists(entry)) CopyDirectory(entry, target, ct); else File.Copy(entry, target, false);
        }
    }
}
