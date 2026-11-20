using Anima.Launcher.Core;
using System.Runtime.InteropServices;

namespace Anima.Launcher;

internal sealed partial class LauncherState
{
    private readonly string setupFile = Path.Combine(Program.StateDir, "onboarding.json");
    private readonly SetupReviewGate setupReview = new();
    private readonly List<SetupProgress> setupStages = [];
    private bool setupRetryAvailable;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? setupDraftTimer;
    private string? setupWriteError;
    private bool setupImportNeedsRepair = true;
    private Func<CancellationToken, Task>? setupInstallAction;

    public SetupRecord Setup { get; private set; } = new();
    public bool SetupActive { get; private set; }
    public event Action? SetupChanged;
    public string SetupError { get; private set; } = "";
    public string SetupNotice { get; private set; } = "";
    public string SetupSystemSummary { get; private set; } = "正在检查系统和显卡…";
    public bool? SetupSystemSupported { get; private set; }
    public string SetupStatusText { get; private set; } = "";
    public int SetupDownloadPercent { get; private set; } = -1;
    public IReadOnlyList<SetupProgress> SetupStages => setupStages;
    public IReadOnlyList<string> SetupMissingModels { get; private set; } = [];
    public InstallationPlan? SetupPlan { get; private set; }
    public PreflightResult? SetupPreflight { get; private set; }
    public bool SetupCanInstall => !Busy && setupReview.CanInstall && SetupPlan is not null;
    public bool SetupCanResume => !Busy && !Setup.SelectionNeedsReview && (Setup.InstallStarted && SetupPlan is not null || Setup.InstallFinished && Setup.ModelsPending
        || setupRetryAvailable && setupInstallAction is not null);
    public bool SetupImportNeedsRepair => setupImportNeedsRepair;
    public bool SetupSelectionNeedsReview => Setup.SelectionNeedsReview && !setupReview.CanInstall;
    public bool SetupLegacyDirectRoot => Setup.Fresh && Setup.DirectComfyRoot;

    private void InitializeSetup(bool resume = false)
    {
        SetupRecord? saved = null;
        if (File.Exists(setupFile))
        {
            try
            {
                saved = JsonFile.Read<SetupRecord>(setupFile);
                if (saved.SchemaVersion != 1 || saved.Draft is null || !Enum.IsDefined(saved.Step))
                    throw new InvalidDataException("无法识别安装向导记录。");
                // Draft fields can be incomplete (for example a URL being typed).
                // Validate them on review, not when restoring the rest of the draft.
            }
            catch (Exception ex)
            {
                try { File.Copy(setupFile, setupFile + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmssfff")); }
                catch (Exception backupError) { setupWriteError = "向导记录无法备份，已保留原文件：" + backupError.Message; }
                AppendLog("安装向导记录无法读取，将根据实际环境恢复：" + ex.Message);
                saved = null;
            }
        }
        var reviewedBeforeExit = saved?.Step == SetupStep.Review && saved.ReviewedPlan is not null;
        var restore = SetupRouting.ResolveForRuntime(Config, saved, Program.Runtime, Program.Models, Program.ModelProfiles, resume);
        var route = restore.Step;
        Setup = saved ?? NewSetupRecord();
        if (Setup.Draft.NetworkMode == "system" && NetworkPolicySnapshot.SystemUsesPac())
            SetupNotice = "检测到系统 PAC。安装前请在「安装位置 → 高级下载设置」选择直连或填写 HTTP 代理地址。";
        // A saved receipt cannot establish that this process's runtime is healthy.
        Setup.MarkRuntimeCheckStale();
        if (Setup.AssistantStatus == AssistantSetupStatus.Configured) Setup.AssistantStatus = AssistantSetupStatus.Saved;
        Setup.ConfigurationIssues ??= [];
        if (Config.InstallPending && (saved is null || !SamePath(Config.ComfyRoot, SetupTarget(saved))))
        {
            Setup = NewSetupRecord();
            Setup.Fresh = true;
            Setup.InstallStarted = true;
        }
        SetupActive = route.HasValue;
        if (route is { } step) Setup.Step = step;
        ApplySetupRuntimeRestore(restore);
        if (Config.InstallPending && route == SetupStep.Installing)
        {
            Setup.Fresh = true;
            Setup.InstallStarted = true;
            Setup.InstallFinished = false;
            Setup.ModelsPending = false;
        }
        if (Setup.Fresh && Setup.InstallStarted && string.IsNullOrWhiteSpace(Setup.Draft.InstallRoot)
            && !string.IsNullOrWhiteSpace(Setup.Draft.ComfyRoot))
            Setup.DirectComfyRoot = true;
        if (route == SetupStep.Installing)
            SetupNotice = Setup.InstallFinished && Setup.ModelsPending
                ? "环境已安装，所选模型尚未准备完成。点击继续可复用已下载文件。"
                : "发现上次未完成的安装。点击继续将使用原先确认的固定版本，已完成步骤会复用。";
        if (Setup.InstallStarted && Setup.ReviewedPlan is { } resumed && SamePath(resumed.ComfyRoot, SetupTarget(Setup)))
            SetupPlan = resumed;
        if (route == SetupStep.Location && saved?.InstallFinished == true)
            SetupNotice = Setup.SelectionNeedsReview ? "上次选择尚未确认，请检查所选内容后再开始安装。"
                : "上次配置的环境文件发生变化，请重新检查目录。";
        if (route == SetupStep.Location && reviewedBeforeExit)
            SetupNotice = "上次检查结果已保留。请重新检查当前目录、磁盘和网络，再确认安装。";
        SetSetupRestoreNotice(restore);
        SynchronizeSetupVersion();
        ResetSetupStages();
    }

    private void ApplySetupRuntimeRestore(SetupRestoreDecision restore)
    {
        if (restore.RequiresRepairReview)
        {
            // A completion receipt without a usable frozen plan cannot be resumed as
            // a fresh installation. Review a repair of the existing environment.
            Setup.Fresh = false;
            Setup.DirectComfyRoot = false;
            Setup.Draft = Config.Clone();
            Setup.InstallStarted = false;
            Setup.InstallFinished = false;
            Setup.ModelsPending = false;
            Setup.RuntimeVerified = false;
            SetupPlan = null;
        }
        if (restore.Step is { } step) Setup.Step = step;
    }

    private void SetSetupRestoreNotice(SetupRestoreDecision restore)
    {
        if (restore.RequiresRepairReview)
            SetupNotice = "上次安装已保留。请重新检查已有环境，生成补齐必需模型与运行资源的修复计划。";
    }

    private SetupRecord NewSetupRecord() => new()
    {
        Draft = Config.Clone(),
        Fresh = string.IsNullOrWhiteSpace(Config.ComfyRoot) || Config.InstallPending,
        Step = SetupStep.Welcome,
        AssistantStatus = AssistantSetupStatus.Pending,
    };

    private static string SetupTarget(SetupRecord record)
    {
        try
        {
            return record.Fresh && record.Draft.InstallRoot.Length > 0
                ? Path.Combine(record.Draft.InstallRoot, "ComfyUI") : record.Draft.ComfyRoot;
        }
        catch { return ""; }
    }

    public void NotifySetup() => RequestPresentation(DisplayChange.Setup);

    private void SynchronizeSetupVersion() => RuntimePolicies.SynchronizeManagedSetupVersion(Setup.Draft, Config, Path.Combine(Program.StateDir, "versions"));

    private void PersistSetup()
    {
        setupDraftTimer?.Stop();
        if (setupWriteError is not null) throw new IOException(setupWriteError);
        SynchronizeSetupVersion();
        JsonFile.Write(setupFile, Setup);
    }

    internal void FlushSetupDraft()
    {
        if (setupDraftTimer?.IsRunning == true) PersistSetup();
    }

    private void ScheduleDraftWrite()
    {
        if (setupDraftTimer is null)
        {
            setupDraftTimer = downloadUi.CreateTimer();
            setupDraftTimer.IsRepeating = false;
            setupDraftTimer.Interval = TimeSpan.FromMilliseconds(350);
            setupDraftTimer.Tick += (_, _) =>
            {
                try { PersistSetup(); }
                catch (Exception ex) { SetupError = "无法保存安装草稿：" + ex.Message; NotifySetup(); }
            };
        }
        setupDraftTimer.Stop();
        setupDraftTimer.Start();
    }

    public void BeginSetup()
    {
        if (SetupActive || Busy) return;
        InitializeSetup(resume: true);
        if (!SetupActive)
        {
            Setup = NewSetupRecord();
            SetupActive = true;
        }
        SetupError = "";
        setupReview.Invalidate();
        SetupPlan = Setup.InstallStarted && !Setup.SelectionNeedsReview ? Setup.ReviewedPlan : null;
        SetupPreflight = null;
        setupImportNeedsRepair = true;
        setupRetryAvailable = false;
        PersistSetup();
        NotifySetup();
    }

    public void SetSetupMode(bool fresh)
    {
        if (Busy) return;
        if (Setup.Fresh != fresh)
        {
            Setup.SetInstallationMode(fresh);
            Setup.InstallStarted = false;
            Setup.InstallFinished = false;
            Setup.ModelsPending = false;
            Setup.DirectComfyRoot = false;
            Setup.ReviewedPlan = null;
            Setup.Draft.Python = "";
            Setup.Draft.PythonExplicit = false;
            Setup.ConfigurationIssues = [];
            InvalidateSetupReview();
        }
        MoveSetupTo(SetupStep.Location);
    }

    public void SaveSetupDraft(LauncherConfig draft)
    {
        if (Busy) return;
        var targetChanged = !SamePath(SetupTarget(Setup), Setup.Fresh && draft.InstallRoot.Length > 0 ? Path.Combine(draft.InstallRoot, "ComfyUI") : draft.ComfyRoot);
        if (!Setup.UpdateDraft(draft)) return;
        if (targetChanged)
        {
            Setup.InstallStarted = false;
            Setup.InstallFinished = false;
            Setup.ModelsPending = false;
            Setup.OptionalReviewAtCompletion = false;
            Setup.SkippedOptionalModelIds = [];
            Setup.ConfigurationIssues = [];
            setupStartedServices.Clear();
            SetupServicesReached = false;
        }
        if (!Setup.InstallStarted || targetChanged)
            Setup.ReviewedPlan = null;
        Setup.RuntimeVerified = false;
        InvalidateSetupReview();
        ScheduleDraftWrite();
        NotifySetup();
    }

    private void InvalidateSetupReview()
    {
        setupReview.Invalidate();
        SetupPlan = null;
        SetupPreflight = null;
        setupRetryAvailable = false;
        setupInstallAction = null;
        SetupError = "";
        SetupMissingModels = [];
        ResetSetupModels();
    }

    public void MoveSetupTo(SetupStep step)
    {
        if (Busy) return;
        if (step is SetupStep.Welcome or SetupStep.Location)
        {
            if (Setup.Step is SetupStep.Installing or SetupStep.Complete)
                Setup.SelectionNeedsReview = true;
            InvalidateSetupReview();
        }
        Setup.Step = step;
        PersistSetup();
        NotifySetup();
    }

    public void SuspendSetup()
    {
        if (Busy) return;
        Setup.Suspended = true;
        PersistSetup();
        SetupActive = false;
        NotifySetup();
    }

    private LauncherConfig SetupConfiguration()
    {
        SynchronizeSetupVersion();
        var draft = Setup.Draft.Clone();
        // Runtime/service fields belong to the active launcher, never to stale UI drafts.
        draft.AppRoot = Config.AppRoot;
        draft.DataDir = Config.DataDir;
        if (Setup.Fresh) draft.ComfyPort = Config.ComfyPort;
        draft.WebPort = Config.WebPort;
        draft.Theme = Config.Theme;
        draft.StartOnLaunch = Config.StartOnLaunch;
        draft.AutoCheckUpdates = Config.AutoCheckUpdates;
        draft.PinnedAppRoot = Config.PinnedAppRoot;
        if (Setup.Fresh)
        {
            draft.ExternalEnvironmentMode = null;
            if (SetupLegacyDirectRoot)
            {
                draft.ComfyRoot = Path.GetFullPath(draft.ComfyRoot.Trim().Trim('"'));
                draft.RuntimeRoot = InstallationPlanner.ResolveRuntimeRoot(draft, Program.StateDir);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(draft.InstallRoot))
                    throw new InvalidDataException("请选择安装位置。");
                draft.InstallRoot = Inventory.InstallLocation(draft.InstallRoot);
                draft.ComfyRoot = Path.Combine(draft.InstallRoot, "ComfyUI");
                draft.RuntimeRoot = Path.Combine(draft.InstallRoot, "runtime");
            }
            draft.Python = Path.Combine(draft.ComfyRoot, ".venv", "Scripts", "python.exe");
            draft.PythonExplicit = false;
            draft.SelectedProfile = "anima";
        }
        else
        {
            if (string.IsNullOrWhiteSpace(draft.ComfyRoot))
                throw new InvalidDataException("请选择已有 ComfyUI 目录。");
            draft.ComfyRoot = Inventory.NormalizeRoot(draft.ComfyRoot);
            if (Config.InstallPending && SamePath(Config.ComfyRoot, draft.ComfyRoot))
                throw new InvalidOperationException("此目录的安装尚未完成，请选择安装完整环境以继续，或导入另一个环境。");
            draft.Python = Inventory.FindPython(draft.ComfyRoot, draft.PythonExplicit ? draft.Python : "", draft.PythonExplicit);
            draft.InstallRoot = "";
            draft.ExternalEnvironmentMode ??= "readOnly";
            draft.RuntimeRoot = draft.ReadOnlyConnection ? ExternalWorkbench.RuntimeRoot(Program.StateDir, draft.ComfyRoot)
                : SamePath(Config.ComfyRoot, draft.ComfyRoot) && !Config.ReadOnlyConnection && Config.RuntimeRoot.Length > 0
                ? Config.RuntimeRoot : Path.TrimEndingDirectorySeparator(draft.ComfyRoot) + ".anima-runtime";
        }
        draft.ManagedComfy = File.Exists(Path.Combine(draft.ComfyRoot, ".anima-revision.json"));
        draft.InstallPending = Setup.Fresh;
        if (!SamePath(draft.ComfyRoot, Config.ComfyRoot)) draft.SetupComplete = false;
        draft.Validate();
        return draft;
    }

    public async Task CheckSetupAsync()
    {
        if (Busy) return;
        var revision = setupReview.Begin();
        Setup.Step = SetupStep.Review;
        SetupError = "";
        SetupNotice = "";
        SetupPlan = null;
        SetupPreflight = null;
        setupImportNeedsRepair = true;
        setupRetryAvailable = false;
        PersistSetup();
        NotifySetup();
        await Run(async ct =>
        {
            SetStatus("正在检查系统、目录和磁盘空间…");
            var draft = SetupConfiguration();
            var fresh = Setup.Fresh;
            if (draft.ReadOnlyConnection)
            {
                var connection = await ExternalWorkbench.ReviewConnectionAsync(draft, Program.Runtime, Program.StateDir, ct);
                if (!setupReview.Complete(revision, true)) return;
                setupImportNeedsRepair = false;
                SetupPlan = Setup.ReviewedPlan = connection;
                SetupPreflight = new([], []);
                Setup.OptionalModelIds = [];
                Setup.Draft = draft;
                SetupNotice = "已核对已有 ComfyUI 的目录、解释器和运行能力。只读连接将准备独立工作台。";
                PersistSetup();
                SetStatus("连接检查通过。");
                return;
            }
            var network = NetworkPolicySnapshot.Capture(draft);
            AppendLog("向导检查网络模式：" + network.Mode);
            if (network.Mode == "system") NetworkPolicySnapshot.RejectPac();
            if (!fresh && !draft.HasIsolatedWorkbench) await WorkbenchDependencies.ValidateInterpreterAsync(draft, ct);
            var plan = await Task.Run(() => InstallationPlanner.Create(draft, Program.Runtime, Program.StateDir,
                fresh, Program.Models, Program.ModelProfiles, Setup.OptionalModelIds), ct);
            plan = await InstallationPlanner.ResolveStableAsync(plan, Program.Runtime, Program.StateDir, ct);
            plan = await InstallationDependencyRecovery.ReviewAsync(plan, Program.StateDir, ct);
            plan = await InstallationPlanner.PreparePlanAsync(plan, Program.Models, Program.ModelProfiles,
                Path.Combine(Program.Assets, "probe_comfy.py"), ct);
            var preflight = await InstallationPlanner.PreflightAsync(plan, ct);
            plan = preflight.VerifiedPlan ?? plan;
            if (!fresh && preflight.CanInstall)
            {
                try
                {
                    // PreparePlanAsync already scanned the models and may have deferred
                    // extra paths until PyYAML is installed. Do not repeat a strict probe.
                    UpdateSetupMissingModels(plan.ModelInventory.ToList());
                    var import = await SetupImportReview.InspectAsync(plan.CreateConfig(), plan.ResolvedRuntime ?? Program.Runtime, ct, Program.StateDir);
                    setupImportNeedsRepair = import.NeedsRepair;
                    plan = plan with { DependencyReviewIssues = plan.DependencyReviewIssues.Concat(import.PreparationIssues).Distinct().ToArray() };
                    if (draft.ExternalEnvironmentMode == "repair" && import.NeedsRepair)
                        plan = plan with { DependencyReviewIssues = plan.DependencyReviewIssues.Append(ExternalRepairBackup.Preview(plan)).ToArray() };
                    if (import.MissingCoreModules.Length > 0)
                        preflight = preflight with { Issues = preflight.Issues.Add(new("import_dependencies",
                            "核心依赖尚未安装完整（" + string.Join("、", import.MissingCoreModules) + "），将在安装环境阶段补齐。", false)) };
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    preflight = preflight with { Issues = preflight.Issues.Add(new("import_probe", "无法检查导入环境：" + ex.Message)) };
                }
            }
            ct.ThrowIfCancellationRequested();
            if ((fresh || setupImportNeedsRepair) && preflight.CanInstall)
            {
                try { await new ComfyUpdateCoordinator(Program.StateDir, "git.exe").EnsureStoppedAsync(draft, ct); }
                catch (InvalidOperationException ex)
                {
                    preflight = preflight with { Issues = preflight.Issues.Add(new("services_running", ex.Message)) };
                }
            }
            if (!setupReview.Complete(revision, preflight.CanInstall)) return;
            SetupPlan = plan;
            Setup.ReviewedPlan = plan;
            SetupPreflight = preflight;
            Setup.Draft = plan.CreateConfig();
            SetupNotice = fresh ? $"ComfyUI {plan.Release?.Tag}（{plan.Release?.Commit[..12]}）与 {plan.NodeVersions.Length} 个新装节点的提交已固定。继续安装会复用这些版本与依赖计划；已有节点源码保持原样。"
                : setupImportNeedsRepair ? "将补齐缺失节点与环境依赖，保留已有节点版本和 GPU 运行环境。" : "现有节点与核心依赖齐全，可直接完成导入。";
            SetupError = preflight.CanInstall ? "" : string.Join("\n", preflight.Issues.Where(i => i.Blocking).Select(i => i.Message));
            PersistSetup();
            SetStatus(preflight.CanInstall ? "检查完成，请确认安装内容。" : "检查发现需要处理的问题。");
        }, applyDrafts: false, setupOperation: true, cancellationMessage: () => "检查已取消。可以修改选项或重新检查。");
    }

    public async Task InstallSetupAsync()
    {
        if (!SetupCanInstall || SetupPlan is null) return;
        var reviewed = SetupPlan;
        var owner = DownloadTasks.RegisterOwner("setup", "安装环境与所选模型", reviewed.CreateConfig(),
            payload: System.Text.Json.JsonSerializer.SerializeToElement(reviewed, JsonFile.Options));
        // The just-confirmed wizard fields supersede older download-page preferences.
        if (reviewed.Purpose != "external-connection" && reviewed.CreateConfig().ExternalEnvironmentMode != "repair") savedDownloadSettings = reviewed.CreateConfig();
        Setup.InstallationOwnerId = owner.Id;
        Setup.SelectionNeedsReview = false;
        Setup.Step = SetupStep.Installing;
        Setup.InstallStarted = true;
        Setup.InstallFinished = false;
        Setup.ModelsPending = false;
        Setup.OptionalReviewAtCompletion = false;
        Setup.RuntimeVerified = false;
        Setup.SkippedOptionalModelIds = [];
        SetupError = "";
        SetupNotice = "";
        setupRetryAvailable = false;
        ResetSetupStages();
        PersistSetup();
        setupReview.Invalidate();
        setupInstallAction = async ct =>
        {
            activeDownloadOwner = owner.Id;
            try { await DownloadTasks.RunOwnerAsync(owner.Id, token => ExecuteReviewedSetupAsync(reviewed, token), ct); }
            finally { activeDownloadOwner = null; }
        };
        setupRetryAvailable = true;
        await Run(setupInstallAction, applyDrafts: false, resumeConfig: reviewed.CreateConfig(), resumeFresh: reviewed.Fresh, setupOperation: true);
    }

    private async Task ExecuteReviewedSetupAsync(InstallationPlan reviewed, CancellationToken ct)
    {
        activeInstallationPlan = reviewed;
        if (reviewed.Purpose == "external-connection")
        {
            await VerifyExternalSetupAsync(reviewed, ct);
            return;
        }
        Config = reviewed.CreateConfig();
        FreshInstallRequested = reviewed.Fresh;
        Setup.Step = SetupStep.Installing;
        Setup.InstallStarted = true;
        PersistSetup();
        if (!Setup.InstallFinished || Setup.FailedFeatureIds.Length > 0)
        {
            // Recheck this frozen owner's interpreter, including after restart/resume.
            // The last UI review flag may belong to a different environment or be stale.
            var import = reviewed.Fresh ? null
                : await SetupImportReview.InspectAsync(Config, reviewed.ResolvedRuntime ?? Program.Runtime, ct, Program.StateDir);
            if (import is { NeedsRepair: false } && Setup.FailedFeatureIds.Length == 0)
            {
                UpdateSetupProgress(new("verify", "检查已有环境", SetupStageStatus.Running));
                Config.ComfyRoot = Inventory.NormalizeRoot(Config.ComfyRoot);
                Config.Python = Inventory.FindPython(Config.ComfyRoot, Config.Python, Config.PythonExplicit);
                FreshInstallRequested = false;
                Config.InstallPending = false;
                Save();
                UpdateSetupProgress(new("verify", "已有环境已接入，等待模型和运行检查", SetupStageStatus.Pending));
            }
            else await InstallEnvironmentAsync(ct, reviewed, prepareModels: false);
            for (var i = 0; i < setupStages.Count; i++)
                if (setupStages[i].Status == SetupStageStatus.Pending && setupStages[i].StageId is not ("models" or "verify"))
                    setupStages[i] = setupStages[i] with { Status = SetupStageStatus.Complete, Detail = setupStages[i].Detail + " · 已复用" };
            Setup.Draft = Config.Clone();
            Setup.InstallFinished = true;
            Setup.ModelsPending = true;
            PersistSetup();
        }
        else
        {
            // Reuse the installed interpreter, while retaining this owner's chosen transport snapshot.
            Config.Python = Inventory.FindPython(Config.ComfyRoot, Setup.Draft.Python, Setup.Draft.PythonExplicit);
            Config.InstallPending = false;
            Config.ManagedComfy = Setup.Draft.ManagedComfy;
            FreshInstallRequested = false;
        }
        await RunSetupSelectedModelsAsync(reviewed.ModelIds.ToArray(), ct);
    }

    private async Task VerifyExternalSetupAsync(InstallationPlan plan, CancellationToken ct)
    {
        if (configWriteError is not null) throw new IOException(configWriteError);
        var candidate = plan.CreateConfig();
        candidate.ComfyRoot = Inventory.NormalizeRoot(candidate.ComfyRoot);
        candidate.Python = Inventory.FindPython(candidate.ComfyRoot, candidate.Python, candidate.PythonExplicit);
        candidate.RuntimeRoot = ExternalWorkbench.RuntimeRoot(Program.StateDir, candidate.ComfyRoot);
        candidate.InstallPending = false;
        var previousOwned = Services.RunningOwned();
        SetupRuntimeReadinessReport report;
        try
        {
            UpdateSetupProgress(new("external-service", "正在确认已有 ComfyUI 服务身份", SetupStageStatus.Running));
            report = await ExternalWorkbench.VerifyAndSaveConnectionAsync(candidate, configFile, async token =>
            {
                if (!await ServiceManager.CheckComfyReuseAsync(candidate, token))
                    throw new IOException("请先用原整合包启动所选的 ComfyUI，再继续连接。");
                UpdateSetupProgress(new("external-service", "已确认 ComfyUI 目录和进程身份", SetupStageStatus.Complete));
                UpdateSetupProgress(new("workbench", "正在准备独立 Python 并验证工作台", SetupStageStatus.Running));
                await Services.StartWebUI(candidate, Program.Runtime, token);
                UpdateSetupProgress(new("workbench", "独立工作台已连接所选 ComfyUI", SetupStageStatus.Complete));
                UpdateSetupProgress(new("verify", "正在检查可用的节点、模型和功能", SetupStageStatus.Running));
                using var readiness = new SetupRuntimeReadiness();
                return await readiness.CheckAsync(candidate, Program.Runtime, Program.Models, Program.ModelProfiles, [], token,
                    AppendLog, candidate.RuntimeRoot, features: ["base"]);
            }, ct);
        }
        catch
        {
            Services.StopStartedServices(Services.RunningOwned().Except(previousOwned).ToArray());
            throw;
        }
        setupStartedServices.UnionWith(Services.RunningOwned().Except(previousOwned));
        Config = candidate;
        savedDownloadSettings = candidate.Clone();
        FreshInstallRequested = false;
        SetupServicesReached = SetupRequiredModelsReady = Setup.InstallFinished = true;
        Setup.RecordRuntimeCheck(report);
        Setup.AssistantAvailable = report.PromptAssistantAvailable;
        SetupOptionalFailures.Clear();
        SetupOptionalFailures.AddRange(report.OptionalIssues.Select(issue => issue.Message));
        UpdateSetupProgress(new("verify", "已检查可用功能", SetupStageStatus.Complete));
        AdvanceAfterSetupModels();
    }

    public async Task ResumeSetupAsync()
    {
        if (Busy) return;
        if (Setup.SelectionNeedsReview) { await CheckSetupAsync(); return; }
        SetupError = "";
        SetupNotice = "";
        if (SetupPlan is { } plan && Setup.InstallStarted)
        {
            var owner = DownloadTasks.RegisterOwner("setup", "安装环境与所选模型", plan.CreateConfig(),
                payload: System.Text.Json.JsonSerializer.SerializeToElement(plan, JsonFile.Options), resumeEnvironmentReview: true);
            await ResumeDownloadOwnerAsync(owner.Id);
            return;
        }
        if (!SetupCanResume) { await CheckSetupAsync(); return; }
        await Run(setupInstallAction!, applyDrafts: false, resumeConfig: Setup.ReviewedPlan?.CreateConfig(), resumeFresh: Setup.Fresh, setupOperation: true);
    }

    public async Task StartSetupWorkbenchAsync()
    {
        if (Busy || Setup.Step != SetupStep.Complete) return;
        var restore = SetupRouting.ResolveForRuntime(Config, Setup, Program.Runtime, Program.Models, Program.ModelProfiles);
        if (restore.RequiresRepairReview)
        {
            ApplySetupRuntimeRestore(restore);
            SetSetupRestoreNotice(restore);
            PersistSetup();
            NotifySetup();
            await CheckSetupAsync();
            return;
        }
        SetupError = "";
        setupRetryAvailable = false;
        setupModelResumeAvailable = false;
        setupModelResumePlan = null;
        setupModelPreviewPlan = null;
        SetupCompletionOperation = SetupCompletionOperation.StartWorkbench;
        await Run(async ct =>
        {
            SetupOptionalFailures.Clear();
            setupFailedOptionalModelIds.Clear();
            var selected = (activeInstallationPlan ?? Setup.ReviewedPlan)?.ModelIds.ToArray()
                ?? SetupRequiredDownloads.Select(model => model.Id).Concat(Setup.OptionalModelIds).ToArray();
            await VerifySetupRuntimeAsync(selected.Except(Setup.SkippedOptionalModelIds).ToArray(), ct);
            if (SetupOptionalFailures.Count > 0)
            {
                Setup.ModelsPending = true;
                Setup.OptionalReviewAtCompletion = true;
                PersistSetup();
                throw new InvalidOperationException("部分所选可选功能未通过运行检查。可在本页重试或跳过。\n" + string.Join("\n", SetupOptionalFailures));
            }
            Setup.ModelsPending = false;
            Setup.OptionalReviewAtCompletion = false;
            if (Config.InstallPending || FreshInstallRequested)
                throw new InvalidOperationException("环境仍有未完成的安装步骤，请重新检查。");
            OpenReadyWorkbench();
            if (BrowserOpenPending) { NotifySetup(); return; }
            CompleteSetupRecord();
            GoTo(0);
        }, applyDrafts: false, setupOperation: true);
    }

    public async Task<Uri?> GenerateSetupTestAsync(CancellationToken polling)
    {
        if (Busy || Setup.Step != SetupStep.Complete || !Setup.InstallFinished) return null;
        TestImageUrl = null;
        SetupError = "";
        await Run(async ct =>
        {
            if (!Setup.RuntimeVerified)
                await VerifySetupRuntimeAsync((Setup.ReviewedPlan?.ModelIds.ToArray() ?? [])
                    .Except(Setup.SkippedOptionalModelIds).ToArray(), ct);
            await TestGenerationAsync(ct);
        }, applyDrafts: false, setupOperation: true, cancellationToken: polling,
            cancellationMessage: () => "已停止等待试生成。已提交的任务可在工作台查看。", allowRetry: () => false);
        return TestImageUrl;
    }

    public void FinishSetupAfterManualOpen()
    {
        if (Busy || Setup.Step != SetupStep.Complete || !Setup.RuntimeVerified || !BrowserOpenPending) return;
        Config.SetupComplete = true;
        Save();
        BrowserOpenPending = false;
        CompleteSetupRecord();
        GoTo(0);
    }

    private void CompleteSetupRecord()
    {
        Setup.Step = SetupStep.Finished;
        PersistSetup();
        SetupActive = false;
        ResetSetupModels();
        SetStatus("环境已配置。可以从概览启动工作台。");
        NotifySetup();
    }

    private void ResetSetupStages()
    {
        setupStages.Clear();
        var readOnly = !Setup.Fresh && Setup.Draft.ReadOnlyConnection;
        foreach (var (id, label) in readOnly ? ExternalWorkbench.ConnectionStages() : InstallationPlanner.SetupStages(Setup.Fresh))
        {
            var complete = Setup.InstallFinished && id is not ("models" or "verify");
            setupStages.Add(new(id, complete ? label + " · 环境已就绪" : label,
                complete ? SetupStageStatus.Complete : SetupStageStatus.Pending));
        }
    }

    private void UpdateSetupProgress(SetupProgress progress)
    {
        if (!SetupActive) return;
        // Core validation happens before model downloads; the final scan is separate.
        if (progress.StageId == "validation")
            ResetTransferProgress();
        var index = setupStages.FindIndex(p => p.StageId == progress.StageId);
        if (index < 0) { setupStages.Add(progress); index = setupStages.Count - 1; }
        setupStages[index] = progress;
        SetupDownloadPercent = -1;
        NotifySetup();
    }

    private void SetSetupInterruption(string message, bool paused)
    {
        if (!SetupActive) return;
        if (paused) SetupNotice = message; else SetupError = message;
        SetupStatusText = Setup.Step == SetupStep.Review ? (paused ? "检查已取消。" : "检查未通过：" + message)
            : Setup.Step == SetupStep.Complete ? message
            : paused ? "安装已暂停，已完成的内容会保留。" : "安装尚未完成：" + message;
        if (Setup.Step != SetupStep.Complete && (!Setup.RuntimeVerified || SetupOptionalFailures.Count == 0))
        {
            Setup.RuntimeVerified = false;
            SetupRequiredModelsReady = false;
        }
        for (var i = 0; i < setupStages.Count; i++)
            if (setupStages[i].Status == SetupStageStatus.Running)
                setupStages[i] = setupStages[i] with { Status = paused ? SetupStageStatus.Paused : SetupStageStatus.Failed };
        NotifySetup();
    }

    private void UpdateSetupMissingModels(List<ModelStatus> scan)
    {
        var selected = SetupRequiredDownloads.Select(m => m.Id).ToHashSet();
        SetupMissingModels = scan.Where(m => selected.Contains(m.Model.Id) && m.Missing).Select(m => m.Model.Name).ToArray();
        if (SetupActive && Setup.Step is SetupStep.Installing or SetupStep.Complete)
        {
            SetupModelReview = BuildSetupModelReview(scan);
            SetupModelsReadiness = SetupModelReview.Readiness;
        }
    }

    private void UpdateSetupSystem(string gpu)
    {
        var supportedSystem = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)
            && RuntimeInformation.OSArchitecture == Architecture.X64;
        var lines = gpu.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var supportedGpu = lines.Length > 0 && lines.All(line =>
            InstallationPlanner.IsNvidiaDriverSupported(line.Split(',').Last().Trim()));
        SetupSystemSupported = supportedSystem && supportedGpu;
        SetupSystemSummary = !supportedSystem ? "此系统不支持自动安装，需要 Windows 10 1809 或更新版本的 x64 系统。"
            : supportedGpu ? "这台电脑支持自动安装。\n" + gpu
            : gpu + $"\n自动安装需要支持的 NVIDIA 显卡和 {InstallationPlanner.MinimumNvidiaDriver} 或更新驱动；也可导入已可用的 ComfyUI。";
        NotifySetup();
    }

    public async Task RefreshSetupCompletionAsync()
    {
        if (Setup.RuntimeVerified && SetupModelsReadiness == SetupModelReadiness.Ready) return;
        await ReviewSetupModelsAsync(showPreview: false);
    }
}
