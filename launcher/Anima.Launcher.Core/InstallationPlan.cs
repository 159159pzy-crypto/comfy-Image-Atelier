using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Anima.Launcher.Core;

public sealed record PreflightIssue(string Code, string Message, bool Blocking = true)
{
    public override string ToString() => Message;
}
public sealed record VolumeEstimate(string Volume, long RequiredBytes, long? AvailableBytes);
public sealed record InstallationDownload(string Destination, long Size, string Sha256, bool InEnvironment);
public sealed record PreflightResult(ImmutableArray<PreflightIssue> Issues, ImmutableArray<VolumeEstimate> Volumes)
{
    public bool CanInstall => Issues.All(issue => !issue.Blocking);
}

/// <summary>The JSON snapshot prevents later edits to the UI config from changing a running install.</summary>
public sealed record InstallationPlan(
    string ConfigurationJson, string ComfyRoot, string RuntimeRoot, string SelectedProfile, bool Fresh,
    long EnvironmentBytes, long RuntimeBytes, long EstimatedDownloadBytes, ImmutableArray<string> Stages)
{
    public long EnvironmentReserveBytes { get; init; }
    public long RuntimeReserveBytes { get; init; }
    public ImmutableArray<InstallationDownload> Downloads { get; init; } = [];
    public bool ModelPathsDeferred { get; init; }
    public LauncherConfig CreateConfig() => JsonSerializer.Deserialize<LauncherConfig>(ConfigurationJson, JsonFile.Options)!;
    public string Describe() => $"{(Fresh ? "新建 NVIDIA 环境" : "补齐现有环境")} · {(SelectedProfile == "anima" ? "Anima" : "Krea 2")}\n" +
        $"ComfyUI：{ComfyRoot}\n运行工具与缓存：{RuntimeRoot}\n" +
        $"已知剩余下载至少 {EstimatedDownloadBytes / 1073741824d:N1} GiB（Python 包按需下载）\n" +
        $"还需环境与模型空间 {EnvironmentBytes / 1073741824d:N1} GiB；工具与缓存空间 {RuntimeBytes / 1073741824d:N1} GiB（已扣除可复用内容）。";
}

public static class InstallationPlanner
{
    private const long GiB = 1024L * 1024 * 1024;
    private const long MiB = 1024L * 1024;
    private static readonly string[] CoreStages = ["准备隔离安装工具", "拉取锁定版本的 ComfyUI", "安装 Python 并创建独立虚拟环境", "安装锁定 CUDA / PyTorch", "安装 ComfyUI 依赖"];
    // CUDA 12.8 GA's Windows driver version, deliberately a conservative fresh-install baseline.
    // https://docs.nvidia.com/cuda/archive/12.8.0/cuda-toolkit-release-notes/index.html
    public const string MinimumNvidiaDriver = "570.65";
    public static bool IsNvidiaDriverSupported(string driver) => Version.TryParse(driver.Trim(), out var version)
        && version >= Version.Parse(MinimumNvidiaDriver);

    public static string ResolveRuntimeRoot(LauncherConfig config, string stateDir) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(config.RuntimeRoot) ? stateDir : config.RuntimeRoot);

    public static InstallationPlan Create(LauncherConfig config, RuntimeManifest manifest, string stateDir, bool fresh,
        ModelManifest? models = null, ModelProfilesManifest? profiles = null)
    {
        var snapshot = config.Clone();
        snapshot.Validate();
        Safety.ValidateRuntime(manifest);
        if (fresh && !string.IsNullOrWhiteSpace(snapshot.InstallRoot))
        {
            snapshot.InstallRoot = Path.GetFullPath(snapshot.InstallRoot.Trim().Trim('"'));
            snapshot.ComfyRoot = Path.Combine(snapshot.InstallRoot, "ComfyUI");
            snapshot.RuntimeRoot = Path.Combine(snapshot.InstallRoot, "runtime");
            snapshot.Python = Path.Combine(snapshot.ComfyRoot, ".venv", "Scripts", "python.exe");
            snapshot.PythonExplicit = false;
        }
        if (string.IsNullOrWhiteSpace(snapshot.ComfyRoot)) throw new InvalidDataException("请先选择安装目录或现有 ComfyUI。");
        snapshot.ComfyRoot = fresh ? Path.GetFullPath(snapshot.ComfyRoot.Trim().Trim('"')) : Inventory.NormalizeRoot(snapshot.ComfyRoot);
        if (!fresh) snapshot.Python = Inventory.FindPython(snapshot.ComfyRoot, snapshot.Python);
        snapshot.RuntimeRoot = ResolveRuntimeRoot(snapshot, stateDir);
        if (SameOrInside(snapshot.RuntimeRoot, snapshot.ComfyRoot) || SameOrInside(snapshot.ComfyRoot, snapshot.RuntimeRoot))
            throw new InvalidDataException("运行工具目录与 ComfyUI 目录不能相同或互相包含，请使用同一安装目录下的 ComfyUI 和 runtime。");
        // Import only repairs nodes; model downloads belong to the fresh-install flow.
        var required = fresh && models is not null && profiles is not null ? profiles.RequiredModels(models, snapshot.SelectedProfile) : [];
        var artifacts = required.Where(m => m.CanDownload).Select(m => new InstallationDownload(
            Safety.Under(Path.Combine(snapshot.ComfyRoot, "models"), m.Category + "/" + m.RelativePath), m.Size, m.Sha256, true)).ToList();
        if (fresh)
        {
            artifacts.AddRange(manifest.TorchWheels.Select(w => new InstallationDownload(Path.Combine(snapshot.RuntimeRoot, "cache", "wheels", w.Executable), w.Size, w.Sha256, false)));
            artifacts.AddRange(manifest.Tools.Where(t => !ToolInstallation.IsReady(snapshot.RuntimeRoot, t))
                .Select(t => new InstallationDownload(Path.Combine(snapshot.RuntimeRoot, "cache", t.Id + ".zip"), t.Size, t.Sha256, false)));
        }
        var (environmentReserve, runtimeReserve) = RemainingComponentReserves(snapshot, manifest, stateDir, fresh);
        var environmentDownloads = artifacts.Where(a => a.InEnvironment).Sum(a => Downloads.RemainingBytes(a.Destination, a.Size, a.Sha256));
        var runtimeDownloads = artifacts.Where(a => !a.InEnvironment).Sum(a => Downloads.RemainingBytes(a.Destination, a.Size, a.Sha256));
        var stages = (fresh
            ? new[] { "检查系统与磁盘", "准备隔离工具", "安装 ComfyUI 与 Python", "安装 CUDA / PyTorch", "安装必要节点", "验证依赖", "下载所选基础模型" }
            : new[] { "检查现有环境", "补齐缺失节点", "验证依赖", "检查所选基础模型" }).ToImmutableArray();
        return new(JsonSerializer.Serialize(snapshot, JsonFile.Options), snapshot.ComfyRoot, snapshot.RuntimeRoot,
            snapshot.SelectedProfile, fresh, environmentReserve + environmentDownloads, runtimeReserve + runtimeDownloads, environmentDownloads + runtimeDownloads, stages)
        { EnvironmentReserveBytes = environmentReserve, RuntimeReserveBytes = runtimeReserve, Downloads = artifacts.ToImmutableArray() };
    }

    private static (long Environment, long Runtime) RemainingComponentReserves(LauncherConfig config, RuntimeManifest manifest, string stateDir, bool fresh)
    {
        var completed = ReadCompletedStages(config, manifest, stateDir);
        var nodePlans = Installer.PlanNodes(config.ComfyRoot, manifest);
        var pendingNodes = nodePlans.Count(node => !node.PreserveExisting && !NodeComplete(node, manifest.Version));
        var nodeReserve = nodePlans.Count == 0 ? 0 : 2 * GiB * pendingNodes / nodePlans.Count;
        if (!fresh) return (Math.Max(128 * MiB, nodeReserve), pendingNodes > 0 ? 512 * MiB : 128 * MiB);

        var venv = Path.Combine(config.ComfyRoot, ".venv");
        var pythonReady = File.Exists(Path.Combine(venv, "Scripts", "python.exe")) && File.Exists(Path.Combine(venv, "pyvenv.cfg"));
        var managed = RepositoryMatches(config.ComfyRoot, manifest.Comfy);
        var torchReady = pythonReady && completed.Contains(CoreStages[3])
            && PackageMetadataPresent(venv, "torch", manifest.TorchVersion)
            && PackageMetadataPresent(venv, "torchvision", manifest.TorchVisionVersion)
            && PackageMetadataPresent(venv, "torchaudio", manifest.TorchAudioVersion);
        var dependenciesReady = pythonReady && completed.Contains(CoreStages[4]) && ConstraintsMatch(config, manifest);
        var cloneReserve = managed ? 0 : GiB;
        var pythonReserve = pythonReady ? 0 : GiB;
        // Count only the managed environment's installed package directories, never
        // user models/output or arbitrary files elsewhere on the same drive.
        var packageRoot = Path.Combine(venv, "Lib", "site-packages");
        var torchBytes = managed ? new[] { "torch", "torchvision", "torchaudio" }.Sum(name => DirectoryBytes(Path.Combine(packageRoot, name))) : 0;
        var torchReserve = torchReady ? 0 : Math.Max(512 * MiB, 8 * GiB - Math.Min(8 * GiB, torchBytes));
        var otherPackageBytes = managed ? Math.Max(0, DirectoryBytes(packageRoot) - torchBytes) : 0;
        var dependencyReserve = dependenciesReady ? 0 : Math.Max(256 * MiB, 3 * GiB - Math.Min(3 * GiB, otherPackageBytes));
        var toolsReady = manifest.Tools.All(t => ToolInstallation.IsReady(config.RuntimeRoot, t));
        var runtimePython = pythonReady ? 0 : Math.Max(128 * MiB, GiB - Math.Min(GiB, DirectoryBytes(Path.Combine(config.RuntimeRoot, "python"))));
        var pipScratch = !torchReady || !dependenciesReady ? 2 * GiB : pendingNodes > 0 ? 512 * MiB : 128 * MiB;
        return (cloneReserve + pythonReserve + torchReserve + dependencyReserve + nodeReserve + 128 * MiB,
            runtimePython + pipScratch + (toolsReady ? 0 : 256 * MiB));
    }

    private static bool NodeComplete(NodeInstallPlan node, string version)
    {
        try { return RepositoryMatches(node.Directory, node.Repository) && File.ReadAllText(Path.Combine(node.Directory, ".anima-dependencies-complete")).Trim() == version; }
        catch { return false; }
    }
    private static bool RepositoryMatches(string path, Repository repo)
    {
        try { var saved = JsonFile.Read<Repository>(Path.Combine(path, ".anima-revision.json")); return saved.Commit == repo.Commit && saved.Id == repo.Id; }
        catch { return false; }
    }
    private static bool ConstraintsMatch(LauncherConfig config, RuntimeManifest manifest)
    {
        try { return File.ReadAllLines(Path.Combine(config.ComfyRoot, ".anima-constraints.txt")).SequenceEqual(manifest.PythonConstraints); }
        catch { return false; }
    }
    private static bool PackageMetadataPresent(string venv, string package, string version)
    {
        try
        {
            var packages = Path.Combine(venv, "Lib", "site-packages");
            return Directory.EnumerateDirectories(packages, package + "-*.dist-info").Any(path =>
                File.Exists(Path.Combine(path, "RECORD")) && File.ReadLines(Path.Combine(path, "METADATA"))
                    .Any(line => line.StartsWith("Version: ") && line[9..].Trim().Split('+')[0] == version.Split('+')[0]));
        }
        catch { return false; }
    }
    private static long DirectoryBytes(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return 0;
            return Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Sum(file => { try { return new FileInfo(file).Length; } catch { return 0L; } });
        }
        catch { return 0; }
    }

    private static string ProgressPath(LauncherConfig config, RuntimeManifest manifest, string stateDir)
    {
        var identity = Path.TrimEndingDirectorySeparator(Path.GetFullPath(config.ComfyRoot)).ToUpperInvariant() + "\n" + JsonSerializer.Serialize(manifest, JsonFile.Options);
        return Path.Combine(stateDir, "install-progress", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant() + ".json");
    }
    private static HashSet<string> ReadCompletedStages(LauncherConfig config, RuntimeManifest manifest, string stateDir)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        try { result.UnionWith(JsonFile.Read<string[]>(ProgressPath(config, manifest, stateDir))); } catch { }
        // Adopt old journals only when the root and its full dependency constraints
        // match this manifest. Preserve that evidence before the next stage rewrites it.
        if (!RepositoryMatches(config.ComfyRoot, manifest.Comfy) || !ConstraintsMatch(config, manifest)) return result;
        try
        {
            var last = JsonFile.Read<InstallJournal>(Path.Combine(stateDir, "install-state.json"));
            if (!Path.GetFullPath(last.Root).Equals(Path.GetFullPath(config.ComfyRoot), StringComparison.OrdinalIgnoreCase)) return result;
            var index = Array.IndexOf(CoreStages, last.Stage);
            if (last.Stage.StartsWith("安装节点 ") || last.Stage == "验证运行依赖") index = CoreStages.Length;
            else if (index >= 0 && last.Status == "complete") index++;
            if (index > 0) result.UnionWith(CoreStages.Take(index));
        }
        catch { }
        return result;
    }
    public static void PreserveCompletedStages(LauncherConfig config, RuntimeManifest manifest, string stateDir) =>
        JsonFile.Write(ProgressPath(config, manifest, stateDir), ReadCompletedStages(config, manifest, stateDir).Order(StringComparer.Ordinal).ToArray());
    public static void MarkStageComplete(LauncherConfig config, RuntimeManifest manifest, string stateDir, string stage)
    {
        var completed = ReadCompletedStages(config, manifest, stateDir);
        completed.Add(stage);
        JsonFile.Write(ProgressPath(config, manifest, stateDir), completed.Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>Resolve resumed installations' effective model paths before checking their destination volumes.</summary>
    public static async Task<InstallationPlan> PreparePlanAsync(InstallationPlan plan, ModelManifest models,
        ModelProfilesManifest profiles, string probeScript, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!plan.Fresh) return plan;
        var config = plan.CreateConfig();
        var paths = new Dictionary<string, string[]>
        {
            ["diffusion_models"] = [Path.Combine(plan.ComfyRoot, "models", "diffusion_models"), Path.Combine(plan.ComfyRoot, "models", "unet")],
            ["text_encoders"] = [Path.Combine(plan.ComfyRoot, "models", "text_encoders"), Path.Combine(plan.ComfyRoot, "models", "clip")],
        };
        var probe = new ProbeResult(plan.ComfyRoot, config.Python, paths);
        string? python = null;
        try { python = Inventory.FindPython(plan.ComfyRoot, config.Python); }
        catch (FileNotFoundException) { }
        if (python is not null && File.Exists(Path.Combine(plan.ComfyRoot, "folder_paths.py")))
        {
            try { probe = await Inventory.Probe(plan.ComfyRoot, python, probeScript, ct, config.ComfyArguments); }
            catch (OperationCanceledException) { throw; }
            catch (InvalidOperationException ex) when (ex.Message.Contains("No module named 'yaml'", StringComparison.Ordinal))
            {
                // Dependency installation can be interrupted before PyYAML exists.
                // Let that stage resume; the UI probes again and checks every real
                // destination volume after dependencies and before model downloads.
                plan = plan with { ModelPathsDeferred = true };
            }
            // An interrupted fresh install may not yet have PyYAML. Without an
            // extra-path config, canonical and legacy paths remain safe defaults.
            catch when (!File.Exists(Path.Combine(plan.ComfyRoot, "extra_model_paths.yaml"))) { }
        }
        return await ResolveModelDestinationsAsync(plan, probe, models, profiles, ct);
    }

    public static Task<InstallationPlan> ResolveModelDestinationsAsync(InstallationPlan plan, ProbeResult probe,
        ModelManifest models, ModelProfilesManifest profiles, CancellationToken ct) => Task.Run(async () =>
    {
        ct.ThrowIfCancellationRequested();
        if (!plan.Fresh) return plan;
        if (!Path.TrimEndingDirectorySeparator(Path.GetFullPath(probe.Root)).Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(plan.ComfyRoot)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("模型目录探测结果与安装目标不一致，请重新检查环境。");
        var selected = new ModelManifest(models.SchemaVersion, profiles.RequiredModels(models, plan.SelectedProfile));
        var inventory = await Inventory.Scan(probe, selected, false, ct);
        var artifacts = plan.Downloads.Where(a => !a.InEnvironment).Concat(inventory.Where(item => item.Model.CanDownload)
            .Select(item => new InstallationDownload(item.FoundPath ?? item.Destination, item.Model.Size, item.Model.Sha256, true))).ToImmutableArray();
        return WithRemainingDownloads(plan with { Downloads = artifacts });
    }, ct);

    private static InstallationPlan WithRemainingDownloads(InstallationPlan plan)
    {
        var environment = plan.Downloads.Where(a => a.InEnvironment).Sum(a => Downloads.RemainingBytes(a.Destination, a.Size, a.Sha256));
        var runtime = plan.Downloads.Where(a => !a.InEnvironment).Sum(a => Downloads.RemainingBytes(a.Destination, a.Size, a.Sha256));
        return plan with { EnvironmentBytes = plan.EnvironmentReserveBytes + environment, RuntimeBytes = plan.RuntimeReserveBytes + runtime, EstimatedDownloadBytes = environment + runtime };
    }

    public static async Task<InstallationPlan> RefreshSpaceEstimateAsync(InstallationPlan plan, CancellationToken ct)
    {
        // Verification of multi-GB caches must not synchronously block WinUI.
        return await Task.Run(async () =>
        {
            long environment = 0, runtime = 0;
            foreach (var artifact in plan.Downloads)
            {
                ct.ThrowIfCancellationRequested();
                if (File.Exists(artifact.Destination) && !await global::Anima.Launcher.Core.Downloads.Verify(artifact.Destination, artifact.Size, artifact.Sha256, ct))
                    throw new InvalidDataException("已有安装文件校验不符，已保留原文件，请备份处理后继续：" + artifact.Destination);
                var remaining = global::Anima.Launcher.Core.Downloads.RemainingBytes(artifact.Destination, artifact.Size, artifact.Sha256);
                if (artifact.InEnvironment) environment += remaining; else runtime += remaining;
            }
            return plan with { EnvironmentBytes = plan.EnvironmentReserveBytes + environment, RuntimeBytes = plan.RuntimeReserveBytes + runtime, EstimatedDownloadBytes = environment + runtime };
        }, ct);
    }

    private static bool SameOrInside(string candidate, string parent)
    {
        var a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        return a.Equals(b, StringComparison.OrdinalIgnoreCase) || a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static ImmutableArray<VolumeEstimate> EstimateVolumes(InstallationPlan plan) => new[]
        {
            (Path: plan.ComfyRoot, Bytes: plan.EnvironmentReserveBytes), (Path: plan.RuntimeRoot, Bytes: plan.RuntimeReserveBytes)
        }.Concat(plan.Downloads.Select(a => (Path: a.Destination, Bytes: Downloads.RemainingBytes(a.Destination, a.Size, a.Sha256)))
            .Where(a => a.Bytes > 0))
        .GroupBy(x => Path.GetPathRoot(x.Path)!, StringComparer.OrdinalIgnoreCase).Select(group =>
        {
            long? available = null;
            try { available = new DriveInfo(group.Key).AvailableFreeSpace; } catch { }
            return new VolumeEstimate(group.Key, checked(group.Sum(x => x.Bytes) + 256L * 1024 * 1024), available);
        }).ToImmutableArray();

    public static async Task<PreflightResult> PreflightAsync(InstallationPlan plan, CancellationToken ct)
    {
        var issues = ImmutableArray.CreateBuilder<PreflightIssue>();
        var config = plan.CreateConfig();
        ct.ThrowIfCancellationRequested();
        if (plan.ModelPathsDeferred)
            issues.Add(new("model_paths_deferred", "依赖尚未安装完整，先按环境内模型目录预留空间；恢复依赖后会重新解析额外模型目录，并在下载前检查实际目标磁盘。", false));
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) || RuntimeInformation.OSArchitecture != Architecture.X64)
            issues.Add(new("system", "安装启动器需要 Windows 10 1809 或更新版本的 x64 系统。"));
        if (plan.Fresh)
        {
            try
            {
                if (Directory.Exists(plan.ComfyRoot) && Directory.EnumerateFileSystemEntries(plan.ComfyRoot).Any()
                    && !File.Exists(Path.Combine(plan.ComfyRoot, ".anima-revision.json")))
                    issues.Add(new("target_not_empty", "ComfyUI 目标目录不为空，请导入现有环境或选择新的安装目录。"));
            }
            catch (Exception ex) { issues.Add(new("target_access", "无法检查 ComfyUI 目标目录：" + ex.Message)); }
            var smi = Commands.Find("nvidia-smi.exe");
            if (smi is null) issues.Add(new("nvidia_missing", "新建环境需要 NVIDIA 驱动；其他显卡请导入已可用的 ComfyUI。"));
            else
            {
                try
                {
                    var versions = await Commands.Run(smi, ["--query-gpu=driver_version", "--format=csv,noheader"],
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), null, ct, timeout: TimeSpan.FromSeconds(15));
                    var drivers = versions.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    if (drivers.Length == 0 || !drivers.All(IsNvidiaDriverSupported))
                        issues.Add(new("nvidia_driver", $"当前 NVIDIA 驱动 {versions.Trim()}；此 CUDA 12.8 新建环境要求 {MinimumNvidiaDriver} 或更新驱动。请更新驱动后重试，或导入已有环境。"));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { issues.Add(new("nvidia_probe", "无法检查 NVIDIA 驱动：" + ex.Message)); }
            }
        }
        else if (!File.Exists(config.Python)) issues.Add(new("python", "未找到现有环境的 Python。"));
        var writablePaths = new[] { plan.ComfyRoot, plan.RuntimeRoot }
            .Concat(plan.Downloads.Where(a => !File.Exists(a.Destination)).Select(a => Path.GetDirectoryName(a.Destination)!));
        foreach (var path in writablePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            try { CheckWriteAccess(path); }
            catch (Exception ex) { issues.Add(new("directory_access", $"目录不可写：{path}。{ex.Message}")); }
        }
        try { plan = await RefreshSpaceEstimateAsync(plan, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { issues.Add(new("cache_invalid", ex.Message)); }
        var volumes = EstimateVolumes(plan);
        foreach (var volume in volumes)
        {
            if (volume.AvailableBytes is null) issues.Add(new("volume_unavailable", $"无法读取磁盘 {volume.Volume} 的可用空间。"));
            else if (volume.AvailableBytes < volume.RequiredBytes)
                issues.Add(new("disk_space", $"磁盘 {volume.Volume} 需预留 {volume.RequiredBytes / (double)GiB:N1} GiB，当前可用 {volume.AvailableBytes / (double)GiB:N1} GiB。"));
        }
        return new(issues.ToImmutable(), volumes);
    }

    private static void CheckWriteAccess(string path)
    {
        var ancestor = Path.GetFullPath(path);
        while (!Directory.Exists(ancestor))
        {
            if (File.Exists(ancestor)) throw new IOException("路径被同名文件占用。");
            ancestor = Path.GetDirectoryName(ancestor) ?? throw new DirectoryNotFoundException("未找到可用父目录。");
        }
        var probe = Path.Combine(ancestor, ".anima-write-check-" + Guid.NewGuid().ToString("N"));
        using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        stream.WriteByte(0);
    }
}
