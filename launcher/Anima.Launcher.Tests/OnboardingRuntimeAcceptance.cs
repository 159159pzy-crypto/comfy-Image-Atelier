using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anima.Launcher.Core;

/// <summary>Opt-in clean environment acceptance; never reads the user's launcher state or model paths.</summary>
public static class OnboardingRuntimeAcceptance
{
    public const string Root = @"E:\comfy-atelier-onboarding-qa-20261031";
    private const string Owner = "Comfy Image Atelier onboarding acceptance 20261031 v1";
    private static readonly string[] FaceIds = ["forbidden-vision-detect", "forbidden-vision-segment", "forbidden-vision-corrector"];
    private sealed record OutputProof(string Path, string Copy, long Bytes, string Sha256, int Width, int Height);

    public static async Task Run(string repositoryRoot, string mode = "all", string? modelCacheRoot = null, string? applicationRoot = null)
    {
        if (mode is not ("prepare" or "install" or "supplement" or "probe-nodes" or "models" or "verify" or "generate-base" or "generate-face" or "generate-nvidia" or "verify-nvidia-result" or "all"))
            throw new ArgumentException("Mode: prepare, install, supplement, probe-nodes, models, verify, generate-base, generate-face, generate-nvidia, verify-nvidia-result, all");
        repositoryRoot = Path.GetFullPath(repositoryRoot);
        applicationRoot = Path.GetFullPath(applicationRoot ?? repositoryRoot);
        if (modelCacheRoot == "-") modelCacheRoot = null;
        if (!File.Exists(Path.Combine(applicationRoot, "anima_webui", "server.py")))
            throw new DirectoryNotFoundException("Acceptance application payload does not contain anima_webui/server.py: " + applicationRoot);
        OwnRoot();
        var state = Safety.Under(Root, "state");
        var evidence = Safety.Under(Root, "evidence");
        var evidenceLabel = Environment.GetEnvironmentVariable("ANIMA_ONBOARDING_EVIDENCE_LABEL");
        if (!string.IsNullOrWhiteSpace(evidenceLabel))
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(evidenceLabel, "^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$"))
                throw new ArgumentException("ANIMA_ONBOARDING_EVIDENCE_LABEL must be a single safe directory label.");
            evidence = Safety.Under(evidence, evidenceLabel);
        }
        Directory.CreateDirectory(state); Directory.CreateDirectory(evidence);
        var runId = mode + "-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff");
        var logPath = Safety.Under(evidence, runId + ".log");
        var logGate = new object();
        var events = new List<string>();
        void Log(string value)
        {
            var line = DateTimeOffset.UtcNow.ToString("O") + " " + DiagnosticRedaction.Text(value);
            lock (logGate) { events.Add(line); Console.WriteLine(line); File.AppendAllText(logPath, line + Environment.NewLine); }
        }
        string[] Events() { lock (logGate) return events.ToArray(); }
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, value) => { value.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        var ct = cancellation.Token;
        var watch = Stopwatch.StartNew();
        var services = new ServiceManager(state, Log);
        services.RecoverOwned();
        string result = "failed"; string? failure = null;
        try
        {
            var manifestDirectory = Path.Combine(repositoryRoot, "launcher", "manifests");
            var runtime = JsonFile.Read<RuntimeManifest>(Path.Combine(manifestDirectory, "launcher-manifest.json"));
            var currentRuntime = runtime;
            var models = JsonFile.Read<ModelManifest>(Path.Combine(manifestDirectory, "model-manifest.json"));
            var profiles = JsonFile.Read<ModelProfilesManifest>(Path.Combine(manifestDirectory, "model-profiles.json"));
            Safety.Validate(runtime, models);
            var configuration = new LauncherConfig
            {
                InstallRoot = Root, ComfyRoot = Safety.Under(Root, "ComfyUI"), RuntimeRoot = Safety.Under(Root, "runtime"),
                AppRoot = applicationRoot, DataDir = Safety.Under(state, "data"), SelectedProfile = "anima",
                ComfyPort = 8288, WebPort = 8290, SourceId = "china", NetworkMode = "system",
                ComfyArguments = [], LaunchArgumentsConfigured = true
            };
            using var isolatedEnvironment = new IsolatedEnvironment(configuration);
            var planPath = Safety.Under(state, "plan.json");
            InstallationPlan plan;
            if (File.Exists(planPath))
            {
                plan = JsonFile.Read<InstallationPlan>(planPath);
                var frozenConfiguration = plan.CreateConfig();
                frozenConfiguration.AppRoot = applicationRoot;
                CheckTarget(frozenConfiguration, applicationRoot);
                Log("Resuming the exact reviewed environment and model identities.");
                JsonFile.Write(Safety.Under(evidence, "environment-reuse-" + runId + ".json"), new {
                    recordedAt = DateTimeOffset.UtcNow, originalColdEvidence = Safety.Under(Root, Path.Combine("evidence", "cold-environment.json")),
                    reusedComfyRoot = frozenConfiguration.ComfyRoot, reusedRuntimeRoot = frozenConfiguration.RuntimeRoot,
                    frozenPlan = planPath, frozenPlanSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(planPath, ct))),
                    reinstallRequested = mode is "install" or "supplement" or "all", modelDownloadRequested = mode is "models" or "all",
                    note = "Incremental acceptance reuses this isolated environment; it is not a second cold installation." });
            }
            else
            {
                if (Directory.Exists(configuration.ComfyRoot) && Directory.EnumerateFileSystemEntries(configuration.ComfyRoot).Any())
                    throw new IOException("A first acceptance plan requires an empty ComfyUI directory.");
                if (await ServiceManager.PortOpen(configuration.ComfyPort, ct) || await ServiceManager.PortOpen(configuration.WebPort, ct))
                    throw new IOException("Acceptance ports 8288/8290 are already occupied; no existing process will be stopped.");
                plan = InstallationPlanner.Create(configuration, runtime, state, true, models, profiles, []);
                plan = await InstallationPlanner.ResolveStableAsync(plan, runtime, state, ct);
                plan = await InstallationPlanner.PreparePlanAsync(plan, models, profiles, Path.Combine(repositoryRoot, "launcher", "scripts", "probe_comfy.py"), ct);
                var preflight = await InstallationPlanner.PreflightAsync(plan, ct);
                JsonFile.Write(Safety.Under(evidence, "initial-preflight.json"), preflight);
                if (!preflight.CanInstall) throw new IOException("Installation preflight failed: " + string.Join("; ", preflight.Issues.Where(issue => issue.Blocking)));
                JsonFile.Write(planPath, plan);
                JsonFile.Write(Safety.Under(evidence, "cold-environment.json"), new { createdAt = DateTimeOffset.UtcNow, emptyComfyRoot = true,
                    configuration.ComfyRoot, configuration.RuntimeRoot, configuration.DataDir, modelCacheRequested = modelCacheRoot is not null,
                    inheritedModelPaths = false, inheritedCustomNodes = false, sourceCommit = await SourceCommit(repositoryRoot, ct) });
            }
            runtime = plan.ResolvedRuntime ?? runtime;
            var frozenIds = plan.FrozenModels.Select(model => model.Id).ToHashSet(StringComparer.Ordinal);
            models = models with { Models = plan.FrozenModels.Concat(models.Models.Where(model => !frozenIds.Contains(model.Id))).ToArray() };
            if (plan.FrozenProfile is { } frozenProfile)
                profiles = profiles with { Profiles = profiles.Profiles.Where(profile => profile.Id != frozenProfile.Id).Append(frozenProfile).ToArray() };
            JsonFile.Write(Safety.Under(evidence, "runtime-manifest.json"), runtime);
            JsonFile.Write(Safety.Under(evidence, "model-manifest.json"), models);
            if (mode == "prepare") { result = "passed"; return; }
            configuration = plan.CreateConfig();
            var configPath = Safety.Under(state, "launcher.json");
            if (File.Exists(configPath)) configuration = JsonFile.Read<LauncherConfig>(configPath);
            configuration.AppRoot = applicationRoot;
            CheckTarget(configuration, applicationRoot);
            JsonFile.Write(configPath, configuration);
            JsonFile.Write(Safety.Under(evidence, "application-source-" + runId + ".json"), new { root = applicationRoot,
                packagedPayload = !applicationRoot.Equals(repositoryRoot, StringComparison.OrdinalIgnoreCase),
                coreAssembly = typeof(Installer).Assembly.Location,
                coreSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(typeof(Installer).Assembly.Location, ct))),
                serverSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(applicationRoot, "anima_webui", "server.py"), ct))) });
            if (mode == "verify-nvidia-result")
            {
                await services.StopOwnedAsync(ct: ct);
                await RecoverNvidiaEvidence(configuration, evidence, Log, ct);
                result = "passed"; return;
            }
            var network = NetworkPolicySnapshot.Capture(configuration);
            var aria2 = await BundledTools.EnsureAsync("aria2", configuration.RuntimeRoot);
            using var downloads = new Downloads(engineOptions: new() { Aria2Path = aria2, NetworkPolicyResolver = () => network });
            await using var tasks = new DownloadTaskService(downloads, state);
            var lastProgress = DateTimeOffset.MinValue;
            var progress = new InlineProgress(value =>
            {
                lock (logGate)
                {
                    if (DateTimeOffset.UtcNow - lastProgress < TimeSpan.FromSeconds(5) && value.Received != value.Total) return;
                    lastProgress = DateTimeOffset.UtcNow;
                    Log($"DOWNLOAD {value.Name}: {value.Received}/{value.Total}; {value.Stage}; {value.Engine}; {value.Source}");
                }
            });
            if (mode is "install" or "supplement" or "all")
            {
                await services.StopOwnedAsync(ct: ct);
                var executionPlan = mode == "supplement"
                    ? await PrepareSupplement(plan, configuration, runtime, currentRuntime, models, profiles, state, evidence, repositoryRoot, ct)
                    : plan;
                var executionRuntime = executionPlan.ResolvedRuntime ?? runtime;
                var owner = tasks.RegisterOwner("setup", "新电脑隔离验收安装", configuration, payload: JsonSerializer.SerializeToElement(executionPlan, JsonFile.Options));
                var lastDependency = DateTimeOffset.MinValue;
                void Dependency(DependencyProgress value)
                {
                    lock (logGate)
                    {
                        if (value.Status == DependencyProgressStatus.Running && DateTimeOffset.UtcNow - lastDependency < TimeSpan.FromSeconds(5)) return;
                        lastDependency = DateTimeOffset.UtcNow;
                        Log($"DEPENDENCY {value.Phase}: {value.CurrentPackage}; {value.Received}/{value.Total}; {value.Detail}; {value.Source}");
                    }
                }
                var installer = new Installer(state, executionRuntime, downloads, Log, progress, name => Log("INSTALL STAGE " + name), dependencyProgress: Dependency);
                await tasks.RunOwnerAsync(owner.Id, async token => { configuration = await installer.Install(executionPlan, token); }, ct);
                configuration.AppRoot = applicationRoot;
                configuration.InstallPending = false;
                JsonFile.Write(configPath, configuration);
                JsonFile.Write(Safety.Under(evidence, "installed-runtime.json"), await RuntimeDependencies.Probe(configuration, ct));
                if (mode is "install" or "supplement") { result = "passed"; return; }
            }
            if (!File.Exists(configuration.Python) || !File.Exists(Path.Combine(configuration.ComfyRoot, "main.py")))
                throw new IOException("Run the install stage before models or runtime acceptance.");
            if (mode == "probe-nodes")
            {
                await AssertIsolatedPaths(configuration, repositoryRoot, evidence, ct);
                await services.StartComfy(configuration, runtime, ct);
                var info = await ServiceManager.Json(configuration.ComfyUrl + "/object_info", ct)
                    ?? throw new IOException("Early node probe did not receive object_info.");
                JsonFile.Write(Safety.Under(evidence, "early-object-info.json"), info);
                JsonFile.Write(Safety.Under(evidence, "early-system-stats.json"), await ServiceManager.Json(configuration.ComfyUrl + "/system_stats", ct));
                var capabilities = FaceIds.AsEnumerable();
                if (NvidiaRuntimePreparation.ReadStatus(configuration, configuration.RuntimeRoot) is { Supported: true })
                    capabilities = capabilities.Append(RuntimeCapabilities.NvidiaCapabilityId);
                var missing = RuntimeCapabilities.RequiredClasses(runtime, capabilities).Where(name => !info.TryGetProperty(name, out _)).ToArray();
                if (missing.Length > 0) throw new IOException("Early node imports failed: " + string.Join(", ", missing));
                RuntimeCapabilities.ValidateObjectInfo(info, capabilities);
                var faceRoot = Path.Combine(configuration.ComfyRoot, "models", "forbidden_vision");
                var faceFiles = Directory.Exists(faceRoot) ? Directory.EnumerateFiles(faceRoot, "*", SearchOption.AllDirectories).ToArray() : [];
                JsonFile.Write(Safety.Under(evidence, "early-unselected-face-models.json"), new { checkedAt = DateTimeOffset.UtcNow, files = faceFiles });
                if (faceFiles.Length > 0) throw new IOException("Unselected face models appeared during early ComfyUI startup.");
                Log("EARLY NODE PROBE PASSED; model generation readiness is still pending.");
                result = "passed"; return;
            }
            var mandatory = SetupModelSelection.MandatoryModels(models, profiles, configuration.SelectedProfile);
            if (mode is "models" or "all")
            {
                await PrepareModels(mandatory, configuration, runtime, models, profiles, downloads, tasks, progress, repositoryRoot,
                    evidence, modelCacheRoot, Log, ct);
                if (mode == "models") { result = "passed"; return; }
            }
            await AssertIsolatedPaths(configuration, repositoryRoot, evidence, ct);
            var mandatoryScan = await ScanModels(mandatory, configuration, repositoryRoot, ct);
            if (mandatoryScan.Any(item => item.Missing)) throw new IOException("Run models stage first: " + string.Join(", ", mandatoryScan.Where(item => item.Missing).Select(item => item.Model.Name)));
            JsonFile.Write(Safety.Under(evidence, "models-mandatory.json"), mandatoryScan);
            Log("MANDATORY MODEL REUSE VERIFIED: all five existing files passed SHA-256; no model download was needed.");
            foreach (var model in mandatoryScan.Where(item => item.Model.Category == "diffusion_models"))
                await ModelMetadata.EnsureDefaultAsync(configuration, runtime, models, profiles, model.FoundPath!, ct, Log);
            await StartAndVerify(services, configuration, runtime, models, profiles, mandatory.Select(model => model.Id), evidence, "baseline", Log, ct);
            var faceSelectionPath = Safety.Under(state, "selected-face-models.json");
            if (!File.Exists(faceSelectionPath))
            {
                var faceRoot = Path.Combine(configuration.ComfyRoot, "models", "forbidden_vision");
                var implicitModels = Directory.Exists(faceRoot) ? Directory.EnumerateFiles(faceRoot, "*", SearchOption.AllDirectories).ToArray() : [];
                JsonFile.Write(Safety.Under(evidence, "unselected-face-models.json"), new { checkedAt = DateTimeOffset.UtcNow, files = implicitModels });
                if (implicitModels.Length > 0) throw new IOException("Optional face models were installed before the explicit face selection.");
            }
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(60) };
            var defaults = (await Read(http, configuration.WebUrl + "/api/config", ct))["defaults"]!.AsObject();
            if (mode is "generate-base" or "all")
            {
                await Generate(http, configuration, defaults, "random-dual", "random", "dual_sampler", null, evidence, Log, Events, ct);
                await Generate(http, configuration, defaults, "single-normal", "single", "normal", null, evidence, Log, Events, ct);
                await Generate(http, configuration, defaults, "single-dual", "single", "dual_sampler", null, evidence, Log, Events, ct);
                await Generate(http, configuration, defaults, "seedvr2", "single", "normal", "seedvr2", evidence, Log, Events, ct);
            }
            if (mode is "generate-face" or "all")
            {
                await Generate(http, configuration, defaults, "single-normal", "single", "normal", null, evidence, Log, Events, ct);
                await services.StopOwnedAsync(ct: ct);
                var faces = File.Exists(faceSelectionPath) ? JsonFile.Read<ModelItem[]>(faceSelectionPath)
                    : models.Models.Where(model => FaceIds.Contains(model.Id)).ToArray();
                if (faces.Length != FaceIds.Length) throw new InvalidDataException("Face acceptance requires the three manifest-locked default models.");
                JsonFile.Write(faceSelectionPath, faces);
                models = models with { Models = models.Models.Where(model => !FaceIds.Contains(model.Id)).Concat(faces).ToArray() };
                await PrepareModels(faces, configuration, runtime, models, profiles, downloads, tasks, progress, repositoryRoot,
                    evidence, modelCacheRoot, Log, ct);
                await StartAndVerify(services, configuration, runtime, models, profiles, mandatory.Select(model => model.Id).Concat(FaceIds), evidence, "face", Log, ct);
                await Generate(http, configuration, defaults, "face-defaults", "single", "normal", "face_refine", evidence, Log, Events, ct);
            }
            if (mode is "generate-nvidia" or "all")
            {
                await services.StopOwnedAsync(ct: ct);
                var nvidia = await NvidiaRuntimePreparation.PrepareAsync(configuration, configuration.RuntimeRoot, downloads, Log, progress, ct);
                JsonFile.Write(Safety.Under(evidence, "nvidia-runtime.json"), nvidia);
                if (nvidia.Supported)
                {
                    if (!nvidia.Ready) throw new IOException("Supported NVIDIA runtime failed preparation: " + nvidia.Detail);
                    await StartAndVerify(services, configuration, runtime, models, profiles,
                        mandatory.Select(model => model.Id).Append(RuntimeCapabilities.NvidiaCapabilityId), evidence, "nvidia", Log, ct);
                    await Generate(http, configuration, defaults, "nvidia-chain", "single", "normal", "nvidia", evidence, Log, Events, ct,
                        () => services.StopOwnedAsync(ct: ct));
                }
                else Log("NVIDIA CASE NOT APPLICABLE: " + nvidia.Detail);
            }
            JsonFile.Write(Safety.Under(evidence, "download-tasks.json"), tasks.GetSnapshot());
            result = "passed";
        }
        catch (Exception error) { failure = DiagnosticRedaction.Text(error.ToString()); Log(failure); throw; }
        finally
        {
            Exception? cleanupFailure = null;
            try { await services.StopOwnedAsync(ct: CancellationToken.None); }
            catch (Exception error) { Log("Owned service cleanup: " + error.Message); result = "failed"; if (failure is null) cleanupFailure = error; failure ??= error.ToString(); }
            JsonFile.Write(Safety.Under(evidence, runId + "-result.json"), new { result, failure, mode, seconds = watch.Elapsed.TotalSeconds,
                startedAt = DateTimeOffset.UtcNow - watch.Elapsed, completedAt = DateTimeOffset.UtcNow, logPath });
            Console.CancelKeyPress -= cancel;
            Log($"ACCEPTANCE {result.ToUpperInvariant()}: {mode}; evidence={evidence}");
            if (cleanupFailure is not null) throw new IOException("Acceptance service cleanup failed.", cleanupFailure);
        }
    }

    private static async Task<InstallationPlan> PrepareSupplement(InstallationPlan original, LauncherConfig config,
        RuntimeManifest frozen, RuntimeManifest current, ModelManifest models, ModelProfilesManifest profiles,
        string state, string evidence, string repositoryRoot, CancellationToken ct)
    {
        var path = Safety.Under(state, "supplement-plan.json");
        if (File.Exists(path)) return JsonFile.Read<InstallationPlan>(path);
        // A separate review takes the current required node contract while keeping
        // every already-reviewed commit and model hash. Never expand the old task.
        var frozenNodeIds = frozen.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        var source = frozen with
        {
            Comfy = frozen.Comfy with { RequiredClasses = current.Comfy.RequiredClasses },
            Nodes = frozen.Nodes.Select(node => current.Nodes.FirstOrDefault(item => item.Id == node.Id) is { } contract
                ? node with { RequiredClasses = contract.RequiredClasses } : node)
                .Concat(current.Nodes.Where(node => !frozenNodeIds.Contains(node.Id))).ToArray()
        };
        var reviewed = InstallationPlanner.Create(config, source, state, false, models, profiles, []);
        reviewed = await InstallationPlanner.PreparePlanAsync(reviewed, models, profiles,
            Path.Combine(repositoryRoot, "launcher", "scripts", "probe_comfy.py"), ct, requireModelPaths: true);
        var preflight = await InstallationPlanner.PreflightAsync(reviewed, ct);
        JsonFile.Write(Safety.Under(evidence, "supplement-preflight.json"), preflight);
        if (!preflight.CanInstall) throw new IOException("Supplemental installation preflight failed.");
        JsonFile.Write(path, reviewed);
        JsonFile.Write(Safety.Under(evidence, "supplement-review.json"), new { reviewedAt = DateTimeOffset.UtcNow,
            originalModelIds = original.ModelIds, original.MandatoryModelIds,
            originalPlanSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Safety.Under(state, "plan.json"), ct))),
            reviewed.RuntimeArtifactsIdentity, reviewed.FrozenRuntimeArtifacts, reviewed.RuntimeArtifactHost });
        return reviewed;
    }

    private static async Task PrepareModels(ModelItem[] selected, LauncherConfig config, RuntimeManifest runtime, ModelManifest models,
        ModelProfilesManifest profiles, Downloads downloads, DownloadTaskService tasks, IProgress<DownloadProgress> progress,
        string repositoryRoot, string evidence, string? cacheRoot, Action<string> log, CancellationToken ct)
    {
        var scan = await ScanModels(selected, config, repositoryRoot, ct);
        foreach (var item in scan)
        {
            var target = item.FoundPath ?? item.Destination;
            if (!item.Missing) log("VERIFIED MODEL REUSE " + item.Model.Id + ": " + target + "; no model download required.");
            _ = Safety.Under(config.ComfyRoot, Path.GetRelativePath(config.ComfyRoot, target));
            if (item.Missing && item.FoundPath is not null) throw new IOException("Preserving corrupt existing model: " + item.FoundPath);
            if (item.Missing && cacheRoot is not null)
            {
                var cache = Path.GetFullPath(cacheRoot);
                if (!Directory.Exists(cache)) throw new DirectoryNotFoundException(cache);
                var candidate = Directory.EnumerateFiles(cache, Path.GetFileName(item.Model.RelativePath),
                    new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).FirstOrDefault();
                if (candidate is not null && await Downloads.Verify(candidate, item.Model.Size, item.Model.Sha256, ct))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(candidate, target, false);
                    if (!await Downloads.Verify(target, item.Model.Size, item.Model.Sha256, ct)) throw new IOException("Verified cache copy changed in transit.");
                    JsonFile.Write(Safety.Under(evidence, "cache-reuse-" + item.Model.Id + ".json"),
                        new { source = candidate, destination = target, item.Model.Size, item.Model.Sha256, verifiedAt = DateTimeOffset.UtcNow, copied = true });
                    log("VERIFIED CACHE COPY " + item.Model.Id + " from " + candidate);
                }
            }
            var owner = tasks.RegisterOwner("models", "隔离验收模型 · " + item.Model.Name, config, [new(item.Model, target)]);
            await tasks.RunOwnerAsync(owner.Id, token => downloads.Fetch(item.Model.Name, DownloadSources.ModelCandidates(item.Model, config),
                target, item.Model.Size, item.Model.Sha256, progress, token), ct);
            if (item.Model.Category == "diffusion_models")
                await ModelMetadata.EnsureDefaultAsync(config, runtime, models, profiles, item.FoundPath ?? item.Destination, ct, log);
        }
        var verified = await ScanModels(selected, config, repositoryRoot, ct);
        if (verified.Any(item => item.Missing)) throw new IOException("Downloaded model inventory did not verify.");
        JsonFile.Write(Safety.Under(evidence, "models-" + (selected.Any(model => FaceIds.Contains(model.Id)) ? "face" : "mandatory") + ".json"), verified);
    }

    private static async Task<List<ModelStatus>> ScanModels(ModelItem[] models, LauncherConfig config, string repositoryRoot, CancellationToken ct)
    {
        var probe = await Inventory.Probe(config.ComfyRoot, config.Python, Path.Combine(repositoryRoot, "launcher", "scripts", "probe_comfy.py"), ct, config.ComfyArguments);
        return await Inventory.Scan(probe, new(1, models), true, ct);
    }

    private static async Task AssertIsolatedPaths(LauncherConfig config, string repositoryRoot, string evidence, CancellationToken ct)
    {
        if (File.Exists(Path.Combine(config.ComfyRoot, "extra_model_paths.yaml"))) throw new IOException("Clean acceptance must not inherit extra_model_paths.yaml.");
        var probe = await Inventory.Probe(config.ComfyRoot, config.Python, Path.Combine(repositoryRoot, "launcher", "scripts", "probe_comfy.py"), ct, config.ComfyArguments);
        foreach (var path in probe.Paths.Values.SelectMany(paths => paths)) _ = Safety.Under(config.ComfyRoot, Path.GetRelativePath(config.ComfyRoot, path));
        var linked = Directory.EnumerateFileSystemEntries(Path.Combine(config.ComfyRoot, "custom_nodes"))
            .Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0).ToArray();
        if (linked.Length > 0) throw new IOException("Clean acceptance custom nodes cannot be junctions or symbolic links.");
        JsonFile.Write(Safety.Under(evidence, "isolated-paths.json"), probe);
    }

    private static async Task StartAndVerify(ServiceManager services, LauncherConfig config, RuntimeManifest runtime, ModelManifest models,
        ModelProfilesManifest profiles, IEnumerable<string> selected, string evidence, string label, Action<string> log, CancellationToken ct)
    {
        await services.StartComfy(config, runtime, ct);
        await ModelMetadata.SynchronizePendingAsync(config, ct, log);
        await services.StartWebUI(config, runtime, ct);
        using var checker = new SetupRuntimeReadiness();
        var report = await checker.CheckAsync(config, runtime, models, profiles, selected, ct, log, config.RuntimeRoot);
        JsonFile.Write(Safety.Under(evidence, "readiness-" + label + ".json"), report);
        if (!report.Ready) throw new IOException("Runtime readiness failed: " + string.Join("; ", report.RequiredIssues.Concat(report.OptionalIssues).Select(issue => issue.Message)));
        var stats = await ServiceManager.Json(config.ComfyUrl + "/system_stats", ct) ?? throw new IOException("Missing ComfyUI identity.");
        if (!ServiceManager.SameRoot(stats, config.ComfyRoot)) throw new IOException("Running ComfyUI is not the clean acceptance environment.");
        JsonFile.Write(Safety.Under(evidence, "system-stats-" + label + ".json"), stats);
        JsonFile.Write(Safety.Under(evidence, "object-info-" + label + ".json"), await ServiceManager.Json(config.ComfyUrl + "/object_info", ct));
    }

    private static async Task Generate(HttpClient http, LauncherConfig config, JsonObject defaults, string name, string workbench,
        string baseMode, string? enabledStage, string evidence, Action<string> log, Func<string[]> events, CancellationToken ct,
        Func<Task>? releaseNativeLog = null)
    {
        var settings = (JsonObject)defaults.DeepClone();
        settings["count"] = 1; settings["workbench"] = workbench; settings["base_mode"] = baseMode;
        settings["extra_prompt"] = "1girl, close-up portrait, face centered, looking at viewer, gentle smile, simple background, safe";
        settings["seeds"] = new JsonObject { ["sample_seed"] = 102631, ["prompt_seed"] = 102631 };
        if (workbench == "single") settings["single"]!["positive_prompt"] = settings["extra_prompt"]!.GetValue<string>();
        foreach (var stage in settings["upscalers"]!.AsObject()) stage.Value!["enabled"] = stage.Key == enabledStage;
        if (enabledStage == "nvidia") settings["upscalers"]!["nvidia"]!["dlss"]!["enabled"] = true;
        var request = settings.ToJsonString(JsonFile.Options);
        var requestHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(request)));
        var implementationHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(typeof(Installer).Assembly.Location, ct)));
        var applicationIdentity = Path.GetFullPath(config.AppRoot) + "|" + Convert.ToHexString(SHA256.HashData(
            await File.ReadAllBytesAsync(Path.Combine(config.AppRoot, "anima_webui", "server.py"), ct)));
        var proofPath = Safety.Under(evidence, name + "-passed.json");
        if (File.Exists(proofPath))
        {
            var previous = JsonNode.Parse(await File.ReadAllTextAsync(proofPath, ct))!;
            var output = previous["output"]!.Deserialize<OutputProof>(JsonFile.Options)!;
            if (previous["requestHash"]?.GetValue<string>() == requestHash && previous["implementationHash"]?.GetValue<string>() == implementationHash
                && previous["applicationIdentity"]?.GetValue<string>() == applicationIdentity
                && await Downloads.Verify(output.Path, output.Bytes, output.Sha256, ct))
            { log("REUSED VERIFIED CASE " + name + "; output=" + output.Path); return; }
        }
        JsonFile.Write(Safety.Under(evidence, name + "-request.json"), settings);
        var eventOffset = events().Length;
        var serviceLogOffsets = new[] { "ComfyUI", "WebUI" }.ToDictionary(role =>
            Safety.Under(Root, Path.Combine("state", "logs", role + ".log")), path => File.Exists(path) ? new FileInfo(path).Length : 0L);
        var submittedAt = DateTimeOffset.UtcNow;
        using var submitted = await http.PostAsJsonAsync(config.WebUrl + "/api/batches", settings, JsonFile.Options, ct);
        var submittedText = await submitted.Content.ReadAsStringAsync(ct);
        await File.WriteAllTextAsync(Safety.Under(evidence, name + "-submission.json"), submittedText, ct);
        if (!submitted.IsSuccessStatusCode) throw new IOException(name + " submission failed: " + submittedText);
        var batchId = JsonNode.Parse(submittedText)!["id"]!.GetValue<string>();
        var deadline = DateTimeOffset.UtcNow.AddMinutes(45);
        var lastStatus = DateTimeOffset.MinValue;
        JsonNode? final = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            final = await Read(http, config.WebUrl + "/api/batches/current", ct);
            JsonFile.Write(Safety.Under(evidence, name + "-batch.json"), final);
            var batch = final["batch"];
            if (batch?["id"]?.GetValue<string>() != batchId) throw new IOException("Acceptance lost ownership of the active batch.");
            var status = batch["status"]!.GetValue<string>();
            if (DateTimeOffset.UtcNow - lastStatus >= TimeSpan.FromSeconds(15))
            { log($"GENERATION {name}: {status}; {batch["progress"]}; {batch["stage"]}"); lastStatus = DateTimeOffset.UtcNow; }
            if (status == "completed") break;
            if (status is "failed" or "error" or "stopped") throw new IOException(name + " failed: " + final.ToJsonString());
        }
        JsonFile.Write(Safety.Under(evidence, name + "-batch.json"), final);
        if (final?["batch"]?["status"]?.GetValue<string>() != "completed") throw new TimeoutException(name + " generation timed out.");
        var history = await Read(http, config.WebUrl + "/api/history?limit=60", ct);
        var image = history["items"]!.AsArray().SingleOrDefault(item => item?["batch_id"]?.GetValue<string>() == batchId)
            ?? throw new IOException("Completed batch is absent from workbench history.");
        JsonFile.Write(Safety.Under(evidence, name + "-history.json"), image);
        var promptId = image["prompt_id"]!.GetValue<string>();
        var comfyHistory = await Read(http, config.ComfyUrl + "/history/" + Uri.EscapeDataString(promptId), ct);
        JsonFile.Write(Safety.Under(evidence, name + "-comfy-history.json"), comfyHistory);
        var graph = comfyHistory[promptId]!["prompt"]![2]!.AsObject();
        var classes = graph.Select(node => node.Value!["class_type"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        var expected = enabledStage switch
        {
            "seedvr2" => new[] { "SeedVR2Conditioning", "SeedVR2Preprocess", "SeedVR2PostProcessing" },
            "face_refine" => ["ForbiddenVisionFaceProcessorIntegrated"],
            "nvidia" => ["RTXVideoSuperResolution", "DLSS5NeuralRender"],
            _ => baseMode == "dual_sampler" ? ["SesquiLatentUpscale", "KSamplerAdvanced"] : ["KSampler"]
        };
        if (expected.Any(node => !classes.Contains(node))) throw new IOException("Submitted graph did not exercise the requested case: " + name);
        if (enabledStage == "face_refine")
        {
            var faceInputs = graph.Single(node => node.Value?["class_type"]?.GetValue<string>() == "ForbiddenVisionFaceProcessorIntegrated").Value!["inputs"]!;
            if (faceInputs["sampler"]?.GetValue<string>() != "res_2m_ode" || faceInputs["scheduler"]?.GetValue<string>() != "beta57")
                throw new IOException("Face acceptance did not preserve the original res_2m_ode / beta57 defaults.");
        }
        if (image["file_type"]!.GetValue<string>() != "output") throw new IOException("Expected a persistent SaveImage output.");
        var relative = Path.Combine(image["subfolder"]!.GetValue<string>(), image["filename"]!.GetValue<string>());
        var path = Safety.Under(Path.Combine(config.ComfyRoot, "output"), relative);
        var bytes = await File.ReadAllBytesAsync(path, ct);
        if (bytes.Length < 24 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new IOException("SaveImage output is not a valid nonempty PNG.");
        var copy = Safety.Under(evidence, name + ".png");
        await File.WriteAllBytesAsync(copy, bytes, ct);
        var proof = new OutputProof(path, copy, bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)),
            BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)), BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)));
        if (proof.Width <= 0 || proof.Height <= 0) throw new IOException("Invalid output dimensions.");
        if (enabledStage is "seedvr2" or "nvidia")
        {
            var scale = settings["upscalers"]![enabledStage]![enabledStage == "seedvr2" ? "scale_by" : "scale"]!.GetValue<double>();
            if (proof.Width != (int)Math.Round(settings["width"]!.GetValue<int>() * scale)
                || proof.Height != (int)Math.Round(settings["height"]!.GetValue<int>() * scale))
                throw new IOException("Upscaler output did not match the requested image dimensions.");
        }
        var runtimeLines = events().Skip(eventOffset).Concat(serviceLogOffsets.SelectMany(entry => ReadLogSince(entry.Key, entry.Value))).ToArray();
        JsonFile.Write(Safety.Under(evidence, name + "-runtime-lines.json"), runtimeLines);
        if (enabledStage == "face_refine")
        {
            var swallowedFailures = new[] { "Error in KSampler:", "Error in inpaint sampling:", "Error in single face sampling:",
                "All face processing failed", "An error occurred during the main face processing workflow:", "An error occurred during batch face processing:" };
            if (runtimeLines.Any(line => swallowedFailures.Any(line.Contains))) throw new IOException("Forbidden Vision swallowed an execution failure; see face runtime evidence.");
            await VerifyFaceChangedPixels(config, Safety.Under(evidence, "single-normal.png"), copy, evidence, ct);
        }
        if (enabledStage == "nvidia")
        {
            // The SDK owns dlss_run.log exclusively for the process lifetime.
            // Save the completed HTTP/output proof first, then release our service.
            if (releaseNativeLog is null) throw new IOException("NVIDIA log validation requires the owned service shutdown callback.");
            await releaseNativeLog();
            await VerifyNvidiaExecution(config, graph, comfyHistory[promptId]!, proof, runtimeLines, submittedAt, evidence, ct);
        }
        JsonFile.Write(proofPath, new { requestHash, implementationHash, applicationIdentity, batchId, promptId, passedAt = DateTimeOffset.UtcNow, output = proof, exercisedNodes = expected });
        log($"GENERATION PASSED {name}: {proof.Width}x{proof.Height}; {copy}");
    }

    private static string[] ReadLogSince(string path, long offset)
    {
        if (!File.Exists(path)) return [];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (offset <= stream.Length) stream.Position = offset;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n').Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => Path.GetFileName(path) + ": " + DiagnosticRedaction.Text(line.TrimEnd('\r'))).ToArray();
    }

    private static async Task<(string Text, int Attempts)> ReadNativeLogAfterShutdown(string path, CancellationToken ct)
    {
        // Windows may finish releasing an SDK file handle just after the owning
        // Python process exits. Retry only that sharing violation, never other IO errors.
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { return (string.Join(Environment.NewLine, ReadLogSince(path, 0)), attempt); }
            catch (IOException error) when ((error.HResult & 0xffff) == 32 && attempt <= 50)
            { await Task.Delay(TimeSpan.FromMilliseconds(200), ct); }
        }
    }

    private static async Task RecoverNvidiaEvidence(LauncherConfig config, string evidence, Action<string> log, CancellationToken ct)
    {
        JsonNode Saved(string name) => JsonNode.Parse(File.ReadAllText(Safety.Under(evidence, name)))
            ?? throw new InvalidDataException("Missing saved NVIDIA evidence: " + name);
        var runFile = Directory.EnumerateFiles(evidence, "generate-nvidia-*-result.json").OrderDescending(StringComparer.Ordinal).FirstOrDefault()
            ?? throw new IOException("No previous NVIDIA generation run is available to verify.");
        var runId = Path.GetFileName(runFile)[..^"-result.json".Length];
        var source = Saved("application-source-" + runId + ".json");
        var implementationHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(typeof(Installer).Assembly.Location, ct)));
        var serverHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(config.AppRoot, "anima_webui", "server.py"), ct)));
        if (source["root"]?.GetValue<string>() != config.AppRoot || source["coreSha256"]?.GetValue<string>() != implementationHash
            || source["serverSha256"]?.GetValue<string>() != serverHash)
            throw new IOException("Saved NVIDIA execution belongs to another payload or Core implementation.");
        var settings = Saved("nvidia-chain-request.json");
        if (settings["upscalers"]?["nvidia"]?["enabled"]?.GetValue<bool>() != true)
            throw new IOException("Saved request did not enable the NVIDIA stage.");
        var batch = Saved("nvidia-chain-batch.json")["batch"]!;
        var image = Saved("nvidia-chain-history.json");
        var batchId = batch["id"]!.GetValue<string>();
        if (batch["status"]?.GetValue<string>() != "completed" || image["batch_id"]?.GetValue<string>() != batchId
            || Saved("nvidia-chain-submission.json")["id"]?.GetValue<string>() != batchId)
            throw new IOException("Saved NVIDIA request, completed batch and history do not match.");
        var promptId = image["prompt_id"]!.GetValue<string>();
        var history = Saved("nvidia-chain-comfy-history.json")[promptId]!;
        if (history["status"]?["completed"]?.GetValue<bool>() != true || history["status"]?["status_str"]?.GetValue<string>() != "success")
            throw new IOException("Saved ComfyUI execution did not succeed.");
        var executionStart = history["status"]!["messages"]!.AsArray().First(message => message?[0]?.GetValue<string>() == "execution_start")!;
        var submittedAt = DateTimeOffset.FromUnixTimeMilliseconds(executionStart[1]!["timestamp"]!.GetValue<long>());
        var graph = history["prompt"]![2]!.AsObject();
        if (image["file_type"]?.GetValue<string>() != "output") throw new IOException("Saved NVIDIA image is not a persistent output.");
        var path = Safety.Under(Path.Combine(config.ComfyRoot, "output"), Path.Combine(image["subfolder"]!.GetValue<string>(), image["filename"]!.GetValue<string>()));
        var bytes = await File.ReadAllBytesAsync(path, ct);
        if (bytes.Length < 24 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new IOException("Saved NVIDIA output is not a complete PNG.");
        var copy = Safety.Under(evidence, "nvidia-chain.png");
        var proof = new OutputProof(path, copy, bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)),
            BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)), BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)));
        if (!await Downloads.Verify(copy, proof.Bytes, proof.Sha256, ct)) throw new IOException("Saved output differs from the original evidence image.");
        var scale = settings["upscalers"]!["nvidia"]!["scale"]!.GetValue<double>();
        if (proof.Width != (int)Math.Round(settings["width"]!.GetValue<int>() * scale)
            || proof.Height != (int)Math.Round(settings["height"]!.GetValue<int>() * scale)) throw new IOException("Saved output dimensions do not match the NVIDIA request.");
        var runtimeLines = Saved("nvidia-chain-runtime-lines.json").Deserialize<string[]>(JsonFile.Options)!;
        await VerifyNvidiaExecution(config, graph, history, proof, runtimeLines, submittedAt, evidence, ct);
        var requestHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(settings.ToJsonString(JsonFile.Options))));
        var applicationIdentity = Path.GetFullPath(config.AppRoot) + "|" + serverHash;
        JsonFile.Write(Safety.Under(evidence, "nvidia-chain-passed.json"), new { requestHash, implementationHash, applicationIdentity,
            batchId, promptId, passedAt = DateTimeOffset.UtcNow, output = proof, exercisedNodes = new[] { "RTXVideoSuperResolution", "DLSS5NeuralRender" },
            recoveredNativeLogAfterShutdown = true, originalRun = runFile });
        log("NVIDIA SAME-EXECUTION EVIDENCE PASSED; no service was started and no image was regenerated.");
    }

    private static async Task VerifyNvidiaExecution(LauncherConfig config, JsonObject graph, JsonNode history, OutputProof output,
        string[] runtimeLines, DateTimeOffset submittedAt, string evidence, CancellationToken ct)
    {
        var vsr = graph.Single(node => node.Value?["class_type"]?.GetValue<string>() == "RTXVideoSuperResolution").Key;
        var dlss = graph.Single(node => node.Value?["class_type"]?.GetValue<string>() == "DLSS5NeuralRender").Key;
        bool DependsOn(string nodeId, string target, HashSet<string> seen)
        {
            if (nodeId == target) return true;
            if (!seen.Add(nodeId) || graph[nodeId]?["inputs"] is not JsonObject inputs) return false;
            return inputs.Any(input => input.Value is JsonArray { Count: 2 } link
                && link[0] is JsonValue value && value.TryGetValue<string>(out var source) && graph.ContainsKey(source)
                && DependsOn(source, target, seen));
        }
        if (!DependsOn(dlss, vsr, []) || !graph.Any(node => node.Value?["class_type"]?.GetValue<string>() == "SaveImage"
            && DependsOn(node.Key, dlss, []))) throw new IOException("NVIDIA nodes were not on the executed SaveImage dependency chain.");
        var cached = history["status"]!["messages"]!.AsArray().Where(message => message?[0]?.GetValue<string>() == "execution_cached")
            .SelectMany(message => message![1]!["nodes"]!.AsArray()).Select(node => node!.GetValue<string>()).ToHashSet();
        if (cached.Contains(vsr) || cached.Contains(dlss)) throw new IOException("NVIDIA workflow acceptance must execute both native nodes without cached outputs.");
        var hostLogPath = Path.Combine(config.ComfyRoot, "custom_nodes", "DLSS5-Comfyui", "vendor", "dlss_run.log");
        if (!File.Exists(hostLogPath) || File.GetLastWriteTimeUtc(hostLogPath) < submittedAt.UtcDateTime)
            throw new IOException("DLSS workflow did not produce a fresh native host log.");
        var logRead = await ReadNativeLogAfterShutdown(hostLogPath, ct);
        var hostLog = logRead.Text;
        await File.WriteAllTextAsync(Safety.Under(evidence, "nvidia-workflow-dlss-native.log"), hostLog, ct);
        if (!hostLog.Contains($"w={output.Width} h={output.Height}") || !hostLog.Contains("Feature 18 created OK")
            || !hostLog.Contains("EvaluateFeature -> 0x00000001")) throw new IOException("DLSS native workflow evaluation was not confirmed at the actual output dimensions.");
        if (runtimeLines.Any(line => line.Contains("VSR intermediate and bicubic final resize")
            || (line.Contains("falling back", StringComparison.OrdinalIgnoreCase)
                && (line.Contains("DLSS5", StringComparison.OrdinalIgnoreCase) || line.Contains("RTX VSR", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("nvvfx", StringComparison.OrdinalIgnoreCase)))
            || line.Contains("dlssnr_process failed") || line.Contains("dlssnr_init failed")))
            throw new IOException("NVIDIA workflow reported a fallback or native failure.");
        var nativeSources = new[] { Path.Combine(config.ComfyRoot, "custom_nodes", "nvidia-rtx-vsr-pro", "__init__.py"),
            Path.Combine(config.ComfyRoot, "custom_nodes", "nvidia-rtx-vsr-pro", "vsr_pro.py"),
            Path.Combine(config.ComfyRoot, "custom_nodes", "DLSS5-Comfyui", "dlss5_backend.py") };
        var hashes = new Dictionary<string, string>();
        foreach (var source in nativeSources) hashes[source] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source, ct)));
        JsonFile.Write(Safety.Under(evidence, "nvidia-native-workflow.json"), new { vsrNode = vsr, dlssNode = dlss,
            outputDependencyChainVerified = true, neitherNativeNodeCached = true, hostLogPath, nativeEvaluationSucceeded = true,
            output.Width, output.Height, sources = hashes, submittedAt, checkedAt = DateTimeOffset.UtcNow,
            nativeLogReadAttempts = logRead.Attempts, nativeLogMaximumWaitSeconds = 10 });
    }

    private static async Task VerifyFaceChangedPixels(LauncherConfig config, string baseline, string refined, string evidence, CancellationToken ct)
    {
        const string script = """
            import json, sys
            import numpy as np
            from PIL import Image
            original = np.asarray(Image.open(sys.argv[1]).convert('RGB'), dtype=np.int16)
            refined = np.asarray(Image.open(sys.argv[2]).convert('RGB'), dtype=np.int16)
            if original.shape != refined.shape:
                raise RuntimeError('Face refinement unexpectedly changed image dimensions')
            difference = np.abs(original - refined)
            result = {'changedPixelRatio': float(np.any(difference >= 16, axis=2).mean()),
                      'meanAbsoluteDifference': float(difference.mean()), 'maximumDifference': int(difference.max())}
            print(json.dumps(result))
            """;
        var result = JsonNode.Parse(await Commands.Run(config.Python, ["-c", script, baseline, refined], config.ComfyRoot, null, ct, timeout: TimeSpan.FromMinutes(1)))!;
        JsonFile.Write(Safety.Under(evidence, "face-pixel-difference.json"), result);
        if (result["changedPixelRatio"]!.GetValue<double>() < 0.001 || result["meanAbsoluteDifference"]!.GetValue<double>() < 0.05)
            throw new IOException("Face refinement did not materially change the same-seed baseline; an undetected face or silent fallback cannot pass acceptance.");
    }

    private static async Task<JsonNode> Read(HttpClient http, string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new IOException(url + ": " + body);
        return JsonNode.Parse(body) ?? throw new IOException("Empty JSON response: " + url);
    }

    private static void OwnRoot()
    {
        var marker = Safety.Under(Root, "owner.txt");
        if (Directory.Exists(Root))
        {
            if (!File.Exists(marker) || File.ReadAllText(marker) != Owner) throw new IOException("Acceptance directory already exists without its exact ownership marker.");
            if ((File.GetAttributes(Root) & FileAttributes.ReparsePoint) != 0) throw new IOException("Acceptance root cannot be a reparse point.");
            return;
        }
        Directory.CreateDirectory(Root);
        File.WriteAllText(marker, Owner);
    }

    private static void CheckTarget(LauncherConfig config, string repositoryRoot)
    {
        foreach (var path in new[] { config.ComfyRoot, config.RuntimeRoot, config.DataDir }) _ = Safety.Under(Root, Path.GetRelativePath(Root, path));
        if (config.ComfyPort != 8288 || config.WebPort != 8290 || !Path.GetFullPath(config.AppRoot).Equals(repositoryRoot, StringComparison.OrdinalIgnoreCase)
            || config.ComfyArguments.Count != 0) throw new IOException("Saved acceptance configuration changed its isolated target or runtime arguments.");
    }

    private static Task<string> SourceCommit(string root, CancellationToken ct) => Commands.Find("git.exe") is { } git
        ? Commands.Run(git, ["rev-parse", "HEAD"], root, null, ct, timeout: TimeSpan.FromSeconds(15)) : Task.FromResult("unavailable");
    private sealed class InlineProgress(Action<DownloadProgress> action) : IProgress<DownloadProgress>
    { public void Report(DownloadProgress value) => action(value); }
    private sealed class IsolatedEnvironment : IDisposable
    {
        private readonly Dictionary<string, string?> previous = new(StringComparer.OrdinalIgnoreCase);
        public IsolatedEnvironment(LauncherConfig config)
        {
            var values = ServiceManager.BuildRuntimeEnvironment(config, hasManagedMarker: true);
            foreach (var value in values.Where(pair => pair.Key != "LORA_MANAGER_PORTABLE").Select(pair => pair.Value).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                _ = Safety.Under(Root, Path.GetRelativePath(Root, value));
                Directory.CreateDirectory(value);
            }
            foreach (var name in new[] { "PYTHONPATH", "PYTHONHOME", "VIRTUAL_ENV", "COMFYUI_MODEL_PATH", "COMFYUI_PATH" }) Set(name, null);
            Set("PYTHONNOUSERSITE", "1");
            foreach (var item in values) Set(item.Key, item.Value);
        }
        private void Set(string name, string? value)
        {
            previous.TryAdd(name, Environment.GetEnvironmentVariable(name));
            Environment.SetEnvironmentVariable(name, value);
        }
        public void Dispose() { foreach (var item in previous) Environment.SetEnvironmentVariable(item.Key, item.Value); }
    }
}
