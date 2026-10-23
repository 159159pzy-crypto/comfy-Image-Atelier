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
    public bool SetupCanResume => !Busy && (Setup.InstallFinished && Setup.ModelsPending
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
        var route = SetupRouting.Resolve(Config, saved);
        Setup = saved ?? NewSetupRecord();
        if (Config.InstallPending && (saved is null || !SamePath(Config.ComfyRoot, SetupTarget(saved))))
        {
            Setup = NewSetupRecord();
            Setup.Fresh = true;
            Setup.InstallStarted = true;
        }
        SetupActive = route.HasValue;
        if (route is { } step) Setup.Step = step;
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
                : "发现上次未完成的安装。重新检查后继续，已完成的文件和步骤会自动复用。";
        if (route == SetupStep.Location && saved?.InstallFinished == true)
            SetupNotice = "上次配置的环境文件发生变化，请重新检查目录。";
        SynchronizeSetupVersion();
        ResetSetupStages();
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

    public void NotifySetup() => SetupChanged?.Invoke();

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
            Setup.Draft.Python = "";
            Setup.Draft.PythonExplicit = false;
            InvalidateSetupReview();
        }
        MoveSetupTo(SetupStep.Location);
    }

    public void SaveSetupDraft(LauncherConfig draft)
    {
        if (Busy) return;
        Setup.Draft = draft.Clone();
        Setup.InstallFinished = false;
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
            plan = await InstallationPlanner.PreparePlanAsync(plan, Program.Models, Program.ModelProfiles,
                Path.Combine(Program.Assets, "probe_comfy.py"), ct);
            var preflight = await InstallationPlanner.PreflightAsync(plan, ct);
            if (!fresh && preflight.CanInstall)
            {
                try
                {
                    var probe = await Inventory.Probe(draft.ComfyRoot, draft.Python, Path.Combine(Program.Assets, "probe_comfy.py"), ct);
                    UpdateSetupMissingModels(await Inventory.Scan(probe, Program.Models, false, ct));
                    var nodes = await Inventory.Nodes(draft.ComfyRoot, Program.Runtime, Commands.Find("git.exe"), ct);
                    setupImportNeedsRepair = nodes.Any(node => node.State == "缺失");
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
            SetupPreflight = preflight;
            Setup.Draft = plan.CreateConfig();
            SetupNotice = fresh ? "已存在且通过校验的文件会复用，Python 依赖的实际下载量可能增加。"
                : setupImportNeedsRepair ? "将补齐缺失节点，保留已有节点版本和 GPU 运行环境。" : "现有节点齐全，可直接完成导入。";
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
        SetupError = "";
        SetupNotice = "";
        setupRetryAvailable = false;
        ResetSetupStages();
        PersistSetup();
        setupReview.Invalidate();
        setupInstallAction = async ct =>
        {
            Config = reviewed.CreateConfig();
            FreshInstallRequested = reviewed.Fresh;
            if (!reviewed.Fresh && !setupImportNeedsRepair)
            {
                UpdateSetupProgress(new("verify", "检查已有环境", SetupStageStatus.Running));
                Config.ComfyRoot = Inventory.NormalizeRoot(Config.ComfyRoot);
                Config.Python = Inventory.FindPython(Config.ComfyRoot, Config.Python);
                FreshInstallRequested = false;
                Config.InstallPending = false;
                Save();
                UpdateSetupProgress(new("verify", "已有环境已接入", SetupStageStatus.Complete));
            }
            else await InstallEnvironmentAsync(ct, reviewed, prepareModels: false);
            for (var i = 0; i < setupStages.Count; i++)
                if (setupStages[i].Status == SetupStageStatus.Pending)
                    setupStages[i] = setupStages[i] with { Status = SetupStageStatus.Complete, Detail = setupStages[i].Detail + " · 已复用" };
            Setup.Draft = Config.Clone();
            Setup.InstallFinished = true;
            Setup.ModelsPending = true;
            PersistSetup();
            await RunSetupSelectedModelsAsync(reviewed.ModelIds.ToArray(), ct);
        };
        setupRetryAvailable = true;
        await Run(setupInstallAction, applyDrafts: false, setupOperation: true);
    }

    public async Task ResumeSetupAsync()
    {
        if (Busy) return;
        if (!SetupCanResume) { await CheckSetupAsync(); return; }
        SetupError = "";
        SetupNotice = "";
        if (Setup.InstallFinished && Setup.ModelsPending)
        {
            var ids = SetupModelSelection.ResolveIds(Program.Models, Program.ModelProfiles,
                Config.SelectedProfile, Setup.OptionalModelIds).ToArray();
            await Run(ct => RunSetupSelectedModelsAsync(ids, ct), applyDrafts: false, setupOperation: true);
            return;
        }
        await Run(setupInstallAction!, applyDrafts: false, setupOperation: true);
    }

    public async Task StartSetupWorkbenchAsync()
    {
        if (Busy || Setup.Step != SetupStep.Complete) return;
        SetupError = "";
        setupRetryAvailable = false;
        setupModelResumeAvailable = false;
        setupModelResumePlan = null;
        setupModelPreviewPlan = null;
        SetupCompletionOperation = SetupCompletionOperation.StartWorkbench;
        await Run(async ct =>
        {
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
            var complete = Setup.InstallFinished && id != "models";
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
        for (var i = 0; i < setupStages.Count; i++)
            if (setupStages[i].Status == SetupStageStatus.Running)
                setupStages[i] = setupStages[i] with { Status = paused ? SetupStageStatus.Paused : SetupStageStatus.Failed };
        NotifySetup();
    }

    private void UpdateSetupMissingModels(List<ModelStatus> scan)
    {
        var selected = Program.ModelProfiles.RequiredModels(Program.Models, SetupActive ? Setup.Draft.SelectedProfile : Config.SelectedProfile)
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
