using Anima.Launcher.Core;

namespace Anima.Launcher;

internal sealed partial class LauncherState
{
    private SetupModelDownloadPlan? setupModelPreviewPlan;
    private SetupModelDownloadPlan? setupModelResumePlan;
    private bool setupModelResumeAvailable;

    public SetupModelReadiness SetupModelsReadiness { get; private set; } = SetupModelReadiness.Unknown;
    public SetupModelReview? SetupModelReview { get; private set; }
    public bool SetupModelReviewVisible { get; private set; }
    public SetupCompletionOperation SetupCompletionOperation { get; private set; }
    public bool SetupCanDownloadModels => CanPrepareSetupModels && !Busy && SetupModelReviewVisible
        && setupModelPreviewPlan is not null && setupModelPreviewPlan.Matches(Config);
    public bool SetupCanResumeModels => CanPrepareSetupModels && !Busy && setupModelResumeAvailable
        && setupModelResumePlan is not null && setupModelResumePlan.Matches(Config);

    private bool CanPrepareSetupModels => SetupActive && Setup.Step == SetupStep.Complete && Setup.InstallFinished
        && !Config.InstallPending && !FreshInstallRequested;

    private void ResetSetupModels()
    {
        SetupModelsReadiness = SetupModelReadiness.Unknown;
        SetupModelReview = null;
        SetupModelReviewVisible = false;
        setupModelPreviewPlan = null;
        setupModelResumePlan = null;
        setupModelResumeAvailable = false;
        SetupCompletionOperation = SetupCompletionOperation.None;
    }

    private SetupModelReview BuildSetupModelReview(List<ModelStatus> scan) => Core.SetupModelReview.Create(
        Program.ModelProfiles.RequiredModels(Program.Models, Config.SelectedProfile), scan,
        status => status.Model.IsPackage
            ? ModelPackages.RemainingBytes(status.Model, status.Destination)
            : Downloads.RemainingBytes(status.Destination, status.Model.Size, status.Model.Sha256),
        status =>
        {
            var urls = (status.Model.IsPackage ? status.Model.Files.SelectMany(file => file.Urls) : status.Model.Urls)
                .Select(url => Safety.MapSource(url, Config)).Distinct().ToArray();
            return urls.Length > 0 ? string.Join("\n", urls) : status.Model.SourceUrl;
        });

    private async Task ScanSetupModelsAsync(CancellationToken ct)
    {
        SetupNotice = "";
        ResetTransferProgress();
        SetupModelsReadiness = SetupModelReadiness.Checking;
        SetStatus("正在验证基础模型完整性…");
        NotifySetup();
        try
        {
            var ids = Program.ModelProfiles.RequiredModels(Program.Models, Config.SelectedProfile).Select(model => model.Id).ToArray();
            // Only the required generation models are checked here. This never invokes
            // installation, repairs nodes, or applies the original installation draft.
            var scan = await ResolveModelIdsAsync(ids, true, ct);
            ct.ThrowIfCancellationRequested();
            UpdateSetupMissingModels(scan);
        }
        catch
        {
            SetupModelsReadiness = SetupModelReadiness.ScanFailed;
            SetupModelReview = null;
            SetupMissingModels = [];
            throw;
        }
        SetupNotice = SetupModelsReadiness switch
        {
            SetupModelReadiness.Ready => "基础模型检查完成，可以打开工作台。",
            SetupModelReadiness.NeedsAttention => "有模型需要手动处理；已有异常文件会保留，不会直接覆盖。",
            SetupModelReadiness.ScanFailed => "未取得全部基础模型的检查结果，请重新检查。",
            _ => "基础模型尚未齐备。先查看下载内容，也可以先打开工作台。",
        };
        SetStatus("基础模型检查完成。");
    }

    public Task ReviewSetupModelsAsync() => ReviewSetupModelsAsync(showPreview: true);

    private async Task ReviewSetupModelsAsync(bool showPreview)
    {
        if (!CanPrepareSetupModels || Busy) return;
        SetupError = "";
        SetupNotice = "";
        SetupModelReviewVisible = showPreview;
        SetupModelReview = null;
        SetupModelsReadiness = SetupModelReadiness.Checking;
        SetupCompletionOperation = SetupCompletionOperation.ReviewModels;
        setupModelPreviewPlan = null;
        setupModelResumePlan = null;
        setupModelResumeAvailable = false;
        NotifySetup();
        await Run(async ct =>
        {
            try
            {
                SetStatus("正在检查基础模型的位置和文件完整性…");
                await ScanSetupModelsAsync(ct);
                if (showPreview && SetupModelReview is { DownloadableItems.IsEmpty: false } review
                    && review.Readiness != SetupModelReadiness.ScanFailed)
                    setupModelPreviewPlan = new(Config, review);
            }
            catch
            {
                SetupModelsReadiness = SetupModelReadiness.ScanFailed;
                SetupModelReview = null;
                SetupMissingModels = [];
                throw;
            }
        }, applyDrafts: false, setupOperation: true);
        SetupCompletionOperation = SetupCompletionOperation.None;
        NotifySetup();
    }

    public async Task DownloadSetupModelsAsync()
    {
        if (!CanPrepareSetupModels || Busy) return;
        if (!SetupCanDownloadModels || setupModelPreviewPlan is null)
        {
            SetupError = "下载内容尚未确认或环境配置已变化，请先重新检查模型。";
            setupModelPreviewPlan = null;
            NotifySetup();
            return;
        }
        await RunSetupModelDownloadAsync(setupModelPreviewPlan);
    }

    public async Task ResumeSetupModelsAsync()
    {
        if (!CanPrepareSetupModels || Busy) return;
        if (!SetupCanResumeModels || setupModelResumePlan is null)
        {
            await ReviewSetupModelsAsync();
            return;
        }
        await RunSetupModelDownloadAsync(setupModelResumePlan);
    }

    private async Task RunSetupModelDownloadAsync(SetupModelDownloadPlan plan)
    {
        SetupError = "";
        SetupNotice = "";
        SetupCompletionOperation = SetupCompletionOperation.DownloadModels;
        setupModelResumePlan = plan;
        setupModelResumeAvailable = false;
        setupModelPreviewPlan = null;
        var completed = false;
        NotifySetup();
        await Run(async ct =>
        {
            await DownloadModelIdsAsync(plan.ModelIds.ToArray(), ct, refreshInventory: false);
            // A final required-model scan distinguishes safe downloads from any
            // remaining manual issues. Downloading never finishes onboarding.
            SetupModelsReadiness = SetupModelReadiness.Checking;
            await ScanSetupModelsAsync(ct);
            completed = true;
        }, applyDrafts: false, resumeConfig: plan.CreateConfig(), resumeFresh: false, setupOperation: true);
        setupModelResumeAvailable = !completed;
        if (completed)
        {
            setupModelResumePlan = null;
            SetupModelReviewVisible = SetupModelsReadiness != SetupModelReadiness.Ready;
            SetupCompletionOperation = SetupCompletionOperation.None;
        }
        else if (SetupModelsReadiness == SetupModelReadiness.Checking)
            SetupModelsReadiness = SetupModelReadiness.ScanFailed;
        NotifySetup();
    }

    public async Task RefreshSetupSystemAsync()
    {
        if (Busy) return;
        SetupError = "";
        SetupSystemSupported = null;
        SetupSystemSummary = "正在检查系统和显卡…";
        NotifySetup();
        await Run(async _ =>
        {
            SystemInfoText = await DetectSystem();
            Reloaded();
        }, applyDrafts: false, setupOperation: true);
    }
}
