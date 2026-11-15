using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Anima.Launcher.Core;

internal static class ComfyReleaseUpdateTests
{
    public static async Task<int> Run(string work, RuntimeManifest runtime)
    {
        var passed = 0;
        void Check(bool result, string text) { if (!result) throw new Exception("FAIL: " + text); passed++; Console.WriteLine("PASS: " + text); }
        async Task Rejected(Func<Task> action, string text)
        {
            try { await action(); } catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException) { Check(true, text); return; }
            throw new Exception("FAIL: " + text);
        }
        Check(!SetupRouting.OpenWizardFromEnvironmentPage(new LauncherConfig { ComfyRoot = work }, false)
            && SetupRouting.OpenWizardFromEnvironmentPage(new LauncherConfig(), false)
            && SetupRouting.OpenWizardFromEnvironmentPage(new LauncherConfig { ComfyRoot = work, InstallPending = true }, false)
            && SetupRouting.OpenWizardFromEnvironmentPage(new LauncherConfig { ComfyRoot = work }, true),
            "configured environments reach maintenance while new and unfinished installations retain the wizard route");
        const string sha = "1111111111111111111111111111111111111111";
        var requests = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            requests.Add(request.RequestUri!.AbsoluteUri);
            return Json(request.RequestUri.AbsolutePath.EndsWith("/releases/latest")
                ? "{\"tag_name\":\"v1.2.3\",\"draft\":false,\"prerelease\":false,\"published_at\":\"2026-10-20T00:00:00Z\"}"
                : request.RequestUri.AbsolutePath.EndsWith("requirements.txt") ? "numpy>=1.0\n" : "{\"sha\":\"" + sha + "\"}");
        }));
        var client = new ComfyReleases(http);
        using var nodeHttp = new HttpClient(new Handler(request => Json(request.RequestUri!.AbsolutePath.Contains("/commits/")
            ? "{\"sha\":\"" + (request.RequestUri.AbsolutePath.Contains("ComfyUI-Prompt-Assistant") ? PromptAssistantCompatibility.SupportedCommit
                : request.RequestUri.AbsolutePath.Contains("ComfyUI-Forbidden-Vision") ? ForbiddenVisionCompatibility.SupportedCommit : sha) + "\"}" : "{\"default_branch\":\"main\"}")));
        var nodeReleases = new NodeReleases(nodeHttp);
        var latest = await client.LatestAsync(default);
        Check(latest.Tag == "v1.2.3" && latest.Commit == sha, "official stable release is resolved to a complete immutable SHA");
        Check(requests.All(url => url.StartsWith("https://api.github.com/", StringComparison.Ordinal)), "version authority stays on the official GitHub API");
        using var restricted = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.Host == "api.github.com") return new(HttpStatusCode.Forbidden);
            return new(HttpStatusCode.OK) { RequestMessage = new(HttpMethod.Get, "https://github.com/Comfy-Org/ComfyUI/releases/tag/v1.2.4"), Content = new StringContent("") };
        }));
        var tagRequested = "";
        var fallback = await new ComfyReleases(restricted, (tag, _) => { tagRequested = tag; return Task.FromResult(sha); }).LatestAsync(default);
        Check(fallback.Tag == "v1.2.4" && tagRequested == "v1.2.4" && fallback.Commit == sha, "403 uses official latest redirect then verifies the tag SHA");
        using var prerelease = new HttpClient(new Handler(_ => Json("{\"tag_name\":\"v1.3.0\",\"draft\":false,\"prerelease\":true}")));
        await Rejected(() => new ComfyReleases(prerelease).LatestAsync(default), "prerelease responses never enter the stable channel");
        Check(!ComfyReleases.StableTag("v1.2.3-rc1") && !ComfyReleases.StableTag("nightly") && ComfyReleases.StableTag("v0.42.1"), "stable tag validation excludes nightly and candidate versions");
        using var report = JsonDocument.Parse("{\"install\":[{\"metadata\":{\"name\":\"NumPy\",\"version\":\"2.0.0\"}},{\"metadata\":{\"name\":\"comfyui_manager\",\"version\":\"4.2.2\"}}]}");
        var pins = ComfyDependencyResolver.MergeConstraints(["numpy==1.0.0", "torch==2.13.0+cu130"], report.RootElement);
        Check(pins.Contains("numpy==2.0.0") && pins.Contains("comfyui-manager==4.2.2") && pins.Contains("torch==2.13.0+cu130"), "resolved dependency changes override previous packages while preserving the CUDA ABI");

        var directory = Path.Combine(work, "comfy-release-update");
        Directory.CreateDirectory(directory);
        var planConfig = new LauncherConfig { ComfyRoot = Path.Combine(directory, "fresh"), RuntimeRoot = Path.Combine(directory, "runtime") };
        var plan = new InstallationPlan(JsonSerializer.Serialize(planConfig, JsonFile.Options), planConfig.ComfyRoot, planConfig.RuntimeRoot, "anima", true, 0, 0, 0, []);
        var stable = await InstallationPlanner.ResolveStableAsync(plan, runtime, directory, default, client, nodeReleases);
        using var offline = new HttpClient(new Handler(_ => throw new HttpRequestException("offline")));
        var resumed = await InstallationPlanner.ResolveStableAsync(plan, runtime, directory, default, new ComfyReleases(offline));
        Check(stable.Release == resumed.Release && resumed.Dependencies!.RequirementsText == stable.Dependencies!.RequirementsText,
            "a restarted offline install reuses its saved release and dependency text without drifting to a newer release");
        InstallationPlanner.MarkStableInstallComplete(planConfig, directory);
        var beforeRequests = requests.Count;
        _ = await InstallationPlanner.ResolveStableAsync(plan, runtime, directory, default, client, nodeReleases);
        Check(requests.Count > beforeRequests, "a new installation after removing a completed environment checks latest stable again");

        var updateConfig = planConfig.Clone(); updateConfig.Python = Path.Combine(planConfig.ComfyRoot, ".venv", "Scripts", "python.exe");
        var plans = new ComfyUpdatePlans(directory);
        var exactDependencies = new ComfyDependencySnapshot(sha, "numpy>=1\n", "comfyui_manager==4.2.2\n", ["numpy==2.0.0", "torch==2.11.0+cu128"])
            { Resolved = true, PythonIdentity = "fixture-python-identity", NodeRequirementsIdentity = "fixture-node-identity" };
        plans.Save(updateConfig, latest, exactDependencies);
        var restartedPlan = new ComfyUpdatePlans(directory).Read(updateConfig);
        Check(restartedPlan?.Release == latest && restartedPlan.Dependencies.Constraints.SequenceEqual(exactDependencies.Constraints)
            && restartedPlan.Dependencies.PythonIdentity == exactDependencies.PythonIdentity, "manual update restart restores the exact release and resolved dependency snapshot without network access");
        var changedPython = updateConfig.Clone(); changedPython.Python = Path.Combine(directory, "another-python.exe");
        Check(plans.Read(changedPython) is null && plans.Read(updateConfig) is not null, "saved manual plans bind the selected interpreter and never follow an environment switch");
        Check(plans.ResetDependencyReview(changedPython) is null && plans.Read(updateConfig)!.Dependencies.Resolved,
            "recovery of a different interpreter cannot invalidate a saved update review");
        var repairedPlan = plans.ResetDependencyReview(updateConfig)!;
        Check(repairedPlan.Release == restartedPlan!.Release && repairedPlan.CreatedAt == restartedPlan.CreatedAt
            && repairedPlan.Dependencies.ComfyCommit == exactDependencies.ComfyCommit
            && repairedPlan.Dependencies.RequirementsText == exactDependencies.RequirementsText
            && repairedPlan.Dependencies.ManagerRequirementsText == exactDependencies.ManagerRequirementsText,
            "dependency repair preserves the exact reviewed source release and its declarations");
        Check(!repairedPlan.Dependencies.Resolved && repairedPlan.Dependencies.Constraints.Length == 0
            && repairedPlan.Dependencies.Packages.Length == 0 && repairedPlan.Dependencies.EnvironmentPackages.Length == 0
            && new ComfyUpdatePlans(directory).Read(updateConfig)!.Dependencies.PythonIdentity.Length == 0,
            "a repaired environment discards the outdated dependency baseline before retrying the same target");
        plans.Save(updateConfig, latest, exactDependencies with { Resolved = false, Constraints = [] });
        Check(new ComfyUpdatePlans(directory).Read(updateConfig)?.Dependencies.Resolved == false, "an interrupted dependency review remains explicitly incomplete after restart");
        plans.Clear(updateConfig);
        Check(plans.Read(updateConfig) is null, "successful update or rollback can clear the saved manual plan");

        var lockRoot = Path.Combine(directory, "interrupted-fetch");
        Directory.CreateDirectory(Path.Combine(lockRoot, ".git"));
        var shallowLock = Path.Combine(lockRoot, ".git", "shallow.lock");
        try
        {
            await GitFetchRecovery.ExecuteAsync(lockRoot, async () => { await Task.Delay(80); File.WriteAllText(shallowLock, "interrupted shallow state"); throw new TimeoutException("fixture timeout"); }, gitIsActive: () => false);
        }
        catch (TimeoutException) { }
        Check(!File.Exists(shallowLock) && Directory.EnumerateFiles(Path.Combine(lockRoot, ".git"), "shallow.lock.anima-interrupted-*").Count() == 1
            && await GitFetchRecovery.ExecuteAsync(lockRoot, () => Task.FromResult("retried"), gitIsActive: () => false) == "retried",
            "a timed out fetch preserves its newly created shallow lock and allows the next attempt");
        File.WriteAllText(shallowLock, "preexisting user lock");
        var invoked = false;
        await Rejected(() => GitFetchRecovery.ExecuteAsync(lockRoot, () => { invoked = true; return Task.FromResult(""); }, gitIsActive: () => false), "a preexisting shallow lock blocks the fetch without moving it");
        Check(!invoked && File.ReadAllText(shallowLock) == "preexisting user lock", "existing Git lock identity and bytes remain unchanged");
        File.Move(shallowLock, shallowLock + ".fixture-preserved");
        var git = Commands.Find("git.exe") ?? Commands.Find("git") ?? throw new Exception("Git is required for update rollback fixtures.");
        var waitingGitInfo = Commands.Info(git, ["hash-object", "--stdin"], lockRoot);
        waitingGitInfo.RedirectStandardInput = true;
        using (var waitingGit = System.Diagnostics.Process.Start(waitingGitInfo)!)
        {
            try
            {
                await Rejected(() => GitFetchRecovery.ExecuteAsync(lockRoot, async () => { await Task.Delay(80); File.WriteAllText(shallowLock, "active Git lock"); throw new TimeoutException("fixture timeout"); }),
                    "a real surviving Git process prevents lock recovery after timeout");
                Check(!waitingGit.HasExited && File.ReadAllText(shallowLock) == "active Git lock", "the live Git process and its possible lock are both left untouched");
            }
            finally { waitingGit.StandardInput.Close(); await waitingGit.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        }
        async Task<string> Git(string root, params string[] arguments) => (await Commands.Run(git, arguments, root, null, default)).Trim();
        var source = Path.Combine(directory, "source");
        var environment = Path.Combine(directory, "environment");
        Directory.CreateDirectory(source);
        await Git(source, "init");
        await Git(source, "config", "user.name", "Launcher fixture");
        await Git(source, "config", "user.email", "fixture@example.invalid");
        File.WriteAllText(Path.Combine(source, ".gitignore"), ".venv/\nmodels/\ncustom_nodes/\nuser/\n.anima-*\n");
        File.WriteAllText(Path.Combine(source, "main.py"), "old source\n");
        File.WriteAllText(Path.Combine(source, "config.yml"), "fixture: old\n");
        File.WriteAllText(Path.Combine(source, "shape"), "old file\n");
        Directory.CreateDirectory(Path.Combine(source, "reverse"));
        File.WriteAllText(Path.Combine(source, "reverse", "item.py"), "old nested source\n");
        File.WriteAllText(Path.Combine(source, "requirements.txt"), "numpy>=1\n");
        File.WriteAllText(Path.Combine(source, "manager_requirements.txt"), "comfyui_manager==4.2.2\n");
        Directory.CreateDirectory(Path.Combine(source, "comfy"));
        File.WriteAllText(Path.Combine(source, "comfy", "cli_args.py"), "# verified target parser fixture\n");
        File.WriteAllText(Path.Combine(source, ".anima-revision.json"), "{\"fixture\":\"tracked marker stays under Git ownership\"}");
        await Git(source, "add", ".");
        await Git(source, "add", "--force", ".anima-revision.json");
        await Git(source, "commit", "-m", "old fixture");
        var before = await Git(source, "rev-parse", "HEAD");
        File.WriteAllText(Path.Combine(source, "main.py"), "new source\n");
        File.WriteAllText(Path.Combine(source, "config.yml"), "fixture: new\n");
        File.WriteAllText(Path.Combine(source, "new_module.py"), "new module\n");
        File.Delete(Path.Combine(source, "shape"));
        Directory.CreateDirectory(Path.Combine(source, "shape"));
        File.WriteAllText(Path.Combine(source, "shape", "item.py"), "new nested source\n");
        File.Delete(Path.Combine(source, "reverse", "item.py"));
        Directory.Delete(Path.Combine(source, "reverse"));
        File.WriteAllText(Path.Combine(source, "reverse"), "new file\n");
        await Git(source, "add", ".");
        await Git(source, "commit", "-m", "new fixture");
        var after = await Git(source, "rev-parse", "HEAD");
        await Git(directory, "clone", source, environment);
        await Git(environment, "checkout", "--detach", before);
        var python = Path.Combine(environment, ".venv", "Scripts", "python.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(python)!);
        File.WriteAllText(python, "fixture python, never executed");
        File.WriteAllText(Path.Combine(environment, ".venv", "pyvenv.cfg"), "include-system-site-packages = false\n");
        var package = Path.Combine(environment, ".venv", "package.txt");
        File.WriteAllText(package, "old dependency");
        Directory.CreateDirectory(Path.Combine(environment, "models"));
        File.WriteAllText(Path.Combine(environment, "models", "keep.bin"), "user model");
        Directory.CreateDirectory(Path.Combine(environment, "custom_nodes", "user-plugin"));
        File.WriteAllText(Path.Combine(environment, "custom_nodes", "user-plugin", "keep.py"), "user plugin");
        var settings = Path.Combine(environment, "user", "default", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        File.WriteAllText(settings, "original settings");
        var managerDatabase = Path.Combine(environment, "user", "manager.db");
        File.WriteAllBytes(managerDatabase, [0, 1, 2, 3]);
        var modelPaths = Path.Combine(environment, "extra_model_paths.yaml");
        File.WriteAllText(modelPaths, "original model paths");
        var ports = FreePorts();
        var config = new LauncherConfig { ComfyRoot = environment, Python = python, RuntimeRoot = Path.Combine(directory, "runtime-update"), ComfyPort = ports.Item1, WebPort = ports.Item2 };
        var coordinator = new ComfyUpdateCoordinator(directory, git, null, async (root, commit, token) => { await Commands.Run(git, ["fetch", source, commit], root, null, token); },
            (target, token) => Task.CompletedTask);
        var beforePython = config.Clone(); beforePython.Python = "";
        await coordinator.EnsureStoppedAsync(beforePython, default);
        Check(true, "fresh installation can inspect running processes before its Python path exists");
        Check(ComfyUpdateCoordinator.CommandTargetsEnvironment("python.exe \"" + Path.Combine(environment, "main.py") + "\" --port 44321", config)
            && !ComfyUpdateCoordinator.CommandTargetsEnvironment("python.exe \"" + Path.Combine(source, "main.py") + "\" --port 44321", config),
            "external process detection matches the environment's absolute script independently of its port");
        passed += await ComfyLightUpdateTests.Run(directory, runtime, git, source, config, before, after);
        return passed;
    }

    private static (int, int) FreePorts()
    {
        using var first = new TcpListener(IPAddress.Loopback, 0); first.Start();
        using var second = new TcpListener(IPAddress.Loopback, 0); second.Start();
        return (((IPEndPoint)first.LocalEndpoint).Port, ((IPEndPoint)second.LocalEndpoint).Port);
    }
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
    }
}
