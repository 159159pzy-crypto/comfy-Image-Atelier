using System.Text.Json;
using Anima.Launcher.Core;

internal static class FeaturePreparationTests
{
    public static async Task<int> Run(string work, RuntimeManifest runtime, ModelManifest models, ModelProfilesManifest profiles)
    {
        var count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); count++; Console.WriteLine("PASS: " + message); }
        void Reject(Action action, string message)
        {
            try { action(); } catch (InvalidDataException) { Check(true, message); return; }
            throw new Exception("Expected rejection: " + message);
        }
        var root = Path.Combine(work, "feature-preparation");
        var config = new LauncherConfig { ComfyRoot = Path.Combine(root, "ComfyUI"), RuntimeRoot = Path.Combine(root, "runtime"),
            AppRoot = Path.Combine(root, "app"), SelectedProfile = "anima", SetupComplete = true };
        config.Python = Path.Combine(config.ComfyRoot, ".venv", "Scripts", "python.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(config.Python)!);
        await File.WriteAllTextAsync(config.Python, "fixture interpreter");
        await File.WriteAllTextAsync(Path.Combine(config.ComfyRoot, "main.py"), "# fixture");
        await File.WriteAllTextAsync(Path.Combine(config.ComfyRoot, "folder_paths.py"), "# fixture");
        var originalConfig = JsonSerializer.Serialize(config, JsonFile.Options);
        var plan = FeaturePreparation.Create(config, runtime, root, models, profiles, ["seedvr2-7b"]);
        FeaturePreparation.Validate(plan);
        var reviewed = plan with { DependencyReviewIssues = ["Pillow 12.3.0 不满足 Pillow>=13,<14", "外部依赖来源尚未验证"] };
        var restoredReview = JsonSerializer.Deserialize<InstallationPlan>(JsonSerializer.Serialize(reviewed, JsonFile.Options), JsonFile.Options)!;
        Check(FeaturePreparation.Describe(restoredReview).Contains("Pillow 12.3.0 不满足 Pillow>=13,<14")
            && FeaturePreparation.Describe(restoredReview).Contains("外部依赖来源尚未验证"),
            "feature confirmation retains exact missing and unknown dependency findings after persistence");
        Check(plan.Purpose == FeaturePreparation.Purpose && !plan.Fresh && plan.WorkbenchDependencyContractVersion == 1,
            "new feature preparation reuses import installation with the current workbench dependency contract");
        Check(plan.ModelIds.Order().SequenceEqual(new[] { "seedvr2-7b-sharp", "seedvr2-vae" }.Order())
            && !plan.ModelIds.Contains("seedvr2-model") && plan.MandatoryModelIds.Length == 0,
            "7B feature preparation freezes only selected 7B and its shared VAE without acquiring default 3B or base models");
        Check(originalConfig == JsonSerializer.Serialize(config, JsonFile.Options)
            && profiles.Get("anima").Seedvr2Model != models.Models.Single(model => model.Id == "seedvr2-7b-sharp").RelativePath,
            "feature selection preserves current configuration and the default SeedVR2 profile");
        var both = FeaturePreparation.Create(config, runtime, root, models, profiles, ["seedvr2", "seedvr2-7b"]);
        Check(both.ModelIds.Length == 3 && both.ModelIds.Count(id => id == "seedvr2-vae") == 1,
            "preparing 3B and 7B together deduplicates their shared VAE");
        var optional = FeaturePreparation.Create(config, runtime, root, models, profiles, ["prompt-assistant", "dlss"]);
        Check(optional.ModelIds.Length == 0 && optional.Features.Contains("prompt-assistant") && optional.Features.Contains("dlss")
            && optional.ResolvedRuntime!.Nodes.Any(node => node.Id == "prompt-assistant")
            && optional.ResolvedRuntime.Nodes.Any(node => node.Id == "DLSS5-Comfyui")
            && optional.ResolvedRuntime.Nodes.All(node => node.Id != "nvidia-rtx-vsr-pro"),
            "model-free feature choices prepare their own plugins without silently adding VSR");
        Reject(() => FeaturePreparation.Create(config, runtime, root, models, profiles, []), "empty feature selection cannot create an installation");
        Reject(() => FeaturePreparation.Create(config, runtime, root, models, profiles, ["unknown"]), "unknown feature choice cannot enter an installation");
        Reject(() => FeaturePreparation.Validate(plan with { FrozenModels = [] }), "missing frozen models cannot be borrowed from the current catalog");
        Reject(() => FeaturePreparation.Validate(plan with { DependencyCheckpointId = "" }), "feature retry requires a durable dependency operation identity");
        var checkpointPath = FeatureDependencyCheckpoint.Open(plan, root);
        var rehydrated = JsonSerializer.Deserialize<InstallationPlan>(JsonSerializer.Serialize(plan, JsonFile.Options), JsonFile.Options)!;
        Check(FeatureDependencyCheckpoint.Open(rehydrated, root) == checkpointPath,
            "restarted feature operation opens its original dependency checkpoint");
        var secondRequest = FeaturePreparation.Create(config, runtime, root, models, profiles, ["seedvr2-7b"]);
        Check(secondRequest.DependencyCheckpointId != plan.DependencyCheckpointId
            && FeatureDependencyCheckpoint.Open(secondRequest, root) != checkpointPath,
            "even identical feature selections in separate confirmed tasks cannot share mutable dependency checkpoints");
        Reject(() => FeatureDependencyCheckpoint.Open(plan with { Features = ["dlss"] }, root),
            "a changed feature scope cannot reuse the original task dependency checkpoint");
        var differentSource = config.Clone(); differentSource.SourceId = "official"; differentSource.ProxyAddress = "";
        Check(FeatureDependencyCheckpoint.Open(plan with { ConfigurationJson = JsonSerializer.Serialize(differentSource, JsonFile.Options),
            DependencyReviewIssues = ["new read-only findings"] }, root) == checkpointPath,
            "explicit source changes and transient review findings preserve the frozen dependency operation identity");
        await File.WriteAllTextAsync(Path.Combine(config.ComfyRoot, "requirements.txt"), "fixture-base>=1\n");
        await File.WriteAllTextAsync(Path.Combine(config.ComfyRoot, "manager_requirements.txt"), "");
        var fixedGraph = FeatureDependencyCheckpoint.SourceArchiveSnapshot("fixture-base>=1\n", "") with
        {
            Resolved = true, PlanVersion = 1, PythonIdentity = "fixture-python",
            EnvironmentPackages = ["torch==2.11.0", "fixture-base==1"],
            Constraints = ["torch==2.11.0", "fixture-base==1", "selected-feature==2"],
            Packages = [new DependencyPackage { Name = "selected-feature", Version = "2" }],
        };
        JsonFile.Write(checkpointPath, fixedGraph);
        var partiallyInstalled = new[] { "torch==2.11.0", "fixture-base==1", "selected-feature==2", "authorized-node-helper==1" };
        try { throw new OperationCanceledException("pause after selected node installer added its helper"); }
        catch (OperationCanceledException) { JsonFile.Write(checkpointPath, DependencyPlans.RecordAuthorizedAdditions(fixedGraph, partiallyInstalled)); }
        var restoredGraph = JsonFile.Read<ComfyDependencySnapshot>(FeatureDependencyCheckpoint.Open(rehydrated, root));
        DependencyPlans.ValidateEnvironment(restoredGraph, partiallyInstalled, installed: true);
        FeatureDependencyCheckpoint.ValidateInputs(restoredGraph, config, [], runtime.Sam2Commit);
        Check(restoredGraph.EnvironmentPackages.Contains("authorized-node-helper==1") && restoredGraph.Packages.Single().Version == "2"
            && restoredGraph.ComfySourceKind == "requirements",
            "a persisted cancellation checkpoint retains authorized installer additions and exact selected versions even for source archives");
        void RejectChanged(Action action, string message)
        {
            try { action(); } catch (InvalidOperationException) { Check(true, message); return; }
            throw new Exception("Expected frozen scope rejection: " + message);
        }
        RejectChanged(() => FeatureDependencyCheckpoint.ValidateInputs(restoredGraph with { ComfySourceKind = "git" }, config, [], runtime.Sam2Commit),
            "a Git checkpoint cannot silently resume against a source archive without its frozen repository identity");
        Check(DependencyInstallation.PlanIdentity(restoredGraph) != DependencyInstallation.PlanIdentity(restoredGraph with { ComfySourceKind = "git" }),
            "source archive plans cannot share dependency installation receipts with a Git plan");
        RejectChanged(() => DependencyPlans.ValidateEnvironment(restoredGraph, partiallyInstalled.Append("unapproved-after-pause==9")),
            "packages added outside the paused feature operation cannot be adopted as a new baseline");
        RejectChanged(() => DependencyPlans.ValidateEnvironment(restoredGraph, partiallyInstalled.Select(value => value == "selected-feature==2" ? "selected-feature==3" : value)),
            "a new available package version cannot replace a resumed feature's frozen target");
        await File.WriteAllTextAsync(Path.Combine(config.ComfyRoot, "requirements.txt"), "fixture-base>=2\n");
        RejectChanged(() => FeatureDependencyCheckpoint.ValidateInputs(restoredGraph, config, [], runtime.Sam2Commit),
            "changed requirement sources require a new feature request instead of silently resolving another graph");
        await File.WriteAllTextAsync(Path.Combine(config.ComfyRoot, "requirements.txt"), "fixture-base>=1\n");

        var payload = JsonSerializer.SerializeToElement(plan, JsonFile.Options);
        var owner = new DownloadOwnerRecord { Kind = "install", EnvironmentRoot = config.ComfyRoot, Configuration = config.Clone(), Payload = payload };
        Check(FeaturePreparation.IsFeatureOwner(owner) && !FeaturePreparation.IsFeatureOwner(new() { Kind = "models", Payload = payload })
            && !FeaturePreparation.IsFeatureOwner(new() { Kind = "install" }),
            "only explicitly reviewed installation owners use the full-feature coordinator; old model owners stay file-only");
        var changedCatalog = models with { Models = models.Models.Select(model => model with { Sha256 = new string('e', 64) }).ToArray() };
        var setup = new SetupRecord { Draft = config.Clone(), InstallFinished = true, Step = SetupStep.Finished,
            AssistantStatus = AssistantSetupStatus.Configured, ModelsPending = false };
        var setupBefore = JsonSerializer.Serialize(setup, JsonFile.Options);
        var recovered = DownloadOwnerRecovery.Prepare(owner, plan, setup, changedCatalog, profiles);
        Check(recovered.Plan.ModelIds.SequenceEqual(plan.ModelIds)
            && recovered.Plan.FrozenModels.Select(model => model.Sha256).SequenceEqual(plan.FrozenModels.Select(model => model.Sha256))
            && recovered.Plan.Purpose == FeaturePreparation.Purpose && setupBefore == JsonSerializer.Serialize(setup, JsonFile.Options),
            "feature recovery retains its frozen resources and cannot rewrite the completed onboarding record");
        var legacyJson = JsonSerializer.SerializeToNode(plan, JsonFile.Options)!.AsObject();
        legacyJson.Remove("purpose"); legacyJson.Remove("requestedChoices"); legacyJson.Remove("workbenchDependencyContractVersion");
        var legacy = legacyJson.Deserialize<InstallationPlan>(JsonFile.Options)!;
        Check(!FeaturePreparation.IsFeaturePlan(legacy) && legacy.WorkbenchDependencyContractVersion == 0,
            "historical plans retain the environment path and do not acquire a new dependency contract");
        var configurationOnly = new SetupRuntimeReadinessReport([], [])
            { ConfigurationIssues = [new("base", "pick a compatible base"), new("seedvr2", "selected repair runtime unavailable")] };
        Check(FeaturePreparation.BlockingIssues(plan, configurationOnly).Select(issue => issue.Feature).SequenceEqual(["seedvr2"]),
            "selected feature failures block completion while an unrelated unconfigured base remains configurable");
        Check(FeaturePreparation.BlockingIssues(plan, new([], [new("optional", "node import failed")])).Length == 1,
            "a selected optional-node failure cannot be reported as completed full-feature preparation");
        using var idleQueue = JsonDocument.Parse("{\"queue_running\":[],\"queue_pending\":[]}");
        using var queued = JsonDocument.Parse("{\"queue_running\":[],\"queue_pending\":[[1,\"job\"]]}");
        using var idleWorkbench = JsonDocument.Parse("{\"queue\":[],\"batch\":null}");
        using var busyWorkbench = JsonDocument.Parse("{\"queue\":[],\"batch\":{\"status\":\"running\"}}");
        Check(FeaturePreparation.RunningServicesIssue(false, null, false, null).Length == 0,
            "stopped services do not require a queue endpoint before feature preparation");
        Check(FeaturePreparation.RunningServicesIssue(true, queued.RootElement, true, idleWorkbench.RootElement).Contains("等待的任务"),
            "pending ComfyUI jobs block dependency installation even if no image is currently running");
        Check(FeaturePreparation.RunningServicesIssue(true, idleQueue.RootElement, true, busyWorkbench.RootElement).Contains("工作台仍有任务"),
            "an active workbench batch blocks feature installation despite an empty ComfyUI queue");
        Check(FeaturePreparation.RunningServicesIssue(true, null, false, null).Contains("无法确认")
            && FeaturePreparation.RunningServicesIssue(false, null, true, null).Contains("无法确认"),
            "unknown queue or workbench status never permits environment mutation");
        Check(FeaturePreparation.RunningServicesIssue(true, idleQueue.RootElement, true, idleWorkbench.RootElement).Contains("先在概览停止"),
            "idle running services require an explicit stop and are never silently terminated");

        var state = Path.Combine(root, "download-state");
        using (var downloads = new Downloads())
        await using (var service = new DownloadTaskService(downloads, state))
        {
            owner = service.RegisterOwner("install", "feature fixture", config, payload: payload);
            try { await service.RunOwnerAsync(owner.Id, _ => throw new OperationCanceledException()); }
            catch (OperationCanceledException) { }
            Check(service.Owners.Single().Status == DownloadTaskStatus.Paused, "feature preparation interruption is persisted as paused");
        }
        using (var downloads = new Downloads())
        await using (var service = new DownloadTaskService(downloads, state))
        {
            var restored = service.Owners.Single();
            Check(restored.Status == DownloadTaskStatus.Paused && FeaturePreparation.IsFeatureOwner(restored)
                && restored.Payload!.Value.Deserialize<InstallationPlan>(JsonFile.Options)!.ModelIds.SequenceEqual(plan.ModelIds),
                "restart recovers the same feature plan without automatically running it");
            try { await service.RunOwnerAsync(restored.Id, _ => throw new IOException("runtime verification failed")); }
            catch (IOException) { }
            Check(service.Owners.Single().Status == DownloadTaskStatus.Failed, "failed final runtime verification leaves feature owner incomplete");
            await service.RunOwnerAsync(restored.Id, _ => Task.CompletedTask);
            Check(service.Owners.Single().Status == DownloadTaskStatus.Completed, "feature owner completes only after the coordinator returns from successful final checks");
        }
        return count;
    }
}
