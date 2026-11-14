using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anima.Launcher.Core;

internal static class ComfyLightUpdateTests
{
    public static async Task<int> Run(string state, RuntimeManifest runtime, string git, string source,
        LauncherConfig config, string before, string after)
    {
        var passed = 0;
        void Check(bool condition, string label) { if (!condition) throw new Exception("FAIL: " + label); passed++; Console.WriteLine("PASS: " + label); }
        async Task Reject(Func<Task> action, string label)
        {
            try { await action(); }
            catch (Exception error) when (error is InvalidOperationException or InvalidDataException or IOException or AggregateException)
            { Check(true, label); return; }
            throw new Exception("FAIL: " + label);
        }
        async Task<string> Git(string root, params string[] args) => (await Commands.Run(git, args, root, null, default)).Trim();
        // These interpreters are text fixtures and can never be live processes.
        // Exercise the real stop guard separately with an actual listener below.
        Task Stopped(LauncherConfig target, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        ComfyUpdateCoordinator Coordinator() => new(state, git, null,
            async (root, commit, token) => { await Commands.Run(git, ["fetch", source, commit], root, null, token); }, Stopped);
        var coordinator = Coordinator();
        var phases = new List<string>(); coordinator.Progress = phases.Add;
        var root = config.ComfyRoot;
        var package = Path.Combine(root, ".venv", "package.txt");
        var settings = Path.Combine(root, "user", "default", "settings.json");
        var database = Path.Combine(root, "user", "manager.db");
        var modelPaths = Path.Combine(root, "extra_model_paths.yaml");
        var release = new ComfyRelease("v1.2.5", after, null, "https://github.com/Comfy-Org/ComfyUI/releases/tag/v1.2.5");
        var snapshot = new ComfyDependencySnapshot(after, "numpy>=1\n", "comfyui_manager==4.2.2\n", ["numpy==2.0.0"])
            { Resolved = true, EnvironmentPackages = ["numpy==1.0.0", "torch==2.11.0+cu128"] };
        Task Valid(LauncherConfig target, CancellationToken token) => Task.CompletedTask;
        Task NoInstall(LauncherConfig target, ComfyDependencySnapshot plan, CancellationToken token) => Task.CompletedTask;

        var prepared = false;
        await Reject(() => coordinator.UpdateAsync(config, release, runtime, snapshot, NoInstall, Valid, default,
            prepareDependencies: (_, _, _) => { prepared = true; throw new IOException("injected preparation download failure"); }),
            "preparation failure leaves source and Python unchanged without publishing a recovery point");
        Check(prepared && coordinator.Previous(config) is null && await Git(root, "rev-parse", "HEAD") == before
            && File.ReadAllText(package) == "old dependency", "preparation finishes before checkout or dependency installation");
        using (var prepareCancellation = new CancellationTokenSource())
        {
            var installed = false;
            try
            {
                await coordinator.UpdateAsync(config, release, runtime, snapshot,
                    (_, _, _) => { installed = true; return Task.CompletedTask; }, Valid, prepareCancellation.Token,
                    prepareDependencies: (_, _, token) => { prepareCancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
                throw new Exception("Preparation cancellation expected");
            }
            catch (OperationCanceledException)
            {
                Check(!installed && coordinator.Previous(config) is null && !File.Exists(Pointer(state, config))
                    && await Git(root, "for-each-ref", "--format=%(refname)", "refs/anima/comfy-recovery/") == "",
                    "cancelling preparation creates neither a recovery record nor a Git recovery ref and never installs packages");
                Check(await Git(root, "rev-parse", "HEAD") == before && File.ReadAllText(package) == "old dependency",
                    "preparation cancellation leaves the original source and Python bytes unchanged");
            }
        }
        prepared = false;
        await Reject(() => coordinator.UpdateAsync(config, release, runtime, snapshot, NoInstall, Valid, default,
            prepareDependencies: (_, _, _) => { prepared = true; return Task.CompletedTask; },
            validateTargetSource: (_, text, _) => { Check(text.Contains("verified target parser"), "preflight reads the verified target parser from Git"); throw new IOException("unsupported isolation flags"); }),
            "unsupported health isolation stops before preparing or changing the environment");
        Check(!prepared && coordinator.Previous(config) is null, "target preflight precedes dependency downloads and recovery-point creation");

        await Git(root, "checkout", "-b", "fixture-original", before);
        var validations = 0;
        await Reject(() => coordinator.UpdateAsync(config, release, runtime, snapshot, (_, _, _) =>
        {
            File.WriteAllText(package, "broken dependency");
            throw new IOException("injected local installation failure");
        }, Valid, default, validateRecovery: (_, _) => { validations++; throw new IOException("old requirements not satisfied"); }),
            "failed dependency application restores source and small configs while retaining explicit repair status");
        var recovery = coordinator.Previous(config)!;
        Check(recovery is { FormatVersion: 1, RecoveryStrategy: "light", Status: "needs-repair", DependenciesMayHaveChanged: true }
            && validations == 1 && recovery.EnvironmentPackages.SequenceEqual(snapshot.EnvironmentPackages),
            "light recovery retains old package inventory and does not claim complete Python restoration");
        Check(await Git(root, "rev-parse", "HEAD") == before && await Git(root, "branch", "--show-current") == "fixture-original"
            && File.ReadAllText(package) == "broken dependency", "automatic rollback restores the prior branch without replacing Python packages");
        Check(File.ReadAllText(modelPaths) == "original model paths" && !File.Exists(Path.Combine(root, ".anima-constraints.txt"))
            && File.ReadAllText(Path.Combine(root, "config.yml")).Replace("\r\n", "\n") == "fixture: old\n"
            && File.ReadAllText(settings) == "original settings" && File.ReadAllBytes(database).SequenceEqual(new byte[] { 0, 1, 2, 3 }),
            "light recovery restores only root config files and leaves user workflows and databases untouched");
        Check(!Directory.Exists(Path.Combine(recovery.Directory, "venv")) && !Directory.Exists(Path.Combine(recovery.Directory, "git"))
            && !Directory.Exists(Path.Combine(recovery.Directory, "source")) && !Directory.Exists(Path.Combine(recovery.Directory, "configuration", "user")),
            "new recovery points contain no complete Git, source, Python or user-directory copies");
        Check(File.Exists(Path.Combine(root, "shape")) && File.Exists(Path.Combine(root, "reverse", "item.py"))
            && !File.Exists(Path.Combine(root, "new_module.py")), "Git rollback handles file/directory transitions and removes new tracked files");
        Check(File.ReadAllText(Path.Combine(root, "models", "keep.bin")) == "user model"
            && File.ReadAllText(Path.Combine(root, "custom_nodes", "user-plugin", "keep.py")) == "user plugin", "light update preserves models and custom nodes");
        Check(ComfyUpdateCoordinator.RequiresRecovery(recovery), "dependency repair status blocks another environment update");
        Check(ComfyUpdateCoordinator.RequiresRecovery(recovery with { FormatVersion = 99, Status = "complete" })
            && ComfyUpdateCoordinator.RequiresRecovery(recovery with { Status = "future-state" }),
            "unknown recovery formats and states block backend updates instead of discarding the record");
        await Reject(() => coordinator.UpdateAsync(config, release, runtime, snapshot, NoInstall, Valid, default), "pending dependency repair blocks a second update");
        await Reject(() => coordinator.CompleteDependencyRepairAsync(config, (_, _) => throw new IOException("still incompatible"), default),
            "a failed explicit repair validation cannot clear the pending recovery record");
        Check(coordinator.Previous(config)!.Status == "needs-repair", "failed repair keeps its durable pending status");
        File.WriteAllText(package, "repaired compatible dependency");
        await coordinator.CompleteDependencyRepairAsync(config, Valid, default);
        Check(coordinator.Previous(config)!.Status == "rolled-back", "explicit repair completes only after validation succeeds");
        Check(phases.Contains("preparing") && phases.Contains("source-checkout") && phases.Contains("dependencies")
            && phases.Contains("restoring-source") && phases.Contains("dependency-review"), "update and recovery publish durable progress phases");

        var sequence = new List<string>();
        var completed = await coordinator.UpdateAsync(config, release, runtime, snapshot, async (_, _, _) =>
        {
            Check(sequence.SequenceEqual(new[] { "prepared" }) && await Git(root, "rev-parse", "HEAD") == after,
                "prepared artifacts are consumed after the exact source commit is checked out");
            sequence.Add("installed"); File.WriteAllText(package, "new dependency");
        }, (_, _) => { sequence.Add("validated"); return Task.CompletedTask; }, default,
            prepareDependencies: async (_, _, _) => { Check(await Git(root, "rev-parse", "HEAD") == before, "artifact preparation observes original source"); sequence.Add("prepared"); },
            validateRecovery: Valid);
        Check(completed.Status == "complete" && completed.CoreVerified && sequence.SequenceEqual(new[] { "prepared", "installed", "validated" }),
            "successful light update publishes completion only after installation and health validation");
        var unchangedPointer = File.ReadAllBytes(Pointer(state, config));
        var unchangedBackup = File.ReadAllBytes(Path.Combine(completed.Directory, "backup.json"));
        var unchangedRefs = await Git(root, "for-each-ref", "--format=%(refname) %(objectname)", "refs/anima/comfy-recovery/");
        var unexpectedWork = false;
        await Reject(() => coordinator.UpdateAsync(config, release, runtime, snapshot,
            (_, _, _) => { unexpectedWork = true; return Task.CompletedTask; }, Valid, default,
            prepareDependencies: (_, _, _) => { unexpectedWork = true; return Task.CompletedTask; }),
            "an environment already at the selected release rejects a redundant update");
        Check(!unexpectedWork && File.ReadAllBytes(Pointer(state, config)).SequenceEqual(unchangedPointer)
            && File.ReadAllBytes(Path.Combine(completed.Directory, "backup.json")).SequenceEqual(unchangedBackup)
            && await Git(root, "for-each-ref", "--format=%(refname) %(objectname)", "refs/anima/comfy-recovery/") == unchangedRefs,
            "a no-op update preserves the existing recovery record and refs byte for byte without preparing or installing");
        Check(File.ReadAllText(Path.Combine(root, "config.yml")).Replace("\r\n", "\n") == "fixture: new\n",
            "tracked root configuration follows the verified target Git blob including checkout line endings");
        Check(await Git(root, "status", "--porcelain", "--untracked-files=no") == ""
            && File.ReadAllText(Path.Combine(root, ".anima-revision.json")).Contains("tracked marker stays under Git ownership"),
            "tracked revision markers stay owned by Git and cannot make a successful update unrollbackable");
        await Git(root, "gc", "--prune=now");
        Check(await Git(root, "rev-parse", completed.RecoveryRef) == before, "dedicated recovery ref remains available after Git maintenance");
        File.AppendAllText(Path.Combine(root, "main.py"), "user edit after update");
        await Reject(() => coordinator.RollbackAsync(config, default, Valid), "manual light rollback rejects tracked edits without overwriting them");
        Check(File.ReadAllText(Path.Combine(root, "main.py")).Contains("user edit after update"), "rejected rollback preserves exact tracked user changes");
        await Git(root, "checkout", "--", "main.py");
        File.WriteAllText(modelPaths, "user changed model paths after update");
        await Reject(() => coordinator.RollbackAsync(config, default, Valid), "manual light rollback detects root configuration drift");
        Check(File.ReadAllText(modelPaths) == "user changed model paths after update" && await Git(root, "rev-parse", "HEAD") == after,
            "configuration conflict leaves both user config and source version unchanged");
        File.WriteAllText(modelPaths, "original model paths");
        config.Theme = "light"; JsonFile.Write(Path.Combine(state, "launcher.json"), config);
        await Coordinator().RollbackAsync(config, default, Valid);
        Check(await Git(root, "rev-parse", "HEAD") == before && File.ReadAllText(package) == "new dependency"
            && JsonFile.Read<LauncherConfig>(Path.Combine(state, "launcher.json")).Theme == "light",
            "manual light rollback keeps compatible Python packages and newer launcher preferences");

        using (var cancelled = new CancellationTokenSource())
        {
            try
            {
                await coordinator.UpdateAsync(config, release, runtime, snapshot, (_, _, _) =>
                {
                    File.WriteAllText(package, "cancelled dependency"); cancelled.Cancel(); throw new OperationCanceledException(cancelled.Token);
                }, Valid, cancelled.Token, validateRecovery: (_, _) => throw new IOException("dependency incomplete"));
                throw new Exception("Cancellation expected");
            }
            catch (OperationCanceledException)
            {
                Check(await Git(root, "rev-parse", "HEAD") == before && coordinator.Previous(config)!.Status == "needs-repair"
                    && File.ReadAllText(package) == "cancelled dependency", "cancellation waits for source recovery and preserves the honest dependency-repair outcome");
            }
        }
        await coordinator.CompleteDependencyRepairAsync(config, Valid, default);
        File.WriteAllText(Path.Combine(root, "new_module.py"), "user untracked file");
        await Reject(() => coordinator.UpdateAsync(config, release, runtime, snapshot, NoInstall, Valid, default), "untracked source collisions block light updates");
        Check(File.ReadAllText(Path.Combine(root, "new_module.py")) == "user untracked file", "a rejected update retains untracked file bytes");
        File.Delete(Path.Combine(root, "new_module.py"));
        using (var listener = new TcpListener(IPAddress.Loopback, config.ComfyPort))
        {
            listener.Start();
            await Reject(() => new ComfyUpdateCoordinator(state, git).ValidateEnvironmentAsync(config, default), "a running environment remains ineligible for updates");
        }
        var shared = config.Clone(); shared.Python = Path.Combine(state, "shared-python.exe");
        await Reject(() => coordinator.ValidateEnvironmentAsync(shared, default), "a shared interpreter remains ineligible for mutation");

        completed = await coordinator.UpdateAsync(config, release, runtime, snapshot, NoInstall, Valid, default);
        var pointer = Pointer(state, config);
        JsonFile.Write(pointer, completed with { Status = "updating", Phase = "health-check" });
        await Coordinator().RollbackAsync(config, default, Valid);
        Check(await Git(root, "rev-parse", "HEAD") == before && coordinator.Previous(config)!.Status == "rolled-back",
            "a restarted light transaction restores from its persisted ref without network access");
        completed = await coordinator.UpdateAsync(config, release, runtime, snapshot, NoInstall, Valid, default);
        await Git(root, "update-ref", completed.RecoveryRef, after);
        await Reject(() => coordinator.RollbackAsync(config, default, Valid), "a tampered recovery ref is rejected without forced checkout");
        Check(await Git(root, "rev-parse", "HEAD") == after, "invalid recovery authority leaves current source intact");
        await Git(root, "update-ref", completed.RecoveryRef, before);
        await coordinator.RollbackAsync(config, default, Valid);

        await Reject(() => coordinator.UpdateAsync(config, release, runtime, snapshot, (_, _, _) =>
        {
            File.WriteAllText(modelPaths, "concurrent user config change");
            throw new IOException("injected failure while user changed config");
        }, Valid, default, validateRecovery: Valid), "automatic recovery never adopts concurrent config edits as authorized installer writes");
        Check(coordinator.Previous(config)!.Status == "rollback-failed" && File.ReadAllText(modelPaths) == "concurrent user config change"
            && await Git(root, "rev-parse", "HEAD") == after, "unexpected config bytes survive a failed automatic restore without a forced source checkout");
        File.WriteAllText(modelPaths, "original model paths");
        await coordinator.RollbackAsync(config, default, Valid);
        completed = await coordinator.UpdateAsync(config, release, runtime, snapshot, NoInstall, Valid, default);
        await coordinator.RollbackAsync(config, default);
        Check(coordinator.Previous(config) is { Status: "needs-repair", CoreVerified: false },
            "source-only rollback without a validator cannot claim dependencies or core startup were verified");
        await coordinator.CompleteDependencyRepairAsync(config, Valid, default);
        Check(coordinator.Previous(config) is { Status: "rolled-back", CoreVerified: true }, "repair success records explicit core validation evidence");

        completed = await coordinator.UpdateAsync(config, release, runtime, snapshot, NoInstall, Valid, default);
        coordinator.Progress = phase => { if (phase == "restoring-configuration") File.WriteAllText(modelPaths, "user edit during source checkout"); };
        await Reject(() => coordinator.RollbackAsync(config, default, Valid), "configuration is rechecked after source checkout before restoring files");
        Check(await Git(root, "rev-parse", "HEAD") == before && File.ReadAllText(modelPaths) == "user edit during source checkout"
            && coordinator.Previous(config)!.Status == "rollback-failed", "an edit during switching is retained even after source recovery succeeds");
        File.WriteAllText(modelPaths, "original model paths");
        await Coordinator().RollbackAsync(config, default, Valid);

        passed += await ComfyUpdateInterruptionTests.Run(state, runtime, git, source, config, before, after);

        // Build an actual historical on-disk backup fixture. New UpdateAsync has no
        // complete-backup option; backward compatibility is exercised only on reads.
        File.WriteAllText(package, "legacy old dependency");
        var legacy = await LegacyBackup(state, config, git, before, after);
        await Git(root, "checkout", "--detach", after);
        File.WriteAllText(package, "legacy failed new dependency");
        File.WriteAllText(settings, "legacy migrated settings");
        File.WriteAllBytes(database, [9, 8, 7]);
        File.WriteAllText(modelPaths, "legacy migrated model paths");
        File.WriteAllText(Path.Combine(root, "config.json"), "new legacy config");
        await Coordinator().RollbackAsync(config, default);
        Check(await Git(root, "rev-parse", "HEAD") == before && File.ReadAllText(package) == "legacy old dependency"
            && File.ReadAllText(settings) == "original settings" && File.ReadAllBytes(database).SequenceEqual(new byte[] { 0, 1, 2, 3 })
            && File.ReadAllText(modelPaths) == "original model paths" && !File.Exists(Path.Combine(root, "config.json")),
            "historical backups without new fields still restore complete Python, Git, config and user database bytes");
        JsonFile.Write(pointer, legacy with { Status = "restoring" });
        Directory.Move(Path.Combine(root, ".git"), Path.Combine(root, ".anima-legacy-interrupted-git"));
        Directory.Move(Path.Combine(root, ".venv"), Path.Combine(root, ".anima-legacy-interrupted-python"));
        await Coordinator().RollbackAsync(config, default);
        Check(await Git(root, "rev-parse", "HEAD") == before && File.ReadAllText(package) == "legacy old dependency"
            && coordinator.Previous(config)!.Status == "rolled-back", "historical recovery resumes when interrupted swaps left Git and Python absent");

        var portableRoot = Path.Combine(state, "light-portable"); Directory.CreateDirectory(portableRoot);
        var portableComfy = Path.Combine(portableRoot, "ComfyUI");
        await Git(portableRoot, "clone", source, portableComfy); await Git(portableComfy, "checkout", "--detach", before);
        var pythonRoot = Path.Combine(portableRoot, "python_embeded"); Directory.CreateDirectory(pythonRoot);
        File.WriteAllText(Path.Combine(pythonRoot, "python.exe"), "fixture Python, never executed");
        File.WriteAllText(Path.Combine(pythonRoot, "python311._pth"), ".\nimport site\n");
        File.WriteAllText(Path.Combine(pythonRoot, "package.txt"), "portable old package");
        var portableConfig = config.Clone(); portableConfig.ComfyRoot = portableComfy; portableConfig.Python = Path.Combine(pythonRoot, "python.exe");
        portableConfig.RuntimeRoot = Path.Combine(state, "light-portable-runtime");
        var portableBackup = await Coordinator().UpdateAsync(portableConfig, release, runtime, snapshot, (_, _, _) =>
        { File.WriteAllText(Path.Combine(pythonRoot, "package.txt"), "portable new package"); return Task.CompletedTask; }, Valid, default);
        await Coordinator().RollbackAsync(portableConfig, default, Valid);
        Check(await Git(portableComfy, "rev-parse", "HEAD") == before && File.ReadAllText(Path.Combine(pythonRoot, "package.txt")) == "portable new package"
            && !Directory.Exists(Path.Combine(portableBackup.Directory, "venv")), "portable Python supports lightweight source recovery without copying its environment");
        Directory.Move(Path.Combine(root, ".venv"), Path.Combine(root, "venv"));
        var alternate = config.Clone(); alternate.Python = Path.Combine(root, "venv", "Scripts", "python.exe");
        Check(ComfyUpdateCoordinator.IndependentPythonDirectory(alternate) == Path.Combine(root, "venv"), "independent venv layout remains supported");
        return passed;
    }

    private static string Pointer(string state, LauncherConfig config) => Path.Combine(state, "comfy-updates",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(config.ComfyRoot)).ToUpperInvariant()))).ToLowerInvariant() + ".json");

    private static async Task<ComfyUpdateBackup> LegacyBackup(string state, LauncherConfig config, string git, string before, string after)
    {
        var root = config.ComfyRoot;
        var backupRoot = Path.Combine(config.RuntimeRoot, "comfy-backups", "historical-fixture");
        Directory.CreateDirectory(backupRoot);
        ComfyUpdateCoordinator.CopyDirectory(Path.Combine(root, ".git"), Path.Combine(backupRoot, "git"), default);
        ComfyUpdateCoordinator.CopyDirectory(Path.Combine(root, ".venv"), Path.Combine(backupRoot, "venv"), default);
        async Task<string[]> Files(string commit) => (await Commands.Run(git, ["ls-tree", "-r", "--name-only", "-z", commit], root, null, default)).TrimEnd('\r', '\n').Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var files = await Files(before); var target = await Files(after);
        foreach (var relative in files)
        {
            var destination = Path.Combine(backupRoot, "source", relative); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(root, relative), destination);
        }
        var configurations = ComfyUpdateCoordinator.ConfigurationPaths.Select(relative =>
        {
            var path = Path.Combine(root, relative); var isDirectory = Directory.Exists(path);
            var item = new ComfyConfigurationEntry(relative, isDirectory || File.Exists(path), isDirectory);
            if (isDirectory) ComfyUpdateCoordinator.CopyDirectory(path, Path.Combine(backupRoot, "configuration", relative), default);
            else if (item.Existed) { Directory.CreateDirectory(Path.Combine(backupRoot, "configuration")); File.Copy(path, Path.Combine(backupRoot, "configuration", relative)); }
            return item;
        }).ToArray();
        var backup = new ComfyUpdateBackup(root, config.Python, before, after, backupRoot, files, target, "complete", DateTimeOffset.UtcNow)
        {
            PythonDirectory = Path.Combine(root, ".venv"), ConfigurationJson = JsonSerializer.Serialize(config, JsonFile.Options), ConfigurationPaths = configurations,
            ConstraintsFile = ComfyUpdateCoordinator.ReadOptional(Path.Combine(root, ".anima-constraints.txt")),
            RevisionMarker = ComfyUpdateCoordinator.ReadOptional(Path.Combine(root, ".anima-revision.json")),
        };
        var json = JsonSerializer.SerializeToNode(backup, JsonFile.Options)!.AsObject();
        foreach (var field in new[] { "formatVersion", "recoveryStrategy", "previousBranch", "recoveryRef", "phase", "dependenciesMayHaveChanged", "environmentPackages", "appliedConfigurationHashes", "recoveryError", "coreVerified" }) json.Remove(field);
        Directory.CreateDirectory(Path.GetDirectoryName(Pointer(state, config))!);
        File.WriteAllText(Pointer(state, config), json.ToJsonString(JsonFile.Options));
        File.WriteAllText(Path.Combine(backupRoot, "backup.json"), json.ToJsonString(JsonFile.Options));
        return backup;
    }
}
