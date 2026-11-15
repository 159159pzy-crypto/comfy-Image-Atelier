using Anima.Launcher.Core;
using System.Text.Json;

namespace Anima.Launcher;

internal sealed partial class LauncherState
{
    private string? featureReviewConfiguration;
    public string FeaturePreparationPreflightIssue { get; private set; } = "";
    public SetupModelChoice[] FeaturePreparationChoices => SetupModelSelection.Choices(
        Program.Models, Program.ModelProfiles, Config.SelectedProfile, includeBase: true).ToArray();

    public DownloadContextProgress? FeaturePreparationProgress
    {
        get
        {
            var snapshot = DownloadView;
            var owner = snapshot.Owners.Where(FeaturePreparation.IsFeatureOwner)
                .Where(item => SamePath(item.EnvironmentRoot, Config.ComfyRoot))
                .OrderByDescending(item => item.Status is DownloadTaskStatus.Running or DownloadTaskStatus.Pausing)
                .ThenByDescending(item => item.UpdatedAt).FirstOrDefault();
            return owner is null ? null : DownloadContextPresentation.Progress(snapshot, owner);
        }
    }

    public async Task<InstallationPlan?> PreviewFeaturePreparationAsync(string[] choiceIds)
    {
        if (Busy || SetupActive || preparingModelDownloads || shuttingDown) return null;
        var inspection = TrackModelInspection();
        preparingModelDownloads = true;
        featureReviewConfiguration = null;
        FeaturePreparationPreflightIssue = "";
        NotifyDownloads();
        try
        {
            ApplyPendingFields();
            var snapshot = Config.Clone();
            var original = JsonSerializer.Serialize(Config, JsonFile.Options);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(modelInspectionLifetime.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            using var lease = await EnvironmentOperationLease.AcquireAsync(snapshot.ComfyRoot, false, timeout.Token);
            SetStatus("正在检查所选功能的模型、插件、运行文件与磁盘空间…");
            await WorkbenchDependencies.ValidateInterpreterAsync(snapshot, timeout.Token);
            var plan = await Task.Run(() => FeaturePreparation.Create(snapshot, Program.Runtime, Program.StateDir,
                Program.Models, Program.ModelProfiles, choiceIds), timeout.Token);
            plan = await InstallationPlanner.ResolveStableAsync(plan, Program.Runtime, Program.StateDir, timeout.Token);
            plan = await InstallationPlanner.PreparePlanAsync(plan, Program.Models, Program.ModelProfiles,
                Path.Combine(Program.Assets, "probe_comfy.py"), timeout.Token);
            var preflight = await InstallationPlanner.PreflightAsync(plan, timeout.Token);
            if (!preflight.CanInstall)
                throw new InvalidOperationException(string.Join("\n", preflight.Issues.Where(issue => issue.Blocking).Select(issue => issue.Message)));
            var review = await SetupImportReview.InspectAsync(plan.CreateConfig(), plan.ResolvedRuntime ?? Program.Runtime,
                timeout.Token, Program.StateDir);
            plan = plan with { DependencyReviewIssues = review.PreparationIssues
                .Concat(review.MissingCoreModules.Select(name => "缺少核心模块：" + name))
                .Select(DiagnosticRedaction.Text).Distinct(StringComparer.Ordinal).ToArray() };
            try { await RequireFeatureServicesStoppedAsync(plan.CreateConfig(), timeout.Token); }
            catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException)
            { FeaturePreparationPreflightIssue = error.Message; }
            if (Busy || original != JsonSerializer.Serialize(Config, JsonFile.Options) || shuttingDown) return null;
            featureReviewConfiguration = original;
            SetStatus("检查完成，请确认完整功能准备清单。");
            return plan;
        }
        catch (OperationCanceledException) when (shuttingDown) { return null; }
        catch (Exception ex) { TaskFailed?.Invoke(ex); return null; }
        finally
        {
            preparingModelDownloads = false;
            CompleteModelInspection(inspection);
            RefreshModelDownloadRows();
            NotifyDownloads();
        }
    }

    public async Task StartFeaturePreparationAsync(InstallationPlan plan)
    {
        if (Busy || SetupActive || shuttingDown) return;
        if (FeaturePreparationPreflightIssue.Length > 0 || featureReviewConfiguration != JsonSerializer.Serialize(Config, JsonFile.Options))
        {
            TaskFailed?.Invoke(new InvalidOperationException("环境或下载设置已改变，请重新检查完整功能准备清单。"));
            return;
        }
        FeaturePreparation.Validate(plan);
        // The install owner keeps one confirmation and one frozen resource scope.
        // Model-only owners remain independent and never gain plugin installation.
        var models = plan.ModelInventory.Where(item => plan.ModelIds.Contains(item.Model.Id) && item.Destination.Length > 0)
            .Select(item => new ModelDownloadIntent(item.Model, item.FoundPath ?? item.Destination)).ToArray();
        var owner = DownloadTasks.RegisterOwner("install", "准备完整功能 · " + string.Join("、", plan.RequestedChoices),
            plan.CreateConfig(), models, JsonSerializer.SerializeToElement(plan, JsonFile.Options));
        featureReviewConfiguration = null;
        await ResumeDownloadOwnerAsync(owner.Id);
    }

    private async Task RequireFeatureServicesStoppedAsync(LauncherConfig config, CancellationToken ct)
    {
        var comfyRunning = await ServiceManager.PortOpen(config.ComfyPort, ct);
        var webRunning = await ServiceManager.PortOpen(config.WebPort, ct);
        var queue = comfyRunning ? await ServiceManager.Json(config.ComfyUrl + "/queue", ct) : null;
        var batch = webRunning ? await ServiceManager.Json(config.WebUrl + "/api/batches/current", ct) : null;
        var issue = FeaturePreparation.RunningServicesIssue(comfyRunning, queue, webRunning, batch);
        if (issue.Length > 0) throw new IOException(issue);
        await new ComfyUpdateCoordinator(Program.StateDir, Commands.Find("git.exe") ?? "git.exe")
            .EnsureStoppedAsync(config, ct);
    }

    private async Task ExecuteFeaturePreparationAsync(InstallationPlan plan, CancellationToken ct)
    {
        FeaturePreparation.Validate(plan);
        activeInstallationPlan = plan;
        await RequireFeatureServicesStoppedAsync(Config, ct);
        var ownerId = activeDownloadOwner ?? throw new InvalidOperationException("功能准备缺少固定任务。");
        DownloadTasks.SetOwnerPhase(ownerId, "Installing", "正在准备插件、Python 依赖与运行文件");
        var installer = CreateInstaller(OperationRuntime);
        Config = await installer.Install(plan, ct);
        Save();
        Reloaded();
        if (installer.OptionalFailures.Count > 0)
            throw new InvalidOperationException("所选功能依赖尚未准备完成：\n" + string.Join("\n", installer.OptionalFailures.Select(issue => issue.Message)));

        DownloadTasks.SetOwnerPhase(ownerId, "Downloading", "正在补齐并校验所选模型");
        var scan = await ResolveModelIdsAsync(plan.ModelIds.ToArray(), true, ct);
        await DownloadModelsAsync(scan.Where(item => item.Missing).ToList(), ct);
        var verified = await ResolveModelIdsAsync(plan.ModelIds.ToArray(), true, ct);
        if (verified.Count != plan.ModelIds.Length || verified.Any(item => item.Missing))
            throw new InvalidOperationException("所选功能的模型文件尚未全部通过校验。");
        foreach (var item in verified.Where(item => item.Model.Category == "diffusion_models"))
            await ModelMetadata.EnsureDefaultAsync(Config, OperationRuntime, OperationModels, OperationProfiles,
                item.FoundPath ?? item.Destination, ct, AppendLog);

        DownloadTasks.SetOwnerPhase(ownerId, "FeatureValidation", "文件已准备，正在启动服务并验证所选功能");
        await WithPortRecoveryAsync(async () =>
        {
            await EnsureCurrentVersionAsync(ct);
            await Services.StartWebUI(Config, Program.Runtime, ct);
        }, ct);
        using var readiness = new SetupRuntimeReadiness();
        var report = await readiness.CheckAsync(Config, OperationRuntime, OperationModels, OperationProfiles,
            plan.ModelIds, ct, AppendLog, InstallationPlanner.ResolveRuntimeRoot(Config, Program.StateDir),
            plan.Features, requiredFeatures: plan.Features);
        var issues = FeaturePreparation.BlockingIssues(plan, report);
        if (issues.Length > 0)
            throw new InvalidOperationException("模型文件已准备，但完整功能检查尚未通过：\n" + string.Join("\n", issues.Select(issue => issue.Message)));
        foreach (var item in verified) PublishCompletedModel(Config, new(item.Model, item.FoundPath ?? item.Destination));
        DownloadTasks.SetOwnerPhase(ownerId, "FeatureValidation", "所选模型、插件与运行文件已通过检查");
        SetStatus("所选完整功能已准备并通过运行检查。工作台默认模型与功能开关保持原设置。");
    }
}
