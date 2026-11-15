using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Anima.Launcher.Core;

/// <summary>
/// Offline real-pip/uv regressions for a source-run workbench whose old installed
/// distribution still declares Pillow&lt;13 while the selected source requires 13.
/// The tiny Pillow wheels contain test data only; no real Pillow or ComfyUI is used.
/// </summary>
internal static class WorkbenchMetadataRegressionTests
{
    private sealed record HardExitRequest(LauncherConfig Config, ComfyDependencySnapshot Plan,
        Dictionary<string, string> Environment, string Root);

    public static async Task<bool> TryHandleFixture(string[] args)
    {
        if (args.FirstOrDefault() != "--workbench-metadata-hard-exit") return false;
        var request = JsonFile.Read<HardExitRequest>(Path.GetFullPath(args[1]));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var logLock = new object();
        void Log(string line)
        {
            lock (logLock)
            {
                File.AppendAllText(Path.Combine(request.Root, "hard-exit-child.log"), line + Environment.NewLine);
                Console.WriteLine(line);
                if (line.Contains("Successfully installed", StringComparison.Ordinal)
                    && line.Contains("anima-random-studio", StringComparison.OrdinalIgnoreCase))
                {
                    File.WriteAllText(Path.Combine(request.Root, "hard-exit-installed.txt"), "pip completed; caller exits before product cleanup");
                    Environment.Exit(77);
                }
            }
        }
        using var downloads = new Downloads(handler: new RejectNetwork(() => { }));
        using var prepared = await DependencyInstallation.PrepareAsync(request.Config, request.Plan,
            request.Config.RuntimeRoot, downloads, request.Environment, timeout.Token, Log);
        File.WriteAllText(Path.Combine(request.Root, "hard-exit-wheel.txt"),
            prepared.Wheels.Single(wheel => wheel.Package.Name == "anima-random-studio").Path);
        try
        {
            await DependencyInstallation.ApplyPreparedAsync(request.Config, prepared, request.Config.RuntimeRoot,
                request.Environment, _ => Task.FromResult<string?>(null), timeout.Token, Log);
        }
        finally { File.WriteAllText(Path.Combine(request.Root, "hard-exit-finally.txt"), "finally executed"); }
        throw new InvalidOperationException("The hard-exit fixture never reached its installer completion boundary.");
    }

    public static async Task<int> Run(string work, string repository)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = deadline.Token;
        var count = 0;
        void Check(bool condition, string message)
        { if (!condition) throw new Exception("FAIL: " + message); count++; Console.WriteLine("PASS: " + message); }
        async Task Reject(Func<Task> operation, string message)
        {
            Exception? failure = null;
            try { await operation(); }
            catch (Exception error) when (error is InvalidOperationException or InvalidDataException or IOException) { failure = error; }
            Check(failure is not null, message);
        }
        var fixtures = new List<Fixture>();
        var networkCalls = 0;
        using var downloads = new Downloads(handler: new RejectNetwork(() => Interlocked.Increment(ref networkCalls)));
        var uv = await BundledTools.EnsureAsync("uv", Path.Combine(work, "metadata-tools"), ct,
            Path.Combine(repository, "tools", "download-tools"));
        Check(File.Exists(uv), "the uv branch uses the verified locally bundled installer without downloading a tool");
        async Task<ComfyDependencySnapshot> Resolve(Fixture fixture) => await ComfyDependencyResolver.ResolveAsync(
            fixture.Config, new ComfyDependencySnapshot(new string('a', 40), "Pillow>=13,<14\n", "", []),
            Path.Combine(fixture.Config.RuntimeRoot, "cache"), ct, fixture.Log,
            environment: fixture.Environment, includeWorkbench: true);
        Task<PreparedDependencyInstallation> Prepare(Fixture fixture, ComfyDependencySnapshot plan) => DependencyInstallation.PrepareAsync(
            fixture.Config, plan, fixture.Config.RuntimeRoot, downloads, fixture.Environment, ct, fixture.Log);
        Task Apply(Fixture fixture, PreparedDependencyInstallation prepared, Func<CancellationToken, Task<string?>>? installer = null) =>
            DependencyInstallation.ApplyPreparedAsync(fixture.Config, prepared, fixture.Config.RuntimeRoot, fixture.Environment,
                installer ?? (_ => Task.FromResult<string?>(null)), ct, fixture.Log);
        try
        {
            foreach (var oldVersion in new[] { "0.6.0", "1.6.2" })
            {
                var fixture = await CreateAsync(work, repository, ct, oldVersion);
                fixtures.Add(fixture);
                var appBefore = HashTree(fixture.Config.AppRoot);
                var pythonBefore = HashTree(Path.GetDirectoryName(Path.GetDirectoryName(fixture.Config.Python)!)!);
                var gpuBefore = GpuFiles(fixture);
                var constraintsBefore = File.ReadAllBytes(fixture.Environment["PIP_CONSTRAINT"]);
                var plan = await Resolve(fixture);
                Check(Equal(pythonBefore, HashTree(Path.GetDirectoryName(Path.GetDirectoryName(fixture.Config.Python)!)!))
                    && Equal(appBefore, HashTree(fixture.Config.AppRoot)),
                    oldVersion + ": planning editable metadata never changes Python or selected source");

                if (oldVersion == "0.6.0")
                {
                    var oldPlan = plan with
                    {
                        WorkbenchDistributionScopeVersion = 0, WorkbenchDistribution = null,
                        Packages = plan.Packages.Where(package => package.Name != "anima-random-studio").ToArray(),
                        Constraints = plan.Constraints.Where(pin => !pin.StartsWith("anima-random-studio==", StringComparison.OrdinalIgnoreCase))
                            .Append("anima-random-studio==0.6.0").ToArray(),
                    };
                    using (var upgraded = await Prepare(fixture, oldPlan))
                    {
                        Check(upgraded.Snapshot.ComfyCommit == oldPlan.ComfyCommit
                            && upgraded.Snapshot.RequirementsText == oldPlan.RequirementsText
                            && upgraded.Snapshot.ManagerRequirementsText == oldPlan.ManagerRequirementsText
                            && JsonSerializer.Serialize(upgraded.Snapshot.Packages.Where(package => package.Name != "anima-random-studio"))
                                == JsonSerializer.Serialize(oldPlan.Packages)
                            && upgraded.Snapshot.Constraints.Where(pin => !pin.StartsWith("anima-random-studio==", StringComparison.OrdinalIgnoreCase)).Order()
                                .SequenceEqual(oldPlan.Constraints.Where(pin => !pin.StartsWith("anima-random-studio==", StringComparison.OrdinalIgnoreCase)).Order())
                            && upgraded.Snapshot.Packages.Single(package => package.Name == "anima-random-studio").Version == "1.6.2",
                            "a persisted old resolved plan gains only the reviewed workbench bridge while retaining its exact ComfyUI target and every other dependency pin");
                    }
                    Check(Equal(pythonBefore, HashTree(Path.GetDirectoryName(Path.GetDirectoryName(fixture.Config.Python)!)!)),
                        "upgrading an old saved plan during preparation performs no live Python writes");
                    var versionFile = Path.Combine(fixture.Config.AppRoot, "launcher", "version.txt");
                    var versionText = File.ReadAllBytes(versionFile);
                    File.WriteAllText(versionFile, "1.6.3\n");
                    await Reject(async () => { await Prepare(fixture, plan); },
                        "source version drift after review is rejected before any live mutation");
                    Check(Equal(pythonBefore, HashTree(Path.GetDirectoryName(Path.GetDirectoryName(fixture.Config.Python)!)!)),
                        "rejected source version drift preserves every Python environment file");
                    File.WriteAllBytes(versionFile, versionText);
                }

                var prepared = await Prepare(fixture, plan);
                Check(Equal(pythonBefore, HashTree(Path.GetDirectoryName(Path.GetDirectoryName(fixture.Config.Python)!)!))
                    && Equal(appBefore, HashTree(fixture.Config.AppRoot)),
                    oldVersion + ": preparing the bridge and dependency artifacts leaves source and all installed files unchanged");

                if (oldVersion == "0.6.0")
                {
                    var project = Path.Combine(fixture.Config.AppRoot, "pyproject.toml");
                    var original = File.ReadAllBytes(project);
                    File.AppendAllText(project, "\n# source changed after wheel preparation\n");
                    await Reject(() => Apply(fixture, prepared), "source bytes changed after preparation cannot authorize installation");
                    Check(Equal(pythonBefore, HashTree(Path.GetDirectoryName(Path.GetDirectoryName(fixture.Config.Python)!)!)),
                        "application rejects changed reviewed source before altering Python");
                    File.WriteAllBytes(project, original);
                    prepared = await Prepare(fixture, plan);

                    var bridge = prepared.Wheels.Single(wheel => wheel.Package.Name == "anima-random-studio");
                    var bridgePath = bridge.Path;
                    File.AppendAllText(bridgePath, "unreviewed wheel bytes");
                    await Reject(() => Apply(fixture, prepared), "changed bridge wheel bytes cannot pass the frozen artifact digest");
                    Check(Equal(pythonBefore, HashTree(Path.GetDirectoryName(Path.GetDirectoryName(fixture.Config.Python)!)!))
                        && !File.Exists(bridgePath),
                        "tamper rejection preserves Python and removes the one-call editable wheel");
                    prepared = await Prepare(fixture, plan);
                    var disposedBridge = prepared.Wheels.Single(wheel => wheel.Package.Name == "anima-random-studio").Path;
                    prepared.Dispose();
                    Check(!File.Exists(disposedBridge) && Equal(pythonBefore, HashTree(Path.GetDirectoryName(Path.GetDirectoryName(fixture.Config.Python)!)!)),
                        "disposing prepared metadata without applying it removes transient artifacts and changes no installed file");
                    prepared = await Prepare(fixture, plan);
                    var canceledBridge = prepared.Wheels.Single(wheel => wheel.Package.Name == "anima-random-studio").Path;
                    using (var canceled = new CancellationTokenSource())
                    {
                        canceled.Cancel();
                        var stopped = false;
                        try
                        {
                            await DependencyInstallation.ApplyPreparedAsync(fixture.Config, prepared, fixture.Config.RuntimeRoot,
                                fixture.Environment, _ => throw new Exception("Canceled application cannot reach an installer."), canceled.Token, fixture.Log);
                        }
                        catch (OperationCanceledException) { stopped = true; }
                        Check(stopped && !File.Exists(canceledBridge)
                            && Equal(pythonBefore, HashTree(Path.GetDirectoryName(Path.GetDirectoryName(fixture.Config.Python)!)!)),
                            "cancellation before application releases the transient bridge and preserves the full Python tree");
                    }
                    prepared = await Prepare(fixture, plan);
                }

                await Apply(fixture, prepared, oldVersion == "1.6.2" ? _ => Task.FromResult<string?>(uv) : null);
                Check((await fixture.Python(["-m", "pip", "check"], ct)).Contains("No broken requirements"),
                    oldVersion + ": normal offline installation repairs both the source contract and installed package graph");
                Check((await WorkbenchDependencies.InspectWorkbenchAsync(fixture.Config, ct)).Ready,
                    oldVersion + ": selected workbench metadata inspection remains ready after repair");
                var distribution = await ReadDistribution(fixture, ct);
                Check(distribution.GetProperty("version").GetString() == "1.6.2"
                    && distribution.GetProperty("requires").EnumerateArray().Any(value => value.GetString()!.Contains("Pillow>=13,<14", StringComparison.OrdinalIgnoreCase)
                        || value.GetString()!.Contains("Pillow<14,>=13", StringComparison.OrdinalIgnoreCase)),
                    oldVersion + ": installed metadata is refreshed even when the distribution version already matches");
                Check(distribution.GetProperty("requiresPython").GetString() == ">=3.11"
                    && distribution.GetProperty("extras").EnumerateArray().Any(value => value.GetString() == "fixture")
                    && distribution.GetProperty("requires").EnumerateArray().Any(value => value.GetString()!.Contains("anima-fixture-extra")
                        && value.GetString()!.Contains("extra") && value.GetString()!.Contains("python_version")),
                    oldVersion + ": replacement metadata preserves Python bounds and optional dependency markers");
                Check(distribution.GetProperty("installer").GetString() == (oldVersion == "1.6.2" ? "uv" : "pip")
                    && !Directory.EnumerateFiles(Path.GetDirectoryName(Path.GetDirectoryName(fixture.Config.Python)!)!, "_anima_workbench_editable*.pyc", SearchOption.AllDirectories).Any(),
                    oldVersion + ": the expected real installer records valid editable metadata without compiling bridge bytecode");
                var origin = distribution.GetProperty("origin");
                Check(origin.GetProperty("dir_info").GetProperty("editable").GetBoolean()
                    && Path.TrimEndingDirectorySeparator(Path.GetFullPath(new Uri(origin.GetProperty("url").GetString()!).LocalPath))
                        .Equals(Path.TrimEndingDirectorySeparator(fixture.Config.AppRoot), StringComparison.OrdinalIgnoreCase)
                    && distribution.GetProperty("sourceImported").GetBoolean(),
                    oldVersion + ": repaired editable distribution imports the actual selected source package");
                Check(distribution.GetProperty("projectCount").GetInt32() == 1
                    && !distribution.GetProperty("sourceOnPath").GetBoolean()
                    && distribution.GetProperty("metadataPath").GetString()!.StartsWith(
                        Path.GetDirectoryName(Path.GetDirectoryName(fixture.Config.Python)!)!, StringComparison.OrdinalIgnoreCase),
                    oldVersion + ": source egg-info cannot become a duplicate installed distribution through editable startup");
                Check(Equal(appBefore, HashTree(fixture.Config.AppRoot)) && Equal(gpuBefore, GpuFiles(fixture)),
                    oldVersion + ": metadata repair preserves selected source bytes and every GPU fixture file");
                Check(constraintsBefore.SequenceEqual(File.ReadAllBytes(fixture.Environment["PIP_CONSTRAINT"])),
                    oldVersion + ": frozen per-operation constraints override old workbench and Pillow pins without rewriting the user's constraint file");
                var gpu = DependencyPlans.Versions(await DependencyPlans.InventoryAsync(fixture.Config, ct));
                Check(gpu["torch"] == "2.11.0+cu128" && gpu["torchvision"] == "0.26.0+cu128" && gpu["torchaudio"] == "2.11.0+cu128",
                    oldVersion + ": the protected GPU version baseline is unchanged");

                var beforeRetry = HashTree(Path.GetDirectoryName(Path.GetDirectoryName(fixture.Config.Python)!)!);
                var retry = await Prepare(fixture, plan);
                var retryInstallerCalls = 0;
                await Apply(fixture, retry, _ => { retryInstallerCalls++; return Task.FromResult<string?>(null); });
                Check(retryInstallerCalls == 0 && Equal(beforeRetry, HashTree(Path.GetDirectoryName(Path.GetDirectoryName(fixture.Config.Python)!)!)),
                    oldVersion + ": a verified same-plan retry performs no package writes");
                if (oldVersion == "0.6.0")
                {
                    var site = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(fixture.Config.Python)!)!, "Lib", "site-packages");
                    var sharedWheel = Wheel(Path.Combine(fixture.Root, "wheels"), "anima-fixture-shared-owner", "1.0", "anima_fixture_shared_owner");
                    AddOwnedFile(sharedWheel, "_anima_workbench_editable.pth", File.ReadAllBytes(Path.Combine(site, "_anima_workbench_editable.pth")));
                    await fixture.Python(["-m", "pip", "install", "--no-index", "--no-deps", sharedWheel], ct);
                    var sharedBefore = HashTree(Path.GetDirectoryName(Path.GetDirectoryName(fixture.Config.Python)!)!);
                    await Reject(async () => { await WorkbenchDistribution.PreparePlanAsync(fixture.Config, ct); },
                        "another distribution owning identical editable startup bytes prevents workbench registration takeover");
                    Check(Equal(sharedBefore, HashTree(Path.GetDirectoryName(Path.GetDirectoryName(fixture.Config.Python)!)!)),
                        "shared RECORD ownership rejection preserves both distributions and their shared file");
                }
            }

            var unbound = await CreateAsync(work, repository, ct, sourceMatches: false);
            fixtures.Add(unbound);
            var unboundBefore = HashTree(Path.GetDirectoryName(Path.GetDirectoryName(unbound.Config.Python)!)!);
            await Reject(async () => { var plan = await Resolve(unbound); await Prepare(unbound, plan); },
                "an editable distribution belonging to another source cannot be adopted by this workbench");
            Check(Equal(unboundBefore, HashTree(Path.GetDirectoryName(Path.GetDirectoryName(unbound.Config.Python)!)!)),
                "rejecting an unrelated source preserves its complete Python environment");
            await unbound.Python(["-m", "pip", "install", "--no-index", "--no-deps", "--force-reinstall", unbound.OldWheel], ct);
            var nonEditableBefore = HashTree(Path.GetDirectoryName(Path.GetDirectoryName(unbound.Config.Python)!)!);
            await Reject(async () => { var plan = await Resolve(unbound); await Prepare(unbound, plan); },
                "a wheel installation with archive provenance cannot be relabeled as this source's editable distribution");
            Check(Equal(nonEditableBefore, HashTree(Path.GetDirectoryName(Path.GetDirectoryName(unbound.Config.Python)!)!)),
                "unsupported non-editable provenance is rejected without package removal or metadata rewriting");

            var unrelated = await CreateAsync(work, repository, ct);
            fixtures.Add(unrelated);
            var unrelatedWheel = Wheel(Path.Combine(unrelated.Root, "wheels"), "anima-fixture-unrelated", "1.0", "anima_fixture_unrelated", "Pillow<13");
            await unrelated.Python(["-m", "pip", "install", "--no-index", "--no-deps", unrelatedWheel], ct);
            var unrelatedMetadata = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(unrelated.Config.Python)!)!,
                "Lib", "site-packages", "anima_fixture_unrelated-1.0.dist-info", "METADATA");
            var unrelatedBefore = File.ReadAllBytes(unrelatedMetadata);
            var unrelatedPlan = await Resolve(unrelated);
            var unrelatedPrepared = await Prepare(unrelated, unrelatedPlan);
            CommandFailureException? unrelatedFailure = null;
            try { await Apply(unrelated, unrelatedPrepared); }
            catch (CommandFailureException error) { unrelatedFailure = error; }
            Check(unrelatedFailure is not null && unrelatedFailure.StandardOutput.Contains("anima-fixture-unrelated 1.0", StringComparison.OrdinalIgnoreCase)
                && !unrelatedFailure.StandardOutput.Contains("anima-random-studio", StringComparison.OrdinalIgnoreCase),
                "full pip check still reports an unrelated real package conflict after workbench metadata is repaired");
            Check(unrelatedBefore.SequenceEqual(File.ReadAllBytes(unrelatedMetadata)),
                "controlled workbench repair does not rewrite or remove an unrelated distribution");
            var failedDistribution = await ReadDistribution(unrelated, ct);
            Check(failedDistribution.GetProperty("origin").GetProperty("dir_info").GetProperty("editable").GetBoolean()
                && failedDistribution.GetProperty("sourceImported").GetBoolean(),
                "an unrelated pip check failure leaves the repaired workbench registered to its editable source");
            using (var retryAfterGraphFailure = await Prepare(unrelated, unrelatedPlan))
                Check(retryAfterGraphFailure.PackageCount == unrelatedPrepared.PackageCount,
                    "the same frozen plan remains eligible for repair after full-graph validation fails");

            var interrupted = await CreateAsync(work, repository, ct);
            fixtures.Add(interrupted);
            var interruptedPlan = await Resolve(interrupted);
            var interruptedPrepared = await Prepare(interrupted, interruptedPlan);
            var interruptedBridge = interruptedPrepared.Wheels.Single(wheel => wheel.Package.Name == "anima-random-studio").Path;
            var interruptedApp = HashTree(interrupted.Config.AppRoot);
            var interruptedGpu = GpuFiles(interrupted);
            using (var stopAfterInstallation = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var sawInstall = false;
                var stopped = false;
                void StopLog(string line)
                {
                    interrupted.Log(line);
                    if (line.Contains("Successfully installed", StringComparison.Ordinal)
                        && line.Contains("anima-random-studio", StringComparison.OrdinalIgnoreCase))
                    { sawInstall = true; stopAfterInstallation.Cancel(); }
                }
                try
                {
                    await DependencyInstallation.ApplyPreparedAsync(interrupted.Config, interruptedPrepared,
                        interrupted.Config.RuntimeRoot, interrupted.Environment, _ => Task.FromResult<string?>(null),
                        stopAfterInstallation.Token, StopLog);
                }
                catch (OperationCanceledException) { stopped = true; }
                Check(sawInstall && stopped, "real pip installation can be interrupted immediately after it replaces the old workbench distribution");
            }
            var interruptedDistribution = await ReadDistribution(interrupted, ct);
            Check(interruptedDistribution.GetProperty("origin").GetProperty("dir_info").GetProperty("editable").GetBoolean()
                && interruptedDistribution.GetProperty("sourceImported").GetBoolean()
                && interruptedDistribution.GetProperty("projectCount").GetInt32() == 1,
                "post-install cancellation completes only exact owned editable registration so recovery can identify the new distribution");
            Check(!File.Exists(interruptedBridge)
                && !File.Exists(Path.Combine(interrupted.Config.RuntimeRoot, "cache", "dependency-artifacts", interruptedPrepared.PlanIdentity, "installed.json"))
                && Equal(interruptedApp, HashTree(interrupted.Config.AppRoot)) && Equal(interruptedGpu, GpuFiles(interrupted)),
                "post-install cancellation removes its wheel, preserves source and GPU files, and never marks the whole dependency operation complete");
            var resumed = await Prepare(interrupted, interruptedPlan);
            await Apply(interrupted, resumed);
            Check((await interrupted.Python(["-m", "pip", "check"], ct)).Contains("No broken requirements"),
                "the original frozen plan can finish successfully after actual installation was cancelled");

            var crashed = await CreateAsync(work, repository, ct);
            fixtures.Add(crashed);
            var crashPlan = await Resolve(crashed);
            var crashApp = HashTree(crashed.Config.AppRoot);
            var crashGpu = GpuFiles(crashed);
            var requestPath = Path.Combine(crashed.Root, "hard-exit-request.json");
            JsonFile.Write(requestPath, new HardExitRequest(crashed.Config, crashPlan, crashed.Environment, crashed.Root));
            CommandFailureException? processExit = null;
            try
            {
                await Commands.Run(Commands.Find("dotnet.exe") ?? throw new FileNotFoundException("dotnet is required for process interruption acceptance."),
                    [typeof(WorkbenchMetadataRegressionTests).Assembly.Location, "--workbench-metadata-hard-exit", requestPath],
                    repository, crashed.Log, ct, crashed.Environment, TimeSpan.FromSeconds(75));
            }
            catch (CommandFailureException error) { processExit = error; }
            Check(processExit?.ExitCode == 77 && File.Exists(Path.Combine(crashed.Root, "hard-exit-installed.txt"))
                && !File.Exists(Path.Combine(crashed.Root, "hard-exit-finally.txt")),
                "an independent caller exits after actual pip replacement while skipping every finally cleanup path");
            var abandonedWheel = File.ReadAllText(Path.Combine(crashed.Root, "hard-exit-wheel.txt"));
            Check(File.Exists(abandonedWheel), "hard process exit preserves the exact transient wheel needed to identify interrupted registration");
            var afterCrash = HashTree(Path.GetDirectoryName(Path.GetDirectoryName(crashed.Config.Python)!)!);
            var persisted = JsonFile.Read<HardExitRequest>(requestPath);
            var recovered = await ComfyDependencyResolver.ResolveAsync(persisted.Config, persisted.Plan,
                Path.Combine(persisted.Config.RuntimeRoot, "cache"), ct, crashed.Log,
                environment: persisted.Environment, includeWorkbench: true);
            var restartPrepared = await Prepare(crashed, recovered);
            Check(Equal(afterCrash, HashTree(Path.GetDirectoryName(Path.GetDirectoryName(crashed.Config.Python)!)!))
                && recovered.ComfyCommit == crashPlan.ComfyCommit,
                "restart resolution and preparation recognize only the persisted interrupted bridge without changing Python or the fixed ComfyUI target");
            restartPrepared.Dispose();
            await SimulateOriginWriteInterruption(crashed, abandonedWheel, ct);
            var betweenOriginAndRecord = HashTree(Path.GetDirectoryName(Path.GetDirectoryName(crashed.Config.Python)!)!);
            var partialPlan = await ComfyDependencyResolver.ResolveAsync(persisted.Config, persisted.Plan,
                Path.Combine(persisted.Config.RuntimeRoot, "cache"), ct, crashed.Log,
                environment: persisted.Environment, includeWorkbench: true);
            using (var partialPrepared = await Prepare(crashed, partialPlan))
                Check(Equal(betweenOriginAndRecord, HashTree(Path.GetDirectoryName(Path.GetDirectoryName(crashed.Config.Python)!)!)),
                    "a recorded interruption between origin and RECORD replacements is recognized without repairing files during resolution or preparation");
            await crashed.Python(["-m", "pip", "uninstall", "--yes", "anima-random-studio"], ct);
            Check(!DependencyPlans.Versions(await DependencyPlans.InventoryAsync(crashed.Config, ct)).ContainsKey("anima-random-studio"),
                "normal pip uninstall reproduces the installer window where the owned workbench distribution is temporarily missing");
            var afterUninstall = HashTree(Path.GetDirectoryName(Path.GetDirectoryName(crashed.Config.Python)!)!);
            var missingPlan = await ComfyDependencyResolver.ResolveAsync(persisted.Config, persisted.Plan,
                Path.Combine(persisted.Config.RuntimeRoot, "cache"), ct, crashed.Log,
                environment: persisted.Environment, includeWorkbench: true);
            var missingPrepared = await Prepare(crashed, missingPlan);
            Check(Equal(afterUninstall, HashTree(Path.GetDirectoryName(Path.GetDirectoryName(crashed.Config.Python)!)!))
                && missingPrepared.Snapshot.ComfyCommit == crashPlan.ComfyCommit,
                "only the persisted owned interruption authorizes a missing registration plan, and preparation still leaves Python unchanged");
            await Apply(crashed, missingPrepared);
            var crashDistribution = await ReadDistribution(crashed, ct);
            Check((await crashed.Python(["-m", "pip", "check"], ct)).Contains("No broken requirements")
                && crashDistribution.GetProperty("origin").GetProperty("dir_info").GetProperty("editable").GetBoolean()
                && crashDistribution.GetProperty("sourceImported").GetBoolean()
                && crashDistribution.GetProperty("projectCount").GetInt32() == 1,
                "the persisted plan safely reinstalls and completes editable registration after a hard process exit");
            Check(Equal(crashApp, HashTree(crashed.Config.AppRoot)) && Equal(crashGpu, GpuFiles(crashed)),
                "hard-exit recovery preserves selected source files and protected GPU package contents");
            Check(networkCalls == 0, "all metadata regression cases use only local fixture wheels without HTTP downloads");
            JsonFile.Write(Path.Combine(work, "workbench-metadata-regression.json"), new { count, networkCalls,
                fixtures = fixtures.Select(fixture => new { fixture.Root, fixture.Config.Python }).ToArray() });
            return count;
        }
        finally { foreach (var fixture in fixtures) fixture.SaveLogs(); }
    }

    private static Dictionary<string, string> HashTree(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(root, path), path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.OrdinalIgnoreCase);
    private static bool Equal(Dictionary<string, string> left, Dictionary<string, string> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var hash) && pair.Value == hash);
    private static Dictionary<string, string> GpuFiles(Fixture fixture) => HashTree(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(fixture.Config.Python)!)!, "Lib", "site-packages"))
        .Where(pair => pair.Key.StartsWith("torch", StringComparison.OrdinalIgnoreCase) || pair.Key.StartsWith("anima_fixture_torch", StringComparison.OrdinalIgnoreCase))
        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    private static async Task<JsonElement> ReadDistribution(Fixture fixture, CancellationToken ct)
    {
        const string script = """
import importlib.metadata as m,json,os,pathlib,sys
d=m.distribution('anima-random-studio')
origin=json.loads(d.read_text('direct_url.json'))
source=pathlib.Path(sys.argv[1]).resolve()
os.environ['ANIMA_FIXTURE_ALLOW_IMPORT']='1'
import anima_webui
projects=[p for p in m.distributions() if (p.metadata.get('Name') or '').lower().replace('_','-')=='anima-random-studio']
print(json.dumps(dict(version=d.version,requires=d.requires or [],requiresPython=d.metadata.get('Requires-Python'),extras=d.metadata.get_all('Provides-Extra') or [],installer=d.read_text('INSTALLER').strip(),origin=origin,sourceOnPath=any(pathlib.Path(p).resolve()==source for p in sys.path),sourceImported=pathlib.Path(anima_webui.__file__).resolve()==source/'anima_webui'/'__init__.py' and anima_webui.FIXTURE_SOURCE=='selected-workbench',projectCount=len(projects),metadataPath=str(d._path.resolve()))))
""";
        return JsonSerializer.Deserialize<JsonElement>(await fixture.Python(["-B", "-c", script, fixture.Config.AppRoot], ct));
    }

    private static async Task SimulateOriginWriteInterruption(Fixture fixture, string wheel, CancellationToken ct)
    {
        // Preserve the genuine installer intent and reproduce the one valid two-file
        // boundary: new origin bytes on disk, original RECORD still on disk. No
        // product hook is needed, and no path outside this fixture is accessed.
        const string script = """
import base64,csv,hashlib,importlib.metadata as m,io,json,pathlib,sys
app=pathlib.Path(sys.argv[1]).resolve()
runtime=pathlib.Path(sys.argv[2]).resolve()
transport=pathlib.Path(sys.argv[3]).resolve().as_uri()
pending_file,items=next((p,json.loads(p.read_text(encoding='utf-8-sig'))) for p in (runtime/'cache'/'workbench-registration').glob('*.json'))
intent=next(item for item in items if item['transportUrl']==transport)
d=m.distribution('anima-random-studio')
folder=pathlib.Path(d._path).resolve()
assert folder.is_relative_to(pathlib.Path(sys.prefix).resolve())
record=folder/'RECORD'
original=record.read_bytes()
origin=(json.dumps({'url':app.as_uri(),'dir_info':{'editable':True}},sort_keys=True)+'\n').encode('utf-8')
relative=folder.name+'/direct_url.json'
rows=[row for row in csv.reader(original.decode('utf-8').splitlines()) if row[0].replace('\\','/')!=relative]
rows.append([relative,'sha256='+base64.urlsafe_b64encode(hashlib.sha256(origin).digest()).rstrip(b'=').decode('ascii'),str(len(origin))])
text=io.StringIO(newline='');csv.writer(text).writerows(rows)
intent.update(originalRecordSha256=hashlib.sha256(original).hexdigest(),finalOriginSha256=hashlib.sha256(origin).hexdigest(),finalRecordSha256=hashlib.sha256(text.getvalue().encode('utf-8')).hexdigest())
pending_file.write_text(json.dumps(items,indent=2),encoding='utf-8')
(folder/'direct_url.json').write_bytes(origin)
assert record.read_bytes()==original
print('Recorded exact origin/RECORD interruption boundary')
""";
        await fixture.Python(["-B", "-c", script, fixture.Config.AppRoot, fixture.Config.RuntimeRoot, wheel], ct);
    }

    internal sealed record Fixture(string Root, LauncherConfig Config, Dictionary<string, string> Environment,
        string OldWheel, List<string> Logs)
    {
        public void Log(string line) { lock (Logs) Logs.Add(line); }
        public Task<string> Python(IEnumerable<string> arguments, CancellationToken ct) =>
            Commands.Run(Config.Python, arguments, Config.ComfyRoot, Log, ct, Environment, TimeSpan.FromSeconds(45));
        public void SaveLogs() { lock (Logs) File.WriteAllLines(Path.Combine(Root, "execution.log"), Logs); }
    }

    internal static async Task<Fixture> CreateAsync(string work, string repository, CancellationToken ct,
        string oldVersion = "0.6.0", string currentVersion = "1.6.2", bool sourceMatches = true)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This fixture uses an independent Windows Python venv.");
        var host = Path.Combine(repository, ".venv", "Scripts", "python.exe");
        if (!File.Exists(host)) throw new FileNotFoundException("The repository Python is required only to create an independent fixture venv.", host);
        var root = Path.GetFullPath(Path.Combine(work, "wbmeta-" + Guid.NewGuid().ToString("N")[..12]));
        var comfy = Path.Combine(root, "ComfyUI");
        var app = Path.Combine(root, "selected-workbench");
        var wheels = Path.Combine(root, "wheels");
        var temporary = Path.Combine(root, "temp");
        foreach (var directory in new[] { comfy, app, wheels, temporary }) Directory.CreateDirectory(directory);
        var oldDependency = Wheel(wheels, "Pillow", "12.0", "anima_fixture_pillow");
        Wheel(wheels, "Pillow", "13.0", "anima_fixture_pillow");
        var gpu = new[]
        {
            Wheel(wheels, "torch", "2.11.0+cu128", "anima_fixture_torch"),
            Wheel(wheels, "torchvision", "0.26.0+cu128", "anima_fixture_torchvision"),
            Wheel(wheels, "torchaudio", "2.11.0+cu128", "anima_fixture_torchaudio"),
        };
        var oldSource = sourceMatches ? app : Path.Combine(root, "other-workbench");
        Directory.CreateDirectory(oldSource);
        var oldWorkbench = EditableWheel(wheels, oldSource, oldVersion, "Pillow<13");
        WriteProject(oldSource, oldVersion, "Pillow<13", bootstrapBackend: true);
        // A tiny standard-library PEP 660 backend lets pip create the real editable
        // direct_url.json itself. It is used only while establishing the old state.
        File.WriteAllText(Path.Combine(oldSource, "fixture_backend.py"), "import pathlib,shutil\n"
            + "def get_requires_for_build_editable(config_settings=None): return []\n"
            + "def build_editable(wheel_directory,config_settings=None,metadata_directory=None):\n"
            + " source=pathlib.Path(" + JsonSerializer.Serialize(oldWorkbench) + ")\n"
            + " shutil.copyfile(source,pathlib.Path(wheel_directory)/source.name)\n return source.name\n"
            + "def build_wheel(*args,**kwargs): raise AssertionError('Only editable setup is authorized')\n");
        Directory.CreateDirectory(Path.Combine(app, "anima_webui"));
        var sourceModule = Path.Combine(app, "anima_webui", "__init__.py");
        File.WriteAllText(sourceModule, "import os\nif os.environ.get('ANIMA_FIXTURE_ALLOW_IMPORT')!='1':\n"
            + " raise AssertionError('Dependency repair must not execute selected workbench source')\nFIXTURE_SOURCE='selected-workbench'\n");
        var sourceEggInfo = Path.Combine(app, "anima_random_studio.egg-info");
        Directory.CreateDirectory(sourceEggInfo);
        File.WriteAllText(Path.Combine(sourceEggInfo, "PKG-INFO"), "Metadata-Version: 2.1\nName: anima-random-studio\nVersion: 9.9.9\nRequires-Dist: Pillow<1\n\n");
        File.WriteAllText(Path.Combine(sourceEggInfo, "SOURCES.txt"), "pyproject.toml\nanima_webui/__init__.py\n");
        File.WriteAllText(Path.Combine(comfy, "requirements.txt"), "Pillow>=13,<14\n");
        var environment = new Dictionary<string, string>
        {
            ["PIP_NO_INDEX"] = "1", ["PIP_FIND_LINKS"] = wheels, ["PIP_CONFIG_FILE"] = "NUL",
            ["PIP_EXTRA_INDEX_URL"] = "", ["PIP_CONSTRAINT"] = "", ["PIP_CACHE_DIR"] = Path.Combine(root, "pip-cache"),
            ["UV_NO_CONFIG"] = "1", ["UV_OFFLINE"] = "1", ["UV_PYTHON_DOWNLOADS"] = "never",
            ["TEMP"] = temporary, ["TMP"] = temporary, ["PYTHONNOUSERSITE"] = "1",
            ["PYTHONDONTWRITEBYTECODE"] = "1", ["PYTHONPATH"] = "", ["PYTHONHOME"] = null!,
        };
        var logs = new List<string>();
        void Log(string line) { lock (logs) logs.Add(line); }
        var venv = Path.Combine(comfy, ".venv");
        await Commands.Run(host, ["-B", "-m", "venv", venv], root, Log, ct, environment, TimeSpan.FromSeconds(60));
        var config = new LauncherConfig { ComfyRoot = comfy, Python = Path.Combine(venv, "Scripts", "python.exe"),
            AppRoot = app, RuntimeRoot = Path.Combine(root, "runtime"), SourceId = "official",
            NetworkMode = "custom", ProxyAddress = "http://127.0.0.1:9" };
        await Commands.Run(config.Python, new[] { "-m", "pip", "install", "--no-index", "--no-deps", "--disable-pip-version-check", oldDependency }.Concat(gpu),
            comfy, Log, ct, environment, TimeSpan.FromSeconds(45));
        await Commands.Run(config.Python, ["-m", "pip", "install", "--no-index", "--no-deps", "--no-build-isolation", "--disable-pip-version-check", "-e", oldSource],
            comfy, Log, ct, environment, TimeSpan.FromSeconds(45));
        // Emulate a source update after installing the old editable distribution.
        WriteProject(app, currentVersion, "Pillow>=13,<14");
        var oldConstraints = Path.Combine(comfy, ".anima-constraints.txt");
        File.WriteAllText(oldConstraints, "anima-random-studio==" + oldVersion + "\nPillow==12.0\n");
        environment["PIP_CONSTRAINT"] = oldConstraints;
        return new(root, config, environment, oldWorkbench, logs);
    }

    private static void WriteProject(string app, string version, string requirement, bool bootstrapBackend = false)
    {
        Directory.CreateDirectory(Path.Combine(app, "launcher"));
        File.WriteAllText(Path.Combine(app, "launcher", "version.txt"), version + "\n");
        File.WriteAllText(Path.Combine(app, "pyproject.toml"), (bootstrapBackend
            ? "[build-system]\nrequires=[]\nbuild-backend='fixture_backend'\nbackend-path=['.']\n"
            : "[build-system]\nrequires=['setuptools>=68']\nbuild-backend='setuptools.build_meta'\n")
            + "[project]\nname='anima-random-studio'\ndynamic=['version']\nrequires-python='>=3.11'\ndependencies=['" + requirement + "']\n"
            + "[project.optional-dependencies]\nfixture=['anima-fixture-extra>=1; python_version >= \"3.11\"']\n"
            + "[tool.setuptools.dynamic]\nversion={file='launcher/version.txt'}\n");
    }

    private static string EditableWheel(string directory, string source, string version, string requirement)
    {
        var path = Wheel(directory, "anima-random-studio", version, "anima_fixture_old_workbench", requirement);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        const string finderName = "__editable___anima_random_studio_fixture";
        var finder = "import importlib.abc,importlib.util,pathlib,sys\n"
            + "class Finder(importlib.abc.MetaPathFinder):\n @classmethod\n def find_spec(cls,fullname,path=None,target=None):\n"
            + "  if fullname!='anima_webui': return None\n"
            + "  root=pathlib.Path(" + JsonSerializer.Serialize(Path.Combine(source, "anima_webui")) + ")\n"
            + "  return importlib.util.spec_from_file_location(fullname,root/'__init__.py',submodule_search_locations=[str(root)])\n"
            + "sys.meta_path.append(Finder)\n";
        var extraEntries = new Dictionary<string, byte[]>
        {
            ["__editable__.anima_random_studio.pth"] = Encoding.UTF8.GetBytes("import " + finderName + "\n"),
            [finderName + ".py"] = Encoding.UTF8.GetBytes(finder),
        };
        foreach (var entry in extraEntries) { using var output = archive.CreateEntry(entry.Key).Open(); output.Write(entry.Value); }
        var record = archive.GetEntry("anima_random_studio-" + version + ".dist-info/RECORD")!;
        string text;
        using (var reader = new StreamReader(record.Open())) text = reader.ReadToEnd();
        record.Delete();
        using var replacement = new StreamWriter(archive.CreateEntry(record.FullName).Open(), new UTF8Encoding(false));
        replacement.Write(text + string.Concat(extraEntries.Select(entry => entry.Key + ",sha256="
            + Convert.ToBase64String(SHA256.HashData(entry.Value)).TrimEnd('=').Replace('+', '-').Replace('/', '_')
            + "," + entry.Value.Length + "\n")));
        return path;
    }

    private static void AddOwnedFile(string wheel, string name, byte[] bytes)
    {
        using var archive = ZipFile.Open(wheel, ZipArchiveMode.Update);
        using (var output = archive.CreateEntry(name).Open()) output.Write(bytes);
        var record = archive.Entries.Single(entry => entry.FullName.EndsWith(".dist-info/RECORD", StringComparison.Ordinal));
        var recordName = record.FullName;
        string text;
        using (var reader = new StreamReader(record.Open())) text = reader.ReadToEnd();
        record.Delete();
        using var writer = new StreamWriter(archive.CreateEntry(recordName).Open(), new UTF8Encoding(false));
        writer.Write(text + name + ",sha256=" + Convert.ToBase64String(SHA256.HashData(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_')
            + "," + bytes.Length + "\n");
    }

    private static string Wheel(string directory, string name, string version, string module, params string[] requirements)
    {
        var normalized = name.ToLowerInvariant().Replace('-', '_');
        var prefix = normalized + "-" + version + ".dist-info/";
        var entries = new Dictionary<string, byte[]>
        {
            [module + "/__init__.py"] = Encoding.UTF8.GetBytes("raise AssertionError('Metadata fixture modules must never be imported')\n"),
            [prefix + "METADATA"] = Encoding.UTF8.GetBytes("Metadata-Version: 2.1\nName: " + name + "\nVersion: " + version + "\n"
                + string.Concat(requirements.Select(value => "Requires-Dist: " + value + "\n")) + "\n"),
            [prefix + "WHEEL"] = Encoding.UTF8.GetBytes("Wheel-Version: 1.0\nGenerator: anima-metadata-regression-fixture\nRoot-Is-Purelib: true\nTag: py3-none-any\n"),
        };
        entries[prefix + "RECORD"] = Encoding.UTF8.GetBytes(string.Concat(entries.Select(pair => pair.Key + ",sha256="
            + Convert.ToBase64String(SHA256.HashData(pair.Value)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + "," + pair.Value.Length + "\n"))
            + prefix + "RECORD,,\n");
        var path = Path.Combine(directory, normalized + "-" + version + "-py3-none-any.whl");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var entry in entries) { using var output = archive.CreateEntry(entry.Key).Open(); output.Write(entry.Value); }
        return path;
    }

    private sealed class RejectNetwork(Action attempted) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { attempted(); throw new InvalidOperationException("This fixture may only use locally generated wheels."); }
    }
}
