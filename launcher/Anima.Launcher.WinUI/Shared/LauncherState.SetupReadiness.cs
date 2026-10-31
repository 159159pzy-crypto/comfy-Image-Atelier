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
        var mandatory = SetupModelSelection.MandatoryModels(OperationModels, OperationProfiles, Config.SelectedProfile);
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
        var report = await readiness.CheckAsync(Config, Program.Runtime, OperationModels, OperationProfiles, selectedIds, ct, AppendLog,
            InstallationPlanner.ResolveRuntimeRoot(Config, Program.StateDir));
        if (report.RequiredIssues.Length > 0)
            throw new InvalidOperationException("运行检查未通过：\n" + string.Join("\n", report.RequiredIssues.Select(issue => issue.Message)));
        foreach (var issue in report.OptionalIssues)
        {
            if (!SetupOptionalFailures.Contains(issue.Message)) SetupOptionalFailures.Add(issue.Message);
            if (!string.IsNullOrWhiteSpace(issue.ModelId)) setupFailedOptionalModelIds.Add(issue.ModelId);
        }
        Setup.RuntimeVerified = true;
        SetupRequiredModelsReady = true;
        Config.SetupComplete = true;
        Save();
        if (SetupActive) PersistSetup();
        UpdateSetupProgress(new("verify", "必需节点、模型分类、模型和词库检查通过", SetupStageStatus.Complete));
    }
}
