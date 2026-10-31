using Anima.Launcher.Core;
using System.Runtime.InteropServices;

namespace Anima.Launcher;

internal sealed partial class LauncherState
{
    private readonly string setupFile = Path.Combine(Program.StateDir, "onboarding.json");
    private readonly SetupReviewGate setupReview = new();
    private readonly List<SetupProgress> setupStages = [];
    private bool setupRetryAvailable;
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
    public bool SetupCanResume => !Busy && (Setup.InstallStarted && SetupPlan is not null || Setup.InstallFinished && Setup.ModelsPending
        || setupRetryAvailable && setupInstallAction is not null);
    public bool SetupImportNeedsRepair => setupImportNeedsRepair;
    public bool SetupLegacyDirectRoot => Setup.Fresh && Setup.DirectComfyRoot;

    private void InitializeSetup()
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
        var restore = SetupRouting.ResolveForRuntime(Config, saved, Program.Runtime, Program.Models, Program.ModelProfiles);
        var route = restore.Step;
        Setup = saved ?? NewSetupRecord();
        // A saved receipt cannot establish that this process's runtime is healthy.
        Setup.RuntimeVerified = false;
        if (Config.InstallPending && (saved is null || !SamePath(Config.ComfyRoot, SetupTarget(saved))))
        {
            Setup = NewSetupRecord();
            Setup.Fresh = true;
            Setup.InstallStarted = true;
        }
        SetupActive = route.HasValue;
        if (route is { } step) Setup.Step = step;
        ApplySetupRuntimeRestore(restore);
        if (Config.InstallPending)
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
            SetupNotice = "上次配置的环境文件发生变化，请重新检查目录。";
        SetSetupRestoreNotice(restore);
        SynchronizeSetupVersion();
        ResetSetupStages();
    }

    private void ApplySetupRuntimeRestore(SetupRestoreDecision restore)
    {
        if (restore.RequiresSupplement)
        {
            Setup.InstallStarted = true;
            Setup.ModelsPending = true;
            Setup.RuntimeVerified = false;
        }
        else if (restore.RequiresRepairReview)
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
        if (restore.RequiresSupplement)
            SetupNotice = "新版增加了必需节点与运行资源。继续后会保留原计划并单独补齐，再检查实际运行状态。";
        else if (restore.RequiresRepairReview)
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
        if (setupWriteError is not null) throw new IOException(setupWriteError);
        SynchronizeSetupVersion();
        JsonFile.Write(setupFile, Setup);
    }

    public void BeginSetup()
    {
        if (SetupActive || Busy) return;
        InitializeSetup();
        if (!SetupActive)
        {
            Setup = NewSetupRecord();
            SetupActive = true;
        }
        SetupError = "";
        setupReview.Invalidate();
        SetupPlan = null;
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
            Setup.Fresh = fresh;
            Setup.InstallStarted = false;
            Setup.InstallFinished = false;
            Setup.ModelsPending = false;
            Setup.DirectComfyRoot = false;
            Setup.ReviewedPlan = null;
            Setup.Draft.Python = "";
            Setup.Draft.PythonExplicit = false;
            InvalidateSetupReview();
        }
        MoveSetupTo(SetupStep.Location);
    }

    public void SaveSetupDraft(LauncherConfig draft)
    {
        if (Busy) return;
        if (!Setup.InstallStarted || !SamePath(SetupTarget(Setup), Setup.Fresh && draft.InstallRoot.Length > 0 ? Path.Combine(draft.InstallRoot, "ComfyUI") : draft.ComfyRoot))
            Setup.ReviewedPlan = null;
        Setup.Draft = draft.Clone();
        Setup.InstallFinished = false;
        Setup.RuntimeVerified = false;
        Setup.SkippedOptionalModelIds = [];
        Setup.SupplementalPlan = null;
        Setup.ModelsPending = false;
        InvalidateSetupReview();
        PersistSetup();
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
        if (step is SetupStep.Welcome or SetupStep.Location) InvalidateSetupReview();
        Setup.Step = step;
        PersistSetup();
        NotifySetup();
    }

    public void SuspendSetup()
    {
        if (Busy) return;
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
        draft.ComfyPort = Config.ComfyPort;
        draft.WebPort = Config.WebPort;
        draft.Theme = Config.Theme;
        draft.StartOnLaunch = Config.StartOnLaunch;
        draft.AutoCheckUpdates = Config.AutoCheckUpdates;
        draft.PinnedAppRoot = Config.PinnedAppRoot;
        if (Setup.Fresh)
        {
            if (SetupLegacyDirectRoot)
            {
                draft.ComfyRoot = Path.GetFullPath(draft.ComfyRoot.Trim().Trim('"'));
                draft.RuntimeRoot = InstallationPlanner.ResolveRuntimeRoot(draft, Program.StateDir);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(draft.InstallRoot))
                    throw new InvalidDataException("请选择安装位置。");
                draft.InstallRoot = Path.GetFullPath(draft.InstallRoot.Trim().Trim('"'));
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
            draft.Python = Inventory.FindPython(draft.ComfyRoot, draft.PythonExplicit ? draft.Python : "");
            draft.InstallRoot = "";
            draft.RuntimeRoot = SamePath(Config.ComfyRoot, draft.ComfyRoot) && Config.RuntimeRoot.Length > 0
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
            var plan = await Task.Run(() => InstallationPlanner.Create(draft, Program.Runtime, Program.StateDir,
                fresh, Program.Models, Program.ModelProfiles, Setup.OptionalModelIds), ct);
            plan = await InstallationPlanner.ResolveStableAsync(plan, Program.Runtime, Program.StateDir, ct);
            plan = await InstallationPlanner.PreparePlanAsync(plan, Program.Models, Program.ModelProfiles,
                Path.Combine(Program.Assets, "probe_comfy.py"), ct);
            var preflight = await InstallationPlanner.PreflightAsync(plan, ct);
            if (!fresh && preflight.CanInstall)
            {
                try
                {
                    // PreparePlanAsync already scanned the models and may have deferred
                    // extra paths until PyYAML is installed. Do not repeat a strict probe.
                    UpdateSetupMissingModels(plan.ModelInventory.ToList());
                    var import = await SetupImportReview.InspectAsync(plan.CreateConfig(), plan.ResolvedRuntime ?? Program.Runtime, ct, Program.StateDir);
                    setupImportNeedsRepair = import.NeedsRepair;
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
        }, applyDrafts: false, setupOperation: true);
    }

    public async Task InstallSetupAsync()
    {
        if (!SetupCanInstall || SetupPlan is null) return;
        var reviewed = SetupPlan;
        Setup.Step = SetupStep.Installing;
        Setup.InstallStarted = true;
        Setup.InstallFinished = false;
        Setup.ModelsPending = false;
        Setup.RuntimeVerified = false;
        Setup.SkippedOptionalModelIds = [];
        Setup.SupplementalPlan = null;
        SetupError = "";
        SetupNotice = "";
        setupRetryAvailable = false;
        ResetSetupStages();
        PersistSetup();
        setupReview.Invalidate();
        var owner = DownloadTasks.RegisterOwner("setup", "安装环境与所选模型", reviewed.CreateConfig(),
            payload: System.Text.Json.JsonSerializer.SerializeToElement(reviewed, JsonFile.Options));
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
        Config = WithSavedDownloadSettings(reviewed.CreateConfig());
        FreshInstallRequested = reviewed.Fresh;
        Setup.Step = SetupStep.Installing;
        Setup.InstallStarted = true;
        PersistSetup();
        if (!Setup.InstallFinished)
        {
            // Recheck this frozen owner's interpreter, including after restart/resume.
            // The last UI review flag may belong to a different environment or be stale.
            var import = reviewed.Fresh ? null
                : await SetupImportReview.InspectAsync(Config, reviewed.ResolvedRuntime ?? Program.Runtime, ct, Program.StateDir);
            if (import is { NeedsRepair: false })
            {
                UpdateSetupProgress(new("verify", "检查已有环境", SetupStageStatus.Running));
                Config.ComfyRoot = Inventory.NormalizeRoot(Config.ComfyRoot);
                Config.Python = Inventory.FindPython(Config.ComfyRoot, Config.Python);
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
            Config.Python = Inventory.FindPython(Config.ComfyRoot, Setup.Draft.Python);
            Config.InstallPending = false;
            Config.ManagedComfy = Setup.Draft.ManagedComfy;
            FreshInstallRequested = false;
        }
        var needsSupplement = SetupRouting.NeedsSupplement(reviewed, Program.Runtime, Program.Models, Program.ModelProfiles);
        await RunSetupSelectedModelsAsync(reviewed.ModelIds.ToArray(), ct, finalize: !needsSupplement);
        if (needsSupplement) await SupplementReviewedSetupAsync(reviewed, ct);
    }

    private async Task SupplementReviewedSetupAsync(InstallationPlan original, CancellationToken ct)
    {
        SetStatus("原安装计划已完成，正在准备新版必需节点与 SeedVR2 的独立补齐计划…");
        var supplement = SetupActive ? Setup.SupplementalPlan : null;
        if (supplement is null || !SamePath(supplement.ComfyRoot, Config.ComfyRoot))
        {
            // A healthy imported environment may have skipped Installer.Install;
            // close its old node snapshot before resolving a distinct supplement.
            InstallationPlanner.MarkStableInstallComplete(Config, Program.StateDir);
            // Keep the old owner's model versions; only additions come from the new catalog.
            var models = OperationModels;
            var profiles = OperationProfiles;
            supplement = await Task.Run(() => InstallationPlanner.Create(Config, Program.Runtime, Program.StateDir,
                false, models, profiles, original.ModelIds.Except(Setup.SkippedOptionalModelIds)), ct);
            supplement = await InstallationPlanner.ResolveStableAsync(supplement, Program.Runtime, Program.StateDir, ct);
            supplement = await InstallationPlanner.PreparePlanAsync(supplement, models, profiles,
                Path.Combine(Program.Assets, "probe_comfy.py"), ct);
            if (SetupActive)
            {
                Setup.SupplementalPlan = supplement;
                PersistSetup();
            }
        }
        var owner = DownloadTasks.RegisterOwner(SetupActive ? "setup" : "install", "补齐新版必需节点与模型", supplement.CreateConfig(),
            payload: System.Text.Json.JsonSerializer.SerializeToElement(supplement, JsonFile.Options));
        if (!SetupActive)
        {
            var previousInstallOwner = activeDownloadOwner;
            activeDownloadOwner = owner.Id;
            try { await DownloadTasks.RunOwnerAsync(owner.Id, token => InstallEnvironmentAsync(token, supplement), ct); }
            finally { activeDownloadOwner = previousInstallOwner; }
            return;
        }
        SetupPlan = supplement;
        Setup.ReviewedPlan = supplement;
        Setup.InstallFinished = false;
        Setup.ModelsPending = true;
        Setup.RuntimeVerified = false;
        PersistSetup();
        var previousOwner = activeDownloadOwner;
        activeDownloadOwner = owner.Id;
        try { await DownloadTasks.RunOwnerAsync(owner.Id, token => ExecuteReviewedSetupAsync(supplement, token), ct); }
        finally { activeDownloadOwner = previousOwner; }
    }

    public async Task ResumeSetupAsync()
    {
        if (Busy) return;
        SetupError = "";
        SetupNotice = "";
        if (SetupPlan is { } plan && Setup.InstallStarted)
        {
            var owner = DownloadTasks.RegisterOwner("setup", "安装环境与所选模型", plan.CreateConfig(),
                payload: System.Text.Json.JsonSerializer.SerializeToElement(plan, JsonFile.Options));
            await ResumeDownloadOwnerAsync(owner.Id);
            return;
        }
        if (!SetupCanResume) { await CheckSetupAsync(); return; }
        await Run(setupInstallAction!, applyDrafts: false, setupOperation: true);
    }

    public async Task StartSetupWorkbenchAsync()
    {
        if (Busy || Setup.Step != SetupStep.Complete) return;
        var restore = SetupRouting.ResolveForRuntime(Config, Setup, Program.Runtime, Program.Models, Program.ModelProfiles);
        if (restore.RequiresSupplement || restore.RequiresRepairReview)
        {
            ApplySetupRuntimeRestore(restore);
            SetSetupRestoreNotice(restore);
            PersistSetup();
            NotifySetup();
            if (restore.RequiresSupplement) await ResumeSetupAsync();
            else await CheckSetupAsync();
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
            await VerifySetupRuntimeAsync(SetupModelSelection.ResolveIds(OperationModels, OperationProfiles,
                Config.SelectedProfile, Setup.OptionalModelIds.Except(Setup.SkippedOptionalModelIds)).ToArray(), ct);
            if (SetupOptionalFailures.Count > 0)
            {
                Setup.Step = SetupStep.Installing;
                Setup.ModelsPending = true;
                PersistSetup();
                throw new InvalidOperationException("部分所选可选模型未通过运行检查，请重试或明确跳过可选项。\n" + string.Join("\n", SetupOptionalFailures));
            }
            await StartServicesAsync(ct);
            // StartServicesAsync only returns without startup when installation is pending.
            if (Config.InstallPending || FreshInstallRequested)
                throw new InvalidOperationException("环境仍有未完成的安装步骤，请重新检查。");
            CompleteSetupRecord();
            GoTo(0);
        }, applyDrafts: false, setupOperation: true);
    }

    public void FinishSetup(bool openModels = false)
    {
        if (Busy || Setup.Step != SetupStep.Complete) return;
        if (!Setup.RuntimeVerified)
        {
            SetupError = "请先完成运行检查并启动工作台，再结束安装向导。";
            NotifySetup();
            return;
        }
        CompleteSetupRecord();
        GoTo(openModels ? 2 : 0);
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
        var stages = Setup.Fresh
            ? new[] { ("tools", "准备安装工具"), ("comfy", "准备 ComfyUI"), ("python", "准备 Python"),
                ("torch", "安装 GPU 运行环境"), ("dependencies", "安装环境依赖"), ("nodes", "准备必要节点"),
                ("validation", "验证运行依赖"), ("models", "下载所选模型"), ("verify", "最终检查") }
            : new[] { ("tools", "准备安装工具"), ("nodes", "检查与补齐节点"), ("validation", "验证运行依赖"), ("models", "下载所选模型"), ("verify", "检查现有环境") };
        foreach (var (id, label) in stages)
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
        SetupStatusText = paused ? "安装已暂停，已完成的内容会保留。" : "安装尚未完成：" + message;
        if (!Setup.RuntimeVerified || SetupOptionalFailures.Count == 0)
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
        var selected = SetupModelSelection.MandatoryModels(OperationModels, OperationProfiles, SetupActive ? Setup.Draft.SelectedProfile : Config.SelectedProfile)
            .Select(m => m.Id).ToHashSet();
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
        await ReviewSetupModelsAsync(showPreview: false);
    }
}
