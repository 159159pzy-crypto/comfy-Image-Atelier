namespace Anima.Launcher.Core;

public enum SetupStep { Welcome, Location, Review, Installing, Complete, Finished, Assistant = 6 }
public enum AssistantSetupStatus { Legacy = 0, Pending, Configured, Skipped }
public enum SetupStageStatus { Pending, Running, Complete, Paused, Failed }
public sealed record SetupProgress(string StageId, string Detail, SetupStageStatus Status);
public sealed record SetupRestoreDecision(SetupStep? Step, bool RequiresSupplement = false, bool RequiresRepairReview = false);

/// <summary>A draft is separate from the active configuration until installation starts.</summary>
public sealed class SetupRecord
{
    public int SchemaVersion { get; set; } = 1;
    public SetupStep Step { get; set; }
    public bool Fresh { get; set; } = true;
    public LauncherConfig Draft { get; set; } = new();
    public bool InstallStarted { get; set; }
    public bool InstallFinished { get; set; }
    public bool ModelsPending { get; set; }
    public bool RuntimeVerified { get; set; }
    public string[] OptionalModelIds { get; set; } = [];
    public string[] SkippedOptionalModelIds { get; set; } = [];
    public InstallationPlan? ReviewedPlan { get; set; }
    public InstallationPlan? SupplementalPlan { get; set; }
    public AssistantSetupStatus AssistantStatus { get; set; }
    // Old installations selected the ComfyUI folder itself. Keep that identity
    // when the user temporarily clears the path while editing a recovery draft.
    public bool DirectComfyRoot { get; set; }
}

public static class SetupRouting
{
    public static bool OpenWizardFromEnvironmentPage(LauncherConfig current, bool setupActive) =>
        setupActive || string.IsNullOrWhiteSpace(current.ComfyRoot) || current.InstallPending;
    /// <summary>
    /// Null means the regular overview. SetupComplete deliberately does not participate:
    /// it records a successful workbench launch, not whether an environment is installed.
    /// </summary>
    public static SetupStep? Resolve(LauncherConfig current, SetupRecord? saved)
    {
        if (current.InstallPending) return SetupStep.Installing;
        var configured = !string.IsNullOrWhiteSpace(current.ComfyRoot);
        if (saved is null || saved.SchemaVersion != 1 || saved.Draft is null || !Enum.IsDefined(saved.Step))
            return configured ? null : SetupStep.Welcome;
        if (configured && !TargetsMatch(current, saved.Draft, saved.Fresh)) return null;
        if (saved.Step == SetupStep.Finished) return configured ? null : SetupStep.Welcome;
        if (saved.InstallFinished)
        {
            // A completion receipt from a different, removed or incomplete environment
            // must never enable a misleading "start creating" screen.
            if (!configured || !TargetsMatch(current, saved.Draft, saved.Fresh) || !HasLaunchFiles(current))
                return SetupStep.Location;
            if (saved.ModelsPending) return SetupStep.Installing;
            return saved.AssistantStatus == AssistantSetupStatus.Pending ? SetupStep.Assistant : SetupStep.Complete;
        }
        if (saved.InstallStarted) return SetupStep.Installing;
        return saved.Step switch
        {
            SetupStep.Location or SetupStep.Review or SetupStep.Installing or SetupStep.Complete or SetupStep.Assistant => SetupStep.Location,
            _ => SetupStep.Welcome
        };
    }

    /// <summary>Unfinished completion screens must prepare requirements introduced after their frozen plan.</summary>
    public static SetupRestoreDecision ResolveForRuntime(LauncherConfig current, SetupRecord? saved,
        RuntimeManifest runtime, ModelManifest models, ModelProfilesManifest profiles)
    {
        var step = Resolve(current, saved);
        // An ended wizard remains ended. Its explicit installation/repair entry point
        // creates a new reviewed plan instead of reopening onboarding on every upgrade.
        if (step is not (SetupStep.Complete or SetupStep.Assistant)) return new(step);
        var plan = saved!.ReviewedPlan;
        if (plan is null || !TargetsMatch(current, new LauncherConfig { ComfyRoot = plan.ComfyRoot }, false)
            || plan.RuntimeArtifactsIdentity.Length > 0 && plan.RuntimeArtifactsIdentity != RuntimeArtifactPlanning.Identity)
            return new(SetupStep.Location, RequiresRepairReview: true);
        return NeedsSupplement(plan, runtime, models, profiles) ? new(SetupStep.Installing, RequiresSupplement: true) : new(step);
    }

    public static bool NeedsSupplement(InstallationPlan plan, RuntimeManifest runtime,
        ModelManifest models, ModelProfilesManifest profiles) => plan.RuntimeArtifactsIdentity.Length == 0
        || SetupModelSelection.SupplementalModels(plan, models, profiles).Length > 0
        || plan.ResolvedRuntime is { } frozen && runtime.Nodes.Any(node => !frozen.Nodes.Any(old => old.Id == node.Id));

    /// <summary>Compare a normalized installation target, including an unexpanded fresh-install draft.</summary>
    public static bool TargetsMatch(LauncherConfig current, LauncherConfig draft, bool fresh)
    {
        try
        {
            var target = fresh && !string.IsNullOrWhiteSpace(draft.InstallRoot)
                ? Path.Combine(draft.InstallRoot.Trim().Trim('"'), "ComfyUI") : draft.ComfyRoot;
            if (string.IsNullOrWhiteSpace(current.ComfyRoot) || string.IsNullOrWhiteSpace(target)) return false;
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current.ComfyRoot.Trim().Trim('"')))
                .Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.Trim().Trim('"'))), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    private static bool HasLaunchFiles(LauncherConfig config) =>
        File.Exists(Path.Combine(config.ComfyRoot, "main.py")) && File.Exists(config.Python);
}

/// <summary>Only the most recent check of the current draft can authorize installation.</summary>
public sealed class SetupReviewGate
{
    private readonly object sync = new();
    private int revision;
    private bool canInstall;
    private bool checking;
    public bool CanInstall { get { lock (sync) return canInstall; } }

    public int Begin()
    {
        lock (sync)
        {
            canInstall = false;
            checking = true;
            return ++revision;
        }
    }

    public void Invalidate()
    {
        lock (sync)
        {
            revision++;
            canInstall = false;
            checking = false;
        }
    }

    /// <returns>True only when this result belongs to the latest unmodified draft.</returns>
    public bool Complete(int checkedRevision, bool passed)
    {
        lock (sync)
        {
            if (!checking || checkedRevision != revision) return false;
            checking = false;
            canInstall = passed;
            return true;
        }
    }
}
