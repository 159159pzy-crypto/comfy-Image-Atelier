using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Anima.Launcher.Core;

/// <summary>Each confirmed feature operation owns one complete dependency graph across retries.</summary>
public static class FeatureDependencyCheckpoint
{
    public static string Open(InstallationPlan plan, string stateDir)
    {
        FeaturePreparation.Validate(plan);
        var config = plan.CreateConfig();
        var runtime = plan.ResolvedRuntime ?? throw new InvalidDataException("功能准备尚未固定节点版本。");
        // Transport preferences and displayed review findings may change without changing this graph.
        var identity = JsonSerializer.Serialize(new
        {
            plan.DependencyCheckpointId,
            Root = DownloadTaskIdentity.PathKey(plan.ComfyRoot),
            Python = DownloadTaskIdentity.PathKey(config.Python),
            AppRoot = string.IsNullOrWhiteSpace(config.AppRoot) ? "" : DownloadTaskIdentity.PathKey(config.AppRoot),
            plan.SelectedProfile, plan.WorkbenchDependencyContractVersion,
            Choices = plan.RequestedChoices.Order(StringComparer.Ordinal),
            Features = plan.Features.Order(StringComparer.Ordinal),
            Nodes = runtime.Nodes.OrderBy(node => node.Id, StringComparer.Ordinal).Select(node => new { node.Id, node.Url, node.Commit }),
            runtime.Sam2Commit,
        }, JsonFile.Options);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        var directory = Path.Combine(stateDir, "feature-dependency-plans", plan.DependencyCheckpointId);
        var binding = Path.Combine(directory, "identity.json");
        var path = Path.Combine(directory, "dependencies.json");
        if (File.Exists(binding))
        {
            if (JsonFile.Read<string>(binding) != digest)
                throw new InvalidDataException("功能准备的依赖记录属于其他环境或功能选择，请重新检查并创建新任务。");
        }
        else
        {
            if (File.Exists(path)) throw new InvalidDataException("功能准备依赖记录缺少任务身份，已保留原文件。");
            JsonFile.Write(binding, digest);
        }
        return path;
    }

    public static void ValidateInputs(ComfyDependencySnapshot snapshot, LauncherConfig config, IEnumerable<string> nodes, string sam2Commit)
    {
        var requirements = File.ReadAllText(Path.Combine(config.ComfyRoot, "requirements.txt"));
        var managerPath = Path.Combine(config.ComfyRoot, "manager_requirements.txt");
        var manager = File.Exists(managerPath) ? File.ReadAllText(managerPath) : "";
        var commit = RuntimeDependencies.NodeCommit(config.ComfyRoot);
        if (snapshot.RequirementsText != requirements || snapshot.ManagerRequirementsText != manager
            || snapshot.NodeRequirementsIdentity != ComfyDependencyResolver.NodeRequirementsIdentity(nodes, sam2Commit)
            || (snapshot.ComfySourceKind == "git" ? snapshot.ComfyCommit != commit
                : snapshot.ComfySourceKind != "requirements" || commit is not null))
            throw new InvalidOperationException("功能准备的依赖声明或 ComfyUI 版本已变化，请重新检查并创建新任务；原固定计划保持不变。");
    }

    // Source archives have no Git identity. Their requirements still receive a stable,
    // explicitly labelled content identity, so feature retries never fall back to a new solve.
    public static ComfyDependencySnapshot SourceArchiveSnapshot(string requirements, string manager) =>
        new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requirements + "\n" + manager)))[..40].ToLowerInvariant(),
            requirements, manager, []) { ComfySourceKind = "requirements" };
}
