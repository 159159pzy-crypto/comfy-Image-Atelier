using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Anima.Launcher.Core;

internal static class DependencyInstallationTests
{
    public static async Task<int> Run(string work)
    {
        var checks = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
        async Task Reject<T>(Func<Task> action, string name) where T : Exception
        {
            try { await action(); }
            catch (T) { checks++; return; }
            throw new Exception(name);
        }
        void RejectState(Action action, string name)
        {
            try { action(); }
            catch (Exception error) when (error is InvalidOperationException or InvalidDataException) { checks++; return; }
            throw new Exception(name);
        }

        var root = Path.Combine(work, "dependency-installation");
        Directory.CreateDirectory(root);
        var wheels = Path.Combine(root, "wheels");
        Directory.CreateDirectory(wheels);
        var shared1 = Wheel(wheels, "anima_fixture_shared", "1.0");
        var shared2 = Wheel(wheels, "anima_fixture_shared", "2.0");
        Wheel(wheels, "anima_fixture_core", "1.0", "anima-fixture-shared>=1");
        Wheel(wheels, "anima_fixture_manager", "1.0", "anima-fixture-core==1.0");
        var direct = Wheel(wheels, "anima_fixture_direct", "1.0");
        var extra = Wheel(wheels, "anima_fixture_extra", "1.0");
        OverlappingWheel(wheels, "anima_fixture_overlap_one", "first owner");
        OverlappingWheel(wheels, "anima_fixture_overlap_two", "second owner with different size");
        var directUri = new Uri(direct).AbsoluteUri;
        var names = new[] { "anima-fixture-shared", "anima-fixture-core", "anima-fixture-manager", "anima-fixture-direct", "anima-fixture-overlap-one", "anima-fixture-overlap-two" };
        var seed = new ComfyDependencySnapshot(new string('a', 40),
            "anima-fixture-core==1.0\nanima-fixture-direct @ " + directUri + "\n",
            "anima-fixture-manager==1.0\nanima-fixture-overlap-one==1.0\nanima-fixture-overlap-two==1.0\n", []);

        var buildPackage = new DependencyPackage { Name = "sam-2", Version = "1.0", Kind = "git",
            Url = "https://example.invalid/sam2", Commit = new string('b', 40) };
        var buildPlan = seed with { PythonIdentity = "fixture-python", Constraints = ["torch==2.11.0+cu128"] };
        var buildFlags = new Dictionary<string, string> { ["SAM2_BUILD_CUDA"] = "0", ["CMAKE_ARGS"] = "-DFIXTURE=ON" };
        string? EmptyBuildEnvironment(string _) => null;
        var buildKey = DependencyInstallation.WheelBuildIdentity(buildPackage, buildPlan, buildFlags, EmptyBuildEnvironment);
        Check(buildKey.Length == 64 && buildKey.All(Uri.IsHexDigit)
            && buildKey == DependencyInstallation.WheelBuildIdentity(buildPackage, buildPlan,
                buildFlags.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value), EmptyBuildEnvironment),
            "wheel build identity is a stable digest independent of explicit environment ordering");
        foreach (var name in new[] { "SAM2_BUILD_CUDA", "SAM2_BUILD_ALLOW_ERRORS", "TORCH_CUDA_ARCH_LIST", "CUDA_HOME", "FORCE_CUDA",
                     "CXX", "CC", "CMAKE_ARGS", "CFLAGS", "CPPFLAGS", "LDFLAGS" })
        {
            var changed = new Dictionary<string, string>(buildFlags) { [name] = "changed-build-setting" };
            Check(buildKey != DependencyInstallation.WheelBuildIdentity(buildPackage, buildPlan, changed, EmptyBuildEnvironment),
                "changing explicit build setting invalidates cached wheels: " + name);
            Check(DependencyInstallation.WheelBuildIdentity(buildPackage, buildPlan, new Dictionary<string, string>(),
                    variable => variable == name ? "inherited-one" : null)
                != DependencyInstallation.WheelBuildIdentity(buildPackage, buildPlan, new Dictionary<string, string>(),
                    variable => variable == name ? "inherited-two" : null),
                "changing inherited build setting invalidates cached wheels: " + name);
        }
        Check(buildKey == DependencyInstallation.WheelBuildIdentity(buildPackage, buildPlan, buildFlags,
                variable => variable is "SAM2_BUILD_CUDA" or "CMAKE_ARGS" ? "ignored-inherited-setting" : null),
            "explicit build settings override inherited settings when selecting cached wheels");
        var emptyOverride = new Dictionary<string, string> { ["CFLAGS"] = "" };
        Check(DependencyInstallation.WheelBuildIdentity(buildPackage, buildPlan, emptyOverride, EmptyBuildEnvironment)
            == DependencyInstallation.WheelBuildIdentity(buildPackage, buildPlan, emptyOverride, variable => variable == "CFLAGS" ? "-O3" : null),
            "an explicitly empty build setting suppresses inherited compiler flags");
        var removedOverride = new Dictionary<string, string> { ["CFLAGS"] = null! };
        Check(DependencyInstallation.WheelBuildIdentity(buildPackage, buildPlan, removedOverride, EmptyBuildEnvironment)
            == DependencyInstallation.WheelBuildIdentity(buildPackage, buildPlan, removedOverride, variable => variable == "CFLAGS" ? "-O3" : null),
            "an explicit removal suppresses inherited compiler flags in the build identity");
        if (OperatingSystem.IsWindows())
            Check(DependencyInstallation.WheelBuildIdentity(buildPackage, buildPlan,
                    new Dictionary<string, string> { ["cflags"] = "-O2" }, variable => variable == "CFLAGS" ? "-O3" : null)
                == DependencyInstallation.WheelBuildIdentity(buildPackage, buildPlan,
                    new Dictionary<string, string> { ["CFLAGS"] = "-O2" }, EmptyBuildEnvironment),
                "Windows build variable overrides use case-insensitive process environment semantics");

        JsonElement Report(string url, string hash = "", string name = "anima-fixture-shared", string version = "1.0", string schema = "1") =>
            JsonSerializer.SerializeToElement(new
            {
                version = schema,
                install = new[] { new { metadata = new { name, version }, download_info = new { url, archive_info = new { hashes = new { sha256 = hash } } }, is_direct = false } },
            });
        var localReport = await DependencyPlans.ReadReportAsync(Report(new Uri(shared1).AbsoluteUri), default);
        var actualHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(shared1))).ToLowerInvariant();
        Check(localReport.Length == 1 && localReport[0].Sha256 == actualHash && localReport[0].Size == new FileInfo(shared1).Length,
            "pip report local artifacts acquire their real size and SHA-256 before installation");
        await Reject<InvalidDataException>(() => DependencyPlans.ReadReportAsync(Report(new Uri(shared1).AbsoluteUri, new string('0', 64)), default),
            "pip report rejects a local artifact whose supplied digest does not match its bytes");
        await Reject<InvalidDataException>(() => DependencyPlans.ReadReportAsync(Report(new Uri(shared1).AbsoluteUri, schema: "2"), default),
            "unknown pip report schemas cannot authorize an installation");
        await Reject<InvalidDataException>(() => DependencyPlans.ReadReportAsync(Report(new Uri(shared1).AbsoluteUri, name: "bad/name"), default),
            "pip report rejects an invalid package name");
        await Reject<InvalidDataException>(() => DependencyPlans.ReadReportAsync(Report(new Uri(shared1).AbsoluteUri, version: "1.0;command"), default),
            "pip report rejects an invalid package version");
        await Reject<InvalidDataException>(() => DependencyPlans.ReadReportAsync(Report("https://packages.example.invalid/shared.whl", "not-a-sha256"), default),
            "pip report rejects malformed remote artifact digests without contacting a server");
        var duplicate = JsonSerializer.SerializeToElement(new { version = "1", install = new[] { Report(new Uri(shared1).AbsoluteUri).GetProperty("install")[0], Report(new Uri(shared1).AbsoluteUri).GetProperty("install")[0] } });
        await Reject<InvalidDataException>(() => DependencyPlans.ReadReportAsync(duplicate, default),
            "duplicate normalized package names cannot form a fixed dependency plan");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            await Reject<OperationCanceledException>(() => DependencyPlans.ReadReportAsync(Report(new Uri(shared1).AbsoluteUri), canceled.Token),
                "report parsing preserves explicit cancellation");
        }

        var gpuPlan = seed with
        {
            Resolved = true, PlanVersion = 1,
            EnvironmentPackages = ["torch==2.11.0+cu128", "torchvision==0.26.0+cu128", "torchaudio==2.11.0+cu128", "fixture-base==1"],
            Packages = [new() { Name = "fixture-target", Version = "2" }],
        };
        DependencyPlans.ValidateEnvironment(gpuPlan, gpuPlan.EnvironmentPackages);
        DependencyPlans.ValidateEnvironment(gpuPlan, gpuPlan.EnvironmentPackages.Append("fixture-target==2"), installed: true);
        Check(true, "unchanged GPU baseline and already installed target versions are valid resume states");
        RejectState(() => DependencyPlans.ValidateEnvironment(gpuPlan, gpuPlan.EnvironmentPackages.Append("unreviewed==1")),
            "external package additions invalidate the reviewed dependency plan");
        RejectState(() => DependencyPlans.ValidateEnvironment(gpuPlan, gpuPlan.EnvironmentPackages.Select(x => x == "fixture-base==1" ? "fixture-base==2" : x)),
            "unrelated baseline version changes invalidate the reviewed dependency plan");
        RejectState(() => DependencyPlans.ValidateEnvironment(gpuPlan, gpuPlan.EnvironmentPackages.Where(x => x != "fixture-base==1")),
            "removed baseline packages invalidate the reviewed dependency plan");
        RejectState(() => DependencyPlans.ValidateEnvironment(gpuPlan, gpuPlan.EnvironmentPackages.Append("fixture-target==3")),
            "an unreviewed target version cannot be mistaken for a resumed installation");
        RejectState(() => DependencyPlans.ValidateEnvironment(gpuPlan, gpuPlan.EnvironmentPackages, installed: true),
            "installation is incomplete until every fixed target package is present");
        foreach (var gpu in new[] { "torch", "torchvision", "torchaudio" })
        {
            var gpuChange = gpuPlan with { Packages = [new() { Name = gpu, Version = "99.0" }] };
            RejectState(() => DependencyPlans.ValidateEnvironment(gpuChange,
                    gpuPlan.EnvironmentPackages.Select(line => line.StartsWith(gpu + "==", StringComparison.Ordinal) ? gpu + "==99.0" : line)),
                "a target entry cannot authorize changing installed GPU package " + gpu);
        }
        RejectState(() => DependencyPlans.Versions(["my_package==1", "my-package==2"]),
            "conflicting distributions remain conflicting after package name normalization");
        RejectState(() => DependencyPlans.ValidateEnvironment(gpuPlan with { PlanVersion = 99 }, gpuPlan.EnvironmentPackages),
            "unknown persisted dependency plan versions are rejected");

        var installedGraph = gpuPlan.EnvironmentPackages.Append("fixture-target==2").ToArray();
        var withTriton = installedGraph.Append("triton-windows==3.3.1").ToArray();
        var afterTriton = DependencyPlans.RecordAuthorizedAdditions(gpuPlan, withTriton);
        DependencyPlans.ValidateEnvironment(afterTriton, withTriton, installed: true);
        Check(afterTriton.EnvironmentPackages.Contains("triton-windows==3.3.1")
            && !gpuPlan.EnvironmentPackages.Contains("triton-windows==3.3.1") && afterTriton.Packages.SequenceEqual(gpuPlan.Packages),
            "launcher-owned Triton additions permit resume while preserving the original snapshot and reviewed targets");
        RejectState(() => DependencyPlans.ValidateEnvironment(afterTriton, withTriton.Append("external-after-checkpoint==1")),
            "a later external addition remains unapproved after a legitimate post-install checkpoint");
        RejectState(() => DependencyPlans.ValidateEnvironment(afterTriton,
                withTriton.Select(line => line.StartsWith("triton-windows==", StringComparison.Ordinal) ? "triton-windows==99" : line)),
            "a recorded optional dependency becomes part of the protected resume baseline");
        foreach (var protectedName in new[] { "fixture-base", "fixture-target", "torch", "torchvision", "torchaudio" })
        {
            RejectState(() => DependencyPlans.RecordAuthorizedAdditions(gpuPlan,
                    withTriton.Select(line => line.StartsWith(protectedName + "==", StringComparison.Ordinal) ? protectedName + "==99" : line).ToArray()),
                "authorized additions cannot hide a changed baseline target or GPU package " + protectedName);
            RejectState(() => DependencyPlans.RecordAuthorizedAdditions(gpuPlan,
                    withTriton.Where(line => !line.StartsWith(protectedName + "==", StringComparison.Ordinal)).ToArray()),
                "authorized additions cannot hide a deleted baseline target or GPU package " + protectedName);
        }
        var interruptedPoststep = gpuPlan;
        var partialPoststepInventory = installedGraph;
        try
        {
            partialPoststepInventory = installedGraph.Append("triton-windows==3.3.1").Append("node-build-helper==1").ToArray();
            throw new OperationCanceledException("fixture pause after optional packages were installed");
        }
        catch (OperationCanceledException) { }
        finally { interruptedPoststep = DependencyPlans.RecordAuthorizedAdditions(interruptedPoststep, partialPoststepInventory); }
        DependencyPlans.ValidateEnvironment(interruptedPoststep, partialPoststepInventory, installed: true);
        Check(interruptedPoststep.EnvironmentPackages.Contains("node-build-helper==1"),
            "a stopped optional node installer can checkpoint its completed additions and safely resume after cancellation");
        RejectState(() => DependencyPlans.ValidateEnvironment(interruptedPoststep, partialPoststepInventory.Append("external-after-pause==1")),
            "a cancellation checkpoint does not authorize packages installed later outside the launcher");
        RejectState(() => DependencyPlans.RecordAuthorizedAdditions(gpuPlan,
                installedGraph.Concat(["new_package==1", "new-package==2"]).ToArray()),
            "a post-install checkpoint rejects conflicting newly added distributions instead of freezing ambiguity");

        var repository = FindRepository();
        var hostPython = Path.Combine(repository, ".venv", "Scripts", "python.exe");
        if (!OperatingSystem.IsWindows() || !File.Exists(hostPython))
            throw new InvalidOperationException("Offline dependency installation acceptance requires the repository Windows .venv Python.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = deadline.Token;
        var networkCalls = 0;
        using var downloads = new Downloads(handler: new RejectNetwork(() => Interlocked.Increment(ref networkCalls)));
        var uvRoot = Path.Combine(root, "bundled-tools");
        var uv = await BundledTools.EnsureAsync("uv", uvRoot, ct, Path.Combine(repository, "tools", "download-tools"));
        Check(File.Exists(uv), "fast installer is materialized from a verified local bundled archive");
        var allProgress = new List<DependencyProgress>();
        var allLogs = new List<string>();
        void Log(string line) { lock (allLogs) allLogs.Add(line); }
        void Progress(DependencyProgress value) { lock (allProgress) allProgress.Add(value); }

        async Task<(LauncherConfig Config, ComfyDependencySnapshot Plan, Dictionary<string, string> Env, string Runtime)> Prepare(string id)
        {
            var environment = Path.Combine(root, id);
            Directory.CreateDirectory(environment);
            var python = Path.Combine(environment, ".venv", "Scripts", "python.exe");
            await Commands.Run(hostPython, ["-m", "venv", Path.Combine(environment, ".venv")], environment, Log, ct, timeout: TimeSpan.FromSeconds(60));
            Check(File.Exists(python) && !Path.GetFullPath(python).Equals(Path.GetFullPath(hostPython), StringComparison.OrdinalIgnoreCase),
                id + " creates an independent interpreter without modifying the repository environment");
            var runtime = Path.Combine(root, id + "-runtime");
            var config = new LauncherConfig { ComfyRoot = environment, RuntimeRoot = runtime, Python = python, SourceId = "official", NetworkMode = "custom", ProxyAddress = "http://127.0.0.1:9" };
            var env = new Dictionary<string, string>
            {
                ["PIP_NO_INDEX"] = "1", ["PIP_FIND_LINKS"] = wheels, ["PIP_CONFIG_FILE"] = "NUL",
                ["PIP_EXTRA_INDEX_URL"] = "", ["PIP_CONSTRAINT"] = "", ["UV_NO_CONFIG"] = "1",
                ["UV_OFFLINE"] = "1", ["UV_PYTHON_DOWNLOADS"] = "never",
            };
            var cache = Path.Combine(runtime, "cache");
            var coreOnly = await ComfyDependencyResolver.ResolveAsync(config, seed, cache, ct, Log, environment: env, dependencyProgress: Progress);
            Check(coreOnly.Constraints.Contains("anima-fixture-shared==2.0"), id + " real offline pip initially selects the latest compatible shared package");
            var node = Path.Combine(environment, "node-requirements.txt");
            File.WriteAllText(node, "anima-fixture-shared<2\n");
            var plan = await ComfyDependencyResolver.ResolveAsync(config, coreOnly, cache, ct, Log, [node], env, dependencyProgress: Progress);
            Check(plan.Resolved && plan.PlanVersion == 1 && plan.Packages.Length == names.Length
                && plan.Constraints.Contains("anima-fixture-shared==1.0") && plan.NodeRequirementsIdentity.Length == 64,
                id + " real joint core Manager and node resolution freezes a complete compatible artifact plan");
            Check(plan.Packages.All(package => package.Kind == "wheel" && package.Size > 0 && package.Sha256.Length == 64 && new Uri(package.Url).IsFile)
                && plan.Packages.Single(package => package.Name == "anima-fixture-direct").Direct
                && plan.Packages.Where(package => package.Name != "anima-fixture-direct").All(package => !package.Direct),
                id + " fixed artifacts retain sizes hashes and direct versus index provenance");
            Check(plan.EnvironmentPackages.All(line => !line.StartsWith("anima-fixture-", StringComparison.OrdinalIgnoreCase)),
                id + " dry-run resolution does not install the selected packages into its baseline");
            return (config, plan, env, runtime);
        }

        var fast = await Prepare("uv-environment");
        await DependencyInstallation.InstallAsync(fast.Config, fast.Plan, fast.Runtime, downloads, fast.Env,
            _ => Task.FromResult<string?>(uv), ct, Log, Progress);
        var fastInventory = await DependencyPlans.InventoryAsync(fast.Config, ct);
        var fastVersions = DependencyPlans.Versions(fastInventory);
        Check(names.All(name => fastVersions.TryGetValue(name, out var version) && version == "1.0"),
            "real uv offline installation produces every selected package at exactly its frozen version");
        await VerifyOrigins(fast.Config, fast.Plan, "uv", ct);
        Check(true, "uv index packages lose transport-only direct_url records while true direct origins and RECORD hashes are preserved");
        Check(allProgress.Any(p => p.Phase == "installing") && allProgress.Any(p => p.Phase == "verifying")
            && allProgress[^1].Status == DependencyProgressStatus.Complete,
            "local installation reports installation and validation before confirmed completion");

        var disconnected = new Dictionary<string, string>(fast.Env) { ["PIP_FIND_LINKS"] = Path.Combine(root, "absent-links") };
        var cached = await ComfyDependencyResolver.ResolveAsync(fast.Config, fast.Plan, Path.Combine(fast.Runtime, "cache"), ct,
            Log, [Path.Combine(fast.Config.ComfyRoot, "node-requirements.txt")], disconnected, dependencyProgress: Progress);
        Check(ReferenceEquals(cached, fast.Plan), "hot resume reuses a validated fixed plan even when package discovery is unavailable");
        var installedModules = names.Select(name => Path.Combine(fast.Config.ComfyRoot, ".venv", "Lib", "site-packages", name.Replace('-', '_'), "__init__.py")).ToArray();
        var sentinelTime = new DateTime(2003, 4, 5, 6, 7, 8, DateTimeKind.Utc);
        foreach (var path in installedModules) File.SetLastWriteTimeUtc(path, sentinelTime);
        var beforeRetry = installedModules.ToDictionary(path => path, File.GetLastWriteTimeUtc);
        var retryLogStart = allLogs.Count;
        var retryInstallerCalls = 0;
        await DependencyInstallation.InstallAsync(fast.Config, cached, fast.Runtime, downloads, disconnected,
            _ => { retryInstallerCalls++; return Task.FromResult<string?>(uv); }, ct, Log, Progress);
        JsonFile.Write(Path.Combine(root, "hot-retry-evidence.json"), new { beforeRetry,
            afterRetry = installedModules.ToDictionary(path => path, File.GetLastWriteTimeUtc), logs = allLogs.Skip(retryLogStart).ToArray() });
        Check(beforeRetry.All(pair => File.GetLastWriteTimeUtc(pair.Key) == pair.Value) && retryInstallerCalls == 0
            && !allLogs.Skip(retryLogStart).Any(line => line.StartsWith("Installed ") || line.StartsWith("Uninstalled ")),
            "a completed same-plan retry verifies file contents without invoking uv or replacing any installed package");
        Check((await DependencyPlans.InventoryAsync(fast.Config, ct)).SequenceEqual(fastInventory),
            "hot local retry leaves already installed package versions and baseline unchanged");
        await VerifyOrigins(fast.Config, fast.Plan, "uv", ct);
        Check(true, "hot retry preserves valid provenance and RECORD metadata");

        var receiptPath = Path.Combine(fast.Runtime, "cache", "dependency-artifacts", DependencyInstallation.PlanIdentity(fast.Plan), "installed.json");
        using (var receipt = JsonDocument.Parse(File.ReadAllText(receiptPath)))
            Check(receipt.RootElement.GetProperty("identity").GetString() == DependencyInstallation.PlanIdentity(fast.Plan)
                && receipt.RootElement.GetProperty("pythonIdentity").GetString() == fast.Plan.PythonIdentity
                && receipt.RootElement.GetProperty("python").GetString()!.Contains("uv-environment", StringComparison.OrdinalIgnoreCase)
                && receipt.RootElement.GetProperty("packages").EnumerateObject().All(pair =>
                    pair.Value.GetProperty("wheelSha256").GetString()!.Length == 64 && pair.Value.GetProperty("files").GetArrayLength() >= 3),
                "completed receipts bind plan interpreter wheel hashes and the complete installed file inventory");
        await Commands.Run(fast.Config.Python, ["-m", "pip", "install", "--no-index", "--no-deps", "--disable-pip-version-check", extra],
            fast.Config.ComfyRoot, Log, ct, fast.Env);
        var afterNode = DependencyPlans.RecordAuthorizedAdditions(fast.Plan, await DependencyPlans.InventoryAsync(fast.Config, ct));
        await DependencyInstallation.InstallAsync(fast.Config, afterNode, fast.Runtime, downloads, disconnected,
            _ => throw new Exception("Authorized node additions must not discard completed dependency receipts"), ct, Log, Progress);
        Check(beforeRetry.All(pair => File.GetLastWriteTimeUtc(pair.Key) == pair.Value),
            "a legitimate later node addition preserves complete dependency receipts and avoids reinstall on resume");

        // A same-version distribution and even a rewritten RECORD cannot hide damaged content.
        var sharedModule = installedModules.Single(path => path.Contains("anima_fixture_shared"));
        var directModule = installedModules.Single(path => path.Contains("anima_fixture_direct"));
        File.WriteAllText(sharedModule, "__version__ = 'bad'\n");
        var sharedRecord = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(sharedModule))!, "anima_fixture_shared-1.0.dist-info", "RECORD");
        File.WriteAllLines(sharedRecord, File.ReadAllLines(sharedRecord).Where(line => !line.StartsWith("anima_fixture_shared/__init__.py,")));
        var directLength = new FileInfo(directModule).Length;
        var directStamp = File.GetLastWriteTimeUtc(directModule);
        File.WriteAllText(directModule, File.ReadAllText(directModule).Replace("'1.0'", "'9.9'"));
        File.SetLastWriteTimeUtc(directModule, directStamp);
        Check(new FileInfo(directModule).Length == directLength && File.GetLastWriteTimeUtc(directModule) == directStamp,
            "the content corruption fixture preserves both installed file size and modification time");
        var receiptBeforePause = File.ReadAllBytes(receiptPath);
        await Reject<OperationCanceledException>(() => DependencyInstallation.InstallAsync(fast.Config, afterNode, fast.Runtime, downloads, disconnected,
                _ => throw new OperationCanceledException("Pause before repairing damaged files"), ct, Log, Progress),
            "an interrupted same-version repair cannot report success or bless damaged files");
        Check(receiptBeforePause.SequenceEqual(File.ReadAllBytes(receiptPath)), "pause does not replace the last fully completed receipt");
        var repairLogs = allLogs.Count;
        await DependencyInstallation.InstallAsync(fast.Config, afterNode, fast.Runtime, downloads, disconnected,
            _ => Task.FromResult<string?>(uv), ct, Log, Progress);
        Check(File.ReadAllText(sharedModule).Contains("'1.0'") && File.ReadAllText(directModule).Contains("'1.0'")
            && installedModules.Except([sharedModule, directModule]).All(path => File.GetLastWriteTimeUtc(path) == beforeRetry[path])
            && allLogs.Skip(repairLogs).Any(line => line.StartsWith("Installed 2 packages", StringComparison.Ordinal)),
            "same-size same-mtime content changes and altered RECORD are repaired while healthy packages stay untouched");
        await VerifyOrigins(fast.Config, fast.Plan, "uv", ct);
        Check(true, "targeted repairs restore exact direct origins and index provenance");
        var outsideModule = Path.Combine(root, "outside-direct-module.py");
        File.Copy(directModule, outsideModule);
        var outsideBytes = File.ReadAllBytes(outsideModule);
        var sitePackages = Path.GetDirectoryName(Path.GetDirectoryName(directModule))!;
        var directRecord = Path.Combine(sitePackages, "anima_fixture_direct-1.0.dist-info", "RECORD");
        var originalRecord = File.ReadAllBytes(directRecord);
        File.AppendAllText(directRecord, Path.GetRelativePath(sitePackages, outsideModule).Replace('\\', '/') + ",sha256="
            + Convert.ToBase64String(SHA256.HashData(outsideBytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + "," + outsideBytes.Length + "\n");
        try
        {
            await Reject<CommandFailureException>(() => DependencyInstallation.InstallAsync(fast.Config, afterNode, fast.Runtime, downloads, disconnected,
                    _ => throw new Exception("Out-of-environment path must not reach an installer"), ct, Log, Progress),
                "a RECORD path escaping the selected interpreter is rejected before reuse or automatic repair");
            Check(outsideBytes.SequenceEqual(File.ReadAllBytes(outsideModule)), "path boundary rejection preserves the external file");
        }
        finally { File.WriteAllBytes(directRecord, originalRecord); }
        File.Delete(directModule);
        var linked = false;
        try { File.CreateSymbolicLink(directModule, outsideModule); linked = true; }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            File.Copy(outsideModule, directModule);
            Log("File symlink integration unavailable in this Windows test process: " + error.GetType().Name);
        }
        if (linked)
        {
            await Reject<CommandFailureException>(() => DependencyInstallation.InstallAsync(fast.Config, afterNode, fast.Runtime, downloads, disconnected,
                    _ => throw new Exception("Unsafe path must be rejected before invoking an installer"), ct, Log, Progress),
                "an identical-content file symlink is rejected before either reuse or automatic repair");
            Check(outsideBytes.SequenceEqual(File.ReadAllBytes(outsideModule)), "unsafe path rejection leaves external bytes unchanged");
            File.Delete(directModule);
            File.Copy(outsideModule, directModule);
        }
        var overlapFile = Path.Combine(fast.Config.ComfyRoot, ".venv", "Lib", "site-packages", "anima_fixture_overlapping", "shared.bin");
        var beforeOverlapRepair = installedModules.ToDictionary(path => path, File.GetLastWriteTimeUtc);
        File.WriteAllText(overlapFile, "unreviewed shared file content");
        var overlapLogs = allLogs.Count;
        await DependencyInstallation.InstallAsync(fast.Config, afterNode, fast.Runtime, downloads, disconnected,
            _ => Task.FromResult<string?>(uv), ct, Log, Progress);
        Check(File.ReadAllText(overlapFile) is "first owner" or "second owner with different size"
            && installedModules.Where(path => !path.Contains("anima_fixture_overlap_")).All(path => File.GetLastWriteTimeUtc(path) == beforeOverlapRepair[path])
            && allLogs.Skip(overlapLogs).Any(line => line.StartsWith("Installed 2 packages", StringComparison.Ordinal)),
            "overlapping files accept only a reviewed owner's content and corruption reinstalls only their two owners");
        File.Delete(receiptPath);
        File.Delete(directModule);
        await DependencyInstallation.InstallAsync(fast.Config, afterNode, fast.Runtime, downloads, disconnected,
            _ => Task.FromResult<string?>(uv), ct, Log, Progress);
        Check(File.Exists(directModule) && File.Exists(receiptPath),
            "a half-installed same-URL package without a completed receipt is reinstalled rather than accepted by uv's version shortcut");
        File.WriteAllText(receiptPath, "[]");
        File.Delete(directModule);
        await DependencyInstallation.InstallAsync(fast.Config, afterNode, fast.Runtime, downloads, disconnected,
            _ => Task.FromResult<string?>(uv), ct, Log, Progress);
        Check(File.Exists(directModule) && File.ReadAllText(receiptPath).StartsWith('{'),
            "a malformed completion receipt cannot authorize a skip or prevent repairing an incomplete package");

        var fallback = await Prepare("pip-environment");
        await DependencyInstallation.InstallAsync(fallback.Config, fallback.Plan, fallback.Runtime, downloads, fallback.Env,
            _ => Task.FromResult<string?>(null), ct, Log, Progress);
        var fallbackInventory = await DependencyPlans.InventoryAsync(fallback.Config, ct);
        var fallbackVersions = DependencyPlans.Versions(fallbackInventory);
        Check(names.All(name => fallbackVersions.TryGetValue(name, out var version) && version == "1.0"),
            "pip fallback installs the same verified local artifacts without a second dependency solve");
        await VerifyOrigins(fallback.Config, fallback.Plan, "pip", ct);
        Check(true, "pip fallback preserves true direct provenance and removes only transport provenance with correct RECORD entries");
        var fallbackModules = names.Select(name => Path.Combine(fallback.Config.ComfyRoot, ".venv", "Lib", "site-packages", name.Replace('-', '_'), "__init__.py")).ToArray();
        foreach (var path in fallbackModules) File.SetLastWriteTimeUtc(path, sentinelTime);
        await DependencyInstallation.InstallAsync(fallback.Config, fallback.Plan, fallback.Runtime, downloads, fallback.Env,
            _ => throw new Exception("Complete pip installs also reuse their verified receipt"), ct, Log, Progress);
        Check(fallbackModules.All(path => File.GetLastWriteTimeUtc(path) == sentinelTime),
            "a completed pip fallback install is a content-verified no-op on resume");
        var fallbackDirect = fallbackModules.Single(path => path.Contains("anima_fixture_direct"));
        File.Delete(fallbackDirect);
        await DependencyInstallation.InstallAsync(fallback.Config, fallback.Plan, fallback.Runtime, downloads, fallback.Env,
            _ => Task.FromResult<string?>(null), ct, Log, Progress);
        Check(File.ReadAllText(fallbackDirect).Contains("'1.0'")
            && fallbackModules.Where(path => path != fallbackDirect).All(path => File.GetLastWriteTimeUtc(path) == sentinelTime),
            "pip repairs only a damaged same-version direct package without reinstalling healthy dependencies");

        var corrupted = fallback.Plan with { Packages = fallback.Plan.Packages.Select((p, i) => i == 0 ? p with { Sha256 = new string('0', 64) } : p).ToArray() };
        var unexpectedInstallerCalls = 0;
        await Reject<InvalidDataException>(() => DependencyInstallation.InstallAsync(fallback.Config, corrupted, fallback.Runtime, downloads, fallback.Env,
            _ => { unexpectedInstallerCalls++; return Task.FromResult<string?>(uv); }, ct, Log, Progress),
            "local artifact hash corruption prevents starting an installer");
        Check(unexpectedInstallerCalls == 0 && (await DependencyPlans.InventoryAsync(fallback.Config, ct)).SequenceEqual(fallbackInventory),
            "artifact rejection leaves the selected environment unchanged");

        var fallbackPlans = Path.Combine(fallback.Runtime, "cache", "dependency-plans");
        var originalSnapshots = Directory.GetFiles(fallbackPlans, "resolved.json", SearchOption.AllDirectories)
            .ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        await Commands.Run(fallback.Config.Python, ["-m", "pip", "install", "--no-index", "--no-deps", "--disable-pip-version-check", extra],
            fallback.Config.ComfyRoot, Log, ct, fallback.Env);
        await Reject<InvalidOperationException>(() => ComfyDependencyResolver.ResolveAsync(fallback.Config, fallback.Plan,
                Path.Combine(fallback.Runtime, "cache"), ct, Log, [Path.Combine(fallback.Config.ComfyRoot, "node-requirements.txt")], fallback.Env),
            "an old resolved snapshot still rejects an externally added distribution");
        var reviewedAgain = await ComfyDependencyResolver.ResolveAsync(fallback.Config, seed,
            Path.Combine(fallback.Runtime, "cache"), ct, Log, [Path.Combine(fallback.Config.ComfyRoot, "node-requirements.txt")], fallback.Env);
        Check(reviewedAgain.Resolved && reviewedAgain.EnvironmentPackages.Any(line =>
                line.Replace('_', '-').Equals("anima-fixture-extra==1.0", StringComparison.OrdinalIgnoreCase)),
            "an explicit unresolved recheck accepts the current environment as a new reviewed baseline");
        Check(originalSnapshots.All(pair => File.Exists(pair.Key) && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pair.Key))) == pair.Value)
            && Directory.GetFiles(fallbackPlans, "resolved.json", SearchOption.AllDirectories).Length > originalSnapshots.Count,
            "rechecking a changed baseline creates a separate cache entry and preserves every old frozen snapshot");

        await Commands.Run(fast.Config.Python, ["-m", "pip", "install", "--no-index", "--no-deps", "--disable-pip-version-check", shared2],
            fast.Config.ComfyRoot, Log, ct, fast.Env);
        await Reject<InvalidOperationException>(() => DependencyInstallation.InstallAsync(fast.Config, fast.Plan, fast.Runtime, downloads, fast.Env,
            _ => { unexpectedInstallerCalls++; return Task.FromResult<string?>(uv); }, ct, Log, Progress),
            "external package drift is rejected before frozen installation retries can overwrite it");
        Check(DependencyPlans.Versions(await DependencyPlans.InventoryAsync(fast.Config, ct))["anima-fixture-shared"] == "2.0"
            && unexpectedInstallerCalls == 0,
            "a changed external target version remains untouched after the resume guard rejects it");
        Check(networkCalls == 0, "real offline dependency resolution installation and hot retry never reach the HTTP download transport");
        checks += await RunSourceBuildTests(work);
        File.WriteAllLines(Path.Combine(root, "execution.log"), allLogs);
        JsonFile.Write(Path.Combine(root, "evidence.json"), new { checks, networkCalls, uv, fast = fast.Config.Python,
            fallback = fallback.Config.Python, fastInventory, fallbackInventory, phases = allProgress.Select(item => item.Phase).Distinct().ToArray() });
        return checks;
    }

    public static async Task<int> RunSourceBuildTests(string work)
    {
        var checks = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
        async Task Reject<T>(Func<Task> action, string name) where T : Exception
        {
            try { await action(); } catch (T) { checks++; return; }
            throw new Exception(name);
        }
        var root = Path.GetFullPath(Path.Combine(work, "source-build"));
        Directory.CreateDirectory(root);
        var logs = new List<string>();
        void Log(string line) { lock (logs) logs.Add(line); }
        var git = Commands.Find("git.exe") ?? throw new FileNotFoundException("Git is required for the local source build fixture.");
        var hostPython = Path.Combine(FindRepository(), ".venv", "Scripts", "python.exe");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = deadline.Token;
        var env = new Dictionary<string, string>
        {
            ["PIP_NO_INDEX"] = "1", ["PIP_CONFIG_FILE"] = "NUL", ["PIP_EXTRA_INDEX_URL"] = "",
            ["ANIMA_FIXTURE_GIT"] = git, ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_GLOBAL"] = "NUL",
            ["GIT_AUTHOR_NAME"] = "Offline Fixture", ["GIT_AUTHOR_EMAIL"] = "fixture@example.invalid",
            ["GIT_COMMITTER_NAME"] = "Offline Fixture", ["GIT_COMMITTER_EMAIL"] = "fixture@example.invalid",
        };
        async Task<string> Git(string cwd, params string[] args) => await Commands.Run(git,
            new[] { "-c", "core.longpaths=true", "-c", "protocol.file.allow=always" }.Concat(args), cwd, null, ct, env);
        var module = Path.Combine(root, "module"); Directory.CreateDirectory(module);
        File.WriteAllText(Path.Combine(module, "member.txt"), "local pinned module");
        await Git(module, "init"); await Git(module, "add", "."); await Git(module, "commit", "-m", "module");
        var moduleCommit = (await Git(module, "rev-parse", "HEAD")).Trim();
        var repository = Path.Combine(root, "repository"); Directory.CreateDirectory(repository);
        var project = Path.Combine(repository, "projects", "package"); Directory.CreateDirectory(project);
        foreach (var name in new[] { "build", "dist", "sibling" })
        { Directory.CreateDirectory(Path.Combine(repository, name)); File.WriteAllText(Path.Combine(repository, name, "tracked.txt"), name); }
        File.WriteAllText(Path.Combine(project, "pyproject.toml"), "[build-system]\nrequires=[]\nbuild-backend='backend'\nbackend-path=['.']\n");
        const string backend = """
import base64,ctypes,hashlib,json,os,pathlib,subprocess,sys,time,zipfile
root=pathlib.Path(__file__).resolve().parents[2]
prefix='anima_fixture_source-1.0.dist-info/'
def get_requires_for_build_wheel(config_settings=None): return []
def files():
 return {'anima_fixture_source/__init__.py':b"__version__ = '1.0'\n",prefix+'METADATA':b'Metadata-Version: 2.1\nName: anima-fixture-source\nVersion: 1.0\n',prefix+'WHEEL':b'Wheel-Version: 1.0\nRoot-Is-Purelib: true\nTag: py3-none-any\n'}
def prepare_metadata_for_build_wheel(metadata_directory,config_settings=None):
 for name,data in files().items():
  if name.startswith(prefix):
   target=pathlib.Path(metadata_directory)/name; target.parent.mkdir(parents=True,exist_ok=True); target.write_bytes(data)
 return prefix.rstrip('/')
def build_wheel(wheel_directory,config_settings=None,metadata_directory=None):
 if os.environ.get('ANIMA_FIXTURE_WAIT'):
  child=subprocess.Popen([sys.executable,'-c','import time; time.sleep(120)'])
  notice=pathlib.Path(os.environ['ANIMA_FIXTURE_WAIT']); staged=notice.with_suffix('.writing')
  staged.write_text(json.dumps([os.getpid(),child.pid])); staged.replace(notice)
  time.sleep(120)
 for name in ['build','dist','sibling']: assert (root/name/'tracked.txt').read_text()==name
 assert (root/'modules/member/member.txt').read_text()=='local pinned module'
 if os.environ.get('ANIMA_REQUIRE_CLEAN_SOURCE'): assert not (root/'build/stale.txt').exists()
 commit=subprocess.check_output([os.environ['ANIMA_FIXTURE_GIT'],'rev-parse','HEAD'],cwd=root,text=True).strip()
 target=root/'build/bdist.win-amd64/wheel/sam2/configs/sam2.1_training/sam2.1_hiera_b+_MOSE_finetune.yaml'
 target.parent.mkdir(parents=True,exist_ok=True)
 kernel=ctypes.WinDLL('kernel32',use_last_error=True)
 kernel.CopyFileA.argtypes=[ctypes.c_char_p,ctypes.c_char_p,ctypes.c_int]
 if not kernel.CopyFileA(os.fsencode(root/'sibling/tracked.txt'),os.fsencode(target),0): raise ctypes.WinError(ctypes.get_last_error())
 data=files(); data[prefix+'build-evidence.json']=json.dumps({'root':str(root),'commit':commit,'longest':len(str(target))}).encode()
 record=''.join(name+',sha256='+base64.urlsafe_b64encode(hashlib.sha256(value).digest()).rstrip(b'=').decode()+','+str(len(value))+'\n' for name,value in data.items())+prefix+'RECORD,,\n'
 data[prefix+'RECORD']=record.encode(); name='anima_fixture_source-1.0-py3-none-any.whl'
 with zipfile.ZipFile(pathlib.Path(wheel_directory)/name,'w') as archive:
  for entry,value in data.items(): archive.writestr(entry,value)
 return name
""";
        File.WriteAllText(Path.Combine(project, "backend.py"), backend);
        await Git(repository, "init");
        await Git(repository, "config", "core.symlinks", "false");
        await Git(repository, "submodule", "add", "--", module, "modules/member");
        File.WriteAllText(Path.Combine(repository, "source-link.txt"), "sibling/tracked.txt");
        await Git(repository, "add", ".");
        var linkBlob = (await Git(repository, "hash-object", "-w", "source-link.txt")).Trim();
        await Git(repository, "update-index", "--cacheinfo", "120000," + linkBlob + ",source-link.txt");
        await Git(repository, "commit", "-m", "source");
        var commit = (await Git(repository, "rev-parse", "HEAD")).Trim();
        const string url = "https://github.com/anima-offline-fixture/source";
        var runtime = Path.Combine(root, "runtime");
        var cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url + "\n" + commit))).ToLowerInvariant();
        var source = Path.Combine(runtime, "cache", "dependency-sources", cacheKey);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!); Directory.Move(repository, source);
        var stale = Path.Combine(source, "build", "stale.txt"); File.WriteAllText(stale, "untracked previous build must remain untouched");
        project = Path.Combine(source, "projects", "package");
        var oldLongest = Path.Combine(source, "build", "bdist.win-amd64", "wheel", "sam2", "configs", "sam2.1_training", "sam2.1_hiera_b+_MOSE_finetune.yaml");
        Check(oldLongest.Length > 260, "source fixture reproduces a real legacy Win32 path beyond MAX_PATH");
        var baselineWheels = Path.Combine(root, "baseline-wheels"); Directory.CreateDirectory(baselineWheels);
        var failure = "";
        try { await Commands.Run(hostPython, ["-B", "-c", "import backend,sys; backend.build_wheel(sys.argv[1])", baselineWheels], project, null, ct, env); }
        catch (CommandFailureException error) { failure = error.Message; }
        Check(failure.Contains("[WinError 206]", StringComparison.Ordinal) || failure.Contains("[WinError 3]", StringComparison.Ordinal),
            "the original deep source actually fails a legacy Win32 file copy before using the short checkout");
        var package = new DependencyPackage { Name = "anima-fixture-source", Version = "1.0", Kind = "git", Url = url,
            Commit = commit, Subdirectory = "projects/package", Direct = true };
        var badAlternate = Path.Combine(source, ".git", "objects", "info", "alternates");
        string taskDirectory;
        using (var temporary = InstallationTemporaryDirectory.Enter(runtime))
        {
            taskDirectory = temporary.Path;
            var preparedProject = await DependencyInstallation.PrepareBuildSourceAsync(package, source, temporary.Path, env, Log, ct);
            var preparedRoot = Directory.GetParent(Directory.GetParent(preparedProject)!.FullName)!.FullName;
            Check(preparedRoot.Length < 90 && Directory.Exists(Path.Combine(preparedRoot, ".git"))
                && (await Git(preparedRoot, "rev-parse", "HEAD")).Trim() == commit, "short checkout retains the exact Git commit and independent metadata for version derivation");
            Check(!File.Exists(Path.Combine(preparedRoot, "build", "stale.txt")) && File.Exists(Path.Combine(preparedRoot, "build", "tracked.txt"))
                && File.Exists(Path.Combine(preparedRoot, "dist", "tracked.txt")), "untracked build pollution is omitted while legitimate tracked build and dist remain");
            Check((await Git(Path.Combine(preparedRoot, "modules", "member"), "rev-parse", "HEAD")).Trim() == moduleCommit,
                "submodules are recreated from the verified local gitlink without any remote fetch");
            Check(!File.Exists(Path.Combine(preparedRoot, ".git", "objects", "info", "alternates")), "short checkout never borrows external Git objects");
            Check(File.ReadAllText(Path.Combine(preparedRoot, "source-link.txt")) == "sibling/tracked.txt"
                && (File.GetAttributes(Path.Combine(preparedRoot, "source-link.txt")) & FileAttributes.ReparsePoint) == 0,
                "Git symlink blobs retain their safe Windows text-file representation without creating filesystem links");
            File.WriteAllText(badAlternate, Path.Combine(module, ".git", "objects"));
            try { await Reject<InvalidDataException>(() => DependencyInstallation.PrepareBuildSourceAsync(package, source, temporary.Path, env, Log, ct),
                "Git object alternates outside the verified cache are rejected before cloning"); }
            finally { File.Delete(badAlternate); }
            await Reject<InvalidDataException>(() => DependencyInstallation.PrepareBuildSourceAsync(package with { Subdirectory = "../outside" }, source, temporary.Path, env, Log, ct),
                "a source subdirectory cannot escape the isolated checkout");
            var directory = Path.Combine(root, "directory-source"); Directory.CreateDirectory(Path.Combine(directory, "build"));
            File.WriteAllText(Path.Combine(directory, "build", "keep.txt"), "legitimate local source");
            var copy = await DependencyInstallation.PrepareBuildSourceAsync(package with { Kind = "directory", Subdirectory = "" }, directory, temporary.Path, env, Log, ct);
            Check(File.ReadAllText(Path.Combine(copy, "build", "keep.txt")) == "legitimate local source", "directory sources retain all safe content without guessing which build directories are disposable");
            var junction = Path.Combine(directory, "outside-link");
            var outside = Path.Combine(root, "outside-source"); Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "untouched.txt"), "external content");
            var linkScript = "New-Item -ItemType Junction -Path '" + junction.Replace("'", "''") + "' -Target '" + outside.Replace("'", "''") + "' | Out-Null";
            await Commands.Run("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", linkScript], root, null, ct);
            Check((File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0, "a real Windows junction exercises the source boundary");
            try
            {
                await Reject<InvalidDataException>(() => DependencyInstallation.PrepareBuildSourceAsync(package with { Kind = "directory", Subdirectory = "" }, directory, temporary.Path, env, Log, ct),
                    "nested reparse points are rejected instead of copying external source content");
                await Reject<InvalidDataException>(() => DependencyInstallation.PrepareBuildSourceAsync(package with { Kind = "directory", Subdirectory = "" }, junction, temporary.Path, env, Log, ct),
                    "a source root pointing through a junction is rejected");
            }
            finally { Directory.Delete(junction); }
            Check(File.ReadAllText(Path.Combine(outside, "untouched.txt")) == "external content", "reparse rejection and cleanup preserve external files");
            File.WriteAllText(Path.Combine(directory, ".git"), "gitdir: " + Path.Combine(module, ".git"));
            await Reject<InvalidDataException>(() => DependencyInstallation.PrepareBuildSourceAsync(package with { Kind = "directory", Subdirectory = "" }, directory, temporary.Path, env, Log, ct),
                "directory source gitfiles cannot retain pointers into another checkout");
            using var alreadyCanceled = new CancellationTokenSource(); alreadyCanceled.Cancel();
            await Reject<OperationCanceledException>(() => DependencyInstallation.PrepareBuildSourceAsync(package, source, temporary.Path, env, Log, alreadyCanceled.Token),
                "source preparation respects cancellation before launching any process");
        }
        Check(!Directory.Exists(taskDirectory), "owned short checkouts are cleaned after their task scope ends");
        var environment = Path.Combine(root, "environment"); Directory.CreateDirectory(environment);
        await Commands.Run(hostPython, ["-m", "venv", Path.Combine(environment, ".venv")], environment, Log, ct);
        var config = new LauncherConfig { Python = Path.Combine(environment, ".venv", "Scripts", "python.exe"), ComfyRoot = environment,
            RuntimeRoot = runtime, SourceId = "official", NetworkMode = "custom", ProxyAddress = "http://127.0.0.1:9" };
        var before = await DependencyPlans.InventoryAsync(config, ct);
        var plan = new ComfyDependencySnapshot(new string('a', 40), "", "", [package.Name + "==" + package.Version])
            { Resolved = true, PlanVersion = 1, PythonIdentity = "isolated-source-fixture", EnvironmentPackages = before, Packages = [package] };
        env["ANIMA_REQUIRE_CLEAN_SOURCE"] = "1";
        var networkCalls = 0;
        using var downloads = new Downloads(handler: new RejectNetwork(() => Interlocked.Increment(ref networkCalls)));
        await DependencyInstallation.InstallAsync(config, plan, runtime, downloads, env, _ => Task.FromResult<string?>(null), ct, Log);
        var installed = await DependencyPlans.InventoryAsync(config, ct);
        Check(installed.Contains("anima-fixture-source==1.0") && before.All(installed.Contains), "the full frozen pipeline builds and installs the exact source version without changing its prior environment");
        var evidencePath = Path.Combine(environment, ".venv", "Lib", "site-packages", "anima_fixture_source-1.0.dist-info", "build-evidence.json");
        using var evidence = JsonDocument.Parse(File.ReadAllText(evidencePath));
        Check(evidence.RootElement.GetProperty("commit").GetString() == commit && evidence.RootElement.GetProperty("longest").GetInt32() < 260,
            "the real wheel build completes the identical Win32 file copy inside the short pinned checkout");
        Check(File.ReadAllText(stale) == "untracked previous build must remain untouched" && string.IsNullOrWhiteSpace(await Git(source, "status", "--porcelain", "--untracked-files=no")),
            "building leaves the original cache tracked tree and existing build leftovers unchanged");
        var pidFile = Path.Combine(root, "build-child-pids.json");
        var cancelEnv = new Dictionary<string, string>(env) { ["CFLAGS"] = "fixture-cancel-build", ["ANIMA_FIXTURE_WAIT"] = pidFile };
        using var cancelBuild = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pending = DependencyInstallation.InstallAsync(config, plan, runtime, downloads, cancelEnv, _ => Task.FromResult<string?>(null), cancelBuild.Token, Log);
        System.Diagnostics.Process[] spawned = [];
        int[] pids = [];
        try
        {
            var until = DateTime.UtcNow.AddSeconds(30);
            while (!File.Exists(pidFile) && !pending.IsCompleted && DateTime.UtcNow < until) await Task.Delay(50, ct);
            Check(File.Exists(pidFile), "the cancellation fixture reaches an actual Python build backend and child process");
            pids = JsonSerializer.Deserialize<int[]>(File.ReadAllText(pidFile))!;
            spawned = pids.Select(System.Diagnostics.Process.GetProcessById).ToArray();
            foreach (var process in spawned) _ = process.Handle; // Bind cleanup to these instances, never recycled PIDs.
        }
        finally { cancelBuild.Cancel(); }
        try
        {
            await Reject<OperationCanceledException>(() => pending, "pausing during a wheel build preserves cancellation");
            Check(spawned.All(process => process.HasExited), "cancellation joins the wheel backend and its child before returning");
        }
        finally
        {
            foreach (var process in spawned)
            {
                try { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } }
                finally { process.Dispose(); }
            }
        }
        Check((await DependencyPlans.InventoryAsync(config, ct)).SequenceEqual(installed), "cancelled source builds leave installed packages unchanged");
        Check(InstallationTemporaryDirectory.CandidateRoots(runtime).Where(Directory.Exists)
            .All(path => !Directory.EnumerateDirectories(path, "t-*").Any()), "cancelled wheel builds release their owned short source and output directories");
        Check(networkCalls == 0, "source staging and offline builds never invoke the HTTP artifact transport");
        File.WriteAllLines(Path.Combine(root, "execution.log"), logs);
        JsonFile.Write(Path.Combine(root, "evidence.json"), new { checks, source, oldLongest = oldLongest.Length, failure, commit, moduleCommit,
            shortBuild = evidence.RootElement.Clone(), networkCalls, installed, cancelledPids = pids });
        return checks;
    }

    private static string FindRepository()
    {
        for (var dir = new DirectoryInfo(Environment.CurrentDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "launcher", "manifests", "launcher-manifest.json"))) return dir.FullName;
        throw new DirectoryNotFoundException("Run dependency installation tests from the repository.");
    }

    private static string Wheel(string directory, string name, string version, params string[] dependencies)
    {
        var file = Path.Combine(directory, name + "-" + version + "-py3-none-any.whl");
        var prefix = name + "-" + version + ".dist-info/";
        var entries = new Dictionary<string, byte[]>
        {
            [name + "/__init__.py"] = Encoding.UTF8.GetBytes("__version__ = '" + version + "'\n"),
            [prefix + "METADATA"] = Encoding.UTF8.GetBytes("Metadata-Version: 2.1\nName: " + name + "\nVersion: " + version
                + "\n" + string.Join("", dependencies.Select(d => "Requires-Dist: " + d + "\n")) + "\n"),
            [prefix + "WHEEL"] = Encoding.UTF8.GetBytes("Wheel-Version: 1.0\nGenerator: anima-offline-fixture\nRoot-Is-Purelib: true\nTag: py3-none-any\n"),
        };
        var record = string.Join("\n", entries.Select(e => e.Key + ",sha256=" + Convert.ToBase64String(SHA256.HashData(e.Value)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + "," + e.Value.Length))
            + "\n" + prefix + "RECORD,,\n";
        entries[prefix + "RECORD"] = Encoding.UTF8.GetBytes(record);
        using var archive = ZipFile.Open(file, ZipArchiveMode.Create);
        foreach (var entry in entries) { using var output = archive.CreateEntry(entry.Key).Open(); output.Write(entry.Value); }
        return Path.GetFullPath(file);
    }

    private static void OverlappingWheel(string directory, string name, string content)
    {
        var path = Wheel(directory, name, "1.0");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        const string shared = "anima_fixture_overlapping/shared.bin";
        var bytes = Encoding.UTF8.GetBytes(content);
        using (var output = archive.CreateEntry(shared).Open()) output.Write(bytes);
        var record = archive.GetEntry(name + "-1.0.dist-info/RECORD")!;
        string text;
        using (var reader = new StreamReader(record.Open())) text = reader.ReadToEnd();
        record.Delete();
        using var replacement = new StreamWriter(archive.CreateEntry(name + "-1.0.dist-info/RECORD").Open(), new UTF8Encoding(false));
        replacement.Write(text + shared + ",sha256=" + Convert.ToBase64String(SHA256.HashData(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + "," + bytes.Length + "\n");
    }

    private static async Task VerifyOrigins(LauncherConfig config, ComfyDependencySnapshot plan, string installer, CancellationToken ct)
    {
        var path = Path.Combine(config.ComfyRoot, "expected-origins.json");
        JsonFile.Write(path, plan.Packages);
        const string script = """
import base64,csv,hashlib,importlib,importlib.metadata as m,json,pathlib,sys
for p in json.load(open(sys.argv[1],encoding='utf-8-sig')):
 d=m.distribution(p['name']); assert d.version==p['version']
 assert importlib.import_module(p['name'].replace('-','_')).__version__==p['version']
 assert d.read_text('INSTALLER').strip()==sys.argv[2], (p['name'],d.read_text('INSTALLER'))
 entries=[f for f in d.files if str(f).replace('\\','/').endswith('.dist-info/METADATA')]
 assert len(entries)==1
 folder=pathlib.Path(d.locate_file(entries[0])).resolve().parent
 origin=folder/'direct_url.json'; key=folder.name+'/direct_url.json'
 with (folder/'RECORD').open(newline='',encoding='utf-8') as f: rows=[r for r in csv.reader(f) if r and r[0].replace('\\','/')==key]
 if not p['direct']:
  assert not origin.exists() and not rows, p['name']
 else:
  data=origin.read_bytes(); info=json.loads(data)
  assert info['url']==p['url'] and info['archive_info']['hashes']['sha256']==p['sha256']
  digest=base64.urlsafe_b64encode(hashlib.sha256(data).digest()).rstrip(b'=').decode('ascii')
  assert rows==[[key,'sha256='+digest,str(len(data))]], (p['name'],rows)
print('Installed versions imports origins and RECORD hashes verified')
""";
        await Commands.Run(config.Python, ["-B", "-c", script, path, installer], config.ComfyRoot, null, ct);
    }

    private sealed class RejectNetwork(Action called) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            called();
            throw new InvalidOperationException("Offline dependency test attempted HTTP: " + request.RequestUri?.Host);
        }
    }
}
