using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Anima.Launcher.Core;

/// <summary>Opt-in real dependency installation. Every mutable path belongs to a marked E: test root.</summary>
public static class DependencyInstallAcceptance
{
    private const string Owner = "Anima dependency acceptance 20261030 v1";
    private const string Allowed = @"E:\CodexDependencyTests";
    private sealed record StageTiming(string Name, double StartedSeconds, double Seconds);
    private sealed record RetainedTask(string Id, string Destination, long Size, string Sha256, long RetainedBytes, string Status);
    private sealed record LegacyBodyObservation(string Path, string FileName, long Size, long Bytes, DateTimeOffset ObservedAt);
    private static bool DependencyStage(string name) => name is "安装 ComfyUI 依赖" or "安装并验证 Manager 依赖";

    public static async Task Run(string repositoryRoot, string suiteRoot, string mode, string label)
    {
        if (mode.StartsWith("fixed-", StringComparison.Ordinal))
        {
            var previous = Environment.GetEnvironmentVariable("GCM_INTERACTIVE");
            Environment.SetEnvironmentVariable("GCM_INTERACTIVE", "never");
            try { await RunFixed(repositoryRoot, suiteRoot, mode, label); }
            finally { Environment.SetEnvironmentVariable("GCM_INTERACTIVE", previous); }
            return;
        }
        if (mode is not ("prepare" or "cold" or "hot" or "pause" or "resume")) throw new ArgumentException("Mode: prepare, cold, hot, pause, resume, fixed-cold, fixed-hot, fixed-pause, fixed-resume");
        if (!Regex.IsMatch(label, "^[a-z0-9-]{1,40}$")) throw new ArgumentException("Use a simple unique run label.");
        suiteRoot = OwnRoot(suiteRoot, allowCreate: true);
        repositoryRoot = Path.GetFullPath(repositoryRoot);
        var runRoot = Under(suiteRoot, label);
        Directory.CreateDirectory(runRoot);
        var marker = Path.Combine(runRoot, "owner.txt");
        if (!File.Exists(marker))
        {
            if (Directory.EnumerateFileSystemEntries(runRoot).Any()) throw new IOException("Run directory is not empty or owned.");
            File.WriteAllText(marker, Owner);
        }
        else if (File.ReadAllText(marker) != Owner) throw new IOException("Run ownership marker mismatch.");
        var state = Under(runRoot, "state"); Directory.CreateDirectory(state);
        var logPath = Under(state, mode + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".log");
        var logGate = new object();
        void Log(string value)
        {
            var line = DateTimeOffset.Now.ToString("O") + " " + DiagnosticRedaction.Text(value);
            lock (logGate) { Console.WriteLine(line); File.AppendAllText(logPath, line + Environment.NewLine); }
        }
        var frozenPath = Under(suiteRoot, "frozen-plan.json");
        var source = JsonFile.Read<RuntimeManifest>(Path.Combine(repositoryRoot, "launcher", "manifests", "launcher-manifest.json"));
        var config = new LauncherConfig { InstallRoot = runRoot, ComfyRoot = Under(runRoot, "ComfyUI"), RuntimeRoot = Under(runRoot, "runtime"),
            DataDir = Under(state, "data"), AppRoot = repositoryRoot, SourceId = "china", ComfyPort = FreePort(), WebPort = FreePort() };
        while (config.ComfyPort == config.WebPort) config.WebPort = FreePort();
        var planFile = Under(state, "plan.json");
        InstallationPlan plan;
        if (File.Exists(planFile)) plan = JsonFile.Read<InstallationPlan>(planFile);
        else
        {
            if (!File.Exists(frozenPath))
            {
                Log("Resolving stable core and node commits once; all runs reuse this reviewed input.");
                var reviewed = await InstallationPlanner.ResolveStableAsync(InstallationPlanner.Create(config, source, state, true), source, state, default);
                JsonFile.Write(frozenPath, reviewed);
                JsonFile.Write(Under(suiteRoot, "source-manifest.json"), source);
            }
            var frozen = JsonFile.Read<InstallationPlan>(frozenPath);
            plan = InstallationPlanner.Create(config, frozen.ResolvedRuntime ?? source, state, true) with
            { ResolvedRuntime = frozen.ResolvedRuntime, Release = frozen.Release, Dependencies = frozen.Dependencies,
                NodeVersions = frozen.NodeVersions, NodeVersionsResolved = true };
            JsonFile.Write(planFile, plan);
        }
        config = plan.CreateConfig();
        foreach (var path in new[] { config.ComfyRoot, config.RuntimeRoot, config.DataDir }) _ = Under(runRoot, Path.GetRelativePath(runRoot, path));
        var manifest = plan.ResolvedRuntime ?? source;
        if (mode == "prepare") { Log("FROZEN PLAN READY " + frozenPath); return; }
        if (mode == "cold" && Directory.Exists(config.ComfyRoot) && Directory.EnumerateFileSystemEntries(config.ComfyRoot).Any())
            throw new IOException("Cold installation requires a genuinely empty new environment; use resume for a partial run.");
        if (mode == "hot" && !File.Exists(Under(state, "launcher.json"))) throw new IOException("Hot run needs a completed owned cold run.");
        long bootstrapReusedBytes = 0;
        if (mode is "cold" or "pause" && label != "current")
            bootstrapReusedBytes = await ReuseBootstrap(suiteRoot, config.RuntimeRoot, manifest, Log);
        var network = NetworkPolicySnapshot.Capture(config);
        var aria2 = await BundledTools.EnsureAsync("aria2", config.RuntimeRoot);
        long managedHttpBodyBytes = 0;
        using var counting = new CountingHandler(network.CreateHttpHandler(), count => Interlocked.Add(ref managedHttpBodyBytes, count));
        using var downloads = new Downloads(handler: counting, engineOptions: new() { Aria2Path = aria2, NetworkPolicyResolver = () => network });
        await using var tasks = ConstructTasks(downloads, state);
        var ownerRecord = tasks.RegisterOwner("setup", "隔离依赖验收", config, payload: JsonSerializer.SerializeToElement(plan, JsonFile.Options));
        var stages = new List<StageTiming>();
        var transfers = new ConcurrentDictionary<string, object>();
        var watch = Stopwatch.StartNew();
        var started = DateTimeOffset.UtcNow;
        var stageStart = 0d; var stageName = "preflight"; var dependencySeconds = 0d; var seedCopySeconds = 0d;
        var phaseEvents = new List<object>(); var lastPhase = ""; var lastProgress = DateTime.MinValue;
        using var stop = new CancellationTokenSource();
        var pauseArmed = false;
        var hasDependencyChannel = typeof(Installer).GetConstructors().Single().GetParameters().Any(parameter => parameter.Name == "dependencyProgress");
        var pauseTrigger = "";
        long observedDependencyReceived = 0;
        var seed = Under(runRoot, "gpu-seed-venv");
        void Stage(string name)
        {
            var now = watch.Elapsed.TotalSeconds;
            stages.Add(new(stageName, stageStart, now - stageStart));
            if (DependencyStage(stageName)) dependencySeconds += now - stageStart;
            stageName = name; stageStart = now;
            if (name == "安装 ComfyUI 依赖" && !Directory.Exists(seed))
            {
                var python = Under(config.ComfyRoot, @".venv\Scripts\python.exe");
                var inventory = Inventory(config.WithPython(python)).GetAwaiter().GetResult();
                JsonFile.Write(Under(state, "gpu-seed-packages.json"), inventory);
                var copyWatch = Stopwatch.StartNew();
                CopyTree(Under(config.ComfyRoot, ".venv"), seed);
                seedCopySeconds += copyWatch.Elapsed.TotalSeconds;
                stageStart = watch.Elapsed.TotalSeconds;
                Log($"Saved isolated GPU environment seed; excluded preparation copy {copyWatch.Elapsed.TotalSeconds:F2}s.");
            }
            if (mode == "pause" && !hasDependencyChannel && name == "安装 ComfyUI 依赖" && !pauseArmed)
            { pauseArmed = true; pauseTrigger = "legacy dependency stage +15s; may precede artifact transfer"; stop.CancelAfter(TimeSpan.FromSeconds(15)); Log("Legacy dependency pause scheduled after 15 seconds."); }
        }
        void Dependency(object value)
        {
            var json = JsonSerializer.SerializeToElement(value, value.GetType(), JsonFile.Options);
            var phase = json.TryGetProperty("phase", out var name) ? name.GetString() ?? "" : "";
            if (phase == "downloading" && json.TryGetProperty("received", out var bytes) && bytes.ValueKind == JsonValueKind.Number && bytes.TryGetInt64(out var received))
                Interlocked.Exchange(ref observedDependencyReceived, Math.Max(observedDependencyReceived, received));
            if (mode == "pause" && phase == "downloading" && !pauseArmed)
            { pauseArmed = true; pauseTrigger = "dependency downloading phase +5s"; stop.CancelAfter(TimeSpan.FromSeconds(5)); Log("Artifact transfer pause scheduled after 5 seconds."); }
            if (phase == lastPhase) return;
            lastPhase = phase;
            lock (phaseEvents) phaseEvents.Add(new { seconds = watch.Elapsed.TotalSeconds, value = json });
            Log("DEPENDENCY PHASE " + phase);
        }
        var progress = new InlineProgress(value =>
        {
            transfers[value.Name] = new { value.Name, value.Total, value.Received, value.Stage, value.Source, value.Engine };
            if (DateTime.UtcNow - lastProgress < TimeSpan.FromSeconds(5)) return;
            lastProgress = DateTime.UtcNow; Log($"DOWNLOAD {value.Name} {value.Received}/{value.Total} {value.Stage} [{value.Engine}]");
        });
        var installer = ConstructInstaller(state, manifest, downloads, Log, progress, Stage, Dependency);
        var services = new ServiceManager(state, Log);
        string status = "failed"; string? error = null; double startupSeconds = 0;
        try
        {
            Log($"BEGIN {label}/{mode}; Core={typeof(Installer).Assembly.Location}; Comfy={manifest.Comfy.Commit}; models=disabled; no dependency cache shared across run labels");
            await tasks.RunOwnerAsync(ownerRecord.Id, async token => { config = await installer.Install(plan, token); }, stop.Token);
            Stage("installation-complete");
            JsonFile.Write(Under(state, "launcher.json"), config);
            JsonFile.Write(Under(state, "installed-manifest.json"), manifest);
            var finalPackages = await Inventory(config);
            JsonFile.Write(Under(state, "final-packages.json"), finalPackages);
            JsonFile.Write(Under(state, mode + "-packages.json"), finalPackages);
            var resolved = FindSnapshot(config.RuntimeRoot, manifest.Comfy.Commit);
            if (resolved is not null) File.Copy(resolved, Under(state, "resolved-snapshot.json"), true);
            JsonFile.Write(Under(state, "runtime-dependencies.json"), await RuntimeDependencies.Probe(config, default));
            var startup = Stopwatch.StartNew();
            await StartWithoutModelDownloads(services, config, manifest);
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(30) };
            var stats = await http.GetStringAsync(config.ComfyUrl + "/system_stats");
            using var statsJson = JsonDocument.Parse(stats);
            if (!ServiceManager.SameRoot(statsJson.RootElement, config.ComfyRoot)) throw new IOException("Started server identity differs from the owned environment.");
            await File.WriteAllTextAsync(Under(state, "system-stats.json"), stats);
            var nodes = await http.GetStringAsync(config.ComfyUrl + "/object_info");
            await File.WriteAllTextAsync(Under(state, "object-info.json"), nodes);
            using var nodeJson = JsonDocument.Parse(nodes);
            var missing = manifest.Nodes.SelectMany(node => node.RequiredClasses).Distinct().Where(node => !nodeJson.RootElement.TryGetProperty(node, out _)).ToArray();
            if (missing.Length > 0) throw new IOException("Startup missing required node classes: " + string.Join(", ", missing));
            if (ModelFiles(config).Length > 0) throw new IOException("Unexpected model weights appeared during dependency-only acceptance.");
            startupSeconds = startup.Elapsed.TotalSeconds;
            status = "passed";
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        { status = "paused"; Log("Dependency installation paused; owned checkpoint files are retained for resume."); }
        catch (Exception ex) { error = DiagnosticRedaction.Text(ex.ToString()); Log(error); throw; }
        finally
        {
            await services.StopOwnedAsync();
            Stage("finished");
            var metrics = new { label, mode, status, error, started, completed = DateTimeOffset.UtcNow,
                coreAssembly = typeof(Installer).Assembly.Location,
                coreSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Installer).Assembly.Location))).ToLowerInvariant(),
                manifest.Comfy.Commit, elapsedSeconds = watch.Elapsed.TotalSeconds, measuredSeconds = watch.Elapsed.TotalSeconds - seedCopySeconds,
                dependencySeconds, startupSeconds, excludedSeedCopySeconds = seedCopySeconds, managedHttpBodyBytes, pauseTrigger, observedDependencyReceived,
                byteScope = "observed artifact payload body bytes read through Downloads; source metadata probes and pip/git/uv subprocess traffic are excluded, never treated as zero",
                modelFiles = ModelFiles(config), modelsRequested = false, bootstrapReusedBytes, bootstrapScope = "only manifest-locked tools, Python and GPU archives; no dependency/pip/uv cache copied", stages, dependencyPhases = phaseEvents, transfers = transfers.Values,
                downloadOwnerId = ownerRecord.Id, durableTaskCount = tasks.Tasks.Count,
                availableBytes = new DriveInfo(Path.GetPathRoot(runRoot)!).AvailableFreeSpace, logPath };
            JsonFile.Write(Under(state, mode + "-metrics.json"), metrics);
            JsonFile.Write(Path.ChangeExtension(logPath, ".metrics.json"), metrics);
            Log($"RESULT {status}; total {watch.Elapsed.TotalSeconds:F2}s; dependency {dependencySeconds:F2}s; startup {startupSeconds:F2}s; metrics={state}");
        }
        if (mode == "pause" && status != "paused") Log("Pause did not interrupt the run; this result must not be counted as resume evidence.");
        Compare(suiteRoot, "current", "baseline", "");
    }

    private static async Task<long> ReuseBootstrap(string suite, string destinationRuntime, RuntimeManifest manifest, Action<string> log, bool includeGpu = true)
    {
        var originalRuntime = Under(suite, @"current\runtime");
        if (!Directory.Exists(originalRuntime) || originalRuntime.Equals(destinationRuntime, StringComparison.OrdinalIgnoreCase)) return 0;
        long bytes = 0;
        var artifacts = manifest.Tools.Concat(includeGpu ? manifest.TorchWheels.Append(PythonRuntimeInstallation.Resolve(manifest).Artifact) : []);
        foreach (var artifact in artifacts)
        {
            var source = ArtifactCache.PathFor(originalRuntime, artifact);
            if (!await Downloads.Verify(source, artifact.Size, artifact.Sha256, default)) continue;
            var destination = ArtifactCache.PathFor(destinationRuntime, artifact);
            _ = Under(destinationRuntime, Path.GetRelativePath(destinationRuntime, destination));
            if (File.Exists(destination)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination); bytes += artifact.Size;
        }
        log($"Reused {bytes} verified bootstrap bytes ({(includeGpu ? "tools/Python/GPU" : "tools")} only); dependency caches remain independent.");
        return bytes;
    }

    private static async Task RunFixed(string repositoryRoot, string suite, string mode, string label)
    {
        if (mode is not ("fixed-cold" or "fixed-hot" or "fixed-pause" or "fixed-resume" or "fixed-recover") || !Regex.IsMatch(label, "^[a-z0-9-]{1,40}$"))
            throw new ArgumentException("Invalid fixed benchmark mode or label.");
        suite = OwnRoot(suite, false);
        var original = Under(suite, "current");
        if (File.ReadAllText(Under(original, "owner.txt")) != Owner) throw new IOException("Original environment ownership differs.");
        var originalState = Under(original, "state");
        var config = JsonFile.Read<LauncherConfig>(Under(originalState, "launcher.json"));
        if (!Path.GetFullPath(config.ComfyRoot).Equals(Under(original, "ComfyUI"), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFullPath(config.Python).Equals(Under(original, @"ComfyUI\.venv\Scripts\python.exe"), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Fixed benchmark configuration must identify the owned original environment.");
        var manifest = JsonFile.Read<RuntimeManifest>(Under(originalState, "installed-manifest.json"));
        var snapshot = JsonFile.Read<ComfyDependencySnapshot>(Under(originalState, "resolved-snapshot.json"));
        if (!snapshot.Resolved || snapshot.ComfyCommit != manifest.Comfy.Commit) throw new IOException("The fixed benchmark requires the completed original solver report.");
        var run = Under(suite, "fixed-" + label); Directory.CreateDirectory(run);
        var owner = Under(run, "owner.txt");
        if (!File.Exists(owner))
        {
            if (Directory.EnumerateFileSystemEntries(run).Any()) throw new IOException("Fixed benchmark directory is not owned.");
            File.WriteAllText(owner, Owner);
        }
        if (File.ReadAllText(owner) != Owner) throw new IOException("Fixed benchmark ownership differs.");
        var state = Under(run, "state"); Directory.CreateDirectory(state);
        var logPath = Under(state, mode + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".log");
        var logGate = new object();
        Action<string>? observeLog = null;
        void Log(string value) { lock (logGate) { var line = DateTimeOffset.Now.ToString("O") + " " + DiagnosticRedaction.Text(value); Console.WriteLine(line); File.AppendAllText(logPath, line + Environment.NewLine); } observeLog?.Invoke(value); }
        if (await ServiceManager.PortOpen(config.ComfyPort)) throw new IOException("Stop the owned environment before a fixed snapshot benchmark.");
        var runtime = Under(run, "runtime");
        if (mode == "fixed-cold" && Directory.Exists(runtime) && Directory.EnumerateFileSystemEntries(runtime).Any()) throw new IOException("Fixed cold cache is not empty; use a new run label.");
        var preparation = Stopwatch.StartNew();
        if (mode is not ("fixed-resume" or "fixed-recover"))
        {
            var environment = Under(original, @"ComfyUI\.venv");
            if (Directory.Exists(environment))
                Directory.Move(environment, Under(original, "retired-venv-" + Guid.NewGuid().ToString("N")));
            CopyTree(Under(original, "gpu-seed-venv"), environment);
        }
        config.RuntimeRoot = runtime;
        config.DataDir = Under(state, "data");
        // Mechanism comparison uses identical reachable transports. Automatic source
        // selection and mirror recovery are measured separately by the full fresh run.
        config.SourceId = "official";
        config.PipIndex = DownloadSources.OfficialPipIndex;
        config.GitMirror = "";
        JsonFile.Write(Under(state, "launcher.json"), config);
        JsonFile.Write(Under(state, "frozen-snapshot.json"), snapshot);
        await ReuseBootstrap(suite, runtime, manifest, Log, includeGpu: false);
        var excludedPreparationSeconds = preparation.Elapsed.TotalSeconds;
        var aria2 = await BundledTools.EnsureAsync("aria2", runtime);
        var network = NetworkPolicySnapshot.Capture(config);
        long managedHttpBodyBytes = 0;
        using var handler = new CountingHandler(network.CreateHttpHandler(), count => Interlocked.Add(ref managedHttpBodyBytes, count));
        using var downloads = new Downloads(handler: handler, engineOptions: new() { Aria2Path = aria2, NetworkPolicyResolver = () => network });
        await using var tasks = ConstructTasks(downloads, state);
        var ownerRecord = tasks.RegisterOwner("dependencyRepair", "固定依赖验收", config, payload: JsonSerializer.SerializeToElement(snapshot, JsonFile.Options));
        var hasChannel = typeof(Installer).GetConstructors().Single().GetParameters().Any(parameter => parameter.Name == "dependencyProgress");
        var retainedBefore = Retained(tasks);
        var retainedAtPause = Array.Empty<RetainedTask>();
        if (mode is "fixed-resume" or "fixed-recover")
        {
            var pausePath = Under(state, "fixed-pause-metrics.json");
            if (!File.Exists(pausePath)) throw new IOException("Fixed resume requires an actual prior pause with retained file checkpoints.");
            using var pausedMetrics = JsonDocument.Parse(File.ReadAllText(pausePath));
            retainedAtPause = pausedMetrics.RootElement.GetProperty("retainedAfter").Deserialize<RetainedTask[]>(JsonFile.Options) ?? [];
            if (pausedMetrics.RootElement.GetProperty("status").GetString() != "paused"
                || pausedMetrics.RootElement.GetProperty("downloadOwnerId").GetString() != ownerRecord.Id
                || hasChannel && (retainedAtPause.Length == 0 || mode == "fixed-resume" && retainedBefore.Length == 0))
                throw new IOException("The prior run did not leave a real partial transfer for this same owner.");
            if (mode == "fixed-recover")
            {
                using var failed = JsonDocument.Parse(File.ReadAllText(Under(state, "fixed-resume-metrics.json")));
                if (failed.RootElement.GetProperty("status").GetString() != "failed"
                    || failed.RootElement.GetProperty("downloadOwnerId").GetString() != ownerRecord.Id
                    || retainedAtPause.Any(before => !tasks.Tasks.Any(after => before.Id == after.Id && before.Destination == after.Destination
                        && before.Sha256 == after.Sha256 && before.Size == after.Size)))
                    throw new IOException("Recovery requires the actual failed resume and unchanged original paused file identities.");
                Log("Recovering the failed post-download attempt; original pause metrics remain unchanged, no fresh partial transfer is claimed.");
            }
            if (!hasChannel && retainedBefore.Length == 0) Log("Legacy retry only: pip's dependency downloads have no durable launcher file checkpoints.");
        }
        using var stop = new CancellationTokenSource();
        var watch = Stopwatch.StartNew();
        var stageStart = 0d; var stageName = "preparing"; var dependencySeconds = 0d;
        var stages = new List<StageTiming>(); var lastPhase = ""; var phases = new List<object>();
        var paused = false;
        var legacyBodyObservations = new List<LegacyBodyObservation>();
        Task? legacyPauseWatcher = null;
        var legacyPauseArmed = 0;
        string? legacyTemporary = null;
        var knownWheelSizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (mode == "fixed-pause" && !hasChannel)
        {
            foreach (var prepared in Directory.EnumerateFiles(Under(original, @"runtime\cache\dependency-artifacts"), "prepared.json", SearchOption.AllDirectories))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(prepared));
                foreach (var package in document.RootElement.GetProperty("packages").EnumerateArray())
                    if (package.GetProperty("size").GetInt64() is > 0 and var size)
                        knownWheelSizes[package.GetProperty("fileName").GetString()!] = size;
            }
            observeLog = value =>
            {
                const string prefix = "安装临时目录：";
                if (value.StartsWith(prefix, StringComparison.Ordinal))
                {
                    var candidate = Path.GetFullPath(value[prefix.Length..]);
                    if (candidate.StartsWith(Allowed + @"\.anima-temp\", StringComparison.OrdinalIgnoreCase)) legacyTemporary = candidate;
                }
                var match = Regex.Match(value, @"^\s*Downloading ([A-Za-z0-9_.+\-]+\.whl) \(");
                if (!match.Success || legacyTemporary is null || !knownWheelSizes.TryGetValue(match.Groups[1].Value, out var expected)
                    || expected < 20_000_000 || Interlocked.CompareExchange(ref legacyPauseArmed, 1, 0) != 0) return;
                var filename = match.Groups[1].Value;
                var temporary = legacyTemporary;
                legacyPauseWatcher = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(3), stop.Token);
                        for (var attempt = 0; attempt < 40; attempt++)
                        {
                            if (Directory.Exists(temporary))
                            {
                                var partial = Directory.EnumerateFiles(temporary, filename, SearchOption.AllDirectories)
                                    .Select(path => new FileInfo(path)).FirstOrDefault(file => file.Length > 0 && file.Length < expected);
                                if (partial is not null)
                                {
                                    legacyBodyObservations.Add(new(partial.FullName, filename, expected, partial.Length, DateTimeOffset.UtcNow));
                                    Log($"Legacy payload observed before cancellation: {filename} {partial.Length}/{expected} bytes; this is not a durable launcher checkpoint.");
                                    stop.Cancel(); return;
                                }
                            }
                            await Task.Delay(250, stop.Token);
                        }
                        Interlocked.Exchange(ref legacyPauseArmed, 0);
                    }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                    catch (IOException error)
                    { Log("Legacy payload observation unavailable: " + error.Message); Interlocked.Exchange(ref legacyPauseArmed, 0); }
                });
            };
        }
        long observedDependencyReceived = 0;
        void Stage(string name)
        {
            var at = watch.Elapsed.TotalSeconds; stages.Add(new(stageName, stageStart, at - stageStart));
            if (DependencyStage(stageName)) dependencySeconds += at - stageStart;
            stageName = name; stageStart = at;
        }
        void Dependency(object value)
        {
            var json = JsonSerializer.SerializeToElement(value, value.GetType(), JsonFile.Options);
            var phase = json.GetProperty("phase").GetString() ?? "";
            if (phase == "downloading" && json.TryGetProperty("received", out var bytes) && bytes.ValueKind == JsonValueKind.Number && bytes.TryGetInt64(out var received))
                Interlocked.Exchange(ref observedDependencyReceived, Math.Max(observedDependencyReceived, received));
            if (mode == "fixed-pause" && phase == "downloading" && !paused) { paused = true; stop.CancelAfter(TimeSpan.FromSeconds(5)); }
            if (lastPhase == phase) return; lastPhase = phase;
            phases.Add(new { seconds = watch.Elapsed.TotalSeconds, value = json }); Log("DEPENDENCY PHASE " + phase);
        }
        var installer = ConstructInstaller(state, manifest, downloads, Log, new InlineProgress(_ => { }), Stage, Dependency);
        var service = new ServiceManager(state, Log);
        string status = "failed"; string? error = null; double startupSeconds = 0;
        try
        {
            Log($"BEGIN fixed snapshot {label}/{mode}; graph and GPU seed come from current cold report; resolver time excluded by design");
            await tasks.RunOwnerAsync(ownerRecord.Id, token => installer.RepairDependencies(config, snapshot, token), stop.Token);
            Stage("startup");
            var finalPackages = await Inventory(config);
            JsonFile.Write(Under(state, "final-packages.json"), finalPackages);
            JsonFile.Write(Under(state, mode + "-packages.json"), finalPackages);
            var timer = Stopwatch.StartNew(); await StartWithoutModelDownloads(service, config, manifest);
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(30) };
            var stats = await http.GetStringAsync(config.ComfyUrl + "/system_stats");
            using var json = JsonDocument.Parse(stats);
            if (!ServiceManager.SameRoot(json.RootElement, config.ComfyRoot)) throw new IOException("Fixed benchmark server identity differs.");
            File.WriteAllText(Under(state, "system-stats.json"), stats);
            File.WriteAllText(Under(state, "object-info.json"), await http.GetStringAsync(config.ComfyUrl + "/object_info"));
            startupSeconds = timer.Elapsed.TotalSeconds; status = "passed";
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { status = "paused"; Log("Fixed transfer paused; checkpoint retained."); }
        catch (Exception ex) { error = DiagnosticRedaction.Text(ex.ToString()); Log(error); throw; }
        finally
        {
            await service.StopOwnedAsync(); Stage("finished");
            if (legacyPauseWatcher is not null) { stop.Cancel(); await legacyPauseWatcher; }
            var retainedAfter = Retained(tasks);
            var realTransferPause = mode == "fixed-pause" && status == "paused" && retainedAfter.Length > 0;
            var sameFilesResumed = mode == "fixed-resume" && status == "passed" && retainedBefore.Length > 0
                && retainedBefore.All(before => tasks.Tasks.Any(after => before.Id == after.Id && before.Destination == after.Destination
                    && before.Sha256 == after.Sha256 && before.Size == after.Size && after.Status == DownloadTaskStatus.Completed));
            var samePausedFilesCompleted = mode == "fixed-recover" && status == "passed" && retainedAtPause.Length > 0
                && retainedAtPause.All(before => tasks.Tasks.Any(after => before.Id == after.Id && before.Destination == after.Destination
                    && before.Sha256 == after.Sha256 && before.Size == after.Size && after.Status == DownloadTaskStatus.Completed));
            var metrics = new { label, mode, status, error, dependencySeconds, startupSeconds, elapsedSeconds = watch.Elapsed.TotalSeconds,
                excludedPreparationSeconds, managedHttpBodyBytes, observedDependencyReceived, stages, phases, downloadOwnerId = ownerRecord.Id, durableTaskCount = tasks.Tasks.Count,
                retainedBefore, retainedAfter, retainedAtPause, realTransferPause, sameFilesResumed, samePausedFilesCompleted,
                legacyBodyObservations, legacyPayloadPause = mode == "fixed-pause" && status == "paused" && legacyBodyObservations.Count > 0,
                legacyPartialFilesRemaining = legacyBodyObservations.Where(value => File.Exists(value.Path)).Select(value => new { value.Path, bytes = new FileInfo(value.Path).Length }).ToArray(),
                resumeKind = sameFilesResumed ? "same durable package file checkpoints" : samePausedFilesCompleted
                    ? "recovery after prior transfer completed; original paused file identities verified"
                    : mode == "fixed-resume" && !hasChannel ? "legacy pip retry; no partial package checkpoint proof" : "none",
                coreSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Installer).Assembly.Location))).ToLowerInvariant(),
                byteScope = "observed artifact payload body bytes read through Downloads; source metadata and subprocess transfer bytes excluded",
                sourcePolicy = "official PyPI and official Git; same original system/configured proxy; noninteractive Git; no automatic mirror selection in this mechanism comparison",
                config.NetworkMode, config.ProxyAddress,
                scope = "identical frozen graph and restored GPU seed; any local frozen inputs are preserved; excludes graph resolution; hot restores the GPU seed but retains artifact cache",
                completed = DateTimeOffset.UtcNow, logPath };
            JsonFile.Write(Under(state, mode + "-metrics.json"), metrics);
            JsonFile.Write(Path.ChangeExtension(logPath, ".metrics.json"), metrics);
            Log($"RESULT fixed {status}; dependency {dependencySeconds:F2}s; startup {startupSeconds:F2}s");
            if (mode == "fixed-pause" && !realTransferPause) Log("NO TRANSFER RESUME EVIDENCE: this pause did not retain a partially downloaded file.");
        }
        Compare(suite, "fixed-current", "fixed-baseline", "fixed-");
    }

    private static void Compare(string suite, string current, string baseline, string prefix)
    {
        object? CompareMode(string mode)
        {
            var stem = prefix + mode;
            var left = Under(suite, current + "\\state\\" + stem + "-packages.json");
            var right = Under(suite, baseline + "\\state\\" + stem + "-packages.json");
            var x = Under(suite, current + "\\state\\" + stem + "-metrics.json");
            var y = Under(suite, baseline + "\\state\\" + stem + "-metrics.json");
            if (!File.Exists(left) || !File.Exists(right) || !File.Exists(x) || !File.Exists(y)) return null;
            var a = JsonFile.Read<string[]>(left); var b = JsonFile.Read<string[]>(right);
            var same = a.SequenceEqual(b);
            using var cx = JsonDocument.Parse(File.ReadAllText(x)); using var by = JsonDocument.Parse(File.ReadAllText(y));
            var passed = cx.RootElement.GetProperty("status").GetString() == "passed" && by.RootElement.GetProperty("status").GetString() == "passed";
            return new { samePackageVersions = same,
                dependencySpeedRatio = same && passed ? (double?)(by.RootElement.GetProperty("dependencySeconds").GetDouble() / cx.RootElement.GetProperty("dependencySeconds").GetDouble()) : null,
                onlyCurrent = a.Except(b), onlyBaseline = b.Except(a) };
        }
        var cold = CompareMode("cold"); var hot = CompareMode("hot");
        if (cold is null && hot is null) return;
        JsonFile.Write(Under(suite, prefix + "comparison.json"), new { cold, hot,
            note = "Each mode requires its own identical final normalized name/version inventories; bootstrap and GPU preparation are separate stages." });
    }

    private static LauncherConfig WithPython(this LauncherConfig config, string python) { var copy = config.Clone(); copy.Python = python; return copy; }
    private static string[] ModelFiles(LauncherConfig config)
    {
        var models = Path.Combine(config.ComfyRoot, "models");
        var extensions = new HashSet<string>([".safetensors", ".ckpt", ".pt", ".pth", ".onnx", ".bin"], StringComparer.OrdinalIgnoreCase);
        return Directory.Exists(models) ? Directory.EnumerateFiles(models, "*", SearchOption.AllDirectories)
            .Where(path => extensions.Contains(Path.GetExtension(path)) && new FileInfo(path).Length > 0).ToArray() : [];
    }
    private static async Task StartWithoutModelDownloads(ServiceManager service, LauncherConfig config, RuntimeManifest manifest)
    {
        var previous = new[] { "HF_HUB_OFFLINE", "TRANSFORMERS_OFFLINE" }.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        foreach (var key in previous.Keys) Environment.SetEnvironmentVariable(key, "1");
        try { await service.StartComfy(config, manifest, default); }
        finally { foreach (var pair in previous) Environment.SetEnvironmentVariable(pair.Key, pair.Value); }
    }
    private static async Task<string[]> Inventory(LauncherConfig config)
    {
        var text = await Commands.Run(config.Python, ["-B", "-c", "import importlib.metadata as m,re; print(chr(10).join(sorted(re.sub(r'[-_.]+','-',d.metadata['Name']).lower()+'=='+d.version for d in m.distributions() if d.metadata['Name'])))"], config.ComfyRoot, null, default);
        return text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
    private static Installer ConstructInstaller(string state, RuntimeManifest manifest, Downloads downloads, Action<string> log,
        IProgress<DownloadProgress> progress, Action<string> stage, Action<object> dependency)
    {
        var constructor = typeof(Installer).GetConstructors().Single();
        var arguments = constructor.GetParameters().Select(parameter => parameter.Name switch
        {
            "stateDir" => (object)state, "manifest" => manifest, "downloads" => downloads, "log" => log,
            "progress" => progress, "stageChanged" => stage, "dependencyProgress" => Adapt(parameter.ParameterType, dependency), _ => null
        }).ToArray();
        return (Installer)constructor.Invoke(arguments);
    }
    private static DownloadTaskService ConstructTasks(Downloads downloads, string state)
    {
        var constructor = typeof(DownloadTaskService).GetConstructors().Single();
        var arguments = constructor.GetParameters().Select(parameter => parameter.Name switch
        {
            "downloads" => (object)downloads, "stateDirectory" => state, "maxConcurrent" => 2, "maxDependencyConcurrent" => 4, _ => null
        }).ToArray();
        return (DownloadTaskService)constructor.Invoke(arguments);
    }
    private static RetainedTask[] Retained(DownloadTaskService tasks) => tasks.Tasks.Where(task => !File.Exists(task.Destination))
        .Select(task => new RetainedTask(task.Id, task.Destination, task.Size, task.Sha256,
            task.Size - Downloads.RemainingBytes(task.Destination, task.Size, task.Sha256), task.Status.ToString()))
        .Where(task => task.RetainedBytes > 0 && task.RetainedBytes < task.Size).ToArray();
    private static Delegate Adapt(Type type, Action<object> callback)
    {
        var value = Expression.Parameter(type.GenericTypeArguments[0]);
        return Expression.Lambda(type, Expression.Invoke(Expression.Constant(callback), Expression.Convert(value, typeof(object))), value).Compile();
    }
    private static int FreePort() { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
    private static string OwnRoot(string path, bool allowCreate)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(Path.GetFullPath(Allowed).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new IOException("Acceptance is restricted to a unique E:\\CodexDependencyTests child.");
        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Acceptance paths cannot traverse junctions or symlinks.");
        var marker = Path.Combine(full, "owner.txt");
        if (File.Exists(marker)) { if (File.ReadAllText(marker) != Owner) throw new IOException("Suite owner mismatch."); return full; }
        if (!allowCreate || Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any()) throw new IOException("Suite requires a new empty owned directory.");
        Directory.CreateDirectory(full); File.WriteAllText(marker, Owner); return full;
    }
    private static string Under(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Path escapes the owned run.");
        for (var current = path; current is not null && current.Length >= root.Length; current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Owned paths cannot contain reparse points.");
        return path;
    }
    private static void CopyTree(string source, string target)
    {
        if (Directory.Exists(target)) throw new IOException("Snapshot destination already exists.");
        Directory.CreateDirectory(target);
        foreach (var path in Directory.EnumerateFileSystemEntries(source))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Environment seed contains a reparse point.");
            var destination = Path.Combine(target, Path.GetFileName(path));
            if (Directory.Exists(path)) CopyTree(path, destination); else File.Copy(path, destination);
        }
    }
    private static string? FindSnapshot(string runtime, string commit) => Directory.EnumerateFiles(Path.Combine(runtime, "cache"), "resolved.json", SearchOption.AllDirectories)
        .Where(path => { try { var value = JsonFile.Read<ComfyDependencySnapshot>(path); return value.Resolved && value.ComfyCommit == commit; } catch { return false; } })
        .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    private sealed class InlineProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress> { public void Report(DownloadProgress value) => report(value); }
    private sealed class CountingHandler(HttpMessageHandler inner, Action<int> count) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            var original = response.Content;
            var wrapped = new StreamContent(new CountingStream(await original.ReadAsStreamAsync(ct), count));
            foreach (var header in original.Headers) wrapped.Headers.TryAddWithoutValidation(header.Key, header.Value);
            response.Content = wrapped;
            return response;
        }
    }
    private sealed class CountingStream(Stream inner, Action<int> count) : Stream
    {
        public override bool CanRead => inner.CanRead; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => inner.Length; public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int size) { var read = inner.Read(buffer, offset, size); count(read); return read; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { var read = await inner.ReadAsync(buffer, ct); count(read); return read; }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int size, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, size), ct).AsTask();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int size) => throw new NotSupportedException();
    }
}
