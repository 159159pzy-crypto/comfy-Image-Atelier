using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Anima.Launcher.Core;

internal static class ReleaseRecoveryTests
{
    private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    public static async Task<int> Run(string work, RuntimeManifest manifest)
    {
        var failures = new List<string>();
        var passed = 0;
        void Check(bool success, string label)
        {
            if (success) { passed++; Console.WriteLine("PASS: " + label); }
            else { failures.Add(label); Console.WriteLine("FAIL: " + label); }
        }
        async Task Reject(Func<Task> action, string label)
        {
            try { await action(); Check(false, label); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException) { Check(true, label); }
        }

        var recovery = Path.Combine(work, "recovery");
        Directory.CreateDirectory(recovery);
        var refusedNetwork = new FakeHandler { Callback = _ => throw new Exception("This recovery fixture must not contact a network.") };
        using var offline = new Downloads(handler: refusedNetwork);
        var installer = new Installer(recovery, manifest, offline, _ => { }, null);
        var wrongTarget = Path.Combine(recovery, "wrong-revision");
        var wrongStage = wrongTarget + ".anima-installing";
        Directory.CreateDirectory(wrongStage);
        JsonFile.Write(Path.Combine(wrongStage, ".anima-revision.json"), manifest.Comfy with { Id = "another-repository", Commit = new string('0', 40) });
        await Reject(() => Clone(installer, manifest.Comfy, wrongTarget), "finalized clone with another repository/commit is refused");
        Check(Directory.Exists(wrongStage) && !Directory.Exists(wrongTarget), "refused finalized clone is preserved without promotion");

        var git = Commands.Find("git.exe") ?? Commands.Find("git") ?? throw new IOException("Git is required for isolated clone recovery tests.");
        typeof(Installer).GetField("git", PrivateInstance)!.SetValue(installer, git);
        var seed = Path.Combine(recovery, "git-seed");
        Directory.CreateDirectory(seed);
        async Task<string> Git(string directory, params string[] argv) =>
            (await Commands.Run(git, argv, directory, null, default, timeout: TimeSpan.FromSeconds(15))).Trim();
        async Task Commit(string directory) => await Git(directory, "-c", "user.name=Anima fixture", "-c", "user.email=fixture@example.invalid", "commit", "-m", "Fixture commit");
        await Git(seed, "init");
        File.WriteAllText(Path.Combine(seed, "tracked.txt"), "first revision\n");
        await Git(seed, "add", "tracked.txt");
        await Commit(seed);
        var firstCommit = await Git(seed, "rev-parse", "HEAD");
        File.WriteAllText(Path.Combine(seed, "tracked.txt"), "second revision\n");
        await Git(seed, "add", "tracked.txt");
        await Commit(seed);
        var secondCommit = await Git(seed, "rev-parse", "HEAD");
        var fixtureRepo = new Repository("fixture", "https://example.com/fixture.git", firstCommit, []);
        async Task<(string Target, string Stage)> Finalized(string name, Repository? repo = null)
        {
            repo ??= fixtureRepo;
            var target = Path.Combine(recovery, name);
            var stage = target + ".anima-installing";
            await Git(recovery, "clone", "--no-hardlinks", seed, stage);
            await Git(stage, "checkout", "--detach", repo.Commit);
            JsonFile.Write(Path.Combine(stage, ".anima-revision.json"), repo);
            return (target, stage);
        }
        var valid = await Finalized("valid-finalized");
        await Clone(installer, fixtureRepo, valid.Target);
        Check(!Directory.Exists(valid.Stage) && await Git(valid.Target, "rev-parse", "HEAD") == firstCommit,
            "validated finalized clone is promoted without fetching or losing its pinned commit");
        var wrongHead = await Finalized("wrong-head");
        await Git(wrongHead.Stage, "checkout", "--detach", secondCommit);
        await Reject(() => Clone(installer, fixtureRepo, wrongHead.Target), "a finalized clone with a mismatched Git HEAD is refused");
        Check(await Git(wrongHead.Stage, "rev-parse", "HEAD") == secondCommit, "refused Git HEAD remains unchanged");
        var dirty = await Finalized("dirty-finalized");
        File.WriteAllText(Path.Combine(dirty.Stage, "tracked.txt"), "user edits must survive\n");
        await Reject(() => Clone(installer, fixtureRepo, dirty.Target), "a finalized clone with edited tracked files is refused");
        Check(File.ReadAllText(Path.Combine(dirty.Stage, "tracked.txt")).Contains("user edits"), "clone rejection preserves user source changes");
        var noGit = Path.Combine(recovery, "missing-git");
        JsonFile.Write(Path.Combine(noGit + ".anima-installing", ".anima-revision.json"), fixtureRepo);
        await Reject(() => Clone(installer, fixtureRepo, noGit), "finalized marker without an independent Git checkout is refused");
        var cancelled = await Finalized("cancelled-finalized");
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            try { await Clone(installer, fixtureRepo, cancelled.Target, cancellation.Token); Check(false, "cancelled clone never promotes its staged directory"); }
            catch (OperationCanceledException) { Check(Directory.Exists(cancelled.Stage) && !Directory.Exists(cancelled.Target), "cancelled clone never promotes its staged directory"); }
        }
        File.WriteAllText(Path.Combine(seed, ".gitmodules"), "[submodule \"fixture-child\"]\n\tpath = fixture-child\n\turl = https://example.com/child.git\n");
        await Git(seed, "add", ".gitmodules");
        await Git(seed, "update-index", "--add", "--cacheinfo", "160000," + firstCommit + ",fixture-child");
        await Commit(seed);
        var submoduleRepo = fixtureRepo with { Commit = await Git(seed, "rev-parse", "HEAD") };
        var missingSubmodule = await Finalized("missing-submodule", submoduleRepo);
        await Reject(() => Clone(installer, submoduleRepo, missingSubmodule.Target), "a finalized clone with an uninitialized submodule is refused");
        var dirtySubmodule = await Finalized("dirty-submodule", submoduleRepo);
        var child = Path.Combine(dirtySubmodule.Stage, "fixture-child");
        await Git(dirtySubmodule.Stage, "clone", "--no-hardlinks", seed, child);
        await Git(child, "checkout", "--detach", firstCommit);
        File.WriteAllText(Path.Combine(child, "tracked.txt"), "edited child source\n");
        await Reject(() => Clone(installer, submoduleRepo, dirtySubmodule.Target), "a finalized clone with tracked submodule edits is refused");

        var toolRuntime = Path.Combine(recovery, "tools-runtime");
        Directory.CreateDirectory(Path.Combine(toolRuntime, "cache"));
        var zipPath = Path.Combine(toolRuntime, "cache", "fixture-tool.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "cmd/tool.exe", "runtime/required.dll" })
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write("complete fixture " + name);
            }
        }
        var artifact = new Artifact("fixture-tool", "https://example.com/tool.zip", new FileInfo(zipPath).Length,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zipPath))).ToLowerInvariant(), "cmd/tool.exe");
        var toolManifest = manifest with { Tools = [artifact] };
        var toolTarget = Path.Combine(toolRuntime, "tools", artifact.Id, artifact.Sha256[..12]);
        Directory.CreateDirectory(Path.Combine(toolTarget, "cmd"));
        File.WriteAllText(Path.Combine(toolTarget, "cmd", "tool.exe"), "interrupted extraction");
        var toolConfig = new LauncherConfig { RuntimeRoot = toolRuntime, ComfyRoot = Path.Combine(recovery, "unused-comfy") };
        var toolInstaller = new Installer(recovery, toolManifest, offline, _ => { }, null);
        var resolved = await Tool(toolInstaller, artifact.Id, toolConfig);
        Check(File.Exists(Path.Combine(toolTarget, "runtime", "required.dll")) && File.ReadAllText(resolved) == "complete fixture cmd/tool.exe",
            "an executable left by interrupted extraction is recovered from the verified cache");
        Check(refusedNetwork.Seen.Count == 0, "tool recovery reuses its verified archive without downloading");
        Check(ToolInstallation.IsReady(toolRuntime, artifact), "complete tool receipt verifies all extracted files");
        Check(Directory.GetDirectories(Path.GetDirectoryName(toolTarget)!, artifact.Sha256[..12] + ".incomplete-*").Length == 1,
            "tool recovery preserves the previous incomplete installation");
        var dependency = Path.Combine(toolTarget, "runtime", "required.dll");
        var originalDependency = File.ReadAllBytes(dependency);
        var changedDependency = originalDependency.ToArray(); changedDependency[0] ^= 1;
        File.WriteAllBytes(dependency, changedDependency);
        Check(!ToolInstallation.IsReady(toolRuntime, artifact), "same-size corruption in a tool dependency invalidates readiness");
        var unfinishedPlan = InstallationPlanner.Create(toolConfig, toolManifest with { Nodes = [], TorchWheels = [] }, recovery, true);
        Check(unfinishedPlan.Downloads.Any(entry => entry.Destination == zipPath), "planner budgets a corrupt tool instead of trusting its executable");
        await Tool(toolInstaller, artifact.Id, toolConfig);
        Check(File.ReadAllBytes(dependency).SequenceEqual(originalDependency), "tool retry repairs a corrupt dependency from its verified cached archive");
        File.Delete(dependency);
        Check(!ToolInstallation.IsReady(toolRuntime, artifact), "missing tool dependencies invalidate readiness");
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            try { await ToolInstallation.InstallVerifiedArchive(toolRuntime, artifact, zipPath, cancellation.Token); Check(false, "cancelled tool repair cannot publish a completion receipt"); }
            catch (OperationCanceledException) { Check(!File.Exists(dependency), "cancelled tool repair cannot publish a completion receipt"); }
        }
        await Tool(toolInstaller, artifact.Id, toolConfig);
        var finishedPlan = InstallationPlanner.Create(toolConfig, toolManifest with { Nodes = [], TorchWheels = [] }, recovery, true);
        Check(finishedPlan.Downloads.Length == 0 && ToolInstallation.IsReady(toolRuntime, artifact), "planner and installer agree after complete tool recovery");

        // Both endpoints and processes are fixtures created by this test. No ComfyUI,
        // launcher configuration or pre-existing local service participates.
        var servicesState = Path.Combine(recovery, "service-state");
        var versions = Path.Combine(servicesState, "versions");
        var configA = new LauncherConfig { AppRoot = Path.Combine(versions, "a"), DataDir = Path.Combine(servicesState, "data-a"), ComfyPort = 18181 };
        var configB = new LauncherConfig { AppRoot = Path.Combine(versions, "b"), DataDir = Path.Combine(servicesState, "data-b"), ComfyPort = 18182 };
        using var endpointA = await StartFixture(configA);
        using var endpointB = await StartFixture(configB);
        try
        {
            JsonFile.Write(Path.Combine(servicesState, "processes.json"), new[] { Record(endpointA), Record(endpointB) });
            var manager = new ServiceManager(servicesState, _ => { });
            manager.RecoverOwned();
            var stopped = await manager.TryStopWebUiAsync(configB, versions);
            Check(stopped && endpointB.HasExited, "version transition stops the endpoint selected by its actual port");
            Check(!endpointA.HasExited && await ServiceManager.PortOpen(configA.WebPort), "version transition preserves another owned workbench on a different port");
            Check(JsonFile.Read<OwnedProcess[]>(Path.Combine(servicesState, "processes.json")).Select(p => p.Pid).SequenceEqual([endpointA.Id]),
                "targeted shutdown retains other environments' persisted ownership records");
            foreach (var wrongContext in new[]
            {
                new LauncherConfig { AppRoot = configA.AppRoot, DataDir = configB.DataDir, ComfyPort = configA.ComfyPort, WebPort = configA.WebPort },
                new LauncherConfig { AppRoot = configB.AppRoot, DataDir = configA.DataDir, ComfyPort = configA.ComfyPort, WebPort = configA.WebPort },
                new LauncherConfig { AppRoot = configA.AppRoot, DataDir = configA.DataDir, ComfyPort = configB.ComfyPort, WebPort = configA.WebPort },
            })
                Check(!await manager.TryStopWebUiAsync(wrongContext, versions) && !endpointA.HasExited,
                    "different app/data/backend context never authorizes an owned process shutdown");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try { await manager.TryStopWebUiAsync(configA, versions, cancellation.Token); Check(false, "cancelled endpoint shutdown leaves owned process running"); }
            catch (OperationCanceledException) { Check(!endpointA.HasExited, "cancelled endpoint shutdown leaves owned process running"); }
            var current = Record(endpointA);
            Check(!ServiceManager.SameProcessIdentity(current with { StartTimeUtcTicks = current.StartTimeUtcTicks - 1 }, current)
                && !ServiceManager.SameProcessIdentity(current with { Executable = "another.exe" }, current), "a recycled PID or changed executable cannot inherit process identity");
            var externalConfig = new LauncherConfig { AppRoot = configA.AppRoot, DataDir = configA.DataDir, ComfyPort = configA.ComfyPort };
            using var external = await StartFixture(externalConfig);
            try
            {
                Check(!await manager.TryStopWebUiAsync(externalConfig, versions) && !external.HasExited && !endpointA.HasExited,
                    "an unowned endpoint cannot redirect shutdown to another owned process");
            }
            finally { await EndFixture(external); }
        }
        finally { await EndFixture(endpointA); await EndFixture(endpointB); }

        if (failures.Count > 0) throw new Exception($"{failures.Count} release recovery regression(s): " + string.Join("; ", failures));
        return passed;
    }

    private static Task Clone(Installer installer, Repository repository, string target, CancellationToken ct = default) =>
        (Task)typeof(Installer).GetMethod("Clone", PrivateInstance)!.Invoke(installer,
            [repository, target, new LauncherConfig { ComfyRoot = target }, ct])!;
    private static Task<string> Tool(Installer installer, string id, LauncherConfig config) =>
        (Task<string>)typeof(Installer).GetMethod("Tool", PrivateInstance)!.Invoke(installer, [id, config, CancellationToken.None])!;
    private static OwnedProcess Record(Process process) => new(process.Id, process.StartTime.ToUniversalTime().Ticks, process.MainModule!.FileName!, "WebUI");

    private static async Task<Process> StartFixture(LauncherConfig config)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        foreach (var argument in new[] { "--http-fixture", config.AppRoot, config.DataDir, config.ComfyUrl }) info.ArgumentList.Add(argument);
        var process = Process.Start(info)!;
        try
        {
            var text = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (!int.TryParse(text, out var port)) throw new IOException("Fixture did not publish a port: " + text);
            config.WebPort = port;
            return process;
        }
        catch { await EndFixture(process); process.Dispose(); throw; }
    }

    private static async Task EndFixture(Process process)
    {
        if (!process.HasExited) process.Kill(true);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    public static async Task ServeFixture(string[] args)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Console.WriteLine(((IPEndPoint)listener.LocalEndpoint).Port);
        Console.Out.Flush();
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            var request = await reader.ReadLineAsync();
            if (request is null) continue; // PortOpen connects without sending HTTP.
            while (await reader.ReadLineAsync() is { Length: > 0 }) { }
            var payload = JsonSerializer.Serialize(new { app = ServiceManager.WorkbenchAppName, app_dir = args[1], data_dir = args[2], comfy_url = args[3] });
            var bytes = Encoding.UTF8.GetBytes(payload);
            var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers);
            await stream.WriteAsync(bytes);
        }
    }
}
