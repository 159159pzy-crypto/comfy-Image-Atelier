using System.Security.Cryptography;
using System.Text;

namespace Anima.Launcher.Core;

public sealed record ComfyUpdatePlan(string Root, string Python, ComfyRelease Release,
    ComfyDependencySnapshot Dependencies, DateTimeOffset CreatedAt);

/// <summary>Preserves a manually reviewed update without checking the network or changing the environment.</summary>
public sealed class ComfyUpdatePlans(string stateDir)
{
    private static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private string PathFor(LauncherConfig config) => Path.Combine(stateDir, "comfy-update-plans",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Full(config.ComfyRoot).ToUpperInvariant()))).ToLowerInvariant() + ".json");

    public ComfyUpdatePlan? Read(LauncherConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ComfyRoot) || string.IsNullOrWhiteSpace(config.Python)) return null;
        var path = PathFor(config);
        if (!File.Exists(path)) return null;
        var plan = JsonFile.Read<ComfyUpdatePlan>(path);
        if (!Full(plan.Root).Equals(Full(config.ComfyRoot), StringComparison.OrdinalIgnoreCase)
            || !Full(plan.Python).Equals(Full(config.Python), StringComparison.OrdinalIgnoreCase)) return null;
        Validate(plan.Release, plan.Dependencies);
        return plan;
    }

    public ComfyUpdatePlan Save(LauncherConfig config, ComfyRelease release, ComfyDependencySnapshot dependencies)
    {
        Validate(release, dependencies);
        var plan = new ComfyUpdatePlan(Full(config.ComfyRoot), Full(config.Python), release, dependencies, DateTimeOffset.UtcNow);
        JsonFile.Write(PathFor(config), plan);
        return plan;
    }

    public void Clear(LauncherConfig config)
    {
        if (Read(config) is not null) File.Delete(PathFor(config));
    }

    private static void Validate(ComfyRelease release, ComfyDependencySnapshot dependencies)
    {
        Safety.Commit(release.Commit);
        if (!ComfyReleases.StableTag(release.Tag) || release.Commit != dependencies.ComfyCommit)
            throw new InvalidDataException("手动更新计划的稳定版本与依赖快照不一致。");
        if (dependencies.Resolved && (dependencies.PythonIdentity.Length == 0 || dependencies.Constraints.Length == 0))
            throw new InvalidDataException("手动更新计划缺少完整的已解析依赖身份。");
    }
}
