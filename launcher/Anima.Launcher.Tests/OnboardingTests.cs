using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using Anima.Launcher.Core;

internal static class OnboardingTests
{
    public static async Task<int> Run(string work, RuntimeManifest manifest)
    {
        var passed = 0;
        void Check(bool success, string label)
        {
            if (!success) throw new Exception("FAIL: " + label);
            passed++;
            Console.WriteLine("PASS: " + label);
        }

        var state = Path.Combine(work, "onboarding");
        var environment = Path.Combine(state, "installation");
        var comfy = Path.Combine(environment, "ComfyUI");
        var python = Path.Combine(comfy, ".venv", "Scripts", "python.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(python)!);
        File.WriteAllText(Path.Combine(comfy, "main.py"), "# isolated fixture");
        File.WriteAllText(python, "");
        var configured = new LauncherConfig { InstallRoot = environment, ComfyRoot = comfy, Python = python };
        var first = new LauncherConfig();

        Check(SetupRouting.Resolve(first, null) == SetupStep.Welcome, "first unconfigured launch enters welcome");
        Check(SetupRouting.Resolve(configured, null) is null, "legacy environment opens overview before its first successful workbench launch");
        configured.SetupComplete = true;
        Check(SetupRouting.Resolve(configured, null) is null, "previously launched environment opens overview");
        configured.SetupComplete = false;
        var unavailable = configured.Clone();
        unavailable.ComfyRoot = Path.Combine(state, "currently-unavailable");
        Check(SetupRouting.Resolve(unavailable, null) is null, "temporarily unavailable legacy root is not silently treated as a fresh installation");

        SetupRecord Record(SetupStep step = SetupStep.Location) => new() { Step = step, Draft = configured.Clone() };
        var saved = Record(SetupStep.Review);
        Check(SetupRouting.Resolve(first, saved) == SetupStep.Location, "review draft must be rechecked after restart");
        Check(SetupRouting.Resolve(configured, saved) == SetupStep.Location, "explicit wizard draft resumes without using stale review approval");
        saved.Step = SetupStep.Welcome;
        Check(SetupRouting.Resolve(first, saved) == SetupStep.Welcome, "welcome draft stays on welcome");
        saved.Step = SetupStep.Location;
        Check(SetupRouting.Resolve(first, saved) == SetupStep.Location, "unconfigured location draft resumes");
        saved.Step = SetupStep.Installing;
        Check(SetupRouting.Resolve(first, saved) == SetupStep.Location, "unstarted installation screen cannot pretend an install has begun");

        saved.InstallStarted = true;
        Check(SetupRouting.Resolve(first, saved) == SetupStep.Installing, "interrupted draft can recover even if active config was not saved");
        Check(SetupRouting.Resolve(configured, saved) == SetupStep.Installing, "matching interrupted installation enters recovery");
        configured.InstallPending = true;
        Check(SetupRouting.Resolve(configured, null) == SetupStep.Installing, "legacy pending flag takes priority without an onboarding receipt");
        saved.Step = SetupStep.Finished;
        Check(SetupRouting.Resolve(configured, saved) == SetupStep.Installing, "pending install takes priority over an ended wizard receipt");
        saved.Draft.ComfyRoot = Path.Combine(state, "unrelated");
        saved.Draft.InstallRoot = "";
        Check(SetupRouting.Resolve(configured, saved) == SetupStep.Installing, "pending current environment takes priority over a foreign receipt");
        configured.InstallPending = false;
        Check(SetupRouting.Resolve(configured, saved) is null, "foreign onboarding receipt never hijacks an established environment");

        saved = Record(SetupStep.Complete);
        saved.InstallStarted = saved.InstallFinished = true;
        Check(SetupRouting.Resolve(configured, saved) == SetupStep.Complete && !configured.SetupComplete,
            "installed but never launched wizard restores completion independently of SetupComplete");
        saved.Step = SetupStep.Installing;
        Check(SetupRouting.Resolve(configured, saved) == SetupStep.Complete, "finished install receipt repairs an out-of-date installing step");
        saved.Step = SetupStep.Finished;
        Check(SetupRouting.Resolve(configured, saved) is null && !configured.SetupComplete,
            "later-open ends onboarding without changing the successful-launch marker");
        Check(SetupRouting.Resolve(first, saved) == SetupStep.Welcome, "ended receipt without any active environment does not hide welcome");
        saved.Step = SetupStep.Complete;
        Check(SetupRouting.Resolve(first, saved) == SetupStep.Location, "completion cannot be trusted without a matching active configuration");
        File.Delete(python);
        Check(SetupRouting.Resolve(configured, saved) == SetupStep.Location, "deleted Python invalidates restored completion");
        File.WriteAllText(python, "");
        File.Delete(Path.Combine(comfy, "main.py"));
        Check(SetupRouting.Resolve(configured, saved) == SetupStep.Location, "deleted ComfyUI entry point invalidates restored completion");
        File.WriteAllText(Path.Combine(comfy, "main.py"), "# isolated fixture");

        var upgradeModels = new ModelManifest(1, new[] {
            ("anima-base-v1", "diffusion_models", "anima.safetensors"),
            ("qwen-text", "text_encoders", "clip.safetensors"), ("qwen-vae", "vae", "vae.safetensors"),
            ("seedvr2-model", "diffusion_models", "seed.safetensors"), ("seedvr2-vae", "vae", "seedvae.safetensors")
        }.Select(item => new ModelItem(item.Item1, item.Item1, "fixture", item.Item2, item.Item3, [], 1,
            new string('a', 64), ["https://example.com/fixture"], "fixture", "https://example.com/license")).ToArray());
        var upgradeProfile = new ModelProfileDefinition("anima", "Anima", "anima.safetensors", "clip.safetensors", "vae.safetensors", "stable_diffusion", "seed.safetensors", "seedvae.safetensors");
        var upgradeProfiles = new ModelProfilesManifest(1, "anima", [upgradeProfile]);
        var currentPlan = new InstallationPlan(JsonSerializer.Serialize(configured, JsonFile.Options), comfy,
            Path.Combine(state, "runtime"), "anima", false, 0, 0, 0, [])
        {
            ResolvedRuntime = manifest, RuntimeArtifactsIdentity = RuntimeArtifactPlanning.Identity,
            ModelIds = upgradeModels.Models.Select(model => model.Id).ToImmutableArray(),
            MandatoryModelIds = upgradeModels.Models.Select(model => model.Id).ToImmutableArray(),
            FrozenModels = upgradeModels.Models, FrozenProfile = upgradeProfile,
        };
        var legacyPlan = currentPlan with { RuntimeArtifactsIdentity = "", MandatoryModelIds = [],
            ModelIds = currentPlan.ModelIds.Take(3).ToImmutableArray(), FrozenModels = upgradeModels.Models.Take(3).ToArray() };
        foreach (var step in new[] { SetupStep.Complete, SetupStep.Assistant })
        {
            var upgradeReceipt = Record(step);
            upgradeReceipt.InstallStarted = upgradeReceipt.InstallFinished = true;
            upgradeReceipt.AssistantStatus = step == SetupStep.Assistant ? AssistantSetupStatus.Pending : AssistantSetupStatus.Skipped;
            upgradeReceipt.ReviewedPlan = legacyPlan;
            var before = JsonSerializer.Serialize(upgradeReceipt, JsonFile.Options);
            var decision = SetupRouting.ResolveForRuntime(configured, upgradeReceipt, manifest, upgradeModels, upgradeProfiles);
            Check(decision.Step == SetupStep.Installing && decision.RequiresSupplement && !decision.RequiresRepairReview
                && JsonSerializer.Serialize(upgradeReceipt, JsonFile.Options) == before,
                $"legacy unfinished {step} returns to supplemental installation without mutating its frozen plan");
            upgradeReceipt.ReviewedPlan = currentPlan;
            Check(SetupRouting.ResolveForRuntime(configured, upgradeReceipt, manifest, upgradeModels, upgradeProfiles).Step == step,
                $"current unfinished {step} keeps its position and still requires fresh runtime verification");
        }
        var noPlan = Record(SetupStep.Complete);
        noPlan.InstallStarted = noPlan.InstallFinished = true;
        Check(SetupRouting.ResolveForRuntime(configured, noPlan, manifest, upgradeModels, upgradeProfiles)
            is { Step: SetupStep.Location, RequiresRepairReview: true },
            "old completion without a frozen plan returns to review an existing-environment repair");
        noPlan.ReviewedPlan = currentPlan with { ComfyRoot = Path.Combine(state, "foreign") };
        Check(SetupRouting.ResolveForRuntime(configured, noPlan, manifest, upgradeModels, upgradeProfiles).RequiresRepairReview,
            "completion never resumes a frozen plan for a different environment");
        noPlan.Step = SetupStep.Finished;
        noPlan.ReviewedPlan = legacyPlan;
        Check(SetupRouting.ResolveForRuntime(configured, noPlan, manifest, upgradeModels, upgradeProfiles).Step is null
            && !SetupRouting.OpenWizardFromEnvironmentPage(configured, false),
            "an explicitly finished legacy wizard stays closed while its maintenance entry can create a new repair plan");
        Check(SetupRouting.NeedsSupplement(currentPlan with { ResolvedRuntime = manifest with { Nodes = [] } }, manifest, upgradeModels, upgradeProfiles),
            "a completed plan missing newly required nodes must be supplemented even with current native identity");
        Check(SetupRouting.NeedsSupplement(legacyPlan with { RuntimeArtifactsIdentity = RuntimeArtifactPlanning.Identity }, manifest, upgradeModels, upgradeProfiles),
            "a completed generation-only plan must add mandatory SeedVR2 before returning to creation");

        saved.SchemaVersion = 999;
        Check(SetupRouting.Resolve(configured, saved) is null && SetupRouting.Resolve(first, saved) == SetupStep.Welcome,
            "unknown onboarding schema is ignored without discarding current configuration");
        saved.SchemaVersion = 1;
        saved.Step = (SetupStep)999;
        Check(SetupRouting.Resolve(first, saved) == SetupStep.Welcome, "unknown wizard step cannot be resumed");
        saved.Step = SetupStep.Location;
        saved.Draft = null!;
        Check(SetupRouting.Resolve(first, saved) == SetupStep.Welcome, "malformed null draft safely falls back to welcome");

        var unexpanded = new LauncherConfig { InstallRoot = environment, SourceId = "mirror" };
        Check(SetupRouting.TargetsMatch(configured, unexpanded, true), "fresh draft install location expands to its actual ComfyUI target");
        Check(!SetupRouting.TargetsMatch(configured, unexpanded, false), "import never derives its target from a stale install-root field");
        var imported = configured.Clone();
        imported.ComfyRoot = comfy.ToUpperInvariant() + Path.DirectorySeparatorChar;
        Check(SetupRouting.TargetsMatch(configured, imported, false), "target identity accepts case and a trailing separator on Windows");
        imported.ComfyRoot = "\0";
        Check(!SetupRouting.TargetsMatch(configured, imported, false), "invalid target path cannot match another environment");

        var draftFile = Path.Combine(state, "onboarding.json");
        saved = Record();
        saved.Fresh = false;
        saved.Draft.SourceId = "mirror";
        saved.Draft.GitMirror = "https://mirror.example.com/{url}";
        JsonFile.Write(draftFile, saved);
        var loaded = JsonFile.Read<SetupRecord>(draftFile);
        Check(!loaded.Fresh && loaded.Step == SetupStep.Location && loaded.Draft.Python == python && loaded.Draft.SourceId == "mirror",
            "onboarding receipt persists import mode, location, source and interpreter");
        Check(configured.SourceId == "china" && configured.GitMirror == "",
            "editing and saving onboarding draft leaves active environment sources unchanged");

        var gate = new SetupReviewGate();
        Check(!gate.CanInstall && !gate.Complete(0, true), "installation is disabled before any review");
        var original = gate.Begin();
        Check(!gate.CanInstall && gate.Complete(original, true) && gate.CanInstall, "successful current review enables install");
        Check(!gate.Complete(original, false) && gate.CanInstall, "duplicate completion cannot overwrite an accepted review");
        gate.Invalidate();
        Check(!gate.CanInstall, "editing location invalidates successful review immediately");
        original = gate.Begin();
        gate.Invalidate();
        Check(!gate.Complete(original, true) && !gate.CanInstall, "source change rejects late success from previous review");
        original = gate.Begin();
        gate.Invalidate();
        Check(!gate.Complete(original, true) && !gate.CanInstall, "installation-mode change rejects late success from previous review");
        original = gate.Begin();
        var latest = gate.Begin();
        Check(!gate.Complete(original, true) && !gate.CanInstall, "overlapping older review cannot enable installation");
        Check(gate.Complete(latest, false) && !gate.CanInstall, "current failed review stays disabled");
        latest = gate.Begin();
        Check(gate.Complete(latest, true) && gate.CanInstall, "retry can authorize installation after a failed review");
        original = gate.Begin();
        latest = gate.Begin();
        Check(gate.Complete(latest, true) && !gate.Complete(original, false) && gate.CanInstall,
            "late failed review cannot replace a newer successful review");

        var events = new List<SetupProgress>();
        var legacyStages = new List<string>();
        var stageConfig = new LauncherConfig { ComfyRoot = comfy };
        using var offline = new Downloads(handler: new FakeHandler { Callback = _ => throw new Exception("No network expected in stage tests.") });
        var installer = new Installer(state, manifest, offline, _ => { }, null, legacyStages.Add, events.Add);
        var stage = typeof(Installer).GetMethod("Stage", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Task RunStage(string id, string label, Func<Task> action) =>
            (Task)stage.Invoke(installer, [stageConfig, id, label, action])!;

        foreach (var id in new[] { "tools", "comfy", "python", "torch", "dependencies", "nodes", "validation" })
        {
            events.Clear();
            await RunStage(id, "fixture " + id, () => Task.CompletedTask);
            Check(events.Select(e => e.Status).SequenceEqual([SetupStageStatus.Running, SetupStageStatus.Complete]) && events.All(e => e.StageId == id),
                $"stage {id} emits stable running and complete events");
        }
        Check(legacyStages.Count == 7 && legacyStages[^1] == "fixture validation", "typed progress preserves the legacy stage callback");
        var journal = JsonFile.Read<InstallJournal>(Path.Combine(state, "install-state.json"));
        Check(journal.Stage == "fixture validation" && journal.Status == "complete", "typed stage events retain the compatible completion journal");

        events.Clear();
        var failure = new IOException("isolated download failure");
        try { await RunStage("nodes", "fixture failure", () => Task.FromException(failure)); throw new Exception("Expected fixture failure."); }
        catch (IOException ex) { Check(ReferenceEquals(ex, failure), "stage failure preserves its original exception"); }
        Check(events.Select(e => e.Status).SequenceEqual([SetupStageStatus.Running, SetupStageStatus.Failed])
            && events[^1].Detail.Contains(failure.Message), "stage failure includes actionable details without claiming completion");
        Check(JsonFile.Read<InstallJournal>(Path.Combine(state, "install-state.json")).Status == "interrupted",
            "failed stage remains resumable in the existing installation journal");
        events.Clear();
        try { await RunStage("tools", "fixture pause", () => Task.FromCanceled(new CancellationToken(true))); throw new Exception("Expected fixture cancellation."); }
        catch (OperationCanceledException) { Check(true, "pausing a stage preserves cancellation for the install task"); }
        Check(events.Select(e => e.Status).SequenceEqual([SetupStageStatus.Running, SetupStageStatus.Paused]),
            "user pause is distinct from stage failure");
        events.Clear();
        await RunStage("nodes", "fixture node retry", () => Task.CompletedTask);
        await RunStage("nodes", "fixture next node", () => Task.CompletedTask);
        Check(events.Count == 4 && events.All(e => e.StageId == "nodes") && events[^1].Status == SetupStageStatus.Complete,
            "retry and multiple node substeps share a stable stage identity");
        return passed;
    }
}
