using Anima.Launcher.Core;

namespace Anima.Launcher;

internal sealed partial class LauncherState
{
    private async Task VerifySetupRuntimeAsync(string[] selectedIds, CancellationToken ct)
    {
        Setup.RuntimeVerified = false;
        SetupRequiredModelsReady = false;
        UpdateSetupProgress(new("verify", "正在启动服务并检查节点、分类、词库和模型", SetupStageStatus.Running));
        SetStatus("正在检查实际运行环境，尚未完成安装…");
        var mandatory = SetupModelSelection.RequiredForPlan(activeInstallationPlan, OperationModels, OperationProfiles, Config.SelectedProfile);
        var files = await ResolveModelIdsAsync(mandatory.Select(model => model.Id).ToArray(), true, ct);
        if (files.Count != mandatory.Length || files.Any(item => item.Missing))
            throw new InvalidOperationException("必装模型尚未全部通过校验，请继续准备模型。");
        foreach (var item in files.Where(item => item.Model.Category == "diffusion_models"))
            await ModelMetadata.EnsureDefaultAsync(Config, OperationRuntime, OperationModels, OperationProfiles,
                item.FoundPath ?? item.Destination, ct, AppendLog);
        await WithPortRecoveryAsync(async () =>
        {
            await EnsureCurrentVersionAsync(ct);
            await Services.StartWebUI(Config, Program.Runtime, ct);
        }, ct);
        using var readiness = new SetupRuntimeReadiness();
        var plan = activeInstallationPlan ?? Setup.ReviewedPlan;
        var features = plan is { ResourcePolicyVersion: > 0 }
            ? SetupFeatures.Resolve(plan.Fresh, selectedIds.Concat(plan.Features
                .Where(feature => feature is "sesqui" or "nvidia" or "dlss" or "prompt-assistant")
                .Select(feature => SetupFeatures.Prefix + feature)).Except(Setup.SkippedOptionalModelIds), Config.SelectedProfile)
            : null;
        var report = await readiness.CheckAsync(Config, Program.Runtime, OperationModels, OperationProfiles, selectedIds, ct, AppendLog,
            InstallationPlanner.ResolveRuntimeRoot(Config, Program.StateDir), features);
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
        Setup.RuntimeVerified = true;
        SetupRequiredModelsReady = true;
        Config.SetupComplete = true;
        Save();
        if (SetupActive) PersistSetup();
        UpdateSetupProgress(new("verify", "环境已就绪，可进入工作台配置与生成", SetupStageStatus.Complete));
    }
}
