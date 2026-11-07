using Anima.Launcher.Core;

internal static class WebUiOwnershipTests
{
    public static int Run(string work)
    {
        var root = Path.Combine(work, "webui-owner-identities");
        var venv = Path.Combine(root, "venv");
        var otherVenv = Path.Combine(root, "other-venv");
        var basePython = Path.Combine(root, "base", "python.exe");
        foreach (var directory in new[] { venv, otherVenv })
        {
            Directory.CreateDirectory(Path.Combine(directory, "Scripts"));
            File.WriteAllText(Path.Combine(directory, "pyvenv.cfg"), "home = " + Path.GetDirectoryName(basePython));
        }
        var owner = new OwnedProcess(100, 1000, Path.Combine(venv, "Scripts", "python.exe"), "WebUI", "token");
        var listener = new OwnedProcess(200, 2000, basePython, "WebUI");
        var passed = 0;
        void Check(bool ok, string message)
        {
            if (!ok) throw new Exception("FAIL: " + message);
            passed++; Console.WriteLine("PASS: " + message);
        }
        Check(ServiceManager.WebUiOwnerMatches(owner, owner, owner), "direct WebUI process identity retains ownership without a parent");
        Check(ServiceManager.WebUiOwnerMatches(owner, listener, listener, owner, owner), "stable direct venv parent and base listener share the saved WebUI token");
        Check(!ServiceManager.WebUiOwnerMatches(owner, null, listener, owner, owner)
            && !ServiceManager.WebUiOwnerMatches(owner, listener, null, owner, owner), "missing listener identity never authorizes a WebUI token");
        Check(!ServiceManager.WebUiOwnerMatches(owner, listener, listener with { StartTimeUtcTicks = 2001 }, owner, owner),
            "a recycled listener PID loses WebUI ownership between checks");
        Check(!ServiceManager.WebUiOwnerMatches(owner, listener, listener with { Executable = owner.Executable }, owner, owner),
            "a changed listener executable loses WebUI ownership between checks");
        Check(!ServiceManager.WebUiOwnerMatches(owner, listener, listener, null, owner)
            && !ServiceManager.WebUiOwnerMatches(owner, listener, listener, owner, null), "an exited or unreadable venv parent never authorizes shutdown");
        Check(!ServiceManager.WebUiOwnerMatches(owner, listener, listener, owner, owner with { Pid = 101 }),
            "a changed direct parent cannot inherit another launcher's shutdown token");
        Check(!ServiceManager.WebUiOwnerMatches(owner, listener, listener, owner, owner with { StartTimeUtcTicks = 1001 }),
            "a recycled venv parent PID cannot inherit the saved shutdown token");
        Check(!ServiceManager.WebUiOwnerMatches(owner, listener, listener, owner,
                owner with { Executable = Path.Combine(otherVenv, "Scripts", "python.exe") }),
            "two venvs sharing one base Python have distinct shutdown ownership");
        var tooNew = owner with { StartTimeUtcTicks = 3000 };
        Check(!ServiceManager.WebUiOwnerMatches(tooNew, listener, listener, tooNew, tooNew),
            "a parent created after its alleged listener child is rejected");
        Check(!ServiceManager.WebUiOwnerMatches(owner, listener with { Executable = Path.Combine(root, "another", "python.exe") },
                listener with { Executable = Path.Combine(root, "another", "python.exe") }, owner, owner),
            "a venv parent cannot grant credentials to an unrelated interpreter");
        var nonVenv = owner with { Executable = Path.Combine(root, "unmanaged", "python.exe") };
        Check(!ServiceManager.WebUiOwnerMatches(nonVenv, listener, listener, nonVenv, nonVenv),
            "an arbitrary parent process cannot grant credentials without a proven venv layout");
        Check(!ServiceManager.WebUiOwnerMatches(owner with { Role = "ComfyUI" }, listener, listener, owner, owner),
            "ComfyUI process ownership cannot authorize a WebUI shutdown");
        File.WriteAllText(Path.Combine(venv, "pyvenv.cfg"), "home = relative-base");
        Check(!ServiceManager.WebUiOwnerMatches(owner, listener, listener, owner, owner),
            "a relative venv base path cannot establish shutdown ownership");
        return passed;
    }
}
