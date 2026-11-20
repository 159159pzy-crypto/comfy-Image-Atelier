using Anima.Launcher.Core;

namespace Anima.Launcher;

internal sealed partial class LauncherState
{
    public List<string> SetupOptionalFailures { get; } = [];
    private readonly HashSet<string> setupFailedOptionalModelIds = new(StringComparer.Ordinal);
    private readonly List<SetupRuntimeIssue> setupEnvironmentFailures = [];
    public bool SetupRequiredModelsReady { get; private set; }
    public bool SetupCanSkipOptional => !Busy && Setup.InstallFinished && Setup.ModelsPending
        && SetupRequiredModelsReady && Setup.RuntimeVerified && SetupOptionalFailures.Count > 0;
    public bool SetupAssistantReady { get; private set; }

    public void ClearSetupFeedback() { SetupError = ""; SetupNotice = ""; NotifySetup(); }

    public Task SelectSetupModelsAsync(SetupModelChoice choice, bool selected)
    {
        if (Busy || Setup.Step != SetupStep.Review) return Task.CompletedTask;
        var choices = SetupModelSelection.Choices(Program.Models, Program.ModelProfiles, Setup.Draft.SelectedProfile, includeBase: !Setup.Fresh);
        Setup.OptionalModelIds = SetupModelSelection.SetChoiceSelected(choices, Setup.OptionalModelIds ?? [], choice.Id, selected);
        InvalidateSetupReview();
        Setup.SelectionNeedsReview = true;
        PersistSetup();
        NotifySetup();
        return Task.CompletedTask;
    }

    private async Task RunSetupSelectedModelsAsync(string[] modelIds, CancellationToken ct)
    {
        SetupOptionalFailures.Clear();
        setupFailedOptionalModelIds.Clear();
        foreach (var issue in setupEnvironmentFailures)
        {
            SetupOptionalFailures.Add(issue.Message);
            setupFailedOptionalModelIds.Add(issue.ModelId);
        }
        SetupRequiredModelsReady = false;
        Setup.RuntimeVerified = false;
        SetupError = "";
        SetupNotice = "";
        ClearBatchProgress();
        UpdateSetupProgress(new("models", "确认所选模型并补齐缺失文件", SetupStageStatus.Running));
        SetStatus("正在校验所选模型，已有文件会复用；大模型校验需要一些时间…");
        var required = SetupModelSelection.RequiredForPlan(activeInstallationPlan, OperationModels, OperationProfiles, Config.SelectedProfile)
            .Select(m => m.Id).ToHashSet();
        var optional = modelIds.Where(id => !SetupFeatures.IsCapability(id) && !required.Contains(id) && !Setup.SkippedOptionalModelIds.Contains(id)).ToHashSet();
        var scan = await ResolveModelIdsAsync(required.ToArray(), true, ct);
        // Resolve optional resources separately: an unavailable optional runtime must
        // not prevent required generation models from downloading.
        foreach (var id in optional)
        {
            try { scan.AddRange(await ResolveModelIdsAsync([id], true, ct)); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var name = Program.Models.Models.FirstOrDefault(model => model.Id == id)?.Name ?? id;
                SetupOptionalFailures.Add(name + "：" + ex.Message);
                setupFailedOptionalModelIds.Add(id);
            }
        }
        var missing = scan.Where(item => item.Missing).ToList();
        await DownloadModelsAsync(missing, ct, optional);
        foreach (var item in scan.Where(item => !item.Missing && item.Model.Category == "diffusion_models"))
            await ModelMetadata.EnsureDefaultAsync(Config, OperationRuntime, OperationModels, OperationProfiles,
                item.FoundPath!, ct, AppendLog);
        var verified = await ResolveModelIdsAsync(required.ToArray(), true, ct);
        if (verified.Count != required.Count || verified.Any(item => item.Missing))
            throw new InvalidOperationException("基础模型尚未齐备，请处理模型检查结果后重试。");
        await ScanSetupModelsAsync(ct);
        await VerifySetupRuntimeAsync(required.Concat(optional).ToArray(), ct);
        SetupRequiredModelsReady = true;
        if (SetupOptionalFailures.Count > 0)
        {
            UpdateSetupProgress(new("models", "基础模型已就绪，部分可选功能或模型未完成", SetupStageStatus.Failed));
            throw new InvalidOperationException("环境已安装，以下可选功能或模型未完成。可以重试失败项，或跳过剩余可选项。\n" + string.Join("\n", SetupOptionalFailures));
        }
        UpdateSetupProgress(new("models", "所选模型已校验", SetupStageStatus.Complete));
        SetStatus("安装完成。必需模型、节点、模型分类和词库检查已通过。");
        AdvanceAfterSetupModels();
    }

    private void AdvanceAfterSetupModels()
    {
        Setup.ModelsPending = false;
        Setup.OptionalReviewAtCompletion = false;
        Setup.Draft = Config.Clone();
        Setup.Step = SetupRouting.AfterModels(Setup);
        SetupAssistantReady = false;
        setupRetryAvailable = false;
        SetupError = "";
        SetupNotice = "";
        PersistSetup();
        NotifySetup();
    }

    public void SkipSetupOptionalModels()
    {
        if (!SetupCanSkipOptional) return;
        Setup.SkippedOptionalModelIds = SetupModelSelection.ExpandSkippedOptionalIds(OperationModels, OperationProfiles,
            Config.SelectedProfile, Setup.SkippedOptionalModelIds.Concat(setupFailedOptionalModelIds),
            allowBase: (activeInstallationPlan ?? Setup.ReviewedPlan) is { ResourcePolicyVersion: > 0, Fresh: false },
            selectedModelIds: Setup.OptionalModelIds);
        Setup.OptionalModelIds = Setup.OptionalModelIds.Except(Setup.SkippedOptionalModelIds).ToArray();
        Setup.FailedFeatureIds = [];
        setupEnvironmentFailures.Clear();
        var completing = Setup.Step == SetupStep.Complete;
        AdvanceAfterSetupModels();
        if (completing) { Setup.Step = SetupStep.Complete; PersistSetup(); }
        SetupNotice = "已跳过未完成的可选功能，可稍后在模型资源页一键准备完整功能。";
        NotifySetup();
    }

    public async Task StartSetupAssistantAsync()
    {
        if (Busy || Config.ReadOnlyConnection || Setup.Step != SetupStep.Assistant) return;
        SetupError = "";
        SetupAssistantReady = false;
        await Run(async ct =>
        {
            await StartComfyAsync(ct);
            var nodes = await ServiceManager.Json(Config.ComfyUrl + "/object_info", ct)
                ?? throw new IOException("无法读取提示词助手安装状态，请确认 ComfyUI 已启动。");
            Setup.AssistantAvailable = nodes.TryGetProperty("PromptExpand", out _);
            if (Setup.AssistantAvailable == false)
            {
                AdvanceAfterSetupModels();
                SetupNotice = "提示词助手尚未安装或未成功加载，已跳过配置。可在模型资源页准备提示词助手功能。";
                NotifySetup();
                return;
            }
            SetupAssistantReady = true;
            PersistSetup();
        }, applyDrafts: false, setupOperation: true);
    }

    public async Task InspectReadOnlyAssistantAsync()
    {
        if (Busy || !Config.ReadOnlyConnection || Setup.Step != SetupStep.Assistant) return;
        await Run(async ct =>
        {
            var health = await ServiceManager.Json(Config.WebUrl + "/api/launcher-health", ct);
            if (!ServiceManager.IsOurWebUi(health, Config)) throw new IOException("此环境的工作台尚未就绪。");
            var options = await ServiceManager.Json(Config.WebUrl + "/api/prompt-assistant/options?kind=rewrite", ct);
            if (options is not { } result || !result.TryGetProperty("available", out var available))
                throw new IOException("工作台未返回提示词助手状态。");
            SetupNotice = available.ValueKind == System.Text.Json.JsonValueKind.True ? "已有提示词助手可在工作台重写提示词。"
                : result.TryGetProperty("reason", out var reason) ? reason.GetString() ?? "当前助手尚不可用。" : "当前助手尚不可用。";
            NotifySetup();
        }, applyDrafts: false, setupOperation: true);
    }

    public void ReviewAssistantRepair()
    {
        if (Busy || !Config.ReadOnlyConnection || Setup.Step is not (SetupStep.Assistant or SetupStep.Complete)) return;
        var draft = Config.Clone();
        draft.ExternalEnvironmentMode = "repair";
        Setup.Fresh = false;
        Setup.InstallStarted = false;
        Setup.InstallFinished = false;
        Setup.ModelsPending = false;
        Setup.AssistantStatus = AssistantSetupStatus.Pending;
        Setup.OptionalModelIds = [SetupFeatures.Prefix + "prompt-assistant"];
        Setup.SkippedOptionalModelIds = [];
        SaveSetupDraft(draft);
        MoveSetupTo(SetupStep.Location);
        SetupNotice = "已在草稿中选择允许补齐和提示词助手。请核对解释器，在下一步查看改动并确认。原实例需由其启动程序停止。";
        NotifySetup();
    }

    public async Task FinishSetupAssistantAsync(bool configured)
    {
        if (Busy || Setup.Step != SetupStep.Assistant || configured && Config.ReadOnlyConnection) return;
        await Run(async ct =>
        {
            Setup.AssistantStatus = configured ? AssistantSetupStatus.Saved : AssistantSetupStatus.Skipped;
            Setup.AssistantDetail = "";
            if (configured) await CheckSavedAssistantAsync(ct);
            Setup.Step = SetupStep.Complete;
            SetupError = "";
            PersistSetup();
            NotifySetup();
        }, applyDrafts: false, setupOperation: true);
        await RefreshSetupCompletionAsync();
    }

    private async Task CheckSavedAssistantAsync(CancellationToken ct)
    {
        if (Setup.AssistantStatus is not (AssistantSetupStatus.Saved or AssistantSetupStatus.Configured)) return;
        Setup.AssistantStatus = AssistantSetupStatus.Saved;
        try
        {
            var health = await ServiceManager.Json(Config.WebUrl + "/api/launcher-health", ct);
            if (!ServiceManager.IsOurWebUi(health, Config)) return;
            var options = await ServiceManager.Json(Config.WebUrl + "/api/prompt-assistant/options?kind=rewrite", ct);
            if (options is { } result && result.TryGetProperty("available", out var available)
                && available.ValueKind == System.Text.Json.JsonValueKind.True)
            {
                Setup.AssistantStatus = AssistantSetupStatus.Configured;
                Setup.AssistantDetail = "";
            }
            else if (options is { } unavailable && unavailable.TryGetProperty("reason", out var reason))
                Setup.AssistantDetail = reason.GetString() ?? "";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TimeoutException or System.Text.Json.JsonException)
        { Setup.AssistantDetail = "工作台就绪后将重新检查。"; }
    }
}
