using System.Text.Json;
using Anima.Launcher.Core;

internal static class DownloadOwnerRecoveryTests
{
    public static int Run(string work)
    {
        var count = 0;
        void Check(bool condition, string label) { if (!condition) throw new Exception("FAIL: " + label); count++; Console.WriteLine("PASS: " + label); }
        void Fails(Action action, string label)
        {
            try { action(); }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException) { Check(true, label); return; }
            throw new Exception("Expected rejection: " + label);
        }
        var root = Path.Combine(work, "owner-recovery");
        var config = new LauncherConfig
        {
            ComfyRoot = Path.Combine(root, "A", "ComfyUI"), InstallRoot = Path.Combine(root, "A"),
            RuntimeRoot = Path.Combine(root, "runtime"), Python = Path.Combine(root, "A", "planned-python.exe"),
            InstallPending = true, SourceId = "official", NetworkMode = "direct"
        };
        ModelItem Model(string id, string category) => new(id, id, "fixture", category, id + ".bin", [], 10,
            new string('a', 64), ["https://example.com/" + id], "fixture", "");
        var frozenModels = new[] { Model("diffusion", "diffusion_models"), Model("clip", "text_encoders"), Model("vae", "vae"), Model("optional", "loras") };
        var profile = new ModelProfileDefinition("anima", "Frozen profile", "diffusion.bin", "clip.bin", "vae.bin", "fixed", "", "");
        var plan = new InstallationPlan(JsonSerializer.Serialize(config, JsonFile.Options), config.ComfyRoot, config.RuntimeRoot,
            "anima", true, 0, 0, 0, [])
        {
            Release = new("v1", new string('a', 40), null, "https://github.com/Comfy-Org/ComfyUI/releases/tag/v1"),
            ModelIds = ["diffusion", "clip", "vae", "optional"], FrozenModels = frozenModels, FrozenProfile = profile
        };
        var owner = new DownloadOwnerRecord { Kind = "setup", EnvironmentRoot = config.ComfyRoot, Configuration = config.Clone() };
        owner.Configuration.SourceId = "custom"; owner.Configuration.ModelMirror = "https://mirror.example";
        owner.Configuration.GitMirror = "https://git.example/{url}"; owner.Configuration.PipIndex = "https://pip.example/simple";
        owner.Configuration.NetworkMode = "custom"; owner.Configuration.ProxyAddress = "http://127.0.0.1:7890";
        var currentModels = new ModelManifest(1, frozenModels.Select(m => m with { Sha256 = new string('b', 64), Urls = ["https://new.example/" + m.Id] }).ToArray());
        var currentProfiles = new ModelProfilesManifest(1, "anima", [profile with { ModelName = "future.bin", ClipType = "changed" }]);
        DownloadOwnerRecoveryResult Prepare(SetupRecord? saved = null, string? commit = null, InstallationPlan? fixedPlan = null) =>
            DownloadOwnerRecovery.Prepare(owner, fixedPlan ?? plan, saved, currentModels, currentProfiles, commit);

        var unrelated = new SetupRecord
        {
            Fresh = true, Draft = config.Clone(), InstallStarted = true, InstallFinished = true, ModelsPending = true,
            Step = SetupStep.Complete, AssistantStatus = AssistantSetupStatus.Configured,
            ReviewedPlan = plan with { Release = plan.Release with { Commit = new string('b', 40) } }
        };
        unrelated.Draft.ComfyRoot = Path.Combine(root, "B", "ComfyUI"); unrelated.Draft.InstallRoot = Path.Combine(root, "B");
        unrelated.Draft.Python = Path.Combine(root, "B", "python.exe");
        var reset = Prepare(unrelated);
        Check(reset.ResetSetup && !reset.ReusesInstalledEnvironment && !reset.Setup.InstallFinished && !reset.Setup.ModelsPending,
            "owner for environment A resets global completion receipt for environment B");
        Check(reset.Configuration.ComfyRoot == config.ComfyRoot && reset.Configuration.Python == config.Python
            && reset.Setup.AssistantStatus == AssistantSetupStatus.Pending,
            "environment A never reuses environment B interpreter or assistant completion");
        Check(unrelated.InstallFinished && unrelated.Draft.ComfyRoot.EndsWith(Path.Combine("B", "ComfyUI")),
            "recovery decisions do not mutate the caller's unrelated setup receipt");

        var installed = new SetupRecord
        {
            Fresh = true, Draft = config.Clone(), InstallStarted = true, InstallFinished = true,
            ModelsPending = true, ReviewedPlan = plan, AssistantStatus = AssistantSetupStatus.Skipped
        };
        installed.Draft.Python = Path.Combine(root, "A", "installed-python.exe");
        installed.Draft.PythonExplicit = true; installed.Draft.ManagedComfy = true;
        var resumed = Prepare(installed);
        Check(!resumed.ResetSetup && resumed.ReusesInstalledEnvironment && resumed.Setup.InstallFinished
            && resumed.Configuration.Python == installed.Draft.Python && resumed.Configuration.PythonExplicit
            && resumed.Configuration.ManagedComfy && !resumed.Configuration.InstallPending,
            "same-environment recovery preserves the installed interpreter and installation results");
        Check(resumed.Configuration.SourceId == "custom" && resumed.Configuration.ModelMirror == owner.Configuration.ModelMirror
            && resumed.Configuration.GitMirror == owner.Configuration.GitMirror && resumed.Configuration.PipIndex == owner.Configuration.PipIndex
            && resumed.Configuration.NetworkMode == "custom" && resumed.Configuration.ProxyAddress == owner.Configuration.ProxyAddress,
            "same-environment recovery takes all six transport choices from the resumed owner");
        Check(resumed.Plan.Release!.Commit == plan.Release!.Commit && resumed.Plan.CreateConfig().Python == installed.Draft.Python
            && plan.CreateConfig().Python == config.Python && plan.CreateConfig().SourceId == "official",
            "effective recovery plan retains its release and leaves the original reviewed plan unchanged");
        Check(resumed.Models.Models.Single(m => m.Id == "diffusion").Sha256 == new string('a', 64)
            && resumed.Profiles.Get("anima").ModelName == "diffusion.bin" && resumed.Profiles.Get("anima").ClipType == "fixed"
            && resumed.Setup.OptionalModelIds.SequenceEqual(["optional"]),
            "frozen model hashes and profile determine restored requirements despite current manifest changes");
        resumed.Models.Models[0].Urls[0] = "https://mutated.example/file";
        Check(plan.FrozenModels[0].Urls[0] == "https://example.com/diffusion", "recovery outputs cannot mutate the saved model snapshot");

        var changedRelease = JsonSerializer.Deserialize<SetupRecord>(JsonSerializer.Serialize(installed, JsonFile.Options), JsonFile.Options)!;
        changedRelease.ReviewedPlan = plan with { Release = plan.Release with { Commit = new string('b', 40) } };
        Fails(() => Prepare(changedRelease), "completed same-environment receipt for another release refuses the old plan");
        Fails(() => Prepare(installed, new string('b', 40)), "actual installed revision takes precedence over an older matching receipt");
        Fails(() => Prepare(unrelated, new string('b', 40)), "actual target revision conflict is rejected even when global setup belongs elsewhere");
        Check(Prepare(installed, new string('a', 40)).ReusesInstalledEnvironment, "matching installed revision permits the frozen installation to continue");
        Check(Prepare(installed, new string('b', 40), plan with { Fresh = false, Release = null }).ReusesInstalledEnvironment,
            "import and repair plans without a pinned release preserve an existing core version");
        Fails(() => Prepare(fixedPlan: plan with { FrozenModels = [] }), "recovery never fills a missing frozen model snapshot from a newer catalog");
        Fails(() => Prepare(fixedPlan: plan with { FrozenProfile = null }), "recovery never fills a missing frozen profile from a newer catalog");
        var legacy = plan with
        {
            FrozenModels = [], FrozenProfile = null,
            ModelInventory = [.. frozenModels.Select(model => new ModelStatus(model, "缺失", null, Path.Combine(config.ComfyRoot, "models", model.RelativePath)))]
        };
        var compatibleProfiles = new ModelProfilesManifest(1, "anima", [profile]);
        var migrated = DownloadOwnerRecovery.Prepare(owner, legacy, installed, currentModels, compatibleProfiles);
        Check(migrated.Plan.FrozenModels.Length == 4 && migrated.Plan.FrozenModels.All(model => model.Sha256 == new string('a', 64))
            && migrated.Plan.FrozenProfile == profile && migrated.Setup.OptionalModelIds.SequenceEqual(["optional"]),
            "legacy inventory migrates exact saved model identities when the profile requires only those filenames");
        Fails(() => Prepare(fixedPlan: legacy), "legacy inventory refuses a current profile requiring a different model filename");
        Fails(() => DownloadOwnerRecovery.Prepare(owner, legacy with { ModelInventory = [.. legacy.ModelInventory.Take(2)] },
            installed, currentModels, compatibleProfiles), "legacy inventory missing a selected model cannot use a new catalog as a substitute");
        Fails(() => Prepare(fixedPlan: plan with { FrozenModels = frozenModels.Where(m => m.Id != "vae").ToArray() }),
            "incomplete frozen requirements are rejected before installation or model download");
        var partial = new SetupRecord { Fresh = true, Draft = installed.Draft, InstallStarted = true, InstallFinished = false, ReviewedPlan = plan };
        Check(!Prepare(partial).ReusesInstalledEnvironment && Prepare(partial).Configuration.Python == config.Python,
            "unfinished receipts cannot skip installation by lending an unverified interpreter");
        var wrongOwner = new DownloadOwnerRecord { Kind = "setup", EnvironmentRoot = unrelated.Draft.ComfyRoot, Configuration = unrelated.Draft };
        Fails(() => DownloadOwnerRecovery.Prepare(wrongOwner, plan, installed, currentModels, currentProfiles),
            "owner environment cannot redirect a frozen installation plan");

        var savedDefaults = installed.Draft.Clone();
        savedDefaults.SourceId = "official"; savedDefaults.GitMirror = ""; savedDefaults.ModelMirror = "";
        savedDefaults.PipIndex = DownloadSources.OfficialPipIndex; savedDefaults.NetworkMode = "direct"; savedDefaults.ProxyAddress = "";
        var displayed = DownloadIntegrationPolicy.WithDownloadSettings(installed.Draft, savedDefaults);
        var operation = DownloadIntegrationPolicy.WithDownloadSettings(displayed, owner.Configuration);
        var sourceModel = frozenModels[0] with { Urls = ["https://huggingface.co/fixture/model/resolve/fixed/model.bin"] };
        Check(DownloadSources.ModelCandidates(sourceModel, operation)[0].StartsWith("https://mirror.example/")
            && NetworkPolicySnapshot.Capture(operation).ProxyAddress == owner.Configuration.ProxyAddress,
            "saving defaults during installation does not change later model URLs or the owner's proxy snapshot");
        Check(DownloadSources.ModelCandidates(sourceModel, displayed)[0].StartsWith("https://huggingface.co/")
            && displayed.NetworkMode == "direct" && displayed.ProxyAddress == "",
            "new independent tasks immediately use saved official sources and direct networking");
        Check(operation.Python == installed.Draft.Python && operation.ComfyRoot == installed.Draft.ComfyRoot
            && operation.SourceId == owner.Configuration.SourceId && operation.GitMirror == owner.Configuration.GitMirror
            && operation.PipIndex == owner.Configuration.PipIndex,
            "frozen transport overlay preserves the actual installed interpreter and environment target");
        var restoredDisplay = DownloadIntegrationPolicy.WithDownloadSettings(owner.Configuration, savedDefaults);
        Check(restoredDisplay.SourceId == "official" && restoredDisplay.ModelMirror == "" && owner.Configuration.SourceId == "custom",
            "resuming an old owner restores displayed defaults without mutating its frozen transport");
        operation.ModelMirror = "https://changed.example";
        Check(owner.Configuration.ModelMirror == "https://mirror.example" && savedDefaults.ModelMirror == "",
            "operation configuration cannot mutate owner identity or saved UI settings");

        var frozenRuntime = new RuntimeManifest(1, "frozen", "fixture", new("comfy", "https://github.com/example/comfy", new string('a', 40), []),
            "3.11.9", "2.0", "", "", "https://example.com", [], [], [], [], new string('a', 40));
        var newRuntime = frozenRuntime with { Version = "current", Nodes = [new("manager", "https://github.com/example/manager", new string('b', 40), [])] };
        Check(ReferenceEquals(DownloadIntegrationPolicy.ModelRuntime(plan with { ResolvedRuntime = frozenRuntime }, newRuntime), frozenRuntime),
            "all restored metadata passes select the frozen runtime despite changed current node commits");
        Check(ReferenceEquals(DownloadIntegrationPolicy.ModelRuntime(null, newRuntime), newRuntime),
            "ordinary model operations use the current runtime when no installation plan is active");

        DownloadOwnerRecord Software(string version, DownloadTaskStatus status = DownloadTaskStatus.Paused)
        {
            var url = "https://github.com/" + LauncherUpdates.Repository + "/releases/download/" + version + "/";
            var release = new LauncherRelease(version, version, "fixture", DateTimeOffset.UtcNow,
                url + "AnimaRandomStudio.exe", 100, new string('c', 64), url + "build-info.json",
                JsonSerializer.SerializeToElement(new { version, runtimeIdentifier = "win-x64", packageFormat = "single-exe-v1" }));
            return new() { Kind = "software", Status = status, Payload = JsonSerializer.SerializeToElement(release, JsonFile.Options) };
        }
        var older = Software("1.9.0"); older.UpdatedAt = DateTimeOffset.UtcNow.AddDays(1);
        var latest = Software("1.10.0");
        var canceled = Software("9.0.0"); canceled.CancellationRequested = true;
        var invalid = Software("10.0.0"); invalid.Payload = JsonSerializer.SerializeToElement(new { version = "10.0.0" });
        var completed = Software("11.0.0", DownloadTaskStatus.Completed);
        var pending = new[] { older, latest, canceled, invalid, completed };
        Check(DownloadIntegrationPolicy.LatestSoftwareOwner(pending, "1.3.1")?.Id == latest.Id,
            "continue-all selects only the highest valid software version rather than the last updated owner");
        Check(DownloadIntegrationPolicy.LatestSoftwareOwner(pending, "1.10.0") is null,
            "continue-all excludes obsolete releases, malformed payloads, completed and canceled owners");
        Check(pending.All(item => item.Status == (item == completed ? DownloadTaskStatus.Completed : DownloadTaskStatus.Paused))
            && latest.Payload!.Value.GetProperty("version").GetString() == "1.10.0",
            "software selection never cancels skipped records, rewrites release identity, or applies an update");
        Check(!DownloadIntegrationPolicy.IsNewerSoftwareVersion("1.3.1", "1.3.1")
            && !DownloadIntegrationPolicy.IsNewerSoftwareVersion("1.3.0", "1.3.1")
            && DownloadIntegrationPolicy.IsNewerSoftwareVersion("1.10.0", "1.9.0"),
            "explicit software resume must be newer than the running version");

        var gate = new SoftwareDownloadGate();
        var reservations = new IDisposable?[32];
        Parallel.For(0, reservations.Length, index => reservations[index] = gate.TryEnter());
        Check(reservations.Count(item => item is not null) == 1 && gate.IsOccupied,
            "different software owners cannot enter the global update slot concurrently");
        var first = reservations.Single(item => item is not null)!;
        first.Dispose();
        var second = gate.TryEnter();
        Check(second is not null && gate.IsOccupied, "software failure or completion releases the slot for explicit next-version resume");
        first.Dispose();
        Check(gate.TryEnter() is null, "late duplicate disposal cannot release a newer software owner's reservation");
        second!.Dispose();
        Check(!gate.IsOccupied, "software reservation releases after the entire validation and ready-state publication boundary");
        return count;
    }
}
