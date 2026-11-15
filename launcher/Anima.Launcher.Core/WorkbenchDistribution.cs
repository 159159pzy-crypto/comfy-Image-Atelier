using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Anima.Launcher.Core;

/// <summary>Frozen inputs for our narrowly scoped editable bridge, never an arbitrary project build.</summary>
public sealed record WorkbenchDistributionPlan(int SchemaVersion, string Name, string Version, string AppRoot,
    string InputIdentity, string Metadata, string RequirementsText, string InstalledIdentity, string InstalledVersion);

public static class WorkbenchDistribution
{
    public const string PackageName = "anima-random-studio";
    internal const string PackageKind = "workbench-editable";
    private sealed record Observation(WorkbenchDistributionPlan Plan, Dictionary<string, string> Files, string InstalledMetadata,
        string PendingTransportUrl, string FinalOriginSha256, string FinalRecordSha256);
    private sealed record AppliedRegistration(string PlanIdentity, string InstalledIdentity);
    private sealed record PendingRegistration(string Root, string Python, string PlanIdentity, WorkbenchDistributionPlan Plan,
        string TransportUrl, string TransportSha256, string OriginalRecordSha256 = "", string FinalOriginSha256 = "", string FinalRecordSha256 = "");
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    private static string Hash(string value) => Hash(Encoding.UTF8.GetBytes(value));

    private static string? PendingFile(LauncherConfig config, string? cacheRoot)
    {
        cacheRoot ??= string.IsNullOrWhiteSpace(config.RuntimeRoot) ? null : Path.Combine(config.RuntimeRoot, "cache");
        return cacheRoot is null ? null : Path.Combine(cacheRoot, "workbench-registration", Hash(Path.GetFullPath(config.ComfyRoot).ToUpperInvariant()
            + "\n" + Path.GetFullPath(config.Python).ToUpperInvariant()) + ".json");
    }

    private static PendingRegistration[] ReadPending(LauncherConfig config, string? cacheRoot)
    {
        var path = PendingFile(config, cacheRoot);
        if (path is null || !File.Exists(path)) return [];
        var pending = JsonFile.Read<PendingRegistration[]>(path);
        if (pending.Length > 2 || pending.Any(item => !Path.GetFullPath(item.Root).Equals(Path.GetFullPath(config.ComfyRoot), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFullPath(item.Python).Equals(Path.GetFullPath(config.Python), StringComparison.OrdinalIgnoreCase)
            || item.Plan.SchemaVersion != 1 || item.Plan.Name != PackageName
            || item.TransportSha256 != Package(item.Plan).Sha256 || !Uri.TryCreate(item.TransportUrl, UriKind.Absolute, out var uri) || !uri.IsFile))
            throw new InvalidDataException("未完成工作台注册记录与本环境不符，已保留。");
        return pending;
    }

    private static void SavePending(LauncherConfig config, string cacheRoot, PendingRegistration[] pending)
    {
        var path = PendingFile(config, cacheRoot)!;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        JsonFile.Write(path, pending);
    }

    private static object Authorization(PendingRegistration intent, bool finalize = false) => new
    {
        intent.Plan, intent.TransportUrl, intent.TransportSha256, intent.OriginalRecordSha256, intent.FinalOriginSha256, intent.FinalRecordSha256, finalize,
        Payload = Payload(intent.Plan).ToDictionary(pair => pair.Key, pair => Hash(pair.Value), StringComparer.Ordinal),
    };

    private static async Task<Observation?> InspectAsync(LauncherConfig config, CancellationToken ct, object? bridge = null, string? cacheRoot = null)
    {
        using var stream = typeof(WorkbenchDistribution).Assembly.GetManifestResourceStream("Anima.Launcher.Core.workbench_distribution.py")
            ?? throw new InvalidDataException("启动器缺少工作台注册检查器。");
        using var reader = new StreamReader(stream);
        var output = await Commands.Run(config.Python, ["-B", "-c", reader.ReadToEnd(), WorkbenchDependencies.PrivateParser(),
            JsonSerializer.Serialize(new { appRoot = config.AppRoot, bridge, authorizations = ReadPending(config, cacheRoot).Select(item => Authorization(item)).ToArray() }, JsonFile.Options)], config.ComfyRoot, null, ct,
            new Dictionary<string, string> { ["PYTHONDONTWRITEBYTECODE"] = "1", ["PYTHONNOUSERSITE"] = "1" }, timeout: TimeSpan.FromSeconds(30));
        return JsonSerializer.Deserialize<Observation>(output, JsonFile.Options);
    }

    public static async Task<WorkbenchDistributionPlan?> PreparePlanAsync(LauncherConfig config, CancellationToken ct, string? cacheRoot = null) =>
        (await InspectAsync(config, ct, cacheRoot: cacheRoot))?.Plan;

    internal static async Task BeginRegistrationAsync(LauncherConfig config, ComfyDependencySnapshot snapshot,
        PreparedDependencyWheel wheel, string cacheRoot, CancellationToken ct)
    {
        var current = await InspectAsync(config, ct, cacheRoot: cacheRoot) ?? throw new InvalidOperationException("工作台注册在安装前缺失。");
        var intent = new PendingRegistration(Path.GetFullPath(config.ComfyRoot), Path.GetFullPath(config.Python),
            DependencyInstallation.PlanIdentity(snapshot), snapshot.WorkbenchDistribution!, new Uri(Path.GetFullPath(wheel.Path)).AbsoluteUri, wheel.Sha256);
        // Retain at most the exact interrupted transport currently on disk, plus this
        // next attempt. Repeated pre-install cancellation cannot lose its ownership.
        var previous = ReadPending(config, cacheRoot).Where(item => item.TransportUrl == current.PendingTransportUrl && item.TransportUrl != intent.TransportUrl).Take(1);
        SavePending(config, cacheRoot, previous.Append(intent).ToArray());
    }

    internal static async Task FinishRegistrationAsync(LauncherConfig config, ComfyDependencySnapshot snapshot,
        PreparedDependencyWheel wheel, string cacheRoot, CancellationToken ct)
    {
        var plan = snapshot.WorkbenchDistribution ?? throw new InvalidDataException("缺少工作台注册计划。");
        var identity = DependencyInstallation.PlanIdentity(snapshot);
        var pending = ReadPending(config, cacheRoot);
        var intent = pending.Single(item => item.PlanIdentity == identity && item.TransportUrl == new Uri(Path.GetFullPath(wheel.Path)).AbsoluteUri);
        var actual = await InspectAsync(config, ct, Authorization(intent), cacheRoot)
            ?? throw new InvalidOperationException("工作台注册尚未安装完整，未完成收尾。");
        intent = intent with { OriginalRecordSha256 = actual.Files[DistributionDirectory(plan) + "/RECORD"],
            FinalOriginSha256 = actual.FinalOriginSha256, FinalRecordSha256 = actual.FinalRecordSha256 };
        SavePending(config, cacheRoot, pending.Select(item => item.TransportUrl == intent.TransportUrl ? intent : item).ToArray());
        actual = await InspectAsync(config, ct, Authorization(intent, finalize: true), cacheRoot)
            ?? throw new InvalidOperationException("工作台注册收尾未完成。");
        var directory = Path.Combine(cacheRoot, "dependency-artifacts", identity);
        Directory.CreateDirectory(directory);
        JsonFile.Write(Path.Combine(directory, "workbench-installed.json"), new AppliedRegistration(identity, actual.Plan.InstalledIdentity));
        File.Delete(PendingFile(config, cacheRoot)!);
    }

    internal static string Identity(WorkbenchDistributionPlan? plan) => plan is null ? "absent" : Hash(JsonSerializer.Serialize(plan, JsonFile.Options));

    /// <summary>Upgrade only our own registration in an older frozen plan; keep every other target and the release fixed.</summary>
    internal static async Task<ComfyDependencySnapshot> BindAsync(LauncherConfig config, ComfyDependencySnapshot plan, CancellationToken ct, string? cacheRoot = null)
    {
        if (plan.WorkbenchDependencyContractVersion == 0) return plan;
        if (plan.WorkbenchDistributionScopeVersion == 1)
        {
            await ValidateAsync(config, plan, ct, cacheRoot);
            return plan;
        }
        if (plan.WorkbenchDistributionScopeVersion != 0) throw new InvalidDataException("不支持工作台注册计划格式。");
        var bridge = await PreparePlanAsync(config, ct, cacheRoot);
        if (bridge is not null && bridge.RequirementsText != plan.WorkbenchRequirementsText)
            throw new InvalidOperationException("当前工作台声明与原固定目标的依赖计划不同，请重新检查同一目标。");
        return Attach(plan, bridge);
    }

    internal static ComfyDependencySnapshot Attach(ComfyDependencySnapshot plan, WorkbenchDistributionPlan? bridge)
    {
        if (bridge is null) return plan with { WorkbenchDistributionScopeVersion = 1, WorkbenchDistribution = null };
        var other = plan.Packages.Where(package => package.Name == PackageName).ToArray();
        if (other.Any(package => package.Kind != PackageKind || package.Version != bridge.Version))
            throw new InvalidOperationException("依赖计划包含另一个工作台包来源，未替换。");
        return plan with { WorkbenchDistributionScopeVersion = 1, WorkbenchDistribution = bridge,
            Packages = plan.Packages.Where(package => package.Name != PackageName).Append(Package(bridge)).ToArray(),
            Constraints = plan.Constraints.Where(line => ComfyDependencyResolver.NormalizeName(line.Split("==", 2)[0]) != PackageName)
                .Append(PackageName + "==" + bridge.Version).Order(StringComparer.Ordinal).ToArray() };
    }

    internal static async Task ValidateAsync(LauncherConfig config, ComfyDependencySnapshot snapshot, CancellationToken ct,
        string? cacheRoot, bool installed = false)
    {
        var frozen = snapshot.WorkbenchDistribution;
        var identity = DependencyInstallation.PlanIdentity(snapshot);
        var receiptPath = cacheRoot is null ? null : Path.Combine(cacheRoot, "dependency-artifacts", identity, "workbench-installed.json");
        var actual = await InspectAsync(config, ct, cacheRoot: cacheRoot);
        if (frozen is null)
        {
            if (actual is not null) throw new InvalidOperationException("检查后新增了工作台注册，原计划未授权修改；请重新检查。");
            return;
        }
        if (frozen.SchemaVersion != 1 || frozen.Name != PackageName || actual is null
            || actual.Plan.AppRoot != frozen.AppRoot || actual.Plan.InputIdentity != frozen.InputIdentity
            || actual.Plan.Metadata != frozen.Metadata || actual.Plan.RequirementsText != frozen.RequirementsText
            || actual.Plan.Version != frozen.Version)
            throw new InvalidOperationException("工作台源码、版本或完整依赖声明在检查后变化，未安装。");
        if (!installed && actual.Plan.InstalledIdentity == frozen.InstalledIdentity) return;
        if (!installed)
        {
            var receipt = receiptPath is not null && File.Exists(receiptPath) ? JsonFile.Read<AppliedRegistration>(receiptPath) : null;
            var pending = ReadPending(config, cacheRoot).Any(item => item.PlanIdentity == identity && item.TransportUrl == actual.PendingTransportUrl);
            if (!pending && (receipt?.PlanIdentity != identity || receipt.InstalledIdentity != actual.Plan.InstalledIdentity))
                throw new InvalidOperationException("工作台已安装元数据或 RECORD 在检查后变化，且没有本计划的安装确认；未授权覆盖。");
            if (pending && actual.Plan.InstalledVersion.Length == 0) return;
        }
        var files = Payload(frozen);
        if (actual.Plan.InstalledVersion != frozen.Version || actual.InstalledMetadata != frozen.Metadata
            || files.Any(pair => !actual.Files.TryGetValue(pair.Key, out var hash) || hash != Hash(pair.Value)))
            throw new InvalidOperationException("工作台已安装元数据或 RECORD 在检查后变化，未授权覆盖。");
        var folder = DistributionDirectory(frozen);
        var allowed = files.Keys.Concat(new[] { folder + "/RECORD", folder + "/INSTALLER", folder + "/REQUESTED", folder + "/direct_url.json", folder + "/uv_cache.json" }).ToHashSet(StringComparer.Ordinal);
        if (actual.Files.Keys.Any(path => !allowed.Contains(path)))
            throw new InvalidOperationException("工作台注册出现计划之外的文件，未授权覆盖。");
        if (installed && receiptPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
            // Record only this exact verified registration, even if a later global pip
            // check fails. A retry cannot accept arbitrary same-version RECORD drift.
            JsonFile.Write(receiptPath, new AppliedRegistration(identity, actual.Plan.InstalledIdentity));
        }
    }

    private static string DistributionDirectory(WorkbenchDistributionPlan plan) => "anima_random_studio-" + plan.Version + ".dist-info";
    internal static DependencyPackage Package(WorkbenchDistributionPlan plan)
    {
        var bytes = WheelBytes(plan);
        return new DependencyPackage { Name = PackageName, Version = plan.Version, Kind = PackageKind,
            Url = new Uri(Path.GetFullPath(plan.AppRoot) + Path.DirectorySeparatorChar).AbsoluteUri, Direct = true,
            FileName = "anima_random_studio-" + plan.Version + "-py3-none-any.whl", Sha256 = Hash(bytes), Size = bytes.Length };
    }

    private static Dictionary<string, byte[]> Payload(WorkbenchDistributionPlan plan)
    {
        var folder = DistributionDirectory(plan);
        // A narrow finder preserves source editing without adding all of AppRoot (and
        // stale egg-info directories) to sys.path. Project code is never imported here.
        var finder = "import sys\nfrom importlib.machinery import PathFinder\n"
            + "_ROOT = " + JsonSerializer.Serialize(plan.AppRoot) + "\n"
            + "class _Finder:\n @classmethod\n def find_spec(cls, fullname, path=None, target=None):\n"
            + "  if fullname == 'anima_webui': return PathFinder.find_spec(fullname, [_ROOT])\n"
            + "def install():\n if _Finder not in sys.meta_path: sys.meta_path.append(_Finder)\n";
        return new(StringComparer.Ordinal)
        {
            [folder + "/METADATA"] = Encoding.UTF8.GetBytes(plan.Metadata),
            [folder + "/WHEEL"] = Encoding.UTF8.GetBytes("Wheel-Version: 1.0\nGenerator: AnimaLauncherEditableBridge/1\nRoot-Is-Purelib: true\nTag: py3-none-any\n\n"),
            ["_anima_workbench_editable.py"] = Encoding.UTF8.GetBytes(finder),
            ["_anima_workbench_editable.pth"] = Encoding.UTF8.GetBytes("import _anima_workbench_editable; _anima_workbench_editable.install()\n"),
        };
    }

    internal static byte[] WheelBytes(WorkbenchDistributionPlan plan)
    {
        var files = Payload(plan);
        var record = string.Join("", files.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + ",sha256="
            + Convert.ToBase64String(SHA256.HashData(pair.Value)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + "," + pair.Value.Length + "\n"))
            + DistributionDirectory(plan) + "/RECORD,,\n";
        files[DistributionDirectory(plan) + "/RECORD"] = Encoding.UTF8.GetBytes(record);
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var pair in files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                var entry = archive.CreateEntry(pair.Key, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var output = entry.Open(); output.Write(pair.Value);
            }
        return memory.ToArray();
    }
}
