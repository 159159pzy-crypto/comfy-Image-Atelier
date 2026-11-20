using Anima.Launcher.Core;

namespace Anima.Launcher;

internal sealed partial class LauncherState
{
    private readonly HashSet<OwnedProcess> setupStartedServices = [];
    public bool SetupServicesReached { get; private set; }
    public bool SetupCanStopStarted => setupStartedServices.Overlaps(Services.RunningOwned());
    public async Task StopIdleSetupServicesAsync()
    {
        if (Busy || !SetupCanStopStarted || Config.ReadOnlyConnection) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        var comfy = await ServiceManager.PortOpen(Config.ComfyPort, ct);
        var studio = await ServiceManager.PortOpen(Config.WebPort, ct);
        var queue = comfy ? await ServiceManager.Json(Config.ComfyUrl + "/queue", ct) : null;
        var batch = studio ? await ServiceManager.Json(Config.WebUrl + "/api/batches/current", ct) : null;
        var issue = FeaturePreparation.QueueIssue(comfy, queue, studio, batch);
        if (issue.Length > 0) throw new InvalidOperationException(issue);
        StopSetupStartedServices();
    }
    public void StopSetupStartedServices()
    {
        if (Busy) return;
        Services.StopStartedServices(setupStartedServices);
        setupStartedServices.Clear();
        SetupServicesReached = false;
        Setup.MarkRuntimeCheckStale();
        SetupNotice = "本次启动的服务已停止。";
        PersistSetup();
        NotifySetup();
    }
    private async Task VerifySetupRuntimeAsync(string[] selectedIds, CancellationToken ct)
    {
        RequireComfyRecoveryComplete();
        Setup.MarkRuntimeCheckStale();
        SetupServicesReached = false;
        SetupRequiredModelsReady = false;
        UpdateSetupProgress(new("verify", "正在启动服务并检查节点、分类、词库和模型", SetupStageStatus.Running));
        SetStatus("正在检查实际运行环境，尚未完成安装…");
        var plan = activeInstallationPlan ?? Setup.ReviewedPlan;
        var mandatory = SetupRequiredDownloads;
        var files = await ResolveModelIdsAsync(mandatory.Select(model => model.Id).ToArray(), true, ct, reuseVerified: true);
        if (files.Count != mandatory.Length || files.Any(item => item.Missing))
            throw new InvalidOperationException("必装模型尚未全部通过校验，请继续准备模型。");
        foreach (var item in files.Where(item => item.Model.Category == "diffusion_models"))
            await ModelMetadata.EnsureDefaultAsync(Config, OperationRuntime, OperationModels, OperationProfiles,
                item.FoundPath ?? item.Destination, ct, AppendLog);
        var previouslyOwned = Services.RunningOwned();
        await WithPortRecoveryAsync(async () =>
        {
            await EnsureCurrentVersionAsync(ct);
            await Services.StartWebUI(Config, Program.Runtime, ct);
        }, ct);
        setupStartedServices.UnionWith(Services.RunningOwned().Except(previouslyOwned));
        SetupServicesReached = true;
        using var readiness = new SetupRuntimeReadiness();
        var features = plan is { ResourcePolicyVersion: > 0 }
            ? SetupFeatures.Resolve(plan.Fresh, selectedIds.Concat(plan.Features
                .Where(feature => feature is "sesqui" or "nvidia" or "dlss" or "prompt-assistant")
                .Select(feature => SetupFeatures.Prefix + feature)).Except(Setup.SkippedOptionalModelIds), Config.SelectedProfile)
            : null;
        var report = await readiness.CheckAsync(Config, Program.Runtime, OperationModels, OperationProfiles, selectedIds, ct, AppendLog,
            InstallationPlanner.ResolveRuntimeRoot(Config, Program.StateDir), features,
            plan is { Fresh: true, ResourcePolicyVersion: > 0 }
                ? (plan.SelectedProfile == "anima" ? ["base", "sesqui"] : ["base"]) : []);
        Setup.AssistantAvailable = report.PromptAssistantAvailable;
        if (!report.PromptAssistantAvailable)
        {
            var nodes = Path.Combine(Config.ComfyRoot, "custom_nodes");
            try
            {
                Setup.AssistantDetail = PromptAssistantCompatibility.ResolveDirectory(Config.ComfyRoot) is not null
                    || Directory.Exists(Path.Combine(nodes, "prompt-assistant"))
                    ? "已安装的提示词助手没有加载重写节点。请检查 ComfyUI 日志。"
                    : $"未检测到提示词助手。插件目录为 {nodes}。";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { Setup.AssistantDetail = "提示词助手目录需要检查：" + ex.Message; }
        }
        if (report.RequiredIssues.Length > 0)
            throw new InvalidOperationException("运行检查未通过：\n" + string.Join("\n", report.RequiredIssues.Select(issue => issue.Message)));
        foreach (var issue in report.OptionalIssues)
        {
            if (!SetupOptionalFailures.Contains(issue.Message)) SetupOptionalFailures.Add(issue.Message);
            if (!string.IsNullOrWhiteSpace(issue.ModelId)) setupFailedOptionalModelIds.Add(issue.ModelId);
        }
        if (report.ConfigurationIssues.Length > 0)
        {
            SetupNotice = "环境已就绪，部分生成资源待配置。可进入工作台选择兼容模型。";
            foreach (var issue in report.ConfigurationIssues) AppendLog(issue.Message);
        }
        await CheckSavedAssistantAsync(ct);
        if (stagedRepair is not null) stagedRepair.Publish(configFile, WithSavedDownloadSettings(Config), report, ct);
        Setup.RecordRuntimeCheck(report);
        SetupRequiredModelsReady = true;
        if (SetupActive) PersistSetup();
        UpdateSetupProgress(new("verify", "环境已就绪，可进入工作台配置与生成", SetupStageStatus.Complete));
    }
}
