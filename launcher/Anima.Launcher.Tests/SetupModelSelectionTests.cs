using System.Security.Cryptography;
using System.Text.Json;
using Anima.Launcher.Core;

internal static class SetupModelSelectionTests
{
    public static async Task<int> Run(string work, RuntimeManifest runtime, ModelManifest models, ModelProfilesManifest profiles)
    {
        var count = 0;
        void Check(bool value, string label)
        { if (!value) throw new Exception("FAIL: " + label); count++; Console.WriteLine("PASS: " + label); }
        async Task Fails(Func<Task> action, string label)
        { try { await action(); } catch (InvalidDataException) { Check(true, label); return; } throw new Exception("Expected InvalidDataException: " + label); }

        var choices = SetupModelSelection.Choices(models, profiles, "anima");
        Check(choices.Length == 10 && choices.Count(choice => choice.ModelIds.Length == 2) == 1
            && choices.Single(choice => choice.Id == "face-refine").ModelIds.Length == 3 && choices.All(choice => choice.Id != "seedvr2"),
            "onboarding offers Krea, the complete face model set and eight taggers while SeedVR2 is mandatory");
        Check(choices.Single(choice => choice.Recommended).Id == "tagger-pixai-tagger-v0.9-timm",
            "tagger recommendation is metadata, not an automatic selection");
        Check(profiles.RequiredModels(models, "anima").Length == 3
            && SetupModelSelection.Resolve(models, profiles, "anima", []).Select(model => model.Id).ToHashSet()
                .SetEquals(["anima-base-v1", "qwen-text", "qwen-vae", "seedvr2-model", "seedvr2-vae"]),
            "empty optional selection adds SeedVR2 without changing the Anima generation profile contract");
        var selected = choices.Where(choice => choice.Id == "krea2").SelectMany(choice => choice.ModelIds).ToArray();
        Check(SetupModelSelection.Resolve(models, profiles, "anima", selected.Concat(["qwen-vae", "krea2-turbo"])).Length == 7,
            "feature union deduplicates repeated choices and the shared Qwen VAE");
        Check(SetupModelSelection.Choices(models, profiles, "krea2").All(choice => choice.Id != "krea2")
            && SetupModelSelection.Resolve(models, profiles, "krea2", []).Length == 5,
            "current Krea profile remains required and cannot be deselected as an optional feature");
        var face = choices.Single(choice => choice.Id == "face-refine");
        Check(!face.Recommended && face.Size == 76326352 && face.ModelIds.All(id => models.Models.Single(model => model.Id == id).CanDownload)
            && SetupModelSelection.Resolve(models, profiles, "anima", face.ModelIds).Length == 8
            && new SetupRecord().OptionalModelIds.Length == 0,
            "face defaults have locked downloads and remain unselected until the user opts in");
        var skippedKrea = SetupModelSelection.ExpandSkippedOptionalIds(models, profiles, "anima", ["krea2-turbo"]);
        Check(skippedKrea.ToHashSet().SetEquals(["krea2-turbo", "qwen3vl-krea2"])
            && !SetupModelSelection.ResolveIds(models, profiles, "anima", selected.Except(skippedKrea))
                .Any(id => selected.Contains(id)),
            "skipping a failed Krea model also deselects its encoder so startup cannot require the skipped profile again");
        Check(SetupModelSelection.ExpandSkippedOptionalIds(models, profiles, "anima", ["forbidden-vision-detect"])
                .ToHashSet().SetEquals(face.ModelIds)
            && SetupModelSelection.ExpandSkippedOptionalIds(models, profiles, "anima", ["tagger-pixai-tagger-v0.9-timm"])
                .SequenceEqual(["tagger-pixai-tagger-v0.9-timm"]),
            "face failures skip the whole feature while independent tagger choices stay independent");
        Check(SetupModelSelection.ExpandSkippedOptionalIds(models, profiles, "krea2", ["krea2-turbo", "qwen3vl-krea2", "qwen-vae", "seedvr2-model", "seedvr2-vae"]).Length == 0,
            "skip expansion cannot remove the active generation profile or mandatory SeedVR2 models");
        await Fails(() => Task.FromResult(SetupModelSelection.Resolve(models, profiles, "anima", ["removed-model"])),
            "stale saved model selection requires review instead of silently dropping a choice");

        var directory = Path.Combine(work, "setup-model-selection");
        var comfy = Path.Combine(directory, "ComfyUI");
        var python = Path.Combine(comfy, ".venv", "Scripts", "python.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(python)!);
        File.WriteAllText(Path.Combine(comfy, "main.py"), "# fixture");
        File.WriteAllText(Path.Combine(comfy, "folder_paths.py"), "# fixture");
        File.WriteAllText(python, "");
        var config = new LauncherConfig { ComfyRoot = comfy, Python = python, RuntimeRoot = Path.Combine(directory, "runtime") };
        var fixtureRuntime = runtime with { Nodes = [], Tools = [], TorchWheels = [] };
        var bytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var sha = Convert.ToHexString(SHA256.HashData(bytes));
        var tagger = models.Models.Single(model => model.Recommended);
        var tiny = new ModelManifest(1, models.Models.Select(model => model.IsPackage
            ? model with { Size = 2 * bytes.Length, Files = [new("config.json", bytes.Length, sha, ["https://example.com/config.json"]),
                new("model.safetensors", bytes.Length, sha, ["https://example.com/model.safetensors"])] }
            : model with { Size = bytes.Length, Sha256 = sha, Urls = ["https://example.com/model"] }).ToArray());
        var legacy = InstallationPlanner.Create(config, fixtureRuntime, directory, false, tiny, profiles);
        Check(legacy.ModelIds.IsEmpty && legacy.Downloads.IsEmpty,
            "legacy import callers retain the existing nodes-only behavior");
        var optional = new[] { tagger.Id };
        var plan = InstallationPlanner.Create(config, fixtureRuntime, directory, false, tiny, profiles, optional);
        optional[0] = "changed-after-review";
        Check(plan.ModelIds.Length == 6 && plan.MandatoryModelIds.Length == 5 && plan.ModelIds.Contains(tagger.Id) && !plan.ModelIds.Contains(optional[0]),
            "explicit import selection snapshots required and optional IDs independently of UI edits");
        Check(plan.ModelPathsDeferred && plan.DeferredModelBytes == 2 * bytes.Length
            && plan.Downloads.Length == 5 && plan.Downloads.All(item => !item.Destination.Contains("wd_taggers")),
            "unknown package target reserves bytes without inventing a download directory");
        var fresh = InstallationPlanner.Create(new LauncherConfig { InstallRoot = Path.Combine(directory, "not-installed") },
            fixtureRuntime, directory, true, tiny, profiles, [tagger.Id]);
        var freshPrepared = await InstallationPlanner.PreparePlanAsync(fresh, tiny, profiles, "not-needed-before-install.py", default);
        Check(freshPrepared.ModelPathsDeferred && freshPrepared.ModelInventory.Length == 18
            && freshPrepared.ModelInventory.Single(item => item.Model.Id == tagger.Id).Destination.Length == 0,
            "fresh setup previews all model choices before Python exists without guessing package targets");
        await Fails(() => InstallationPlanner.PreparePlanAsync(fresh, tiny, profiles, "not-installed.py", default, true),
            "post-install model phase requires a real path probe before any download");
        var noTaggerProbe = new ProbeResult(comfy, python, new());
        await Fails(() => InstallationPlanner.ResolveModelDestinationsAsync(plan, noTaggerProbe, tiny, profiles, default),
            "missing runtime package registration blocks download destination resolution");
        var deferred = await InstallationPlanner.ResolveModelDestinationsAsync(plan, noTaggerProbe, tiny, profiles, default, true);
        Check(deferred.ModelPathsDeferred && deferred.ModelInventory.Single(item => item.Model.Id == tagger.Id).Destination == "",
            "pre-install deferred package stays visible with an unconfirmed destination");
        var external = Path.Combine(directory, "external-models");
        var probe = new ProbeResult(comfy, python, new()
        {
            ["diffusion_models"] = [Path.Combine(external, "diffusion")],
            ["text_encoders"] = [Path.Combine(external, "text")],
            ["vae"] = [Path.Combine(external, "vae")],
            ["wd_taggers"] = [Path.Combine(external, "taggers")],
            ["forbidden_vision"] = [Path.Combine(external, "faces")],
        });
        var resolved = await InstallationPlanner.ResolveModelDestinationsAsync(plan, probe, tiny, profiles, default);
        var requiredOnly = InstallationPlanner.Create(config, fixtureRuntime, directory, false, tiny, profiles, []);
        var requiredPreview = await InstallationPlanner.ResolveModelDestinationsAsync(requiredOnly, probe, tiny, profiles, default);
        Check(requiredPreview.ModelInventory.Length == 18 && requiredPreview.ModelIds.Length == 5
            && requiredPreview.Downloads.Length == 5 && requiredPreview.EstimatedDownloadBytes == 5 * bytes.Length
            && requiredPreview.ModelInventory.Single(item => item.Model.Id == "seedvr2-model").Destination.StartsWith(external)
            && requiredPreview.ModelInventory.Single(item => item.Model.Id == tagger.Id).Destination.StartsWith(external),
            "unchecked optional choices show actual status and paths without entering selected downloads or byte totals");
        var optionalModel = tiny.Models.Single(item => item.Id == "krea2-turbo");
        var optionalPath = requiredPreview.ModelInventory.Single(item => item.Model.Id == optionalModel.Id).Destination;
        Directory.CreateDirectory(Path.GetDirectoryName(optionalPath)!);
        File.WriteAllBytes(optionalPath, bytes[..2]);
        var previewDamaged = await InstallationPlanner.ResolveModelDestinationsAsync(requiredOnly, probe, tiny, profiles, default);
        var previewPreflight = await InstallationPlanner.PreflightAsync(previewDamaged, default);
        Check(previewDamaged.ModelInventory.Single(item => item.Model.Id == optionalModel.Id).State == "大小异常"
            && previewPreflight.CanInstall && previewPreflight.Issues.All(issue => issue.Code != "cache_invalid")
            && InstallationPlanner.EstimateVolumes(previewDamaged).Select(item => (item.Volume, item.RequiredBytes))
                .SequenceEqual(InstallationPlanner.EstimateVolumes(requiredPreview).Select(item => (item.Volume, item.RequiredBytes)))
            && File.ReadAllBytes(optionalPath).SequenceEqual(bytes[..2]),
            "damaged unchecked model stays visible and untouched without changing disk budgets or blocking preflight");
        File.WriteAllBytes(optionalPath, bytes);
        var previewExisting = await InstallationPlanner.ResolveModelDestinationsAsync(requiredOnly, probe, tiny, profiles, default);
        Check(previewExisting.ModelInventory.Single(item => item.Model.Id == optionalModel.Id).State == "已存在"
            && previewExisting.ModelInventory.Single(item => item.Model.Id == optionalModel.Id).FoundPath == optionalPath,
            "unchecked installed models are discovered before the user selects them");
        var uncertainProbe = probe with { Paths = new(probe.Paths) { ["wd_taggers"] = ["\0"] } };
        var previewUncertain = await InstallationPlanner.ResolveModelDestinationsAsync(requiredOnly, uncertainProbe, tiny, profiles, default);
        Check(previewUncertain.ModelInventory.Where(item => item.Model.Category == "wd_taggers")
                .All(item => item.State == "目录待确认" && item.Destination.Length == 0)
            && !previewUncertain.ModelPathsDeferred && previewUncertain.DeferredModelBytes == 0
            && previewUncertain.Downloads.SequenceEqual(requiredPreview.Downloads)
            && previewUncertain.EstimatedDownloadBytes == requiredPreview.EstimatedDownloadBytes,
            "unreadable unchecked package paths stay pending without changing selected artifacts or adding deferred reserves");
        var unregisteredPreview = await InstallationPlanner.ResolveModelDestinationsAsync(requiredOnly, noTaggerProbe, tiny, profiles, default);
        Check(unregisteredPreview.ModelInventory.Where(item => item.Model.Category == "wd_taggers")
                .All(item => item.State == "目录待确认" && item.Destination.Length == 0)
            && !unregisteredPreview.ModelPathsDeferred && unregisteredPreview.ModelIds.SequenceEqual(requiredOnly.ModelIds),
            "unregistered unchecked taggers do not require package paths for the selected plan");
        var package = resolved.ModelInventory.Single(item => item.Model.Id == tagger.Id);
        var staging = ModelPackages.Staging(package.Model, package.Destination);
        Check(!resolved.ModelPathsDeferred && resolved.DeferredModelBytes == 0 && resolved.Downloads.Length == 7
            && resolved.Downloads.Count(item => item.Destination.StartsWith(staging)) == 2
            && resolved.ModelInventory.All(item => item.Destination.StartsWith(external)),
            "actual model paths and package staging files replace provisional download and volume estimates");
        Directory.CreateDirectory(staging);
        var stagedFile = package.Model.Files[0];
        var partial = Path.Combine(staging, stagedFile.Path) + ".part";
        File.WriteAllBytes(partial, bytes[..3]);
        JsonFile.Write(partial + ".json", new ResumeInfo(stagedFile.Sha256, stagedFile.Size, stagedFile.Urls[0], null));
        var resumed = await InstallationPlanner.RefreshSpaceEstimateAsync(resolved, default);
        Check(resumed.EstimatedDownloadBytes == 7 * bytes.Length - 3,
            "package partial downloads reduce the remaining batch estimate by actual staged bytes");
        var corruptStagingFile = Path.Combine(staging, stagedFile.Path);
        var corrupt = new byte[bytes.Length];
        File.WriteAllBytes(corruptStagingFile, corrupt);
        var repairable = await InstallationPlanner.ResolveModelDestinationsAsync(resolved, probe, tiny, profiles, default);
        var replacement = repairable.Downloads.Single(item => item.Destination == corruptStagingFile);
        Check(replacement.RepairableStaging && replacement.RequiresReplacement && replacement.RemainingBytes == bytes.Length
            && repairable.EstimatedDownloadBytes == 7 * bytes.Length,
            "same-size corrupt package staging reserves a full replacement while preserving its original allocation");
        var repairPreflight = await InstallationPlanner.PreflightAsync(repairable, default);
        Check(repairPreflight.CanInstall && repairPreflight.Issues.All(issue => issue.Code != "cache_invalid")
            && repairPreflight.Volumes.Single().RequiredBytes == repairable.EnvironmentReserveBytes + repairable.RuntimeReserveBytes
                + 7 * bytes.Length + 256L * 1024 * 1024
            && File.ReadAllBytes(corruptStagingFile).SequenceEqual(corrupt),
            "repairable staged corruption passes preflight with full disk reserve and no mutation of the damaged file");
        var finalSingle = repairable.Downloads.First(item => !item.RepairableStaging);
        Directory.CreateDirectory(Path.GetDirectoryName(finalSingle.Destination)!);
        File.WriteAllBytes(finalSingle.Destination, corrupt);
        var protectedPreflight = await InstallationPlanner.PreflightAsync(repairable, default);
        Check(!protectedPreflight.CanInstall && protectedPreflight.Issues.Any(issue => issue.Code == "cache_invalid")
            && File.ReadAllBytes(finalSingle.Destination).SequenceEqual(corrupt),
            "repairable package staging does not authorize replacing corrupt installed single-file models");
        File.Delete(finalSingle.Destination);
        File.WriteAllBytes(corruptStagingFile, bytes);
        var repairedEstimate = await InstallationPlanner.RefreshSpaceEstimateAsync(repairable, default);
        Check(!repairedEstimate.Downloads.Single(item => item.Destination == corruptStagingFile).RequiresReplacement
            && repairedEstimate.EstimatedDownloadBytes == 6 * bytes.Length,
            "refresh clears replacement reserve once the staged file actually verifies");
        Directory.CreateDirectory(package.Destination);
        foreach (var file in package.Model.Files) File.WriteAllBytes(Path.Combine(package.Destination, file.Path), bytes);
        var installed = await InstallationPlanner.ResolveModelDestinationsAsync(resolved, probe, tiny, profiles, default);
        Check(installed.ModelInventory.Single(item => item.Model.Id == tagger.Id).State == "已校验"
            && installed.Downloads.Length == 5 && installed.EstimatedDownloadBytes == 5 * bytes.Length,
            "verified installed packages are reused without staging a second copy");
        await Fails(() => InstallationPlanner.ResolveModelDestinationsAsync(plan, probe with { Root = external }, tiny, profiles, default),
            "model paths reported for a different ComfyUI root are rejected");

        var old = JsonSerializer.Deserialize<SetupRecord>("{\"schemaVersion\":1,\"step\":4}", JsonFile.Options)!;
        Check(old.OptionalModelIds.Length == 0 && !old.ModelsPending && old.AssistantStatus == AssistantSetupStatus.Legacy
            && (int)SetupStep.Complete == 4 && (int)SetupStep.Finished == 5 && (int)SetupStep.Assistant == 6,
            "old setup receipts and numeric steps remain compatible");
        var saved = new SetupRecord { Fresh = false, Draft = config.Clone(), InstallStarted = true, InstallFinished = true,
            ModelsPending = true, Step = SetupStep.Installing, AssistantStatus = AssistantSetupStatus.Pending };
        Check(SetupRouting.Resolve(config, saved) == SetupStep.Installing,
            "interrupted model-only phase resumes without reinstalling completed dependencies");
        saved.ModelsPending = false;
        Check(SetupRouting.Resolve(config, saved) == SetupStep.Assistant,
            "new setup receipt routes to pending supplier configuration after models finish");
        saved.AssistantStatus = AssistantSetupStatus.Skipped;
        Check(SetupRouting.Resolve(config, saved) == SetupStep.Complete,
            "skipped supplier setup does not block the completed local environment");
        saved.AssistantStatus = AssistantSetupStatus.Configured;
        Check(SetupRouting.Resolve(config, saved) == SetupStep.Complete,
            "configured supplier setup reaches completion");
        return count;
    }
}
