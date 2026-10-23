using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Anima.Launcher.Core;

internal static class SoftwareUpdateServiceTests
{
    public static async Task<int> Run(string work, RuntimeManifest manifest)
    {
        var python = Commands.Find("python.exe") ?? throw new IOException("Python is required for software-update service fixtures.");
        var root = Path.Combine(work, "software-update-services");
        var versions = Path.Combine(root, "versions");
        var comfy = Path.Combine(root, "stopped-comfy");
        Directory.CreateDirectory(comfy);
        var startedMarker = Path.Combine(comfy, "comfy-was-started.txt");
        File.WriteAllText(Path.Combine(comfy, "main.py"), "from pathlib import Path\nPath(__file__).with_name('comfy-was-started.txt').write_text('unexpected start')\n");
        var passed = 0;
        void Check(bool ok, string message)
        {
            if (!ok) throw new Exception("FAIL: " + message);
            passed++;
            Console.WriteLine("PASS: " + message);
        }
        var previous = new LauncherConfig
        {
            AppRoot = Path.Combine(versions, "old"), DataDir = Path.Combine(root, "data"), ComfyRoot = comfy,
            Python = python, WebPort = FreePort(), ComfyPort = FreePort(), SetupComplete = true,
        };
        while (previous.WebPort == previous.ComfyPort) previous.ComfyPort = FreePort();
        WriteWorkbench(previous.AppRoot, failStatus: false);
        var target = previous.Clone(); target.AppRoot = Path.Combine(versions, "new");
        WriteWorkbench(target.AppRoot, failStatus: false);
        var runtime = manifest with { Nodes = [] };
        var services = new ServiceManager(root, _ => { });
        try
        {
            await services.StartWebUIForSoftwareUpdateAsync(previous, runtime, default);
            var original = JsonFile.Read<OwnedProcess[]>(Path.Combine(root, "processes.json")).Single();
            Check(!File.Exists(startedMarker) && !await ServiceManager.PortOpen(previous.ComfyPort)
                && services.IsOwnedRunning("WebUI") && !services.IsOwnedRunning("ComfyUI"),
                "update-only startup restores an offline workbench without starting stopped ComfyUI");
            await services.SwitchWorkbenchWhenIdleAsync(previous, target, runtime, versions, true);
            var health = await ServiceManager.Json(target.WebUrl + "/api/launcher-health");
            Check(!LauncherUpdateStore.IsRunning(original) && ServiceManager.IsOurWebUi(health, target)
                && !File.Exists(startedMarker) && !await ServiceManager.PortOpen(target.ComfyPort),
                "full workbench switch safely replaces only WebUI while stopped ComfyUI remains stopped");
            Check(JsonFile.Read<OwnedProcess[]>(Path.Combine(root, "processes.json")).All(p => p.Role == "WebUI"),
                "software update never registers a newly started ComfyUI process");
            Check(await services.TryStopWebUiWhenIdleAsync(target, versions) == IdleShutdownResult.Stopped,
                "updated offline workbench retains its token and can exit through the idle guard");

            var failing = target.Clone(); failing.AppRoot = Path.Combine(versions, "failed-status");
            WriteWorkbench(failing.AppRoot, failStatus: true);
            try
            {
                await services.StartWebUIForSoftwareUpdateAsync(failing, runtime, default);
                Check(false, "failed candidate status must reject startup");
            }
            catch (IOException)
            {
                Check(services.IsOwnedRunning("WebUI") && await ServiceManager.PortOpen(failing.WebPort),
                    "failed update startup preserves its live WebUI instead of force-killing it");
            }
            var owned = JsonFile.Read<OwnedProcess[]>(Path.Combine(root, "processes.json")).Single();
            Check(!string.IsNullOrWhiteSpace(owned.ShutdownToken) && !File.Exists(startedMarker),
                "failed startup retains ownership and shutdown credentials without starting ComfyUI");
            Check(await services.TryStopWebUiWhenIdleAsync(failing, versions) == IdleShutdownResult.Stopped,
                "a failed live candidate remains recoverable through authenticated idle shutdown");

            // The bootstrap's generic command helper must also opt out of its
            // usual timeout/cancellation process-tree termination.
            var childPid = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = new CancellationTokenSource();
            var command = Commands.Run(python, ["-c", "import os,time; print(os.getpid(),flush=True); time.sleep(30)"], root,
                line => { if (int.TryParse(line, out var id)) childPid.TrySetResult(id); }, cancellation.Token, terminateOnFailure: false);
            var pid = await childPid.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var bootstrap = Process.GetProcessById(pid);
            try
            {
                cancellation.Cancel();
                try { await command; Check(false, "update bootstrap cancellation must propagate"); }
                catch (OperationCanceledException) { Check(!bootstrap.HasExited, "update bootstrap cancellation preserves the process and its service descendants"); }
            }
            finally { if (!bootstrap.HasExited) bootstrap.Kill(true); await bootstrap.WaitForExitAsync(); }
        }
        finally
        {
            // Test-only emergency cleanup is confined to the exact identities
            // launched into this new fixture state, never production processes.
            var records = Path.Combine(root, "processes.json");
            if (File.Exists(records))
                foreach (var identity in JsonFile.Read<OwnedProcess[]>(records))
                    if (LauncherUpdateStore.IsRunning(identity))
                    {
                        using var process = Process.GetProcessById(identity.Pid);
                        process.Kill(true);
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    }
        }
        return passed;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }

    private static void WriteWorkbench(string directory, bool failStatus)
    {
        Directory.CreateDirectory(Path.Combine(directory, "anima_webui"));
        File.WriteAllText(Path.Combine(directory, "anima_webui", "server.py"), "# launcher-shutdown fixture\n");
        File.WriteAllText(Path.Combine(directory, "status-mode.txt"), failStatus ? "fail" : "offline");
        File.WriteAllText(Path.Combine(directory, "run.py"), """
import argparse, json, os, threading
from pathlib import Path
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
parser = argparse.ArgumentParser()
parser.add_argument('--host'); parser.add_argument('--port', type=int); parser.add_argument('--comfy-url')
parser.add_argument('--data-dir'); parser.add_argument('--resource-paths'); parser.add_argument('--no-browser', action='store_true')
parser.add_argument('--shutdown-token')
args = parser.parse_args()
root = Path(__file__).resolve().parent
def shutdown():
    server.shutdown(); server.server_close(); os._exit(0)
class Handler(BaseHTTPRequestHandler):
    def log_message(self, *_): pass
    def reply(self, status, value):
        body = json.dumps(value).encode()
        self.send_response(status); self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(body))); self.end_headers(); self.wfile.write(body); self.wfile.flush()
    def do_GET(self):
        if self.path == '/api/launcher-health':
            self.reply(200, dict(app='anima-random-studio', app_dir=str(root), data_dir=str(Path(args.data_dir).resolve()),
                                 comfy_url=args.comfy_url, idle_shutdown_supported=True))
        elif self.path == '/api/status':
            self.reply(503 if (root / 'status-mode.txt').read_text() == 'fail' else 200, dict(online=False))
        else: self.reply(404, {})
    def do_POST(self):
        body = json.loads(self.rfile.read(int(self.headers.get('Content-Length', '0'))))
        if self.path != '/api/launcher-shutdown' or body.get('token') != args.shutdown_token:
            self.reply(403, {}); return
        self.reply(200, dict(stopping=True))
        threading.Timer(0.1, shutdown).start()
server = ThreadingHTTPServer(('127.0.0.1', args.port), Handler)
server.serve_forever()
""");
    }
}
