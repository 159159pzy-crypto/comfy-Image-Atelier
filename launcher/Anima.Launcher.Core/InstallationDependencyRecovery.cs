using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Anima.Launcher.Core;

public sealed record ReviewedDependencyAdditions(string CheckpointDigest, string[] Packages);

public static class InstallationDependencyRecovery
{
    internal static string CheckpointPath(string root, string commit, string stateDir)
    {
        Safety.Commit(commit);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant()))).ToLowerInvariant();
        return Path.Combine(stateDir, "install-dependency-plans", key, commit + ".json");
    }

    private static string Digest(ComfyDependencySnapshot snapshot) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonFile.Options)));

    internal static void ValidateInputs(ComfyDependencySnapshot saved, ComfyDependencySnapshot expected)
    {
        if (saved.ComfyCommit != expected.ComfyCommit || saved.RequirementsText != expected.RequirementsText
            || saved.ManagerRequirementsText != expected.ManagerRequirementsText)
            throw new InvalidDataException("续装依赖计划与已确认的 ComfyUI 版本不同。");
    }

    internal static ReviewedDependencyAdditions? Review(ComfyDependencySnapshot saved, string[] current)
    {
        var known = DependencyPlans.Versions(saved.EnvironmentPackages);
        foreach (var package in saved.Packages) known.TryAdd(ComfyDependencyResolver.NormalizeName(package.Name), package.Version);
        var additions = DependencyPlans.Versions(current).Where(pair => !known.ContainsKey(pair.Key))
            .Select(pair => pair.Key + "==" + pair.Value).Order(StringComparer.Ordinal).ToArray();
        if (additions.Length == 0)
        {
            DependencyPlans.ValidateEnvironment(saved, current);
            return null;
        }
        // Only pure additions are eligible. Keep the old target versions and GPU ABI.
        DependencyPlans.RecordAuthorizedAdditions(saved, current);
        return new(Digest(saved), additions);
    }

    public static async Task<InstallationPlan> ReviewAsync(InstallationPlan plan, string stateDir, CancellationToken ct)
    {
        if (!plan.Fresh || plan.Dependencies is not { } expected) return plan;
        var path = CheckpointPath(plan.ComfyRoot, expected.ComfyCommit, stateDir);
        if (!File.Exists(path)) return plan;
        var saved = JsonFile.Read<ComfyDependencySnapshot>(path);
        ValidateInputs(saved, expected);
        var config = plan.CreateConfig();
        if (!saved.Resolved || saved.PlanVersion != 1 || !File.Exists(config.Python)) return plan;
        var review = Review(saved, await CurrentEnvironment(config, saved, ct));
        if (review is null) return plan;
        var message = "继续安装将保留以下新增的 Python 包：" + string.Join("、", review.Packages);
        if (File.Exists(path + ".active.json")) message = "上次依赖安装器中断。" + message;
        return plan with { ReviewedDependencyAdditions = review, DependencyReviewIssues = [.. plan.DependencyReviewIssues, message] };
    }

    internal static async Task<string[]> CurrentEnvironment(LauncherConfig config, ComfyDependencySnapshot saved, CancellationToken ct)
    {
        var (identity, packages, _) = await DependencyPlans.ReadEnvironmentAsync(config, ct);
        if (identity != saved.PythonIdentity)
            throw new InvalidOperationException("Python 或 GPU 版本与原环境不一致。请恢复原版本后重新检查。");
        return packages;
    }

    internal static async Task CheckpointAsync(LauncherConfig config, ComfyDependencySnapshot snapshot, string path,
        Func<Task> action, Action<ComfyDependencySnapshot> checkpointed)
    {
        DependencyPlans.ValidateEnvironment(snapshot, await CurrentEnvironment(config, snapshot, CancellationToken.None), installed: true);
        var activePath = path + ".active.json";
        JsonFile.Write(activePath, DependencyInstallation.PlanIdentity(snapshot));
        try { await action(); }
        finally
        {
            var current = await CurrentEnvironment(config, snapshot, CancellationToken.None);
            var updated = DependencyPlans.RecordAuthorizedAdditions(snapshot, current);
            JsonFile.Write(path, updated);
            checkpointed(updated);
            File.Delete(activePath);
        }
    }

    internal static ComfyDependencySnapshot Apply(ComfyDependencySnapshot saved, ReviewedDependencyAdditions approval, string[] current)
    {
        if (Digest(saved) != approval.CheckpointDigest)
        {
            // A retry may already have checkpointed the accepted additions.
            var known = DependencyPlans.Versions(saved.EnvironmentPackages);
            if (approval.Packages.Length > 0 && DependencyPlans.Versions(approval.Packages).All(pair => known.GetValueOrDefault(pair.Key) == pair.Value))
            {
                DependencyPlans.ValidateEnvironment(saved, current);
                return saved;
            }
            throw new InvalidOperationException("依赖计划在确认后发生变化，请重新检查。");
        }
        if (Review(saved, current) is not { } observed || !approval.Packages.SequenceEqual(observed.Packages))
            throw new InvalidOperationException("新增依赖与已确认的清单不一致，请重新检查。");
        return DependencyPlans.RecordAuthorizedAdditions(saved, current);
    }
}
