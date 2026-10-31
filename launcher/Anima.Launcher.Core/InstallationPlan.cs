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
public sealed record InstallationDownload(string Destination, long Size, string Sha256, bool InEnvironment)
{
    // Package Fetch preserves a damaged staging file as a backup before replacing it.
    // Final single-file models and tool caches remain protected from replacement.
    public bool RepairableStaging { get; init; }
    public bool RequiresReplacement { get; init; }
    public string[] LegacyDestinations { get; init; } = [];
    public string[] VerifiedLegacyDestinations { get; init; } = [];
    public bool VerifiedExisting { get; init; }
    public bool Bundled { get; init; }
    public long RemainingBytes => Bundled ? 0 : RequiresReplacement ? Size : LegacyDestinations.Aggregate(
        global::Anima.Launcher.Core.Downloads.RemainingBytes(Destination, Size, Sha256),
        (remaining, path) => Math.Min(remaining, global::Anima.Launcher.Core.Downloads.RemainingBytes(path, Size, Sha256)));
    public long RemainingAllocationBytes => RequiresReplacement ? Size : VerifiedExisting || VerifiedLegacyDestinations.Length > 0 ? 0
        : LegacyDestinations.Aggregate(DownloadDiskSpace.RemainingAllocationBytes(Destination, Size, Sha256),
            (remaining, path) => Math.Min(remaining, DownloadDiskSpace.RemainingAllocationBytes(path, Size, Sha256)));
}
public sealed record PreflightResult(ImmutableArray<PreflightIssue> Issues, ImmutableArray<VolumeEstimate> Volumes)
{
    public bool CanInstall => Issues.All(issue => !issue.Blocking);
}

/// <summary>The JSON snapshot prevents later edits to the UI config from changing a running install.</summary>
public sealed record InstallationPlan(
    string ConfigurationJson, string ComfyRoot, string RuntimeRoot, string SelectedProfile, bool Fresh,
    long EnvironmentBytes, long RuntimeBytes, long EstimatedDownloadBytes, ImmutableArray<string> Stages)
{
    public RuntimeManifest? ResolvedRuntime { get; init; }
    public ComfyRelease? Release { get; init; }
    public ComfyDependencySnapshot? Dependencies { get; init; }
    public NodeRelease[] NodeVersions { get; init; } = [];
    public bool NodeVersionsResolved { get; init; }
    public long EnvironmentReserveBytes { get; init; }
    public long RuntimeReserveBytes { get; init; }
    public ImmutableArray<InstallationDownload> Downloads { get; init; } = [];
    public ImmutableArray<string> ModelIds { get; init; } = [];
    public ImmutableArray<string> MandatoryModelIds { get; init; } = [];
    public ModelItem[] FrozenModels { get; init; } = [];
    public ModelProfileDefinition? FrozenProfile { get; init; }
    public ImmutableArray<ModelStatus> ModelInventory { get; init; } = [];
    public bool ModelPathsDeferred { get; init; }
    public long DeferredModelBytes { get; init; }
    // Empty in legacy plans: their frozen dependency scope must not silently gain
    // new native downloads when resumed by a newer launcher.
    public string RuntimeArtifactsIdentity { get; init; } = "";
    public bool RuntimeArtifactsPrepared { get; init; }
    public Artifact[] FrozenRuntimeArtifacts { get; init; } = [];
    public RuntimeArtifactHost? RuntimeArtifactHost { get; init; }
    public long RuntimeArtifactEnvironmentReserve { get; init; }
    public long RuntimeArtifactCacheReserve { get; init; }
    public LauncherConfig CreateConfig() => JsonSerializer.Deserialize<LauncherConfig>(ConfigurationJson, JsonFile.Options)!;
    public string Describe() => $"{(Fresh ? "新建 NVIDIA 环境" : "补齐现有环境")} · {(SelectedProfile == "anima" ? "Anima" : "Krea 2")}\n" +
        $"ComfyUI：{ComfyRoot}\n" + (Release is null ? "" : $"稳定版本：{Release.Tag} · {Release.Commit[..12]}（已固定，继续安装不会换版）\n") +
        (NodeVersions.Length == 0 ? "" : "新装节点提交已固定：\n" + string.Join("\n", NodeVersions.Select(node => $"{node.Id} · {node.Branch} · {node.Commit[..12]}" + (node.Archived == true ? "（仓库已归档）" : ""))) + "\n") + $"运行工具与缓存：{RuntimeRoot}\n" +
        $"已知剩余下载至少 {EstimatedDownloadBytes / 1073741824d:N1} GiB（Python 包按需下载）\n" +
        $"还需环境与模型空间 {EnvironmentBytes / 1073741824d:N1} GiB；工具与缓存空间 {RuntimeBytes / 1073741824d:N1} GiB（已扣除可复用内容）。";
}

public static class InstallationPlanner
{
    private sealed record StableInstallSnapshot(string Root, ComfyRelease Release, RuntimeManifest Runtime, ComfyDependencySnapshot Dependencies)
    {
        public bool Completed { get; init; }
        public NodeRelease[] NodeVersions { get; init; } = [];
    }
    private static string StablePlanPath(string root, string stateDir) => Path.Combine(stateDir, "install-plans",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant()))).ToLowerInvariant() + ".json");
    private sealed record NodeInstallSnapshot(string Root, RuntimeManifest Runtime, NodeRelease[] NodeVersions, bool Completed = false);
    private static string NodePlanPath(string root, string stateDir) => Path.ChangeExtension(StablePlanPath(root, stateDir), ".nodes.json");
    public static void MarkStableInstallComplete(LauncherConfig config, string stateDir)
    {
        var path = StablePlanPath(config.ComfyRoot, stateDir);
        if (File.Exists(path)) JsonFile.Write(path, JsonFile.Read<StableInstallSnapshot>(path) with { Completed = true });
        var nodes = NodePlanPath(config.ComfyRoot, stateDir);
        if (File.Exists(nodes)) JsonFile.Write(nodes, JsonFile.Read<NodeInstallSnapshot>(nodes) with { Completed = true });
    }

    public static async Task<InstallationPlan> ResolveStableAsync(InstallationPlan plan, RuntimeManifest runtime, string stateDir,
        CancellationToken ct, ComfyReleases? releases = null, NodeReleases? nodeReleases = null)
    {
        if (!plan.Fresh) return await ResolveMissingNodesAsync(plan, runtime, stateDir, ct, nodeReleases);
        if (plan.ResolvedRuntime is not null) return plan;
        var path = StablePlanPath(plan.ComfyRoot, stateDir);
        StableInstallSnapshot snapshot;
        var saved = File.Exists(path) ? JsonFile.Read<StableInstallSnapshot>(path) : null;
        if (saved is not null && (!saved.Completed || File.Exists(Path.Combine(plan.ComfyRoot, ".anima-revision.json"))))
        {
            snapshot = saved;
            if (!Path.GetFullPath(snapshot.Root).Equals(Path.GetFullPath(plan.ComfyRoot), StringComparison.OrdinalIgnoreCase)
                || snapshot.Runtime.Comfy.Commit != snapshot.Release.Commit || snapshot.Dependencies.ComfyCommit != snapshot.Release.Commit)
                throw new InvalidDataException("已保存的稳定版本安装计划身份不匹配。");
            Safety.ValidateRuntime(snapshot.Runtime);
            if (snapshot.NodeVersions.Any(node => !snapshot.Runtime.Nodes.Any(repository => repository.Id == node.Id
                && repository.Url == node.Url && repository.Commit == node.Commit)))
                throw new InvalidDataException("已保存的节点版本与安装清单不一致。");
        }
        else
        {
            var config = plan.CreateConfig();
            var git = Commands.Find("git.exe");
            releases ??= new ComfyReleases(resolveTag: git is null ? null : (tag, token) => ComfyReleases.ResolveTagWithGit(git, tag, stateDir, config, token), config: config);
            var marker = Path.Combine(plan.ComfyRoot, ".anima-revision.json");
            var release = File.Exists(marker)
                ? new ComfyRelease("继续已安装提交", JsonFile.Read<Repository>(marker).Commit, null, "https://github.com/Comfy-Org/ComfyUI")
                : await releases.LatestAsync(ct);
            var dependencies = await releases.DependenciesAsync(release, runtime, ct);
            runtime = PreserveExistingNodeVersions(plan.ComfyRoot, runtime);
            var missingNodes = Installer.PlanNodes(plan.ComfyRoot, runtime).Where(node => !Directory.Exists(node.Directory)).Select(node => node.Repository).ToArray();
            nodeReleases ??= new NodeReleases(resolveHead: git is null ? null : (repository, token) =>
                NodeReleases.ResolveHeadWithGit(git, repository, Directory.Exists(stateDir) ? stateDir : Environment.CurrentDirectory, token));
            var versions = await nodeReleases.ResolveAsync(missingNodes, ct);
            foreach (var version in versions)
            {
                PromptAssistantCompatibility.ValidatePlannedVersion(version.Id, version.Commit);
                ForbiddenVisionCompatibility.ValidatePlannedVersion(version.Id, version.Commit);
            }
            var byId = versions.ToDictionary(node => node.Id, StringComparer.Ordinal);
            var resolved = runtime with
            {
                Comfy = runtime.Comfy with { Commit = release.Commit }, PythonConstraints = dependencies.Constraints,
                Nodes = runtime.Nodes.Select(repository => byId.TryGetValue(repository.Id, out var node)
                    ? repository with { Url = node.Url, Commit = node.Commit } : repository).ToArray()
            };
            Safety.ValidateRuntime(resolved);
            snapshot = new(plan.ComfyRoot, release, resolved, dependencies) { NodeVersions = versions };
            JsonFile.Write(path, snapshot);
        }
        return plan with { Release = snapshot.Release, ResolvedRuntime = snapshot.Runtime, Dependencies = snapshot.Dependencies, NodeVersions = snapshot.NodeVersions, NodeVersionsResolved = true };
    }

    private static async Task<InstallationPlan> ResolveMissingNodesAsync(InstallationPlan plan, RuntimeManifest runtime,
        string stateDir, CancellationToken ct, NodeReleases? releases)
    {
        if (plan.NodeVersionsResolved && plan.ResolvedRuntime is not null) return plan;
        var path = NodePlanPath(plan.ComfyRoot, stateDir);
        var saved = File.Exists(path) ? JsonFile.Read<NodeInstallSnapshot>(path) : null;
        if (saved is { Completed: false })
        {
            if (!Path.GetFullPath(saved.Root).Equals(Path.GetFullPath(plan.ComfyRoot), StringComparison.OrdinalIgnoreCase)
                || saved.NodeVersions.Any(node => !saved.Runtime.Nodes.Any(repository => repository.Id == node.Id && repository.Url == node.Url && repository.Commit == node.Commit)))
                throw new InvalidDataException("已保存的节点补装计划身份不匹配。");
            Safety.ValidateRuntime(saved.Runtime);
            return plan with { ResolvedRuntime = saved.Runtime, NodeVersions = saved.NodeVersions, NodeVersionsResolved = true };
        }
        runtime = PreserveExistingNodeVersions(plan.ComfyRoot, plan.ResolvedRuntime ?? runtime);
        var missing = Installer.PlanNodes(plan.ComfyRoot, runtime).Where(node => !Directory.Exists(node.Directory)).Select(node => node.Repository).ToArray();
        if (missing.Length == 0) return plan with { ResolvedRuntime = runtime, NodeVersionsResolved = true };
        var git = Commands.Find("git.exe");
        releases ??= new NodeReleases(resolveHead: git is null ? null : (repository, token) =>
            NodeReleases.ResolveHeadWithGit(git, repository, Directory.Exists(stateDir) ? stateDir : Environment.CurrentDirectory, token));
        var versions = await releases.ResolveAsync(missing, ct);
        foreach (var version in versions)
        {
            PromptAssistantCompatibility.ValidatePlannedVersion(version.Id, version.Commit);
            ForbiddenVisionCompatibility.ValidatePlannedVersion(version.Id, version.Commit);
        }
        var byId = versions.ToDictionary(node => node.Id, StringComparer.Ordinal);
        runtime = runtime with { Nodes = runtime.Nodes.Select(repository => byId.TryGetValue(repository.Id, out var node)
            ? repository with { Url = node.Url, Commit = node.Commit } : repository).ToArray() };
        Safety.ValidateRuntime(runtime);
        JsonFile.Write(path, new NodeInstallSnapshot(plan.ComfyRoot, runtime, versions));
        return plan with { ResolvedRuntime = runtime, NodeVersions = versions, NodeVersionsResolved = true };
    }

    /// <summary>Existing managed node versions belong to the environment; only an explicit update may replace them.</summary>
    public static RuntimeManifest PreserveExistingNodeVersions(string root, RuntimeManifest runtime)
    {
        var nodes = Installer.PlanNodes(root, runtime).Select(node =>
        {
            var marker = Path.Combine(node.Directory, ".anima-revision.json");
            if (!File.Exists(marker)) return node.Repository;
            var installed = JsonFile.Read<Repository>(marker);
            if (installed.Id != node.Repository.Id) throw new InvalidDataException("已有节点记录身份不匹配，已保留源码：" + node.Directory);
            Safety.Commit(installed.Commit);
            Safety.Https(installed.Url);
            return node.Repository with { Url = installed.Url, Commit = installed.Commit };
        }).ToArray();
        return runtime with { Nodes = nodes };
    }

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
        ModelManifest? models = null, ModelProfilesManifest? profiles = null, IEnumerable<string>? optionalModelIds = null)
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
        if (!fresh) manifest = PreserveExistingNodeVersions(snapshot.ComfyRoot, manifest);
        if (SameOrInside(snapshot.RuntimeRoot, snapshot.ComfyRoot) || SameOrInside(snapshot.ComfyRoot, snapshot.RuntimeRoot))
            throw new InvalidDataException("运行工具目录与 ComfyUI 目录不能相同或互相包含，请使用同一安装目录下的 ComfyUI 和 runtime。");
        // Legacy callers importing an environment repair nodes only. The wizard explicitly
        // supplies its optional selection (including []) to include required models on import.
        if (optionalModelIds is not null && (models is null || profiles is null))
            throw new InvalidDataException("所选模型需要完整的模型与档案清单。");
        var required = (fresh || optionalModelIds is not null) && models is not null && profiles is not null
            ? SetupModelSelection.Resolve(models, profiles, snapshot.SelectedProfile, optionalModelIds) : [];
        var mandatory = required.Length > 0 ? SetupModelSelection.MandatoryModels(models!, profiles!, snapshot.SelectedProfile) : [];
        // A package target must come from the actual path probe. Until then reserve its
        // byte size on the environment volume, without manufacturing a download directory.
        var deferredBytes = required.Where(model => model.IsPackage && model.CanDownload).Sum(model => model.Size);
        var artifacts = required.Where(m => m.CanDownload && !m.IsPackage).Select(m => new InstallationDownload(
            Safety.Under(Path.Combine(snapshot.ComfyRoot, "models"), m.Category + "/" + m.RelativePath), m.Size, m.Sha256, true)).ToList();
        if (fresh)
        {
            InstallationDownload Cached(Artifact artifact) => new(ArtifactCache.PathFor(snapshot.RuntimeRoot, artifact), artifact.Size, artifact.Sha256, false)
                { LegacyDestinations = ArtifactCache.LegacyPaths(snapshot.RuntimeRoot, artifact), Bundled = BundledTools.IsAvailable(artifact), RepairableStaging = true };
            artifacts.AddRange(manifest.TorchWheels.Select(Cached));
            artifacts.AddRange(manifest.Tools.Where(t => !ToolInstallation.IsReady(snapshot.RuntimeRoot, t))
                .Select(Cached));
            var python = PythonRuntimeInstallation.Resolve(manifest);
            if (!PythonRuntimeInstallation.IsReady(snapshot.RuntimeRoot, python)) artifacts.Add(Cached(python.Artifact));
        }
        var (environmentReserve, runtimeReserve) = RemainingComponentReserves(snapshot, manifest, stateDir, fresh);
        var environmentDownloads = deferredBytes + artifacts.Where(a => a.InEnvironment).Sum(a => a.RemainingAllocationBytes);
        var runtimeDownloads = artifacts.Where(a => !a.InEnvironment).Sum(a => a.RemainingAllocationBytes);
        var networkDownloads = deferredBytes + artifacts.Sum(a => a.RemainingBytes);
        var stages = (fresh
            ? new[] { "检查系统与磁盘", "准备隔离工具", "安装 ComfyUI 与 Python", "安装 CUDA / PyTorch", "安装必要节点", "验证依赖", "下载所选模型" }
            : new[] { "检查现有环境", "补齐缺失节点", "验证依赖", "检查所选模型" }).ToImmutableArray();
        return new(JsonSerializer.Serialize(snapshot, JsonFile.Options), snapshot.ComfyRoot, snapshot.RuntimeRoot,
            snapshot.SelectedProfile, fresh, environmentReserve + environmentDownloads, runtimeReserve + runtimeDownloads, networkDownloads, stages)
        { ResolvedRuntime = fresh ? null : manifest, EnvironmentReserveBytes = environmentReserve, RuntimeReserveBytes = runtimeReserve, Downloads = artifacts.ToImmutableArray(),
            ModelIds = required.Select(model => model.Id).ToImmutableArray(), MandatoryModelIds = mandatory.Select(model => model.Id).ToImmutableArray(), FrozenModels = required,
            FrozenProfile = profiles?.Get(snapshot.SelectedProfile), DeferredModelBytes = deferredBytes, ModelPathsDeferred = deferredBytes > 0,
            RuntimeArtifactsIdentity = RuntimeArtifactPlanning.Identity };
    }

    private static (long Environment, long Runtime) RemainingComponentReserves(LauncherConfig config, RuntimeManifest manifest, string stateDir, bool fresh)
    {
        var completed = ReadCompletedStages(config, manifest, stateDir);
        var nodePlans = Installer.PlanNodes(config.ComfyRoot, manifest);
        var pendingNodes = nodePlans.Count(node => !node.PreserveExisting && !NodeComplete(node, config, manifest));
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

    private static bool NodeComplete(NodeInstallPlan node, LauncherConfig config, RuntimeManifest manifest)
    {
        try
        {
            if (!RepositoryMatches(node.Directory, node.Repository)) return false;
            var file = Path.Combine(node.Directory, ".anima-dependencies-complete");
            // Legacy releases recorded the manifest version. New receipts prove the interpreter,
            // GPU package, source dependency files and ComfyUI commit independently.
            return File.ReadAllText(file).Trim() == manifest.Version
                || RuntimeDependencies.NodeReceiptMatches(file, config, node.Directory, manifest.Comfy.Commit);
        }
        catch { return false; }
    }
    private static bool RepositoryMatches(string path, Repository repo)
    {
        try { var saved = JsonFile.Read<Repository>(Path.Combine(path, ".anima-revision.json")); return saved.Commit == repo.Commit && saved.Id == repo.Id; }
        catch { return false; }
    }
    private static bool ConstraintsMatch(LauncherConfig config, RuntimeManifest manifest)
    {
        try
        {
            var lines = File.ReadAllLines(Path.Combine(config.ComfyRoot, ".anima-constraints.txt"));
            if (lines.SequenceEqual(manifest.PythonConstraints)) return true; // Existing fixed-manifest completion records.
            static Dictionary<string, string> Pins(IEnumerable<string> input) => input.Select(line => line.Split("==", 2))
                .ToDictionary(parts => parts.Length == 2 ? ComfyDependencyResolver.NormalizeName(parts[0]) : throw new InvalidDataException("无效依赖锁定"), parts => parts[1], StringComparer.OrdinalIgnoreCase);
            static bool Matches(string expected, string actual) => expected.Contains('+') ? expected.Equals(actual, StringComparison.OrdinalIgnoreCase)
                : expected.Equals(actual.Split('+')[0], StringComparison.OrdinalIgnoreCase);
            var saved = Pins(lines);
            var expected = Pins(manifest.PythonConstraints);
            if (expected.Count == 0 || !expected.All(pin => saved.TryGetValue(pin.Key, out var value) && Matches(pin.Value, value))) return false;
            // Stable-release resolution expands the GPU-only seed to a full lock. Credit it
            // only after those exact distributions exist, never merely because a lock was written.
            var installed = RuntimeDependencies.InstalledPackages(config.Python);
            return saved.All(pin => installed.TryGetValue(pin.Key, out var value) && Matches(pin.Value, value));
        }
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
        ModelProfilesManifest profiles, string probeScript, CancellationToken ct, bool requireModelPaths = false)
    {
        ct.ThrowIfCancellationRequested();
        if (plan.ResolvedRuntime is not null)
            plan = await RuntimeArtifactPlanning.PrepareAsync(plan, plan.ResolvedRuntime, ct);
        if (!plan.Fresh && plan.ModelIds.IsEmpty) return plan;
        var config = plan.CreateConfig();
        var selected = SelectedModels(plan, models, profiles);
        var needsTagger = ModelProbePolicy.NeedsTaggerRuntime(selected);
        // The review also displays unselected taggers. Runtime uncertainty in this
        // read-only probe affects only their preview paths, not selected downloads.
        var previewTaggers = models.Models.Any(model => model.Category == "wd_taggers");
        var paths = new Dictionary<string, string[]>
        {
            ["diffusion_models"] = [Path.Combine(plan.ComfyRoot, "models", "diffusion_models"), Path.Combine(plan.ComfyRoot, "models", "unet")],
            ["text_encoders"] = [Path.Combine(plan.ComfyRoot, "models", "text_encoders"), Path.Combine(plan.ComfyRoot, "models", "clip")],
        };
        var probe = new ProbeResult(plan.ComfyRoot, config.Python, paths);
        var resolved = false;
        string? python = null;
        try { python = Inventory.FindPython(plan.ComfyRoot, config.Python); }
        catch (FileNotFoundException) { }
        if (python is not null && File.Exists(Path.Combine(plan.ComfyRoot, "folder_paths.py")))
        {
            try
            {
                config.Python = python;
                probe = needsTagger || previewTaggers ? await TaggerRuntime.Probe(config, probeScript, ct)
                    : await Inventory.Probe(plan.ComfyRoot, python, probeScript, ct, config.ComfyArguments);
                resolved = true;
            }
            catch (OperationCanceledException) { throw; }
            catch (InvalidOperationException ex) when (!requireModelPaths && ex.Message.Contains("No module named 'yaml'", StringComparison.Ordinal))
            {
                // Dependency installation can be interrupted before PyYAML exists.
                // Let that stage resume; the UI probes again and checks every real
                // destination volume after dependencies and before model downloads.
                plan = plan with { ModelPathsDeferred = true };
            }
            // An interrupted fresh install may not yet have PyYAML. Without an
            // extra-path config, canonical and legacy paths remain safe defaults.
            catch when (!requireModelPaths && plan.Fresh && !File.Exists(Path.Combine(plan.ComfyRoot, "extra_model_paths.yaml")))
            { plan = plan with { ModelPathsDeferred = true }; }
        }
        if (!resolved && requireModelPaths)
            throw new InvalidDataException("无法确认模型实际下载目录，请先完成环境依赖并重新检查。");
        var mayDefer = !requireModelPaths && (!resolved || needsTagger && TaggerRuntime.ResolveNode(plan.ComfyRoot) is null);
        var prepared = await ResolveModelDestinationsAsync(plan, probe, models, profiles, ct, mayDefer);
        return prepared with { ModelPathsDeferred = prepared.ModelPathsDeferred || !resolved };
    }

    public static Task<InstallationPlan> ResolveModelDestinationsAsync(InstallationPlan plan, ProbeResult probe,
        ModelManifest models, ModelProfilesManifest profiles, CancellationToken ct, bool allowDeferredPackages = false) => Task.Run(async () =>
    {
        ct.ThrowIfCancellationRequested();
        if (!plan.Fresh && plan.ModelIds.IsEmpty) return plan;
        if (!Path.TrimEndingDirectorySeparator(Path.GetFullPath(probe.Root)).Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(plan.ComfyRoot)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("模型目录探测结果与安装目标不一致，请重新检查环境。");
        if (plan.FrozenModels.Length > 0)
        {
            var frozenIds = plan.FrozenModels.Select(model => model.Id).ToHashSet(StringComparer.Ordinal);
            models = models with { Models = models.Models.Where(model => !frozenIds.Contains(model.Id)).Concat(plan.FrozenModels).ToArray() };
        }
        if (plan.FrozenProfile is not null)
            profiles = profiles with { Profiles = profiles.Profiles.Where(profile => profile.Id != plan.SelectedProfile).Append(plan.FrozenProfile).ToArray() };
        var selected = new ModelManifest(models.SchemaVersion, SelectedModels(plan, models, profiles));
        var inventory = await Inventory.Scan(probe, selected, false, ct);
        var artifacts = plan.Downloads.Where(a => !a.InEnvironment).ToList();
        long deferred = 0;
        foreach (var item in inventory.Where(item => item.Model.CanDownload))
        {
            if (string.IsNullOrWhiteSpace(item.Destination))
            {
                if (!allowDeferredPackages) throw new InvalidDataException(item.Model.Name + " 的实际模型目录尚未确认，请检查节点和运行环境后重新扫描。");
                deferred += item.Model.Size;
                continue;
            }
            if (!item.Model.IsPackage)
                artifacts.Add(new(item.FoundPath ?? item.Destination, item.Model.Size, item.Model.Sha256, true));
            else if (item.Missing)
            {
                // Fetch publishes a complete, verified directory atomically. Its existing
                // destination is retained as a backup, so reserve only actual staging files.
                var staging = ModelPackages.Staging(item.Model, item.Destination);
                foreach (var file in item.Model.Files)
                {
                    var target = Safety.Under(staging, file.Path);
                    var damaged = File.Exists(target) && !await Downloads.Verify(target, file.Size, file.Sha256, ct);
                    artifacts.Add(new(target, file.Size, file.Sha256, true)
                        { RepairableStaging = true, RequiresReplacement = damaged });
                }
            }
        }
        // Only selected models above contribute download artifacts and disk budgets.
        // Populate the remaining choices separately so an unreadable/unregistered
        // optional resource cannot block installation or disappear from review.
        var included = inventory.Select(item => item.Model.Id).ToHashSet(StringComparer.Ordinal);
        var previewIds = SetupModelSelection.Choices(models, profiles, plan.SelectedProfile)
            .SelectMany(choice => choice.ModelIds).ToHashSet(StringComparer.Ordinal);
        foreach (var model in models.Models.Where(model => previewIds.Contains(model.Id) && !included.Contains(model.Id)))
        {
            ct.ThrowIfCancellationRequested();
            try { inventory.AddRange(await Inventory.Scan(probe, new(models.SchemaVersion, [model]), false, ct)); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { inventory.Add(new(model, "目录待确认", null, "")); }
        }
        return WithRemainingDownloads(plan with { Downloads = artifacts.ToImmutableArray(), ModelInventory = inventory.ToImmutableArray(), FrozenModels = selected.Models,
            DeferredModelBytes = deferred, ModelPathsDeferred = deferred > 0 });
    }, ct);

    private static ModelItem[] SelectedModels(InstallationPlan plan, ModelManifest models, ModelProfilesManifest profiles)
    {
        if (plan.FrozenModels.Length > 0) return plan.FrozenModels;
        if (plan.ModelIds.IsEmpty) return plan.Fresh ? SetupModelSelection.RequiredForPlan(plan, models, profiles, plan.SelectedProfile) : [];
        var byId = models.Models.ToDictionary(model => model.Id, StringComparer.Ordinal);
        return plan.ModelIds.Distinct().Select(id => byId.TryGetValue(id, out var model) ? model
            : throw new InvalidDataException("安装计划包含已失效的模型，请重新检查：" + id)).ToArray();
    }

    private static InstallationPlan WithRemainingDownloads(InstallationPlan plan)
    {
        var environment = plan.DeferredModelBytes + plan.Downloads.Where(a => a.InEnvironment).Sum(a => a.RemainingAllocationBytes);
        var runtime = plan.Downloads.Where(a => !a.InEnvironment).Sum(a => a.RemainingAllocationBytes);
        return plan with { EnvironmentBytes = plan.EnvironmentReserveBytes + environment, RuntimeBytes = plan.RuntimeReserveBytes + runtime,
            EstimatedDownloadBytes = plan.DeferredModelBytes + plan.Downloads.Sum(a => a.RemainingBytes) };
    }

    public static async Task<InstallationPlan> RefreshSpaceEstimateAsync(InstallationPlan plan, CancellationToken ct)
    {
        // Verification of multi-GB caches must not synchronously block WinUI.
        return await Task.Run(async () =>
        {
            long environment = plan.DeferredModelBytes, runtime = 0, network = plan.DeferredModelBytes;
            var refreshed = ImmutableArray.CreateBuilder<InstallationDownload>(plan.Downloads.Length);
            foreach (var artifact in plan.Downloads)
            {
                ct.ThrowIfCancellationRequested();
                var usableLegacy = new List<string>();
                var verifiedLegacy = new List<string>();
                foreach (var legacy in artifact.LegacyDestinations)
                {
                    ct.ThrowIfCancellationRequested();
                    if (File.Exists(legacy))
                    {
                        if (await global::Anima.Launcher.Core.Downloads.Verify(legacy, artifact.Size, artifact.Sha256, ct))
                        { usableLegacy.Add(legacy); verifiedLegacy.Add(legacy); }
                    }
                    else if (global::Anima.Launcher.Core.Downloads.RemainingBytes(legacy, artifact.Size, artifact.Sha256) < artifact.Size)
                        usableLegacy.Add(legacy);
                }
                var damaged = File.Exists(artifact.Destination)
                    && !await global::Anima.Launcher.Core.Downloads.Verify(artifact.Destination, artifact.Size, artifact.Sha256, ct);
                if (damaged && !artifact.RepairableStaging)
                    throw new InvalidDataException("已有安装文件校验不符，已保留原文件，请备份处理后继续：" + artifact.Destination);
                var current = artifact with { RequiresReplacement = damaged, LegacyDestinations = usableLegacy.ToArray(),
                    VerifiedExisting = File.Exists(artifact.Destination) && !damaged, VerifiedLegacyDestinations = verifiedLegacy.ToArray() };
                refreshed.Add(current);
                var remaining = current.RemainingAllocationBytes;
                network += current.RemainingBytes;
                if (artifact.InEnvironment) environment += remaining; else runtime += remaining;
            }
            return plan with { Downloads = refreshed.ToImmutable(), EnvironmentBytes = plan.EnvironmentReserveBytes + environment,
                RuntimeBytes = plan.RuntimeReserveBytes + runtime, EstimatedDownloadBytes = network };
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
            (Path: plan.ComfyRoot, Bytes: plan.EnvironmentReserveBytes + plan.DeferredModelBytes), (Path: plan.RuntimeRoot, Bytes: plan.RuntimeReserveBytes)
        }.Concat(plan.Downloads.Select(a => (Path: a.Destination, Bytes: a.RemainingAllocationBytes))
            .Where(a => a.Bytes > 0))
        // The short build scratch helper only selects ancestors on this same volume;
        // RuntimeReserveBytes already includes the pip build/extraction allowance.
        .Select(x => (x.Bytes, Root: PlanningVolumeRoot(x.Path)))
        .GroupBy(x => x.Root.Volume, StringComparer.OrdinalIgnoreCase).Select(group =>
        {
            long? available = null;
            // An offline volume still belongs in the review. Never substitute the
            // logical drive's free bytes when its physical target could not resolve.
            if (group.All(x => x.Root.Resolved))
                try { available = DownloadDiskSpace.AvailableBytes(group.Key); } catch { }
            return new VolumeEstimate(group.Key, checked(group.Sum(x => x.Bytes) + 256L * 1024 * 1024), available);
        }).ToImmutableArray();

    private static (string Volume, bool Resolved) PlanningVolumeRoot(string path)
    {
        try { return (DownloadDiskSpace.VolumeRoot(path), true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Display-only fallback. Download admission continues to require the
            // actual volume through DownloadDiskSpace.VolumeRoot / AcquireAsync.
            return (Path.GetPathRoot(Path.GetFullPath(path))!, false);
        }
    }

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
        try { using var temporary = InstallationTemporaryDirectory.Enter(plan.RuntimeRoot); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { issues.Add(new("temporary_directory", ex.Message)); }
        try { plan = await RefreshSpaceEstimateAsync(plan, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { issues.Add(new("cache_invalid", ex.Message)); }
        var volumeBuilder = ImmutableArray.CreateBuilder<VolumeEstimate>();
        foreach (var volume in EstimateVolumes(plan))
        {
            if (volume.AvailableBytes is null) { volumeBuilder.Add(volume); continue; }
            try { volumeBuilder.Add(volume with { AvailableBytes = await DownloadDiskSpace.AvailableForPlanningAsync(volume.Volume, ct) }); }
            catch (OperationCanceledException) { throw; }
            catch { volumeBuilder.Add(volume with { AvailableBytes = null }); }
        }
        var volumes = volumeBuilder.ToImmutable();
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
