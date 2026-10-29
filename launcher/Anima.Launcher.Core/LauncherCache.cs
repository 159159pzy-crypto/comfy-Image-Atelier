using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Anima.Launcher.Core;

public sealed record LauncherCacheContext(string CurrentAssets, string CurrentAppRoot, string CurrentExecutable, string CurrentVersion, bool IsBusy = false);
public sealed record LauncherCacheProcessSnapshot(bool Reliable, string[] ProtectedPaths, string? Reason = null);
public sealed record LauncherCacheReport(DateTimeOffset At, long ScannedBytes, long ReleasedBytes, string[] DeletedDirectories, string[] Skipped);
public sealed record LauncherCacheFile(string Path, long Size, string Sha256);
public sealed class LauncherCacheEntry
{
    public string Kind { get; set; } = "";
    public string Version { get; set; } = "";
    public string Directory { get; set; } = "";
    public string AppRoot { get; set; } = "";
    public string[] PackageHashes { get; set; } = [];
    public DateTimeOffset LastUsedAt { get; set; }
    public LauncherCacheFile[] Files { get; set; } = [];
    public LauncherCacheFile[]? PendingDeletionFiles { get; set; }
}
public sealed class LauncherCacheIndex
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset? LastMaintenanceAt { get; set; }
    public List<LauncherCacheEntry> Entries { get; set; } = [];
}

/// <summary>Collects only explicitly registered application snapshots. Never owns runtime, model, download or .NET extraction caches.</summary>
public sealed class LauncherCache
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    public static readonly TimeSpan MaintenanceInterval = TimeSpan.FromHours(24);
    private readonly string stateDirectory;
    private readonly Action<string> log;
    private readonly Func<DateTimeOffset> now;
    private readonly Func<CancellationToken, Task<LauncherCacheProcessSnapshot>>? inspectProcesses;
    private readonly Action<string, int>? afterFileDeleted;
    public string IndexPath => Path.Combine(stateDirectory, "cache-index.json");
    public string ReportPath => Path.Combine(stateDirectory, "cache-maintenance.json");

    public LauncherCache(string stateDirectory, Action<string>? log = null, Func<DateTimeOffset>? utcNow = null,
        Func<CancellationToken, Task<LauncherCacheProcessSnapshot>>? inspectProcesses = null, Action<string, int>? afterFileDeleted = null)
    {
        this.stateDirectory = Normalize(stateDirectory);
        this.log = log ?? (_ => { }); this.now = utcNow ?? (() => DateTimeOffset.UtcNow); this.inspectProcesses = inspectProcesses; this.afterFileDeleted = afterFileDeleted;
    }

    /// <summary>Shared across async operations and processes; unlike a Mutex this handle is not thread-affine.</summary>
    public static async Task<IDisposable> AcquireUseAsync(string stateDirectory, CancellationToken ct = default)
    {
        var root = Normalize(stateDirectory);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return OpenLease(root, "cache-maintenance.lock", FileShare.ReadWrite); }
            catch (IOException error) when (IsSharingViolation(error)) { await Task.Delay(100, ct); }
        }
    }

    public async Task RegisterSnapshotAsync(string version, string assets, string appRoot, CancellationToken ct = default, bool newlyCreated = false, string? packageSha256 = null)
    {
        var entry = new LauncherCacheEntry { Kind = "snapshot", Version = version, Directory = Normalize(assets), AppRoot = Normalize(appRoot),
            PackageHashes = packageSha256 is null ? [] : [packageSha256.ToLowerInvariant()], LastUsedAt = now() };
        ValidateEntry(entry);
        if (!File.Exists(Path.Combine(entry.AppRoot, "run.py"))) throw new IOException("工作台快照尚未完成，不能登记缓存。");
        await RegisterAsync(entry, ct, allowNew: newlyCreated);
    }

    public async Task RegisterPackageAsync(InstalledLauncher package, CancellationToken ct = default)
    {
        using var lease = await AcquireUseAsync(stateDirectory, ct);
        new LauncherUpdateStore(stateDirectory).ValidateInstalled(package, managed: true);
        if (!await Downloads.Verify(package.Executable, package.Size, package.Sha256, ct)) throw new IOException("未通过校验的启动器包不能登记缓存。");
        await RegisterVerifiedPackageAsync(package, ct);
    }

    /// <summary>Only for a caller that just hashed these exact bytes or atomically published a verified temporary file.
    /// Persisted update state alone is not verification. Maintenance always rehashes before any deletion.</summary>
    internal async Task RegisterVerifiedPackageAsync(InstalledLauncher package, CancellationToken ct = default)
    {
        using var lease = await AcquireUseAsync(stateDirectory, ct);
        new LauncherUpdateStore(stateDirectory).ValidateInstalled(package, managed: true);
        if (!File.Exists(package.Executable) || new FileInfo(package.Executable).Length != package.Size)
            throw new IOException("已验证的启动器包在登记前改变或缺失，已跳过缓存登记。");
        await RegisterAsync(new LauncherCacheEntry { Kind = "package", Version = package.Version,
            Directory = Normalize(Path.GetDirectoryName(package.Executable)!), PackageHashes = [package.Sha256.ToLowerInvariant()], LastUsedAt = now(),
            Files = [new("AnimaRandomStudio.exe", package.Size, package.Sha256.ToLowerInvariant())] }, ct);
    }

    /// <summary>Called under the launcher's outer shared lease before reusing an extracted snapshot.</summary>
    public async Task<bool> HasPendingSnapshotRepairAsync(string assets, CancellationToken ct = default)
    {
        using var lease = await AcquireUseAsync(stateDirectory, ct);
        using var indexLease = await AcquireIndexAsync(ct);
        var entry = Load().Entries.SingleOrDefault(item => Same(item.Directory, assets));
        if (entry is null) return false;
        if (entry.Kind != "snapshot") throw new InvalidDataException("修复目标不是工作台快照。");
        return entry.PendingDeletionFiles is not null;
    }

    /// <summary>The caller restores missing embedded files only. Changed or added user files must never be adopted.</summary>
    public async Task CompleteSnapshotRepairAsync(string assets, CancellationToken ct = default)
    {
        using var lease = await AcquireUseAsync(stateDirectory, ct);
        using var indexLease = await AcquireIndexAsync(ct);
        var index = Load();
        var entry = index.Entries.SingleOrDefault(item => Same(item.Directory, assets));
        if (entry is null || entry.Kind != "snapshot" || entry.PendingDeletionFiles is null)
            throw new InvalidDataException("没有可恢复的已登记工作台快照。");
        ValidateEntry(entry);
        var actual = await InventoryAsync(entry.Directory, ct);
        if (!SameInventory(entry.Files, actual)) throw new InvalidDataException("工作台快照仍有缺失、修改或新增文件；原文件已保留，不能复用此快照。");
        entry.PendingDeletionFiles = null;
        entry.LastUsedAt = now();
        JsonFile.Write(IndexPath, index);
    }

    private async Task RegisterAsync(LauncherCacheEntry entry, CancellationToken ct, bool allowNew = true)
    {
        using var lease = await AcquireUseAsync(stateDirectory, ct);
        using var indexLease = await AcquireIndexAsync(ct);
        var index = Load();
        var existing = index.Entries.SingleOrDefault(item => Same(item.Directory, entry.Directory));
        if (existing is not null)
        {
            if (existing.Kind != entry.Kind || existing.Version != entry.Version || !SameOrEmpty(existing.AppRoot, entry.AppRoot))
                throw new InvalidDataException("缓存目录登记信息冲突，已保留原记录。");
            existing.LastUsedAt = now();
            existing.PackageHashes = existing.PackageHashes.Concat(entry.PackageHashes).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        else
        {
            if (!allowNew) { log("旧工作台目录未登记，保持原样：" + entry.Directory); return; }
            if (entry.Kind == "package")
            {
                var files = EnumerateRegularFiles(entry.Directory).ToArray();
                if (files.Length != 1 || Path.GetFileName(files[0]) != "AnimaRandomStudio.exe")
                { log("更新目录含有额外文件，保持原样：" + entry.Directory); return; }
            }
            else entry.Files = await InventoryAsync(entry.Directory, ct);
            index.Entries.Add(entry);
        }
        JsonFile.Write(IndexPath, index);
    }

    public async Task<LauncherCacheReport> MaintainAsync(LauncherCacheContext context, CancellationToken ct = default)
    {
        var at = now(); var skipped = new List<string>(); var deleted = new List<string>(); long scanned = 0, released = 0;
        LauncherCacheReport Result(string? reason = null)
        {
            if (reason is not null) skipped.Add(reason);
            return new(at, scanned, released, deleted.ToArray(), skipped.ToArray());
        }
        if (context.IsBusy) return Result("业务任务仍在运行，缓存清理已延后。");
        FileStream maintenance;
        try { maintenance = OpenLease(stateDirectory, "cache-maintenance.lock", FileShare.None); }
        catch (IOException error) when (IsSharingViolation(error)) { return Result("资源正在使用，缓存清理已延后。"); }
        using (maintenance)
        {
            try
            {
                using var indexLease = await AcquireIndexAsync(ct);
                var index = Load();
                if (index.LastMaintenanceAt is { } previous && at - previous < MaintenanceInterval) return Result("距上次缓存检查不足 24 小时。");
                var protectedPaths = ReadProtectedPaths(context, out var protectedVersions, out var protectedHashes);
                var live = inspectProcesses is not null ? await inspectProcesses(ct) : await InspectProcessesAsync(index, ct);
                if (!live.Reliable) return Result(live.Reason ?? "无法确认在用进程，保留全部缓存。");
                foreach (var path in live.ProtectedPaths) AddPath(protectedPaths, path);
                foreach (var entry in index.Entries.ToArray())
                {
                    ct.ThrowIfCancellationRequested();
                    ValidateEntry(entry);
                    if (!Directory.Exists(entry.Directory)) { index.Entries.Remove(entry); continue; }
                    scanned += entry.Files.Sum(file => file.Size);
                    if (protectedPaths.Any(path => Related(path, entry.Directory)) || ProtectedSnapshot(entry, protectedVersions, protectedHashes))
                    { entry.LastUsedAt = at; skipped.Add("保留使用中或可回退缓存：" + entry.Directory); continue; }
                    if (at - entry.LastUsedAt < Retention) { skipped.Add("保留七天内使用的缓存：" + entry.Directory); continue; }
                    try
                    {
                        var files = await InventoryAsync(entry.Directory, ct);
                        if (!(entry.PendingDeletionFiles is { } pending ? IsUnchangedSubset(pending, files) : SameInventory(entry.Files, files)))
                        { skipped.Add("目录内容已变化，保留：" + entry.Directory); continue; }
                        // The exclusive maintenance lease prevents new participating launches/transitions.
                        // Re-read durable pointers immediately before deleting each immutable tree.
                        var latest = ReadProtectedPaths(context, out var latestVersions, out var latestHashes);
                        if (latest.Any(path => Related(path, entry.Directory)) || ProtectedSnapshot(entry, latestVersions, latestHashes)) continue;
                        var latestProcesses = inspectProcesses is not null ? await inspectProcesses(ct) : await InspectProcessesAsync(index, ct);
                        if (!latestProcesses.Reliable)
                        { skipped.Add(latestProcesses.Reason ?? "删除前无法再次确认进程身份，停止本轮清理。"); break; }
                        if (latestProcesses.ProtectedPaths.Any(path => Related(path, entry.Directory)))
                        { entry.LastUsedAt = at; skipped.Add("删除前发现工作台已启动，保留：" + entry.Directory); continue; }
                        ValidateEntry(entry);
                        if (entry.PendingDeletionFiles is null)
                        {
                            entry.PendingDeletionFiles = files;
                            JsonFile.Write(IndexPath, index); // Crash recovery may only remove an unchanged subset of this exact manifest.
                        }
                        DeleteTree(entry.Directory, files, bytes => released += bytes);
                        deleted.Add(entry.Directory); index.Entries.Remove(entry);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
                    { skipped.Add("无法安全清理，保留剩余文件：" + entry.Directory + " · " + error.Message); }
                }
                foreach (var root in new[] { Path.Combine(stateDirectory, "versions"), Path.Combine(stateDirectory, "launcher-updates") })
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (var directory in Directory.EnumerateDirectories(root))
                        if (!index.Entries.Any(entry => Same(entry.Directory, directory))) skipped.Add("未登记目录保持原样：" + directory);
                }
                index.LastMaintenanceAt = at; JsonFile.Write(IndexPath, index);
                var report = Result(); JsonFile.Write(ReportPath, report);
                log($"缓存检查完成：已检查 {scanned} 字节，释放 {released} 字节，删除 {deleted.Count} 个目录；共享 .NET 解压缓存保持原样。");
                return report;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException)
            { log("缓存检查已跳过：" + error.Message); return Result("无法完整确认缓存引用，保留：" + error.Message); }
        }
    }

    private HashSet<string> ReadProtectedPaths(LauncherCacheContext context, out HashSet<string> versions, out HashSet<string> hashes)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase); versions = new(StringComparer.Ordinal); hashes = new(StringComparer.OrdinalIgnoreCase);
        AddPath(paths, context.CurrentAssets); AddPath(paths, context.CurrentAppRoot); AddPath(paths, context.CurrentExecutable);
        if (!string.IsNullOrWhiteSpace(context.CurrentVersion)) versions.Add(context.CurrentVersion);
        void Config(LauncherConfig config) { config.Validate(validateArguments: false); AddPath(paths, config.AppRoot); AddPath(paths, config.PinnedAppRoot); }
        foreach (var name in new[] { "launcher.json", "launcher.previous.json" })
        { var path = Path.Combine(stateDirectory, name); if (File.Exists(path)) Config(JsonFile.Read<LauncherConfig>(path)); }
        var onboarding = Path.Combine(stateDirectory, "onboarding.json");
        if (File.Exists(onboarding)) { var value = JsonFile.Read<SetupRecord>(onboarding); if (value.SchemaVersion != 1) throw new InvalidDataException("未知向导记录版本。"); Config(value.Draft); }
        var store = new LauncherUpdateStore(stateDirectory); var state = store.Load();
        var retainedVersions = versions;
        var retainedHashes = hashes;
        void Package(InstalledLauncher? package)
        {
            if (package is null) return;
            store.ValidateInstalled(package, managed: false); AddPath(paths, package.Executable); retainedVersions.Add(package.Version); retainedHashes.Add(package.Sha256);
        }
        Package(state.Active); Package(state.Previous); Package(state.Downloaded);
        if (state.PendingTransactionId is { } pending)
        { var transaction = store.ReadTransaction(pending); Package(transaction.Candidate); Package(transaction.Previous); Config(transaction.PreviousConfig); }
        var transactionRoot = Path.Combine(store.Root, "transactions");
        if (Directory.Exists(transactionRoot))
            foreach (var file in Directory.EnumerateFiles(transactionRoot, "*.json"))
            {
                var transaction = store.ReadTransaction(Path.GetFileNameWithoutExtension(file));
                if (transaction.Phase is "committed" or "failed") continue;
                Package(transaction.Candidate); Package(transaction.Previous); Config(transaction.PreviousConfig);
            }
        var downloads = new DownloadTaskStore(stateDirectory); var tasks = downloads.Load();
        if (downloads.LoadError is not null) throw new InvalidDataException(downloads.LoadError);
        if (tasks.Tasks.Any(task => task.Status is DownloadTaskStatus.Running or DownloadTaskStatus.Queued or DownloadTaskStatus.Pausing)
            || tasks.Owners.Any(owner => owner.Status is DownloadTaskStatus.Running or DownloadTaskStatus.Queued or DownloadTaskStatus.Pausing))
            throw new IOException("下载或安装任务仍在运行。");
        foreach (var owner in tasks.Owners.Where(owner => owner.Status != DownloadTaskStatus.Completed)) Config(owner.Configuration);
        foreach (var task in tasks.Tasks.Where(task => task.Status != DownloadTaskStatus.Completed)) AddPath(paths, task.Destination);
        return paths;
    }

    private async Task<LauncherCacheProcessSnapshot> InspectProcessesAsync(LauncherCacheIndex index, CancellationToken ct)
    {
        var paths = new List<string>();
        var processFile = Path.Combine(stateDirectory, "processes.json");
        var recorded = File.Exists(processFile) ? JsonFile.Read<OwnedProcess[]>(processFile) : [];
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var name = process.ProcessName;
                    if (process.Id == Environment.ProcessId) continue;
                    var known = recorded.FirstOrDefault(record => record.Pid == process.Id);
                    if (!name.StartsWith("python", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("Anima", StringComparison.OrdinalIgnoreCase) && known is null) continue;
                    if (process.HasExited) continue;
                    var started = process.StartTime.ToUniversalTime().Ticks;
                    var executable = process.MainModule?.FileName;
                    if (string.IsNullOrEmpty(executable)) return new(false, [], "无法确认相关进程路径，保留缓存。");
                    paths.Add(executable);
                    if (name.StartsWith("Anima", StringComparison.OrdinalIgnoreCase)) return new(false, [], "另一个启动器进程仍在运行，延后缓存清理。");
                    var commandLine = await ServiceManager.ReadProcessCommandLineAsync(process.Id, ct);
                    if (commandLine is null) return new(false, [], "无法确认 Python 工作台目录，保留缓存。");
                    using var fresh = Process.GetProcessById(process.Id);
                    if (fresh.HasExited || fresh.StartTime.ToUniversalTime().Ticks != started || !Same(fresh.MainModule?.FileName ?? "", executable))
                        return new(false, [], "进程在检查中发生变化，保留缓存。");
                    var classification = ClassifyPythonCommandLine(commandLine);
                    if (!classification.Reliable) return classification;
                    paths.AddRange(classification.ProtectedPaths);
                }
                catch (InvalidOperationException) { /* The process exited during inspection. */ }
                catch (System.ComponentModel.Win32Exception) { return new(false, [], "相关进程身份不可读取，保留缓存。"); }
            }
        }
        return new(true, paths.ToArray());
    }

    internal static LauncherCacheProcessSnapshot ClassifyPythonCommandLine(string commandLine)
    {
        try
        {
            var arguments = ComfyLaunchArguments.Parse(commandLine);
            if (arguments.Count < 2) return new(false, [], "Python 的启动方式无法确认，保留缓存。");
            var scripts = arguments.Skip(1).Where(argument => Path.GetFileName(argument).Equals("run.py", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (scripts.Any(script => !Path.IsPathFullyQualified(script)) || arguments.Contains("-c")
                || arguments.Contains("-m") && arguments.Any(argument => argument.StartsWith("anima_webui", StringComparison.OrdinalIgnoreCase))
                || scripts.Length == 0 && commandLine.Contains("run.py", StringComparison.OrdinalIgnoreCase))
                return new(false, [], "Python 使用相对或不明确的工作台入口，保留缓存。");
            return new(true, scripts.Select(script => Normalize(Path.GetDirectoryName(script)!)).ToArray());
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException)
        { return new(false, [], "Python 命令行无法解析，保留缓存。"); }
    }

    private LauncherCacheIndex Load()
    {
        if (File.Exists(IndexPath) && (File.GetAttributes(IndexPath) & FileAttributes.ReparsePoint) != 0) throw new IOException("缓存索引不能是链接。");
        var index = File.Exists(IndexPath) ? JsonFile.Read<LauncherCacheIndex>(IndexPath) : new();
        if (index.SchemaVersion != 1 || index.Entries is null || index.Entries.Count > 10000) throw new InvalidDataException("缓存索引格式未知。");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in index.Entries)
        {
            ValidateEntry(entry);
            if (!seen.Add(entry.Directory) || entry.Files is null) throw new InvalidDataException("缓存索引重复或不完整。");
            ValidateManifest(entry.Directory, entry.Files);
            if (entry.PendingDeletionFiles is { } pending) ValidateManifest(entry.Directory, pending);
        }
        return index;
    }

    private void ValidateEntry(LauncherCacheEntry entry)
    {
        LauncherUpdates.ParseVersion(entry.Version);
        if (entry.PackageHashes is null || entry.PackageHashes.Any(hash => !Regex.IsMatch(hash ?? "", "^[a-f0-9]{64}$"))) throw new InvalidDataException("缓存关联的启动器哈希无效。");
        var parent = entry.Kind switch { "snapshot" => "versions", "package" => "launcher-updates", _ => throw new InvalidDataException("未知缓存类型。") };
        var root = Path.Combine(stateDirectory, parent);
        if (!Path.IsPathFullyQualified(entry.Directory) || !Same(Path.GetDirectoryName(entry.Directory)!, root)
            || !Regex.IsMatch(Path.GetFileName(entry.Directory), "^" + Regex.Escape(entry.Version) + "-[a-f0-9]{16}$"))
            throw new InvalidDataException("缓存目录不属于当前启动器。");
        LauncherUpdateStore.RequireNoLinks(entry.Directory);
        if (entry.Kind == "snapshot" && (!Path.IsPathFullyQualified(entry.AppRoot) || !Same(Path.GetDirectoryName(entry.AppRoot)!, entry.Directory)
            || !Regex.IsMatch(Path.GetFileName(entry.AppRoot), "^app-[a-f0-9]{16}$"))) throw new InvalidDataException("工作台缓存路径无效。");
    }

    private static async Task<LauncherCacheFile[]> InventoryAsync(string directory, CancellationToken ct)
    {
        var result = new List<LauncherCacheFile>();
        foreach (var file in EnumerateRegularFiles(directory))
        {
            ct.ThrowIfCancellationRequested();
            await using var input = File.OpenRead(file);
            result.Add(new(Path.GetRelativePath(directory, file), input.Length, Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).ToLowerInvariant()));
        }
        return result.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static IEnumerable<string> EnumerateRegularFiles(string directory)
    {
        LauncherUpdateStore.RequireNoLinks(directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("缓存中存在链接。");
            if ((attributes & FileAttributes.Directory) != 0) { foreach (var file in EnumerateRegularFiles(entry)) yield return file; }
            else yield return entry;
        }
    }
    private static bool DerivedBytecode(string path) => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains("__pycache__") && path.EndsWith(".pyc", StringComparison.OrdinalIgnoreCase);
    private static void ValidateManifest(string directory, LauncherCacheFile[] files)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (file is null || string.IsNullOrWhiteSpace(file.Path) || !seen.Add(file.Path) || file.Size < 0 || !Regex.IsMatch(file.Sha256 ?? "", "^[a-f0-9]{64}$"))
                throw new InvalidDataException("缓存文件清单无效。");
            _ = Safety.Under(directory, file.Path);
        }
    }
    private static bool ProtectedSnapshot(LauncherCacheEntry entry, HashSet<string> versions, HashSet<string> hashes) => entry.Kind == "snapshot"
        && (entry.PackageHashes.Length == 0 ? versions.Contains(entry.Version) : entry.PackageHashes.Any(hashes.Contains));
    private static bool SameInventory(LauncherCacheFile[] expected, LauncherCacheFile[] actual)
    {
        var left = expected.Where(file => !DerivedBytecode(file.Path)).OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var right = actual.Where(file => !DerivedBytecode(file.Path)).OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase);
        return left.SequenceEqual(right);
    }
    private static bool IsUnchangedSubset(LauncherCacheFile[] expected, LauncherCacheFile[] actual) => actual.All(file => expected.Contains(file));
    private void DeleteTree(string directory, LauncherCacheFile[] inventory, Action<long> released)
    {
        // No recursive shell operation: check every file and only remove empty directories afterward.
        LauncherUpdateStore.RequireNoLinks(directory);
        var handles = new List<FileStream>();
        try
        {
            // Obtain all handles before deleting anything, so a locked runtime file preserves its whole snapshot.
            foreach (var file in inventory)
            {
                var full = Safety.Under(directory, file.Path);
                if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new IOException("缓存文件是链接。");
                handles.Add(new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Delete));
            }
            for (var i = 0; i < handles.Count; i++)
                if (handles[i].Length != inventory[i].Size || !Convert.ToHexString(SHA256.HashData(handles[i])).Equals(inventory[i].Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("缓存内容在检查后发生变化，已保留整个目录。");
            var deleted = 0;
            foreach (var file in inventory)
            {
                var path = Safety.Under(directory, file.Path); File.Delete(path); released(file.Size);
                afterFileDeleted?.Invoke(path, ++deleted);
            }
        }
        finally { foreach (var handle in handles) handle.Dispose(); }
        RemoveEmptyDirectories(directory);
    }
    private static void RemoveEmptyDirectories(string directory)
    {
        LauncherUpdateStore.RequireNoLinks(directory);
        foreach (var child in Directory.EnumerateDirectories(directory)) RemoveEmptyDirectories(child);
        Directory.Delete(directory, recursive: false);
    }
    private async Task<FileStream> AcquireIndexAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return OpenLease(stateDirectory, "cache-index.lock", FileShare.None); }
            catch (IOException error) when (IsSharingViolation(error)) { await Task.Delay(50, ct); }
        }
    }
    private static FileStream OpenLease(string root, string name, FileShare share)
    {
        LauncherUpdateStore.RequireNoLinks(root); Directory.CreateDirectory(root);
        var path = Path.Combine(root, name);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("缓存锁文件不能是链接。");
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, share);
    }
    private static bool IsSharingViolation(IOException error) => (error.HResult & 0xffff) is 32 or 33 or 11;
    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool Same(string a, string b) => Normalize(a).Equals(Normalize(b), StringComparison.OrdinalIgnoreCase);
    private static bool SameOrEmpty(string a, string b) => a.Length == 0 && b.Length == 0 || a.Length > 0 && b.Length > 0 && Same(a, b);
    private static bool Related(string path, string directory) => Same(path, directory) || Normalize(path).StartsWith(Normalize(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static void AddPath(HashSet<string> paths, string path) { if (!string.IsNullOrWhiteSpace(path)) paths.Add(Normalize(path)); }
}
