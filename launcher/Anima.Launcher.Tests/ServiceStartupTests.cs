using System.Diagnostics;
using System.Reflection;
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
