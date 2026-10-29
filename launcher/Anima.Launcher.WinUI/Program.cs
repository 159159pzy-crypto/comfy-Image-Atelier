using Anima.Launcher.Core;
using Microsoft.UI.Xaml;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Anima.Launcher;

internal static class Program
{
    private static readonly string DefaultStateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnimaRandomStudio");
    internal static readonly string StateDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.GetEnvironmentVariable("ANIMA_LAUNCHER_HOME") ?? DefaultStateDir));
    internal static string Assets = "";
    internal static string AppRoot = "";
    internal static RuntimeManifest Runtime = null!;
    internal static ModelManifest Models = null!;
    internal static SourceManifest Sources = null!;
    internal static ModelProfilesManifest ModelProfiles = null!;
    internal static string Version => Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
    private static readonly object LogLock = new();

    /// <summary>Named auto-reset event: a second instance signals it to surface the hidden window.</summary>
    private static string InstanceSuffix => StateDir.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(DefaultStateDir)), StringComparison.OrdinalIgnoreCase) ? "" : "-" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(StateDir.ToUpperInvariant())))[..12];
    internal static string ShowEventName => "Local\\AnimaRandomStudioShow" + InstanceSuffix;
    /// <summary>Owned by the first instance; created before the window so early second instances can wait on it.</summary>
    internal static EventWaitHandle? ShowSignal;
    internal static UpdateTransaction? UpdateContext { get; private set; }
    internal static bool IsUpdateRecovery { get; private set; }
    internal static event Action? UpdateExitRequested;
    private static EventWaitHandle? updateExitSignal;

    internal static async Task CompleteUpdateAsync(bool success, string? error = null)
    {
        if (UpdateContext is not { } context) return;
        if (IsUpdateRecovery) { if (!success) Log("旧工作台恢复失败：" + error); else SyncInstalledVersion(Version); return; }
        var store = new LauncherUpdateStore(StateDir);
        JsonFile.Write(store.ReceiptPath(context.Id), new UpdateReady(context.Token, success, error, AppRoot, Environment.ProcessId));
        if (!success) return;
        for (var i = 0; i < 200; i++)
        {
            var active = store.Load();
            if (active.PendingTransactionId is null && active.Active?.Sha256 == context.Candidate.Sha256
                && active.Active.Version == context.Candidate.Version) { SyncInstalledVersion(Version); return; }
            var current = store.ReadTransaction(context.Id, context.Token);
            if (current.Phase == "committed") { SyncInstalledVersion(Version); return; }
            if (current.Phase is "rolling-back" or "failed" or "recovery-required") throw new IOException(current.Error ?? "更新验证失败，正在恢复旧版本。");
            await Task.Delay(100);
        }
        throw new IOException("更新提交未完成，已保留恢复记录。");
    }

    private static string Argument(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : "";
    }
    private static void Report(string[] args, object value)
    {
        var report = Argument(args, "--report");
        if (report.Length > 0) JsonFile.Write(Path.GetFullPath(report), value);
        else { AttachConsole(0xFFFFFFFF); Console.WriteLine(JsonSerializer.Serialize(value, JsonFile.Options)); }
    }
    private static LauncherPackageInfo PackageInfo()
    {
        BundledTools.VerifyEmbedded();
        var directory = AppContext.BaseDirectory;
        var build = JsonFile.Read<JsonElement>(Path.Combine(directory, "build-info.json"));
        LauncherUpdates.ValidateBuildInfo(build, Version);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<LauncherPackageFile>();
        foreach (var line in File.ReadLines(Path.Combine(directory, "FILES.sha256")))
        {
            if (line.Length == 0) continue;
            var match = System.Text.RegularExpressions.Regex.Match(line, "^([a-fA-F0-9]{64})  (.+)$");
            if (!match.Success || !seen.Add(match.Groups[2].Value) || match.Groups[2].Value.Equals("FILES.sha256", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("启动器内嵌文件清单格式无效。");
            var relative = match.Groups[2].Value;
            var path = Safety.Under(directory, relative);
            LauncherUpdateStore.RequireNoLinks(Path.GetDirectoryName(path)!);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("启动器资源不能是链接。");
            using var input = File.OpenRead(path);
            var hash = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
            if (!hash.Equals(match.Groups[1].Value, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("启动器资源校验失败：" + relative);
            files.Add(new(relative, input.Length, hash));
        }
        foreach (var required in new[] { "AnimaRandomStudio.dll", "Anima.Launcher.Core.dll", "AnimaRandomStudio.pri", "Microsoft.UI.Xaml.dll", "System.Private.CoreLib.dll", "build-info.json", "LICENSE", "THIRD-PARTY-NOTICES.txt",
            "Microsoft.AspNetCore.Server.Kestrel.Core.dll", "Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.dll",
            "DOWNLOAD-TOOLS-NOTICES.txt", "DOWNLOAD-TOOLS-SOURCES.zip" })
            if (!seen.Contains(required)) throw new InvalidDataException("启动器包缺少 " + required);
        return new(1, Version, "single-exe-v1", Environment.ProcessPath!, directory, build, files.ToArray());
    }
    private static bool RunningLauncher()
    {
        if (!Mutex.TryOpenExisting("Local\\AnimaRandomStudioLauncher" + InstanceSuffix, out var mutex)) return false;
        using (mutex)
        {
            bool acquired;
            try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
            if (acquired) mutex.ReleaseMutex();
            return !acquired;
        }
    }
    private static void ListenForUpdateExit(UpdateTransaction transaction)
    {
        updateExitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, LauncherUpdateCoordinator.ExitEventName(transaction));
        new Thread(() => { try { updateExitSignal.WaitOne(); UpdateExitRequested?.Invoke(); } catch (ObjectDisposedException) { } })
            { IsBackground = true, Name = "UpdateExit" }.Start();
    }
    private static void SyncInstalledVersion(string version)
    {
        if (!LauncherUpdateStore.SamePath(StateDir, DefaultStateDir)) return;
        try
        {
            const string uninstall = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AnimaRandomStudio_is1";
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(uninstall, writable: true);
            if (key is null) return;
            var location = key.GetValue("InstallLocation") as string;
            var previous = UpdateContext?.Previous.Executable ?? Environment.ProcessPath!;
            var knownEntries = new LauncherUpdateStore(StateDir).Load().EntryPaths;
            if (!ProductBranding.OwnsInstallation(key.GetValue("DisplayName") as string, location,
                knownEntries.Concat([previous, Environment.ProcessPath!]))) return;
            key.SetValue("DisplayVersion", version);
            key.SetValue("DisplayName", ProductBranding.DisplayName);
        }
        catch (Exception ex) { Log("安装信息版本同步失败：" + ex.Message); }
    }

    private static int? RouteInstalledVersion(LauncherUpdates updates, string[] args)
    {
        var configPath = Path.Combine(StateDir, "launcher.json");
        LauncherConfig? config = null;
        if (File.Exists(configPath))
        {
            try { config = JsonFile.Read<LauncherConfig>(configPath); config.Validate(validateArguments: false); }
            catch { return null; } // Existing configuration recovery UI owns malformed config.
            var versions = Path.GetFullPath(Path.Combine(StateDir, "versions")) + Path.DirectorySeparatorChar;
            if (config.AppRoot.Length > 0 && !Path.GetFullPath(config.AppRoot).StartsWith(versions, StringComparison.OrdinalIgnoreCase)) return null;
        }
        using var gate = new Mutex(false, updates.Store.MutexName);
        bool acquired;
        try { acquired = gate.WaitOne(TimeSpan.FromMinutes(4)); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new IOException("更新正在进行，请稍后重新打开启动器。");
        try
        {
            var state = updates.Store.Load();
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, "build-info.json"))
                && !state.EntryPaths.Any(path => LauncherUpdateStore.SamePath(path, Environment.ProcessPath!)))
            {
                state.EntryPaths.Add(Environment.ProcessPath!);
                if (state.EntryPaths.Count > 32) state.EntryPaths.RemoveAt(0);
                updates.Store.Save(state);
            }
            if (state.PendingTransactionId is { } pending)
            {
                var transaction = updates.Store.ReadTransaction(pending);
                if (LauncherUpdateStore.IsRunning(transaction.Parent)) { WakeRunningInstance(); return 0; }
                var recovered = new LauncherUpdateCoordinator(updates, Log).Run(transaction.Id, transaction.Token, recoverOnly: true);
                var after = updates.Store.ReadTransaction(transaction.Id, transaction.Token);
                if (after.Phase == "recovery-required")
                {
                    var message = "上次更新尚不能安全恢复，当前生图服务保持运行。请等待生成和排队任务完成后，重新打开启动器重试。\n\n" + after.Error;
                    if (args.Contains("--diagnose")) Console.Error.WriteLine(message); else NativeMessage(message, "更新恢复待重试", true);
                }
                return recovered;
            }
            if (state.Active is null)
            {
                // Build/debug output has no release metadata. Do not register it as a distributable.
                if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "build-info.json"))) return null;
                PackageInfo();
                state.Active = updates.ImportAsync(Environment.ProcessPath!, Version, default).GetAwaiter().GetResult();
                updates.Store.Save(state);
                return null;
            }
            updates.Store.ValidateInstalled(state.Active, managed: false);
            var currentVersion = LauncherUpdates.ParseVersion(Version);
            var activeVersion = LauncherUpdates.ParseVersion(state.Active.Version);
            if (currentVersion == activeVersion && !LauncherUpdateStore.SamePath(state.Active.Executable, Environment.ProcessPath!)
                && !Downloads.Verify(state.Active.Executable, state.Active.Size, state.Active.Sha256, default).GetAwaiter().GetResult())
            {
                if (RunningLauncher()) { WakeRunningInstance(); return 0; }
                PackageInfo();
                var source = updates.IdentifyAsync(Environment.ProcessPath!, Version, default).GetAwaiter().GetResult();
                if (source.Sha256 != state.Active.Sha256 || source.Size != state.Active.Size) throw new IOException("当前入口与待修复的同版本文件不一致，请使用原始发行文件修复。");
                state.Active = updates.ImportAsync(Environment.ProcessPath!, Version, default, allowRepair: true).GetAwaiter().GetResult();
                updates.Store.Save(state);
            }
            if (currentVersion > activeVersion && !args.Contains("--diagnose"))
            {
                var local = updates.IdentifyAsync(Environment.ProcessPath!, Version, default).GetAwaiter().GetResult();
                if (!updates.IsFailed(local))
                {
                    if (RunningLauncher()) throw new IOException("请先从托盘退出正在运行的启动器，再打开此新版；现有服务可以保留。");
                    PackageInfo();
                    local = updates.ImportAsync(Environment.ProcessPath!, Version, default).GetAwaiter().GetResult();
                    config ??= new LauncherConfig { DataDir = Path.Combine(StateDir, "data") };
                    var running = ServiceManager.PortOpen(config.WebPort).GetAwaiter().GetResult();
                    if (running)
                    {
                        var health = ServiceManager.Json(config.WebUrl + "/api/launcher-health", default).GetAwaiter().GetResult();
                        if (!ServiceManager.IsOurWebUi(health, config)) throw new IOException("工作台端口由其他环境占用，不能切换版本。");
                    }
                    // Prepare commits under its own synchronous mutex after async hashing.
                    // Do not hold this thread-affine gate while waiting for that continuation.
                    gate.ReleaseMutex(); acquired = false;
                    var transaction = updates.PrepareAsync(local, config, running, default).GetAwaiter().GetResult();
                    updates.StartCoordinator(transaction);
                    return 0;
                }
            }
            if (LauncherUpdateStore.SamePath(state.Active.Executable, Environment.ProcessPath!)) return null;
            if (!Downloads.Verify(state.Active.Executable, state.Active.Size, state.Active.Sha256, default).GetAwaiter().GetResult())
                throw new IOException("活动启动器文件缺失或校验失败，原版本文件已保留，请重新运行已安装的有效启动器。");
            var info = new ProcessStartInfo(state.Active.Executable)
            {
                UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(state.Active.Executable)!,
                CreateNoWindow = args.Contains("--diagnose"), RedirectStandardOutput = args.Contains("--diagnose"), RedirectStandardError = args.Contains("--diagnose")
            };
            foreach (var arg in args) info.ArgumentList.Add(arg);
            info.Environment["ANIMA_LAUNCHER_HOME"] = StateDir;
            // Do not keep the update gate while the forwarded launcher enters it.
            gate.ReleaseMutex(); acquired = false;
            using var process = Process.Start(info) ?? throw new IOException("无法打开活动启动器。");
            if (!args.Contains("--diagnose")) return 0;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            process.WaitForExit(); Console.Write(output.GetAwaiter().GetResult()); Console.Error.Write(error.GetAwaiter().GetResult());
            return process.ExitCode;
        }
        finally { if (acquired) gate.ReleaseMutex(); }
    }

    internal static void Log(string text)
    {
        // No request URLs with signed queries or credential values are logged.
        lock (LogLock)
        {
            var dir = Path.Combine(StateDir, "logs");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "launcher-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
            File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss ") + text + Environment.NewLine);
        }
    }

    internal static void Open(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(uint processId);

    private static void NativeMessage(string text, string caption, bool error)
    {
        // MB_ICONINFORMATION=0x40, MB_ICONERROR=0x10. A native box works before any window exists.
        MessageBoxW(IntPtr.Zero, text, caption, error ? 0x10u : 0x40u);
    }

    /// <summary>Acquire the single-instance mutex; an owner that died without release still yields first=true.</summary>
    private static Mutex AcquireInstanceMutex(out bool first)
    {
        try
        {
            return new Mutex(true, "Local\\AnimaRandomStudioLauncher" + InstanceSuffix, out first);
        }
        catch (AbandonedMutexException)
        {
            // A previous instance died holding the mutex; take ownership through WaitOne.
            var recovered = new Mutex(false, "Local\\AnimaRandomStudioLauncher" + InstanceSuffix);
            recovered.WaitOne();
            first = true;
            return recovered;
        }
    }

    /// <summary>Ask the running instance to surface its window (it may sit in the tray); fall back to a message.</summary>
    private static void WakeRunningInstance()
    {
        // Retry briefly: the first instance may hold the mutex but not have created the signal yet.
        for (var i = 0; i < 20; i++)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(ShowEventName, out var signal))
                {
                    using (signal) signal.Set();
                    return;
                }
            }
            catch { }
            Thread.Sleep(250);
        }
        NativeMessage("启动器已运行，请从系统托盘打开。", ProductBranding.DisplayName, false);
    }

    [STAThread]
    private static int Main(string[] args)
    {
        var raw = args.Contains("--package-info") || args.Contains("--installation-status");
        if (raw)
        {
            try
            {
                if (args.Contains("--package-info")) Report(args, PackageInfo());
                else
                {
                    var updates = new LauncherUpdateStore(StateDir);
                    var updating = updates.InProgress(); var running = RunningLauncher();
                    var active = updates.Load().Active;
                    if (active is not null) updates.ValidateInstalled(active, managed: false);
                    var effectiveVersion = active is not null && LauncherUpdates.ParseVersion(active.Version) > LauncherUpdates.ParseVersion(Version) ? active.Version : Version;
                    Report(args, new { protocolVersion = 1, version = Version, effectiveVersion, executablePath = Environment.ProcessPath, stateDirectory = StateDir,
                        updateInProgress = updating, running, canInstall = !updating && !running, reason = updating ? "软件更新尚未结束。" : running ? "请先退出启动器。" : "" });
                }
                return 0;
            }
            catch (Exception ex) { AttachConsole(0xFFFFFFFF); Console.Error.WriteLine(ex.Message); return 1; }
        }
        if (args.Contains("--version"))
        {
            AttachConsole(0xFFFFFFFF);
            Console.WriteLine(Version);
            return 0;
        }
        var diagnose = args.Contains("--diagnose");
        if (diagnose) AttachConsole(0xFFFFFFFF); // Attach to the parent console; EXE stays a GUI app.
        try
        {
            using var updates = new LauncherUpdates(StateDir, Environment.ProcessPath!, Version, Log);
            if (args.Contains("--apply-update"))
                return new LauncherUpdateCoordinator(updates, Log).Run(Argument(args, "--apply-update"), Argument(args, "--update-token"));
            var candidate = args.Contains("--update-candidate");
            IsUpdateRecovery = args.Contains("--recover-update");
            if (candidate || IsUpdateRecovery)
            {
                UpdateContext = updates.Store.ReadTransaction(Argument(args, candidate ? "--update-candidate" : "--recover-update"), Argument(args, "--update-token"));
                var expected = candidate ? UpdateContext.Candidate : UpdateContext.Previous;
                if (!LauncherUpdateStore.SamePath(expected.Executable, Environment.ProcessPath!) || expected.Version != Version
                    || candidate && !LauncherUpdateStore.IsRunning(UpdateContext.Coordinator)) throw new InvalidDataException("更新启动上下文不匹配。");
                var state = updates.Store.Load();
                if (candidate && state.PendingTransactionId != UpdateContext.Id
                    || IsUpdateRecovery && (UpdateContext.Phase != "failed" || state.PendingTransactionId is not null || state.Active != UpdateContext.Previous))
                    throw new InvalidDataException("此更新启动请求已失效。");
                ListenForUpdateExit(UpdateContext);
            }
            else if (!Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                var result = RouteInstalledVersion(updates, args);
                if (result.HasValue) return result.Value;
            }
            var first = true;
            using var earlyInstance = !diagnose && UpdateContext is null ? AcquireInstanceMutex(out first) : null;
            if (earlyInstance is not null)
            {
                if (!first) { WakeRunningInstance(); return 0; }
                ShowSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            }
            Directory.CreateDirectory(StateDir);
            Log("启动器初始化");
            // Diagnostics and update candidates still serialize resource preparation.
            // Ordinary duplicate launches have already signaled the existing window.
            using var extract = new Mutex(false, "Local\\AnimaRandomStudioExtract" + InstanceSuffix);
            var acquired = false;
            try { acquired = extract.WaitOne(TimeSpan.FromSeconds(60)); }
            catch (AbandonedMutexException) { acquired = true; }
            try
            {
                if (!acquired) throw new IOException("等待资源提取锁超时，请关闭其他实例后重试。");
                using var cacheUse = LauncherCache.AcquireUseAsync(StateDir).GetAwaiter().GetResult();
                ExtractResources();
            }
            finally { if (acquired) extract.ReleaseMutex(); }
            Log("应用资源就绪");
            if (diagnose) return Diagnose(args).GetAwaiter().GetResult();
            using var mutex = earlyInstance is null ? AcquireInstanceMutex(out first) : null;
            if (!first)
            {
                WakeRunningInstance();
                return 0;
            }
            ShowSignal ??= new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            // Retry a prior registry write failure on ordinary startup. Candidate
            // versions synchronize only after the update transaction commits.
            if (UpdateContext is null) SyncInstalledVersion(Version);
            var forceShow = args.Contains("--show") || UpdateContext is not null;
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(_ =>
            {
                // The generated Main normally installs this; we must too so awaits return to the UI thread.
                var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
                System.Threading.SynchronizationContext.SetSynchronizationContext(context);
                var app = new App();
                app.InitializeMainWindow(forceShow);
            });
            return 0;
        }
        catch (Exception ex)
        {
            Log(ex.ToString());
            if (UpdateContext is not null)
            {
                try { CompleteUpdateAsync(false, ex.Message).GetAwaiter().GetResult(); } catch { }
                return 1;
            }
            if (diagnose) Console.Error.WriteLine(ex.Message);
            else NativeMessage(ex.Message, "启动器错误", true);
            return 1;
        }
    }

    private static void ExtractResources()
    {
        var assembly = Assembly.GetExecutingAssembly();
        // Manifests and payload form one immutable bundle. Two builds sharing a
        // version number must never overwrite each other's running resources.
        using var identityHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var resource in assembly.GetManifestResourceNames().Order(StringComparer.Ordinal))
        {
            using var source = assembly.GetManifestResourceStream(resource)!;
            identityHash.AppendData(System.Text.Encoding.UTF8.GetBytes(resource));
            identityHash.AppendData(SHA256.HashData(source));
        }
        var bundleId = Convert.ToHexString(identityHash.GetHashAndReset())[..16].ToLowerInvariant();
        Assets = Path.Combine(StateDir, "versions", Version + "-" + bundleId);
        var assetsExisted = Directory.Exists(Assets);
        var cache = new LauncherCache(StateDir, Log);
        var repairSnapshot = false;
        try { repairSnapshot = cache.HasPendingSnapshotRepairAsync(Assets).GetAwaiter().GetResult(); }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        { Log("无法读取缓存维护索引，本次保留资源并跳过缓存维护登记：" + error.Message); }
        Directory.CreateDirectory(Assets);
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            // The SDK embeds resources as "<rootNamespace>.<file>" (folder prefixes are dropped),
            // so match on the file-name suffix rather than a folder prefix.
            var name = resource.EndsWith("payload.zip") ? "payload.zip"
                : resource.EndsWith("probe_comfy.py") ? "probe_comfy.py"
                : resource.EndsWith("launcher-manifest.json") ? "launcher-manifest.json"
                : resource.EndsWith("model-manifest.json") ? "model-manifest.json"
                : resource.EndsWith("tagger-models.json") ? "tagger-models.json"
                : resource.EndsWith("source-profiles.json") ? "source-profiles.json"
                : resource.EndsWith("model-profiles.json") ? "model-profiles.json"
                : null;
            if (name is null) continue;
            var target = Path.Combine(Assets, name);
            if (File.Exists(target)) continue;
            using (var stream = assembly.GetManifestResourceStream(resource)!)
            using (var file = File.Create(target + ".tmp")) stream.CopyTo(file);
            File.Move(target + ".tmp", target, true);
        }
        Runtime = JsonFile.Read<RuntimeManifest>(Path.Combine(Assets, "launcher-manifest.json"));
        Models = JsonFile.Read<ModelManifest>(Path.Combine(Assets, "model-manifest.json"));
        Models = Models with { Models = Models.Models.Concat(JsonFile.Read<ModelManifest>(Path.Combine(Assets, "tagger-models.json")).Models).ToArray() };
        Sources = JsonFile.Read<SourceManifest>(Path.Combine(Assets, "source-profiles.json"));
        ModelProfiles = JsonFile.Read<ModelProfilesManifest>(Path.Combine(Assets, "model-profiles.json"));
        Safety.Validate(Runtime, Models);
        var zip = Path.Combine(Assets, "payload.zip");
        if (File.Exists(zip))
        {
            using var input = File.OpenRead(zip);
            var identity = Convert.ToHexString(SHA256.HashData(input))[..16].ToLowerInvariant();
            var bundled = Path.Combine(Assets, "app-" + identity);
            if (!Directory.Exists(bundled))
            {
                var staging = bundled + ".extracting";
                if (Directory.Exists(staging))
                {
                    if (!Path.GetDirectoryName(Path.GetFullPath(staging))!.Equals(Path.GetFullPath(Assets), StringComparison.OrdinalIgnoreCase)) throw new IOException("临时资源目录超出版本目录。");
                    Directory.Delete(staging, true);
                }
                Installer.ExtractZip(zip, staging);
                Directory.Move(staging, bundled);
            }
            else if (repairSnapshot) RepairMissingWorkbench(zip, bundled);
            AppRoot = bundled;
        }
        else
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "run.py"))) directory = directory.Parent;
            AppRoot = directory?.FullName ?? throw new FileNotFoundException("发布文件缺少工作台资源，请使用 Build-Launcher.ps1 构建。");
        }
        if (Directory.Exists(AppRoot) && Path.GetFullPath(AppRoot).StartsWith(Path.GetFullPath(Assets) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            // A known interrupted deletion must be completely repaired before this version can run.
            // Existing modified files are never overwritten; verification rejects an ambiguous tree.
            if (repairSnapshot) cache.CompleteSnapshotRepairAsync(Assets).GetAwaiter().GetResult();
            try
            {
                var state = new LauncherUpdateStore(StateDir).Load();
                var packageHash = new[] { state.Active, state.Previous, state.Downloaded }
                    .FirstOrDefault(item => item is not null && LauncherUpdateStore.SamePath(item.Executable, Environment.ProcessPath!))?.Sha256;
                new LauncherCache(StateDir, Log).RegisterSnapshotAsync(Version, Assets, AppRoot,
                    newlyCreated: !assetsExisted, packageSha256: packageHash).GetAwaiter().GetResult();
            }
            catch (Exception error) { Log("缓存登记已跳过：" + error.Message); }
        }

    }

    private static void RepairMissingWorkbench(string archive, string destination)
    {
        var staging = Path.Combine(StateDir, ".cache-repair-" + Guid.NewGuid().ToString("N"));
        try
        {
            Installer.ExtractZip(archive, staging);
            foreach (var source in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
            {
                var target = Safety.Under(destination, Path.GetRelativePath(staging, source));
                LauncherUpdateStore.RequireNoLinks(target);
                if (File.Exists(target)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(source, target); // Never overwrite an existing user-modified file.
            }
        }
        finally
        {
            // Only this unique, freshly extracted staging tree is owned by this invocation.
            if (!LauncherUpdateStore.SamePath(Path.GetDirectoryName(staging)!, StateDir)) throw new IOException("资源修复目录超出状态目录。");
            if (Directory.Exists(staging))
            {
                LauncherUpdateStore.RequireNoLinks(staging);
                foreach (var entry in Directory.EnumerateFileSystemEntries(staging, "*", SearchOption.AllDirectories))
                    if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new IOException("资源修复目录中出现链接，已保留。");
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    private static async Task<int> Diagnose(string[] args)
    {
        string Arg(string flag, string fallback = "") { var i = Array.IndexOf(args, flag); return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : fallback; }
        var target = Arg("--diagnose");
        if (target.Length == 0)
        {
            Console.Error.WriteLine("用法：AnimaRandomStudio.exe --diagnose <ComfyUI 目录> [--python <python.exe>] [--hashes] [--report <报告路径>]");
            return 2;
        }
        var root = Inventory.NormalizeRoot(target);
        var python = Inventory.FindPython(root, Arg("--python"));
        var warnings = new List<string>();
        var configuration = new LauncherConfig();
        try { if (File.Exists(Path.Combine(StateDir, "launcher.json"))) configuration = JsonFile.Read<LauncherConfig>(Path.Combine(StateDir, "launcher.json")); }
        catch (Exception ex) { warnings.Add("启动器配置无法读取：" + ex.Message); }
        var portText = Arg("--comfy-port", configuration.ComfyPort.ToString());
        if (!int.TryParse(portText, out var port) || port is < 1024 or > 65535) throw new ArgumentException("无效的 ComfyUI 端口。");
        // Diagnose the explicitly selected environment without saving over the user's configuration.
        configuration.ComfyRoot = root;
        configuration.Python = python;
        configuration.ComfyPort = port;
        var probe = await TaggerRuntime.Probe(configuration, Path.Combine(Assets, "probe_comfy.py"), CancellationToken.None);
        var models = await Inventory.Scan(probe, Models, args.Contains("--hashes"), CancellationToken.None);
        var nodes = await Inventory.Nodes(root, Runtime, Commands.Find("git.exe"), CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        JsonElement? comfy = null;
        try { comfy = await ServiceManager.Json($"http://127.0.0.1:{port}/system_stats", timeout.Token); }
        catch (Exception ex) { warnings.Add("ComfyUI 接口检查失败：" + ex.Message); }
        var report = new { launcherVersion = Version, manifestVersion = Runtime.Version, root, python, models, nodes, paths = probe.Paths, loras = Inventory.Loras(probe), comfyPort = port, comfy, warnings };
        JsonFile.Write(Arg("--report", Path.Combine(StateDir, "diagnosis.json")), report);
        return 0;
    }
}
