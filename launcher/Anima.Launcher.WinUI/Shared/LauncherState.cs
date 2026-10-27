using Anima.Launcher.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace Anima.Launcher;

/// <summary>
/// Owns launcher state and orchestration: config, services, downloads, model scan,
/// install, start/stop and test generation. UI pages bind to this and raise commands;
/// dialogs use the live <see cref="Microsoft.UI.Xaml.XamlRoot"/>.
/// </summary>
internal sealed partial class LauncherState : IDisposable
{
    private readonly string configFile = Path.Combine(Program.StateDir, "launcher.json");
    private readonly Func<XamlRoot?> root;
    private readonly Action showWindow;

    public LauncherConfig Config { get; private set; }
    public ServiceManager Services { get; }
    public Downloads Downloads { get; }
    public ObservableCollection<ModelRow> Rows { get; } = [];
    public ObservableCollection<ModelGroup> Groups { get; } = [];

    private CancellationTokenSource? task;
    private List<ModelStatus> downloadQueue = [];
    private bool cancelDownloads;
    private bool upgradeOffered;
    private string? configWriteError;
    private Func<CancellationToken, Task>? retryAction;
    private bool retryable;
    private bool retryIsSetup;
    private LauncherConfig? retryConfig;
    private bool retryFresh;
    private string? startingRole;
    private long progressBytes;
    private string progressName = "";
    private DateTime progressAt = DateTime.UtcNow;
    public bool CanRetry => !Busy && !retryIsSetup && retryable && retryAction is not null && retryAction.Method.Name != nameof(PreviewInstallAsync);
    public string ProgressText { get; private set; } = "";
    public event Action? TaskStateChanged;
    public event Action<string>? TaskProgressChanged;
    public string InstallSummaryText { get; private set; } = "选择安装方式与目录，然后检查安装内容。";
    public event Action<string>? InstallSummaryChanged;

    /// <summary>Fired on the UI thread when a status message changes.</summary>
    public event Action<string>? StatusChanged;
    /// <summary>May run on a process-output thread; subscribers enqueue for batched UI updates.</summary>
    public event Action<string>? LogAppended;
    /// <summary>Fired on the UI thread when a task begins/ends (busy state).</summary>
    public event Action<bool>? BusyChanged;
    /// <summary>Fired on the UI thread after config is loaded/changed so pages can re-fill fields.</summary>
    public event Action? FieldsReloaded;
    /// <summary>Fired on the UI thread after a scan; pages rebuild the model list.</summary>
    public event Action<List<ModelStatus>>? Scanned;
    /// <summary>Fired on the UI thread to switch pages (0=环境,1=模型,2=日志,3=设置).</summary>
    public event Action<int>? NavigateRequested;
    /// <summary>Raised when a background task fails; UI shows a dialog.</summary>
    public event Action<Exception>? TaskFailed;

    public bool Busy => task is not null;
    public XamlRoot? Root => root();

    public LauncherState(Func<XamlRoot?> getRoot, Action showWindowAction)
    {
        root = getRoot;
        showWindow = showWindowAction;
        Config = File.Exists(configFile) ? LoadConfig() : new LauncherConfig { AppRoot = Program.AppRoot, DataDir = Path.Combine(Program.StateDir, "data") };
        Downloads = new(Credentials.Read, engineOptions: new DownloadEngineOptions
        {
            Aria2PathResolver = async ct => await BundledTools.EnsureAsync("aria2", Path.Combine(Program.StateDir, "download-runtime"), ct),
            NetworkPolicyResolver = () => NetworkPolicySnapshot.Capture(Config),
        });
        InitializeDownloadTasks();
        FreshInstallRequested = Config.InstallPending;
        Program.Log("配置已读取");
        Services = new ServiceManager(Program.StateDir, AppendLog);
        Services.RecoverOwned();
        InitializeSetup();
    }

    private LauncherConfig LoadConfig()
    {
        // A changed ComfyUI version or an invalid editable argument must not erase the
        // selected environment. Surface it in settings/startup while preserving the configuration.
        try { var loaded = JsonFile.Read<LauncherConfig>(configFile); loaded.Validate(validateArguments: false); return loaded; }
        catch (Exception ex)
        {
            try
            {
                var backup = configFile + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmssfff");
                File.Copy(configFile, backup, false);
                Program.Log("配置无法读取，已备份至 " + backup + "：" + ex.Message);
            }
            catch (Exception backupError)
            {
                configWriteError = "配置损坏且无法备份，暂不覆盖原文件：" + configFile + "。" + backupError.Message;
                Program.Log(configWriteError);
            }
            return new() { AppRoot = Program.AppRoot, DataDir = Path.Combine(Program.StateDir, "data") };
        }
    }

    /// <summary>In-memory copy of the session log for the log page.</summary>
    public List<string> LogLines { get; } = [];

    public void SetStatus(string text)
    {
        SetupStatusText = text;
        StatusChanged?.Invoke(text);
        if (SetupActive) NotifySetup();
    }
    public void AppendLog(string text)
    {
        Program.Log(text);
        var line = DateTime.Now.ToString("HH:mm:ss ") + (text.Length > 20000 ? text[..20000] + " …" : text);
        // Called from worker threads (process output callbacks); keep the list and UI event safe.
        lock (LogLines)
        {
            LogLines.Add(line);
            if (LogLines.Count > 2000) LogLines.RemoveRange(0, LogLines.Count - 2000);
        }
        LogAppended?.Invoke(line);
    }
    public void Save()
    {
        if (configWriteError is not null) throw new IOException(configWriteError);
        JsonFile.Write(configFile, WithSavedDownloadSettings(Config));
    }
    public void Reloaded() { Config = WithSavedDownloadSettings(Config); FieldsReloaded?.Invoke(); }
    public void GoTo(int page) => NavigateRequested?.Invoke(page);
    public void Show() => showWindow();

    // --- Field synchronization (pages call on save / before ops) ---

    /// <summary>Latest values captured from the environment page; null keeps current config.</summary>
    public EnvironmentFields? PendingEnvironmentFields { get; set; }
    /// <summary>Latest values captured from the settings page; null keeps current config.</summary>
    public SettingsFields? PendingSettingsFields { get; set; }

    public void SyncFromUi(EnvironmentFields e, SettingsFields s)
    {
        var next = Config.Clone();
        var chosen = e.ComfyRoot.Trim().Trim('"');
        var newRoot = e.Fresh && chosen.Length > 0 ? Path.Combine(chosen, "ComfyUI") : chosen;
        if (!e.Fresh && chosen.Length > 0 && (File.Exists(Path.Combine(chosen, "main.py")) || File.Exists(Path.Combine(chosen, "ComfyUI", "main.py"))))
            newRoot = Inventory.NormalizeRoot(chosen);
        var environmentChanged = !SamePath(next.ComfyRoot, newRoot);
        if (!e.Fresh && !environmentChanged && Config.InstallPending)
            throw new InvalidOperationException("此目录的安装尚未完成。请选择“新建环境”继续安装，或导入另一个完整环境。");
        // An unchanged interpreter in a different environment was auto-discovered,
        // not an explicit request to reuse the old environment's interpreter.
        var python = e.Python.Trim();
        if (environmentChanged && !e.PythonExplicit) python = "";
        if (environmentChanged)
        {
            // Ownership and setup belong to an environment, not the launcher session.
            // Carrying these across an import would override its cache configuration.
            next.ManagedComfy = newRoot.Length > 0 && File.Exists(Path.Combine(newRoot, ".anima-revision.json"));
            next.SetupComplete = false;
        }
        next.ComfyRoot = newRoot;
        next.Python = python;
        next.PythonExplicit = e.PythonExplicit;
        next.InstallPending = e.Fresh && chosen.Length > 0;
        if (e.Fresh && chosen.Length > 0)
        {
            next.InstallRoot = Path.GetFullPath(chosen);
            next.RuntimeRoot = Path.Combine(next.InstallRoot, "runtime");
            next.SelectedProfile = "anima";
        }
        else if (environmentChanged && newRoot.Length > 0)
        {
            next.InstallRoot = "";
            next.RuntimeRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(newRoot)) + ".anima-runtime";
        }
        next.SourceId = e.SourceId;
        next.GitMirror = e.GitMirror.Trim();
        next.ModelMirror = e.HfMirror.Trim();
        next.PipIndex = e.PipIndex.Trim();
        next.CustomRepository = e.CustomRepository.Trim();
        next.ComfyPort = s.ComfyPort;
        next.WebPort = s.WebPort;
        next.Theme = s.Theme;
        next.StartOnLaunch = s.StartOnLaunch;
        next.ComfyArguments = ComfyLaunchArguments.Parse(s.ComfyArgumentsText);
        if (!ComfyLaunchArguments.Equivalent(next.ComfyArguments, Config.ComfyArguments)) next.LaunchArgumentsConfigured = true;
        if (next.AppRoot.Length == 0) next.AppRoot = Program.AppRoot;
        if (next.DataDir.Length == 0) next.DataDir = Path.Combine(Program.StateDir, "data");
        next.Validate();
        ComfyLaunchArguments.ValidateForRoot(next.ComfyArguments, next.ComfyRoot);
        Config = next;
        savedDownloadSettings = next.Clone();
        FreshInstallRequested = e.Fresh;
    }

    /// <summary>Apply any pending field edits into <see cref="Config"/>. Unset pages keep prior values.</summary>
    public void ApplyPendingFields()
    {
        var e = PendingEnvironmentFields ?? new EnvironmentFields(FreshInstallRequested && Config.InstallRoot.Length > 0 ? Config.InstallRoot : Config.ComfyRoot, Config.Python, Config.SourceId, Config.GitMirror, Config.ModelMirror, Config.PipIndex, Config.CustomRepository, FreshInstallRequested, Config.PythonExplicit);
        var s = PendingSettingsFields ?? SettingsFields.From(Config);
        SyncFromUi(e, s);
        PendingEnvironmentFields = null;
        PendingSettingsFields = null;
    }

    // --- Task gating ---

    public async Task Run(Func<CancellationToken, Task> action, bool applyDrafts = true, LauncherConfig? resumeConfig = null, bool resumeFresh = false, bool setupOperation = false)
    {
        if (SetupActive && !setupOperation)
        {
            showWindow();
            SetStatus("请先完成安装向导，或从向导返回概览后执行此操作。");
            return;
        }
        if (task is not null)
        {
            showWindow();
            SetStatus("已有任务进行中，请先等待完成或暂停后再操作。");
            return;
        }
        task = new CancellationTokenSource();
        retryIsSetup = setupOperation;
        retryAction = action;
        retryConfig = null;
        retryable = false;
        cancelDownloads = false;
        ResetTransferProgress();
        ClearBatchProgress();
        BusyChanged?.Invoke(true);
        TaskStateChanged?.Invoke();
        try
        {
            if (resumeConfig is not null)
            {
                Config = WithSavedDownloadSettings(resumeConfig.Clone());
                FreshInstallRequested = resumeFresh;
            }
            else if (applyDrafts) ApplyPendingFields();
            // Tasks own a snapshot. Pages only edit drafts until the next task.
            Config = Config.Clone();
            retryConfig = Config.Clone();
            retryFresh = FreshInstallRequested;
            // The gate is held before awaiting a pause, so a second UI action cannot enter.
            foreach (var owner in DownloadTasks.Owners.Where(o => o.Kind == "models" && o.Status == DownloadTaskStatus.Running
                && SamePath(o.EnvironmentRoot, Config.ComfyRoot)))
                await DownloadTasks.PauseOwnerAsync(owner.Id);
            using var environmentLease = await EnvironmentOperationLease.AcquireAsync(Config.ComfyRoot, true, task.Token);
            await action(task.Token);
        }
        catch (OperationCanceledException) when (task.IsCancellationRequested)
        {
            retryable = true;
            var message = cancelDownloads ? "任务已取消，已下载文件保留。需要释放空间时，请打开「查看全部下载」单独删除未完成文件。" : "任务已暂停，点击继续可从已完成步骤恢复。";
            SetSetupInterruption(message, true);
            SetStatus(message);
            AppendLog(message);
        }
        catch (OperationCanceledException ex)
        {
            retryable = true;
            if (ex.CancellationToken.IsCancellationRequested)
            {
                const string message = "下载任务已停止，进度已保留。可在对应页面继续，或打开「查看全部下载」处理未完成的资源。";
                SetSetupInterruption(message, true);
                SetStatus(message);
                AppendLog(message);
                return;
            }
            var error = new TimeoutException("网络或服务响应超时，请检查连接后重试。", ex);
            SetSetupInterruption(error.Message, false);
            TaskFailed?.Invoke(error);
        }
        catch (Exception ex) { retryable = true; SetSetupInterruption(ex.Message, false); TaskFailed?.Invoke(ex); }
        finally
        {
            activeInstallationPlan = null;
            task.Dispose();
            task = null;
            BusyChanged?.Invoke(false);
            TaskStateChanged?.Invoke();
            Reloaded();
            NotifySetup();
        }
    }

    // Environment tasks resume their reviewed snapshot. A software download
    // does not own settings and must retain edits made while it was paused.
    public Task ResumeTaskAsync() => !CanRetry ? Task.CompletedTask
        : retryAction == (Func<CancellationToken, Task>)DownloadSoftwareUpdateCoreAsync ? DownloadSoftwareUpdateAsync()
        : Run(retryAction!, retryConfig is null, retryConfig, retryFresh);

    public void PauseTask() => task?.Cancel();
    public void CancelTaskAndDownloads() { cancelDownloads = true; task?.Cancel(); }
    public bool CancelDownloadsRequested => cancelDownloads;

    private void ResetTransferProgress()
    {
        SetupDownloadPercent = -1;
        ProgressText = "";
        progressName = "";
        progressBytes = 0;
        progressAt = DateTime.UtcNow;
        TaskProgressChanged?.Invoke(ProgressText);
    }

    private void InstallStageChanged(string stage)
    {
        if (activeDownloadOwner is not null) DownloadTasks.SetOwnerPhase(activeDownloadOwner, "Installing", stage);
        ResetTransferProgress();
        ProgressChanged?.Invoke(-1); // Dependency stages have no byte total.
        SetStatus(stage);
    }

    public IProgress<DownloadProgress> DownloadProgress() => new Progress<DownloadProgress>(p =>
    {
        var percent = (int)Math.Clamp(p.Received * 100d / Math.Max(1, p.Total), 0, p.Phase == "Completed" ? 100 : 99);
        SetupDownloadPercent = percent;
        ProgressChanged?.Invoke(percent);
        var now = DateTime.UtcNow;
        var seconds = (now - progressAt).TotalSeconds;
        var speed = progressName == p.Name && p.Received >= progressBytes && seconds > 0
            ? (p.Received - progressBytes) / seconds / 1048576d : 0;
        ProgressText = $"{p.Received / 1048576d:N1} / {p.Total / 1048576d:N1} MiB" + (speed > 0 ? $" · {speed:N1} MiB/s" : "");
        progressAt = now; progressBytes = p.Received; progressName = p.Name;
        TaskProgressChanged?.Invoke(ProgressText);
        SetStatus($"{p.Name} · {p.Stage}");
    });
    public event Action<int>? ProgressChanged;

    // --- Environment ---

    public async Task<string> DetectSystem()
    {
        var gpu = "未检测到 NVIDIA；可导入其他显卡环境";
        var smi = Commands.Find("nvidia-smi.exe");
        if (smi is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { gpu = (await Commands.Run(smi, ["--query-gpu=name,driver_version", "--format=csv,noheader"], Program.StateDir, null, timeout.Token)).Trim(); }
            catch { gpu = "NVIDIA 检测失败，请检查驱动或使用导入方式"; }
        }
        var text = $"Windows {Environment.OSVersion.Version} · {(Environment.Is64BitOperatingSystem ? "x64" : "x86")}\n{gpu}\nGit：{(Commands.Find("git.exe") is null ? "新建时自动准备" : "已检测到")} · .NET：已内置\n";
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
            text += $"{drive.Name} 可用 {drive.AvailableFreeSpace / 1073741824d:N1} GiB  ";
        UpdateSetupSystem(gpu);
        return text;
    }

    public async Task CheckImportAsync(CancellationToken ct)
    {
        FreshInstallRequested = false;
        // Called after ReadFields; normalize and rescan.
        Config.ComfyRoot = Inventory.NormalizeRoot(Config.ComfyRoot);
        Config.Python = Inventory.FindPython(Config.ComfyRoot, Config.Python);
        Save();
        Reloaded();
        await Scan(ct);
    }

    public async Task PreviewInstallAsync(CancellationToken ct)
    {
        InstallPreviewReady = false;
        RequireRoot();
        if (!FreshInstallRequested)
        {
            Config.ComfyRoot = Inventory.NormalizeRoot(Config.ComfyRoot);
            Config.Python = Inventory.FindPython(Config.ComfyRoot, Config.Python);
        }
        var plan = await PlanInstallationAsync(FreshInstallRequested, ct);
        reviewedEnvironmentPlan = plan;
        SetStatus("正在检查系统、安装目录和磁盘空间…");
        var preflight = await InstallationPlanner.PreflightAsync(plan, ct);
        InstallPreviewReady = preflight.CanInstall;
        InstallSummaryText = plan.Describe() + "\n\n" + string.Join("\n", preflight.Issues.Select(i => i.Message));
        if (preflight.CanInstall) InstallSummaryText += "\n检查通过，可以开始安装。";
        InstallSummaryChanged?.Invoke(InstallSummaryText);
        if (!preflight.CanInstall) throw new InvalidOperationException(string.Join("\n", preflight.Issues.Where(i => i.Blocking).Select(i => i.Message)));
        SetStatus("检查完成，请确认安装内容后开始安装。");
    }

    public bool InstallPreviewReady { get; private set; }
    private InstallationPlan? reviewedEnvironmentPlan;

    private async Task<InstallationPlan> PlanInstallationAsync(bool fresh, CancellationToken ct)
    {
        var snapshot = Config.Clone();
        // Counting an existing environment can touch thousands of package files.
        // Keep it off the UI thread, then resolve ComfyUI's configured model roots.
        var plan = await Task.Run(() => InstallationPlanner.Create(snapshot, Program.Runtime, Program.StateDir, fresh, Program.Models, Program.ModelProfiles), ct);
        plan = await InstallationPlanner.ResolveStableAsync(plan, Program.Runtime, Program.StateDir, ct);
        return await InstallationPlanner.PreparePlanAsync(plan, Program.Models, Program.ModelProfiles,
            Path.Combine(Program.Assets, "probe_comfy.py"), ct);
    }

    public async Task InstallAsync(CancellationToken ct)
    {
        var plan = reviewedEnvironmentPlan ?? await PlanInstallationAsync(FreshInstallRequested, ct);
        var owner = DownloadTasks.RegisterOwner("install", "安装环境与基础模型", plan.CreateConfig(),
            payload: JsonSerializer.SerializeToElement(plan, JsonFile.Options));
        activeDownloadOwner = owner.Id;
        try { await DownloadTasks.RunOwnerAsync(owner.Id, token => InstallEnvironmentAsync(token, plan), ct); }
        finally { activeDownloadOwner = null; }
    }

    private async Task InstallEnvironmentAsync(CancellationToken ct, InstallationPlan? reviewedPlan = null, bool prepareModels = true)
    {
        RequireRoot();
        var fresh = FreshInstallRequested;
        if (fresh && Directory.Exists(Config.ComfyRoot) && File.Exists(Path.Combine(Config.ComfyRoot, "main.py")) && !File.Exists(Path.Combine(Config.ComfyRoot, ".anima-revision.json")))
            throw new IOException("此目录已有 ComfyUI，请选择导入方式。");
        if (!fresh)
        {
            Config.ComfyRoot = Inventory.NormalizeRoot(Config.ComfyRoot);
            Config.Python = Inventory.FindPython(Config.ComfyRoot, Config.Python);
        }
        var plan = reviewedPlan ?? (reviewedEnvironmentPlan is { } reviewed && reviewed.Fresh == fresh
            && reviewed.ConfigurationJson == JsonSerializer.Serialize(Config, JsonFile.Options) ? reviewed : await PlanInstallationAsync(fresh, ct));
        activeInstallationPlan = plan;
        InstallSummaryText = plan.Describe();
        InstallSummaryChanged?.Invoke(InstallSummaryText);
        Config.InstallPending = fresh;
        // Journal the new-install mode before the first side effect so a launcher
        // restart resumes CUDA/dependency stages, not an incomplete import.
        Save();
        SetStatus("正在安装环境；已完成的下载和步骤会自动复用…");
        var installer = new Installer(Program.StateDir, Program.Runtime, Downloads, AppendLog, DownloadProgress(), InstallStageChanged, UpdateSetupProgress);
        Config = await installer.Install(plan, ct);
        Config.InstallPending = fresh;
        Save();
        Reloaded();
        if (fresh && prepareModels)
        {
            UpdateSetupProgress(new("models", "准备 Anima 基础模型", SetupStageStatus.Running));
            SetStatus("环境已安装，正在准备 Anima 基础模型…");
            var probe = await TaggerRuntime.Probe(Config, Path.Combine(Program.Assets, "probe_comfy.py"), ct);
            var selected = OperationProfiles.RequiredModels(OperationModels, Config.SelectedProfile).Select(m => m.Id).ToHashSet();
            var inventory = await Inventory.Scan(probe, OperationModels, false, ct);
            var pending = inventory.Where(m => selected.Contains(m.Model.Id) && m.Missing).ToList();
            await DownloadModelsAsync(pending, ct);
            // A resumed install may already have the verified model, but may have
            // stopped before its first LoRA Manager classification was written.
            foreach (var item in inventory.Where(m => selected.Contains(m.Model.Id) && m.Model.Category == "diffusion_models"))
                await ModelMetadata.EnsureDefaultAsync(Config, OperationRuntime, OperationModels, OperationProfiles,
                    item.FoundPath ?? item.Destination, ct, AppendLog);
            UpdateSetupProgress(new("models", "基础模型已准备", SetupStageStatus.Complete));
        }
        UpdateSetupProgress(new("verify", "检查模型与节点", SetupStageStatus.Running));
        if (prepareModels) await Scan(ct);
        UpdateSetupProgress(new("verify", "最终检查完成", SetupStageStatus.Complete));
        FreshInstallRequested = false;
        Config.InstallPending = false;
        Save();
        Reloaded();
        if (!SetupActive) GoTo(0);
        SetStatus("安装完成。点击“启动工作台”开始创作；可选资源可在模型页添加。");
    }

    /// <summary>Pages set this before calling Install to choose fresh/import.</summary>
    public bool FreshInstallRequested { get; set; }

    /// <summary>Passed by MainWindow; when true the app stays visible instead of auto-hiding.</summary>
    public bool ForceShow { get; set; }
    /// <summary>Hide the main window to tray (injected by MainWindow).</summary>
    public Action? HideWindow { get; set; }
    /// <summary>Latest system-detection summary shown on the environment page.</summary>
    public string SystemInfoText { get; set; } = "正在检测系统…";

    // --- Models ---

    public async Task Scan(CancellationToken ct) => await Scan(ct, false);

    public async Task Scan(CancellationToken ct, bool hashes)
    {
        RequireRoot();
        Config.ComfyRoot = Inventory.NormalizeRoot(Config.ComfyRoot);
        Config.Python = Inventory.FindPython(Config.ComfyRoot, Config.Python);
        SetStatus("正在读取 ComfyUI 有效模型路径…");
        var probe = await TaggerRuntime.Probe(Config, Path.Combine(Program.Assets, "probe_comfy.py"), ct);
        var scan = await Task.Run(() => Inventory.Scan(probe, OperationModels, hashes, ct), ct);
        UpdateSetupMissingModels(scan);
        var nodes = await Inventory.Nodes(Config.ComfyRoot, Program.Runtime, Commands.Find("git.exe"), ct);
        var loras = await Task.Run(() => Inventory.Loras(probe), ct);
        AppendLog($"本地 LoRA：{loras.Length} 个，仅检测；请在工作台选择使用。");
        foreach (var node in nodes) AppendLog($"节点 {node.Name}: {node.State} {node.Commit} · {node.Path}");
        Scanned?.Invoke(scan);
        Save();
        Reloaded();
        SetStatus($"已扫描：模型 {scan.Count(s => !s.Missing)}/{scan.Count}；缺失节点 {nodes.Count(n => n.State == "缺失")}；本地 LoRA {loras.Length}。勾选后才下载。");
    }

    public void RebuildRows(List<ModelStatus> scan)
    {
        ModelListProjection.Refresh(Rows, Groups, scan);
        modelLibraryContext = ModelContext(Config);
        RefreshModelDownloadRows();
    }

    public void SelectBaseGroup()
    {
        var required = OperationProfiles.RequiredModels(OperationModels, Config.SelectedProfile).Select(m => m.Id).ToHashSet();
        foreach (var row in Rows) row.Selected = required.Contains(row.Status.Model.Id) && row.CanSelect;
    }

    public Func<CancellationToken, Task> CaptureSelectedModelDownload()
    {
        var selectedIds = Rows.Where(r => r.Selected).Select(r => r.Status.Model.Id).Distinct().ToArray();
        // Rows can belong to a previously scanned environment. Retain only model
        // identities before Run applies drafts, so even configuration validation
        // failures retain the original selection when the task is retried.
        return token => DownloadModelIdsAsync(selectedIds, token);
    }

    private async Task<List<ModelStatus>> ResolveModelIdsAsync(string[] modelIds, bool hashes, CancellationToken ct, LauncherConfig? configuration = null)
    {
        var config = configuration ?? Config;
        if (string.IsNullOrWhiteSpace(config.ComfyRoot)) throw new InvalidOperationException("请先选择 ComfyUI 环境。");
        config.ComfyRoot = Inventory.NormalizeRoot(config.ComfyRoot);
        config.Python = Inventory.FindPython(config.ComfyRoot, config.Python);
        var models = modelIds.Select(id => OperationModels.Models.SingleOrDefault(model => model.Id == id)
            ?? throw new InvalidDataException("模型清单中已不存在此资源，请重新扫描：" + id)).ToArray();
        var script = Path.Combine(Program.Assets, "probe_comfy.py");
        var probe = ModelProbePolicy.NeedsTaggerRuntime(models)
            ? await TaggerRuntime.Probe(config, script, ct)
            : await Inventory.Probe(config.ComfyRoot, config.Python, script, ct, config.ComfyArguments);
        return await Task.Run(() => Inventory.Scan(probe, new ModelManifest(Program.Models.SchemaVersion, models), hashes, ct), ct);
    }

    private async Task DownloadModelIdsAsync(string[] modelIds, CancellationToken ct, bool refreshInventory = true)
    {
        if (modelIds.Length == 0)
        {
            SetStatus("请勾选缺失模型。已有异常文件请先手动备份；未核实来源的模型请手动导入。");
            return;
        }
        SetStatus("正在重新确认当前环境中的模型位置与已有文件…");
        var resolved = await ResolveModelIdsAsync(modelIds, true, ct);
        var missing = resolved.Where(item => item.Missing).ToList();
        // DownloadModelsAsync rejects abnormal existing files before any download;
        // verified files in the current environment are reused without overwriting.
        if (missing.Count > 0) await DownloadModelsAsync(missing, ct);
        // A previous attempt can finish the model file before its metadata stage.
        foreach (var item in resolved.Where(item => !item.Missing && item.Model.Category == "diffusion_models"))
            await ModelMetadata.EnsureDefaultAsync(Config, OperationRuntime, OperationModels, OperationProfiles,
                item.FoundPath!, ct, AppendLog);
        if (refreshInventory) await Scan(ct);
        SetStatus(missing.Count == 0 ? "当前环境已有所选模型，校验通过，无需下载。"
            : "所选模型下载并校验完成。若 ComfyUI 已运行，请在任务空闲时重新启动以刷新清单。");
    }

    public async Task OpenModelLocationAsync(string modelId, CancellationToken ct)
    {
        var model = (await ResolveModelIdsAsync([modelId], false, ct)).Single();
        var dir = model.Model.IsPackage && model.FoundPath is not null ? model.FoundPath : Path.GetDirectoryName(model.FoundPath ?? model.Destination);
        if (string.IsNullOrWhiteSpace(dir)) throw new IOException("该模型没有可打开的目录。");
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(dir);
        Program.Open(dir);
        SetStatus(model.FoundPath is null ? "当前环境尚无此模型，已打开其下载目录。" : "已打开当前环境中的模型实际位置。");
    }

    public event Action<bool>? DownloadCancelable;

    public async Task OpenModelFolderAsync(string category, FrameworkElement? anchor = null)
    {
        try
        {
            var ct = CancellationToken.None;
            if (string.IsNullOrWhiteSpace(Config.ComfyRoot)) { GoTo(1); SetStatus("先选择并检查 ComfyUI 目录，即可打开模型文件夹。"); return; }
            var snapshot = Config.Clone();
            snapshot.ComfyRoot = Inventory.NormalizeRoot(snapshot.ComfyRoot);
            snapshot.Python = Inventory.FindPython(snapshot.ComfyRoot, snapshot.Python);
            var probe = await TaggerRuntime.Probe(snapshot, Path.Combine(Program.Assets, "probe_comfy.py"), ct);
            var paths = (probe.Paths.GetValueOrDefault(category) ?? []).Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (paths.Length == 0) throw new IOException("此环境没有配置该类型的模型目录，请先检查 ComfyUI 路径配置。");
            void OpenFolder(string path)
            {
                try { Directory.CreateDirectory(path); Program.Open(path); SetStatus("复制模型文件后点击重新扫描；运行中的 ComfyUI 可能需要重启以刷新列表。"); }
                catch (Exception ex) { TaskFailed?.Invoke(new IOException("无法打开模型目录：" + path + "\n" + ex.Message, ex)); }
            }
            if (paths.Length == 1 || anchor is null) { OpenFolder(paths[0]); return; }
            var menu = new MenuFlyout();
            for (var i = 0; i < paths.Length; i++)
            {
                var path = paths[i];
                var item = new MenuFlyoutItem { Text = (i == 0 ? "默认 · " : "") + path };
                item.Click += (_, _) => OpenFolder(path);
                menu.Items.Add(item);
            }
            menu.ShowAt(anchor);
        }
        catch (Exception ex) { TaskFailed?.Invoke(ex); }
    }

    // --- Services ---

    /// <summary>Normalize ComfyRoot + Python for start/stop paths. Throws when unconfigured.</summary>
    private void ResolvePaths()
    {
        RequireRoot();
        Config.ComfyRoot = Inventory.NormalizeRoot(Config.ComfyRoot);
        Config.Python = Inventory.FindPython(Config.ComfyRoot, Config.Python);
    }

    /// <summary>Start (or reuse) ComfyUI only.</summary>
    public async Task StartComfyAsync(CancellationToken ct)
    {
        RequireCompletedInstallation();
        ResolvePaths();
        startingRole = "ComfyUI";
        try { await WithPortRecoveryAsync(() => Services.StartComfy(Config, Program.Runtime, ct), ct); }
        finally { startingRole = null; }
        SetStatus("ComfyUI 已就绪：" + Config.ComfyUrl);
    }

    /// <summary>Start ComfyUI (if needed) and the Studio workbench.</summary>
    public async Task StartWebUIAsync(CancellationToken ct)
    {
        RequireCompletedInstallation();
        ResolvePaths();
        startingRole = "WebUI";
        try { await Services.StartWebUI(Config, Program.Runtime, ct); }
        finally { startingRole = null; }
        Config.SetupComplete = true;
        Save();
        SetStatus("服务已就绪：" + Config.WebUrl);
    }

    private void RequireCompletedInstallation()
    {
        if (!FreshInstallRequested && !Config.InstallPending) return;
        GoTo(1);
        throw new InvalidOperationException("安装尚未完成，请先继续准备环境与模型，再启动工作台。");
    }

    /// <summary>Set while a bundled upgrade waits for the workbench to go idle.</summary>
    public bool UpgradePending { get; private set; }

    /// <summary>Config AppRoot points at a live source checkout, not an extracted bundle.</summary>
    public bool DevAppRoot => !PathUnderVersionsRoot(Config.AppRoot);

    private static bool PathUnderVersionsRoot(string path)
    {
        try
        {
            var root = Path.GetFullPath(Path.Combine(Program.StateDir, "versions")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool SamePath(string a, string b)
    {
        try { return Path.GetFullPath(a).Equals(Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>True while the running workbench has an active batch or a non-empty queue.</summary>
    public async Task<bool> IsWorkbenchBusyAsync(CancellationToken ct)
    {
        var current = await ServiceManager.Json(Config.WebUrl + "/api/batches/current", ct);
        var activity = RuntimePolicies.WorkbenchActivity(current);
        if (activity == WorkbenchActivity.Unknown) throw new IOException("无法确认工作台是否空闲，请检查连接后重试；现有服务保持运行。");
        return activity == WorkbenchActivity.Busy;
    }

    /// <summary>
    /// Move the workbench onto the version bundled in this EXE (or restart a stale
    /// running instance) before services start. Skips dev-mode appRoots outside the
    /// versions tree; defers while a batch is running; refuses when the port is held
    /// by something that is not one of our snapshots.
    /// </summary>
    /// <summary>Returns true when a stale running instance had to be stopped (a restart is appropriate).</summary>
    public async Task<bool> EnsureCurrentVersionAsync(CancellationToken ct)
    {
        if (!Config.SetupComplete || DevAppRoot) return false;
        var desired = Config.PinnedAppRoot.Length > 0 ? Config.PinnedAppRoot : Program.AppRoot;
        var health = await ServiceManager.PortOpen(Config.WebPort, ct) ? await ServiceManager.Json(Config.WebUrl + "/api/launcher-health", ct) : null;
        var runningDir = ServiceManager.IsWorkbenchHealth(health) && health!.Value.TryGetProperty("app_dir", out var dir) && dir.ValueKind == JsonValueKind.String ? dir.GetString()! : "";
        var staleRunning = PathUnderVersionsRoot(runningDir) && !SamePath(runningDir, desired);
        var shouldSwitch = RuntimePolicies.ShouldSwitchBundledVersion(Config.AppRoot, desired, Path.Combine(Program.StateDir, "versions"), Config.SetupComplete, Config.PinnedAppRoot.Length > 0);
        if (!shouldSwitch && !staleRunning) return false;
        if (runningDir.Length > 0 && await IsWorkbenchBusyAsync(ct))
        {
            var runningConfig = Config.Clone();
            runningConfig.AppRoot = runningDir;
            if (!PathUnderVersionsRoot(runningDir) || !ServiceManager.IsOurWebUi(health, runningConfig))
                throw new PortConflictException(Config.WebPort);
            // The persisted pointer can be newer than the process after an
            // interrupted upgrade. Reuse the proven live context until idle.
            Config.AppRoot = runningDir;
            Save();
            UpgradePending = true;
            SetStatus("工作台正在生成，继续使用当前版本；空闲后可应用新版。");
            return false;
        }
        await TransitionVersionAsync(desired, Config.PinnedAppRoot, ct);
        return true;
    }

    private async Task TransitionVersionAsync(string target, string pin, CancellationToken ct)
    {
        if (!File.Exists(Path.Combine(target, "run.py"))) throw new IOException("目标工作台版本资源不存在。");
        ResolvePaths();
        // A pending Comfy argument change must fail before stopping the current workbench.
        await ServiceManager.CheckComfyReuseAsync(Config, ct);
        var previous = Config.Clone();
        var wasRunning = await ServiceManager.PortOpen(Config.WebPort, ct);
        if (wasRunning)
        {
            var health = await ServiceManager.Json(Config.WebUrl + "/api/launcher-health", ct);
            if (!ServiceManager.IsWorkbenchHealth(health)) throw new PortConflictException(Config.WebPort);
            if (health!.Value.TryGetProperty("app_dir", out var liveDir) && liveDir.ValueKind == JsonValueKind.String && PathUnderVersionsRoot(liveDir.GetString()!))
            {
                var liveConfig = previous.Clone();
                liveConfig.AppRoot = liveDir.GetString()!;
                if (!ServiceManager.IsOurWebUi(health, liveConfig)) throw new PortConflictException(Config.WebPort);
                previous.AppRoot = liveConfig.AppRoot;
            }
            if (await IsWorkbenchBusyAsync(ct)) throw new IOException("工作台正在生成，请等待任务完成后切换版本。");
            var stopped = await Services.TryStopWebUiWhenIdleAsync(previous, Path.Combine(Program.StateDir, "versions"), ct);
            if (stopped != IdleShutdownResult.Stopped) throw new IdleShutdownException(stopped);
        }
        Config = previous.Clone();
        Config.AppRoot = target;
        Config.PinnedAppRoot = pin;
        var newStarted = false;
        try
        {
            await Services.Start(Config, Program.Runtime, ct);
            newStarted = true;
            // Publish the new pointer only after both services are healthy.
            JsonFile.Write(Path.Combine(Program.StateDir, "launcher.previous.json"), previous);
            Save();
            UpgradePending = false;
            Reloaded();
        }
        catch
        {
            if (newStarted)
            {
                try { await Services.StopOwnedAsync("WebUI"); }
                catch (Exception ex) { AppendLog("新版服务清理失败：" + ex.Message); }
            }
            Config = previous;
            try { Save(); }
            catch (Exception ex) { AppendLog("原版本配置恢复写入失败：" + ex.Message); }
            Reloaded();
            if (wasRunning)
            {
                using var recovery = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                try { await Services.Start(Config, Program.Runtime, recovery.Token); AppendLog("新版本启动失败，已恢复原工作台服务。"); }
                catch (Exception ex) { AppendLog("已恢复原版本配置；服务恢复失败，请重试启动：" + ex.Message); }
            }
            throw;
        }
    }

    /// <summary>Start everything, handle port conflicts (bounded retries), open the workbench, hide unless forced.</summary>
    public async Task StartServicesAsync(CancellationToken ct)
    {
        if (FreshInstallRequested || Config.InstallPending)
        {
            GoTo(1);
            SetStatus("安装尚未完成，请检查安装内容后继续准备环境与模型。");
            return;
        }
        // A second conflict is common when both ports are taken, or a freed port is raced;
        // offer auto-assign up to three times instead of failing on the second conflict.
        await WithPortRecoveryAsync(async () => { await EnsureCurrentVersionAsync(ct); await StartWebUIAsync(ct); }, ct);
        Config.SetupComplete = true;
        Save();
        Program.Open(Config.WebUrl);
        SetStatus("服务已就绪：" + Config.WebUrl);
        if (Config.StartOnLaunch && !ForceShow) HideWindow?.Invoke();
    }

    private async Task WithPortRecoveryAsync(Func<Task> start, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { await start(); break; }
            catch (PortConflictException ex) when (attempt < 2)
            {
                showWindow();
                if (!SetupActive) GoTo(4); // 向导内保留完成页，冲突对话框仍可分配端口。
                var x = root();
                if (x is null || !await Dialogs.ConfirmCancel(x, "端口冲突", $"{ex.Message}\n是否自动寻找并保存空闲端口，然后重新启动？", "自动分配")) throw;
                var port = ex.Port + 1;
                while (port < 65535 && (port == Config.ComfyPort || port == Config.WebPort || await ServiceManager.PortOpen(port, ct))) port++;
                ct.ThrowIfCancellationRequested();
                if (port >= 65535) throw new InvalidOperationException("没有可用空闲端口，请手动修改端口后重试。");
                if (ex.Port == Config.ComfyPort) Config.ComfyPort = port; else Config.WebPort = port;
                Save();
            }
        }
    }

    /// <summary>Stop one managed service by role ("ComfyUI" or "WebUI"). Confirms when stopping ComfyUI while the workbench runs.</summary>
    public async Task StopServiceAsync(string role, bool webUiRunning)
    {
        showWindow();
        var x = root();
        if (x is null) return;
        var label = role == "ComfyUI" ? "ComfyUI" : "工作台";
        var message = role == "ComfyUI" && webUiRunning
            ? "停止本程序拥有的 ComfyUI？工作台将失去后端而不可用，正在执行的生成会中断。"
            : $"停止本程序拥有的{label}？正在执行的任务会中断。";
        if (!await Dialogs.ConfirmCancel(x, "停止" + label, message, "停止")) return;
        await Run(async ct =>
        {
            await Services.StopOwnedAsync(role, ct);
            SetStatus($"已停止启动器拥有的{label}；外部服务保持运行。");
        }, applyDrafts: false);
    }

    /// <summary>Reachability and ownership are separate; a live PID is not ready.</summary>
    public async Task<ServiceInfo> ProbeComfyAsync()
    {
        var info = new ServiceInfo { Url = Config.ComfyUrl };
        if (string.IsNullOrWhiteSpace(Config.ComfyRoot)) return info;
        info.Configured = true;
        info.Managed = Services.IsOwnedRunning("ComfyUI");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try
        {
            var comparison = await ServiceManager.InspectComfyReuseAsync(Config, timeout.Token);
            if (comparison is not null)
            {
                info.Ours = comparison.RootMatches;
                info.Running = info.Ours;
                info.Status = info.Ours ? info.Managed ? "ready" : "external" : "conflict";
                if (info.Ours)
                {
                    info.ArgumentsMatch = comparison.Matches;
                    if (!comparison.Matches) { info.Status = "needs_restart"; info.Detail = comparison.Detail; }
                }
            }
            else info.Status = startingRole is "ComfyUI" or "WebUI" ? "starting" : info.Managed ? "error" : "stopped";
        }
        catch (Exception ex) { info.Status = startingRole is not null ? "starting" : "error"; info.Detail = ex.Message; }
        return info;
    }

    public async Task<ServiceInfo> ProbeWebUIAsync()
    {
        var info = new ServiceInfo { Url = Config.WebUrl };
        if (string.IsNullOrWhiteSpace(Config.ComfyRoot)) return info;
        info.Configured = true;
        info.Managed = Services.IsOwnedRunning("WebUI");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try
        {
            if (await ServiceManager.PortOpen(Config.WebPort, timeout.Token))
            {
                var health = await ServiceManager.Json(Config.WebUrl + "/api/launcher-health", timeout.Token);
                info.Ours = ServiceManager.IsOurWebUi(health, Config);
                if (info.Ours)
                {
                    var status = await ServiceManager.Json(Config.WebUrl + "/api/status", timeout.Token);
                    info.Running = status is not null && status.Value.TryGetProperty("online", out var online) && online.ValueKind == JsonValueKind.True;
                    info.Status = info.Running ? info.Managed ? "ready" : "external" : startingRole is not null ? "starting" : "error";
                }
                else info.Status = "conflict";
            }
            else info.Status = startingRole == "WebUI" ? "starting" : info.Managed ? "error" : "stopped";
        }
        catch (Exception ex) { info.Status = startingRole is not null ? "starting" : "error"; info.Detail = ex.Message; }
        return info;
    }

    public async Task StopServicesAsync()
    {
        showWindow();
        var x = root();
        if (x is null || !await Dialogs.ConfirmCancel(x, "停止服务", "停止启动器拥有的服务？正在执行的生成会中断。", "停止服务")) return;
        await Run(async ct =>
        {
            await Services.StopOwnedAsync(ct: ct);
            SetStatus("已停止启动器拥有的服务。");
        }, applyDrafts: false);
    }

    public async Task TestGenerationAsync(CancellationToken ct)
    {
        var queue = await ServiceManager.Json(Config.ComfyUrl + "/queue", ct) ?? throw new IOException("请先启动 ComfyUI 和工作台。");
        if (queue.GetProperty("queue_running").GetArrayLength() > 0 || queue.GetProperty("queue_pending").GetArrayLength() > 0) throw new IOException("ComfyUI 正在处理其他任务，请等待空闲再测试。");
        var active = await ServiceManager.Json(Config.WebUrl + "/api/batches/current", ct) ?? throw new IOException("请先启动工作台。");
        if (active.TryGetProperty("batch", out var batch) && batch.ValueKind == JsonValueKind.Object && batch.TryGetProperty("status", out var s) && s.GetString() is "running" or "stopping") throw new IOException("工作台有正在执行的批次，请等待完成。");
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(30) };
        var body = JsonSerializer.Serialize(new
        {
            count = 1,
            width = 512,
            height = 512,
            steps = 4,
            model_profile = "anima",
            base_mode = "normal",
            extra_prompt = "1girl, portrait, gentle smile, simple background, safe",
            upscalers = new
            {
                latent = new { enabled = false, scale_by = 1.5 },
                seedvr2 = new { enabled = false, model_name = "" },
                nvidia = new { enabled = false, model_name = "" },
                face_refine = new { enabled = false },
            },
        });
        using var response = await client.PostAsync(Config.WebUrl + "/api/batches", new StringContent(body, System.Text.Encoding.UTF8, "application/json"), ct);
        response.EnsureSuccessStatusCode();
        SetStatus("已提交一张 512 × 512 测试图（4 步、无可选修复阶段），可在工作台查看进度。");
        Program.Open(Config.WebUrl);
    }

    // --- Versioning ---

    public Task ApplySoftwareUpdateAsync(bool workbenchWasRunning, CancellationToken ct) =>
        ApplySoftwareUpdateAsync(Config.Clone(), workbenchWasRunning, ct);

    /// <summary>
    /// Candidate startup uses the transaction's proven prior configuration, not
    /// page drafts. Stopped services stay stopped; a running workbench must grant
    /// its atomic idle shutdown before the candidate is started.
    /// </summary>
    public async Task ApplySoftwareUpdateAsync(LauncherConfig previous, bool workbenchWasRunning, CancellationToken ct)
    {
        if (!RuntimePolicies.CanApplyBundledSoftwareUpdate(previous, Path.Combine(Program.StateDir, "versions"), workbenchWasRunning))
            throw new IOException("当前使用源码工作台，无法自动替换其目录；请先切换到启动器内置工作台。");
        var next = previous.Clone();
        next.AppRoot = Program.AppRoot;
        next.PinnedAppRoot = "";
        await Services.SwitchWorkbenchWhenIdleAsync(previous, next, Program.Runtime,
            Path.Combine(Program.StateDir, "versions"), workbenchWasRunning, ct);
        // Do not publish either the workbench pointer or pin removal until the
        // requested service transition has completed successfully.
        JsonFile.Write(Path.Combine(Program.StateDir, "launcher.previous.json"), previous);
        Config = next;
        Save();
        UpgradePending = false;
        Reloaded();
        SetStatus(workbenchWasRunning ? "新版启动器和工作台已就绪。" : "新版启动器已就绪；工作台保持停止状态。");
    }

    /// <summary>
    /// Runs in the restored launcher after a failed candidate. A candidate that
    /// has since accepted work is kept intact instead of force-killed or hidden
    /// behind a configuration pointing to an unrelated old snapshot.
    /// </summary>
    public async Task RecoverSoftwareUpdateAsync(LauncherConfig previous, bool workbenchWasRunning, CancellationToken ct)
    {
        var restored = previous.Clone();
        var live = restored.Clone();
        if (await ServiceManager.PortOpen(restored.WebPort, ct))
        {
            var health = await ServiceManager.Json(restored.WebUrl + "/api/launcher-health", ct);
            if (!ServiceManager.IsWorkbenchHealth(health)
                || !health!.Value.TryGetProperty("app_dir", out var directory)
                || directory.ValueKind != JsonValueKind.String || !PathUnderVersionsRoot(directory.GetString()!))
                throw new IdleShutdownException(IdleShutdownResult.IdentityMismatch);
            live.AppRoot = directory.GetString()!;
            if (!ServiceManager.IsOurWebUi(health, live)) throw new IdleShutdownException(IdleShutdownResult.IdentityMismatch);
            if (!workbenchWasRunning || !SamePath(live.AppRoot, restored.AppRoot))
            {
                var stopped = workbenchWasRunning
                    ? await Services.TryStopWebUiWhenIdleAsync(live, Path.Combine(Program.StateDir, "versions"), ct)
                    : IdleShutdownResult.IdentityMismatch;
                if (stopped != IdleShutdownResult.Stopped)
                {
                    // Preserve the live context (including a deliberate version
                    // pin) if newer user work prevents recovery.
                    Config = live;
                    Config.PinnedAppRoot = live.AppRoot;
                    Save();
                    Reloaded();
                    throw new IdleShutdownException(stopped);
                }
            }
        }
        if (workbenchWasRunning) await Services.StartWebUIForSoftwareUpdateAsync(restored, Program.Runtime, ct);
        Config = restored;
        Save();
        UpgradePending = false;
        Reloaded();
        SetStatus(workbenchWasRunning ? "更新未完成，已恢复原工作台服务。" : "更新未完成，原版本已恢复，工作台保持停止状态。");
    }

    public async Task ApplyBundledVersionAsync(CancellationToken ct)
    {
        if (SamePath(Config.AppRoot, Program.AppRoot))
        {
            Config.PinnedAppRoot = "";
            Save();
            SetStatus("当前已经是此启动器内置版本。");
            return;
        }
        await TransitionVersionAsync(Program.AppRoot, "", ct);
        SetStatus("新版本健康检查通过；旧版本与用户数据已保留。");
    }

    public async Task RollbackVersionAsync(CancellationToken ct)
    {
        var previousFile = Path.Combine(Program.StateDir, "launcher.previous.json");
        if (!File.Exists(previousFile)) throw new IOException("还没有可回退的上一工作台版本。");
        var previous = JsonFile.Read<LauncherConfig>(previousFile);
        await TransitionVersionAsync(previous.AppRoot, previous.AppRoot, ct);
        SetStatus("已恢复上一版本并固定使用；主动应用新版后才解除固定。");
    }

    // --- Misc ---

    public void CreateDesktopShortcut()
    {
        var exe = Environment.ProcessPath!;
        var shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Anima Random Studio.lnk");
        // COM arguments are supplied as data, never interpolated into a shell command.
        var type = Type.GetTypeFromProgID("WScript.Shell")!;
        dynamic shell = Activator.CreateInstance(type)!;
        dynamic link = shell.CreateShortcut(shortcut);
        link.TargetPath = exe;
        link.WorkingDirectory = Path.GetDirectoryName(exe);
        link.IconLocation = exe + ",0";
        link.Save();
        SetStatus("桌面快捷方式已创建。");
    }

    public void OfferUpgradeIfNeeded()
    {
        if (Config.SetupComplete && !DevAppRoot && Config.PinnedAppRoot.Length == 0 && !SamePath(Config.AppRoot, Program.AppRoot) && !upgradeOffered)
        {
            upgradeOffered = true;
            SetStatus("检测到工作台新版本；下次主动启动时检查并应用，可在设置中管理版本。");
        }
    }

    private void RequireRoot()
    {
        if (string.IsNullOrWhiteSpace(Config.ComfyRoot)) throw new InvalidDataException("请先选择 ComfyUI 目录：新建选空目录，导入选含 main.py 的目录。");
    }

    public void Dispose() { DisposeSoftwareUpdates(); modelInspectionLifetime.Dispose(); Downloads.Dispose(); }
}

public sealed record EnvironmentFields(string ComfyRoot, string Python, string SourceId, string GitMirror, string HfMirror, string PipIndex, string CustomRepository, bool Fresh = false, bool PythonExplicit = false);
public sealed record SettingsFields(int ComfyPort, int WebPort, string Theme, bool StartOnLaunch = false, string ComfyArgumentsText = "")
{
    public static SettingsFields From(LauncherConfig config) => new(config.ComfyPort, config.WebPort, config.Theme,
        config.StartOnLaunch, ComfyLaunchArguments.Format(config.ComfyArguments));
    public bool Matches(LauncherConfig config)
    {
        if (ComfyPort != config.ComfyPort || WebPort != config.WebPort || Theme != config.Theme || StartOnLaunch != config.StartOnLaunch) return false;
        try
        {
            var args = ComfyLaunchArguments.Parse(ComfyArgumentsText);
            ComfyLaunchArguments.Validate(args);
            return ComfyLaunchArguments.Equivalent(args, config.ComfyArguments);
        }
        catch (InvalidDataException) { return false; }
    }
}

/// <summary>Reachability/ownership snapshot for one managed service.</summary>
public sealed class ServiceInfo
{
    public bool Configured { get; set; }
    public bool Running { get; set; }
    public bool Managed { get; set; }
    public bool Ours { get; set; }
    public bool ArgumentsMatch { get; set; }
    public string Url { get; set; } = "";
    public string Status { get; set; } = "unconfigured";
    public string Detail { get; set; } = "";
}
