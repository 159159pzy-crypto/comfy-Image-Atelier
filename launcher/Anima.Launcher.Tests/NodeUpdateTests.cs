using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Anima.Launcher.Core;

internal static class NodeUpdateTests
{
    public static async Task<int> Run(string work, RuntimeManifest runtime, bool healthMutationsOnly = false)
    {
        var passed = 0;
        void Check(bool result, string text)
        {
            if (!result) throw new Exception("FAIL: " + text);
            passed++; Console.WriteLine("PASS: " + text);
        }
        async Task Rejected(Func<Task> action, string text)
        {
            try { await action(); }
            catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or OperationCanceledException)
            { Check(true, text); return; }
            throw new Exception("FAIL: " + text);
        }

        var git = Commands.Find("git.exe") ?? Commands.Find("git") ?? throw new Exception("Git is required for node update fixtures.");
        async Task<string> Git(string directory, params string[] args) =>
            (await Commands.Run(git, ["-c", "core.longpaths=true", .. args], directory, null, default)).Trim();
        async Task Init(string directory)
        {
            Directory.CreateDirectory(directory);
            await Git(directory, "init", "-b", "main");
            await Git(directory, "config", "user.name", "Node update fixture");
            await Git(directory, "config", "user.email", "fixture@example.invalid");
            await Git(directory, "config", "core.autocrlf", "false");
        }
        async Task<string> Commit(string directory, string message)
        {
            await Git(directory, "add", "."); await Git(directory, "commit", "-m", message);
            return await Git(directory, "rev-parse", "HEAD");
        }
        var fixture = Path.Combine(work, "node-update-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(fixture, "node-source");
        await Init(source);
        File.WriteAllText(Path.Combine(source, "__init__.py"), "old node\n");
        File.WriteAllText(Path.Combine(source, "requirements.txt"), "fixture-package==1\n");
        var deepRelative = Path.Combine("nested-source-" + new string('n', 32), "nested-module-" + new string('m', 32), "deep.py");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(source, deepRelative))!);
        File.WriteAllText(Path.Combine(source, deepRelative), "old deep node");
        var before = await Commit(source, "before");
        File.WriteAllText(Path.Combine(source, "__init__.py"), "new node\n");
        File.WriteAllText(Path.Combine(source, "requirements.txt"), "fixture-package==2\n");
        File.WriteAllText(Path.Combine(source, "new_module.py"), "new target file\n");
        File.WriteAllText(Path.Combine(source, deepRelative), "new deep node");
        var target = await Commit(source, "target");

        var environment = Path.Combine(fixture, "ComfyUI");
        await Init(environment);
        File.WriteAllText(Path.Combine(environment, ".gitignore"), ".venv/\nvenv/\npython_embeded/\ncustom_nodes/\nmodels/\nuser/\n.anima-*\n");
        File.WriteAllText(Path.Combine(environment, "main.py"), "fixed ComfyUI\n");
        File.WriteAllText(Path.Combine(environment, "requirements.txt"), "numpy>=1\n");
        File.WriteAllText(Path.Combine(environment, "manager_requirements.txt"), "comfyui_manager==4.2.2\n");
        var core = await Commit(environment, "fixed core");
        var custom = Path.Combine(environment, "custom_nodes"); Directory.CreateDirectory(custom);
        var node = Path.Combine(custom, "known-node");
        await Git(custom, "clone", "--no-hardlinks", source, node);
        await Git(node, "checkout", "--detach", before);
        await Git(node, "remote", "set-url", "origin", "https://github.com/fixture/known-node.git");
        var nodeConfig = Path.Combine(node, "user-settings.json");
        File.WriteAllText(nodeConfig, "user node configuration");
        var dirty = Path.Combine(custom, "dirty-node");
        await Git(custom, "clone", "--no-hardlinks", source, dirty);
        await Git(dirty, "remote", "set-url", "origin", "https://github.com/fixture/dirty-node.git");
        File.WriteAllText(Path.Combine(dirty, "__init__.py"), "user edited source");
        var plain = Path.Combine(custom, "plain-node"); Directory.CreateDirectory(plain);
        File.WriteAllText(Path.Combine(plain, "__init__.py"), "non-Git user node");
        var unknown = Path.Combine(custom, "unknown-node");
        await Git(custom, "clone", "--no-hardlinks", source, unknown);
        const string secret = "fixture-password-never-log";
        await Git(unknown, "remote", "set-url", "origin", "https://fixture-user:" + secret + "@private.example.invalid/node.git");
        var pythonDirectory = Path.Combine(environment, ".venv");
        var python = Path.Combine(pythonDirectory, "Scripts", "python.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(python)!);
        File.WriteAllText(python, "fixture interpreter, never executed");
        File.WriteAllText(Path.Combine(pythonDirectory, "pyvenv.cfg"), "include-system-site-packages = false\n");
        var package = Path.Combine(pythonDirectory, "package.txt"); File.WriteAllText(package, "original package tree");
        var torch = Path.Combine(pythonDirectory, "torch.dll"); File.WriteAllBytes(torch, [8, 6, 7, 5, 3, 0, 9]);
        var torchDistribution = Path.Combine(pythonDirectory, "Lib", "site-packages", "torch-2.11.0.dist-info");
        Directory.CreateDirectory(torchDistribution);
        var torchMetadata = Path.Combine(torchDistribution, "METADATA");
        File.WriteAllText(torchMetadata, "Name: torch\nVersion: 2.11.0+cu128\n");
        File.WriteAllText(Path.Combine(torchDistribution, "RECORD"), "torch.dll,,\n");
        var model = Path.Combine(environment, "models", "keep.bin"); Directory.CreateDirectory(Path.GetDirectoryName(model)!);
        File.WriteAllBytes(model, [2, 4, 6, 8]);
        var settings = Path.Combine(environment, "user", "default", "settings.json"); Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        File.WriteAllText(settings, "original user settings");
        var database = Path.Combine(environment, "user", "manager.db"); File.WriteAllBytes(database, [0, 1, 2, 3]);
        var modelPaths = Path.Combine(environment, "extra_model_paths.yaml"); File.WriteAllText(modelPaths, "original model paths");
        var constraints = Path.Combine(environment, ".anima-constraints.txt"); File.WriteAllText(constraints, "torch==fixture\n");
        var state = Path.Combine(fixture, "state"); Directory.CreateDirectory(state);
        var ports = FreePorts();
        var config = new LauncherConfig { ComfyRoot = environment, Python = python, RuntimeRoot = Path.Combine(fixture, "runtime"), ComfyPort = ports.Item1, WebPort = ports.Item2, Theme = "dark" };
        JsonFile.Write(Path.Combine(state, "launcher.json"), config);
        var messages = new List<string>(); var resolves = new List<string>(); var fetches = new List<string>();
        var resolvedTarget = target; var rejectDependencies = false; var dependencyCalls = 0;
        NodeUpdateCoordinator Coordinator() => new(state, git, messages.Add,
            (repository, _) => { resolves.Add(repository.Id); return Task.FromResult(new NodeRelease(repository.Id, repository.Url, "main", resolvedTarget)); },
            async (entry, token) => { fetches.Add(entry.Name); await Commands.Run(git, ["fetch", source, entry.TargetSha], entry.Directory, null, token); },
            async (_, snapshot, requirements, map, token) =>
            {
                token.ThrowIfCancellationRequested(); dependencyCalls++;
                Check(await Git(node, "rev-parse", "HEAD") == before, "dependency preflight runs while the original node is active");
                Check(requirements.Any(path => path.Contains("node-update-plans") && File.ReadAllText(path).Contains("fixture-package==2")),
                    "preflight reads the frozen target requirements from separate staging");
                if (rejectDependencies) throw new InvalidOperationException("fixture requirements conflict");
                return snapshot with { Resolved = true, PythonIdentity = "fixture", Constraints = ["torch==fixture", "fixture-package==2"],
                    NodeRequirementsIdentity = ComfyDependencyResolver.NodeRequirementsIdentity(requirements, runtime.Sam2Commit, map) };
            });
        var coordinator = Coordinator();
        var plan = await coordinator.CheckAsync(config, runtime, default);
        Check(plan.CanApply && plan.Entries.Single(entry => entry.CanUpdate).Directory == node, "only a clean independent Git node is eligible");
        Check(plan.Entries.Single(entry => entry.Name == "dirty-node").CanUpdate == false
            && plan.Entries.Single(entry => entry.Name == "plain-node").CanUpdate == false,
            "dirty and non-Git user nodes remain listed but skipped");
        Check(resolves.SequenceEqual(["known-node"]) && fetches.SequenceEqual(["known-node"])
            && !JsonSerializer.Serialize(plan, JsonFile.Options).Contains(secret) && !string.Join("\n", messages).Contains(secret),
            "an unknown credential-bearing origin is skipped before network work without persisting or logging credentials");
        File.WriteAllText(Path.Combine(source, "__init__.py"), "later upstream node\n");
        resolvedTarget = await Commit(source, "upstream advanced after review");
        var loaded = Coordinator().LoadPlan(config)!;
        Check(loaded.CanApply && loaded.Entries.Single(entry => entry.CanUpdate).TargetSha == target && resolves.Count == 1,
            "restart reloads the reviewed full commit and never re-resolves a newer branch head");
        if (healthMutationsOnly) { await HealthMutationChecks(); return passed; }
        var switched = config.Clone(); switched.Python = Path.Combine(fixture, "other-python.exe");
        Check(coordinator.LoadPlan(switched) is null, "persisted node plans cannot follow a changed interpreter");

        rejectDependencies = true; var installed = false;
        await Rejected(() => coordinator.ApplyAsync(config, loaded, (_, _, _) => { installed = true; return Task.CompletedTask; }, (_, _) => Task.CompletedTask, default),
            "dependency conflicts reject the update before taking a backup or switching source");
        Check(!installed && coordinator.Previous(config) is null && await Git(node, "rev-parse", "HEAD") == before && File.ReadAllText(package) == "original package tree",
            "failed preflight leaves node, Python and backup pointer untouched");
        rejectDependencies = false;

        var staged = loaded.Entries.Single(entry => entry.CanUpdate).StagedDirectory;
        Check(Path.Combine(staged, deepRelative).Length > 260 && File.ReadAllText(Path.Combine(staged, deepRelative)) == "new deep node",
            "production staging clone and checkout retain tracked source beyond the Windows legacy path limit");
        var stagedInit = Path.Combine(staged, "__init__.py"); var stagedText = File.ReadAllText(stagedInit);
        File.WriteAllText(stagedInit, "tampered staging");
        await Rejected(() => coordinator.ApplyAsync(config, loaded, (_, _, _) => Task.CompletedTask, (_, _) => Task.CompletedTask, default),
            "a changed staged target cannot be applied under the reviewed plan");
        File.WriteAllText(stagedInit, stagedText);
        var collision = Path.Combine(node, "new_module.py"); File.WriteAllText(collision, "user file must survive");
        await Rejected(() => coordinator.ApplyAsync(config, loaded, (_, _, _) => Task.CompletedTask, (_, _) => Task.CompletedTask, default),
            "a target tracked file colliding with an existing untracked user file blocks update");
        Check(File.ReadAllText(collision) == "user file must survive" && coordinator.Previous(config) is null, "collision detection preserves the original file and does not start a transaction");
        File.Delete(collision);

        using (var listener = new TcpListener(IPAddress.Loopback, config.ComfyPort))
        {
            listener.Start();
            await Rejected(() => coordinator.ApplyAsync(config, loaded, (_, _, _) => Task.CompletedTask, (_, _) => Task.CompletedTask, default),
                "a running ComfyUI port blocks node and Python changes");
        }
        var shared = config.Clone(); shared.Python = Path.Combine(fixture, "shared", "python.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(shared.Python)!); File.WriteAllText(shared.Python, "shared interpreter");
        await Rejected(() => coordinator.PrepareAsync(shared, loaded with { Python = shared.Python }, runtime, default),
            "system or shared Python is rejected before dependency installation");
        File.WriteAllText(Path.Combine(pythonDirectory, "pyvenv.cfg"), "include-system-site-packages = true\n");
        await Rejected(() => coordinator.ApplyAsync(config, loaded, (_, _, _) => Task.CompletedTask, (_, _) => Task.CompletedTask, default),
            "a venv sharing system packages is not treated as a recoverable independent environment");
        File.WriteAllText(Path.Combine(pythonDirectory, "pyvenv.cfg"), "include-system-site-packages = false\n");

        void Mutate()
        {
            File.WriteAllText(package, "changed package tree"); File.WriteAllBytes(torch, [9, 9]);
            File.WriteAllText(settings, "migrated user settings"); File.WriteAllBytes(database, [9, 8]);
            File.WriteAllText(modelPaths, "changed model paths"); File.WriteAllText(constraints, "changed pins");
            File.WriteAllText(nodeConfig, "migrated node settings");
            File.WriteAllText(Path.Combine(environment, "config.json"), "new root settings");
            File.WriteAllText(Path.Combine(environment, "user", "created.json"), "new user setting");
            var changed = config.Clone(); changed.Theme = "light"; JsonFile.Write(Path.Combine(state, "launcher.json"), changed);
        }
        async Task Restored(string cause)
        {
            Check(await Git(node, "rev-parse", "HEAD") == before && await Git(environment, "rev-parse", "HEAD") == core
                && File.ReadAllText(Path.Combine(node, deepRelative)) == "old deep node"
                && File.ReadAllText(package) == "original package tree" && File.ReadAllBytes(torch).SequenceEqual(new byte[] { 8, 6, 7, 5, 3, 0, 9 })
                && RuntimeDependencies.InstalledPackages(config.Python).GetValueOrDefault("torch") == "2.11.0+cu128",
                cause + " restores the complete old node and Python while retaining the exact ComfyUI commit");
            Check(File.ReadAllText(nodeConfig) == "user node configuration" && File.ReadAllText(settings) == "original user settings"
                && File.ReadAllBytes(database).SequenceEqual(new byte[] { 0, 1, 2, 3 }) && File.ReadAllText(modelPaths) == "original model paths"
                && File.ReadAllText(constraints) == "torch==fixture\n" && !File.Exists(Path.Combine(environment, "config.json"))
                && !File.Exists(Path.Combine(environment, "user", "created.json")) && JsonFile.Read<LauncherConfig>(Path.Combine(state, "launcher.json")).Theme == "dark",
                cause + " restores node, Manager, user and launcher configuration including absent files");
            Check(File.ReadAllBytes(model).SequenceEqual(new byte[] { 2, 4, 6, 8 }) && File.ReadAllText(Path.Combine(dirty, "__init__.py")) == "user edited source"
                && File.ReadAllText(Path.Combine(plain, "__init__.py")) == "non-Git user node" && coordinator.Previous(config)?.Status == "rolled-back",
                cause + " preserves models and skipped user nodes and records completed recovery");
        }
        async Task HealthMutationChecks()
        {
            await Rejected(() => coordinator.ApplyAsync(config, loaded, (_, _, _) => Task.CompletedTask, (_, _) =>
            {
                Mutate(); File.WriteAllText(torchMetadata, "Name: torch\nVersion: 9.9.9\n"); return Task.CompletedTask;
            }, default), "a health startup that changes the GPU distribution is detected after validation and rolled back");
            await Restored("Health-time GPU drift");
            await Rejected(() => coordinator.ApplyAsync(config, loaded, (_, _, _) => Task.CompletedTask, async (_, _) =>
            {
                Mutate(); await Git(node, "checkout", "--detach", before);
            }, default), "a health startup that changes the reviewed node HEAD is detected after validation and rolled back");
            await Restored("Health-time node HEAD drift");
        }
        await Rejected(() => coordinator.ApplyAsync(config, loaded, (_, _, _) => { Mutate(); throw new InvalidOperationException("injected dependency failure"); }, (_, _) => Task.CompletedTask, default),
            "dependency installation failure triggers complete automatic rollback");
        await Restored("Dependency failure");
        await Rejected(() => coordinator.ApplyAsync(config, loaded, (_, _, _) => Task.CompletedTask, (_, _) => { Mutate(); throw new InvalidOperationException("injected health failure"); }, default),
            "node health failure triggers complete automatic rollback");
        await Restored("Health failure");
        using (var canceled = new CancellationTokenSource())
        {
            await Rejected(() => coordinator.ApplyAsync(config, loaded, (_, _, _) => { Mutate(); canceled.Cancel(); canceled.Token.ThrowIfCancellationRequested(); return Task.CompletedTask; }, (_, _) => Task.CompletedTask, canceled.Token),
                "cancellation during dependency installation restores with an independent recovery token");
        }
        await Restored("Cancellation");
        await Rejected(() => coordinator.ApplyAsync(config, loaded, (_, _, _) =>
        {
            Mutate(); File.WriteAllText(torchMetadata, "Name: torch\nVersion: 9.9.9\n"); return Task.CompletedTask;
        }, (_, _) => Task.CompletedTask, default), "dependency installation that changes the GPU distribution version is rolled back");
        await Restored("GPU version drift");
        await HealthMutationChecks();

        var healthSawTarget = false;
        var completed = await coordinator.ApplyAsync(config, loaded, (_, _, _) => { File.WriteAllText(package, "successful new package"); return Task.CompletedTask; },
            async (_, _) => { healthSawTarget = await Git(node, "rev-parse", "HEAD") == target; }, default);
        Check(completed.Status == "complete" && healthSawTarget && coordinator.LoadPlan(config) is null
            && File.ReadAllText(Path.Combine(node, deepRelative)) == "new deep node"
            && await Git(environment, "rev-parse", "HEAD") == core && File.ReadAllText(nodeConfig) == "user node configuration"
            && File.ReadAllBytes(model).SequenceEqual(new byte[] { 2, 4, 6, 8 }) && File.ReadAllBytes(torch).SequenceEqual(new byte[] { 8, 6, 7, 5, 3, 0, 9 }),
            "successful update health-checks the frozen target, preserves user files and core/GPU/models, and clears its saved plan");
        Check(Directory.EnumerateDirectories(custom).All(path => !Path.GetFileName(path).Contains("anima-node")),
            "retired nodes are kept outside custom_nodes so ComfyUI cannot load duplicate old plugins");
        await coordinator.RollbackAsync(config, default); await Restored("Manual rollback");

        _ = await coordinator.ApplyAsync(config, loaded, (_, _, _) => { Mutate(); return Task.CompletedTask; }, (_, _) => Task.CompletedTask, default);
        var interrupted = coordinator.Previous(config)! with { Status = "restoring" };
        JsonFile.Write(Path.Combine(interrupted.Directory, "backup.json"), interrupted);
        var pointer = Directory.GetFiles(Path.Combine(state, "node-updates"), "*.json").Single(); JsonFile.Write(pointer, interrupted);
        // Simulate a process exit between directory exchanges, entirely inside this fixture.
        var retiredRoot = Path.Combine(fixture, "interrupted-retired"); Directory.CreateDirectory(retiredRoot);
        Directory.Move(node, Path.Combine(retiredRoot, "node")); Directory.Move(pythonDirectory, Path.Combine(retiredRoot, "python"));
        await Coordinator().RollbackAsync(config, default); await Restored("Interrupted restore retry");

        // A real nested Git repository proves the target parent's gitlink wins over the child's moving default branch.
        var child = Path.Combine(fixture, "child-source"); await Init(child);
        File.WriteAllText(Path.Combine(child, "child.py"), "child A\n"); var childA = await Commit(child, "child A");
        File.WriteAllText(Path.Combine(child, "child.py"), "child B\n"); var childB = await Commit(child, "child B");
        var parent = Path.Combine(fixture, "parent-source"); await Init(parent);
        File.WriteAllText(Path.Combine(parent, "__init__.py"), "parent\n");
        await Git(parent, "-c", "protocol.file.allow=always", "submodule", "add", child, "dep");
        await Git(Path.Combine(parent, "dep"), "checkout", "--detach", childA);
        await Git(parent, "config", "-f", ".gitmodules", "submodule.dep.branch", "main");
        var parentA = await Commit(parent, "parent pins A");
        await Git(Path.Combine(parent, "dep"), "checkout", "--detach", childB); var parentB = await Commit(parent, "parent pins B");
        File.WriteAllText(Path.Combine(child, "child.py"), "child C\n"); var childC = await Commit(child, "child branch moves to C");
        var subnode = Path.Combine(custom, "submodule-node"); await Git(custom, "clone", "--no-hardlinks", parent, subnode);
        await Git(subnode, "checkout", "--detach", parentA);
        await Git(subnode, "-c", "protocol.file.allow=always", "submodule", "update", "--init", "--recursive");
        await Git(subnode, "remote", "set-url", "origin", "https://github.com/fixture/submodule-node.git");
        var subCoordinator = new NodeUpdateCoordinator(state, git, null,
            (repository, _) => Task.FromResult(new NodeRelease(repository.Id, repository.Url, "main", repository.Id == "submodule-node" ? parentB : before)),
            async (entry, token) => { await Commands.Run(git, ["fetch", parent, entry.TargetSha], entry.Directory, null, token); },
            (_, snapshot, requirements, map, _) => Task.FromResult(snapshot with { Resolved = true, PythonIdentity = "fixture", Constraints = ["torch==fixture"],
                NodeRequirementsIdentity = ComfyDependencyResolver.NodeRequirementsIdentity(requirements, runtime.Sam2Commit, map) }),
            async (stage, _, token) => { await Commands.Run(git, ["-c", "core.longpaths=true", "-c", "protocol.file.allow=always", "submodule", "update", "--init", "--recursive"], stage, null, token); });
        var subPlan = await subCoordinator.CheckAsync(config, runtime, default);
        Check(subPlan.CanApply && subPlan.Entries.Single(entry => entry.CanUpdate).TargetSha == parentB,
            "submodule fixture prepares only the reviewed parent update: " + subPlan.Note);
        await subCoordinator.ApplyAsync(config, subPlan, (_, _, _) => Task.CompletedTask, (_, _) => Task.CompletedTask, default);
        Check(await Git(Path.Combine(subnode, "dep"), "rev-parse", "HEAD") == childB && childB != childC,
            "submodule update follows the target parent gitlink even when .gitmodules tracks a newer main branch");
        await subCoordinator.RollbackAsync(config, default);
        Check(await Git(subnode, "rev-parse", "HEAD") == parentA && await Git(Path.Combine(subnode, "dep"), "rev-parse", "HEAD") == childA,
            "manual rollback restores the complete parent and nested submodule Git state");

        File.WriteAllText(Path.Combine(environment, "main.py"), "next ComfyUI\n");
        await Git(environment, "add", "main.py"); await Git(environment, "commit", "-m", "next core");
        var nextCore = await Git(environment, "rev-parse", "HEAD");
        var coreSource = Path.Combine(fixture, "core-source");
        await Git(fixture, "clone", "--no-hardlinks", environment, coreSource);
        await Git(environment, "checkout", "--detach", core);
        var coreCoordinator = new ComfyUpdateCoordinator(state, git, null,
            async (root, commit, token) => { await Commands.Run(git, ["fetch", coreSource, commit], root, null, token); });
        var release = new ComfyRelease("v9.9.9", nextCore, null, "https://github.com/Comfy-Org/ComfyUI/releases/tag/v9.9.9");
        var coreDependencies = new ComfyDependencySnapshot(nextCore, "numpy>=1\n", "comfyui_manager==4.2.2\n", ["torch==fixture"]) { Resolved = true };
        Task CoreUpdate() => coreCoordinator.UpdateAsync(config, release, runtime, coreDependencies,
            (_, _, _) => { File.WriteAllText(package, "core-updated package"); return Task.CompletedTask; }, (_, _) => Task.CompletedTask, default);

        // Node -> core must unwind core -> node because each operation snapshots the complete Python tree.
        await coordinator.ApplyAsync(config, loaded, (_, _, _) => { File.WriteAllText(package, "node-updated package"); return Task.CompletedTask; }, (_, _) => Task.CompletedTask, default);
        await CoreUpdate();
        await Rejected(() => coordinator.RollbackAsync(config, default), "node rollback refuses to replace Python underneath a newer ComfyUI version");
        Check(await Git(environment, "rev-parse", "HEAD") == nextCore && File.ReadAllText(package) == "core-updated package",
            "out-of-order node rollback leaves the newer core and Python intact");
        await coreCoordinator.RollbackAsync(config, default);
        Check(await Git(environment, "rev-parse", "HEAD") == core && await Git(node, "rev-parse", "HEAD") == target
            && File.ReadAllText(package) == "node-updated package", "rolling back the later core update restores its preceding node/Python combination");
        await coordinator.RollbackAsync(config, default);
        Check(await Git(node, "rev-parse", "HEAD") == before && File.ReadAllText(package) == "original package tree",
            "the earlier node update can then be safely rolled back");

        // Core -> node must unwind node -> core for the same dependency compatibility reason.
        await CoreUpdate();
        var newerCorePlan = loaded with { CoreCommit = nextCore, Dependencies = loaded.Dependencies! with { ComfyCommit = nextCore } };
        await coordinator.ApplyAsync(config, newerCorePlan, (_, _, _) => { File.WriteAllText(package, "node-on-new-core package"); return Task.CompletedTask; }, (_, _) => Task.CompletedTask, default);
        await Rejected(() => coreCoordinator.RollbackAsync(config, default), "core rollback refuses to restore an older Python snapshot while a newer node update is active");
        Check(await Git(node, "rev-parse", "HEAD") == target && File.ReadAllText(package) == "node-on-new-core package",
            "out-of-order core rollback leaves the newer nodes and Python intact");
        await coordinator.RollbackAsync(config, default);
        Check(await Git(environment, "rev-parse", "HEAD") == nextCore && await Git(node, "rev-parse", "HEAD") == before
            && File.ReadAllText(package) == "core-updated package", "rolling back the later node update restores its preceding core/Python combination");
        await coreCoordinator.RollbackAsync(config, default);
        Check(await Git(environment, "rev-parse", "HEAD") == core && File.ReadAllText(package) == "original package tree"
            && File.ReadAllBytes(model).SequenceEqual(new byte[] { 2, 4, 6, 8 }), "the earlier core update can then be safely rolled back without touching models");
        return passed;
    }

    private static (int, int) FreePorts()
    {
        using var first = new TcpListener(IPAddress.Loopback, 0); using var second = new TcpListener(IPAddress.Loopback, 0);
        first.Start(); second.Start();
        return (((IPEndPoint)first.LocalEndpoint).Port, ((IPEndPoint)second.LocalEndpoint).Port);
    }
}
