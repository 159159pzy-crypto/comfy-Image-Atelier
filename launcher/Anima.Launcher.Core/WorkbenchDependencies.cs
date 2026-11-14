using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Anima.Launcher.Core;

public sealed record WorkbenchDependencyContract(string RequirementsText, string Identity);
public sealed record DeclaredDependencyIssue(string State, string Requirement, string Source, string Message);
public sealed record DeclaredDependencyReport(DeclaredDependencyIssue[] Issues, WorkbenchDependencyContract Workbench)
{
    public bool Ready => Issues.Length == 0;
}

/// <summary>One workbench contract and metadata-only probe for installation, import and repair.</summary>
public static class WorkbenchDependencies
{
    private const string ArchiveResource = "Anima.Launcher.Core.packaging-26.0.zip";
    private const string ArchiveSha256 = "e5b1dfcdf8311b0c161ddda81ed7c1db9216ed42d4740a5460b42a03075cf56c";
    private static string Script()
    {
        using var source = typeof(WorkbenchDependencies).Assembly.GetManifestResourceStream("Anima.Launcher.Core.probe_dependencies.py")
            ?? throw new InvalidDataException("启动器缺少依赖检查脚本。");
        using var reader = new StreamReader(source);
        return reader.ReadToEnd();
    }

    private static string PrivateParser()
    {
        using var resource = typeof(WorkbenchDependencies).Assembly.GetManifestResourceStream(ArchiveResource)
            ?? throw new InvalidDataException("启动器缺少私有依赖解析工具。");
        using var memory = new MemoryStream(); resource.CopyTo(memory);
        var bytes = memory.ToArray();
        if (Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != ArchiveSha256)
            throw new InvalidDataException("启动器私有依赖解析工具校验失败。");
        var root = Path.Combine(Path.GetTempPath(), "AnimaLauncher", "dependency-probe", ArchiveSha256);
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "packaging.zip");
        if (File.Exists(path))
        {
            using var existing = File.OpenRead(path);
            if (Convert.ToHexString(SHA256.HashData(existing)).ToLowerInvariant() == ArchiveSha256) return path;
        }
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(temporary, bytes);
        try { File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return path;
    }

    private static async Task<T> Probe<T>(LauncherConfig config, string mode, object request, CancellationToken ct)
    {
        var result = await Commands.Run(config.Python, ["-B", "-c", Script(), PrivateParser(), mode, JsonSerializer.Serialize(request, JsonFile.Options)],
            config.ComfyRoot, null, ct, timeout: TimeSpan.FromSeconds(30));
        return JsonSerializer.Deserialize<T>(result, JsonFile.Options) ?? throw new InvalidDataException("依赖检查未返回有效结果。");
    }

    public static Task<WorkbenchDependencyContract> ReadAsync(LauncherConfig config, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(config.AppRoot) ? Task.FromResult(new WorkbenchDependencyContract("", ""))
        : Probe<WorkbenchDependencyContract>(config, "contract", new { appRoot = config.AppRoot }, ct);

    public static IReadOnlyList<string> RequirementFiles(LauncherConfig config, RuntimeManifest runtime)
    {
        var files = new List<string> { Path.Combine(config.ComfyRoot, "requirements.txt") };
        var manager = Path.Combine(config.ComfyRoot, "manager_requirements.txt");
        if (File.Exists(manager) || ComfyLaunchArguments.ManagerEnabled(config.ComfyArguments)) files.Add(manager);
        files.AddRange(Inventory.ResolveNodeDirectories(config.ComfyRoot, runtime).Values
            .Select(directory => Path.Combine(directory, "requirements.txt")).Where(File.Exists));
        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static Task<DeclaredDependencyReport> InspectAsync(LauncherConfig config, RuntimeManifest runtime, CancellationToken ct) =>
        Probe<DeclaredDependencyReport>(config, "inspect", new { appRoot = config.AppRoot, files = RequirementFiles(config, runtime) }, ct);

    public static Task<DeclaredDependencyReport> InspectWorkbenchAsync(LauncherConfig config, CancellationToken ct) =>
        Probe<DeclaredDependencyReport>(config, "inspect", new { appRoot = config.AppRoot, files = Array.Empty<string>() }, ct);

    public static async Task ValidateAsync(LauncherConfig config, CancellationToken ct)
    {
        var report = await InspectWorkbenchAsync(config, ct);
        if (!report.Ready)
            throw new InvalidOperationException("当前工作台依赖尚未就绪，请通过环境修复或准备功能单独补齐："
                + string.Join("；", report.Issues.Select(issue => issue.Message)));
    }

    public static string WriteRequirements(WorkbenchDependencyContract contract, string cacheRoot)
    {
        var directory = Path.Combine(cacheRoot, "workbench-requirements", contract.Identity.Length > 0 ? contract.Identity : "empty");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "requirements.txt");
        File.WriteAllText(path, contract.RequirementsText, new UTF8Encoding(false));
        return path;
    }
}
