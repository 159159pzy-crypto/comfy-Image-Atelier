using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Anima.Launcher.Core;

public sealed class ExternalRepairBinding
{
    public LauncherConfig Previous { get; private set; }
    public bool Published { get; private set; }
    private readonly LauncherConfig target;

    private ExternalRepairBinding(LauncherConfig previous, LauncherConfig target)
    { Previous = previous.Clone(); this.target = target.Clone(); }

    public static ExternalRepairBinding? Begin(LauncherConfig active, LauncherConfig? candidate)
    {
        if (candidate?.ExternalEnvironmentMode != "repair") return null;
        if (active.ExternalEnvironmentMode == "repair" && SetupRouting.TargetsMatch(active, candidate, false)
            && !string.IsNullOrWhiteSpace(active.Python) && LauncherUpdateStore.SamePath(active.Python, candidate.Python)
            && active.ComfyPort == candidate.ComfyPort && active.WebPort == candidate.WebPort) return null;
        return new(active, candidate);
    }

    public void RememberDownloadSettings(LauncherConfig settings) => Previous = DownloadIntegrationPolicy.WithDownloadSettings(Previous, settings);

    public static void SaveActive(string file, LauncherConfig config, ExternalRepairBinding? repair)
    {
        if (repair is not { Published: false }) JsonFile.Write(file, config);
    }

    public void Publish(string file, LauncherConfig candidate, SetupRuntimeReadinessReport report, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!report.RequiredReady || candidate.ExternalEnvironmentMode != "repair" || !SetupRouting.TargetsMatch(target, candidate, false)
            || !LauncherUpdateStore.SamePath(target.Python, candidate.Python)) throw new InvalidOperationException("环境修复及工作台身份尚未验证，保留原环境。");
        candidate.Validate();
        JsonFile.Write(file, candidate);
        Published = true;
    }
}

/// <summary>Owns only the launcher's Python and package cache. The ComfyUI interpreter is its service identity.</summary>
public static class ExternalWorkbench
{
    public static (string Id, string Title)[] ConnectionStages() =>
        [("external-service", "确认已有 ComfyUI 服务身份"), ("workbench", "准备并验证独立工作台"), ("verify", "检查可用功能")];
    public static async Task<SetupRuntimeReadinessReport> VerifyAndSaveConnectionAsync(LauncherConfig candidate, string configurationFile,
        Func<CancellationToken, Task<SetupRuntimeReadinessReport>> verify, CancellationToken ct)
    {
        if (!candidate.ReadOnlyConnection) throw new InvalidOperationException("此操作仅用于已确认的只读接入。");
        candidate.Validate();
        var report = await verify(ct);
        ct.ThrowIfCancellationRequested();
        if (!report.RequiredReady) throw new IOException("运行检查未通过：\n" + string.Join("\n", report.RequiredIssues.Select(issue => issue.Message)));
        JsonFile.Write(configurationFile, candidate);
        return report;
    }

    public static string ResourceBindingsFile(string stateDirectory, LauncherConfig config)
    {
        if (!config.HasIsolatedWorkbench) return Path.Combine(stateDirectory, "resource-paths.json");
        var identity = Path.TrimEndingDirectorySeparator(Path.GetFullPath(config.ComfyRoot)).ToUpperInvariant() + "\n" + config.ComfyPort;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(stateDirectory, "workbench-runtime", "bindings", hash + ".json");
    }

    public static string RuntimeRoot(string stateDirectory, string comfyRoot)
    {
        var root = Path.GetFullPath(Path.Combine(stateDirectory, "workbench-runtime"));
        if (InsideEnvironment(root, comfyRoot))
            throw new InvalidOperationException("只读接入需要独立于 ComfyUI 的启动器状态目录。请调整 ANIMA_LAUNCHER_HOME。");
        return root;
    }

    private static bool InsideEnvironment(string path, string comfyRoot)
    {
        static string ResolveDirectory(string value)
        {
            var full = Path.GetFullPath(value);
            var current = Path.GetPathRoot(full)!;
            foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                var directory = new DirectoryInfo(Path.Combine(current, part));
                current = directory.LinkTarget is null ? directory.FullName : directory.ResolveLinkTarget(true)!.FullName;
            }
            return Path.TrimEndingDirectorySeparator(current);
        }
        var relative = Path.GetRelativePath(ResolveDirectory(comfyRoot), ResolveDirectory(path));
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    internal static void ValidateDataDirectory(LauncherConfig config)
    {
        if (config.ReadOnlyConnection && !string.IsNullOrWhiteSpace(config.DataDir) && !string.IsNullOrWhiteSpace(config.ComfyRoot)
            && InsideEnvironment(config.DataDir, config.ComfyRoot))
            throw new InvalidDataException($"工作台数据目录位于只读环境中：{config.DataDir}。请将工作台数据目录移至此环境之外，再重新连接。");
    }

    public static string Backend(JsonElement stats)
    {
        if (!stats.TryGetProperty("devices", out var devices) || devices.ValueKind != JsonValueKind.Array) return "未识别";
        var backends = new HashSet<string>();
        foreach (var device in devices.EnumerateArray())
        {
            var type = device.TryGetProperty("type", out var t) ? t.ToString().ToLowerInvariant() : "";
            var name = device.TryGetProperty("name", out var n) ? n.ToString().ToLowerInvariant() : "";
            backends.Add(type switch
            {
                "hip" or "rocm" => "ROCm",
                "cuda" when name.Contains("amd") || name.Contains("radeon") || name.Contains("hip") => "ROCm",
                "cuda" => "CUDA", "xpu" => "XPU", "cpu" => "CPU", _ => type.Length > 0 ? type : "未识别"
            });
        }
        return backends.Count == 0 ? "未识别" : string.Join("、", backends);
    }

    public static async Task<InstallationPlan> ReviewConnectionAsync(LauncherConfig config, RuntimeManifest manifest,
        string stateDirectory, CancellationToken ct)
    {
        if (!config.ReadOnlyConnection) throw new InvalidOperationException("请选择只读连接。");
        config.Validate();
        ValidateDataDirectory(config);
        var root = RuntimeRoot(stateDirectory, config.ComfyRoot);
        if (!await ServiceManager.CheckComfyReuseAsync(config, ct))
            throw new IOException($"请先用原整合包启动所选 ComfyUI，再连接 {config.ComfyUrl}。");
        var stats = await ServiceManager.Json(config.ComfyUrl + "/system_stats", ct)
            ?? throw new IOException("无法读取所选 ComfyUI 的设备信息。");
        var nodes = await ServiceManager.Json(config.ComfyUrl + "/object_info", ct);
        if (nodes is not { ValueKind: JsonValueKind.Object })
            throw new IOException("ComfyUI 节点清单尚未就绪。");
        var findings = RuntimeCapabilities.StartupClasses(manifest).Where(id => !nodes.Value.TryGetProperty(id, out _))
            .Select(id => "需要此节点的功能暂不可用：" + id).Prepend("已连接的计算后端：" + Backend(stats)).ToArray();
        return new(JsonSerializer.Serialize(config, JsonFile.Options), config.ComfyRoot, root, config.SelectedProfile, false,
            0, 0, 0, ConnectionStages().Select(stage => stage.Title).ToImmutableArray())
        {
            Purpose = "external-connection", ResourcePolicyVersion = SetupFeatures.PolicyVersion, ResolvedRuntime = manifest,
            Features = ["base"], DependencyReviewIssues = findings
        };
    }

    private static string EnvironmentIdentity(LauncherConfig config, RuntimeManifest manifest)
    {
        var runtime = PythonRuntimeInstallation.Resolve(manifest);
        var project = File.ReadAllBytes(Path.Combine(config.AppRoot, "pyproject.toml"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(config.AppRoot).ToUpperInvariant()
            + "\n" + Convert.ToHexString(SHA256.HashData(project)) + "\n" + runtime.Artifact.Sha256))).ToLowerInvariant();
    }

    internal static string ExpectedPython(LauncherConfig config, RuntimeManifest manifest, string stateDirectory) =>
        Path.Combine(RuntimeRoot(stateDirectory, config.ComfyRoot), "envs", EnvironmentIdentity(config, manifest)[..20], "Scripts", "python.exe");

    public static async Task<LauncherConfig> PrepareAsync(LauncherConfig config, RuntimeManifest manifest, string stateDirectory,
        Downloads downloads, Action<string> log, CancellationToken ct)
    {
        if (!config.HasIsolatedWorkbench) return config;
        var root = RuntimeRoot(stateDirectory, config.ComfyRoot);
        using var lease = await EnvironmentOperationLease.AcquireAsync(root, true, ct);
        var runtime = PythonRuntimeInstallation.Resolve(manifest);
        var identity = EnvironmentIdentity(config, manifest);
        var directory = Path.Combine(root, "envs", identity[..20]);
        var target = config.Clone();
        target.RuntimeRoot = root;
        target.ComfyRoot = directory;
        target.Python = Path.Combine(directory, "Scripts", "python.exe");
        var receipt = Path.Combine(directory, ".workbench-ready");
        if (File.Exists(target.Python) && File.Exists(receipt) && File.ReadAllText(receipt) == identity)
        {
            var report = await WorkbenchDependencies.InspectWorkbenchAsync(target, ct);
            if (report.Ready) return target;
        }

        log("正在准备启动器独立工作台 Python：" + directory);
        Directory.CreateDirectory(root);
        var manager = new Installer(stateDirectory, manifest, downloads, log, null);
        var uv = await manager.Tool("uv", target, ct);
        var env = new Dictionary<string, string> { ["PYTHONHOME"] = "", ["PYTHONPATH"] = "", ["PYTHONNOUSERSITE"] = "1",
            ["PIP_CONSTRAINT"] = "", ["UV_NO_CONFIG"] = "1", ["UV_CACHE_DIR"] = Path.Combine(root, "cache", "uv") };
        NetworkPolicySnapshot.Capture(config).AddEnvironment(env);
        var python = PythonRuntimeInstallation.ExecutablePath(root, runtime);
        if (!PythonRuntimeInstallation.IsReady(root, runtime))
        {
            var archive = await ArtifactCache.FetchAsync(root, runtime.Artifact, config, downloads, null, ct);
            python = await PythonRuntimeInstallation.InstallAsync(uv, root, runtime, archive, log, ct, env);
        }
        if (!File.Exists(target.Python))
            await Commands.Run(python, ["-I", "-m", "venv", directory], root, log, ct, env);
        await Commands.Run(target.Python, ["-Im", "ensurepip"], directory, log, ct, env);
        var seed = new ComfyDependencySnapshot(identity[..40], "", "", []) { ComfySourceKind = "requirements" };
        var plan = await ComfyDependencyResolver.ResolveAsync(target, seed, Path.Combine(root, "cache"), ct,
            log, environment: env, includeWorkbench: true);
        await DependencyInstallation.InstallAsync(target, plan, root, downloads, env, _ => Task.FromResult<string?>(uv), ct, log);
        await WorkbenchDependencies.ValidateAsync(target, ct);
        await File.WriteAllTextAsync(receipt, identity, ct);
        return target;
    }
}
