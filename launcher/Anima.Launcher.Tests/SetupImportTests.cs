using System.Text.Json;
using Anima.Launcher.Core;

internal static class SetupImportTests
{
    public static async Task<int> Run(string work, string repository, RuntimeManifest runtime, ModelManifest models,
        ModelProfilesManifest profiles)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIP: imported Windows venv review requires Windows");
            return 0;
        }
        var count = 0;
        void Check(bool value, string label)
        { if (!value) throw new Exception("FAIL: " + label); count++; Console.WriteLine("PASS: " + label); }
        async Task RejectExternal(LauncherConfig config, RuntimeManifest manifest, string label)
        {
            try { await SetupImportReview.InspectAsync(config, manifest, default); }
            catch (InvalidOperationException error)
            {
                Check(error.Message.Contains(".venv") && (error.Message.Contains("Python") || error.Message.Contains("解释器")), label);
                return;
            }
            throw new Exception("Expected external environment repair rejection: " + label);
        }
        async Task Reject<T>(Func<Task> operation, string label) where T : Exception
        {
            try { await operation(); }
            catch (T) { Check(true, label); return; }
            throw new Exception("Expected " + typeof(T).Name + ": " + label);
        }

        var bootstrap = Path.Combine(repository, ".venv", "Scripts", "python.exe");
        if (!File.Exists(bootstrap)) bootstrap = Commands.Find("python.exe")
            ?? throw new FileNotFoundException("Setup import regressions require repository .venv or Python on PATH.");
        var directory = Path.Combine(work, "setup-import");
        var comfy = Path.Combine(directory, "ComfyUI");
        Directory.CreateDirectory(comfy);
        var venv = Path.Combine(comfy, ".venv");
        await Commands.Run(bootstrap, ["-m", "venv", "--without-pip", venv], directory, null, default,
            timeout: TimeSpan.FromMinutes(1));
        var python = Path.Combine(venv, "Scripts", "python.exe");
        var site = JsonSerializer.Deserialize<string>(await Commands.Run(python,
            ["-B", "-c", "import json,sysconfig; print(json.dumps(sysconfig.get_path('purelib')))"], comfy, null, default))!;
        var config = new LauncherConfig { ComfyRoot = comfy, Python = python, RuntimeRoot = Path.Combine(directory, "runtime") };
        var noNodes = runtime with { Nodes = [], Tools = [], TorchWheels = [] };
        var node = new Repository("setup-import-fixture", "https://github.com/example/setup-import-fixture", new string('a', 40), []);
        var withNode = noNodes with { Nodes = [node] };
        var nodeDirectory = Path.Combine(comfy, "custom_nodes", node.Id);

        // Use the actual launcher path probe with only ComfyUI's standard-library
        // path interfaces stubbed; there are deliberately no third-party packages.
        Directory.CreateDirectory(Path.Combine(comfy, "comfy"));
        Directory.CreateDirectory(Path.Combine(comfy, "utils"));
        File.WriteAllText(Path.Combine(comfy, "main.py"), "# isolated ComfyUI fixture\n");
        File.WriteAllText(Path.Combine(comfy, "comfy", "__init__.py"), "");
        File.WriteAllText(Path.Combine(comfy, "comfy", "options.py"), "def enable_args_parsing():\n    pass\n");
        File.WriteAllText(Path.Combine(comfy, "utils", "__init__.py"), "");
        File.WriteAllText(Path.Combine(comfy, "utils", "extra_config.py"),
            "import yaml\ndef load_extra_path_config(path):\n    raise AssertionError('A deferred path config must not be guessed')\n");
        File.WriteAllText(Path.Combine(comfy, "folder_paths.py"), """
from pathlib import Path
models_dir = str(Path(__file__).resolve().parent / 'models')
folder_names_and_paths = {key: ([str(Path(models_dir) / key)], set()) for key in ['diffusion_models', 'text_encoders', 'vae', 'loras']}
""");
        var pristine = await SetupImportReview.InspectAsync(config, noNodes, default);
        Check(pristine.NeedsRepair && pristine.MissingCoreModules.Order().SequenceEqual(new[] { "aiohttp", "torch", "yaml" }),
            "a real empty venv reaches import review and schedules dependency repair without importing third-party packages");
        Directory.CreateDirectory(nodeDirectory);
        var existingNodes = await SetupImportReview.InspectAsync(config, withNode, default);
        Check(existingNodes.NeedsRepair && existingNodes.MissingCoreModules.Length == 3,
            "existing node directories do not make a clean venv falsely ready");

        var tinyModels = new ModelManifest(models.SchemaVersion, SetupModelSelection.MandatoryModels(models, profiles, "anima")
            .Select(model => model with { Size = 1, Sha256 = new string('0', 64) }).ToArray());
        var plan = InstallationPlanner.Create(config, noNodes, Path.Combine(directory, "state"), false, tinyModels, profiles, []);
        var script = Path.Combine(repository, "launcher", "scripts", "probe_comfy.py");
        var prepared = await InstallationPlanner.PreparePlanAsync(plan, tinyModels, profiles, script, default);
        Check(!prepared.ModelPathsDeferred && prepared.ModelInventory.Length == tinyModels.Models.Length,
            "without extra model YAML the real path probe inventories a pristine venv using only standard-library modules");
        var yamlPath = Path.Combine(comfy, "extra_model_paths.yaml");
        File.WriteAllText(yamlPath, "external:\n  base_path: ../external-models\n  diffusion_models: diffusion_models\n");
        var deferred = await InstallationPlanner.PreparePlanAsync(plan, tinyModels, profiles, script, default);
        var preflight = await InstallationPlanner.PreflightAsync(deferred, default);
        Check(deferred.ModelPathsDeferred && preflight.CanInstall
            && preflight.Issues.Any(issue => issue.Code == "model_paths_deferred" && !issue.Blocking),
            "missing PyYAML defers configured model paths without blocking dependency installation");
        await Reject<InvalidOperationException>(
            () => InstallationPlanner.PreparePlanAsync(deferred, tinyModels, profiles, script, default, requireModelPaths: true),
            "the same missing PyYAML still blocks strict model download destination resolution");
        File.Delete(yamlPath);

        void AddModules(string target)
        {
            foreach (var name in new[] { "aiohttp", "yaml", "torch" })
                File.WriteAllText(Path.Combine(target, name + ".py"), "raise AssertionError('Review must discover modules without importing them')\n");
        }
        AddModules(site);
        var ready = await SetupImportReview.InspectAsync(config, withNode, default);
        Check(!ready.NeedsRepair && ready.MissingCoreModules.Length == 0,
            "module presence checks do not import torch or execute third-party initialization");
        Directory.Delete(nodeDirectory);
        var missingNode = await SetupImportReview.InspectAsync(config, withNode, default);
        Check(missingNode.NeedsRepair && missingNode.MissingCoreModules.Length == 0,
            "complete core modules still require repair when a declared node is missing");
        Directory.CreateDirectory(nodeDirectory);

        var externalVenv = Path.Combine(directory, "external-python");
        await Commands.Run(bootstrap, ["-m", "venv", "--without-pip", externalVenv], directory, null, default,
            timeout: TimeSpan.FromMinutes(1));
        var external = config.Clone();
        external.Python = Path.Combine(externalVenv, "Scripts", "python.exe");
        var externalSite = JsonSerializer.Deserialize<string>(await Commands.Run(external.Python,
            ["-B", "-c", "import json,sysconfig; print(json.dumps(sysconfig.get_path('purelib')))"], comfy, null, default))!;
        await RejectExternal(external, withNode,
            "an external interpreter missing core dependencies is rejected during review with dedicated-venv guidance");
        AddModules(externalSite);
        var sharedReady = await SetupImportReview.InspectAsync(external, withNode, default);
        Check(!sharedReady.NeedsRepair && sharedReady.MissingCoreModules.Length == 0,
            "a complete external interpreter remains eligible for import without dependency mutations");

        var faceManifest = noNodes with { Nodes = [runtime.Nodes.Single(item => item.Id == ForbiddenVisionCompatibility.NodeId)] };
        var face = Path.Combine(comfy, "custom_nodes", ForbiddenVisionCompatibility.NodeId);
        ForbiddenVisionCompatibilityTests.WriteSource(face);
        var originalFace = File.ReadAllText(Path.Combine(face, "__init__.py"));
        var facePending = await SetupImportReview.InspectAsync(config, faceManifest, default);
        Check(facePending.NeedsRepair && facePending.PreparationIssues.Any(issue => issue.Contains("Forbidden Vision"))
            && File.ReadAllText(Path.Combine(face, "__init__.py")) == originalFace,
            "an existing unpatched face node schedules installer preparation without changing source during review");
        await RejectExternal(external, faceManifest, "a shared interpreter cannot bypass required managed face preparation");
        await ForbiddenVisionCompatibility.CheckOrApply(config, config.RuntimeRoot, face, true, default);
        var missingMarker = await SetupImportReview.InspectAsync(config, faceManifest, default);
        Check(missingMarker.NeedsRepair && missingMarker.PreparationIssues.Any(issue => issue.Contains("标记")),
            "a valid face patch without its download guard still requires installer preparation");
        var skip = Path.Combine(comfy, "custom_nodes", "skip_download_model");
        File.WriteAllText(skip, "managed");
        Check(!(await SetupImportReview.InspectAsync(external, faceManifest, default)).NeedsRepair,
            "a fully prepared face node can be connected using an external interpreter without mutations");
        var patchedFace = File.ReadAllText(Path.Combine(face, "__init__.py"));
        File.AppendAllText(Path.Combine(face, "__init__.py"), "\n# user changed source\n");
        Check((await SetupImportReview.InspectAsync(config, faceManifest, default)).NeedsRepair,
            "changed compatibility source cannot reuse an old preparation receipt");
        File.WriteAllText(Path.Combine(face, "__init__.py"), patchedFace);

        var nvidiaNode = runtime.Nodes.Single(item => item.Id == "nvidia-rtx-vsr-pro");
        Directory.CreateDirectory(Path.Combine(comfy, "custom_nodes", nvidiaNode.Id));
        var nvidiaManifest = noNodes with { Nodes = [nvidiaNode] };
        const string cuda = "__version__ = 'fixture'\nclass cuda:\n    @staticmethod\n    def is_available(): return True\n    @staticmethod\n    def get_device_name(index): return 'NVIDIA GeForce RTX 4060'\n";
        File.WriteAllText(Path.Combine(site, "torch.py"), cuda);
        File.WriteAllText(Path.Combine(externalSite, "torch.py"), cuda);
        var nativePending = await SetupImportReview.InspectAsync(config, nvidiaManifest, default);
        Check(nativePending.NeedsRepair && nativePending.PreparationIssues.Any(issue => issue.Contains("NVIDIA")),
            "supported NVIDIA hardware cannot bypass missing native runtime preparation merely because nodes exist");
        var plannedGpu = await RuntimeArtifactPlanning.ProbeAsync(config, nvidiaManifest, false, default);
        Check(plannedGpu.TorchCuda == true && plannedGpu.Generation == "rtx40" && plannedGpu.Gpu == "NVIDIA GeForce RTX 4060",
            "native planning uses the imported Torch device instead of a different physical GPU index");
        await RejectExternal(external, nvidiaManifest,
            "a shared interpreter missing NVIDIA verification is rejected before a mutating repair starts");
        File.WriteAllText(Path.Combine(externalSite, "torch.py"), cuda.Replace("return True", "return False"));
        Check(!(await SetupImportReview.InspectAsync(external, nvidiaManifest, default)).NeedsRepair,
            "hardware without the optional NVIDIA capability can connect without a native runtime receipt");
        var plannedCpu = await RuntimeArtifactPlanning.ProbeAsync(external, nvidiaManifest, false, default);
        Check(plannedCpu.TorchCuda == false && plannedCpu.Generation.Length == 0,
            "an imported CPU Torch does not acquire NVIDIA downloads merely because the machine has an RTX card");
        AddModules(site);
        AddModules(externalSite);
        Directory.Delete(nodeDirectory);
        await RejectExternal(external, withNode,
            "an external interpreter needing a missing node is rejected before installation");
        Directory.CreateDirectory(nodeDirectory);
        File.Delete(Path.Combine(site, "yaml.py"));
        var changed = await SetupImportReview.InspectAsync(config, withNode, default);
        Check(changed.NeedsRepair && changed.MissingCoreModules.SequenceEqual(new[] { "yaml" }),
            "rechecking the frozen target notices dependency changes instead of reusing a previous ready decision");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Reject<OperationCanceledException>(() => SetupImportReview.InspectAsync(config, withNode, cancelled.Token),
            "cancelling import review propagates cancellation instead of accepting or repairing the environment");
        return count;
    }
}
