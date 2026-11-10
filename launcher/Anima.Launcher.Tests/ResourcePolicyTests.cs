using System.Text.Json;
using Anima.Launcher.Core;

internal static class ResourcePolicyTests
{
    public static async Task<int> Run(string work, RuntimeManifest runtime)
    {
        var count = 0;
        void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); count++; Console.WriteLine("PASS: " + label); }
        var basic = SetupFeatures.SelectRuntime(runtime, SetupFeatures.Resolve(false, []));
        Check(basic.Nodes.All(node => SetupFeatures.NodeFeature(node.Id) is null), "import selects only core nodes");
        Check(!RuntimeCapabilities.StartupClasses(runtime).Any(name => name.Contains("SeedVR2") || name.Contains("Sesqui") || name.Contains("DLSS") || name.Contains("RTX")), "startup does not require inactive enhancement nodes");
        Check(SetupFeatures.Resolve(true, []).SequenceEqual(["sesqui"]), "new default dual sampling retains Sesqui independently of repair switches");
        Check(!SetupFeatures.Resolve(true, [], "krea2").Contains("sesqui"), "Krea normal base sampling does not add an unused Sesqui dependency");
        Check(SetupFeatures.SelectRuntime(runtime, ["nvidia"]).Nodes.Any(node => node.Id == "nvidia-rtx-vsr-pro")
            && SetupFeatures.SelectRuntime(runtime, ["nvidia"]).Nodes.All(node => node.Id != "DLSS5-Comfyui"), "VSR selection never adds DLSS installation");
        var root = Path.Combine(work, "resource-policy");
        var config = new LauncherConfig { ComfyRoot = Path.Combine(root, "ComfyUI"), AppRoot = Path.Combine(root, "app"), RuntimeRoot = Path.Combine(root, "runtime") };
        Directory.CreateDirectory(config.ComfyRoot);
        await File.WriteAllTextAsync(Path.Combine(config.ComfyRoot, "main.py"), "# fixture\n");
        var source = Path.Combine(config.AppRoot, "anima_webui", "comfy_extension", "atelier_resources");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "__init__.py"), "# version one\n");
        var deferred = ResourceInspectionExtension.Ensure(config, true);
        Check(!deferred.Changed && !Directory.Exists(Path.Combine(config.ComfyRoot, "custom_nodes")), "running external ComfyUI is not modified to install a new extension");
        var installed = ResourceInspectionExtension.Ensure(config, false);
        var target = Path.Combine(config.ComfyRoot, "custom_nodes", ResourceInspectionExtension.DirectoryName, "__init__.py");
        Check(installed.Available && installed.Changed && File.ReadAllText(target) == "# version one\n", "bundled extension is copied and owned by a content receipt");
        Check(!ResourceInspectionExtension.Ensure(config, false).Changed, "identical extension installation is idempotent");
        await File.WriteAllTextAsync(Path.Combine(source, "__init__.py"), "# version two\n");
        Check(!ResourceInspectionExtension.Ensure(config, true).Changed && File.ReadAllText(target).Contains("one"), "running extension update waits for a normal restart");
        Check(ResourceInspectionExtension.Ensure(config, false).Changed && File.ReadAllText(target).Contains("two")
            && Directory.EnumerateDirectories(Path.Combine(config.ComfyRoot, ".anima-extension-backups")).Count() == 1,
            "managed extension update verifies old files and preserves a backup outside custom_nodes");
        await File.WriteAllTextAsync(target, "# user customization\n");
        Check(!ResourceInspectionExtension.Ensure(config, false).Changed && File.ReadAllText(target).Contains("customization"), "user modified extension is never overwritten");

        var plan = new InstallationPlan(JsonSerializer.Serialize(config, JsonFile.Options), config.ComfyRoot, config.RuntimeRoot, "anima", false, 0, 0, 0, [])
        { ResourcePolicyVersion = 1, RuntimeArtifactsIdentity = RuntimeArtifactPlanning.Identity };
        var probes = 0;
        Task<RuntimeArtifactHost> Probe(LauncherConfig _, RuntimeManifest __, bool ___, CancellationToken ____) { probes++; return Task.FromResult(new RuntimeArtifactHost("cp311", "rtx40", "RTX 4090", "", true)); }
        var noFeatures = await RuntimeArtifactPlanning.PrepareAsync(plan, runtime, default, Probe);
        Check(noFeatures.FrozenRuntimeArtifacts.Length == 0 && probes == 0, "disabled enhancements have no runtime downloads or hardware probe");
        var vsr = await RuntimeArtifactPlanning.PrepareAsync(plan with { Features = ["nvidia"] }, runtime, default, Probe);
        var dlss = await RuntimeArtifactPlanning.PrepareAsync(plan with { Features = ["dlss"] }, runtime, default, Probe);
        var catalog = NvidiaRuntimePreparation.Catalog();
        Check(vsr.FrozenRuntimeArtifacts.Any(item => catalog.VfxWheels.Any(wheel => wheel.Sha256 == item.Sha256))
            && vsr.FrozenRuntimeArtifacts.All(item => item.Sha256 != catalog.DlssArchive.Sha256), "VSR budget contains VFX only");
        Check(dlss.FrozenRuntimeArtifacts.Any(item => item.Sha256 == catalog.DlssArchive.Sha256)
            && dlss.FrozenRuntimeArtifacts.All(item => catalog.VfxWheels.All(wheel => wheel.Sha256 != item.Sha256)), "DLSS budget does not require VFX");
        config.Python = Path.Combine(root, "python.exe");
        await File.WriteAllTextAsync(config.Python, "fixture interpreter");
        var interpreter = new FileInfo(config.Python);
        var stateKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            Path.GetFullPath(config.ComfyRoot).ToUpperInvariant())))[..24];
        var stateDirectory = Path.Combine(config.RuntimeRoot, "nvidia-runtime", stateKey);
        var previous = new NvidiaRuntimeStatus(true, true, "rtx40", "legacy verified")
        {
            ComfyRoot = Path.GetFullPath(config.ComfyRoot), Python = Path.GetFullPath(config.Python), Gpu = "RTX 4090",
            PythonIdentity = $"{Path.GetFullPath(config.Python)}|{interpreter.Length}|{interpreter.LastWriteTimeUtc.Ticks}"
        };
        JsonFile.Write(Path.Combine(stateDirectory, "status.json"), previous);
        Check(NvidiaRuntimePreparation.ReadStatus(config, config.RuntimeRoot, ["nvidia"]) is { Ready: true }
            && NvidiaRuntimePreparation.ReadStatus(config, config.RuntimeRoot, ["dlss"]) is { Ready: true },
            "legacy combined NVIDIA verification remains readable for each selected capability");
        JsonFile.Write(Path.Combine(stateDirectory, "status-nvidia.json"), previous with { Ready = false, Features = ["nvidia"] });
        Check(NvidiaRuntimePreparation.ReadStatus(config, config.RuntimeRoot, ["nvidia"]) is { Ready: false }
            && NvidiaRuntimePreparation.ReadStatus(config, config.RuntimeRoot, ["dlss"]) is { Ready: true },
            "a newer VSR failure is not hidden by old success and does not invalidate independent DLSS state");
        return count;
    }
}
