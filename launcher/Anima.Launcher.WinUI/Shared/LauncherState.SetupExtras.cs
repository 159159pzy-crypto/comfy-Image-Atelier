using Anima.Launcher.Core;

namespace Anima.Launcher;

internal sealed partial class LauncherState
{
    public List<string> SetupOptionalFailures { get; } = [];
    public bool SetupRequiredModelsReady { get; private set; }
    public bool SetupCanSkipOptional => !Busy && Setup.InstallFinished && Setup.ModelsPending && SetupRequiredModelsReady;
    public bool SetupAssistantReady { get; private set; }

    public async Task SelectSetupModelsAsync(SetupModelChoice choice, bool selected)
    {
        if (Busy || Setup.Step != SetupStep.Review) return;
        var ids = (Setup.OptionalModelIds ?? []).ToHashSet(StringComparer.Ordinal);
        if (selected) ids.UnionWith(choice.ModelIds); else ids.ExceptWith(choice.ModelIds);
        Setup.OptionalModelIds = ids.Order(StringComparer.Ordinal).ToArray();
        InvalidateSetupReview();
        PersistSetup();
        await CheckSetupAsync();
    }

    private async Task RunSetupSelectedModelsAsync(string[] modelIds, CancellationToken ct)
    {
        SetupOptionalFailures.Clear();
        SetupRequiredModelsReady = false;
        SetupError = "";
        SetupNotice = "";
        ClearBatchProgress();
        UpdateSetupProgress(new("models", "确认所选模型并补齐缺失文件", SetupStageStatus.Running));
        var required = Program.ModelProfiles.RequiredModels(Program.Models, Config.SelectedProfile).Select(m => m.Id).ToHashSet();
        var optional = modelIds.Where(id => !required.Contains(id)).ToHashSet();
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
            }
        }
        var missing = scan.Where(item => item.Missing).ToList();
        SetupRequiredModelsReady = scan.Where(item => required.Contains(item.Model.Id)).All(item => !item.Missing);
        await DownloadModelsAsync(missing, ct, optional);
        foreach (var item in scan.Where(item => !item.Missing && item.Model.Category == "diffusion_models"))
            await ModelMetadata.EnsureDefaultAsync(Config, Program.Runtime, Program.Models, Program.ModelProfiles,
                item.FoundPath!, ct, AppendLog);
        await ScanSetupModelsAsync(ct);
        SetupRequiredModelsReady = SetupModelsReadiness == SetupModelReadiness.Ready;
        if (!SetupRequiredModelsReady)
            throw new InvalidOperationException("基础模型尚未齐备，请处理模型检查结果后重试。");
        if (SetupOptionalFailures.Count > 0)
        {
            UpdateSetupProgress(new("models", "基础模型已就绪，部分可选模型未完成", SetupStageStatus.Failed));
            throw new InvalidOperationException("环境已安装，以下可选模型未完成。可以重试失败项，或跳过剩余可选项。\n" + string.Join("\n", SetupOptionalFailures));
        }
        UpdateSetupProgress(new("models", "所选模型已校验", SetupStageStatus.Complete));
        AdvanceAfterSetupModels();
    }

    private void AdvanceAfterSetupModels()
    {
        Setup.ModelsPending = false;
        Setup.Draft = Config.Clone();
        Setup.Step = Setup.AssistantStatus == AssistantSetupStatus.Pending ? SetupStep.Assistant : SetupStep.Complete;
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
        AdvanceAfterSetupModels();
        SetupNotice = "已跳过未完成的可选模型，可稍后在模型资源页继续下载。";
        NotifySetup();
    }

    public async Task StartSetupAssistantAsync()
    {
        if (Busy || Setup.Step != SetupStep.Assistant) return;
        SetupError = "";
        SetupAssistantReady = false;
        await Run(async ct =>
        {
            await StartComfyAsync(ct);
            SetupAssistantReady = true;
        }, applyDrafts: false, setupOperation: true);
    }

    public async Task FinishSetupAssistantAsync(bool configured)
    {
        if (Busy || Setup.Step != SetupStep.Assistant) return;
        Setup.AssistantStatus = configured ? AssistantSetupStatus.Configured : AssistantSetupStatus.Skipped;
        Setup.Step = SetupStep.Complete;
        SetupError = "";
        PersistSetup();
        NotifySetup();
        await RefreshSetupCompletionAsync();
    }
}
