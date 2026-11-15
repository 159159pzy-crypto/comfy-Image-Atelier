using System.Text.Json;
using Anima.Launcher.Core;

internal static class ResourcePolicyTests
{
    public static async Task<int> Run(string work, RuntimeManifest runtime)
    {
        var count = 0;
        void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); count++; Console.WriteLine("PASS: " + label); }
        var basic = SetupFeatures.SelectRuntime(runtime, SetupFeatures.Resolve(false, []));
        Check(basic.Nodes.All(node => SetupFeatures.NodeFeature(node.Id) is null), "import selects only core nodes");
        Check(!RuntimeCapabilities.StartupClasses(runtime).Any(name => name.Contains("SeedVR2") || name.Contains("Sesqui") || name.Contains("DLSS") || name.Contains("RTX")), "startup does not require inactive enhancement nodes");
        Check(SetupFeatures.Resolve(true, []).SequenceEqual(["sesqui"]), "new default dual sampling retains Sesqui independently of repair switches");
        Check(!SetupFeatures.Resolve(true, [], "krea2").Contains("sesqui"), "Krea normal base sampling does not add an unused Sesqui dependency");
        Check(SetupFeatures.SelectRuntime(runtime, ["nvidia"]).Nodes.Any(node => node.Id == "nvidia-rtx-vsr-pro")
            && SetupFeatures.SelectRuntime(runtime, ["nvidia"]).Nodes.All(node => node.Id != "DLSS5-Comfyui"), "VSR selection never adds DLSS installation");
        var missingNodes = runtime.Nodes.Select(node => new NodeStatus(node.Id, "缺失", "")).ToArray();
        var baseInventory = ResourceInventoryPresentation.Classify(missingNodes, []);
        Check(baseInventory.Where(item => item.NeedsRepair).Select(item => item.Node.Name).ToHashSet()
            .SetEquals(basic.Nodes.Select(node => node.Id)), "optional and historical missing nodes never inflate the basic repair count");
        var selectedInventory = ResourceInventoryPresentation.Classify(missingNodes, ["sesqui", "nvidia"]);
        Check(selectedInventory.Single(item => item.Node.Name == "SesquiLSR").NeedsRepair
            && selectedInventory.Single(item => item.Node.Name == "nvidia-rtx-vsr-pro").NeedsRepair
            && !selectedInventory.Single(item => item.Node.Name == "DLSS5-Comfyui").NeedsRepair,
            "selected enhancements require repair independently without adding unselected DLSS");
        Check(baseInventory.Where(item => item.Purpose == ResourcePurpose.Legacy).Count() == 3
            && baseInventory.Where(item => item.Purpose is ResourcePurpose.Optional or ResourcePurpose.Legacy)
                .All(item => item.StateLabel.Contains("不影响基础使用")), "unused historical and optional resources have explanatory inventory states");
        Check(ResourceInventoryPresentation.Classify(missingNodes, ["legacy-detailers"])
                .Where(item => SetupFeatures.NodeFeature(item.Node.Name) == "legacy-detailers").All(item => item.NeedsRepair),
            "a legacy frozen plan still counts its explicitly required historical nodes");
        var keptLegacy = ResourceInventoryPresentation.Classify([new NodeStatus("comfyui-impact-pack", "已有安装，版本未验证", "")], []);
        Check(ResourceInventoryPresentation.Summary(keptLegacy).Contains("待补齐 0")
            && ResourceInventoryPresentation.Summary(keptLegacy).Contains("已保留 1"), "installed historical nodes are reported as retained without requesting repair");
        var nodeCoreCommit = new string('a', 40);
        var nodeDependencyPlan = new NodeUpdatePlan(work, Path.Combine(work, "python.exe"), nodeCoreCommit,
            [new("fixture", Path.Combine(work, "custom_nodes", "fixture"), "https://github.com/example/fixture", "main",
                new string('b', 40), new string('c', 40), true, "fixture")],
            new(nodeCoreCommit, "", "", ["fixture-package==1.0"]) { Resolved = true }, DateTimeOffset.UtcNow)
        { Sam2Commit = runtime.Sam2Commit };
        var restoredNodePlan = JsonSerializer.Deserialize<NodeUpdatePlan>(JsonSerializer.Serialize(nodeDependencyPlan, JsonFile.Options), JsonFile.Options)!;
        var newerCatalog = runtime with { Sam2Commit = new string('f', 40) };
        var frozenDependencyRuntime = NodeUpdateCoordinator.DependencyRuntime(restoredNodePlan, newerCatalog);
        Check(restoredNodePlan.CanApply && frozenDependencyRuntime.Sam2Commit == nodeDependencyPlan.Sam2Commit
            && frozenDependencyRuntime.Sam2Commit != newerCatalog.Sam2Commit
            && frozenDependencyRuntime.Comfy.Commit == nodeCoreCommit
            && frozenDependencyRuntime.PythonConstraints.SequenceEqual(["fixture-package==1.0"]),
            "node update restore freezes SAM2, core commit and package constraints instead of using a newer launcher catalog");
        var legacyNodeJson = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(nodeDependencyPlan, JsonFile.Options))!.AsObject();
        legacyNodeJson.Remove("sam2Commit");
        var legacyNodePlan = legacyNodeJson.Deserialize<NodeUpdatePlan>(JsonFile.Options)!;
        var legacyRuntimeRejected = false;
        try { NodeUpdateCoordinator.DependencyRuntime(legacyNodePlan, newerCatalog); }
        catch (InvalidOperationException error) { legacyRuntimeRejected = error.Message.Contains("重新固定节点依赖"); }
        Check(!legacyNodePlan.CanApply && legacyRuntimeRejected
            && legacyNodePlan.Entries.Single().TargetSha == nodeDependencyPlan.Entries.Single().TargetSha,
            "legacy node plans without a frozen SAM2 commit require dependency review while preserving selected node targets");
        var root = Path.Combine(work, "resource-policy");
        var config = new LauncherConfig { ComfyRoot = Path.Combine(root, "ComfyUI"), AppRoot = Path.Combine(root, "app"), RuntimeRoot = Path.Combine(root, "runtime") };
        Directory.CreateDirectory(config.ComfyRoot);
        await File.WriteAllTextAsync(Path.Combine(config.ComfyRoot, "main.py"), "# fixture\n");
        var source = Path.Combine(config.AppRoot, "anima_webui", "comfy_extension", "atelier_resources");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "__init__.py"), "# version one\n");
        var deferred = ResourceInspectionExtension.Ensure(config, true);
        Check(!deferred.Changed && !Directory.Exists(Path.Combine(config.ComfyRoot, "custom_nodes")), "running external ComfyUI is not modified to install a new extension");
        var installed = ResourceInspectionExtension.Ensure(config, false);
        var target = Path.Combine(config.ComfyRoot, "custom_nodes", ResourceInspectionExtension.DirectoryName, "__init__.py");
        Check(installed.Available && installed.Changed && File.ReadAllText(target) == "# version one\n", "bundled extension is copied and owned by a content receipt");
        Check(!ResourceInspectionExtension.Ensure(config, false).Changed, "identical extension installation is idempotent");
        await File.WriteAllTextAsync(Path.Combine(source, "__init__.py"), "# version two\n");
        Check(!ResourceInspectionExtension.Ensure(config, true).Changed && File.ReadAllText(target).Contains("one"), "running extension update waits for a normal restart");
        Check(ResourceInspectionExtension.Ensure(config, false).Changed && File.ReadAllText(target).Contains("two")
            && Directory.EnumerateDirectories(Path.Combine(config.ComfyRoot, ".anima-extension-backups")).Count() == 1,
            "managed extension update verifies old files and preserves a backup outside custom_nodes");
        await File.WriteAllTextAsync(target, "# user customization\n");
        Check(!ResourceInspectionExtension.Ensure(config, false).Changed && File.ReadAllText(target).Contains("customization"), "user modified extension is never overwritten");

        var plan = new InstallationPlan(JsonSerializer.Serialize(config, JsonFile.Options), config.ComfyRoot, config.RuntimeRoot, "anima", false, 0, 0, 0, [])
        { ResourcePolicyVersion = 1, RuntimeArtifactsIdentity = RuntimeArtifactPlanning.Identity };
        var installedPlan = plan with { Fresh = true, Features = ["sesqui", "nvidia"], ModelIds = ["seedvr2-model", "seedvr2-vae"] };
        var laterFeaturePlan = plan with { Features = ["prompt-assistant"] };
        var combinedSelection = ResourceInventoryPresentation.FeaturesForPlan(installedPlan, runtime, ["capability:nvidia"])
            .Concat(ResourceInventoryPresentation.FeaturesForPlan(laterFeaturePlan, runtime)).ToHashSet();
        Check(combinedSelection.SetEquals(["sesqui", "seedvr2", "prompt-assistant"]),
            "a later feature plan retains original fresh-install Sesqui and other selected resources while respecting skipped options");
        using (var ownerDownloads = new Downloads(handler: new FakeHandler { Callback = _ => throw new Exception("No inventory network requests expected.") }))
        await using (var ownerTasks = new DownloadTaskService(ownerDownloads, Path.Combine(root, "feature-owner-inventory")))
        {
            var ownerConfig = config.Clone();
            ownerConfig.Python = Path.Combine(config.ComfyRoot, ".venv", "Scripts", "python.exe");
            var featurePlan = laterFeaturePlan with
            {
                ConfigurationJson = JsonSerializer.Serialize(ownerConfig, JsonFile.Options),
                Purpose = FeaturePreparation.Purpose, RequestedChoices = ["prompt-assistant"],
                DependencyCheckpointId = Guid.NewGuid().ToString("N"),
            };
            DownloadOwnerRecord Register(LauncherConfig targetConfig, InstallationPlan targetPlan) => ownerTasks.RegisterOwner(
                "install", "prepared feature fixture", targetConfig, payload: JsonSerializer.SerializeToElement(targetPlan, JsonFile.Options));
            var featureOwner = Register(ownerConfig, featurePlan);
            DownloadOwnerRecord CopyOwner(DownloadTaskStatus status)
            {
                var copy = JsonSerializer.Deserialize<DownloadOwnerRecord>(JsonSerializer.Serialize(featureOwner, JsonFile.Options), JsonFile.Options)!;
                copy.Status = status;
                return copy;
            }
            foreach (var status in new[] { DownloadTaskStatus.Completed, DownloadTaskStatus.Paused, DownloadTaskStatus.Failed,
                         DownloadTaskStatus.Queued, DownloadTaskStatus.Running, DownloadTaskStatus.Pausing })
                Check(ResourceInventoryPresentation.FeaturesForOwners(ownerConfig, [CopyOwner(status)], runtime).SequenceEqual(["prompt-assistant"]),
                    "persisted confirmed feature owner retains selected scope after reload: " + status);
            var restoredFeatures = ResourceInventoryPresentation.FeaturesForOwners(ownerConfig, [CopyOwner(DownloadTaskStatus.Completed)], runtime);
            Check(ResourceInventoryPresentation.Classify(missingNodes, restoredFeatures)
                    .Single(item => item.Node.Name == "prompt-assistant").NeedsRepair,
                "a missing plugin selected after initial setup remains an actionable selected feature after restart");
            foreach (var status in new[] { DownloadTaskStatus.Canceled, DownloadTaskStatus.NeedsReview })
                Check(ResourceInventoryPresentation.FeaturesForOwners(ownerConfig, [CopyOwner(status)], runtime).Length == 0,
                    "unconfirmed or canceled feature ownership does not restore selection: " + status);
            var canceling = CopyOwner(DownloadTaskStatus.Pausing); canceling.CancellationRequested = true;
            Check(ResourceInventoryPresentation.FeaturesForOwners(ownerConfig, [canceling], runtime).Length == 0
                && ResourceInventoryPresentation.FeaturesForOwners(ownerConfig, [], runtime).Length == 0,
                "pending cancellation and an unregistered UI draft cannot establish selected features");
            var otherEnvironment = ownerConfig.Clone(); otherEnvironment.ComfyRoot = Path.Combine(root, "other-environment");
            var foreignOwner = Register(otherEnvironment, featurePlan with { ComfyRoot = otherEnvironment.ComfyRoot,
                ConfigurationJson = JsonSerializer.Serialize(otherEnvironment, JsonFile.Options) });
            var otherPython = ownerConfig.Clone(); otherPython.Python = Path.Combine(root, "other-python.exe");
            var otherPythonOwner = Register(otherPython, featurePlan with { ConfigurationJson = JsonSerializer.Serialize(otherPython, JsonFile.Options) });
            var mismatchedPayload = Register(ownerConfig, featurePlan with { ComfyRoot = otherEnvironment.ComfyRoot });
            Check(ResourceInventoryPresentation.FeaturesForOwners(ownerConfig, [foreignOwner, otherPythonOwner, mismatchedPayload], runtime).Length == 0,
                "owners from another root, interpreter, or mismatched frozen target never affect current inventory");
            var corrupt = CopyOwner(DownloadTaskStatus.Completed); corrupt.Fingerprint = new string('0', 64);
            var malformed = ownerTasks.RegisterOwner("install", "malformed feature fixture", ownerConfig,
                payload: JsonSerializer.SerializeToElement(new { purpose = FeaturePreparation.Purpose, modelIds = "not-an-array" }, JsonFile.Options));
            var logs = new List<string>();
            Check(ResourceInventoryPresentation.FeaturesForOwners(ownerConfig,
                    [corrupt, malformed, CopyOwner(DownloadTaskStatus.Completed)], runtime, logs.Add).SequenceEqual(["prompt-assistant"])
                && logs.Count == 2,
                "damaged feature records are logged and skipped without preventing healthy inventory results");
            var ordinaryOwner = ownerTasks.RegisterOwner("install", "ordinary install fixture", ownerConfig,
                payload: JsonSerializer.SerializeToElement(new { purpose = "environment", modelIds = "not-an-array" }, JsonFile.Options));
            var modelOwner = ownerTasks.RegisterOwner("models", "model-only fixture", ownerConfig,
                payload: JsonSerializer.SerializeToElement(featurePlan, JsonFile.Options));
            logs.Clear();
            Check(ResourceInventoryPresentation.FeaturesForOwners(ownerConfig, [ordinaryOwner, modelOwner], runtime, logs.Add).Length == 0
                && logs.Count == 0, "ordinary installation and model-download owners are not parsed as feature selections");
            var reselected = ResourceInventoryPresentation.FeaturesForPlan(installedPlan with { Features = ["sesqui", "prompt-assistant"] },
                    runtime, ["capability:prompt-assistant"])
                .Concat(ResourceInventoryPresentation.FeaturesForOwners(ownerConfig, [CopyOwner(DownloadTaskStatus.Completed)], runtime)).ToHashSet();
            Check(reselected.Contains("prompt-assistant"), "a later confirmed feature can reselect a capability explicitly skipped during onboarding");
        }
        using (var scopedDownloads = new Downloads())
        {
            var noSesquiRuntime = runtime with { Nodes = [] };
            await SesquiRuntimePreparation.PrepareAsync(config, noSesquiRuntime, config.RuntimeRoot, scopedDownloads, _ => { }, null, default);
            Check(!Directory.Exists(Path.Combine(config.RuntimeRoot, "cache")),
                "an update scope without Sesqui performs no weight download or cache writes");
            var missingSesquiRuntime = runtime with { Nodes = runtime.Nodes.Where(node => node.Id == "SesquiLSR").ToArray() };
            var issues = await SesquiRuntimePreparation.FilesIssuesAsync(config, missingSesquiRuntime, default);
            Check(issues.Length == 1 && issues[0].Contains("SesquiLSR"),
                "a reviewed Sesqui update still verifies its own missing weight and node");
        }
        var probes = 0;
        Task<RuntimeArtifactHost> Probe(LauncherConfig _, RuntimeManifest __, bool ___, CancellationToken ____) { probes++; return Task.FromResult(new RuntimeArtifactHost("cp311", "rtx40", "RTX 4090", "", true)); }
        var noFeatures = await RuntimeArtifactPlanning.PrepareAsync(plan, runtime, default, Probe);
        Check(noFeatures.FrozenRuntimeArtifacts.Length == 0 && probes == 0, "disabled enhancements have no runtime downloads or hardware probe");
        var vsr = await RuntimeArtifactPlanning.PrepareAsync(plan with { Features = ["nvidia"] }, runtime, default, Probe);
        var dlss = await RuntimeArtifactPlanning.PrepareAsync(plan with { Features = ["dlss"] }, runtime, default, Probe);
        var catalog = NvidiaRuntimePreparation.Catalog();
        Check(vsr.FrozenRuntimeArtifacts.Any(item => catalog.VfxWheels.Any(wheel => wheel.Sha256 == item.Sha256))
            && vsr.FrozenRuntimeArtifacts.All(item => item.Sha256 != catalog.DlssArchive.Sha256), "VSR budget contains VFX only");
        Check(dlss.FrozenRuntimeArtifacts.Any(item => item.Sha256 == catalog.DlssArchive.Sha256)
            && dlss.FrozenRuntimeArtifacts.All(item => catalog.VfxWheels.All(wheel => wheel.Sha256 != item.Sha256)), "DLSS budget does not require VFX");
        config.Python = Path.Combine(root, "python.exe");
        await File.WriteAllTextAsync(config.Python, "fixture interpreter");
        var interpreter = new FileInfo(config.Python);
        var stateKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            Path.GetFullPath(config.ComfyRoot).ToUpperInvariant())))[..24];
        var stateDirectory = Path.Combine(config.RuntimeRoot, "nvidia-runtime", stateKey);
        var previous = new NvidiaRuntimeStatus(true, true, "rtx40", "legacy verified")
        {
            ComfyRoot = Path.GetFullPath(config.ComfyRoot), Python = Path.GetFullPath(config.Python), Gpu = "RTX 4090",
            PythonIdentity = $"{Path.GetFullPath(config.Python)}|{interpreter.Length}|{interpreter.LastWriteTimeUtc.Ticks}"
        };
        JsonFile.Write(Path.Combine(stateDirectory, "status.json"), previous);
        Check(NvidiaRuntimePreparation.ReadStatus(config, config.RuntimeRoot, ["nvidia"]) is { Ready: true }
            && NvidiaRuntimePreparation.ReadStatus(config, config.RuntimeRoot, ["dlss"]) is { Ready: true },
            "legacy combined NVIDIA verification remains readable for each selected capability");
        JsonFile.Write(Path.Combine(stateDirectory, "status-nvidia.json"), previous with { Ready = false, Features = ["nvidia"] });
        Check(NvidiaRuntimePreparation.ReadStatus(config, config.RuntimeRoot, ["nvidia"]) is { Ready: false }
            && NvidiaRuntimePreparation.ReadStatus(config, config.RuntimeRoot, ["dlss"]) is { Ready: true },
            "a newer VSR failure is not hidden by old success and does not invalidate independent DLSS state");
        return count;
    }
}
