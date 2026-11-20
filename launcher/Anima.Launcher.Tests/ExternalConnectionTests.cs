using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Anima.Launcher.Core;

internal static class ExternalConnectionTests
{
    public static async Task<int> Run(string repository, string work, RuntimeManifest manifest)
    {
        var count = 0;
        void Check(bool value, string label)
        {
            if (!value) throw new Exception(label);
            Console.WriteLine("PASS: " + label);
            count++;
        }
        foreach (var (type, name, expected) in new[] { ("cuda", "NVIDIA RTX", "CUDA"), ("cuda", "AMD Radeon", "ROCm"),
            ("hip", "device", "ROCm"), ("xpu", "Intel Arc", "XPU"), ("cpu", "cpu", "CPU") })
            Check(ExternalWorkbench.Backend(JsonSerializer.SerializeToElement(new { devices = new[] { new { type, name } } })) == expected,
                "running backend: " + expected);
        var root = Path.Combine(work, "external");
        var state = Path.Combine(work, "launcher");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(state);
        var config = new LauncherConfig { ComfyRoot = root, AppRoot = repository, RuntimeRoot = state,
            Python = Path.Combine(repository, ".venv", "Scripts", "python.exe"), ExternalEnvironmentMode = "readOnly" };
        var nested = config.Clone();
        nested.DataDir = Path.Combine(root, "history-data");
        try
        {
            await ExternalWorkbench.ReviewConnectionAsync(nested, manifest, state, default);
            throw new Exception("Read-only connection accepted a history database inside the external environment");
        }
        catch (InvalidDataException ex) when (ex.Message.Contains(nested.DataDir))
        { Check(!Directory.Exists(nested.DataDir), "overlapping workbench storage is rejected before starting services or writing history"); }
        ExternalWorkbench.ValidateDataDirectory(new LauncherConfig { ExternalEnvironmentMode = "readOnly", ComfyRoot = root, DataDir = root + "-data" });
        var alias = Path.Combine(work, "external-test-alias");
        LauncherCacheTests.CreateDirectoryLink(alias, root);
        nested.DataDir = Path.Combine(alias, "new", "data");
        try { ExternalWorkbench.ValidateDataDirectory(nested); throw new Exception("linked external history storage was accepted"); }
        catch (InvalidDataException) { Check(!Directory.Exists(nested.DataDir), "read-only connection also protects linked parent directories"); }
        var artifact = new InstallationDownload(Path.Combine(work, "progress.bin"), 1, new string('a', 64), true);
        var plan = new InstallationPlan(JsonSerializer.Serialize(config, JsonFile.Options), root, state, "anima", true, 0, 0, 0, [])
            { Downloads = [artifact] };
        var activeFile = Path.Combine(state, "launcher.json");
        var original = config.Clone();
        original.ComfyRoot = Path.Combine(work, "previous-environment");
        original.SetupComplete = true;
        JsonFile.Write(activeFile, original);
        var originalBytes = File.ReadAllBytes(activeFile);
        var reviewedConnection = plan with { Fresh = false, Purpose = "external-connection" };
        var onboarding = new SetupRecord { Step = SetupStep.Installing, Fresh = false, InstallStarted = true,
            Draft = config.Clone(), ReviewedPlan = reviewedConnection };
        Check(SetupRouting.ResolveForRuntime(original, onboarding, manifest, new(1, []), new(1, "anima", []), resume: true).Step == SetupStep.Installing
            && SetupRouting.Resolve(original, onboarding) is null, "reopening onboarding restores the pending connection while normal startup keeps the old environment");
        var done = new SetupRecord { Step = SetupStep.Finished, Fresh = false, InstallStarted = true, InstallFinished = true, Draft = original.Clone() };
        Check(SetupRouting.ResolveForRuntime(original, done, manifest, new(1, []), new(1, "anima", []), resume: true).Step is null,
            "completed connections retain normal overview routing");
        try { await ExternalWorkbench.VerifyAndSaveConnectionAsync(config, activeFile, _ => Task.FromResult(new SetupRuntimeReadinessReport([new("workbench", "failure")], [])), default); }
        catch (IOException) { }
        Check(originalBytes.SequenceEqual(File.ReadAllBytes(activeFile)), "a failed workbench check leaves the previous launcher configuration byte-for-byte intact");
        using (var canceled = new CancellationTokenSource())
        {
            try { await ExternalWorkbench.VerifyAndSaveConnectionAsync(config, activeFile, _ => { canceled.Cancel(); return Task.FromResult(new SetupRuntimeReadinessReport([], [])); }, canceled.Token); }
            catch (OperationCanceledException) { }
            Check(originalBytes.SequenceEqual(File.ReadAllBytes(activeFile)), "a connection canceled after service startup preserves the previous binding");
        }
        await ExternalWorkbench.VerifyAndSaveConnectionAsync(config, activeFile, _ =>
        {
            Check(originalBytes.SequenceEqual(File.ReadAllBytes(activeFile)), "previous binding stays active throughout independent workbench verification");
            return Task.FromResult(new SetupRuntimeReadinessReport([], []));
        }, default);
        Check(JsonFile.Read<LauncherConfig>(activeFile).ComfyRoot == config.ComfyRoot
            && ExternalWorkbench.ResourceBindingsFile(state, config) != ExternalWorkbench.ResourceBindingsFile(state, original),
            "successful connection atomically selects its verified environment with distinct runtime bindings");
        var repairTarget = config.Clone();
        repairTarget.ExternalEnvironmentMode = "repair";
        JsonFile.Write(activeFile, original);
        var transaction = ExternalRepairBinding.Begin(original, repairTarget) ?? throw new Exception("Expected staged repair binding");
        ExternalRepairBinding.SaveActive(activeFile, repairTarget, transaction);
        Check(originalBytes.SequenceEqual(File.ReadAllBytes(activeFile)), "repair stage checkpoints do not publish a different environment's configuration");
        try { transaction.Publish(activeFile, repairTarget, new([new("workbench", "failure")], []), default); } catch (InvalidOperationException) { }
        Check(!transaction.Published && transaction.Previous.ComfyRoot == original.ComfyRoot && originalBytes.SequenceEqual(File.ReadAllBytes(activeFile)),
            "failed repair health validation preserves both the previous in-memory binding and saved configuration");
        var repairDraft = new SetupRecord { Step = SetupStep.Installing, Fresh = false, InstallStarted = true, InstallFinished = true, ModelsPending = true,
            Draft = repairTarget, ReviewedPlan = reviewedConnection with { Purpose = "environment", ConfigurationJson = JsonSerializer.Serialize(repairTarget, JsonFile.Options) } };
        Check(SetupRouting.ResolveForRuntime(original, repairDraft, manifest, new(1, []), new(1, "anima", []), resume: true).Step == SetupStep.Installing
            && SetupRouting.Resolve(original, repairDraft) is null, "an interrupted post-install repair resumes explicitly while normal launch retains the active environment");
        transaction.Publish(activeFile, repairTarget, new([], []), default);
        Check(transaction.Published && JsonFile.Read<LauncherConfig>(activeFile).ExternalEnvironmentMode == "repair"
            && ExternalRepairBinding.Begin(repairTarget, repairTarget.Clone()) is null, "a verified repair commits once and reopening the same repaired binding needs no switch");
        async Task RequiresWrite(Func<Task> action, string label)
        {
            try { await action(); }
            catch (InvalidOperationException ex) when (ex.Message == LauncherConfig.ReadOnlyMaintenanceNotice) { Check(true, label); return; }
            throw new Exception("Read-only operation was accepted: " + label);
        }
        using (var transfers = new Downloads())
        {
            var installer = new Installer(state, manifest, transfers, _ => {}, null);
            var core = new ComfyUpdateCoordinator(state, "unavailable-git");
            var nodes = new NodeUpdateCoordinator(state, "unavailable-git");
            var nodePlan = new NodeUpdatePlan(root, config.Python, "", [], null, DateTimeOffset.UtcNow);
            await RequiresWrite(() => nodes.PrepareAsync(config, nodePlan, manifest, default), "read-only node preparation is blocked before accessing repositories");
            await RequiresWrite(() => nodes.ApplyAsync(config, nodePlan, (_, _, _) => Task.CompletedTask, (_, _) => Task.CompletedTask, default), "read-only node update is blocked for a restored plan");
            await RequiresWrite(() => nodes.RollbackAsync(config, default), "read-only node rollback cannot restore an earlier writable backup");
            await RequiresWrite(() => core.UpdateAsync(config, new("", "", null, ""), manifest, new("", "", "", []), (_, _, _) => Task.CompletedTask, (_, _) => Task.CompletedTask, default), "read-only ComfyUI update is blocked before fetching the target");
            await RequiresWrite(() => core.RollbackAsync(config, default), "read-only ComfyUI rollback cannot apply a historical backup");
            await RequiresWrite(() => installer.RepairDependencies(config, null, default), "read-only package maintenance is blocked");
            await RequiresWrite(() => installer.RepairOptionalDependencies(config, default), "read-only optional acceleration maintenance is blocked");
            Check(!Directory.EnumerateFileSystemEntries(root).Any(), "refused maintenance leaves the external root untouched");
            await using var service = new DownloadTaskService(transfers, state);
            var first = service.RegisterOwner("setup", "test install", config, payload: JsonSerializer.SerializeToElement(plan, JsonFile.Options));
            for (int i = 0; i < 5; i++)
            {
                File.WriteAllBytes(artifact.Destination, [1]);
                var again = service.RegisterOwner("setup", "test install", config, payload: JsonSerializer.SerializeToElement(plan, JsonFile.Options));
                Check(again.Id == first.Id && service.Owners.Count == 1, "partial disk progress preserves install owner " + i);
                File.Delete(artifact.Destination);
            }
            var changed = plan with { NodeVersions = [new("a", "https://github.com/example/a.git", "main", new string('b', 40))] };
            var other = service.RegisterOwner("setup", "new review", config, payload: JsonSerializer.SerializeToElement(changed, JsonFile.Options));
            Check(other.Id != first.Id, "different frozen node identities remain separate");
        }
        if (!OperatingSystem.IsWindows()) return count;
        if (!File.Exists(config.Python)) config.Python = Commands.Find("python.exe") ?? throw new FileNotFoundException("Python fixture unavailable");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        config.ComfyPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        const string server = """
import sys, json
from http.server import HTTPServer, BaseHTTPRequestHandler
class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        replies = {
            '/system_stats': {'system': {'argv': sys.argv}, 'devices': [{'type':'cpu','name':'cpu'}]},
            '/object_info': {'KSampler': {}},
            '/internal/folder_paths': {'loras': [__file__]},
        }
        self.send_response(200)
        self.end_headers()
        self.wfile.write(json.dumps(replies.get(self.path, {})).encode())
HTTPServer(('127.0.0.1', int(sys.argv[1])), Handler).serve_forever()
""";
        var main = Path.Combine(root, "main.py");
        File.WriteAllText(main, server);
        File.WriteAllText(Path.Combine(root, "folder_paths.py"), "");
        string[] Snapshot() => Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).Order()
            .Select(p => p + ":" + File.GetLastWriteTimeUtc(p).Ticks).ToArray();
        var before = Snapshot();
        var info = new ProcessStartInfo(config.Python) { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-B", main, config.ComfyPort.ToString() }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            while (!await ServiceManager.PortOpen(config.ComfyPort, deadline.Token)) await Task.Delay(50, deadline.Token);
            var reviewed = await ExternalWorkbench.ReviewConnectionAsync(config, manifest, state, deadline.Token);
            var catalog = await TaggerRuntime.Probe(config, "invalid-script-must-not-run.py", deadline.Token);
            Check(catalog.Paths["loras"].Single() == main, "read-only model inventory uses the verified running server without importing local Python modules");
            Check(reviewed.Purpose == "external-connection" && reviewed.ModelIds.Length == 0 && reviewed.DependencyReviewIssues.Any(s => s.Contains("CPU")),
                "read-only review checks a live external interpreter with partial nodes");
            var services = new ServiceManager(state, _ => {});
            await services.StartComfy(config, manifest, deadline.Token);
            services.StopOwned();
            Check(!process.HasExited && Snapshot().SequenceEqual(before), "external process survives stop and external directory is unchanged");
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        return count;
    }
}
