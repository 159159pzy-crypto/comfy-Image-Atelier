using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Anima.Launcher.Core;

internal static class ServiceStartupTests
{
    public static async Task<int> Run(string work)
    {
        var passed = 0;
        void Check(bool success, string label)
        {
            if (!success) throw new Exception("FAIL: " + label);
            passed++;
            Console.WriteLine("PASS: " + label);
        }
        var pathRoot = Path.Combine(work, "service-default-paths");
        var bundle = Path.Combine(pathRoot, "bundled app 中文");
        var pin = Path.Combine(pathRoot, "pinned app");
        var data = Path.Combine(pathRoot, "data");
        Directory.CreateDirectory(bundle);
        Directory.CreateDirectory(pin);
        File.WriteAllText(Path.Combine(bundle, "run.py"), "# startup fixture");
        File.WriteAllText(Path.Combine(pin, "run.py"), "# pinned fixture");
        // The isolated candidate and older installations legitimately persist only
        // ports/preferences before an application snapshot has been selected.
        var initial = JsonSerializer.Deserialize<LauncherConfig>("""{"schemaVersion":1,"comfyPort":19992,"webPort":19994,"startOnLaunch":false}""", JsonFile.Options)!;
        Check(RuntimePolicies.EnsureWorkbenchDefaults(initial, bundle, data)
            && initial.AppRoot == bundle && initial.DataDir == data && !initial.SetupComplete
            && initial.ComfyPort == 19992 && initial.WebPort == 19994,
            "minimal persisted first-run configuration receives bundled workbench paths without needing SetupComplete");
        RuntimePolicies.ValidateWorkbenchDirectory(initial.AppRoot);
        Check(!RuntimePolicies.EnsureWorkbenchDefaults(initial, pin, Path.Combine(pathRoot, "other-data"))
            && initial.AppRoot == bundle && initial.DataDir == data,
            "workbench default restoration is idempotent and preserves explicit source and data paths");
        var pinned = new LauncherConfig { PinnedAppRoot = pin, DataDir = data };
        Check(RuntimePolicies.EnsureWorkbenchDefaults(pinned, bundle, "unused") && pinned.AppRoot == pin && pinned.PinnedAppRoot == pin
            && pinned.DataDir == data, "an omitted AppRoot honors a user's existing rollback pin");
        var frozen = JsonSerializer.Serialize(new LauncherConfig { ComfyRoot = Path.Combine(pathRoot, "ComfyUI"), DataDir = data, SourceId = "mirror" }, JsonFile.Options);
        var resumed = JsonSerializer.Deserialize<LauncherConfig>(frozen, JsonFile.Options)!;
        Check(RuntimePolicies.EnsureWorkbenchDefaults(resumed, bundle, "unused") && resumed.AppRoot == bundle
            && JsonSerializer.Deserialize<LauncherConfig>(frozen, JsonFile.Options)!.AppRoot.Length == 0
            && resumed.SourceId == "mirror" && resumed.DataDir == data,
            "resuming an old empty-AppRoot snapshot repairs only the active runtime copy and retains frozen task identity");
        {
            var services = new ServiceManager(Path.Combine(pathRoot, "guard-state"), _ => { });
            foreach (var invalid in new[] { "", "relative-app", Path.Combine(pathRoot, "missing") })
            {
                foreach (var update in new[] { false, true })
                {
                    try
                    {
                        var config = new LauncherConfig { AppRoot = invalid };
                        if (update) await services.StartWebUIForSoftwareUpdateAsync(config, null!, default);
                        else await services.StartWebUI(config, null!, default);
                        throw new Exception("Expected invalid workbench directory rejection.");
                    }
                    catch (InvalidDataException error)
                    {
                        Check(error.Message.Contains("工作台资源目录") && !error.Message.Contains("WinError"),
                            $"{(update ? "update" : "normal")} startup rejects an invalid workbench directory before starting any services: {invalid}");
                    }
                }
            }
            Check(!File.Exists(Path.Combine(pathRoot, "guard-state", "processes.json")),
                "invalid workbench paths never create process ownership records");
        }
        var info = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        info.ArgumentList.Add("--exit-fixture");
        using var owner = Process.Start(info)!;
        try
        {
            if (await owner.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) != "ready")
                throw new IOException("Service exit fixture did not become ready.");
            // This is how StartProcess acquires Python launched by its short-lived bootstrap.
            using var attached = Process.GetProcessById(owner.Id);
            _ = attached.StartTime;
            await owner.StandardInput.WriteLineAsync("exit");
            await owner.StandardInput.FlushAsync();
            await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var ready = typeof(ServiceManager).GetMethod("Ready", BindingFlags.NonPublic | BindingFlags.Static)!;
            var logPath = Path.Combine(work, "service-startup", "logs", "ComfyUI.log");
            foreach (var (process, label) in new[] { (attached, "attached"), (owner, "directly-started") })
            {
                try
                {
                    await (Task)ready.Invoke(null, ["http://127.0.0.1:1/not-used", process, CancellationToken.None, "ComfyUI", logPath])!;
                    throw new Exception("Exited service must fail readiness.");
                }
                catch (IOException ex)
                {
                    Check(ex.Message.Contains("ComfyUI 服务启动失败") && ex.Message.Contains("请查看日志")
                        && ex.Message.Contains(logPath) && !ex.Message.Contains("Process was not started"),
                        $"an exited {label} service reports an actionable Chinese error and its actual log path");
                    if (process == owner)
                        Check(ex.Message.Contains("退出码 23"), "service failure includes the exit code when the process object retains it");
                }
            }
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try
            {
                await (Task)ready.Invoke(null, ["http://127.0.0.1:1/not-used", attached, cancelled.Token, "ComfyUI", logPath])!;
                throw new Exception("Cancelled readiness must stop.");
            }
            catch (OperationCanceledException) { Check(true, "user cancellation remains cancellation instead of a service-startup failure"); }
        }
        finally
        {
            if (!owner.HasExited) owner.Kill(true);
            await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        return passed;
    }
}
