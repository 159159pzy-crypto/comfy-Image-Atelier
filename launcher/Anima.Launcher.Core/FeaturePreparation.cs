using System.Text.Json;

namespace Anima.Launcher.Core;

/// <summary>A reviewed feature request uses the normal frozen installation and download contract.</summary>
public static class FeaturePreparation
{
    public const string Purpose = "feature-preparation";
    public static bool IsFeaturePlan(InstallationPlan plan) => plan.Purpose == Purpose;
    public static bool IsFeatureOwner(DownloadOwnerRecord owner)
    {
        if (owner.Kind != "install" || owner.Payload is not { ValueKind: JsonValueKind.Object } payload) return false;
        return payload.TryGetProperty("purpose", out var purpose) && purpose.ValueKind == JsonValueKind.String
            && purpose.GetString() == Purpose;
    }

    public static InstallationPlan Create(LauncherConfig config, RuntimeManifest runtime, string stateDir,
        ModelManifest models, ModelProfilesManifest profiles, IEnumerable<string> choiceIds)
    {
        var choices = SetupModelSelection.Choices(models, profiles, config.SelectedProfile, includeBase: true)
            .ToDictionary(choice => choice.Id, StringComparer.Ordinal);
        var requested = choiceIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (requested.Length == 0) throw new InvalidDataException("请至少选择一项需要准备的功能。");
        if (requested.Any(id => !choices.ContainsKey(id))) throw new InvalidDataException("功能选项已改变，请重新选择并检查。");
        var ids = requested.SelectMany(id => choices[id].ModelIds).Distinct(StringComparer.Ordinal).ToArray();
        return InstallationPlanner.Create(config, runtime, stateDir, false, models, profiles, ids)
            with { Purpose = Purpose, RequestedChoices = requested, DependencyCheckpointId = Guid.NewGuid().ToString("N") };
    }

    public static void Validate(InstallationPlan plan)
    {
        if (!IsFeaturePlan(plan) || plan.Fresh || plan.ResourcePolicyVersion <= 0 || plan.RequestedChoices.Length == 0
            || !Guid.TryParseExact(plan.DependencyCheckpointId, "N", out _))
            throw new InvalidDataException("完整功能准备计划无效，请重新检查。");
        if (plan.ModelIds.Distinct(StringComparer.Ordinal).Count() != plan.ModelIds.Length
            || plan.FrozenModels.Length != plan.ModelIds.Length
            || plan.ModelIds.Any(id => plan.FrozenModels.Count(model => model.Id == id) != 1))
            throw new InvalidDataException("完整功能准备计划缺少固定模型版本，请重新检查。");
    }

    public static string Describe(InstallationPlan plan)
    {
        Validate(plan);
        var runtime = plan.ResolvedRuntime ?? throw new InvalidDataException("节点版本尚未固定，请重新检查。");
        var lines = new List<string> { plan.Describe(), "", "模型文件（包含共享依赖）：" };
        if (plan.FrozenModels.Length == 0) lines.Add("本次功能不需要额外模型文件。");
        foreach (var model in plan.FrozenModels)
        {
            var status = plan.ModelInventory.FirstOrDefault(item => item.Model.Id == model.Id);
            lines.Add($"• {model.Name} · {(status is { Missing: false } ? "发现已有文件，准备时校验" : "检查并补齐")}\n  {status?.FoundPath ?? status?.Destination ?? model.RelativePath}");
        }
        lines.Add("\n插件与节点（保留现有版本）：");
        foreach (var node in Installer.PlanNodes(plan.ComfyRoot, runtime))
            lines.Add($"• {node.Repository.Id} · {(Directory.Exists(node.Directory) ? "检查已有依赖" : "安装")}\n  {node.Directory}");
        lines.Add("\n运行文件：");
        if (plan.FrozenRuntimeArtifacts.Length == 0) lines.Add("所选功能无需新增运行文件，已有内容将在安装时复查。");
        foreach (var artifact in plan.FrozenRuntimeArtifacts) lines.Add($"• {artifact.Id} · {artifact.Size / 1048576d:N1} MiB");
        lines.Add("\n依赖检查：");
        if (plan.DependencyReviewIssues.Length == 0) lines.Add("已检查的声明依赖没有发现缺项；新增插件的依赖将在安装时联合解析并校验。");
        else foreach (var issue in plan.DependencyReviewIssues) lines.Add("• " + issue);
        if (plan.ModelPathsDeferred) lines.Add("部分模型目录需在依赖补齐后才能确认；下载前会重新解析实际位置并检查目标磁盘空间。");
        lines.Add("\nPython 依赖会在此环境中联合检查并按需补齐；保留现有 GPU 运行时。\n开始前请停止此环境的 ComfyUI 和工作台。完成后会重新启动服务，校验本次所选功能；不会更改工作台默认模型或功能开关。");
        return string.Join("\n", lines);
    }

    public static SetupRuntimeIssue[] BlockingIssues(InstallationPlan plan, SetupRuntimeReadinessReport report)
    {
        Validate(plan);
        var modules = plan.Features.Select(feature => feature == "face-refine" ? "face_refine" : feature).ToHashSet(StringComparer.Ordinal);
        if (plan.Features.Contains("sesqui")) modules.Add("base_dual");
        if (plan.RequestedChoices.Contains("base") || plan.RequestedChoices.Contains("krea2")) modules.Add("base");
        return report.RequiredIssues.Concat(report.OptionalIssues)
            .Concat(report.ConfigurationIssues.Where(issue => modules.Contains(issue.Feature))).Distinct().ToArray();
    }

    public static string RunningServicesIssue(bool comfyRunning, JsonElement? queue, bool webRunning, JsonElement? batch)
    {
        var issue = QueueIssue(comfyRunning, queue, webRunning, batch);
        if (issue.Length > 0) return issue;
        return comfyRunning || webRunning
            ? "本次准备需要安装或检查插件依赖。请先在概览停止此环境的 ComfyUI 和工作台，再回到本页继续；外部服务请从原入口停止。完成后会重新启动并验证所选功能。"
            : "";
    }

    public static string QueueIssue(bool comfyRunning, JsonElement? queue, bool webRunning, JsonElement? batch)
    {
        if (comfyRunning)
        {
            if (queue is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty("queue_running", out var running)
                || !value.TryGetProperty("queue_pending", out var pending)
                || running.ValueKind != JsonValueKind.Array || pending.ValueKind != JsonValueKind.Array)
                return "无法确认 ComfyUI 队列状态。请检查连接并停止目标服务后继续；未更改环境。";
            if (running.GetArrayLength() > 0 || pending.GetArrayLength() > 0)
                return "ComfyUI 有正在执行或等待的任务。请等待任务完成，再停止 ComfyUI 和工作台后继续准备；未中断生成。";
        }
        if (webRunning && RuntimePolicies.WorkbenchActivity(batch) != WorkbenchActivity.Idle)
            return "工作台仍有任务，或无法确认是否空闲。请确认任务完成并停止服务后继续准备；未中断生成。";
        return "";
    }
}
