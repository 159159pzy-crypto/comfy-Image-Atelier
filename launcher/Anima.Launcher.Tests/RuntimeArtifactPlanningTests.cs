using System.Text.Json;
using Anima.Launcher.Core;

internal static class RuntimeArtifactPlanningTests
{
    public static async Task<int> Run(string work, RuntimeManifest runtime)
    {
        var count = 0;
        void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); count++; Console.WriteLine("PASS: " + label); }
        async Task Reject(Func<Task> operation, string label)
        {
            try { await operation(); }
            catch (InvalidOperationException) { Check(true, label); return; }
            catch (InvalidDataException) { Check(true, label); return; }
            throw new Exception("Expected rejection: " + label);
        }
        var directory = Path.Combine(work, "runtime-artifacts");
        var config = new LauncherConfig { ComfyRoot = Path.Combine(directory, "ComfyUI"), RuntimeRoot = Path.Combine(directory, "runtime"),
            Python = Path.Combine(directory, "ComfyUI", ".venv", "Scripts", "python.exe") };
        var original = new InstallationPlan(JsonSerializer.Serialize(config, JsonFile.Options), config.ComfyRoot, config.RuntimeRoot, "anima", true, 0, 0, 0, [])
        { ResolvedRuntime = runtime, RuntimeArtifactsIdentity = RuntimeArtifactPlanning.Identity };
        Task<RuntimeArtifactHost> Host(LauncherConfig _, RuntimeManifest __, bool ___, CancellationToken ____) =>
            Task.FromResult(new RuntimeArtifactHost("cp311", "rtx40", "NVIDIA RTX 4060", "", false));
        var prepared = await RuntimeArtifactPlanning.PrepareAsync(original, runtime, default, Host);
        var catalog = NvidiaRuntimePreparation.Catalog();
        Check(prepared.RuntimeArtifactsPrepared && prepared.FrozenRuntimeArtifacts.Count(item => item.Id.StartsWith("nvidia-vfx")) == 1
            && prepared.FrozenRuntimeArtifacts.Any(item => item.FileName.Contains("cp311-cp311")),
            "native planning freezes exactly the selected ABI rather than all three NVIDIA wheels");
        Check(prepared.FrozenRuntimeArtifacts.Any(item => item.Sha256 == catalog.DlssArchive.Sha256)
            && prepared.FrozenRuntimeArtifacts.Any(item => item.Sha256 == catalog.VcRuntime.Sha256)
            && !prepared.FrozenRuntimeArtifacts.Any(item => item.FileName.EndsWith(".rar"))
            && prepared.FrozenRuntimeArtifacts.Any(item => item.Id == SesquiRuntimePreparation.Weight.Id),
            "RTX 40 plans include the base DLL archive, VC runtime, and mandatory Sesqui weight without unrelated GPU archives");
        Check(prepared.Downloads.Length == prepared.FrozenRuntimeArtifacts.Length && prepared.EstimatedDownloadBytes == prepared.FrozenRuntimeArtifacts.Sum(item => item.Size)
            && prepared.EnvironmentReserveBytes > 2L * 1024 * 1024 * 1024 && prepared.RuntimeReserveBytes > 2L * 1024 * 1024 * 1024,
            "all frozen native artifacts contribute download bytes and both installation and extraction disk reserves");
        var resumed = JsonSerializer.Deserialize<InstallationPlan>(JsonSerializer.Serialize(prepared, JsonFile.Options), JsonFile.Options)!;
        var repeat = await RuntimeArtifactPlanning.PrepareAsync(resumed, runtime, default, (_, _, _, _) => throw new Exception("frozen host must not be reselected"));
        Check(repeat.RuntimeArtifactsIdentity == prepared.RuntimeArtifactsIdentity
            && repeat.Downloads.Select(item => (item.Destination, item.Sha256)).SequenceEqual(prepared.Downloads.Select(item => (item.Destination, item.Sha256)))
            && repeat.EnvironmentReserveBytes == prepared.EnvironmentReserveBytes, "resuming a prepared plan preserves its resources without doubling reserve or changing host selection");
        var legacy = original with { RuntimeArtifactsIdentity = "" };
        Check(ReferenceEquals(await RuntimeArtifactPlanning.PrepareAsync(legacy, runtime, default, (_, _, _, _) => throw new Exception("legacy host probe")), legacy),
            "legacy plans without a native snapshot do not silently gain new runtime downloads");
        await Reject(() => RuntimeArtifactPlanning.PrepareAsync(original with { RuntimeArtifactsIdentity = new string('0', 64) }, runtime, default, Host),
            "a different embedded native catalog refuses the stale plan and preserves the existing installation");
        var withoutNvidia = await RuntimeArtifactPlanning.PrepareAsync(original, runtime, default,
            (_, _, _, _) => Task.FromResult(new RuntimeArtifactHost("cp311", "", "", "", false)));
        Check(withoutNvidia.FrozenRuntimeArtifacts.Select(item => item.Id).SequenceEqual([SesquiRuntimePreparation.Weight.Id]),
            "unsupported hardware receives only required base-generation assets and no NVIDIA disk reservation");
        var rtx30 = await RuntimeArtifactPlanning.PrepareAsync(original, runtime, default,
            (_, _, _, _) => Task.FromResult(new RuntimeArtifactHost("cp310", "rtx30", "NVIDIA RTX 3060", "", true)));
        Check(rtx30.FrozenRuntimeArtifacts.Any(item => item.FileName.Contains("cp310-cp310"))
            && rtx30.FrozenRuntimeArtifacts.Any(item => item.FileName == "RTX30xx.rar")
            && rtx30.FrozenRuntimeArtifacts.Any(item => item.Sha256 == catalog.SevenZipBootstrap.Sha256)
            && !rtx30.FrozenRuntimeArtifacts.Any(item => item.Sha256 == catalog.VcRuntime.Sha256),
            "RTX 30 import chooses its ABI and GPU DLL plus portable 7-Zip, while reusing installed VC runtime");
        var noModels = original with { Fresh = false, ModelIds = [] };
        var noModelsPrepared = await RuntimeArtifactPlanning.PrepareAsync(noModels, runtime, default, Host);
        Check(noModelsPrepared.EstimatedDownloadBytes > 0, "native planning also covers imported environments with no selected model downloads");

        var sesqui = Path.Combine(config.ComfyRoot, "custom_nodes", "SesquiLSR");
        Directory.CreateDirectory(Path.Combine(sesqui, "models"));
        File.WriteAllText(Path.Combine(sesqui, "nodes.py"), "class SesquiLatentUpscale:\n    pass\n");
        Check((await SesquiRuntimePreparation.FilesIssuesAsync(config, runtime, default)).Any(issue => issue.Contains("upscaler_Wan21")),
            "registered Sesqui nodes cannot pass readiness when their actual Wan 2.1 weight is missing");
        File.WriteAllText(Path.Combine(sesqui, "models", SesquiRuntimePreparation.Weight.FileName), "version https://git-lfs.github.com/spec/v1\n");
        Check((await SesquiRuntimePreparation.FilesIssuesAsync(config, runtime, default)).Length > 0,
            "an unresolved LFS pointer or damaged weight fails SHA-256 validation before first generation");
        ForbiddenVisionCompatibility.ValidatePlannedVersion(ForbiddenVisionCompatibility.NodeId, ForbiddenVisionCompatibility.SupportedCommit);
        await Reject(() => { ForbiddenVisionCompatibility.ValidatePlannedVersion(ForbiddenVisionCompatibility.NodeId, new string('a', 40)); return Task.CompletedTask; },
            "an unsupported Forbidden Vision release fails at planning instead of late installation");
        return count;
    }
}
