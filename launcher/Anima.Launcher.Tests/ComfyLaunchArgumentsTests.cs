using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Anima.Launcher.Core;

internal static class ComfyLaunchArgumentsTests
{
    public static async Task<int> Run(string work, RuntimeManifest manifest)
    {
        var count = 0;
        void Check(bool success, string label)
        {
            if (!success) throw new Exception("FAIL: " + label);
            count++; Console.WriteLine("PASS: " + label);
        }
        void Reject(Action action, string label)
        {
            try { action(); }
            catch (InvalidDataException) { Check(true, label); return; }
            throw new Exception("Expected invalid arguments: " + label);
        }
        JsonElement Stats(IEnumerable<string> argv) => JsonSerializer.SerializeToElement(new { system = new { argv } });
        var config = new LauncherConfig { ComfyRoot = Path.Combine(work, "comfy 参数"), Python = @"C:\python\python.exe", ComfyArguments = ["--lowvram", "--reserve-vram", "1.50", "--enable-manager"] };
        var original = config.ComfyArguments.ToArray();
        var built = ComfyLaunchArguments.Build(config);
        Check(built[0] == Path.Combine(config.ComfyRoot, "main.py") && built.Contains("--disable-auto-launch") && built[^1] == "--enable-manager" && config.ComfyArguments.SequenceEqual(original), "launch builder includes managed arguments without mutating saved options");
        Check(ComfyLaunchArguments.Compare(Stats(built), config).Matches, "identical live arguments can be reused");
        Check(ComfyLaunchArguments.Compare(Stats([built[0], "--enable-manager", "--reserve-vram=1.5", "--lowvram"]), config).Matches, "common option order, equals syntax, numeric formatting and default listener/port are equivalent");
        var changed = config.Clone(); changed.ComfyArguments = ["--cpu", "--reserve-vram", "1.5", "--enable-manager"];
        var mismatch = ComfyLaunchArguments.Compare(Stats(built), changed);
        Check(mismatch.RootMatches && !mismatch.Matches && mismatch.Detail.Contains("需要重启") && mismatch.Detail.Contains("期望") && mismatch.Detail.Contains("实际"), "changed runtime options produce an actionable restart mismatch");
        var defaults = config.Clone(); defaults.ComfyArguments = [];
        Check(!ComfyLaunchArguments.Compare(Stats([built[0], "--cpu"]), defaults).Matches, "an unexpected actual CPU option cannot be reused when settings are empty");
        Check(!ComfyLaunchArguments.Compare(Stats([Path.Combine(work, "other", "main.py")]), defaults).RootMatches, "a different root remains a port identity conflict");
        Check(!ComfyLaunchArguments.Compare(Stats(["main.py"]), defaults).RootMatches, "an ambiguous relative entrypoint cannot authorize process reuse");
        Check(!ServiceManager.SameRoot(Stats(["other.py", "--input", built[0]]), config.ComfyRoot), "a matching main.py used as an unrelated argument is not a process identity");
        Check(!ComfyLaunchArguments.Compare(JsonSerializer.SerializeToElement(new { system = new { argv = new object[] { built[0], 123 } } }), defaults).Matches, "malformed runtime arguments are not reusable");
        Check(!ComfyLaunchArguments.Compare(Stats([built[0], "--listen", "0.0.0.0"]), defaults).Matches, "listening on a different address requires restart");
        var portable = defaults.Clone(); portable.Python = @"C:\ComfyUI\python_embeded\python.exe";
        Check(ComfyLaunchArguments.Build(portable).Contains("--windows-standalone-build") && !ComfyLaunchArguments.Compare(Stats([built[0]]), portable).Matches, "portable runtime flag is constructed and checked consistently");
        var unknown = defaults.Clone(); unknown.ComfyArguments = ["--future-option", "value with space", "--another-future", "one", "two"];
        Check(ComfyLaunchArguments.Compare(Stats(ComfyLaunchArguments.Build(unknown)), unknown).Matches, "unknown custom options and all their values are preserved for matching");
        Check(!ComfyLaunchArguments.Compare(Stats([built[0], "--another-future", "one", "two", "--future-option", "value with space"]), unknown).Matches, "unknown option ordering is conservatively preserved");
        var legacyManager = defaults.Clone(); legacyManager.ComfyArguments = ["--enable-manager-legacy-ui"];
        Check(ComfyLaunchArguments.ManagerEnabled(legacyManager.ComfyArguments) && ComfyLaunchArguments.Compare(Stats([built[0], "--enable-manager", "--enable-manager-legacy-ui"]), legacyManager).Matches, "legacy Manager UI implies Manager enablement without a false mismatch");

        string[] roundTrip = ["--future-path", @"C:\folder with space\", "--future-value", "a\"b", @"C:\literal\path", "中文值", "--literal", "$(not-a-shell) & ignored"];
        Check(ComfyLaunchArguments.Parse(ComfyLaunchArguments.Format(roundTrip)).SequenceEqual(roundTrip), "advanced argument quoting round-trips Windows paths, quotes, Unicode and shell metacharacters without execution");
        Reject(() => ComfyLaunchArguments.Parse("--path \"unfinished"), "unterminated quoted input is rejected");
        foreach (var args in new string[][] { ["--cpu", "--lowvram"], ["--force-fp32", "--force-fp16"], ["--use-pytorch-cross-attention", "--use-sage-attention"], ["--cpu", "--cpu"], ["--enable-manager=true"], ["--reserve-vram"], ["--reserve-vram", "-1"], ["--reserve-vram", "NaN"], ["--reserve-vram", "Infinity"], ["--reserve-vram", "1", "2"], ["--disable-manager-ui", "--enable-manager-legacy-ui"], ["--port=18888"], ["--models-directory", @"C:\other"], ["--listen", "0.0.0.0"], ["--future", "bad\nvalue"] })
            Reject(() => ComfyLaunchArguments.Validate(args), "invalid common or managed arguments rejected: " + ComfyLaunchArguments.Format(args));
        ComfyLaunchArguments.Validate(["--reserve-vram", "0", "--future-option", "a"]);
        Check(true, "zero reserved VRAM and unknown custom options remain valid");
        var common = new ComfyLaunchOptions("--novram", 0.75, true, "--force-fp16", "--use-pytorch-cross-attention", "--enable-manager");
        var combined = ComfyLaunchArguments.WithOptions(common, ["--future-option", "value with spaces"]);
        var read = ComfyLaunchArguments.ReadOptions(combined);
        Check(read.Options == common && read.Advanced.SequenceEqual(["--future-option", "value with spaces"]), "common settings and advanced options round-trip through the existing argument list");
        var flagValue = ComfyLaunchArguments.ReadOptions(["--future-option=--literal-value", "--empty="]);
        Check(flagValue.Advanced.SequenceEqual(["--future-option=--literal-value", "--empty="]), "advanced values resembling flags or empty values retain equals syntax");
        Reject(() => ComfyLaunchArguments.WithOptions(common, ["--cpu"]), "advanced options cannot silently override a selected common mode");
        var savedPath = Path.Combine(work, "launch-arguments", "launcher.json");
        config.ComfyArguments = combined;
        JsonFile.Write(savedPath, config);
        Check(JsonFile.Read<LauncherConfig>(savedPath).ComfyArguments.SequenceEqual(combined), "configured arguments persist in the compatible launcher JSON list");
        Check(JsonSerializer.Deserialize<LauncherConfig>("{}", JsonFile.Options)!.ComfyArguments.Count == 0, "legacy configuration without arguments retains automatic defaults");
        var explicitlyDisabled = new LauncherConfig { LaunchArgumentsConfigured = true, ComfyArguments = [] };
        var restoredChoice = JsonSerializer.Deserialize<LauncherConfig>(JsonSerializer.Serialize(explicitlyDisabled, JsonFile.Options), JsonFile.Options)!;
        Check(restoredChoice.LaunchArgumentsConfigured && restoredChoice.ComfyArguments.Count == 0
            && !JsonSerializer.Deserialize<LauncherConfig>("{}", JsonFile.Options)!.LaunchArgumentsConfigured,
            "an explicitly saved empty argument list survives reload while legacy configurations retain unset initialization intent");
        var oldRoot = Path.Combine(work, "older-comfy-parser");
        Directory.CreateDirectory(Path.Combine(oldRoot, "comfy"));
        File.WriteAllText(Path.Combine(oldRoot, "comfy", "cli_args.py"), "parser.add_argument('--cpu', action='store_true')\nparser.add_argument(\"--reserve-vram\", type=float)\n");
        Check(ComfyLaunchArguments.SupportedOptions(oldRoot)!.SetEquals(["--cpu", "--reserve-vram"]), "installed version support is detected from its parser without executing Python");
        Reject(() => ComfyLaunchArguments.ValidateForRoot(["--enable-manager"], oldRoot), "Manager cannot be enabled for an installed version without that option");
        Reject(() => ComfyLaunchArguments.ValidateForRoot(["--use-sage-attention"], oldRoot), "saving a common option unsupported by the target version is rejected");
        var retained = new LauncherConfig { ComfyRoot = oldRoot, ComfyArguments = ["--enable-manager"] };
        retained.Validate(validateArguments: false);
        Check(retained.ComfyRoot == oldRoot && retained.ComfyArguments.SequenceEqual(["--enable-manager"]), "loading preserves unsupported editable arguments instead of discarding the environment configuration");
        Reject(() => ComfyLaunchArguments.Build(retained), "retained unsupported arguments still cannot launch the old target version");
        ComfyLaunchArguments.ValidateForRoot(["--cpu", "--future-plugin-option"], oldRoot);
        Check(ComfyLaunchArguments.SupportedOptions(Path.Combine(work, "not-installed")) is null, "uninstalled environments allow the default catalog and custom advanced flags are preserved");
        var identity = new OwnedProcess(123, 456, defaults.Python, "ComfyUI");
        Check(ServiceManager.ComfyProcessIdentityMatches(defaults, identity, identity), "reuse identity accepts an unchanged selected Python process");
        Check(!ServiceManager.ComfyProcessIdentityMatches(defaults, identity, identity with { StartTimeUtcTicks = 789 }), "a recycled listener PID cannot inherit a previous Python identity");
        Check(!ServiceManager.ComfyProcessIdentityMatches(defaults, identity, null) && !ServiceManager.ComfyProcessIdentityMatches(defaults, null, identity), "an unreadable listener identity cannot authorize reuse");

        // A real HTTP listener proves StartComfy rejects before reading nodes or launching a replacement.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        defaults.ComfyPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        defaults.WebPort = defaults.ComfyPort == 8190 ? 8191 : 8190;
        var requests = new List<string>();
        var serving = Task.Run(async () =>
        {
            try
            {
                while (!cancel.IsCancellationRequested)
                {
                    using var socket = await listener.AcceptTcpClientAsync(cancel.Token);
                    using var stream = socket.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                    var request = await reader.ReadLineAsync(cancel.Token);
                    if (request is null) continue; // PortOpen reachability connection.
                    lock (requests) requests.Add(request);
                    while (await reader.ReadLineAsync(cancel.Token) is { Length: > 0 }) { }
                    var bytes = request.Contains("/object_info")
                        ? JsonSerializer.SerializeToUtf8Bytes(manifest.Comfy.RequiredClasses.Concat(manifest.Nodes.SelectMany(node => node.RequiredClasses))
                            .Concat(["UNETLoader", "CLIPLoader", "VAELoader", "KSampler", "KSamplerAdvanced", "VAEDecodeTiled", "EmptyLatentImage"])
                            .Distinct().ToDictionary(name => name, _ => new { }))
                        : JsonSerializer.SerializeToUtf8Bytes(new { system = new { argv = new[] { built[0], "--port", defaults.ComfyPort.ToString(), "--cpu" } } });
                    var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header, cancel.Token); await stream.WriteAsync(bytes, cancel.Token);
                }
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
            catch (SocketException) when (cancel.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (cancel.IsCancellationRequested) { }
        }, cancel.Token);
        try
        {
            var state = Path.Combine(work, "argument-mismatch-process");
            var manager = new ServiceManager(state, _ => { });
            try { await ServiceManager.CheckComfyReuseAsync(defaults, cancel.Token); throw new Exception("Mismatched preflight was accepted."); }
            catch (ComfyArgumentsMismatchException)
            {
                Check(!Directory.Exists(state) && await ServiceManager.PortOpen(defaults.ComfyPort, cancel.Token), "preflight rejects changed arguments before any related service or ownership state is changed");
            }
            lock (requests) requests.Clear();
            try { await manager.StartComfy(defaults, manifest, cancel.Token); throw new Exception("Mismatched process was reused."); }
            catch (ComfyArgumentsMismatchException)
            {
                Check(await ServiceManager.PortOpen(defaults.ComfyPort, cancel.Token) && !manager.IsOwnedRunning("ComfyUI"), "mismatched external service remains alive and never becomes launcher-owned");
                lock (requests) Check(requests.Count == 1 && requests[0].Contains("/system_stats"), "mismatch fails before node inspection or a replacement launch");
            }
            defaults.ComfyArguments = ["--cpu"];
            try { await manager.StartComfy(defaults, manifest, cancel.Token); throw new Exception("A matching HTTP response from another executable was reused."); }
            catch (ComfyArgumentsMismatchException error)
            {
                Check(error.Message.Contains("Python") && await ServiceManager.PortOpen(defaults.ComfyPort, cancel.Token)
                    && !manager.IsOwnedRunning("ComfyUI"), "matching reported arguments from the wrong executable are rejected without stopping or adopting it");
            }
        }
        finally { cancel.Cancel(); listener.Stop(); await serving; }

        var repository = File.Exists(Path.Combine(Environment.CurrentDirectory, "launcher", "manifests", "launcher-manifest.json"))
            ? Environment.CurrentDirectory : Directory.GetParent(work)!.Parent!.Parent!.FullName;
        var python = Path.Combine(repository, ".venv", "Scripts", "python.exe");
        if (!File.Exists(python)) python = Commands.Find("python.exe") ?? throw new Exception("A real Python is required for the process identity fixture.");
        var pythonRoot = Path.Combine(work, "python-identity-fixture");
        Directory.CreateDirectory(pythonRoot);
        var entrypoint = Path.Combine(pythonRoot, "main.py");
        File.WriteAllText(Path.Combine(pythonRoot, "nodes.json"), JsonSerializer.Serialize(manifest.Comfy.RequiredClasses.Concat(manifest.Nodes.SelectMany(node => node.RequiredClasses))
            .Concat(["UNETLoader", "CLIPLoader", "VAELoader", "KSampler", "KSamplerAdvanced", "VAEDecodeTiled", "EmptyLatentImage"])
            .Distinct().ToDictionary(name => name, _ => new { })));
        File.WriteAllText(entrypoint, """
import http.server, json, pathlib, sys, os
class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args): pass
    def do_GET(self):
        body = pathlib.Path(__file__).with_name('nodes.json').read_bytes() if self.path == '/object_info' else json.dumps({'system': {'argv': sys.argv}}).encode()
        self.send_response(200)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)
server = http.server.HTTPServer(('127.0.0.1', int(sys.argv[sys.argv.index('--port') + 1])), Handler)
print('ready', flush=True)
print(json.dumps({'pid': os.getpid(), 'python': sys.executable}), flush=True)
server.serve_forever()
""");
        var freePort = new TcpListener(IPAddress.Loopback, 0);
        freePort.Start();
        var pythonPort = ((IPEndPoint)freePort.LocalEndpoint).Port;
        freePort.Stop();
        var pythonConfig = defaults.Clone();
        pythonConfig.ComfyRoot = pythonRoot; pythonConfig.Python = python; pythonConfig.ComfyPort = pythonPort;
        pythonConfig.WebPort = pythonPort == 8190 ? 8191 : 8190;
        var secondVenv = Path.Combine(work, "another-venv");
        await Commands.Run(python, ["-m", "venv", "--without-pip", secondVenv], pythonRoot, null, default, timeout: TimeSpan.FromSeconds(30));
        using (var realPython = Process.Start(Commands.Info(python, new[] { "-s", "-u", "-B" }.Concat(ComfyLaunchArguments.Build(pythonConfig)), pythonRoot))!)
        {
            try
            {
                Check(await realPython.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)) == "ready", "real Python HTTP fixture starts with normal -s -u -B interpreter options");
                using var pythonDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                using var pythonIdentity = JsonDocument.Parse((await realPython.StandardOutput.ReadLineAsync())!);
                var serverPid = pythonIdentity.RootElement.GetProperty("pid").GetInt32();
                var inspection = await ServiceManager.InspectComfyReuseAsync(pythonConfig, pythonDeadline.Token);
                if (inspection is not { Matches: true }) Console.WriteLine("Python identity diagnostic: " + inspection?.Detail);
                Check(inspection is { Matches: true, RootMatches: true }, "service inspection confirms selected Python plus real sys.argv and root");
                using var childProcess = Process.GetProcessById(serverPid);
                var childIdentity = new OwnedProcess(serverPid, childProcess.StartTime.ToUniversalTime().Ticks, childProcess.MainModule!.FileName!, "ComfyUI");
                var launcherIdentity = new OwnedProcess(realPython.Id, realPython.StartTime.ToUniversalTime().Ticks, realPython.MainModule!.FileName!, "ComfyUI launcher");
                if (!childIdentity.Executable.Equals(python, StringComparison.OrdinalIgnoreCase))
                {
                    Check(!ServiceManager.ComfyProcessIdentityMatches(pythonConfig, childIdentity, childIdentity, launcherIdentity,
                        launcherIdentity with { StartTimeUtcTicks = launcherIdentity.StartTimeUtcTicks + 1 }), "a recycled venv launcher parent cannot authorize its old listener child");
                    Check(!ServiceManager.ComfyProcessIdentityMatches(pythonConfig, childIdentity, childIdentity), "a base Python listener without its proven venv parent cannot be reused as that venv");
                }
                var pythonManager = new ServiceManager(Path.Combine(work, "python-reuse-state"), _ => { });
                await pythonManager.StartComfy(pythonConfig, manifest, pythonDeadline.Token);
                Check(!realPython.HasExited && !pythonManager.IsOwnedRunning("ComfyUI"), "matching real Python is reused without claiming ownership");
                var differentPython = pythonConfig.Clone(); differentPython.Python = Path.Combine(secondVenv, "Scripts", "python.exe");
                var changedPython = await ServiceManager.InspectComfyReuseAsync(differentPython, pythonDeadline.Token);
                Check(changedPython is { RootMatches: true, Matches: false } && changedPython.Detail.Contains("Python") && !realPython.HasExited,
                    "two real venvs sharing a base Python remain distinct despite matching root and argv");
                try { await pythonManager.StartComfy(differentPython, manifest, pythonDeadline.Token); throw new Exception("Another Python environment was reused."); }
                catch (ComfyArgumentsMismatchException) { Check(!realPython.HasExited, "start rejects an interpreter mismatch as restart rather than an auto-recoverable port conflict"); }
            }
            finally
            {
                if (!realPython.HasExited) realPython.Kill(true); // Only this test-created fixture.
                await realPython.WaitForExitAsync();
            }
        }

        var ownedManager = new ServiceManager(Path.Combine(work, "owned-python-fixture"), _ => { });
        try
        {
            using var ownedDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            await ownedManager.StartComfy(pythonConfig, manifest, ownedDeadline.Token);
            Check(ownedManager.IsOwnedRunning("ComfyUI") && await ServiceManager.CheckComfyReuseAsync(pythonConfig, ownedDeadline.Token),
                "a newly started managed Python is verified by the same listener identity and argv policy");
        }
        finally { await ownedManager.StopOwnedAsync("ComfyUI"); }

        var exitingListener = new TcpListener(IPAddress.Loopback, 0);
        exitingListener.Start();
        defaults.ComfyPort = ((IPEndPoint)exitingListener.LocalEndpoint).Port;
        defaults.WebPort = defaults.ComfyPort == 8190 ? 8191 : 8190;
        using var exitingDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var exiting = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var socket = await exitingListener.AcceptTcpClientAsync(exitingDeadline.Token);
                    using var stream = socket.GetStream();
                    using var reader = new StreamReader(stream);
                    if (await reader.ReadLineAsync(exitingDeadline.Token) is null) continue;
                    exitingListener.Stop(); // External fixture exits during the HTTP health probe.
                    return;
                }
            }
            finally { exitingListener.Stop(); }
        }, exitingDeadline.Token);
        try
        {
            Check(!await ServiceManager.CheckComfyReuseAsync(defaults, exitingDeadline.Token), "an external process exiting between TCP and HTTP probes is treated as stopped without a false port conflict");
            await exiting;
        }
        finally { exitingDeadline.Cancel(); exitingListener.Stop(); }
        return count;
    }
}
