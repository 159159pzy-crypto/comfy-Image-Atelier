using System.Security.Cryptography;
using System.Text.Json;

namespace Anima.Launcher.Core;

public sealed record PythonRuntimeArtifact(string Implementation, string Version, string Build, string Platform,
    string Architecture, string InstallationKey, Artifact Artifact)
{
    public void Validate(string requestedVersion)
    {
        Safety.ValidateArtifact(Artifact);
        if (Implementation != "cpython" || Platform != "windows" || Architecture != "x86_64"
            || Version != requestedVersion || !System.Version.TryParse(Version, out _)
            || !System.Text.RegularExpressions.Regex.IsMatch(Build, "^[0-9]{8}$")
            || InstallationKey != $"cpython-{Version}-windows-x86_64-none" || Artifact.ArchiveFormat != "tar.gz")
            throw new InvalidDataException("Python 归档必须与已锁定的版本、构建和 Windows x64 平台一致。");
    }
}

public sealed record InstalledPythonReceipt(string InstallationKey, string Build, string ArchiveSha256, long ArchiveSize, string ExecutableSha256);

public static class PythonRuntimeInstallation
{
    private static readonly Lazy<PythonRuntimeArtifact> Baseline = new(() =>
    {
        using var stream = typeof(PythonRuntimeInstallation).Assembly.GetManifestResourceStream("Anima.Launcher.Core.python-runtime.json")
            ?? throw new InvalidDataException("缺少锁定的 Python 运行时清单。");
        return JsonSerializer.Deserialize<PythonRuntimeArtifact>(stream, JsonFile.Options)
            ?? throw new InvalidDataException("Python 运行时清单无效。");
    });

    /// <summary>Old snapshots retain their exact version; only the known historical baseline is upgraded with its archive identity.</summary>
    public static PythonRuntimeArtifact Resolve(RuntimeManifest manifest)
    {
        var runtime = manifest.PythonRuntime ?? Baseline.Value;
        runtime.Validate(manifest.PythonVersion);
        return runtime;
    }
    public static string ExecutablePath(string runtimeRoot, PythonRuntimeArtifact runtime) =>
        Safety.Under(Path.Combine(runtimeRoot, "python"), runtime.InstallationKey + "/python.exe");
    private static string ReceiptPath(string runtimeRoot, PythonRuntimeArtifact runtime) =>
        Safety.Under(Path.Combine(runtimeRoot, "python"), ".anima-" + runtime.InstallationKey + ".json");

    public static bool IsReady(string runtimeRoot, PythonRuntimeArtifact runtime)
    {
        try
        {
            var receipt = JsonFile.Read<InstalledPythonReceipt>(ReceiptPath(runtimeRoot, runtime));
            if (receipt.InstallationKey != runtime.InstallationKey || receipt.Build != runtime.Build
                || receipt.ArchiveSha256 != runtime.Artifact.Sha256 || receipt.ArchiveSize != runtime.Artifact.Size) return false;
            using var stream = File.OpenRead(ExecutablePath(runtimeRoot, runtime));
            return Convert.ToHexString(SHA256.HashData(stream)).Equals(receipt.ExecutableSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static string WriteLocalMetadata(string runtimeRoot, PythonRuntimeArtifact runtime, string verifiedArchive)
    {
        runtime.Validate(runtime.Version);
        var version = System.Version.Parse(runtime.Version);
        var entry = new Dictionary<string, object?>
        {
            ["name"] = runtime.Implementation, ["arch"] = new { family = runtime.Architecture, variant = (string?)null },
            ["os"] = runtime.Platform, ["libc"] = "none", ["major"] = version.Major, ["minor"] = version.Minor,
            ["patch"] = version.Build, ["prerelease"] = "", ["variant"] = null, ["build"] = runtime.Build,
            ["url"] = new Uri(Path.GetFullPath(verifiedArchive)).AbsoluteUri, ["sha256"] = runtime.Artifact.Sha256
        };
        var path = Path.Combine(runtimeRoot, "cache", "python-metadata", runtime.Artifact.Sha256 + ".json");
        JsonFile.Write(path, new Dictionary<string, object> { [runtime.InstallationKey] = entry });
        return path;
    }

    public static async Task<string> InstallAsync(string uv, string runtimeRoot, PythonRuntimeArtifact runtime, string verifiedArchive,
        Action<string>? log, CancellationToken ct, IDictionary<string, string>? environment = null)
    {
        if (!await Downloads.Verify(verifiedArchive, runtime.Artifact.Size, runtime.Artifact.Sha256, ct))
            throw new InvalidDataException("Python 安装前归档校验失败。");
        if (await Task.Run(() => IsReady(runtimeRoot, runtime), ct)) return ExecutablePath(runtimeRoot, runtime);
        var metadata = WriteLocalMetadata(runtimeRoot, runtime, verifiedArchive);
        var env = environment is null ? new Dictionary<string, string>() : new Dictionary<string, string>(environment);
        env["UV_PYTHON_INSTALL_DIR"] = Path.Combine(runtimeRoot, "python");
        env["UV_PYTHON_INSTALL_MIRROR"] = "";
        env["UV_PYTHON_DOWNLOADS_JSON_URL"] = metadata;
        env["UV_PYTHON_DOWNLOADS"] = "manual";
        env["UV_CACHE_DIR"] = Path.Combine(runtimeRoot, "cache", "uv");
        env["TEMP"] = env["TMP"] = Path.Combine(runtimeRoot, "temp");
        Directory.CreateDirectory(env["TEMP"]);
        Directory.CreateDirectory(env["UV_PYTHON_INSTALL_DIR"]);
        await Commands.Run(uv, ["python", "install", runtime.InstallationKey, "--python-downloads-json-url", metadata,
            "--offline", "--no-config", "--no-bin", "--no-registry", "--reinstall"], runtimeRoot, log, ct, env);
        var executable = ExecutablePath(runtimeRoot, runtime);
        var version = (await Commands.Run(executable, ["-I", "-c", "import platform,struct;print(platform.python_version()+'|'+str(struct.calcsize('P')*8))"], runtimeRoot, null, ct, env)).Trim();
        if (version != runtime.Version + "|64") throw new InvalidDataException("安装的 Python 版本或架构与锁定归档不同。");
        await using var input = File.OpenRead(executable);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, ct));
        JsonFile.Write(ReceiptPath(runtimeRoot, runtime), new InstalledPythonReceipt(runtime.InstallationKey, runtime.Build,
            runtime.Artifact.Sha256, runtime.Artifact.Size, hash));
        return executable;
    }
}
