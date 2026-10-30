using System.Net;
using System.Text.Json.Nodes;
using Anima.Launcher.Core;

internal static class InstallationSourcePreferenceTests
{
    public static async Task<int> Run(string work)
    {
        var originalDirect = Environment.GetEnvironmentVariable("ANIMA_LAUNCHER_DIRECT_NETWORK");
        try { Environment.SetEnvironmentVariable("ANIMA_LAUNCHER_DIRECT_NETWORK", null); return await RunIsolated(work); }
        finally { Environment.SetEnvironmentVariable("ANIMA_LAUNCHER_DIRECT_NETWORK", originalDirect); }
    }

    private static async Task<int> RunIsolated(string work)
    {
        var count = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); count++; }
        var cache = Path.Combine(work, "source-preference-cache");
        var config = new LauncherConfig { SourceId = "china", NetworkMode = "direct" };
        const string official = InstallationSourcePreferences.GitHubBase;
        const string mirror = "https://gh-proxy.com/https://github.com/";
        const string firstRepo = "https://github.com/fixture/first.git";
        const string secondRepo = "https://github.com/fixture/second.git";
        Check(InstallationSourcePreferences.GitBases(cache, config).SequenceEqual([mirror, official]),
            "before any proven success the selected profile keeps its configured source order");
        InstallationSourcePreferences.RememberRepository(cache, config, firstRepo, firstRepo);
        Check(InstallationSourcePreferences.GitRepositories(cache, secondRepo, config).SequenceEqual([secondRepo, "https://gh-proxy.com/" + secondRepo]),
            "a verified repository fetch lets the next repository reuse the working Git base immediately");
        var path = InstallationSourcePreferences.PathFor(cache, config);
        Check(File.Exists(path) && InstallationSourcePreferences.GitBases(cache, config)[0] == official,
            "source preference is read back from the runtime cache for restarted installation steps");

        var changedProxy = config.Clone(); changedProxy.NetworkMode = "custom"; changedProxy.ProxyAddress = "http://127.0.0.1:43210";
        Check(InstallationSourcePreferences.GitBases(cache, changedProxy)[0] == mirror,
            "changing the network profile does not reuse a success observed under another transport");
        var changedSource = config.Clone(); changedSource.SourceId = "official";
        Check(InstallationSourcePreferences.GitBases(cache, changedSource).SequenceEqual([official]),
            "changing source profiles cannot reintroduce a formerly allowed mirror");
        var previousProxy = HttpClient.DefaultProxy;
        try
        {
            var system = config.Clone(); system.NetworkMode = "system";
            HttpClient.DefaultProxy = new WebProxy("http://127.0.0.1:43111");
            InstallationSourcePreferences.RememberGitBase(cache, system, official);
            Check(InstallationSourcePreferences.GitBases(cache, system)[0] == official,
                "an unchanged effective system proxy can reuse its successful Git source");
            HttpClient.DefaultProxy = new WebProxy("http://127.0.0.1:43222");
            Check(InstallationSourcePreferences.GitBases(cache, system)[0] == mirror,
                "changing the effective system proxy invalidates preference even when saved settings are identical");
        }
        finally { HttpClient.DefaultProxy = previousProxy; }

        var custom = config.Clone(); custom.GitMirror = "https://custom.example/prefix/{url}/suffix";
        InstallationSourcePreferences.RememberGitBase(cache, custom, official);
        Check(InstallationSourcePreferences.GitBases(cache, custom)[0] == "https://custom.example/prefix/https://github.com//suffix"
            && InstallationSourcePreferences.GitRepositories(cache, secondRepo, custom)[0] == "https://custom.example/prefix/" + secondRepo + "/suffix"
            && InstallationSourcePreferences.GitRepositories(cache, secondRepo, custom)[1] == secondRepo,
            "an explicit custom mirror retains precedence while cached success orders its fallbacks");
        Check(InstallationSourcePreferences.GitRepositories(cache, "https://git.example/repository.git", config).SequenceEqual(["https://git.example/repository.git"]),
            "GitHub preference does not rewrite unrelated repository hosts");
        var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject(); document["gitBase"] = "https://unexpected.example/";
        File.WriteAllText(path, document.ToJsonString());
        Check(InstallationSourcePreferences.GitBases(cache, config).SequenceEqual([mirror, official]),
            "cached URLs outside the current source allowlist are ignored");
        try { InstallationSourcePreferences.RememberGitBase(cache, config, "https://unexpected.example/"); throw new Exception("Invalid source accepted"); }
        catch (InvalidDataException) { count++; }
        File.WriteAllText(path, "broken JSON");
        Check(InstallationSourcePreferences.GitBases(cache, config).SequenceEqual([mirror, official]),
            "a damaged optional preference file does not prevent installation");
        InstallationSourcePreferences.RememberGitBase(cache, config, mirror);
        Environment.SetEnvironmentVariable("ANIMA_LAUNCHER_DIRECT_NETWORK", "1");
        try { Check(InstallationSourcePreferences.GitBases(cache, config).SequenceEqual([official]), "forced direct acceptance cannot reuse a cached forwarding gateway"); }
        finally { Environment.SetEnvironmentVariable("ANIMA_LAUNCHER_DIRECT_NETWORK", null); }

        var sha = new string('a', 40);
        const string modules = "[submodule \"github\"]\n path = web/github\n url = https://github.com/fixture/submodule.git\n[submodule \"other\"]\n path = web/other\n url = https://other.example/submodule.git\n";
        Check(!InstallationSourcePreferences.HasPendingGitHubSubmodules("", "")
            && !InstallationSourcePreferences.HasPendingGitHubSubmodules(modules, " " + sha + " web/github\n"),
            "empty or already initialized submodule commands cannot count as a successful source transfer");
        Check(InstallationSourcePreferences.HasPendingGitHubSubmodules(modules, "-" + sha + " web/github\n"),
            "an uninitialized GitHub submodule identifies a real transfer using the selected Git base");
        Check(!InstallationSourcePreferences.HasPendingGitHubSubmodules(modules, " " + sha + " web/github\n-" + sha + " web/other\n"),
            "a pending unrelated submodule does not fabricate a GitHub transport success");

        var auth = new CommandFailureException("git.exe", 128, "", "fatal: Authentication failed for '" + mirror + "fixture/first.git'");
        var forbidden = new CommandFailureException("git.exe", 128, "", "fatal: unable to access '" + mirror + "fixture/first.git': The requested URL returned error: 403");
        var wrapped = new CommandFailureException("python.exe", 1, "", "fatal: Authentication failed for '" + mirror + "fixture/first.git'\n git clone did not run successfully");
        var pypi = new CommandFailureException("python.exe", 1, "", "ERROR: HTTP 403 while fetching https://pypi.org/simple/fixture/");
        Check(Installer.IsPublicGitMirrorFailure(auth, mirror, config) && Installer.IsPublicGitMirrorFailure(forbidden, mirror, config)
            && Installer.IsPublicGitMirrorFailure(wrapped, mirror, config),
            "public built-in Git mirror authentication failures can fall back including explicit Git failures wrapped by pip");
        Check(!Installer.IsPublicGitMirrorFailure(pypi, mirror, config)
            && !Installer.IsPublicGitMirrorFailure(auth, official, config)
            && !Installer.IsPublicGitMirrorFailure(auth, "https://custom.example/https://github.com/", config),
            "PyPI permission failures official Git auth and custom gateways do not receive the public-mirror exception");
        Check(!Installer.IsPublicGitMirrorFailure(auth, mirror, custom), "an explicit custom Git preference keeps its authentication failure actionable");
        var privateConfig = config.Clone(); privateConfig.CustomRepository = "https://github.com/private/environment.git";
        Check(!Installer.IsPublicGitMirrorFailure(auth, mirror, privateConfig), "a user-supplied repository does not inherit the built-in public Git authentication exception");

        var attempts = new List<(string Index, string Git)>();
        var resolved = await ComfyDependencyResolver.WithPackageSourcesAsync(["https://pypi.org/simple"], [mirror, official], (index, git) =>
        {
            attempts.Add((index, git));
            return git == mirror ? Task.FromException<bool>(wrapped) : Task.FromResult(true);
        }, default, config: config);
        Check(resolved && attempts.Count == 2 && attempts[0].Index == attempts[1].Index && attempts[1].Git == official,
            "Git mirror authentication fallback changes Git only and preserves the package index");
        attempts.Clear();
        try
        {
            await ComfyDependencyResolver.WithPackageSourcesAsync(["https://pypi.org/simple", "https://second.example/simple"], [mirror, official], (index, git) =>
            { attempts.Add((index, git)); return Task.FromException<bool>(pypi); }, default, config: config);
            throw new Exception("PyPI permission failure retried");
        }
        catch (CommandFailureException error) when (ReferenceEquals(error, pypi))
        { Check(attempts.Count == 1, "PyPI 403 remains terminal even when a public Git fallback is available"); }
        return count;
    }
}
