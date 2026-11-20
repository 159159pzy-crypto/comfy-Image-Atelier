using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Anima.Launcher.Core;
using Microsoft.Win32;

internal static class PacStartupTests
{
    private const string InternetSettings = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const string PacUrl = "http://127.0.0.1:1/launcher-test-only.pac";
    private static string TestKey(Guid id) => @"Software\AnimaLauncher-PacTest-" + id.ToString("N");

    [DllImport("advapi32.dll")]
    private static extern int RegOverridePredefKey(nint key, nint replacement);

    public static async Task<int> RunAsync(string work, string repository)
    {
        if (!OperatingSystem.IsWindows()) return 0;
        var id = Guid.NewGuid();
        using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        using var actualSettings = user.OpenSubKey(InternetSettings, writable: false);
        var before = actualSettings?.GetValue("AutoConfigURL");
        using (var tree = user.CreateSubKey(TestKey(id), writable: true, RegistryOptions.Volatile))
        using (var settings = tree.CreateSubKey(InternetSettings, writable: true, RegistryOptions.Volatile))
            settings.SetValue("AutoConfigURL", PacUrl);
        try
        {
            var state = Path.Combine(work, "pac-fixture-" + id.ToString("N"));
            Directory.CreateDirectory(state);
            var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = repository,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { typeof(PacStartupTests).Assembly.Location, "--pac-startup-child", state, id.ToString("N") }) info.ArgumentList.Add(arg);
            foreach (var key in new[] { "ANIMA_LAUNCHER_DIRECT_NETWORK", "ANIMA_LAUNCHER_NETWORK_MODE", "ANIMA_LAUNCHER_NETWORK_TARGET", "ANIMA_LAUNCHER_PROXY_ADDRESS" }) info.Environment.Remove(key);
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try { await process.WaitForExitAsync(timeout.Token); }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
            var lines = await output;
            File.WriteAllText(Path.Combine(work, "pac-startup.log"), lines + await errors);
            Console.Write(lines);
            if (process.ExitCode != 0) throw new Exception("Isolated PAC check failed: " + await errors);
            if (!Equals(before, actualSettings?.GetValue("AutoConfigURL"))) throw new Exception("The real user's PAC setting changed during verification.");
            const string parentResult = "PASS: parent user's PAC setting is unchanged";
            Console.WriteLine(parentResult);
            File.AppendAllText(Path.Combine(work, "pac-startup.log"), parentResult + Environment.NewLine);
            return lines.Split('\n').Count(line => line.StartsWith("PASS: ", StringComparison.Ordinal)) + 1;
        }
        finally { user.DeleteSubKeyTree(TestKey(id), throwOnMissingSubKey: false); }
    }

    [SupportedOSPlatform("windows")]
    public static async Task RunChildAsync(string work, Guid id)
    {
        using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        using var tree = user.OpenSubKey(TestKey(id), writable: false) ?? throw new IOException("Missing isolated PAC test root");
        var hkcu = (nint)unchecked((int)0x80000001);
        var result = RegOverridePredefKey(hkcu, tree.Handle.DangerousGetHandle());
        if (result != 0) throw new Win32Exception(result);
        try
        {
            using var settings = Registry.CurrentUser.OpenSubKey(InternetSettings);
            Check(Equals(settings?.GetValue("AutoConfigURL"), PacUrl) && NetworkPolicySnapshot.SystemUsesPac(), "real policy reader detects PAC in child process's mapped HKCU");
            var config = new LauncherConfig();
            config.Validate();
            using var updates = new LauncherUpdates(work, Environment.ProcessPath!, "1.7.0");
            using var downloads = new Downloads(engineOptions: new() { NetworkPolicyResolver = () => NetworkPolicySnapshot.Capture(config) });
            await using var tasks = new DownloadTaskService(downloads, work);
            using var systemHttp = NetworkPolicySnapshot.Capture(config).CreateHttpHandler();
            Check(systemHttp.UseProxy && systemHttp.Proxy is null && updates.Store.Load().Active is null && tasks.Owners.Count == 0,
                "startup constructs update and download services and retains the OS proxy handler under PAC");
            var repository = Environment.CurrentDirectory;
            var interpreter = Path.Combine(repository, ".venv", "Scripts", "python.exe");
            if (!File.Exists(interpreter)) interpreter = Commands.Find("python.exe") ?? throw new IOException("Python not found for the ComfyUI diagnostic fixture");
            var fixture = Path.Combine(work, "diagnostic-environment");
            Directory.CreateDirectory(Path.Combine(fixture, "comfy"));
            File.WriteAllText(Path.Combine(fixture, "main.py"), "");
            File.WriteAllText(Path.Combine(fixture, "comfy", "options.py"), "def enable_args_parsing(): pass");
            File.WriteAllText(Path.Combine(fixture, "folder_paths.py"), "from pathlib import Path\nmodels_dir = str(Path(__file__).parent / 'models')\nfolder_names_and_paths = {}\n");
            var probe = await Inventory.Probe(Inventory.NormalizeRoot(fixture), interpreter, Path.Combine(repository, "launcher", "scripts", "probe_comfy.py"), default);
            Check(probe.Paths["sams"].Single() == Path.Combine(fixture, "models", "sams"), "the packaged ComfyUI diagnostics script reports model directories under the PAC policy");
            try { NetworkPolicySnapshot.RejectPac(); throw new Exception("System-PAC confirmation was accepted"); }
            catch (InvalidDataException error) { Check(error.Message.Contains("PAC") && error.Message.Contains("代理地址"), "install confirmation identifies PAC and the available recovery settings"); }
            var install = new Dictionary<string, string>();
            NetworkPolicySnapshot.Capture(config).AddEnvironment(install, new Uri("https://pypi.org/simple/"));
            try { Commands.Info(interpreter, [], work, install); throw new Exception("System-PAC install subprocess was accepted"); }
            catch (InvalidDataException error) { Check(error.Message.Contains("PAC"), "a managed install subprocess also rejects implicit PAC transport"); }
            foreach (var mode in new[] { "direct", "custom" })
            {
                using var local = new TcpListener(IPAddress.Loopback, 0);
                local.Start();
                var address = "http://" + local.LocalEndpoint;
                config.NetworkMode = mode;
                config.ProxyAddress = mode == "custom" ? address : "";
                var file = Path.Combine(work, "pac-network.json");
                JsonFile.Write(file, config);
                var saved = JsonFile.Read<LauncherConfig>(file);
                saved.Validate();
                var choice = NetworkPolicySnapshot.Capture(saved);
                var environment = new Dictionary<string, string>();
                choice.AddEnvironment(environment, new Uri("https://pypi.org/simple/"));
                var process = Commands.Info(interpreter, [], work, environment);
                Check(process.Environment["HTTPS_PROXY"] == config.ProxyAddress, mode + " transport saved and applied to installation subprocess settings");
                using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var client = new HttpClient(choice.CreateHttpHandler());
                var destination = mode == "custom" ? "http://pac-proxy-test.invalid/check" : address + "/check";
                var response = client.GetStringAsync(destination, ct.Token);
                using var socket = await local.AcceptTcpClientAsync(ct.Token);
                using var stream = socket.GetStream();
                using var reader = new StreamReader(stream, leaveOpen: true);
                var request = await reader.ReadLineAsync(ct.Token);
                while (await reader.ReadLineAsync(ct.Token) is { Length: > 0 }) { }
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"), ct.Token);
                Check(await response == "ok" && request == "GET " + (mode == "custom" ? destination : "/check") + " HTTP/1.1",
                    mode + " HTTP request uses the saved route against a real loopback endpoint");
            }
        }
        finally
        {
            result = RegOverridePredefKey(hkcu, 0);
            if (result != 0) throw new Win32Exception(result);
        }
    }

    private static void Check(bool value, string label)
    {
        if (!value) throw new Exception("FAIL: " + label);
        Console.WriteLine("PASS: " + label);
    }
}
