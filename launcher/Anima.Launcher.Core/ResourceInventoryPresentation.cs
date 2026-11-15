using System.Text.Json;

namespace Anima.Launcher.Core;

public enum ResourcePurpose { Required, Selected, Optional, Legacy }

public sealed record NodeInventoryItem(NodeStatus Node, ResourcePurpose Purpose)
{
    public bool NeedsRepair => Node.State == "缺失" && Purpose is ResourcePurpose.Required or ResourcePurpose.Selected;
    public string PurposeLabel => Purpose switch
    {
        ResourcePurpose.Required => "基础必需",
        ResourcePurpose.Selected => "已选功能",
        ResourcePurpose.Legacy => "历史兼容，当前工作台无需",
        _ => "可选功能",
    };
    public string StateLabel => Node.State == "缺失" && Purpose is ResourcePurpose.Optional or ResourcePurpose.Legacy
        ? "未安装，不影响基础使用" : Node.State;
}

/// <summary>Inventory describes unused resources without treating them as installation failures.</summary>
public static class ResourceInventoryPresentation
{
    public static string[] FeaturesForOwners(LauncherConfig config, IEnumerable<DownloadOwnerRecord> owners,
        RuntimeManifest runtime, Action<string>? log = null)
    {
        var features = new HashSet<string>(StringComparer.Ordinal);
        foreach (var owner in owners)
        {
            // Completed preparations remain selected after restart. Paused/failed
            // preparations were also confirmed and still need their missing resources.
            // Canceled or NeedsReview records cannot establish a current selection.
            if (owner.CancellationRequested || owner.Status is not (DownloadTaskStatus.Queued or DownloadTaskStatus.Running
                or DownloadTaskStatus.Pausing or DownloadTaskStatus.Paused or DownloadTaskStatus.Failed or DownloadTaskStatus.Completed)
                || !FeaturePreparation.IsFeatureOwner(owner)) continue;
            try
            {
                if (owner.Configuration is null || owner.Fingerprint != DownloadTaskIdentity.Owner(owner))
                    throw new InvalidDataException("功能任务指纹不匹配。");
                var plan = owner.Payload!.Value.Deserialize<InstallationPlan>(JsonFile.Options)
                    ?? throw new InvalidDataException("功能任务缺少冻结计划。");
                if (plan.Features is null || plan.RequestedChoices is null || plan.FrozenModels is null
                    || plan.ModelIds.IsDefault || plan.ModelIds.Any(string.IsNullOrWhiteSpace)
                    || plan.Features.Any(string.IsNullOrWhiteSpace) || plan.FrozenModels.Any(model => model is null)
                    || plan.ResourcePolicyVersion != SetupFeatures.PolicyVersion)
                    throw new InvalidDataException("功能任务资源合同无效。");
                FeaturePreparation.Validate(plan);
                var frozenConfig = plan.CreateConfig() ?? throw new InvalidDataException("功能任务缺少冻结环境。");
                if (!SamePath(config.ComfyRoot, owner.EnvironmentRoot) || !SamePath(config.ComfyRoot, owner.Configuration.ComfyRoot)
                    || !SamePath(config.ComfyRoot, plan.ComfyRoot) || !SamePath(config.ComfyRoot, frozenConfig.ComfyRoot)
                    || !SamePath(config.Python, owner.Configuration.Python) || !SamePath(config.Python, frozenConfig.Python)) continue;
                // A separately confirmed feature can reselect an option skipped during
                // first-time setup. The setup receipt's skip list does not apply here.
                features.UnionWith(FeaturesForPlan(plan, runtime));
            }
            catch (Exception error) when (error is JsonException or InvalidDataException or IOException or ArgumentException or InvalidOperationException or NotSupportedException)
            {
                log?.Invoke("已忽略无法验证的功能准备记录 " + owner.Id + "：" + error.Message);
            }
        }
        return features.Order(StringComparer.Ordinal).ToArray();
    }

    private static bool SamePath(string? first, string? second) => !string.IsNullOrWhiteSpace(first)
        && !string.IsNullOrWhiteSpace(second) && Path.IsPathFullyQualified(first) && Path.IsPathFullyQualified(second)
        && Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)).Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), StringComparison.OrdinalIgnoreCase);

    public static string[] FeaturesForPlan(InstallationPlan plan, RuntimeManifest runtime, IEnumerable<string>? skippedIds = null)
    {
        if (plan.ResourcePolicyVersion == 0)
            return (plan.ResolvedRuntime ?? runtime).Nodes.Select(node => SetupFeatures.NodeFeature(node.Id))
                .Where(feature => feature is not null).Select(feature => feature!).Distinct().ToArray();
        return SetupFeatures.Resolve(plan.Fresh, plan.ModelIds.Concat(plan.Features
            .Where(feature => feature is "sesqui" or "nvidia" or "dlss" or "prompt-assistant")
            .Select(feature => SetupFeatures.Prefix + feature)).Except(skippedIds ?? []), plan.SelectedProfile);
    }

    public static NodeInventoryItem[] Classify(IEnumerable<NodeStatus> nodes, IEnumerable<string> selectedFeatures)
    {
        var selected = selectedFeatures.ToHashSet(StringComparer.Ordinal);
        return nodes.Select(node => new NodeInventoryItem(node, SetupFeatures.NodeFeature(node.Name) switch
        {
            "legacy-detailers" when !selected.Contains("legacy-detailers") => ResourcePurpose.Legacy,
            null => ResourcePurpose.Required,
            var feature when selected.Contains(feature) => ResourcePurpose.Selected,
            _ => ResourcePurpose.Optional,
        })).ToArray();
    }

    public static string Summary(IEnumerable<NodeInventoryItem> nodes)
    {
        var items = nodes.ToArray();
        return $"必需及已选节点待补齐 {items.Count(item => item.NeedsRepair)}；"
            + $"可选未安装 {items.Count(item => item.Purpose == ResourcePurpose.Optional && item.Node.State == "缺失")}；"
            + $"历史兼容节点已保留 {items.Count(item => item.Purpose == ResourcePurpose.Legacy && item.Node.State != "缺失")}";
    }
}
