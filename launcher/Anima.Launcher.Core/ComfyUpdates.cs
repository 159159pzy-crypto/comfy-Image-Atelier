using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Anima.Launcher.Core;

public sealed record ComfyConfigurationEntry(string RelativePath, bool Existed, bool IsDirectory)
{
    public string Sha256 { get; init; } = "";
}
public sealed record ComfyUpdateBackup(string Root, string Python, string PreviousCommit, string TargetCommit,
    string Directory, string[] SourceFiles, string[] TargetFiles, string Status, DateTimeOffset CreatedAt)
{
    public string? RevisionMarker { get; init; }
    public string? ConstraintsFile { get; init; }
    public string PythonDirectory { get; init; } = "";
    public string ConfigurationJson { get; init; } = "";
    public ComfyConfigurationEntry[] ConfigurationPaths { get; init; } = [];
    // Missing fields identify historical complete backups. New updates never create one.
    public int FormatVersion { get; init; }
    public string RecoveryStrategy { get; init; } = "full";
    public string PreviousBranch { get; init; } = "";
    public string RecoveryRef { get; init; } = "";
    public string Phase { get; init; } = "";
    public bool DependenciesMayHaveChanged { get; init; }
    public string[] EnvironmentPackages { get; init; } = [];
    public Dictionary<string, string?> AppliedConfigurationHashes { get; init; } = new(StringComparer.Ordinal);
    public string RecoveryError { get; init; } = "";
    public bool CoreVerified { get; init; }
}

/// <summary>Manual updates retain a Git recovery reference and small configuration snapshots; historical complete backups remain recoverable.</summary>
public sealed partial class ComfyUpdateCoordinator(string stateDir, string git, Action<string>? log = null,
    Func<string, string, CancellationToken, Task>? fetchCommit = null,
    Func<LauncherConfig, CancellationToken, Task>? inspectStopped = null)
{
    public Action<string>? Progress { get; set; }
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
        config.RequireEnvironmentWrite();
        config.Validate();
        var root = Full(config.ComfyRoot);
        if (!Directory.Exists(Path.Combine(root, ".git")) || !File.Exists(Path.Combine(root, "main.py")) || !File.Exists(config.Python))
            throw new InvalidOperationException("更新需要独立 Git 仓库与有效的专用 Python；工作树或源码压缩包请使用原有方式更新。");
        _ = IndependentPythonDirectory(config);
        RejectLink(root);
        RejectLink(Path.Combine(root, ".git"));
        if (File.Exists(Path.Combine(root, ".git", "objects", "info", "alternates")))
            throw new InvalidOperationException("此 Git 仓库共享其他目录的对象，无法保证本地恢复提交可用，请先转换为独立克隆。");
        if (!Same((await Git(root, ["rev-parse", "--show-toplevel"], ct)).Trim(), root)) throw new InvalidOperationException("目标不是独立 Git 根目录。");
        if ((await Git(root, ["ls-files", "--stage"], ct)).Split('\n').Any(line => line.StartsWith("160000 ", StringComparison.Ordinal)))
            throw new InvalidOperationException("环境包含 Git 子模块，暂不支持原地更新，未更新。");
        if (clean && !string.IsNullOrWhiteSpace(await Git(root, ["status", "--porcelain", "--untracked-files=no"], ct)))
            throw new InvalidOperationException("ComfyUI 源码有本地修改，请先保存修改后再更新。");
        await EnsureStoppedAsync(config, ct);
    }

    public async Task EnsureStoppedAsync(LauncherConfig config, CancellationToken ct)
    {
        if (inspectStopped is not null) { await inspectStopped(config, ct); return; }
        if (await ServiceManager.PortOpen(config.ComfyPort, ct) || await ServiceManager.PortOpen(config.WebPort, ct))
            throw new InvalidOperationException($"环境端口 {config.ComfyPort} 或 {config.WebPort} 正在使用。请先确认生成队列已完成，关闭占用的服务后重新检查。");
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

    public async Task RollbackAsync(LauncherConfig config, CancellationToken ct,
        Func<LauncherConfig, CancellationToken, Task>? validateRecovery = null)
    {
        config.RequireEnvironmentWrite();
        var backup = Previous(config) ?? throw new InvalidOperationException("此环境没有可回退的更新备份。");
        if (backup.Status == "rolled-back") throw new InvalidOperationException("此备份已经回退。");
        var nodes = new NodeUpdateCoordinator(stateDir, git).Previous(config);
        if (nodes?.Status is "updating" or "restoring" or "rollback-failed" || nodes?.Status == "complete" && nodes.CreatedAt > backup.CreatedAt)
            throw new InvalidOperationException("节点更新发生在此 ComfyUI 备份之后，请先回退节点更新，再回退 ComfyUI。");
        if (backup.Status == "complete") await ValidateEnvironmentAsync(config, ct);
        await EnsureStoppedAsync(config, ct);
        if (IsLight(backup))
        {
            try { await RestoreLightAsync(config, backup, validateRecovery, ct); }
            catch (Exception error)
            {
                var current = Previous(config) ?? backup;
                if (current.Status != "needs-repair") Save(config, current with { Status = "rollback-failed", RecoveryError = error.Message, CoreVerified = false });
                throw;
            }
        }
        else
        {
            ValidateLegacyFormat(backup);
            await RestoreAsync(config, backup, ct);
        }
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
        if (backup.Phase.Length > 0) Progress?.Invoke(backup.Phase);
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
