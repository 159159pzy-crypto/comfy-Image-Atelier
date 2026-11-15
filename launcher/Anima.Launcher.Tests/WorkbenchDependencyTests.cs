using System.Text.Json;
using Anima.Launcher.Core;

internal static class WorkbenchDependencyTests
{
    public static async Task<int> Run(string work, string repository)
    {
        if (!OperatingSystem.IsWindows()) return 0;
        var count = 0;
        void Check(bool value, string label)
        { if (!value) throw new Exception("FAIL: " + label); count++; Console.WriteLine("PASS: " + label); }
        var host = Path.Combine(repository, ".venv", "Scripts", "python.exe");
        if (!File.Exists(host)) host = Commands.Find("python.exe") ?? throw new FileNotFoundException("Python is required for dependency probe regressions.");
        var root = Path.Combine(work, "workbench-contract");
        var comfy = Path.Combine(root, "ComfyUI");
        var app = Path.Combine(root, "selected-workbench");
        Directory.CreateDirectory(comfy); Directory.CreateDirectory(app);
        var venv = Path.Combine(comfy, ".venv");
        await Commands.Run(host, ["-m", "venv", "--without-pip", venv], root, null, default);
        var python = Path.Combine(venv, "Scripts", "python.exe");
        var site = JsonSerializer.Deserialize<string>(await Commands.Run(python,
            ["-B", "-c", "import json,sysconfig; print(json.dumps(sysconfig.get_path('purelib')))"], root, null, default))!;
        var config = new LauncherConfig { ComfyRoot = comfy, Python = python, AppRoot = app, RuntimeRoot = Path.Combine(root, "runtime") };
        File.WriteAllText(Path.Combine(comfy, "main.py"), "# fixture\n");
        File.WriteAllText(Path.Combine(app, "pyproject.toml"), "[project]\ndependencies=['aiohttp>=3.14.5,<4', 'multidict>=6.9.1,<7', 'Pillow>=13,<14']\n");
        File.WriteAllText(Path.Combine(app, "run.py"), "raise AssertionError('An unready workbench must never be started')\n");
        File.WriteAllText(Path.Combine(comfy, "requirements.txt"), "core-fixture>=1\n-r nested/core.txt\n-c constraints.txt\n");
        Directory.CreateDirectory(Path.Combine(comfy, "nested"));
        File.WriteAllText(Path.Combine(comfy, "nested", "core.txt"), "-r ../requirements.txt\nnever-installed; sys_platform == 'never-a-platform'\n");
        File.WriteAllText(Path.Combine(comfy, "constraints.txt"), "not-required==9\ncore-fixture<3\n");
        File.WriteAllText(Path.Combine(comfy, "manager_requirements.txt"), "manager-fixture==1\n");
        var runtime = JsonFile.Read<RuntimeManifest>(Path.Combine(repository, "launcher", "manifests", "launcher-manifest.json"));
        var node = new Repository("probe-fixture-node", "https://github.com/example/probe-fixture-node", new string('a', 40), []);
        runtime = runtime with { Nodes = [node] };
        var nodeDir = Path.Combine(comfy, "custom_nodes", node.Id);
        Directory.CreateDirectory(nodeDir);
        File.WriteAllText(Path.Combine(nodeDir, "requirements.txt"), "node-fixture[probe]>=1\n");
        foreach (var module in new[] { "aiohttp", "yaml", "torch" })
            File.WriteAllText(Path.Combine(site, module + ".py"), "raise AssertionError('Dependency review must not execute this module')\n");
        var tools = await Commands.Run(python, ["-B", "-c", "import importlib.util; print(importlib.util.find_spec('pip') is None and importlib.util.find_spec('packaging') is None)"], root, null, default);
        Check(tools.Trim() == "True", "probe fixture has neither pip nor packaging installed");
        var missing = await SetupImportReview.InspectAsync(config, runtime, default);
        Check(missing.NeedsRepair && missing.MissingCoreModules.Length == 0
            && missing.DependencyIssues.Any(issue => issue.Requirement.StartsWith("node-fixture") && issue.State == "missing")
            && missing.DependencyIssues.Any(issue => issue.Requirement.StartsWith("Pillow") && issue.State == "missing"),
            "core module and node directory presence cannot hide missing workbench or node distributions");
        Check(!missing.DependencyIssues.Any(issue => issue.Requirement.StartsWith("not-required") || issue.Requirement.StartsWith("never-installed")),
            "constraint-only packages and inactive platform markers do not become installation requirements");

        void Package(string name, string version, params string[] requires)
        {
            var directory = Path.Combine(site, name.Replace('-', '_') + "-fixture.dist-info");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "METADATA"), "Metadata-Version: 2.1\nName: " + name + "\nVersion: " + version + "\n"
                + string.Join("", requires.Select(value => "Requires-Dist: " + value + "\n")) + "\n");
            File.WriteAllText(Path.Combine(directory, "RECORD"), "");
        }
        Package("aiohttp", "3.14.5"); Package("multidict", "6.9.1"); Package("Pillow", "13.0");
        Package("core-fixture", "1.5"); Package("manager-fixture", "1");
        Package("node-fixture", "1", "extra-child>=2; extra == 'probe'", "unused-child; extra == 'unused'");
        var extra = await WorkbenchDependencies.InspectAsync(config, runtime, default);
        Check(extra.Issues.Length == 1 && extra.Issues[0].Requirement.StartsWith("extra-child>=2"), "selected extras inspect their transitive requirements without enabling unrelated extras");
        Package("extra-child", "2");
        Check(!(await SetupImportReview.InspectAsync(config, runtime, default)).NeedsRepair,
            "metadata-only inspection accepts all selected dependencies without importing third-party modules");
        Package("aiohttp", "3.14.4"); Package("Pillow", "14.0");
        var versions = await WorkbenchDependencies.InspectAsync(config, runtime, default);
        Check(versions.Issues.Count(issue => issue.State == "incompatible") == 2,
            "workbench lower and upper version bounds are both enforced");
        Package("aiohttp", "3.14.5"); Package("Pillow", "13.0");
        Package("core-fixture", "3");
        Check((await WorkbenchDependencies.InspectAsync(config, runtime, default)).Issues.Any(issue => issue.State == "incompatible" && issue.Source.Contains("constraints.txt")),
            "recursive constraint files validate installed packages without installing unused constraints");
        Package("core-fixture", "1.5");

        var contract = await WorkbenchDependencies.ReadAsync(config, default);
        Check(contract.RequirementsText.Contains("aiohttp>=3.14.5,<4") && contract.Identity.Length == 64,
            "the dependency contract is extracted from the selected workbench pyproject using standard-library TOML");
        var projectFile = Path.Combine(app, "pyproject.toml");
        var originalProject = File.ReadAllText(projectFile);
        File.AppendAllText(projectFile, "requires-python='>=99'\n");
        try { await SetupImportReview.InspectAsync(config, runtime, default); throw new Exception("Expected interpreter compatibility rejection"); }
        catch (InvalidOperationException error)
        { Check(error.Message.Contains(">=99") && error.Message.Contains(config.Python) && error.Message.Contains("独立"),
            "import checks the selected workbench Python range first and reports the chosen interpreter with recovery guidance"); }
        File.WriteAllText(projectFile, originalProject);
        await WorkbenchDependencies.ValidateInterpreterAsync(config, default);
        Check((await Commands.Run(python, ["-B", "-c", "import importlib.util; print(importlib.util.find_spec('pip') is None and importlib.util.find_spec('packaging') is None)"], root, null, default)).Trim() == "True",
            "interpreter and TOML compatibility checks do not install pip or packaging in the target environment");
        File.AppendAllText(Path.Combine(nodeDir, "requirements.txt"), "direct-fixture @ git+https://github.com/example/dependency@main\n-r https://example.invalid/requirements.txt\n");
        Package("direct-fixture", "1");
        var unknown = await SetupImportReview.InspectAsync(config, runtime, default);
        Check(unknown.NeedsRepair && unknown.DependencyIssues.Count(issue => issue.State == "unknown") == 2,
            "mutable direct URLs and remote requirement includes are explicitly unverified without network access");
        var directInfo = Path.Combine(site, "direct_fixture-fixture.dist-info", "direct_url.json");
        var commit = new string('b', 40);
        File.WriteAllText(directInfo, JsonSerializer.Serialize(new { url = "https://github.com/example/dependency", vcs_info = new { vcs = "git", commit_id = commit } }));
        File.WriteAllText(Path.Combine(nodeDir, "requirements.txt"), "direct-fixture @ git+https://github.com/example/dependency@" + commit + "\n");
        Check((await WorkbenchDependencies.InspectAsync(config, runtime, default)).Ready,
            "fixed direct VCS requirements accept only a matching installed commit and origin");
        File.WriteAllText(directInfo, JsonSerializer.Serialize(new { url = "https://github.com/example/dependency", vcs_info = new { vcs = "git", commit_id = new string('c', 40) } }));
        Check((await WorkbenchDependencies.InspectAsync(config, runtime, default)).Issues.Any(issue => issue.State == "incompatible"),
            "a different installed direct VCS commit requires repair");
        Package("SAM-2", "1.0");
        var samOrigin = Path.Combine(site, "SAM_2-fixture.dist-info", "direct_url.json");
        File.WriteAllText(samOrigin, JsonSerializer.Serialize(new { url = "https://github.com/facebookresearch/sam2",
            vcs_info = new { vcs = "git", commit_id = runtime.Sam2Commit } }));
        var samSource = "git+https://github.com/facebookresearch/sam2\n";
        File.WriteAllText(Path.Combine(nodeDir, "requirements.txt"), samSource);
        Check((await WorkbenchDependencies.InspectAsync(config, runtime, default)).Ready
            && File.ReadAllText(Path.Combine(nodeDir, "requirements.txt")) == samSource,
            "the installer and metadata probe share SAM2 normalization without editing upstream declarations");
        var commentedSamSource = "SAM-2 @ git+https://github.com/facebookresearch/sam2@" + new string('f', 40) + " # pinned by upstream\n";
        File.WriteAllText(Path.Combine(nodeDir, "requirements.txt"), commentedSamSource);
        var normalizedSam = Installer.PinSam2(commentedSamSource, runtime.Sam2Commit);
        var preparedSamFile = ComfyDependencyResolver.PrepareNodeRequirements(Path.Combine(nodeDir, "requirements.txt"),
            Path.Combine(root, "commented-sam-requirements"), runtime.Sam2Commit);
        Check(normalizedSam == "SAM-2 @ git+https://github.com/facebookresearch/sam2@" + runtime.Sam2Commit + " # pinned by upstream\n"
            && preparedSamFile != Path.Combine(nodeDir, "requirements.txt") && File.ReadAllText(preparedSamFile) == normalizedSam,
            "installer input with a trailing comment is actually rewritten to the same fixed SAM2 commit as the probe");
        Check((await WorkbenchDependencies.InspectAsync(config, runtime, default)).Ready
            && File.ReadAllText(Path.Combine(nodeDir, "requirements.txt")) == commentedSamSource,
            "commented SAM2 declarations accept the installed locked commit without modifying the original source file");
        File.WriteAllText(samOrigin, JsonSerializer.Serialize(new { url = "https://github.com/facebookresearch/sam2",
            vcs_info = new { vcs = "git", commit_id = new string('f', 40) } }));
        var wrongSam = await WorkbenchDependencies.InspectAsync(config, runtime, default);
        Check(wrongSam.Issues.Single() is { State: "incompatible" } issue && issue.Source.EndsWith("requirements.txt:1"),
            "commented managed SAM2 still rejects the wrong recorded commit and retains the original source line");
        File.Delete(samOrigin);
        Check((await WorkbenchDependencies.InspectAsync(config, runtime, default)).Issues.Single().State == "unknown",
            "managed SAM2 without recorded provenance remains explicitly unverified");
        const string unknownSource = "git+https://github.com/example/unmanaged@main # unrelated repository\n";
        File.WriteAllText(Path.Combine(nodeDir, "requirements.txt"), unknownSource);
        Check((await WorkbenchDependencies.InspectAsync(config, runtime, default)).Issues.Single().State == "unknown"
            && Installer.PinSam2(unknownSource, runtime.Sam2Commit) is null
            && Installer.PinSam2("git+https://github.com/facebookresearch/sam2#egg=SAM-2", runtime.Sam2Commit) is null,
            "comment handling never normalizes unknown VCS sources or mistakes URL fragments for comments");
        var external = config.Clone();
        external.ComfyRoot = Path.Combine(root, "other-comfy"); Directory.CreateDirectory(external.ComfyRoot);
        File.WriteAllText(Path.Combine(external.ComfyRoot, "requirements.txt"), "");
        Check(!(await SetupImportReview.InspectAsync(external, runtime with { Nodes = [] }, default)).NeedsRepair,
            "a complete external interpreter remains eligible for connection without package mutation");
        Package("multidict", "6.8");
        try { await SetupImportReview.InspectAsync(external, runtime with { Nodes = [] }, default); throw new Exception("Expected shared interpreter rejection"); }
        catch (InvalidOperationException error) { Check(error.Message.Contains("独立") && error.Message.Contains("multidict"), "an incompatible external interpreter receives precise independent-environment repair guidance"); }
        using var port = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        port.Start(); config.WebPort = ((System.Net.IPEndPoint)port.LocalEndpoint).Port; port.Stop();
        var services = new ServiceManager(Path.Combine(root, "start-state"), _ => { });
        foreach (var softwareUpdate in new[] { false, true })
        {
            try
            {
                if (softwareUpdate) await services.StartWebUIForSoftwareUpdateAsync(config, runtime with { Nodes = [] }, default);
                else await services.StartWebUI(config, runtime with { Nodes = [] }, default);
                throw new Exception("Expected dependency gate before workbench process startup");
            }
            catch (InvalidOperationException error)
            {
                Check(error.Message.Contains("工作台依赖尚未就绪") && error.Message.Contains("multidict"),
                    softwareUpdate ? "software update validates the target workbench contract before starting its process"
                        : "legacy-plan startup detects missing workbench dependencies before launching ComfyUI or the workbench");
            }
        }
        return count;
    }
}
