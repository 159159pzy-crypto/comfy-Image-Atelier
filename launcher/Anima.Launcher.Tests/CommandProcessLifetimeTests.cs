using System.Diagnostics;
using System.Text.Json;
using Anima.Launcher.Core;

internal static class CommandProcessLifetimeTests
{
    public static async Task<int> Run(string work)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The command lifetime fixtures require Windows jobs.");
        var python = Commands.Find("python.exe") ?? throw new IOException("Python is required for command lifetime fixtures.");
        var root = Path.Combine(work, "command lifetime 中文");
        Directory.CreateDirectory(root);
        var passed = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new Exception("FAIL: " + description);
            Console.WriteLine("PASS: " + description); passed++;
        }
        var arguments = new[] { "", "space path\\", "中文 参数", "embedded\"quote", "\\\\", "backslash\\\"quote", "tail \\\\", "{\"args\":[\"a b\",\"C:\\\\with space\\\\\"]}", "line\nline" };
        var roundTrip = await Commands.Run(python, new[] { "-c", "import json,sys; print(json.dumps(sys.argv[1:],ensure_ascii=False))" }.Concat(arguments), root, null, default);
        Check(JsonSerializer.Deserialize<string[]>(roundTrip)!.SequenceEqual(arguments), "native argv preserves empty, Unicode, spaces, quotes, JSON and trailing backslashes");
        var bare = await Commands.Run("python.exe", ["-c", "print('bare-executable-ok')"], root, null, default);
        Check(bare.Trim() == "bare-executable-ok", "bare executable retains PATH lookup");
        var nested = await Commands.Run(python, ["-c", """
import ctypes
from ctypes import wintypes
k = ctypes.WinDLL('kernel32', use_last_error=True)
k.GetCurrentProcess.restype = wintypes.HANDLE
k.CreateJobObjectW.argtypes = [ctypes.c_void_p, wintypes.LPCWSTR]
k.CreateJobObjectW.restype = wintypes.HANDLE
k.IsProcessInJob.argtypes = [wintypes.HANDLE, wintypes.HANDLE, ctypes.POINTER(wintypes.BOOL)]
k.AssignProcessToJobObject.argtypes = [wintypes.HANDLE, wintypes.HANDLE]
k.CloseHandle.argtypes = [wintypes.HANDLE]
member = wintypes.BOOL()
assert k.IsProcessInJob(k.GetCurrentProcess(), None, ctypes.byref(member)) and member.value
job = k.CreateJobObjectW(None, None)
assert job
try:
    assert k.AssignProcessToJobObject(job, k.GetCurrentProcess()), ctypes.get_last_error()
    print('nested-job-ok')
finally: k.CloseHandle(job)
"""], root, null, default);
        Check(nested.Trim() == "nested-job-ok", "command is inside its creation job and permits nested Windows jobs");
        var variable = "ANIMA_COMMAND_LIFETIME_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "inherited");
        try
        {
            var environment = await Commands.Run(python, ["-c", "import os,sys; print(os.environ.get(sys.argv[1],'removed')); print(os.environ['ANIMA_COMMAND_UNICODE']); print(os.environ['GCM_INTERACTIVE'])", variable], root, null, default,
                new Dictionary<string, string> { [variable] = null!, ["ANIMA_COMMAND_UNICODE"] = "中文 \"quoted\"", ["GCM_INTERACTIVE"] = "always" });
            Check(environment.Replace("\r", "").Trim() == "removed\n中文 \"quoted\"\nnever", "native environment preserves UTF-8, removes inherited values and disables interactive GCM");
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }

        // A parent exits while its child retains both stdout/stderr and starts a
        // grandchild. Waiting for pipe EOF or killing just the exited PID fails.
        var script = Path.Combine(root, "tree fixture.py");
        await File.WriteAllTextAsync(script, """
import os, subprocess, sys, time, json
from pathlib import Path
mode, marker = sys.argv[1:3]
def write_marker(path, value):
    temporary = Path(str(path) + '.tmp')
    temporary.write_text(value)
    temporary.replace(path)
if mode == 'leaf':
    write_marker(marker + '.leaf', str(os.getpid()))
    time.sleep(120)
elif mode == 'middle':
    child = subprocess.Popen([sys.executable, __file__, 'leaf', marker])
    write_marker(marker + '.middle', str(os.getpid()))
    while not Path(marker + '.leaf').exists(): time.sleep(.01)
    # Exit before cancellation: the root's process tree can no longer discover
    # this child's descendant by following only live parent processes.
    if sys.argv[3] == 'orphan': os._exit(0)
    time.sleep(120)
else:
    child = subprocess.Popen([sys.executable, __file__, 'middle', marker, 'orphan' if mode == 'cancel' else 'stay'])
    while not Path(marker + '.leaf').exists(): time.sleep(.01)
    write_marker(marker, json.dumps([os.getpid(), child.pid, int(Path(marker + '.leaf').read_text())]))
    print('root-ready', flush=True)
    if mode == 'success': sys.exit(0)
    if mode == 'failure':
        print('expected-stderr-中文', file=sys.stderr, flush=True)
        sys.exit(7)
    time.sleep(120)
""");
        async Task<int[]> ReadPids(string marker)
        {
            var stop = Stopwatch.StartNew();
            while (stop.Elapsed < TimeSpan.FromSeconds(10))
            {
                try { if (File.Exists(marker)) return JsonSerializer.Deserialize<int[]>(await File.ReadAllTextAsync(marker))!; }
                catch (IOException) { }
                await Task.Delay(10);
            }
            throw new TimeoutException("Fixture PID marker did not become readable: " + marker);
        }
        bool Exited(int pid)
        {
            try { using var process = Process.GetProcessById(pid); return process.HasExited; }
            catch (ArgumentException) { return true; }
        }
        async Task Cleanup(int[] pids)
        {
            foreach (var pid in pids.Reverse())
            {
                try
                {
                    using var process = Process.GetProcessById(pid);
                    if (!process.HasExited) process.Kill(true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (ArgumentException) { }
            }
        }
        foreach (var mode in new[] { "success", "failure", "cancel", "timeout" })
        {
            var marker = Path.Combine(root, mode + ".json");
            using var cancellation = new CancellationTokenSource();
            var command = Commands.Run(python, [script, mode, marker], root, null, cancellation.Token,
                timeout: TimeSpan.FromSeconds(mode == "timeout" ? 2 : 15));
            var pids = await ReadPids(marker);
            // Hold each exact native process handle where it is still alive;
            // assertions cannot accidentally observe a reused PID.
            var handles = pids.Select(pid =>
            {
                Process? process = null;
                try { process = Process.GetProcessById(pid); _ = process.Handle; return process; }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                { process?.Dispose(); return null; } // A successfully joined parent can already be gone.
            }).ToArray();
            try
            {
                if (mode == "cancel") { await Task.Delay(120); cancellation.Cancel(); }
                Exception? failure = null;
                string? output = null;
                try { output = await command.WaitAsync(TimeSpan.FromSeconds(20)); } catch (Exception ex) { failure = ex; }
                Check(mode switch
                {
                    "success" => failure is null && output!.Contains("root-ready"),
                    "failure" => failure is CommandFailureException commandFailure && commandFailure.Message.Contains("7") && commandFailure.Message.Contains("expected-stderr-中文"),
                    "cancel" => failure is OperationCanceledException,
                    _ => failure is TimeoutException,
                }, mode + " retains the command result/exception contract");
                Check(handles.Select((p, i) => p?.HasExited ?? Exited(pids[i])).All(x => x), mode + " returns only after every child/grandchild has exited");
            }
            finally
            {
                cancellation.Cancel();
                foreach (var process in handles) process?.Dispose();
                await Cleanup(pids);
            }
        }

        // Match ServiceManager: its long-lived service inherits real handles,
        // never the temporary command's redirected stdout pipes.
        var serviceMarker = Path.Combine(root, "service.pid");
        const string launch = "import subprocess,sys; from pathlib import Path; p=subprocess.Popen([sys.executable,'-c','import time; time.sleep(120)'],stdin=subprocess.DEVNULL,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL); Path(sys.argv[1]).write_text(str(p.pid)); print(p.pid)";
        var serviceOutput = await Commands.Run(python, ["-c", launch, serviceMarker], root, null, default, allowBackgroundChildren: true);
        var servicePid = int.Parse(serviceOutput.Trim());
        try { Check(!Exited(servicePid), "explicit successful service bootstrap preserves its background child"); }
        finally { await Cleanup([servicePid]); }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            // No readiness handshake: this parent can exit immediately after
            // spawning its inherited-pipe child, before any post-Start assign.
            var quick = await Commands.Run(python, ["-c", "import subprocess,sys; p=subprocess.Popen([sys.executable,'-c','import time; time.sleep(120)']); print(p.pid,flush=True)"], root, null, default,
                timeout: TimeSpan.FromSeconds(10));
            var quickPid = int.Parse(quick.Trim());
            try { Check(Exited(quickPid), "creation-time job captures immediately exiting parent, attempt " + (attempt + 1)); }
            finally { await Cleanup([quickPid]); }
        }

        var callbackMarker = Path.Combine(root, "callback-failure.json");
        Exception? callbackFailure = null;
        var callbackCommand = Commands.Run(python, [script, "cancel", callbackMarker], root,
            _ => throw new InvalidOperationException("fixture logging callback failure"), default, timeout: TimeSpan.FromSeconds(10));
        var callbackPids = await ReadPids(callbackMarker);
        try
        {
            try { await callbackCommand; } catch (Exception ex) { callbackFailure = ex; }
            Check(callbackFailure is InvalidOperationException && callbackFailure.Message == "fixture logging callback failure",
                "reader callback fault promptly preserves its own exception instead of becoming a timeout");
            Check(callbackPids.All(Exited), "reader callback fault joins all command descendants");
        }
        finally { await Cleanup(callbackPids); }

        var preserved = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var preserveCancellation = new CancellationTokenSource();
        var preserve = Commands.Run(python, ["-c", "import os,time; print(os.getpid(),flush=True); time.sleep(120)"], root,
            line => { if (int.TryParse(line, out var pid)) preserved.TrySetResult(pid); }, preserveCancellation.Token, terminateOnFailure: false);
        var preservedPid = await preserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            preserveCancellation.Cancel();
            try { await preserve; Check(false, "preserve cancellation must propagate"); }
            catch (OperationCanceledException) { Check(!Exited(preservedPid), "terminateOnFailure false preserves its process on cancellation"); }
        }
        finally { await Cleanup([preservedPid]); }
        await File.WriteAllTextAsync(Path.Combine(root, "evidence.json"), JsonSerializer.Serialize(new { passed, windows = Environment.OSVersion.VersionString, arguments, source = "atomic creation-time job membership; no installation environment modified" }, new JsonSerializerOptions { WriteIndented = true }));
        return passed;
    }
}
