using Anima.Launcher.Core;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

public static class ArtifactNetworkTests
{
    public static async Task<int> Run(string work)
    {
        var passed = 0;
        void Check(bool condition, string label) { if (!condition) throw new Exception("FAIL: " + label); passed++; Console.WriteLine("PASS: " + label); }
        void Reject(Action action, string label)
        {
            try { action(); } catch (InvalidDataException) { Check(true, label); return; }
            throw new Exception("Expected invalid-data rejection: " + label);
        }
        var folder = Path.Combine(work, "artifact-network-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var bytes = Enumerable.Range(0, 8192).Select(index => (byte)(index % 251)).ToArray();
        var digest = Convert.ToHexString(SHA256.HashData(bytes));
        var artifact = new Artifact("fixture", "https://github.com/owner/repo/releases/download/v1/payload.zip", bytes.Length, digest, "tool.exe")
        { FileName = "payload.zip", Sources = [new("native", "Native", "https://mirror.example/payload.zip", Priority: 1)] };
        var first = Path.Combine(folder, "complete");
        Directory.CreateDirectory(Path.Combine(first, "cache"));
        var legacy = Path.Combine(first, "cache", "fixture.zip");
        await File.WriteAllBytesAsync(legacy, bytes);
        var adopted = await ArtifactCache.PrepareAsync(first, artifact);
        Check(adopted.Contains(digest.ToLowerInvariant()) && !File.Exists(legacy) && await Downloads.Verify(adopted, bytes.Length, digest, default), "verified legacy archive moves to content-addressed cache");
        Check(ArtifactCache.PathFor(first, artifact with { Id = "another-name" }) == adopted, "same content and original filename share cache across resource IDs");
        var parallelRoot = Path.Combine(folder, "parallel-migration");
        Directory.CreateDirectory(Path.Combine(parallelRoot, "cache"));
        var parallelLegacy = Path.Combine(parallelRoot, "cache", artifact.Id + ".zip");
        await File.WriteAllBytesAsync(parallelLegacy, bytes);
        var parallelDestination = ArtifactCache.PathFor(parallelRoot, artifact);
        Directory.CreateDirectory(Path.GetDirectoryName(parallelDestination)!);
        Task<string> waitingMigration;
        using (var externalLock = new FileStream(parallelDestination + ".download.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            waitingMigration = ArtifactCache.PrepareAsync(parallelRoot, artifact);
            await Task.Delay(180);
            Check(!waitingMigration.IsCompleted && File.Exists(parallelLegacy) && !File.Exists(parallelDestination),
                "legacy migration waits for the shared OS destination lease before moving bytes");
        }
        var parallelResults = await Task.WhenAll(new[] { waitingMigration }.Concat(Enumerable.Range(0, 8).Select(_ => ArtifactCache.PrepareAsync(parallelRoot, artifact))));
        Check(parallelResults.All(path => path == parallelDestination) && !File.Exists(parallelLegacy)
            && await Downloads.Verify(parallelDestination, artifact.Size, artifact.Sha256, default),
            "concurrent cache preparation returns one valid migrated archive without move conflicts");
        var toolsRoot = Path.Combine(folder, "parallel-tools");
        Directory.CreateDirectory(toolsRoot);
        var toolArchive = Path.Combine(toolsRoot, "fixture.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(toolArchive, System.IO.Compression.ZipArchiveMode.Create))
        using (var payload = zip.CreateEntry("tool.exe").Open()) payload.Write(bytes);
        var archiveBytes = await File.ReadAllBytesAsync(toolArchive);
        var toolFixture = new Artifact("parallel-fixture", "https://example.test/tool.zip", archiveBytes.Length,
            Convert.ToHexString(SHA256.HashData(archiveBytes)), "tool.exe") { ExecutableSha256 = digest };
        var installedTools = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => ToolInstallation.InstallVerifiedArchive(toolsRoot, toolFixture, toolArchive, default)));
        Check(installedTools.Distinct().Count() == 1 && ToolInstallation.IsReady(toolsRoot, toolFixture),
            "concurrent tool extraction shares its destination lease and publishes one verified installation");

        var partialRoot = Path.Combine(folder, "partial");
        Directory.CreateDirectory(Path.Combine(partialRoot, "cache"));
        var oldPart = Path.Combine(partialRoot, "cache", "fixture.zip.part");
        await File.WriteAllBytesAsync(oldPart, bytes[..2048]);
        JsonFile.Write(oldPart + ".json", new ResumeInfo(digest, bytes.Length, artifact.Url, "\"stable\""));
        var migrated = await ArtifactCache.PrepareAsync(partialRoot, artifact);
        Check(File.Exists(migrated + ".part") && new FileInfo(migrated + ".part").Length == 2048 && !File.Exists(oldPart), "matching legacy resume bytes migrate without an extra allocation");
        Check(JsonFile.Read<ResumeInfo>(migrated + ".part.json").ETag == "\"stable\"", "legacy resume metadata stays bound to its original source");
        var wrong = artifact with { Sha256 = new string('a', 64) };
        Check(!File.Exists(await ArtifactCache.PrepareAsync(first, wrong)), "different content never adopts a same-name cached archive");
        Reject(() => ArtifactCache.PathFor(first, artifact with { FileName = "../escape.zip" }), "cache rejects original filename path traversal");

        var oldDirect = Environment.GetEnvironmentVariable("ANIMA_LAUNCHER_DIRECT_NETWORK");
        try
        {
            Environment.SetEnvironmentVariable("ANIMA_LAUNCHER_DIRECT_NETWORK", null);
            var china = DownloadSources.ArtifactCandidates(artifact, new());
            Check(china[0] == artifact.Sources[0].Url && china[^1] == artifact.Url, "native artifact mirror precedes gateway and official fallback");
            Check(DownloadSources.ArtifactCandidates(artifact, new() { SourceId = "official" }).SequenceEqual([artifact.Url]), "official profile does not inject native mirrors");
            var info = new ProcessStartInfo("fixture");
            new NetworkPolicySnapshot("custom", "http://127.0.0.1:7890").Apply(info, new Uri("https://pypi.org/simple"));
            Check(info.Environment["HTTPS_PROXY"] == "http://127.0.0.1:7890" && info.Environment["PIP_PROXY"] == "http://127.0.0.1:7890"
                && info.Environment["GIT_CONFIG_VALUE_0"] == "http://127.0.0.1:7890", "Git pip and HTTP child transports receive one explicit proxy");
            using var handler = new NetworkPolicySnapshot("custom", "http://127.0.0.1:7890").CreateHttpHandler();
            Check(handler.UseProxy && handler.Proxy!.GetProxy(new Uri("https://pypi.org"))!.Port == 7890, "managed HTTP uses the same explicit proxy endpoint");
            new NetworkPolicySnapshot("direct").Apply(info);
            Check(info.Environment["HTTPS_PROXY"] == "" && info.Environment["NO_PROXY"] == "*", "direct mode clears inherited proxy settings");
            Environment.SetEnvironmentVariable("ANIMA_LAUNCHER_DIRECT_NETWORK", "1");
            Check(NetworkPolicySnapshot.Capture(new() { NetworkMode = "custom", ProxyAddress = "http://localhost:7890" }).Mode == "direct", "acceptance direct override wins over saved network settings");
            Check(DownloadSources.ArtifactCandidates(artifact, new()).SequenceEqual([artifact.Sources[0].Url, artifact.Url]), "direct acceptance retains native mirrors and skips gateways");
        }
        finally { Environment.SetEnvironmentVariable("ANIMA_LAUNCHER_DIRECT_NETWORK", oldDirect); }
        Reject(() => NetworkPolicySnapshot.ValidateSettings("custom", "http://name:secret@localhost:7890"), "proxy credentials cannot enter persisted addresses");
        Reject(() => NetworkPolicySnapshot.ValidateSettings("custom", "socks5://localhost:7890"), "unsupported proxy protocols fail explicitly");
        var failure = new CommandFailureException("git.exe", 128, "https://name:secret@github.com/x?token=topsecret", "Authorization: Bearer private-token\nHTTP Error 503: connection reset");
        Check(failure.Kind == ProcessFailureKind.ServerUnavailable && !failure.StandardOutput.Contains("secret")
            && !failure.StandardError.Contains("private-token") && !failure.Message.Contains("private-token"), "structured command failure preserves classification without credentials");
        Check(!Installer.IsSourceFailure(new CommandFailureException("pip", 1, "https://repo.example/package", "Failed building wheel; connection helper compilation failed")), "build failure does not become a mirror retry because its logs mention a connection");
        Check(!Installer.IsSourceFailure(new CommandFailureException("pip", 1, "", "No space left on device; timeout")), "disk failure takes precedence over network terms");
        var deniedPackage = new CommandFailureException("pip", 1, "", "HTTP error 403 while getting package.whl\n403 Client Error: Forbidden for url: https://pypi.example/package.whl");
        Check(deniedPackage.Kind == ProcessFailureKind.Forbidden && !Installer.IsSourceFailure(deniedPackage),
            "real pip forbidden response retains its HTTP classification and stops automatic retries");
        Check(CommandFailureException.Classify("HTTP Error 401 while getting package.whl") == ProcessFailureKind.AuthenticationRequired
            && CommandFailureException.Classify("git checkout 429d0159ad failed") != ProcessFailureKind.RateLimited,
            "HTTP auth status is recognized without interpreting commit digits as rate limits");
        var sourceAttempts = new List<string>();
        var sourceResult = await ComfyDependencyResolver.WithPackageSourcesAsync(["mirror", "official"], ["git-mirror", "git-official"],
            (index, gitSource) =>
            {
                sourceAttempts.Add(index + "/" + gitSource);
                return index == "mirror"
                    ? Task.FromException<string>(new CommandFailureException("pip", 1, "", "No matching distribution found for required-package==1.2.3"))
                    : Task.FromResult("resolved");
            }, default);
        Check(sourceResult == "resolved" && sourceAttempts.SequenceEqual(["mirror/git-mirror", "official/git-mirror"]),
            "missing PyPI package advances the package index without repeating unrelated Git sources");
        sourceAttempts.Clear();
        await ComfyDependencyResolver.WithPackageSourcesAsync(["mirror", "official"], ["git-mirror", "git-official"],
            (index, gitSource) =>
            {
                sourceAttempts.Add(index + "/" + gitSource);
                return gitSource == "git-mirror"
                    ? Task.FromException<string>(new CommandFailureException("git", 128, "", "connection reset"))
                    : Task.FromResult("resolved");
            }, default);
        Check(sourceAttempts.SequenceEqual(["mirror/git-mirror", "mirror/git-official"]), "Git transport failure retains Git-source fallback before changing PyPI");
        sourceAttempts.Clear();
        try
        {
            await ComfyDependencyResolver.WithPackageSourcesAsync<string>(["mirror", "official"], ["git-mirror", "git-official"],
                (index, gitSource) => { sourceAttempts.Add(index + "/" + gitSource); throw new CommandFailureException("pip", 1, "", "ResolutionImpossible: conflicting dependencies"); }, default);
            throw new Exception("Expected dependency conflict");
        }
        catch (CommandFailureException ex) when (ex.Kind == ProcessFailureKind.DependencyConflict)
        { Check(sourceAttempts.Count == 1, "dependency conflict stops without retrying either source family"); }
        var deepRuntime = Path.Combine(folder, new string('d', 60), "runtime");
        Directory.CreateDirectory(deepRuntime);
        var candidates = InstallationTemporaryDirectory.CandidateRoots(deepRuntime);
        string firstScratch;
        using (var scratch = InstallationTemporaryDirectory.Enter(deepRuntime, _ => true))
        {
            firstScratch = scratch.Path;
            Check(scratch.Path.Length <= InstallationTemporaryDirectory.MaximumPathLength
                && InstallationTemporaryDirectory.VolumeRoot(scratch.Path) == InstallationTemporaryDirectory.VolumeRoot(deepRuntime),
                "deep Windows installation uses short scratch on its original volume");
            using var parallel = InstallationTemporaryDirectory.Enter(deepRuntime, _ => true);
            Check(parallel.Path != scratch.Path && Directory.Exists(scratch.Path) && Directory.Exists(parallel.Path),
                "independent operations on the same environment receive separate scratch directories");
            using var fallback = InstallationTemporaryDirectory.Enter(deepRuntime, candidate => candidate != candidates[0]);
            Check(!fallback.Path.StartsWith(candidates[0] + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "denied nearest ancestor falls back to a shorter permitted ancestor");
            File.WriteAllText(Path.Combine(scratch.Path, "owned.txt"), "temporary");
        }
        Check(!Directory.Exists(firstScratch), "scratch disposal removes only the completed operation directory");
        try
        {
            using var denied = InstallationTemporaryDirectory.Enter(deepRuntime, _ => false);
            throw new Exception("Expected no writable short scratch");
        }
        catch (IOException ex) { Check(ex.Message.Contains("短临时目录"), "no permitted same-volume scratch produces an actionable error"); }
        var occupiedRuntime = Path.Combine(folder, "occupied-runtime");
        Directory.CreateDirectory(occupiedRuntime);
        var occupied = InstallationTemporaryDirectory.CandidateRoots(occupiedRuntime)[0];
        Directory.CreateDirectory(occupied);
        var preserved = Path.Combine(occupied, "user-file.txt");
        File.WriteAllText(preserved, "keep");
        using (var scratch = InstallationTemporaryDirectory.Enter(occupiedRuntime, _ => true))
            Check(!scratch.Path.StartsWith(occupied + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && File.ReadAllText(preserved) == "keep", "preexisting ordinary directory is never claimed or cleaned as launcher scratch");
        BundledTools.VerifyEmbedded();
        Check(true, "embedded uv and aria2 archives and executable digests verify");
        var tools = new[] { BundledTools.GetArtifact("uv"), BundledTools.GetArtifact("aria2") };
        Check(tools.All(tool => tool.SourceArchive is not null && tool.ExecutableSha256.Length == 64 && tool.License.Length > 0), "bundled tools contain pinned executable and source license identities");
        var modelRoot = Path.Combine(folder, "frozen-models");
        var frozen = new[] { ("diffusion_models", "old-diffusion.bin"), ("text_encoders", "old-encoder.bin"), ("vae", "old-vae.bin") }
            .Select((item, index) => new ModelItem("base-" + index, "Base " + index, "base", item.Item1, item.Item2, [], bytes.Length, digest,
                ["https://models.example/" + item.Item2], "test", "https://models.example/license")).ToArray();
        var frozenProfile = new ModelProfileDefinition("anima", "Anima", frozen[0].RelativePath, frozen[1].RelativePath, frozen[2].RelativePath, "anima", "", "");
        var frozenPlan = new InstallationPlan("{}", modelRoot, Path.Combine(folder, "frozen-runtime"), "anima", true, 0, 0, 0, [])
            { FrozenModels = frozen, FrozenProfile = frozenProfile, ModelIds = [.. frozen.Select(model => model.Id)] };
        var upgradedModels = new ModelManifest(1, frozen.Select(model => model with { RelativePath = "new-" + model.RelativePath, Sha256 = new string('a', 64) }).ToArray());
        var upgradedProfiles = new ModelProfilesManifest(1, "anima", [frozenProfile with { ModelName = "new-" + frozenProfile.ModelName,
            ClipName = "new-" + frozenProfile.ClipName, VaeName = "new-" + frozenProfile.VaeName }]);
        var probe = new ProbeResult(modelRoot, "", frozen.ToDictionary(model => model.Category, model => new[] { Path.Combine(modelRoot, "models", model.Category) }));
        var resolved = await InstallationPlanner.ResolveModelDestinationsAsync(frozenPlan, probe, upgradedModels, upgradedProfiles, default);
        Check(resolved.ModelInventory.Length == 3 && resolved.ModelInventory.All(item => item.Model.Sha256 == digest && item.Model.RelativePath.StartsWith("old-")),
            "resumed installation keeps frozen profile filenames and model hashes after catalog changes");
        var legacyJson = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(frozenPlan, JsonFile.Options))!;
        legacyJson.AsObject().Remove("frozenModels"); legacyJson.AsObject().Remove("frozenProfile");
        var oldPlan = legacyJson.Deserialize<InstallationPlan>(JsonFile.Options)!;
        Check(oldPlan.FrozenModels.Length == 0 && oldPlan.FrozenProfile is null, "pre-freezing installation snapshots deserialize with compatible defaults");
        passed += await DownloadDiskSpaceTests.Run(folder);
        return passed;
    }

    public static async Task RunAcceptance(string repositoryRoot, string target)
    {
        target = Path.GetFullPath(target);
        var allowed = Path.Combine(Path.GetFullPath(repositoryRoot), "output", "download-acceptance") + Path.DirectorySeparatorChar;
        if (!target.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || Directory.Exists(target) || File.Exists(target))
            throw new IOException("工具冷安装验收要求 output/download-acceptance 下一个全新的独立目录。");
        Directory.CreateDirectory(target);
        var runtime = Path.Combine(target, "runtime");
        var logPath = Path.Combine(target, "acceptance.log");
        void Log(string value) { var clean = DiagnosticRedaction.Text(value); Console.WriteLine(clean); File.AppendAllText(logPath, clean + Environment.NewLine); }
        var manifest = JsonFile.Read<RuntimeManifest>(Path.Combine(repositoryRoot, "launcher", "manifests", "launcher-manifest.json"));
        var config = new LauncherConfig { RuntimeRoot = runtime, SourceId = "china" };
        var network = NetworkPolicySnapshot.Capture(config);
        var env = new Dictionary<string, string>(); network.AddEnvironment(env);
        BundledTools.VerifyEmbedded();
        var uv = await BundledTools.EnsureAsync("uv", runtime);
        var aria2 = await BundledTools.EnsureAsync("aria2", runtime);
        var uvVersion = (await Commands.Run(uv, ["--version"], target, Log, default, env)).Trim();
        var ariaVersion = (await Commands.Run(aria2, ["--version"], target, Log, default, env)).Split('\n')[0].Trim();
        using var downloads = new Downloads(handler: network.CreateHttpHandler(), engineOptions: new()
        { Aria2Path = aria2, NetworkPolicyResolver = () => network });
        var progress = new InlineProgress(item => Log(item.Name + " " + item.Stage + " [" + item.Engine + "] " + item.Received + "/" + item.Total));
        var gitSpec = manifest.Tools.Single(tool => tool.Id == "git");
        var gitArchive = await ArtifactCache.FetchAsync(runtime, gitSpec, config, downloads, progress, default);
        var git = await ToolInstallation.InstallVerifiedArchive(runtime, gitSpec, gitArchive, default);
        var gitVersion = (await Commands.Run(git, ["--version"], target, Log, default, env)).Trim();
        var pythonSpec = PythonRuntimeInstallation.Resolve(manifest);
        var pythonArchive = await ArtifactCache.FetchAsync(runtime, pythonSpec.Artifact, config, downloads, progress, default);
        var python = await PythonRuntimeInstallation.InstallAsync(uv, runtime, pythonSpec, pythonArchive, Log, default, env);
        var pythonVersion = (await Commands.Run(python, ["-I", "--version"], target, Log, default, env)).Trim();
        env["UV_PYTHON_DOWNLOADS"] = "never";
        env["UV_CACHE_DIR"] = Path.Combine(runtime, "cache", "uv");
        await Commands.Run(uv, ["venv", "--offline", "--no-config", "--no-python-downloads", "--python", python, Path.Combine(target, "venv")], target, Log, default, env);
        var venvPython = Path.Combine(target, "venv", "Scripts", "python.exe");
        var baseIdentity = (await Commands.Run(venvPython, ["-I", "-c", "import sys;print(sys.base_prefix)"], target, Log, default, env)).Trim();
        if (!Path.GetFullPath(baseIdentity).Equals(Path.GetDirectoryName(python), StringComparison.OrdinalIgnoreCase)) throw new Exception("虚拟环境未采用锁定的隔离 Python。");
        JsonFile.Write(Path.Combine(target, "acceptance.json"), new { success = true, uvVersion, ariaVersion, gitVersion, pythonVersion,
            pythonBuild = pythonSpec.Build, pythonSha256 = pythonSpec.Artifact.Sha256, python, venvPython, baseIdentity,
            network, metadata = Path.Combine(runtime, "cache", "python-metadata", pythonSpec.Artifact.Sha256 + ".json"),
            completedAtUtc = DateTime.UtcNow });
        Log("COLD TOOL/PYTHON ACCEPTANCE PASSED " + target);
    }

    public static async Task RunEnvironmentAcceptance(string repositoryRoot, string target)
    {
        target = Path.GetFullPath(target);
        var allowed = Path.Combine(Path.GetFullPath(repositoryRoot), "output", "download-acceptance") + Path.DirectorySeparatorChar;
        if (!target.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.Combine(target, "acceptance.json")))
            throw new IOException("环境验收必须复用已通过工具冷安装验收的独立目录。");
        var state = Path.Combine(target, "state");
        Directory.CreateDirectory(state);
        var logPath = Path.Combine(state, "environment-install.log");
        var logGate = new object();
        void Log(string value)
        {
            var line = DateTime.UtcNow.ToString("O") + " " + DiagnosticRedaction.Text(value);
            lock (logGate) { Console.WriteLine(line); File.AppendAllText(logPath, line + Environment.NewLine); }
        }
        var sourceManifest = JsonFile.Read<RuntimeManifest>(Path.Combine(repositoryRoot, "launcher", "manifests", "launcher-manifest.json"));
        var configuration = new LauncherConfig { InstallRoot = target, ComfyRoot = Path.Combine(target, "ComfyUI"), RuntimeRoot = Path.Combine(target, "runtime"),
            ComfyPort = 19888, WebPort = 19890, AppRoot = repositoryRoot, DataDir = Path.Combine(state, "data"), SourceId = "china" };
        var planPath = Path.Combine(state, "environment-plan.json");
        var network = NetworkPolicySnapshot.Capture(configuration);
        var plan = File.Exists(planPath) ? JsonFile.Read<InstallationPlan>(planPath)
            : await InstallationPlanner.ResolveStableAsync(InstallationPlanner.Create(configuration, sourceManifest, state, true), sourceManifest, state, default);
        JsonFile.Write(planPath, plan);
        JsonFile.Write(Path.Combine(state, "source-manifest.json"), sourceManifest);
        var selectedManifest = plan.ResolvedRuntime ?? sourceManifest;
        Log("Frozen ComfyUI " + selectedManifest.Comfy.Commit + "; free bytes " + new DriveInfo(Path.GetPathRoot(target)!).AvailableFreeSpace);
        var aria2 = await BundledTools.EnsureAsync("aria2", configuration.RuntimeRoot);
        using var downloads = new Downloads(handler: network.CreateHttpHandler(), engineOptions: new() { Aria2Path = aria2, NetworkPolicyResolver = () => network });
        var lastProgress = DateTime.MinValue;
        var progress = new InlineProgress(item =>
        {
            if (DateTime.UtcNow - lastProgress < TimeSpan.FromSeconds(3) && item.Received != item.Total) return;
            lastProgress = DateTime.UtcNow;
            Log(item.Name + " " + item.Stage + " [" + item.Engine + "] " + item.Received + "/" + item.Total);
        });
        try
        {
            var installer = new Installer(state, selectedManifest, downloads, Log, progress);
            configuration = await installer.Install(plan, default);
            JsonFile.Write(Path.Combine(state, "launcher.json"), configuration);
            JsonFile.Write(Path.Combine(state, "installed-manifest.json"), selectedManifest);
            var dependencies = await RuntimeDependencies.Probe(configuration, default);
            JsonFile.Write(Path.Combine(state, "runtime-dependencies.json"), dependencies);
            var service = new ServiceManager(state, Log);
            await service.StartComfy(configuration, selectedManifest, default);
            var probe = await TaggerRuntime.Probe(configuration, Path.Combine(repositoryRoot, "launcher", "scripts", "probe_comfy.py"), default);
            JsonFile.Write(Path.Combine(state, "runtime-probe.json"), probe);
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
            var nodes = await http.GetStringAsync(configuration.ComfyUrl + "/object_info");
            await File.WriteAllTextAsync(Path.Combine(state, "object-info.json"), nodes);
            JsonFile.Write(Path.Combine(state, "environment-acceptance.json"), new { success = true, configuration.ComfyRoot, configuration.Python,
                configuration.ComfyPort, configuration.WebPort, selectedManifest.Comfy.Commit, dependencies, probe,
                freeBytes = new DriveInfo(Path.GetPathRoot(target)!).AvailableFreeSpace, completedAtUtc = DateTime.UtcNow });
            Log("ISOLATED COMFYUI ENVIRONMENT ACCEPTANCE PASSED; service remains owned by " + state);
        }
        catch (Exception ex)
        {
            JsonFile.Write(Path.Combine(state, "environment-acceptance.json"), new { success = false, error = DiagnosticRedaction.Text(ex.ToString()),
                freeBytes = new DriveInfo(Path.GetPathRoot(target)!).AvailableFreeSpace, failedAtUtc = DateTime.UtcNow });
            Log("ENVIRONMENT ACCEPTANCE FAILED: " + ex.Message);
            throw;
        }
    }

    public static async Task RunTemporaryDirectoryAcceptance(string repositoryRoot, string target)
    {
        target = Path.GetFullPath(target);
        var allowed = Path.Combine(Path.GetFullPath(repositoryRoot), "output", "download-acceptance") + Path.DirectorySeparatorChar;
        if (!target.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
            throw new IOException("短临时目录验收只允许独立下载验收根目录。");
        var state = Path.Combine(target, "state");
        var configuration = JsonFile.Read<LauncherConfig>(Path.Combine(state, "launcher.json"));
        using var temporary = InstallationTemporaryDirectory.Enter(configuration.RuntimeRoot);
        var logPath = Path.Combine(state, "antlr-product-temp-build.log");
        void Log(string value) { var clean = DiagnosticRedaction.Text(value); Console.WriteLine(clean); File.AppendAllText(logPath, clean + Environment.NewLine); }
        Log("PRODUCT TEMP: " + temporary.Path);
        var environment = new Dictionary<string, string>
        {
            ["TEMP"] = temporary.Path, ["TMP"] = temporary.Path, ["PIP_CACHE_DIR"] = Path.Combine(configuration.RuntimeRoot, "cache", "pip"),
            ["PIP_CONFIG_FILE"] = "NUL", ["PIP_INDEX_URL"] = "https://pypi.org/simple", ["PIP_EXTRA_INDEX_URL"] = ""
        };
        NetworkPolicySnapshot.Capture(configuration).AddEnvironment(environment, new Uri("https://pypi.org/simple"));
        var destination = Path.Combine(configuration.RuntimeRoot, "cache", "product-temp-wheel");
        await Commands.Run(configuration.Python,
            ["-m", "pip", "wheel", "--no-cache-dir", "--no-build-isolation", "--no-deps", "--disable-pip-version-check",
             "--index-url", "https://pypi.org/simple", "--wheel-dir", destination, "antlr4-python3-runtime==4.9.3"],
            configuration.ComfyRoot, Log, default, environment);
        var wheel = Path.Combine(destination, "antlr4_python3_runtime-4.9.3-py3-none-any.whl");
        if (!File.Exists(wheel)) throw new IOException("真实构建没有产生预期的同版本 wheel。");
        JsonFile.Write(Path.Combine(state, "antlr-product-temp-build.json"), new { success = true, temporary = temporary.Path,
            temporaryLength = temporary.Path.Length, runtimeRoot = configuration.RuntimeRoot, sameVolume = InstallationTemporaryDirectory.VolumeRoot(temporary.Path) == InstallationTemporaryDirectory.VolumeRoot(configuration.RuntimeRoot),
            package = "antlr4-python3-runtime==4.9.3", cacheDisabled = true, wheel, completedAtUtc = DateTime.UtcNow });
    }

    private sealed class InlineProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    { public void Report(DownloadProgress value) => report(value); }
}
