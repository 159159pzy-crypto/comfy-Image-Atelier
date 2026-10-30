using Anima.Launcher.Core;
using System.IO.Compression;

internal static class RuntimeDependencyTests
{
    public static async Task<int> Run(string work)
    {
        var count = 0;
        void Check(bool success, string label)
        {
            if (!success) throw new Exception("FAIL: " + label);
            count++;
            Console.WriteLine("PASS: " + label);
        }
        var probe = new RuntimeDependencyProbe("3.11.9", "cp311", "Windows", "2.11.0+cu128", true, [89], "", false, "", false);
        var wheel = RuntimeDependencies.SelectTriton(probe, out var reason);
        Check(wheel?.Version == "3.6.0.post26" && reason == "", "fresh CUDA 12.8 runtime selects verified Triton 3.6 wheel");
        wheel = RuntimeDependencies.SelectTriton(probe with { Torch = "2.13.0+cu130" }, out reason);
        Check(wheel?.Version == "3.7.1.post27", "imported CUDA 13 runtime selects Triton without replacing Torch");
        foreach (var unsupported in new[] { probe with { Torch = "2.14.0" }, probe with { Capabilities = [75] }, probe with { Capabilities = [89, 75] }, probe with { Cuda = false }, probe with { PythonTag = "cp39" }, probe with { Platform = "Linux" } })
            Check(RuntimeDependencies.SelectTriton(unsupported, out reason) is null && reason.Length > 0,
                "unsupported Triton combination has an explicit nonblocking reason");
        var catalog = RuntimeDependencies.Catalog();
        var namedSam2 = "SAM-2 @ git+https://github.com/facebookresearch/sam2@" + new string('a', 40);
        Check(Installer.PinSam2("git+https://github.com/facebookresearch/sam2", new string('a', 40)) == namedSam2
            && Installer.PinSam2(namedSam2, new string('a', 40)) == namedSam2,
            "SAM2 pinning supplies the wheel metadata name and remains stable when already pinned");
        Check(catalog.Wheels.Length == 8 && catalog.Wheels.All(item => item.Sha256.Length == 64 && item.Size > 40_000_000 && new Uri(item.Url).Host == "files.pythonhosted.org"),
            "Triton wheel catalog locks all supported Python ABI files to official hashes and sizes");
        var root = Path.Combine(work, "dependency-receipts");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "requirements.txt"), "numpy>=2\n");
        var receipt = RuntimeDependencies.Receipt(root, probe, new string('a', 40));
        Check(receipt == RuntimeDependencies.Receipt(root, probe, new string('a', 40)), "dependency receipt is deterministic");
        Check(receipt != RuntimeDependencies.Receipt(root, probe with { Torch = "2.13.0+cu130" }, new string('a', 40)), "changing GPU ABI invalidates a dependency receipt");
        Check(receipt != RuntimeDependencies.Receipt(root, probe with { Python = "3.12.0" }, new string('a', 40)), "changing interpreter version invalidates a dependency receipt");
        Check(receipt != RuntimeDependencies.Receipt(root, probe, new string('b', 40)), "changing ComfyUI commit invalidates a dependency receipt");
        File.WriteAllText(Path.Combine(root, "requirements.txt"), "numpy>=3\n");
        Check(receipt != RuntimeDependencies.Receipt(root, probe, new string('a', 40)), "changing dependency contents invalidates a dependency receipt");
        var environment = Path.Combine(work, "receipt-environment");
        var venv = Path.Combine(environment, ".venv");
        var config = new LauncherConfig { ComfyRoot = environment, Python = Path.Combine(venv, "Scripts", "python.exe"), RuntimeRoot = Path.Combine(work, "receipt-runtime") };
        Directory.CreateDirectory(Path.GetDirectoryName(config.Python)!);
        File.WriteAllText(config.Python, "python executable fixture");
        File.WriteAllText(Path.Combine(venv, "pyvenv.cfg"), "version = 3.11.9\ninclude-system-site-packages = false\n");
        File.WriteAllText(Path.Combine(environment, "main.py"), "# fixture");
        File.WriteAllText(Path.Combine(environment, "folder_paths.py"), "# fixture");
        string Package(string name, string version)
        {
            var directory = Path.Combine(venv, "Lib", "site-packages", name + "-" + version + ".dist-info");
            Directory.CreateDirectory(directory);
            var metadata = Path.Combine(directory, "METADATA");
            File.WriteAllText(metadata, "Name: " + name + "\nVersion: " + version + "\n");
            File.WriteAllText(Path.Combine(directory, "RECORD"), "");
            return metadata;
        }
        var torchMetadata = Package("torch", probe.Torch);
        var comfy = new Repository("ComfyUI", "https://github.com/Comfy-Org/ComfyUI.git", new string('a', 40), []);
        var node = new Repository("receipt-node", "https://github.com/example/node.git", new string('b', 40), []);
        var runtime = new RuntimeManifest(1, "receipt-fixture", "fixture", comfy, "3.11.9", "2.11.0", "0.26.0", "2.11.0",
            "https://download.pytorch.org/whl/cu128", [], [node], [], ["torch==2.11.0"], new string('c', 40));
        var sharedConfig = config.Clone();
        sharedConfig.Python = Path.Combine(work, "shared-python", "python.exe");
        var rejectedState = Path.Combine(work, "shared-repair-state");
        using (var downloads = new Downloads())
        {
            var repair = new Installer(rejectedState, runtime, downloads, _ => { }, null);
            foreach (var action in new Func<Task>[] { () => repair.RepairDependencies(sharedConfig, null, default), () => repair.RepairOptionalDependencies(sharedConfig, default) })
            {
                try { await action(); throw new Exception("Shared Python repair was not rejected."); }
                catch (InvalidOperationException error)
                { Check(error.Message.Contains("解释器") && !Directory.Exists(rejectedState), "repair rejects a shared interpreter before creating state or launching Python"); }
            }
        }
        Check(ComfyUpdateCoordinator.IndependentPythonDirectory(config) == venv, "dependency repair accepts the fresh dedicated virtual environment layout");
        var nodeRoot = Path.Combine(environment, "custom_nodes", node.Id);
        Directory.CreateDirectory(nodeRoot);
        Directory.CreateDirectory(Path.Combine(nodeRoot, ".git"));
        File.WriteAllText(Path.Combine(nodeRoot, ".git", "HEAD"), node.Commit);
        File.WriteAllText(Path.Combine(nodeRoot, "requirements.txt"), "numpy>=2\n");
        JsonFile.Write(Path.Combine(environment, ".anima-revision.json"), comfy);
        JsonFile.Write(Path.Combine(nodeRoot, ".anima-revision.json"), node);
        var state = Path.Combine(work, "receipt-state");
        var incomplete = InstallationPlanner.Create(config, runtime, state, false);
        var receiptPath = Path.Combine(nodeRoot, ".anima-dependencies-complete");
        JsonFile.Write(receiptPath, RuntimeDependencies.CreateNodeReceipt(config, nodeRoot, probe, comfy.Commit));
        Check(RuntimeDependencies.NodeReceiptMatches(receiptPath, config, nodeRoot, comfy.Commit), "structured node receipt verifies source files, Python binary/config and Torch metadata without executing Python");
        var complete = InstallationPlanner.Create(config, runtime, state, false);
        Check(complete.EnvironmentReserveBytes < incomplete.EnvironmentReserveBytes && complete.RuntimeReserveBytes < incomplete.RuntimeReserveBytes,
            "new node receipts receive completed-space credit instead of comparing a digest to the manifest version");
        File.AppendAllText(Path.Combine(nodeRoot, "requirements.txt"), "pillow\n");
        Check(!RuntimeDependencies.NodeReceiptMatches(receiptPath, config, nodeRoot, comfy.Commit), "changed node requirements invalidate the structured receipt");
        File.WriteAllText(Path.Combine(nodeRoot, "requirements.txt"), "numpy>=2\n");
        File.AppendAllText(config.Python, "changed");
        Check(!RuntimeDependencies.NodeReceiptMatches(receiptPath, config, nodeRoot, comfy.Commit), "changed Python executable invalidates the structured receipt");
        File.WriteAllText(config.Python, "python executable fixture");
        File.AppendAllText(Path.Combine(venv, "pyvenv.cfg"), "home = changed\n");
        Check(!RuntimeDependencies.NodeReceiptMatches(receiptPath, config, nodeRoot, comfy.Commit), "changed Python environment identity invalidates the structured receipt");
        File.WriteAllText(Path.Combine(venv, "pyvenv.cfg"), "version = 3.11.9\ninclude-system-site-packages = false\n");
        File.WriteAllText(torchMetadata, "Name: torch\nVersion: 2.13.0+cu130\n");
        Check(!RuntimeDependencies.NodeReceiptMatches(receiptPath, config, nodeRoot, comfy.Commit), "changed installed Torch metadata invalidates the structured receipt");
        File.WriteAllText(torchMetadata, "Name: torch\nVersion: " + probe.Torch + "\n");
        Check(!RuntimeDependencies.NodeReceiptMatches(receiptPath, config, nodeRoot, new string('d', 40)), "different ComfyUI commit cannot reuse a structured node receipt");
        File.WriteAllText(Path.Combine(nodeRoot, ".git", "HEAD"), new string('e', 40));
        Check(!RuntimeDependencies.NodeReceiptMatches(receiptPath, config, nodeRoot, comfy.Commit), "a changed node source commit invalidates its receipt even when requirements are unchanged");
        File.WriteAllText(Path.Combine(nodeRoot, ".git", "HEAD"), "ref: refs/heads/main\n");
        Directory.CreateDirectory(Path.Combine(nodeRoot, ".git", "refs", "heads"));
        var looseRef = Path.Combine(nodeRoot, ".git", "refs", "heads", "main");
        File.WriteAllText(looseRef, node.Commit);
        Check(RuntimeDependencies.NodeReceiptMatches(receiptPath, config, nodeRoot, comfy.Commit), "a matching loose branch ref verifies a node source receipt");
        File.Delete(looseRef);
        File.WriteAllText(Path.Combine(nodeRoot, ".git", "packed-refs"), node.Commit + " refs/heads/main\n");
        Check(RuntimeDependencies.NodeReceiptMatches(receiptPath, config, nodeRoot, comfy.Commit), "a matching packed branch ref verifies a node source receipt");
        var zipNode = Path.Combine(environment, "custom_nodes", "zip-node");
        Directory.CreateDirectory(zipNode);
        var zipReceipt = Path.Combine(zipNode, ".anima-dependencies-complete");
        JsonFile.Write(zipReceipt, RuntimeDependencies.CreateNodeReceipt(config, zipNode, probe, comfy.Commit));
        Check(!RuntimeDependencies.NodeReceiptMatches(zipReceipt, config, zipNode, comfy.Commit), "a source archive without a verifiable Git commit never reuses a dependency receipt");
        File.WriteAllText(receiptPath, runtime.Version);
        Check(InstallationPlanner.Create(config, runtime, state, false).EnvironmentReserveBytes == complete.EnvironmentReserveBytes,
            "legacy version-based node markers remain readable for existing installations");
        var noNodes = runtime with { Nodes = [] };
        File.WriteAllLines(Path.Combine(environment, ".anima-constraints.txt"), ["torch==2.11.0+cu128", "numpy==2.2.0"]);
        InstallationPlanner.MarkStageComplete(config, noNodes, state, "安装 ComfyUI 依赖");
        var missingDistribution = InstallationPlanner.Create(config, noNodes, state, true);
        Package("numpy", "2.2.0");
        var expandedLockComplete = InstallationPlanner.Create(config, noNodes, state, true);
        Check(expandedLockComplete.EnvironmentReserveBytes + 2L * 1024 * 1024 * 1024 < missingDistribution.EnvironmentReserveBytes,
            "resolved full dependency locks receive credit only when their pinned distributions are installed");
        File.WriteAllLines(Path.Combine(environment, ".anima-constraints.txt"), ["torch==2.13.0+cu130", "numpy==2.2.0"]);
        Check(InstallationPlanner.Create(config, noNodes, state, true).EnvironmentReserveBytes > expandedLockComplete.EnvironmentReserveBytes,
            "a resolved lock with a changed GPU pin cannot satisfy the original environment plan");
        foreach (var message in new[] { "HTTP 403", "Connection timed out", "No matching distribution found", "Could not resolve host" })
            Check(Installer.IsSourceFailure(new InvalidOperationException(message)), "network or unsynchronized index permits source fallback");
        const string slowGit = "fatal: unable to access 'https://example.invalid/repo.git/': Operation too slow. Less than 1024 bytes/sec transferred the last 30 seconds";
        var unattendedGit = Commands.Info("git.exe", ["--version"], work, new Dictionary<string, string> { ["GCM_INTERACTIVE"] = "always" });
        Check(unattendedGit.Environment["GCM_INTERACTIVE"] == "never" && unattendedGit.Environment["GIT_TERMINAL_PROMPT"] == "0",
            "installer child commands cannot wait for a hidden Git Credential Manager prompt");
        var removedEnvironment = Commands.Info("git.exe", ["--version"], work,
            new Dictionary<string, string> { ["PYTHONUTF8"] = null!, ["ANIMA_TEST_EMPTY"] = "" });
        Check(!removedEnvironment.Environment.ContainsKey("PYTHONUTF8") && removedEnvironment.Environment.ContainsKey("ANIMA_TEST_EMPTY")
            && removedEnvironment.Environment["ANIMA_TEST_EMPTY"] == "",
            "null child overrides remove variables while explicit empty values remain distinct");
        Check(CommandFailureException.Classify("fatal: Cannot prompt because user interactivity has been disabled.") == ProcessFailureKind.AuthenticationRequired,
            "disabled Git credential prompting is a structured authentication failure");
        foreach (var wrapper in new[] { "", "error: subprocess-exited-with-error\n" })
        {
            var failure = new CommandFailureException("git.exe", 128, "", wrapper + slowGit);
            Check(failure.Kind == ProcessFailureKind.Transport && Installer.IsSourceFailure(failure), "Git low-speed abort is a retryable transport error, including pip's subprocess wrapper");
        }
        var lowSpeedAttempts = new List<string>();
        await ComfyDependencyResolver.WithPackageSourcesAsync(["https://index.invalid/simple"], ["https://mirror.invalid/", "https://github.com/"], (index, source) =>
        {
            lowSpeedAttempts.Add(index + "|" + source);
            return lowSpeedAttempts.Count == 1 ? Task.FromException<bool>(new CommandFailureException("git.exe", 128, "", slowGit)) : Task.FromResult(true);
        }, default);
        Check(lowSpeedAttempts.SequenceEqual(new[] { "https://index.invalid/simple|https://mirror.invalid/", "https://index.invalid/simple|https://github.com/" }),
            "a real Git low-speed abort changes Git source only and keeps the successful package index");
        Check(CommandFailureException.Classify("error: subprocess-exited-with-error\ncompiler error") == ProcessFailureKind.BuildFailure,
            "a genuine compiler failure is never reclassified as a source retry");
        foreach (var error in new Exception[] { new OperationCanceledException(), new IOException("locked"), new InvalidOperationException("Permission denied"), new InvalidOperationException("No space left on device"), new InvalidOperationException("ResolutionImpossible") })
            Check(!Installer.IsSourceFailure(error), "cancellation, local errors and incompatible requirements do not trigger source fallback");
        foreach (var error in new[] { "Filename too long; Failed to build git+https://github.com/facebookresearch/sam2", "WinError 206: git clone https://github.com/example/repo checkout failed",
            "ResolutionImpossible: https://example.invalid/pkg has conflicting dependencies", "Failed to build git+https://github.com/facebookresearch/sam2", "Build failed at line 403" })
            Check(!Installer.IsSourceFailure(new InvalidOperationException(error)), "local build errors containing URLs or status-like numbers never trigger a mirror retry");
        foreach (var error in new[] { "HTTP Error 429", "HTTP status code 503", "The requested URL returned error: 403", "404 Client Error: Not Found for url https://example.invalid" })
            Check(Installer.IsSourceFailure(new InvalidOperationException(error)), "actual HTTP status failures still permit source fallback");
        var gitEnvironment = new Dictionary<string, string>();
        Installer.ConfigureGitEnvironment(gitEnvironment, "https://fixture.invalid/https://github.com/");
        Check(gitEnvironment["GIT_CONFIG_COUNT"] == "2" && gitEnvironment["GIT_CONFIG_KEY_0"] == "core.longpaths" && gitEnvironment["GIT_CONFIG_VALUE_0"] == "true"
            && gitEnvironment["GIT_CONFIG_KEY_1"].StartsWith("url."), "Git long-path support and mirror rewriting coexist in process-local configuration");
        var git = Commands.Find("git.exe") ?? Commands.Find("git");
        if (git is not null)
        {
            var longRepo = Path.Combine(work, "longpath-git-fixture");
            var longFile = Path.Combine(longRepo, string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat("long-path-segment-" + new string('x', 40), 5)), "fixture.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(longFile)!);
            File.WriteAllText(longFile, "long checkout fixture");
            Installer.ConfigureGitEnvironment(gitEnvironment, "https://github.com/");
            await Commands.Run(git, ["init"], longRepo, null, default, gitEnvironment);
            await Commands.Run(git, ["add", "."], longRepo, null, default, gitEnvironment);
            await Commands.Run(git, ["-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "-m", "long path"], longRepo, null, default, gitEnvironment);
            File.Delete(longFile);
            await Commands.Run(git, ["checkout", "--", "."], longRepo, null, default, gitEnvironment);
            Check(longFile.Length > 300 && File.ReadAllText(longFile) == "long checkout fixture", "process-local Git configuration checks out a real path beyond Windows MAX_PATH");
        }
        var requirementsRoot = Path.Combine(work, "requirements-graph");
        var nested = Path.Combine(requirementsRoot, "nested");
        Directory.CreateDirectory(nested);
        var top = Path.Combine(requirementsRoot, "requirements.txt");
        var child = Path.Combine(nested, "extra.txt");
        var constraint = Path.Combine(nested, "limits.txt");
        File.WriteAllText(top, "-r nested/extra.txt\n");
        File.WriteAllText(child, "-c limits.txt\ngit+https://github.com/facebookresearch/sam2\n");
        File.WriteAllText(constraint, "numpy<2\n");
        var identity = ComfyDependencyResolver.NodeRequirementsIdentity([top], runtime.Sam2Commit);
        var nestedReceipt = RuntimeDependencies.Receipt(requirementsRoot, probe, comfy.Commit);
        File.AppendAllText(constraint, "pillow>=10\n");
        Check(identity != ComfyDependencyResolver.NodeRequirementsIdentity([top], runtime.Sam2Commit), "nested constraint changes invalidate the combined dependency snapshot");
        Check(nestedReceipt != RuntimeDependencies.Receipt(requirementsRoot, probe, comfy.Commit), "nested requirement changes also invalidate the node completion receipt");
        Check(ComfyDependencyResolver.RequirementFiles([top]).Count == 3, "recursive requirements include both requirement and constraint inputs");
        var rewritten = ComfyDependencyResolver.PrepareNodeRequirements(top, Path.Combine(work, "pinned-graph"), runtime.Sam2Commit);
        var pinnedFiles = ComfyDependencyResolver.RequirementFiles([rewritten]);
        Check(rewritten != top && pinnedFiles.Count == 3 && pinnedFiles.Any(path => File.ReadAllText(path).Contains("sam2@" + runtime.Sam2Commit)),
            "nested SAM2 sources are pinned without breaking relative includes");
        Check(File.ReadAllText(child).Contains("sam2\n") && File.ReadAllText(top) == "-r nested/extra.txt\n", "pinning node dependencies preserves original source files");
        var enabledRoot = Path.Combine(requirementsRoot, "custom_nodes");
        foreach (var name in new[] { "enabled", "old.disabled", ".hidden" })
        {
            Directory.CreateDirectory(Path.Combine(enabledRoot, name));
            File.WriteAllText(Path.Combine(enabledRoot, name, "requirements.txt"), "# fixture\n");
        }
        Check(ComfyDependencyResolver.ExistingNodeRequirements(new() { ComfyRoot = requirementsRoot }).SequenceEqual([Path.Combine(enabledRoot, "enabled", "requirements.txt")]),
            "combined repair considers enabled nodes and excludes disabled or hidden plugins");

        // Exercise real pip resolution offline with tiny local wheels. No package is installed.
        var repository = File.Exists(Path.Combine(Environment.CurrentDirectory, "launcher", "manifests", "launcher-manifest.json"))
            ? Environment.CurrentDirectory : Directory.GetParent(work)!.Parent!.Parent!.FullName;
        var python = Path.Combine(repository, ".venv", "Scripts", "python.exe");
        if (File.Exists(python))
        {
            var fixture = Path.Combine(work, "joint-pip-fixture");
            var wheels = Path.Combine(fixture, "wheels");
            Directory.CreateDirectory(wheels);
            void Wheel(string name, string version, params string[] dependencies)
            {
                using var archive = ZipFile.Open(Path.Combine(wheels, name + "-" + version + "-py3-none-any.whl"), ZipArchiveMode.Create);
                var info = name + "-" + version + ".dist-info/";
                void Write(string file, string value) { using var writer = new StreamWriter(archive.CreateEntry(info + file).Open()); writer.Write(value); }
                Write("METADATA", "Metadata-Version: 2.1\nName: " + name + "\nVersion: " + version + "\n" + string.Join("", dependencies.Select(dep => "Requires-Dist: " + dep + "\n")) + "\n");
                Write("WHEEL", "Wheel-Version: 1.0\nGenerator: launcher-fixture\nRoot-Is-Purelib: true\nTag: py3-none-any\n");
                Write("RECORD", "");
            }
            Wheel("anima_fixture_shared", "1.0");
            Wheel("anima_fixture_shared", "2.0");
            Wheel("anima_fixture_core", "1.0", "anima-fixture-shared>=1");
            Wheel("anima_fixture_manager", "1.0", "anima-fixture-core==1.0");
            var fixtureConfig = new LauncherConfig { ComfyRoot = fixture, Python = python };
            var cache = Path.Combine(fixture, "cache");
            var snapshot = new ComfyDependencySnapshot(new string('a', 40), "anima-fixture-core==1.0\n", "anima-fixture-manager==1.0\n", []);
            var env = new Dictionary<string, string> { ["PIP_NO_INDEX"] = "1", ["PIP_FIND_LINKS"] = wheels };
            var coreOnly = await ComfyDependencyResolver.ResolveAsync(fixtureConfig, snapshot, cache, default, environment: env);
            Check(coreOnly.Constraints.Contains("anima-fixture-shared==2.0"), "offline core-only resolution selects the latest compatible fixture dependency");
            var nodeFile = Path.Combine(fixture, "node-requirements.txt");
            File.WriteAllText(nodeFile, "anima-fixture-shared<2\n");
            var combined = await ComfyDependencyResolver.ResolveAsync(fixtureConfig, coreOnly, cache, default, nodeRequirements: [nodeFile], environment: env);
            Check(combined.Constraints.Contains("anima-fixture-shared==1.0") && combined.Constraints.Contains("anima-fixture-manager==1.0"),
                "joint core Manager and node resolution revises a core-only pin while keeping all requirements satisfiable");
            Check(combined.NodeRequirementsIdentity.Length == 64 && combined.NodeRequirementsIdentity != coreOnly.NodeRequirementsIdentity,
                "resolved dependency plan binds the selected node requirements");
            var saved = Directory.GetFiles(Path.Combine(cache, "dependency-plans"), "resolved.json", SearchOption.AllDirectories)
                .Single(path => JsonFile.Read<ComfyDependencySnapshot>(path).NodeRequirementsIdentity == "");
            var relative = Path.GetRelativePath(cache, saved);
            var legacy = Path.Combine(cache, "dependencies", relative);
            Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
            File.Move(saved, legacy);
            foreach (var wheelFile in Directory.EnumerateFiles(wheels)) File.Delete(wheelFile);
            var resumed = await ComfyDependencyResolver.ResolveAsync(fixtureConfig, snapshot, cache, default, environment: env);
            Check(resumed.Constraints.SequenceEqual(coreOnly.Constraints), "legacy cache layout reuses its frozen plan even when all package sources are unavailable");
        }
        else Console.WriteLine("SKIP: offline pip resolution fixture requires repository .venv Python");
        return count;
    }
}
