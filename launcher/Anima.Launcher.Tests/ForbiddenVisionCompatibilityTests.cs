using System.Net;
using System.Net.Sockets;
using Anima.Launcher.Core;

internal static class ForbiddenVisionCompatibilityTests
{
    internal static void WriteSource(string root, string version = "original")
    {
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "__init__.py"), """
import os
def initialize_forbidden_vision():
    manager = ForbiddenVisionModelManager.get_instance()
    validation_status = manager.validate_model_availability()
    if not all(validation_status.values()):
        manager.initialize_default_models()
        validation_status = manager.validate_model_availability()
    return validation_status

""" + "# " + version + "\n");
        File.WriteAllText(Path.Combine(root, "src", "model_manager.py"), """
import os
class ForbiddenVisionModelManager:
    MODELS_CONFIG = {'face_detect': {'filename': 'face.pt'}}
    def _download_model(self, model_key):
        config = self.MODELS_CONFIG[model_key]
        return hf_hub_download(model_key)

""" + "# " + version + "\n");
    }

    public static async Task<int> Run(string work, RuntimeManifest runtime)
    {
        var count = 0;
        void Check(bool result, string label)
        { if (!result) throw new Exception("FAIL: " + label); count++; Console.WriteLine("PASS: " + label); }
        async Task Reject(Func<Task> action, string label)
        {
            try { await action(); }
            catch (InvalidOperationException) { Check(true, label); return; }
            catch (IOException) { Check(true, label); return; }
            throw new Exception("Expected rejection: " + label);
        }
        var git = Commands.Find("git.exe") ?? Commands.Find("git") ?? throw new Exception("Git required");
        var python = Commands.Find("python.exe") ?? Commands.Find("python") ?? throw new Exception("Python required");
        async Task<string> Git(string root, params string[] args) => (await Commands.Run(git, args, root, null, default)).Trim();
        async Task Init(string root)
        {
            Directory.CreateDirectory(root);
            await Git(root, "init", "-b", "main");
            await Git(root, "config", "user.name", "Forbidden Vision fixture");
            await Git(root, "config", "user.email", "fixture@example.invalid");
            await Git(root, "config", "core.autocrlf", "false");
        }
        async Task<string> Commit(string root, string label)
        { await Git(root, "add", "."); await Git(root, "commit", "-m", label); return await Git(root, "rev-parse", "HEAD"); }
        var directory = Path.Combine(work, "forbidden-compatibility");
        var source = Path.Combine(directory, "source");
        await Init(source);
        WriteSource(source);
        var before = await Commit(source, "original source");
        WriteSource(source, "compatible upstream update");
        var target = await Commit(source, "compatible updated source");
        WriteSource(source, "unsupported API");
        var managerSource = Path.Combine(source, "src", "model_manager.py");
        File.WriteAllText(managerSource, File.ReadAllText(managerSource).Replace("self, model_key", "self, model_key, token"));
        var unsupported = await Commit(source, "unsupported API");

        var comfy = Path.Combine(directory, "ComfyUI");
        await Init(comfy);
        File.WriteAllText(Path.Combine(comfy, "main.py"), "# fixture\n");
        File.WriteAllText(Path.Combine(comfy, "requirements.txt"), "");
        File.WriteAllText(Path.Combine(comfy, "manager_requirements.txt"), "");
        File.WriteAllText(Path.Combine(comfy, ".gitignore"), ".venv/\ncustom_nodes/\n.anima-*\nuser/\n");
        var core = await Commit(comfy, "fixed core");
        await Commands.Run(python, ["-m", "venv", "--without-pip", Path.Combine(comfy, ".venv")], directory, null, default);
        var custom = Path.Combine(comfy, "custom_nodes");
        Directory.CreateDirectory(custom);
        var node = Path.Combine(custom, ForbiddenVisionCompatibility.NodeId);
        var declared = runtime.Nodes.Single(item => item.Id == ForbiddenVisionCompatibility.NodeId);
        await Git(custom, "clone", "--no-hardlinks", source, node);
        await Git(node, "checkout", "--detach", before);
        await Git(node, "remote", "set-url", "origin", declared.Url);
        using var first = new TcpListener(IPAddress.Loopback, 0);
        using var second = new TcpListener(IPAddress.Loopback, 0);
        first.Start(); second.Start();
        var config = new LauncherConfig
        {
            ComfyRoot = comfy, Python = Path.Combine(comfy, ".venv", "Scripts", "python.exe"), RuntimeRoot = Path.Combine(directory, "runtime"),
            ComfyPort = ((IPEndPoint)first.LocalEndpoint).Port, WebPort = ((IPEndPoint)second.LocalEndpoint).Port
        };
        first.Stop(); second.Stop();
        var state = Path.Combine(directory, "state");
        await ForbiddenVisionCompatibility.CheckOrApply(config, state, node, true, default);
        File.WriteAllText(Path.Combine(node, "user-settings.json"), "preserved user settings");
        var oldReceipt = File.ReadAllText(Path.Combine(node, ".anima-managed-models-v1", "receipt.json"));
        var oldSource = File.ReadAllText(Path.Combine(node, "__init__.py"));
        var resolved = target;
        var coordinator = new NodeUpdateCoordinator(state, git, null,
            (repository, _) => Task.FromResult(new NodeRelease(repository.Id, repository.Url, "main", resolved)),
            async (entry, token) => { await Commands.Run(git, ["fetch", source, entry.TargetSha], entry.Directory, null, token); },
            (_, snapshot, files, map, _) => Task.FromResult(snapshot with { Resolved = true, PythonIdentity = "fixture", Constraints = [],
                NodeRequirementsIdentity = ComfyDependencyResolver.NodeRequirementsIdentity(files, runtime.Sam2Commit, map) }));
        var manifest = runtime with { Nodes = [declared] };
        var plan = await coordinator.CheckAsync(config, manifest, default);
        Check(plan.Entries.Single().CanUpdate, "managed Forbidden Vision changes remain eligible for upstream updates");
        plan = await coordinator.PrepareAsync(config, plan, manifest, default);
        var staged = plan.Entries.Single().StagedDirectory;
        await ForbiddenVisionCompatibility.CheckOrApply(config, state, staged, false, default);
        var newReceipt = File.ReadAllText(Path.Combine(staged, ".anima-managed-models-v1", "receipt.json"));
        Check(newReceipt != oldReceipt, "new upstream source receives its own validated compatibility receipt before mutation");
        async Task CheckRestored()
        {
            await ForbiddenVisionCompatibility.CheckOrApply(config, state, node, false, default);
            Check(await Git(node, "rev-parse", "HEAD") == before && File.ReadAllText(Path.Combine(node, "__init__.py")) == oldSource
                && File.ReadAllText(Path.Combine(node, ".anima-managed-models-v1", "receipt.json")) == oldReceipt,
                "rollback restores original commit, patched source, and original compatibility receipt together");
        }
        await Reject(() => coordinator.ApplyAsync(config, plan, (_, _, _) => Task.CompletedTask,
            (_, _) => throw new InvalidOperationException("injected health failure"), default), "health failure rolls back a prepared Forbidden Vision update");
        await CheckRestored();
        var backup = await coordinator.ApplyAsync(config, plan, (_, _, _) => Task.CompletedTask, (_, _) => Task.CompletedTask, default);
        Check(backup.Status == "complete" && await Git(node, "rev-parse", "HEAD") == target
            && File.ReadAllText(Path.Combine(node, ".anima-managed-models-v1", "receipt.json")) == newReceipt
            && File.ReadAllText(Path.Combine(node, "user-settings.json")) == "preserved user settings",
            "updating replaces only validated managed receipts and preserves user files without collisions");
        await coordinator.RollbackAsync(config, default);
        await CheckRestored();
        resolved = unsupported;
        var incompatiblePlan = await coordinator.CheckAsync(config, manifest, default);
        await Reject(() => coordinator.PrepareAsync(config, incompatiblePlan, manifest, default),
            "unsupported future source contract is rejected in staging before active node or Python changes");
        await CheckRestored();
        Check(await Git(comfy, "rev-parse", "HEAD") == core, "compatibility updates and rollback preserve ComfyUI itself");
        Check(!ForbiddenVisionCompatibility.IsManagedArtifact(".anima-managed-models-v1/user-note.txt"), "unrecognized files beside receipts remain user-owned");
        return count;
    }
}
