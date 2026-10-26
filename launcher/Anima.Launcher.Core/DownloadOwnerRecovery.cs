using System.Text.Json;

namespace Anima.Launcher.Core;

public sealed record DownloadOwnerRecoveryResult(LauncherConfig Configuration, InstallationPlan Plan, SetupRecord Setup,
    ModelManifest Models, ModelProfilesManifest Profiles, bool ResetSetup, bool ReusesInstalledEnvironment);

/// <summary>Pure recovery decisions. The caller still verifies local files and owns installation transactions.</summary>
public static class DownloadOwnerRecovery
{
    public static DownloadOwnerRecoveryResult Prepare(DownloadOwnerRecord owner, InstallationPlan plan, SetupRecord? savedSetup,
        ModelManifest currentModels, ModelProfilesManifest currentProfiles, string? installedCommit = null)
    {
        if (owner.Kind is not ("setup" or "install")) throw new InvalidDataException("此下载任务不包含安装计划。");
        var frozen = DownloadTaskIdentity.Copy(plan);
        var config = frozen.CreateConfig();
        config.Validate();
        if (!SamePath(config.ComfyRoot, frozen.ComfyRoot) || !SamePath(owner.EnvironmentRoot, frozen.ComfyRoot)
            || !SamePath(owner.Configuration.ComfyRoot, frozen.ComfyRoot) || config.SelectedProfile != frozen.SelectedProfile)
            throw new InvalidDataException("下载任务与固定安装计划的环境身份不一致，请重新检查。");
        if (frozen.Release is { } release) Safety.Commit(release.Commit);
        if (frozen.ResolvedRuntime is { } runtime && frozen.Release is { } resolvedRelease
            && !runtime.Comfy.Commit.Equals(resolvedRelease.Commit, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("固定安装清单与版本提交不一致，请重新检查。");

        var sameSetup = savedSetup is { SchemaVersion: 1, Draft: not null }
            && SetupRouting.TargetsMatch(config, savedSetup.Draft, savedSetup.Fresh);
        var reusesInstalled = sameSetup && savedSetup!.InstallFinished;
        // A repair/import plan may carry the bundled runtime manifest while retaining
        // an existing ComfyUI checkout. Only an explicitly frozen Release pins core.
        var expectedCommit = frozen.Release?.Commit;
        if (!string.IsNullOrWhiteSpace(installedCommit))
        {
            Safety.Commit(installedCommit);
            RejectDifferentCommit(expectedCommit, installedCommit);
        }
        if (reusesInstalled)
        {
            var installedSetup = savedSetup!;
            var installedConfig = installedSetup.Draft!;
            if (installedSetup.ReviewedPlan is { } receipt)
            {
                if (!SamePath(receipt.ComfyRoot, frozen.ComfyRoot))
                    throw new InvalidDataException("已安装环境的完成记录与目标不一致，请重新检查。");
                RejectDifferentCommit(expectedCommit, receipt.Release?.Commit);
            }
            // These values are installation results. Source selection and frozen resource
            // versions come from the resumed owner, never the global onboarding draft.
            config.Python = installedConfig.Python;
            config.PythonExplicit = installedConfig.PythonExplicit;
            config.ManagedComfy = installedConfig.ManagedComfy;
            config.InstallPending = false;
        }
        config.SourceId = owner.Configuration.SourceId;
        config.GitMirror = owner.Configuration.GitMirror;
        config.ModelMirror = owner.Configuration.ModelMirror;
        config.PipIndex = owner.Configuration.PipIndex;
        config.NetworkMode = owner.Configuration.NetworkMode;
        config.ProxyAddress = owner.Configuration.ProxyAddress;
        config.Validate();
        frozen = frozen with { ConfigurationJson = JsonSerializer.Serialize(config, JsonFile.Options) };

        if (frozen.ModelIds.Length > 0 && frozen.FrozenModels.Length == 0)
        {
            // Earlier reviewed plans kept full ModelItem identities in inventory. This
            // is a bounded migration from saved data, never from today's model catalog.
            var inventory = frozen.ModelInventory.Where(item => frozen.ModelIds.Contains(item.Model.Id))
                .GroupBy(item => item.Model.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray());
            if (frozen.ModelIds.Any(id => !inventory.TryGetValue(id, out var items) || items.Length != 1))
                throw new InvalidDataException("旧安装计划未保存完整模型版本，请重新检查后创建新任务，不能套用当前模型清单继续。");
            frozen = frozen with { FrozenModels = frozen.ModelIds.Select(id => inventory[id][0].Model).ToArray() };
        }
        if (frozen.ModelIds.Length > 0 && frozen.FrozenProfile is null)
        {
            var candidate = currentProfiles.Get(frozen.SelectedProfile);
            try
            {
                // A legacy profile is admissible only when every required filename
                // already exists in the frozen inventory. It cannot add a new version.
                _ = new ModelProfilesManifest(1, candidate.Id, [candidate]).RequiredModels(new(1, frozen.FrozenModels), candidate.Id);
            }
            catch (InvalidDataException)
            {
                throw new InvalidDataException("旧安装计划的基础模型与当前档案不一致，请重新检查；不能用新模型替换旧版本。");
            }
            frozen = frozen with { FrozenProfile = candidate };
        }
        var frozenIds = frozen.FrozenModels.Select(model => model.Id).ToHashSet(StringComparer.Ordinal);
        if (frozenIds.Count != frozen.FrozenModels.Length || frozen.ModelIds.Any(id => !frozenIds.Contains(id)))
            throw new InvalidDataException("固定安装计划缺少已选模型版本或包含重复身份，请重新检查。");
        if (frozen.FrozenProfile is { } fixedProfile && fixedProfile.Id != frozen.SelectedProfile)
            throw new InvalidDataException("固定模型档案与安装计划不一致。");
        var models = new ModelManifest(frozen.FrozenModels.Length > 0 ? 1 : currentModels.SchemaVersion,
            frozen.FrozenModels.Concat(currentModels.Models.Where(model => !frozenIds.Contains(model.Id))).ToArray());
        var profiles = frozen.FrozenProfile is { } profile
            ? new ModelProfilesManifest(1, profile.Id, currentProfiles.Profiles.Where(item => item.Id != profile.Id).Append(profile).ToArray())
            : currentProfiles;
        string[] optional = [];
        if (frozen.ModelIds.Length > 0)
        {
            // Resolve required resources only inside the snapshot; current catalog additions
            // may be displayed but can never substitute for a missing frozen requirement.
            var required = profiles.RequiredModels(new(1, frozen.FrozenModels), frozen.SelectedProfile).Select(model => model.Id).ToHashSet();
            if (required.Any(id => !frozen.ModelIds.Contains(id)))
                throw new InvalidDataException("固定安装计划缺少基础模型，请重新检查。");
            optional = frozen.ModelIds.Where(id => !required.Contains(id)).ToArray();
        }
        var setup = sameSetup ? DownloadTaskIdentity.Copy(savedSetup!) : new SetupRecord { AssistantStatus = AssistantSetupStatus.Pending };
        setup.Draft = config.Clone(); setup.Fresh = frozen.Fresh; setup.InstallStarted = true;
        setup.Step = SetupStep.Installing; setup.ReviewedPlan = frozen; setup.OptionalModelIds = optional;
        return new(config, frozen, setup, DownloadTaskIdentity.Copy(models), DownloadTaskIdentity.Copy(profiles), !sameSetup, reusesInstalled);
    }

    private static void RejectDifferentCommit(string? expected, string? actual)
    {
        if (expected is not null && actual is not null && !expected.Equals(actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("此环境已完成另一个 ComfyUI 版本，不能继续旧安装计划。请保留现有环境并重新检查。");
    }

    private static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        return DownloadTaskIdentity.PathKey(left) == DownloadTaskIdentity.PathKey(right);
    }
}
