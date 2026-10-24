using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Anima.Launcher.Core;

public sealed record RuntimeDependencyProbe(string Python, string PythonTag, string Platform, string Torch,
    bool Cuda, int[] Capabilities, string Manager, bool ManagerImportable, string Triton, bool TritonImportable);
public sealed record TritonWheel(string Version, string Python, string Filename, string Url, long Size, string Sha256);
public sealed record RuntimeDependencyCatalog(int SchemaVersion, string Source, TritonWheel[] Wheels);
public sealed record OptionalDependencyStatus(string Name, bool Available, string Detail);
public sealed record NodeDependencyReceipt(int SchemaVersion, string Fingerprint, string PythonExecutable,
    string PythonIdentity, string PythonFingerprint, string TorchVersion, string ComfyCommit, string NodeCommit = "");

public static class RuntimeDependencies
{
    private const string ProbeCode = """
import importlib.metadata as m, importlib.util, json, platform, sys
import torch
def version(name):
    try: return m.version(name)
    except m.PackageNotFoundError: return ''
def available(name):
    try: return importlib.util.find_spec(name) is not None
    except (ImportError, ValueError): return False
print(json.dumps(dict(python=sys.version, pythonTag='cp'+str(sys.version_info.major)+str(sys.version_info.minor), platform=platform.system(), torch=torch.__version__, cuda=torch.cuda.is_available(), capabilities=[torch.cuda.get_device_capability(i)[0]*10+torch.cuda.get_device_capability(i)[1] for i in range(torch.cuda.device_count())], manager=version('comfyui-manager'), managerImportable=available('comfyui_manager'), triton=version('triton-windows') or version('triton'), tritonImportable=available('triton'))))
""";

    public static async Task<RuntimeDependencyProbe> Probe(LauncherConfig config, CancellationToken ct)
    {
        var output = await Commands.Run(config.Python, ["-B", "-c", ProbeCode], config.ComfyRoot, null, ct, timeout: TimeSpan.FromMinutes(2));
        return JsonSerializer.Deserialize<RuntimeDependencyProbe>(output.Trim().Split('\n').Last(), JsonFile.Options)
            ?? throw new InvalidDataException("无法读取 Python / PyTorch 依赖状态。");
    }

    public static RuntimeDependencyCatalog Catalog()
    {
        using var input = typeof(RuntimeDependencies).Assembly.GetManifestResourceStream("Anima.Launcher.Core.runtime-dependencies.json")!;
        return JsonSerializer.Deserialize<RuntimeDependencyCatalog>(input, JsonFile.Options)!;
    }

    public static TritonWheel? SelectTriton(RuntimeDependencyProbe probe, out string reason)
    {
        reason = "";
        if (probe.Platform != "Windows" || !probe.Cuda || probe.Capabilities.Length == 0 || probe.Capabilities.Any(capability => capability < 80))
        {
            reason = "PatchTritonVAE 自动修复支持 Windows NVIDIA Ampere 或更新显卡；当前环境保留基础生图功能。";
            return null;
        }
        var version = Regex.Match(probe.Torch, @"^2\.(\d+)(?:\.|\+|$)");
        var minor = version.Success ? int.Parse(version.Groups[1].Value) : -1;
        var target = minor switch { 10 or 11 => "3.6.0.post26", 12 or 13 => "3.7.1.post27", _ => "" };
        var wheel = Catalog().Wheels.SingleOrDefault(item => item.Version == target && item.Python == probe.PythonTag);
        if (wheel is null) reason = $"PatchTritonVAE 尚无已验证的 Triton 组合：{probe.PythonTag} / PyTorch {probe.Torch}；不会更换现有 PyTorch。";
        return wheel;
    }

    public static string Receipt(string directory, RuntimeDependencyProbe probe, string comfyCommit)
        => Receipt(directory, probe.Python, probe.Torch, comfyCommit);

    private static string Receipt(string directory, string python, string torch, string comfyCommit)
    {
        var inputs = new StringBuilder(comfyCommit).Append('\n').Append(python).Append('\n').Append(torch);
        var requirements = Path.Combine(directory, "requirements.txt");
        var inputsFiles = File.Exists(requirements) ? ComfyDependencyResolver.RequirementFiles([requirements]).ToList() : [];
        var installer = Path.Combine(directory, "install.py");
        if (File.Exists(installer)) inputsFiles.Add(installer);
        foreach (var path in inputsFiles)
        {
            inputs.Append('\n').Append(Path.GetRelativePath(directory, path)).Append('\n').Append(File.ReadAllText(path));
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(inputs.ToString())));
    }

    public static NodeDependencyReceipt CreateNodeReceipt(LauncherConfig config, string directory, RuntimeDependencyProbe probe, string comfyCommit) =>
        new(1, Receipt(directory, probe, comfyCommit), Path.GetFullPath(config.Python), probe.Python,
            PythonFingerprint(config.Python), probe.Torch, comfyCommit, NodeCommit(directory) ?? "");

    public static bool NodeReceiptMatches(string file, LauncherConfig config, string directory, string comfyCommit)
    {
        try
        {
            var receipt = JsonFile.Read<NodeDependencyReceipt>(file);
            return receipt.SchemaVersion == 1 && receipt.ComfyCommit == comfyCommit
                && receipt.NodeCommit.Length > 0 && receipt.NodeCommit == NodeCommit(directory)
                && Path.GetFullPath(receipt.PythonExecutable).Equals(Path.GetFullPath(config.Python), StringComparison.OrdinalIgnoreCase)
                && receipt.PythonFingerprint == PythonFingerprint(config.Python)
                && InstalledPackages(config.Python).TryGetValue("torch", out var torch) && torch == receipt.TorchVersion
                && receipt.Fingerprint == Receipt(directory, receipt.PythonIdentity, receipt.TorchVersion, comfyCommit);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        { return false; }
    }

    /// <summary>Read detached, loose-ref or packed-ref HEAD without invoking Git. Non-Git trees are not cached.</summary>
    public static string? NodeCommit(string directory)
    {
        try
        {
            var git = Path.Combine(directory, ".git");
            if (File.Exists(git))
            {
                var pointer = File.ReadAllText(git).Trim();
                if (!pointer.StartsWith("gitdir: ", StringComparison.Ordinal)) return null;
                git = Path.GetFullPath(Path.Combine(directory, pointer[8..].Trim()));
            }
            if (!Directory.Exists(git)) return null;
            var head = File.ReadAllText(Path.Combine(git, "HEAD")).Trim();
            if (Regex.IsMatch(head, "^[a-f0-9]{40}$")) return head;
            if (!head.StartsWith("ref: refs/", StringComparison.Ordinal)) return null;
            var reference = head[5..];
            var commonFile = Path.Combine(git, "commondir");
            var common = File.Exists(commonFile) ? Path.GetFullPath(Path.Combine(git, File.ReadAllText(commonFile).Trim())) : git;
            var loose = Safety.Under(common, reference);
            if (File.Exists(loose))
            {
                var commit = File.ReadAllText(loose).Trim();
                return Regex.IsMatch(commit, "^[a-f0-9]{40}$") ? commit : null;
            }
            var packed = Path.Combine(common, "packed-refs");
            if (!File.Exists(packed)) return null;
            foreach (var line in File.ReadLines(packed))
            {
                var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[1] == reference && Regex.IsMatch(parts[0], "^[a-f0-9]{40}$")) return parts[0];
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    private static string PythonDirectory(string python)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(python))!;
        return Path.GetFileName(directory).Equals("Scripts", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(directory)! : directory;
    }

    private static string PythonFingerprint(string python)
    {
        using var input = File.OpenRead(python);
        var executableHash = Convert.ToHexString(SHA256.HashData(input));
        var directory = PythonDirectory(python);
        var config = Path.Combine(directory, "pyvenv.cfg");
        var inputs = new StringBuilder(executableHash).Append('\n').Append(File.Exists(config) ? File.ReadAllText(config) : "");
        foreach (var path in Directory.EnumerateFiles(directory, "python*._pth").Order(StringComparer.OrdinalIgnoreCase))
            inputs.Append('\n').Append(Path.GetFileName(path)).Append('\n').Append(File.ReadAllText(path));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(inputs.ToString())));
    }

    /// <summary>Read distribution metadata without starting Python or loading third-party nodes.</summary>
    public static Dictionary<string, string> InstalledPackages(string python)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var packages = Path.Combine(PythonDirectory(python), "Lib", "site-packages");
        if (!Directory.Exists(packages)) return result;
        foreach (var directory in Directory.EnumerateDirectories(packages, "*.dist-info"))
        {
            var metadata = Path.Combine(directory, "METADATA");
            if (!File.Exists(metadata) || !File.Exists(Path.Combine(directory, "RECORD"))) continue;
            string? name = null, version = null;
            foreach (var line in File.ReadLines(metadata))
            {
                if (line.StartsWith("Name: ", StringComparison.OrdinalIgnoreCase)) name = line[6..].Trim();
                if (line.StartsWith("Version: ", StringComparison.OrdinalIgnoreCase)) version = line[9..].Trim();
                if (name is not null && version is not null) break;
            }
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version)) continue;
            var key = ComfyDependencyResolver.NormalizeName(name);
            // Multiple distributions for one package indicate an incomplete/ambiguous install.
            if (result.ContainsKey(key)) result[key] = ""; else result[key] = version;
        }
        return result;
    }

    public static async Task ValidateManager(LauncherConfig config, CancellationToken ct)
    {
        var requirements = Path.Combine(config.ComfyRoot, "manager_requirements.txt");
        if (!File.Exists(requirements)) throw new InvalidOperationException("此 ComfyUI 版本未提供 manager_requirements.txt，请先更新到支持内置 Manager 的稳定版。");
        const string script = """
import importlib.metadata as m, pathlib, sys
from packaging.requirements import Requirement
for line in pathlib.Path(sys.argv[1]).read_text(encoding='utf-8-sig').splitlines():
    line = line.split('#', 1)[0].strip()
    if not line: continue
    requirement = Requirement(line)
    if requirement.marker and not requirement.marker.evaluate(): continue
    actual = m.version(requirement.name)
    if not requirement.specifier.contains(actual, prereleases=True): raise RuntimeError(f'{requirement.name}: required {requirement.specifier}, installed {actual}')
import comfyui_manager
print('Manager dependencies verified')
""";
        await Commands.Run(config.Python, ["-B", "-c", script, requirements], config.ComfyRoot, null, ct, timeout: TimeSpan.FromMinutes(2));
    }

    public static async Task ValidateTriton(LauncherConfig config, string runtimeRoot, CancellationToken ct, Action<string>? log = null)
    {
        var cache = Path.Combine(runtimeRoot, "cache", "runtime-checks");
        Directory.CreateDirectory(cache);
        var path = Path.Combine(cache, "check_triton.py");
        using var resource = typeof(RuntimeDependencies).Assembly.GetManifestResourceStream("Anima.Launcher.Core.check_triton.py")!;
        using (var reader = new StreamReader(resource)) await File.WriteAllTextAsync(path, await reader.ReadToEndAsync(ct), ct);
        await Commands.Run(config.Python, ["-B", path], config.ComfyRoot, log, ct, new Dictionary<string, string>
        {
            ["TRITON_CACHE_DIR"] = Path.Combine(cache, "triton"),
            ["TORCHINDUCTOR_CACHE_DIR"] = Path.Combine(cache, "inductor")
        }, timeout: TimeSpan.FromMinutes(3));
    }
}
