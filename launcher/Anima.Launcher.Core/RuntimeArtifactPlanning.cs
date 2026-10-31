using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Anima.Launcher.Core;

public sealed record RuntimeArtifactHost(string PythonTag, string Generation, string Gpu, string VfxVersion, bool VcRuntime,
    bool? TorchCuda = null);

/// <summary>Freezes conditional native downloads before installation, without importing custom nodes.</summary>
public static class RuntimeArtifactPlanning
{
    private const long GiB = 1024L * 1024 * 1024;
    public static string Identity => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new { Nvidia = NvidiaRuntimePreparation.Catalog(), Sesqui = SesquiRuntimePreparation.Weight }, JsonFile.Options))));

    public static void ValidateIdentity(InstallationPlan plan)
    {
        if (plan.RuntimeArtifactsIdentity.Length > 0 && plan.RuntimeArtifactsIdentity != Identity)
            throw new InvalidOperationException("已确认计划的 NVIDIA/放大运行资源与当前安装包不同。已保留原安装现场与下载，请重新检查安装计划后继续。");
    }

    public static async Task<RuntimeArtifactHost> ProbeAsync(LauncherConfig config, RuntimeManifest runtime, bool fresh, CancellationToken ct)
    {
        var tag = "cp" + string.Join("", runtime.PythonVersion.Split('.').Take(2));
        var version = "";
        var vc = false;
        bool? torchCuda = null;
        var torchGpu = "";
        if (File.Exists(config.Python))
        {
            const string code = """
import ctypes, importlib.metadata as m, json, sys
try: vfx = m.version('nvidia-vfx')
except m.PackageNotFoundError: vfx = ''
vc = False
if sys.platform == 'win32':
    try:
        ctypes.WinDLL('vcruntime140.dll'); ctypes.WinDLL('msvcp140.dll'); vc = True
    except OSError: pass
cuda, gpu = None, ''
try:
    import torch
    cuda = torch.cuda.is_available()
    if cuda: gpu = torch.cuda.get_device_name(0)
except Exception: pass
print(json.dumps(dict(pythonTag='cp'+str(sys.version_info.major)+str(sys.version_info.minor), generation='', gpu=gpu, vfxVersion=vfx, vcRuntime=vc, torchCuda=cuda)))
""";
            var output = await Commands.Run(config.Python, ["-B", "-c", code], config.ComfyRoot, null, ct, timeout: TimeSpan.FromSeconds(30));
            var python = JsonSerializer.Deserialize<RuntimeArtifactHost>(output.Trim().Split('\n').Last(), JsonFile.Options)
                ?? throw new InvalidDataException("无法读取运行库所需的 Python ABI。");
            tag = python.PythonTag; version = python.VfxVersion; vc = python.VcRuntime;
            torchCuda = python.TorchCuda; torchGpu = python.Gpu;
        }
        else if (!fresh) throw new FileNotFoundException("导入环境的 Python 不存在。", config.Python);
        else if (OperatingSystem.IsWindows())
        {
            vc = NativeLibrary.TryLoad("vcruntime140.dll", out var first);
            if (vc) NativeLibrary.Free(first);
            var cpp = NativeLibrary.TryLoad("msvcp140.dll", out var second);
            if (cpp) NativeLibrary.Free(second);
            vc &= cpp;
        }
        string gpu = "", generation = "";
        // A resumed fresh install will still install its pinned CUDA torch. An
        // imported CPU-only interpreter, however, must retain that choice.
        if (fresh && torchCuda == false) torchCuda = null;
        if (OperatingSystem.IsWindows() && Environment.Is64BitOperatingSystem)
        {
            var smi = torchCuda.HasValue ? null : Commands.Find("nvidia-smi.exe");
            gpu = torchGpu;
            if (smi is not null)
            {
                try
                {
                    var names = await Commands.Run(smi, ["--query-gpu=name", "--format=csv,noheader"], Environment.CurrentDirectory, null, ct, timeout: TimeSpan.FromSeconds(20));
                    gpu = names.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
                }
                catch (OperationCanceledException) { throw; }
                catch (InvalidOperationException) { /* No usable NVIDIA device; base generation remains available. */ }
            }
            var match = Regex.Match(gpu, @"\bRTX\s*([345])\d{2,3}\b", RegexOptions.IgnoreCase);
            if (match.Success) generation = "rtx" + match.Groups[1].Value + "0";
        }
        return new(tag, generation, gpu, version, vc, torchCuda);
    }

    public static async Task<InstallationPlan> PrepareAsync(InstallationPlan plan, RuntimeManifest runtime, CancellationToken ct,
        Func<LauncherConfig, RuntimeManifest, bool, CancellationToken, Task<RuntimeArtifactHost>>? probe = null)
    {
        // Do not expand a deserialized legacy plan's frozen download scope.
        if (plan.RuntimeArtifactsIdentity.Length == 0) return plan;
        ValidateIdentity(plan);
        if (plan.RuntimeArtifactsPrepared) return plan;
        var config = plan.CreateConfig();
        var artifacts = new List<Artifact>();
        long environmentReserve = 0, cacheReserve = 0;
        RuntimeArtifactHost? host = null;
        if ((await SesquiRuntimePreparation.FilesIssuesAsync(config, runtime, ct)).Length > 0)
        {
            artifacts.Add(SesquiRuntimePreparation.Weight);
            environmentReserve += SesquiRuntimePreparation.Weight.Size;
            cacheReserve += SesquiRuntimePreparation.Weight.Size;
        }
        if (runtime.Nodes.Any(node => node.Id is "nvidia-rtx-vsr-pro" or "DLSS5-Comfyui"))
        {
            host = await (probe ?? ProbeAsync)(config, runtime, plan.Fresh, ct);
            if (host.Generation.Length > 0)
            {
                var catalog = NvidiaRuntimePreparation.Catalog();
                var wheel = NvidiaRuntimePreparation.WheelFor(host.PythonTag)
                    ?? throw new InvalidOperationException("当前 Python ABI 没有已验证的 NVIDIA VFX 运行库；保留现有 Python 与 Torch，请重新检查支持的独立环境。");
                if (host.VfxVersion != wheel.Version)
                {
                    artifacts.Add(wheel);
                    // The official wheel contains compressed SDK models; reserve the
                    // expanded package and pip's separate extraction space.
                    environmentReserve += 2 * GiB;
                    cacheReserve += 2 * GiB;
                }
                if (!host.VcRuntime) artifacts.Add(catalog.VcRuntime);
                var node = NvidiaRuntimePreparation.ResolveDlssDirectory(config.ComfyRoot);
                var variant = catalog.DlssRuntimes.Single(item => item.Generation == host.Generation);
                var hostReady = node is not null && await Downloads.Verify(Path.Combine(node, "vendor", catalog.DlssHost.FileName), catalog.DlssHost.Size, catalog.DlssHost.Sha256, ct);
                var dllReady = node is not null && await Downloads.Verify(Path.Combine(node, "vendor", variant.Generation, variant.Dll.FileName), variant.Dll.Size, variant.Dll.Sha256, ct);
                if (!hostReady || host.Generation == "rtx40" && !dllReady) artifacts.Add(catalog.DlssArchive);
                if (!hostReady) environmentReserve += catalog.DlssHost.Size;
                if (!dllReady)
                {
                    environmentReserve += variant.Dll.Size;
                    cacheReserve += variant.Dll.Size;
                    if (host.Generation != "rtx40")
                    {
                        artifacts.Add(variant.Archive);
                        var sevenZip = Path.Combine(plan.RuntimeRoot, "tools", "7zip-26.05");
                        if (!await HashMatches(Path.Combine(sevenZip, "7z.exe"), catalog.SevenZipExeSha256, ct)
                            || !await HashMatches(Path.Combine(sevenZip, "7z.dll"), catalog.SevenZipDllSha256, ct))
                        {
                            artifacts.Add(catalog.SevenZipBootstrap); artifacts.Add(catalog.SevenZipArchive);
                            cacheReserve += 8 * 1024 * 1024;
                        }
                    }
                }
            }
        }
        var frozen = artifacts.DistinctBy(item => item.Sha256).ToArray();
        var downloads = plan.Downloads.ToBuilder();
        foreach (var artifact in frozen)
        {
            var destination = ArtifactCache.PathFor(plan.RuntimeRoot, artifact);
            if (downloads.Any(item => item.Destination.Equals(destination, StringComparison.OrdinalIgnoreCase))) continue;
            downloads.Add(new(destination, artifact.Size, artifact.Sha256, false)
            {
                LegacyDestinations = ArtifactCache.LegacyPaths(plan.RuntimeRoot, artifact), RepairableStaging = true,
                RequiresReplacement = File.Exists(destination) && !await Downloads.Verify(destination, artifact.Size, artifact.Sha256, ct)
            });
        }
        var prepared = plan with
        {
            RuntimeArtifactsPrepared = true, FrozenRuntimeArtifacts = frozen, RuntimeArtifactHost = host,
            RuntimeArtifactEnvironmentReserve = environmentReserve, RuntimeArtifactCacheReserve = cacheReserve,
            EnvironmentReserveBytes = plan.EnvironmentReserveBytes + environmentReserve,
            RuntimeReserveBytes = plan.RuntimeReserveBytes + cacheReserve, Downloads = downloads.ToImmutable()
        };
        return await InstallationPlanner.RefreshSpaceEstimateAsync(prepared, ct);
    }

    public static async Task ValidateHostAsync(InstallationPlan plan, RuntimeManifest runtime, CancellationToken ct)
    {
        ValidateIdentity(plan);
        if (plan.RuntimeArtifactHost is not { } frozen) return;
        var current = await ProbeAsync(plan.CreateConfig(), runtime, plan.Fresh, ct);
        if (current.PythonTag != frozen.PythonTag || current.Generation != frozen.Generation || current.Gpu != frozen.Gpu)
            throw new InvalidOperationException("检查后 Python ABI 或 NVIDIA 显卡发生变化。已保留安装现场，请重新检查下载计划。");
    }

    private static async Task<bool> HashMatches(string path, string sha256, CancellationToken ct)
    {
        if (!File.Exists(path)) return false;
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).Equals(sha256, StringComparison.OrdinalIgnoreCase);
    }
}
