using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Anima.Launcher.Core;

internal static class ComfyUpdateProbeTests
{
    public static async Task<int> Run(string work)
    {
        var passed = 0;
        void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); passed++; Console.WriteLine("PASS: " + label); }
        void Reject(Action action, string label)
        {
            try { action(); } catch (InvalidDataException) { Check(true, label); return; }
            throw new Exception("Expected rejection: " + label);
        }
        var root = Path.Combine(work, "core update probe 中文");
        var source = Path.Combine(root, "ComfyUI");
        var state = Path.Combine(root, "state");
        Directory.CreateDirectory(Path.Combine(source, "comfy"));
        string[] options = ["--listen", "--port", "--disable-auto-launch", "--base-directory", "--user-directory", "--input-directory", "--output-directory", "--temp-directory", "--front-end-root", "--disable-all-custom-nodes",
            "--models-directory", "--previews-directory", "--database-url", "--disable-assets", "--enable-assets", "--cpu", "--reserve-vram", "--cuda-device", "--directml", "--disable-api-nodes"];
        var parser = string.Join('\n', options.Select(option => $"parser.add_argument('{option}')"));
        var parserFile = Path.Combine(source, "comfy", "cli_args.py");
        File.WriteAllText(parserFile, parser);
        ComfyUpdateProbe.ValidateSource(parser, ["--cpu", "--reserve-vram=1.5"]);
        Check(true, "both parser declaration formats and selected compute options pass static isolation preflight");
        foreach (var missing in new[] { "--disable-all-custom-nodes", "--user-directory", "--base-directory", "--front-end-root", "--disable-assets" })
            Reject(() => ComfyUpdateProbe.ValidateSource(parser.Replace($"parser.add_argument('{missing}')", $"# parser.add_argument('{missing}')")), "commented or absent isolation option cannot authorize a probe: " + missing);
        Reject(() => ComfyUpdateProbe.ValidateSource(parser, ["--use-sage-attention"]), "target parser must support inherited compute options before source mutation");
        Reject(() => ComfyUpdateProbe.ValidateSource(parser, ["--directml=--enable-manager"]), "a compute value cannot be reinterpreted as a Manager or other isolation-breaking flag");
        Reject(() => ComfyUpdateProbe.ValidateSource(parser, ["--reserve-vram=-1"]), "inherited common compute settings retain launch-time validation");
        var config = new LauncherConfig { ComfyRoot = source, Python = "not-started.exe", ManagedComfy = true,
            ComfyArguments = ["--enable-manager", "--enable-manager-legacy-ui", "--whitelist-custom-nodes", "dangerous-plugin", "--database-url", "sqlite:///production.db",
                "--enable-assets", "--front-end-root", "external-frontend", "--cpu", "--reserve-vram=1.5", "--cuda-device", "0", "--unknown-plugin-option", "value"] };
        var original = JsonSerializer.Serialize(config);
        var sandbox = Path.Combine(root, "sandbox");
        var args = ComfyUpdateProbe.BuildArguments(config, sandbox, 12345);
        Check(args.Contains("--disable-all-custom-nodes") && args.Contains("--disable-assets") && !args.Contains("--whitelist-custom-nodes")
            && !args.Contains("--enable-manager") && !args.Contains("--enable-manager-legacy-ui") && !args.Contains("--enable-assets")
            && args.Contains(Path.Combine(sandbox, "frontend")) && !args.Contains("external-frontend") && !args.Contains("--unknown-plugin-option") && !args.Contains("--windows-standalone-build"),
            "probe argument allowlist excludes Manager, whitelist, external database/frontend and arbitrary plugin flags");
        Check(args.Contains("--cpu") && args.Contains("--cuda-device") && args.Contains("1.5") && !args.Contains("sqlite:///production.db")
            && args.Contains("sqlite:///" + Path.Combine(sandbox, "user", "probe.db").Replace('\\', '/')) && JsonSerializer.Serialize(config) == original,
            "probe retains selected compute settings and redirects database without changing saved options");
        var env = ComfyUpdateProbe.BuildEnvironment(sandbox);
        Check(env["PYTHONDONTWRITEBYTECODE"] == "1" && env["PYTHONNOUSERSITE"] == "1" && env["PYTHONPATH"] == ""
            && env["HF_HUB_OFFLINE"] == "1" && env["TEMP"].StartsWith(sandbox) && env["TORCH_HOME"].StartsWith(sandbox),
            "probe disables bytecode/user site writes and isolates temporary and model-library caches");
        Check(ComfyUpdateProbe.StatsMatch(JsonSerializer.SerializeToElement(new { system = new { argv = args } }), args)
            && !ComfyUpdateProbe.StatsMatch(JsonSerializer.SerializeToElement(new { system = new { argv = args.Concat(new[] { "--enable-manager" }) } }), args)
            && !ComfyUpdateProbe.StatsMatch(JsonSerializer.SerializeToElement(new { system = new { argv = new object[] { 3 } } }), args),
            "HTTP identity requires exact isolated argv and rejects malformed or extra flags");
        Check(ComfyUpdateProbe.CommandLineMatches(ComfyLaunchArguments.Format(new[] { "python.exe", "-B", "-c", "print('中文')", "path with space" }), ["-B", "-c", "print('中文')", "path with space"])
            && !ComfyUpdateProbe.CommandLineMatches("python.exe main.py", ["-B", "main.py"]), "OS command line independently proves the bytecode flag and exact bootstrap arguments");

        if (!OperatingSystem.IsWindows()) return passed;
        var python = Commands.Find("python.exe") ?? throw new IOException("Python is required for isolated ComfyUI probe fixtures.");
        File.WriteAllText(Path.Combine(source, "main.py"), Fixture);
        File.WriteAllText(Path.Combine(source, "mode.txt"), "ok");
        Directory.CreateDirectory(Path.Combine(source, "user"));
        Directory.CreateDirectory(Path.Combine(source, "custom_nodes", "must-not-import"));
        File.WriteAllText(Path.Combine(source, "user", "comfyui.db"), "original database");
        File.WriteAllText(Path.Combine(source, "custom_nodes", "must-not-import", "__init__.py"), "raise Exception('probe imported a custom node')");
        Directory.CreateDirectory(state);
        File.WriteAllText(Path.Combine(state, "resource-paths.json"), "original resource bindings");
        File.WriteAllText(Path.Combine(state, "processes.json"), "[]");
        var logs = new List<string>();
        var services = new ServiceManager(state, line => { lock (logs) logs.Add(line); });
        config.Python = python;
        using var unrelated = new TcpListener(IPAddress.Loopback, 0);
        unrelated.Start();
        config.ComfyPort = ((IPEndPoint)unrelated.LocalEndpoint).Port;
        var before = Fingerprint(source);
        await services.ProbeComfyUpdateAsync(config, default);
        Check(Fingerprint(source) == before && File.ReadAllText(Path.Combine(state, "resource-paths.json")) == "original resource bindings"
            && File.ReadAllText(Path.Combine(state, "processes.json")) == "[]", "real successful probe leaves source, custom nodes, user database, bindings and normal ownership byte-identical");
        Check(await ServiceManager.PortOpen(config.ComfyPort) && !services.IsOwnedRunning("ComfyUI")
            && !Directory.EnumerateDirectories(Path.Combine(state, "comfy-probes")).Any(), "probe uses its own port, stops its job and removes its temporary data without stopping the existing listener");
        var capture = Capture(state);
        var capturedRoot = capture.GetProperty("base").GetString()!;
        Check(capture.GetProperty("dont_write_bytecode").GetBoolean() && capture.GetProperty("cwd").GetString() == capturedRoot
            && capture.GetProperty("user").GetString()!.StartsWith(capturedRoot) && capture.GetProperty("cache").GetString()!.StartsWith(capturedRoot),
            "actual Python fixture observes -B, disposable cwd, isolated user directory and isolated cache");
        Check(!ProcessAlive(capture.GetProperty("pid").GetInt32()), "successful probe process has exited before the API returns");
        foreach (var mode in new[] { "bad-argv", "missing-core" })
        {
            File.WriteAllText(Path.Combine(source, "mode.txt"), mode);
            before = Fingerprint(source);
            try { await services.ProbeComfyUpdateAsync(config, default); throw new Exception("Expected probe failure."); }
            catch (IOException) { Check(true, "real probe rejects " + mode); }
            Check(Fingerprint(source) == before && !ProcessAlive(Capture(state).GetProperty("pid").GetInt32()) && await ServiceManager.PortOpen(config.ComfyPort),
                "failed " + mode + " probe cleans only its own process and preserves environment data");
        }
        File.WriteAllText(Path.Combine(source, "mode.txt"), "wait");
        before = Fingerprint(source);
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
        {
            try { await services.ProbeComfyUpdateAsync(config, cancellation.Token); throw new Exception("Expected cancelled probe."); }
            catch (OperationCanceledException)
            {
                var cancelled = Capture(state);
                Check(Fingerprint(source) == before && !ProcessAlive(cancelled.GetProperty("pid").GetInt32())
                    && !ProcessAlive(cancelled.GetProperty("child").GetInt32()), "cancelled probe joins its complete child lifetime before returning");
            }
        }
        var venv = Path.Combine(root, "venv");
        await Commands.Run(python, ["-B", "-m", "venv", "--without-pip", venv], root, null, default, timeout: TimeSpan.FromMinutes(1));
        config.Python = Path.Combine(venv, "Scripts", "python.exe");
        File.WriteAllText(Path.Combine(source, "mode.txt"), "ok");
        await services.ProbeComfyUpdateAsync(config, default);
        Check(!ProcessAlive(Capture(state).GetProperty("pid").GetInt32()), "real Windows venv redirector and base-interpreter listener pass identity checks and both stop");
        File.WriteAllText(parserFile, "parser.add_argument('--cpu')");
        var priorLog = File.ReadAllText(Path.Combine(state, "logs", "ComfyUI-update-probe.log"));
        try { await services.ProbeComfyUpdateAsync(config, default); throw new Exception("Expected unsupported parser."); }
        catch (InvalidDataException) { Check(priorLog == File.ReadAllText(Path.Combine(state, "logs", "ComfyUI-update-probe.log")), "unsupported source rejects before process launch or probe-log mutation"); }
        return passed;
    }

    private static string Fingerprint(string root) => string.Join('\n', Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));
    private static bool ProcessAlive(int pid) { try { using var process = Process.GetProcessById(pid); return !process.HasExited; } catch (ArgumentException) { return false; } }
    private static JsonElement Capture(string state)
    {
        var line = File.ReadLines(Path.Combine(state, "logs", "ComfyUI-update-probe.log")).Single(line => line.StartsWith("FIXTURE_PROBE="));
        using var json = JsonDocument.Parse(line["FIXTURE_PROBE=".Length..]);
        return json.RootElement.Clone();
    }
    private const string Fixture = """
import json, os, subprocess, sys, time
from pathlib import Path
from http.server import BaseHTTPRequestHandler, HTTPServer
def value(flag):
    return sys.argv[sys.argv.index(flag) + 1]
source = Path(__file__).parent
mode = (source / 'mode.txt').read_text()
base = value('--base-directory')
if '--disable-all-custom-nodes' not in sys.argv or '--whitelist-custom-nodes' in sys.argv:
    (source / 'CUSTOM_NODE_IMPORTED').write_text('bad')
if '--enable-manager' in sys.argv or '--enable-assets' in sys.argv:
    (source / 'user' / 'comfyui.db').write_text('bad')
Path(value('--user-directory'), 'probe-user.json').write_text('{}')
Path(value('--input-directory'), 'probe-input.txt').write_text('temporary')
Path(value('--output-directory'), 'probe-output.txt').write_text('temporary')
child = subprocess.Popen([sys.executable, '-B', '-c', 'import time; time.sleep(60)']) if mode == 'wait' else None
print('FIXTURE_PROBE=' + json.dumps({'pid':os.getpid(), 'child':child.pid if child else 0, 'dont_write_bytecode':sys.dont_write_bytecode, 'cwd':os.getcwd(), 'base':base, 'user':value('--user-directory'), 'cache':os.environ['TORCH_HOME']}), flush=True)
if mode == 'wait':
    time.sleep(60)
class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path == '/system_stats':
            payload = {'system': {'argv': sys.argv + (['--enable-manager'] if mode == 'bad-argv' else [])}}
        elif self.path == '/object_info':
            payload = {} if mode == 'missing-core' else {name: {} for name in ['UNETLoader', 'CLIPLoader', 'VAELoader', 'KSampler', 'KSamplerAdvanced', 'VAEDecodeTiled', 'EmptyLatentImage']}
        else:
            self.send_error(404)
            return
        encoded = json.dumps(payload).encode()
        self.send_response(200)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(encoded)))
        self.end_headers()
        self.wfile.write(encoded)
    def log_message(self, *_):
        pass
HTTPServer(('127.0.0.1', int(value('--port'))), Handler).serve_forever()
""";
}
