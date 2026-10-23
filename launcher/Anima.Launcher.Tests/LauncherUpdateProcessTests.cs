using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Anima.Launcher.Core;

/// <summary>Real coordinator/process handoff tests; all executables, state and services are isolated fixtures.</summary>
internal static class LauncherUpdateProcessTests
{
    private const string Marker = ".launcher-update-test-fixture";

    public static async Task<bool> TryHandleFixture(string[] args)
    {
        if (args.Contains("--package-info") && File.Exists(Path.Combine(AppContext.BaseDirectory, Marker)))
        {
            var metadata = JsonFile.Read<JsonElement>(Path.Combine(AppContext.BaseDirectory, "fixture-build-info.json"));
            var report = args[Array.IndexOf(args, "--report") + 1];
            JsonFile.Write(report, new LauncherPackageInfo(1, metadata.GetProperty("version").GetString()!, "single-exe-v1",
                Environment.ProcessPath!, AppContext.BaseDirectory, metadata, []));
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "fixture-package-inspected.txt"), "true");
            return true;
        }
        if (!args.Contains("--update-candidate") && !args.Contains("--recover-update")) return false;
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, Marker))) return false;
        var state = Environment.GetEnvironmentVariable("ANIMA_LAUNCHER_HOME") ?? throw new IOException("Fixture state is missing.");
        var store = new LauncherUpdateStore(state);
        var recovery = args.Contains("--recover-update");
        string Arg(string name) => args[Array.IndexOf(args, name) + 1];
        var transaction = store.ReadTransaction(Arg(recovery ? "--recover-update" : "--update-candidate"), Arg("--update-token"));
        if (recovery)
        {
            File.WriteAllText(Path.Combine(state, "recovery-observed.txt"), Environment.ProcessPath);
            return true;
        }
        var mode = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, Marker)).Trim();
        if (mode == "crash") { Environment.ExitCode = 23; return true; }
        using var exit = new EventWaitHandle(false, EventResetMode.AutoReset, LauncherUpdateCoordinator.ExitEventName(transaction));
        File.WriteAllText(Path.Combine(state, "candidate-observed.txt"), Environment.ProcessId.ToString());
        if (mode != "wait")
        {
            var next = transaction.PreviousConfig.Clone();
            next.AppRoot = Path.Combine(state, "versions", "candidate-workbench");
            next.PinnedAppRoot = "";
            Directory.CreateDirectory(next.AppRoot);
            File.WriteAllText(Path.Combine(next.AppRoot, "run.py"), "# isolated updater fixture\n");
            JsonFile.Write(Path.Combine(state, "launcher.json"), next);
            JsonFile.Write(store.ReceiptPath(transaction.Id),
                new UpdateReady(transaction.Token, mode != "reject", mode == "reject" ? "fixture refused startup" : null, next.AppRoot, Environment.ProcessId));
        }
        await Task.Run(() => exit.WaitOne(TimeSpan.FromSeconds(45)));
        return true;
    }

    public static async Task<int> Run(string work, RuntimeManifest manifest)
    {
        if (!OperatingSystem.IsWindows()) return 0;
        var root = Path.Combine(work, "update-processes");
        Directory.CreateDirectory(root);
        var passed = 0;
        void Check(bool ok, string message)
        {
            if (!ok) throw new Exception("FAIL: " + message);
            passed++;
            Console.WriteLine("PASS: " + message);
        }

        foreach (var mode in new[] { "success", "reject", "crash" })
        {
            var fixture = await Prepare(root, mode);
            try
            {
                var outcome = await Task.Run(() => new LauncherUpdateCoordinator(fixture.Updates).Run(fixture.Transaction.Id, fixture.Transaction.Token));
                var state = fixture.Updates.Store.Load();
                var transaction = fixture.Updates.Store.ReadTransaction(fixture.Transaction.Id);
                if (mode == "success")
                {
                    Check(outcome == 0 && state.Active == fixture.Transaction.Candidate && state.PendingTransactionId is null,
                        "coordinator commits a verified candidate subprocess only after its ready receipt");
                    Check(state.Previous == fixture.Transaction.Previous && transaction.Phase == "committed",
                        "coordinator retains the previous executable and finalizes the committed transaction");
                    var config = JsonFile.Read<LauncherConfig>(Path.Combine(fixture.State, "launcher.json"));
                    Check(config.DataDir == fixture.Transaction.PreviousConfig.DataDir && config.PinnedAppRoot == ""
                        && config.AppRoot.EndsWith("candidate-workbench"), "candidate commit preserves data paths and adopts the new bundle");
                    // A crash after the authoritative state write must not trigger rollback.
                    transaction.Phase = "candidate";
                    fixture.Updates.Store.WriteTransaction(transaction);
                    var replay = new LauncherUpdateCoordinator(fixture.Updates).Run(transaction.Id, transaction.Token, recoverOnly: true);
                    Check(replay == 0 && fixture.Updates.Store.ReadTransaction(transaction.Id).Phase == "committed"
                        && fixture.Updates.Store.Load().Active == fixture.Transaction.Candidate,
                        "reopening a transaction after its commit repairs the receipt without rolling back");
                }
                else
                {
                    Check(outcome == 1 && state.Active == fixture.Transaction.Previous && state.PendingTransactionId is null,
                        $"{mode} candidate restores the previous executable without leaving a pending transaction");
                    Check(File.ReadAllText(Path.Combine(fixture.State, "launcher.json")) == fixture.Transaction.PreviousConfigText,
                        $"{mode} rollback restores the complete original configuration bytes");
                    Check(state.Failed.Any(x => x.Sha256 == fixture.Transaction.Candidate.Sha256 && x.Version == fixture.Transaction.Candidate.Version),
                        $"{mode} candidate is recorded so normal entries cannot repeat the failed upgrade");
                    await WaitFor(() => File.Exists(Path.Combine(fixture.State, "recovery-observed.txt")));
                    Check(File.ReadAllText(Path.Combine(fixture.State, "recovery-observed.txt")) == fixture.Transaction.Previous.Executable,
                        $"{mode} rollback launches the immutable previous executable");
                }
            }
            finally { await StopCandidate(fixture); fixture.Updates.Dispose(); }
        }

        var interrupted = await Prepare(root, "wait");
        try
        {
            using var child = Process.Start(interrupted.Updates.ProcessInfo(interrupted.Transaction.Candidate.Executable, "--update-candidate", interrupted.Transaction))!;
            interrupted.Transaction.CandidateProcess = LauncherUpdateStore.Identity(child);
            interrupted.Transaction.Phase = "candidate";
            interrupted.Updates.Store.WriteTransaction(interrupted.Transaction);
            await WaitFor(() => File.Exists(Path.Combine(interrupted.State, "candidate-observed.txt")));
            var outcome = await Task.Run(() => new LauncherUpdateCoordinator(interrupted.Updates).Run(interrupted.Transaction.Id, interrupted.Transaction.Token, recoverOnly: true));
            Check(outcome == 1 && child.HasExited && interrupted.Updates.Store.Load().PendingTransactionId is null,
                "interrupted update cooperatively closes its candidate before restoring the old pointer");
            await WaitFor(() => File.Exists(Path.Combine(interrupted.State, "recovery-observed.txt")));
            Check(File.ReadAllText(Path.Combine(interrupted.State, "launcher.json")) == interrupted.Transaction.PreviousConfigText,
                "interrupted update preserves the original configuration and starts recovery");
        }
        finally { await StopCandidate(interrupted); interrupted.Updates.Dispose(); }

        // The actual new WebUI can precede launcher.json persistence. Recovery
        // must inspect the live app directory and respect a 409 even in that gap.
        var unsaved = await Prepare(root, "unsaved-busy", candidateMode: "crash");
        var serviceConfig = unsaved.Transaction.PreviousConfig.Clone();
        serviceConfig.AppRoot = Path.Combine(unsaved.State, "versions", "candidate-workbench");
        using var service = await StartBusyWorkbench(unsaved.Transaction.Previous.Executable, serviceConfig);
        try
        {
            unsaved.Transaction.PreviousConfig.WebPort = serviceConfig.WebPort;
            JsonFile.Write(Path.Combine(unsaved.State, "launcher.json"), unsaved.Transaction.PreviousConfig);
            unsaved.Transaction.PreviousConfigText = File.ReadAllText(Path.Combine(unsaved.State, "launcher.json"));
            unsaved.Transaction.WorkbenchWasRunning = true;
            unsaved.Updates.Store.WriteTransaction(unsaved.Transaction);
            var identity = LauncherUpdateStore.Identity(service) with { Role = "WebUI", ShutdownToken = "fixture-idle-token" };
            JsonFile.Write(Path.Combine(unsaved.State, "processes.json"), new[] { identity });
            var outcome = await Task.Run(() => new LauncherUpdateCoordinator(unsaved.Updates).Run(unsaved.Transaction.Id, unsaved.Transaction.Token));
            Check(outcome == 1 && !service.HasExited && unsaved.Updates.Store.ReadTransaction(unsaved.Transaction.Id).Phase == "recovery-required",
                "rollback detects an unsaved candidate workbench and preserves it when its idle guard refuses");
            Check(unsaved.Updates.Store.Load().PendingTransactionId == unsaved.Transaction.Id
                && File.ReadAllText(Path.Combine(unsaved.State, "launcher.json")) == unsaved.Transaction.PreviousConfigText,
                "busy recovery retains its transaction and leaves configuration untouched");
            await EndFixture(service);
            var retry = await Task.Run(() => new LauncherUpdateCoordinator(unsaved.Updates).Run(unsaved.Transaction.Id, unsaved.Transaction.Token, recoverOnly: true));
            Check(retry == 1 && unsaved.Updates.Store.Load().PendingTransactionId is null
                && unsaved.Updates.Store.ReadTransaction(unsaved.Transaction.Id).Phase == "failed",
                "recovery can complete later after the protected workbench has stopped");
        }
        finally { await EndFixture(service); await StopCandidate(unsaved); unsaved.Updates.Dispose(); }
        return passed + await LauncherUpdateDownloadTests.Run(work) + await SoftwareUpdateServiceTests.Run(work, manifest);
    }

    private sealed record Fixture(string State, LauncherUpdates Updates, UpdateTransaction Transaction);

    private static async Task<Fixture> Prepare(string root, string name, string? candidateMode = null)
    {
        var state = Path.Combine(root, name, "state");
        var updates = new LauncherUpdates(state, Environment.ProcessPath!, "1.0.0");
        var old = await CopyPackage(updates, "1.0.0", "success");
        var next = await CopyPackage(updates, "1.0.1", candidateMode ?? name);
        var previous = new LauncherConfig
        {
            AppRoot = Path.Combine(state, "versions", "previous-workbench"), DataDir = Path.Combine(state, "retained-user-data"),
            PinnedAppRoot = Path.Combine(state, "versions", "previous-workbench"), ComfyRoot = Path.Combine(state, "ComfyUI"),
            Python = Path.Combine(state, "missing-python.exe"), SetupComplete = true, StartOnLaunch = true,
        };
        var freePort = new TcpListener(IPAddress.Loopback, 0);
        freePort.Start(); previous.WebPort = ((IPEndPoint)freePort.LocalEndpoint).Port; freePort.Stop();
        Directory.CreateDirectory(previous.AppRoot);
        File.WriteAllText(Path.Combine(previous.AppRoot, "run.py"), "# retained old workbench\n");
        JsonFile.Write(Path.Combine(state, "launcher.json"), previous);
        var transaction = new UpdateTransaction
        {
            Id = Guid.NewGuid().ToString("N"), Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            Previous = old, Candidate = next, PreviousConfig = previous, PreviousConfigText = File.ReadAllText(Path.Combine(state, "launcher.json")),
            WorkbenchWasRunning = false,
        };
        updates.Store.WriteTransaction(transaction);
        updates.Store.Save(new LauncherUpdateState { Active = old, PendingTransactionId = transaction.Id });
        return new(state, updates, transaction);
    }

    private static async Task<InstalledLauncher> CopyPackage(LauncherUpdates updates, string version, string mode)
    {
        var assembly = Assembly.GetEntryAssembly()!.Location;
        var source = Path.ChangeExtension(assembly, ".exe");
        var info = await updates.IdentifyAsync(source, version, default);
        var executable = updates.Store.CandidatePath(version, info.Sha256);
        var destination = Path.GetDirectoryName(executable)!;
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(assembly)!))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        File.Copy(source, executable, true);
        File.WriteAllText(Path.Combine(destination, Marker), mode);
        return info with { Executable = executable };
    }

    private static async Task<Process> StartBusyWorkbench(string executable, LauncherConfig config)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "--http-fixture", config.AppRoot, config.DataDir, config.ComfyUrl, "busy" }) info.ArgumentList.Add(arg);
        var process = Process.Start(info)!;
        try
        {
            var port = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            config.WebPort = int.Parse(port ?? throw new IOException("Fixture did not report its port."));
            return process;
        }
        catch { await EndFixture(process); process.Dispose(); throw; }
    }

    private static async Task WaitFor(Func<bool> ready)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!ready()) await Task.Delay(50, deadline.Token);
    }

    private static async Task StopCandidate(Fixture fixture)
    {
        var transaction = fixture.Updates.Store.ReadTransaction(fixture.Transaction.Id);
        if (!LauncherUpdateStore.IsRunning(transaction.CandidateProcess)) return;
        if (OperatingSystem.IsWindows() && EventWaitHandle.TryOpenExisting(LauncherUpdateCoordinator.ExitEventName(transaction), out var signal)) { using (signal) signal.Set(); }
        using var process = Process.GetProcessById(transaction.CandidateProcess!.Pid);
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { await EndFixture(process); }
    }

    private static async Task EndFixture(Process process)
    {
        if (!process.HasExited) process.Kill(true);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }
}
