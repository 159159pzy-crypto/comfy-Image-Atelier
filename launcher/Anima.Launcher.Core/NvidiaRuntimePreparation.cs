using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Anima.Launcher.Core;

public sealed record NvidiaRuntimeFile(string FileName, long Size, string Sha256);
public sealed record NvidiaDlssRuntime(string Generation, Artifact Archive, NvidiaRuntimeFile Dll);
public sealed record NvidiaRuntimeManifest(int SchemaVersion, Artifact[] VfxWheels, Artifact SevenZipBootstrap,
    Artifact SevenZipArchive, string SevenZipExeSha256, string SevenZipDllSha256, Artifact VcRuntime,
    Artifact DlssArchive, NvidiaRuntimeFile DlssHost, NvidiaDlssRuntime[] DlssRuntimes);
public sealed record NvidiaRuntimeProbe(bool Supported, string Generation, string Gpu, string PythonTag,
    string PythonVersion, string TorchVersion, string VfxVersion, bool VcRuntime);
public sealed record NvidiaRuntimeStatus(bool Supported, bool Ready, string Generation, string Detail)
{
    public string ComfyRoot { get; init; } = "";
    public string Python { get; init; } = "";
    public string Gpu { get; init; } = "";
    public string DlssRoot { get; init; } = "";
    public string PythonIdentity { get; init; } = "";
    public DateTimeOffset CheckedUtc { get; init; }
}

/// <summary>Prepare fixed native assets, then prove VFX and DLSS can execute on this machine.</summary>
public static class NvidiaRuntimePreparation
{
    private static readonly Regex VfxRequirement = new(@"(?m)^[ \t]*nvidia[-_]vfx(?:[ \t]*[<>=!~][^\r\n#;]*)?[ \t]*(?:#[^\r\n]*)?\r?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Lazy<NvidiaRuntimeManifest> Manifest = new(() =>
    {
        using var resource = typeof(NvidiaRuntimePreparation).Assembly.GetManifestResourceStream("Anima.Launcher.Core.nvidia-runtime.json")
            ?? throw new InvalidOperationException("安装包缺少 NVIDIA 运行库清单。");
        var value = JsonSerializer.Deserialize<NvidiaRuntimeManifest>(resource, JsonFile.Options)
            ?? throw new InvalidDataException("NVIDIA 运行库清单无效。");
        if (value.SchemaVersion != 1 || value.VfxWheels.Length != 3 || value.DlssRuntimes.Length != 3) throw new InvalidDataException("NVIDIA 运行库清单版本无效。");
        foreach (var artifact in value.VfxWheels.Concat(new[] { value.SevenZipBootstrap, value.SevenZipArchive, value.VcRuntime, value.DlssArchive }).Concat(value.DlssRuntimes.Select(item => item.Archive))) Safety.ValidateArtifact(artifact);
        return value;
    });
    public static NvidiaRuntimeManifest Catalog() => Manifest.Value;
    public static string RequirementsIdentity => string.Join("|", Catalog().VfxWheels.Select(wheel => wheel.Sha256));
    public static bool HasVfxRequirement(string text) => VfxRequirement.IsMatch(text);
    public static string PinRequirements(string text)
    {
        if (!HasVfxRequirement(text)) return text;
        var lines = Catalog().VfxWheels.Select(wheel =>
        {
            var marker = wheel.FileName.Contains("cp310-cp310") ? "python_version == '3.10'" : wheel.FileName.Contains("cp311-cp311") ? "python_version == '3.11'" : "python_version >= '3.12'";
            return $"nvidia-vfx @ {wheel.Url}#sha256={wheel.Sha256} ; sys_platform == 'win32' and platform_machine == 'AMD64' and {marker}";
        });
        return VfxRequirement.Replace(text, string.Join("\n", lines));
    }
    public static Artifact? WheelFor(string pythonTag) => Catalog().VfxWheels.SingleOrDefault(wheel =>
        pythonTag is "cp310" or "cp311" ? wheel.FileName.Contains(pythonTag + "-" + pythonTag)
        : int.TryParse(pythonTag.StartsWith("cp3") ? pythonTag[3..] : "", out var minor) && minor >= 12 && wheel.FileName.Contains("cp312-abi3"));

    public static bool IsReadyFor(NvidiaRuntimeProbe probe, NvidiaRuntimeStatus? status) => !probe.Supported ||
        status is { Supported: true, Ready: true } && status.Generation == probe.Generation && status.Gpu == probe.Gpu
        && probe.VcRuntime && WheelFor(probe.PythonTag)?.Version == probe.VfxVersion;

    private static string StatePath(LauncherConfig config, string runtimeRoot)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(config.ComfyRoot).ToUpperInvariant())))[..24];
        return Path.Combine(runtimeRoot, "nvidia-runtime", key, "status.json");
    }
    private static string InterpreterIdentity(string python)
    {
        var info = new FileInfo(python);
        return info.Exists ? $"{Path.GetFullPath(python)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}" : "";
    }
    public static NvidiaRuntimeStatus? ReadStatus(LauncherConfig config, string runtimeRoot)
    {
        try
        {
            var path = StatePath(config, runtimeRoot);
            if (!File.Exists(path)) return null;
            var status = JsonFile.Read<NvidiaRuntimeStatus>(path);
            return status.ComfyRoot.Equals(Path.GetFullPath(config.ComfyRoot), StringComparison.OrdinalIgnoreCase)
                && status.Python.Equals(Path.GetFullPath(config.Python), StringComparison.OrdinalIgnoreCase)
                && status.PythonIdentity == InterpreterIdentity(config.Python) ? status : null;
        }
        catch (Exception error) when (error is IOException or JsonException or ArgumentException) { return null; }
    }
    public static async Task<NvidiaRuntimeStatus?> ValidateStatusAsync(LauncherConfig config, string runtimeRoot, CancellationToken ct = default)
    {
        var status = ReadStatus(config, runtimeRoot);
        if (status is not { Ready: true }) return status;
        var runtime = Catalog().DlssRuntimes.SingleOrDefault(item => item.Generation == status.Generation);
        if (runtime is null || ResolveDlssDirectory(config.ComfyRoot) is not { } node
            || !Path.GetFullPath(node).Equals(Path.GetFullPath(status.DlssRoot), StringComparison.OrdinalIgnoreCase))
            return status with { Ready = false, Detail = "NVIDIA 运行库验证不属于当前节点或显卡，请重新检查安装。" };
        if (!await Verify(Path.Combine(status.DlssRoot, "vendor", Catalog().DlssHost.FileName), Catalog().DlssHost, ct)
            || !await Verify(Path.Combine(status.DlssRoot, "vendor", runtime.Generation, runtime.Dll.FileName), runtime.Dll, ct))
            return status with { Ready = false, Detail = "NVIDIA 运行文件缺失或校验失败，请在启动器重试安装。" };
        return status;
    }

    private static async Task<string> Script(string runtimeRoot, CancellationToken ct)
    {
        using var resource = typeof(NvidiaRuntimePreparation).Assembly.GetManifestResourceStream("Anima.Launcher.Core.check_nvidia_runtime.py")
            ?? throw new InvalidOperationException("安装包缺少 NVIDIA 验证脚本。");
        using var reader = new StreamReader(resource, Encoding.UTF8);
        var content = await reader.ReadToEndAsync(ct);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..16];
        var path = Path.Combine(runtimeRoot, "tools", "check-nvidia-" + hash + ".py");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), ct);
        return path;
    }
    private static T ParseOutput<T>(string output)
    {
        foreach (var line in output.Replace("\r", "").Split('\n').Reverse())
        {
            if (!line.StartsWith('{')) continue;
            try { if (JsonSerializer.Deserialize<T>(line, JsonFile.Options) is { } value) return value; }
            catch (JsonException) { }
        }
        throw new InvalidDataException("NVIDIA 检查未返回有效结果。");
    }
    public static async Task<NvidiaRuntimeProbe> ProbeAsync(LauncherConfig config, string runtimeRoot, CancellationToken ct)
    {
        var script = await Script(runtimeRoot, ct);
        return ParseOutput<NvidiaRuntimeProbe>(await Commands.Run(config.Python, ["-B", script, "--probe"], config.ComfyRoot, null, ct, timeout: TimeSpan.FromMinutes(1)));
    }
    public static string? ResolveDlssDirectory(string comfyRoot)
    {
        var custom = Path.Combine(comfyRoot, "custom_nodes");
        if (!Directory.Exists(custom)) return null;
        var candidates = Directory.EnumerateDirectories(custom).Where(path => !Path.GetFileName(path).StartsWith('.')
            && !new[] { ".disabled", ".anima-installing", ".extracting" }.Any(suffix => path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            && File.Exists(Path.Combine(path, "dlss_engine.py")) && File.Exists(Path.Combine(path, "vendor_dlls.py"))).ToArray();
        if (candidates.Length > 1) throw new InvalidDataException("发现多个 DLSS5 节点目录，请只保留一个有效安装。");
        return candidates.SingleOrDefault();
    }

    public static async Task<NvidiaRuntimeStatus> PrepareAsync(LauncherConfig config, string runtimeRoot, Downloads downloads,
        Action<string> log, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var probe = await ProbeAsync(config, runtimeRoot, ct);
        var status = new NvidiaRuntimeStatus(probe.Supported, false, probe.Generation,
            probe.Supported ? "正在准备 NVIDIA 运行库。" : "当前设备不支持 RTX 30/40/50 的 N 卡放大与 DLSS；基础生图仍可使用。")
        { ComfyRoot = Path.GetFullPath(config.ComfyRoot), Python = Path.GetFullPath(config.Python), PythonIdentity = InterpreterIdentity(config.Python), Gpu = probe.Gpu, CheckedUtc = DateTimeOffset.UtcNow };
        var statePath = StatePath(config, runtimeRoot);
        JsonFile.Write(statePath, status);
        if (!probe.Supported) { log(status.Detail); return status; }
        try
        {
            var catalog = Catalog();
            var wheel = WheelFor(probe.PythonTag) ?? throw new InvalidOperationException("当前 Python ABI 没有已验证的 NVIDIA VFX 运行库；保留现有 Python 与 Torch。");
            if (!probe.VcRuntime)
            {
                var installer = await ArtifactCache.FetchAsync(runtimeRoot, catalog.VcRuntime, config, downloads, progress, ct);
                try { await Commands.Run(installer, ["/install", "/quiet", "/norestart"], runtimeRoot, log, ct, timeout: TimeSpan.FromMinutes(5)); }
                catch (CommandFailureException error) when (error.ExitCode == 3010) { log("Visual C++ 运行库安装完成，系统提示需要重启；继续检查当前是否可用。"); }
                var afterVc = await ProbeAsync(config, runtimeRoot, ct);
                if (!afterVc.VcRuntime) throw new InvalidOperationException("Visual C++ 运行库尚不可用；请按系统提示完成安装或重启后重试。");
            }
            if (probe.VfxVersion != wheel.Version)
            {
                var file = await ArtifactCache.FetchAsync(runtimeRoot, wheel, config, downloads, progress, ct);
                await Commands.Run(config.Python, ["-m", "pip", "install", "--no-index", "--no-deps", "--disable-pip-version-check", file], config.ComfyRoot, log, ct, timeout: TimeSpan.FromMinutes(5));
            }
            var node = ResolveDlssDirectory(config.ComfyRoot) ?? throw new InvalidOperationException("缺少 DLSS5 节点，请先补齐节点安装。");
            status = status with { DlssRoot = node };
            var runtime = catalog.DlssRuntimes.Single(item => item.Generation == probe.Generation);
            var host = Path.Combine(node, "vendor", catalog.DlssHost.FileName);
            var dll = Path.Combine(node, "vendor", runtime.Generation, runtime.Dll.FileName);
            if (!await Verify(host, catalog.DlssHost, ct) || runtime.Generation == "rtx40" && !await Verify(dll, runtime.Dll, ct))
            {
                var archive = await ArtifactCache.FetchAsync(runtimeRoot, catalog.DlssArchive, config, downloads, progress, ct);
                await ExtractZipMember(archive, host, catalog.DlssHost, ct);
                if (runtime.Generation == "rtx40") await ExtractZipMember(archive, dll, runtime.Dll, ct);
            }
            if (!await Verify(dll, runtime.Dll, ct))
            {
                var sevenZip = await PrepareSevenZip(runtimeRoot, config, downloads, progress, log, ct);
                var archive = await ArtifactCache.FetchAsync(runtimeRoot, runtime.Archive, config, downloads, progress, ct);
                var staging = Path.Combine(runtimeRoot, "nvidia-runtime", "extract-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(staging);
                await Commands.Run(sevenZip, ["e", archive, "-o" + staging, runtime.Dll.FileName, "-r", "-y"], runtimeRoot, log, ct, timeout: TimeSpan.FromMinutes(3));
                await Publish(Path.Combine(staging, runtime.Dll.FileName), dll, runtime.Dll, ct);
            }
            var script = await Script(runtimeRoot, ct);
            var result = await Commands.Run(config.Python, ["-B", script, "--dlss-root", node, "--state-dir", Path.GetDirectoryName(statePath)!, "--generation", runtime.Generation], config.ComfyRoot, log, ct, timeout: TimeSpan.FromMinutes(5));
            using var checkedResult = JsonDocument.Parse(ParseOutput<JsonElement>(result).GetRawText());
            if (!checkedResult.RootElement.TryGetProperty("ready", out var ready) || !ready.GetBoolean()) throw new InvalidOperationException("NVIDIA 实际处理验证未通过。");
            status = status with { Ready = true, Detail = "NVIDIA VFX 与 DLSS 已完成真实图像处理验证。", CheckedUtc = DateTimeOffset.UtcNow };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            status = status with { Ready = false, Detail = "NVIDIA 运行库尚未就绪：" + DiagnosticRedaction.Text(error.Message), CheckedUtc = DateTimeOffset.UtcNow };
        }
        JsonFile.Write(statePath, status);
        log(status.Detail);
        return status;
    }

    private static Task<bool> Verify(string path, NvidiaRuntimeFile file, CancellationToken ct) => Downloads.Verify(path, file.Size, file.Sha256, ct);
    private static async Task Publish(string source, string destination, NvidiaRuntimeFile expected, CancellationToken ct)
    {
        if (!await Verify(source, expected, ct)) throw new InvalidDataException("NVIDIA DLL 校验失败：" + expected.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination) && (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0) throw new IOException("NVIDIA DLL 是链接，已保留。");
        var staging = destination + ".anima-" + Guid.NewGuid().ToString("N") + ".tmp";
        File.Copy(source, staging);
        if (File.Exists(destination) && !await Verify(destination, expected, ct)) File.Move(destination, destination + ".previous-" + Guid.NewGuid().ToString("N"));
        File.Move(staging, destination, true);
    }
    private static async Task ExtractZipMember(string archive, string destination, NvidiaRuntimeFile expected, CancellationToken ct)
    {
        if (await Verify(destination, expected, ct)) return;
        using var zip = ZipFile.OpenRead(archive);
        var members = zip.Entries.Where(entry => Path.GetFileName(entry.FullName).Equals(expected.FileName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (members.Length != 1 || members[0].Length != expected.Size) throw new InvalidDataException("NVIDIA 压缩包内容与清单不一致。");
        var staging = archive + "." + expected.FileName + ".tmp";
        members[0].ExtractToFile(staging, true);
        await Publish(staging, destination, expected, ct);
        File.Delete(staging);
    }
    private static async Task<string> PrepareSevenZip(string runtimeRoot, LauncherConfig config, Downloads downloads,
        IProgress<DownloadProgress>? progress, Action<string> log, CancellationToken ct)
    {
        var catalog = Catalog();
        var directory = Path.Combine(runtimeRoot, "tools", "7zip-26.05");
        var exe = Path.Combine(directory, "7z.exe");
        async Task<bool> Ready() => await HashMatches(exe, catalog.SevenZipExeSha256, ct) && await HashMatches(Path.Combine(directory, "7z.dll"), catalog.SevenZipDllSha256, ct);
        if (await Ready()) return exe;
        var bootstrap = await ArtifactCache.FetchAsync(runtimeRoot, catalog.SevenZipBootstrap, config, downloads, progress, ct);
        var archive = await ArtifactCache.FetchAsync(runtimeRoot, catalog.SevenZipArchive, config, downloads, progress, ct);
        Directory.CreateDirectory(directory);
        // 7zr extracts the official SFX archive; this does not install/register 7-Zip globally.
        await Commands.Run(bootstrap, ["x", archive, "-o" + directory, "-y"], runtimeRoot, log, ct, timeout: TimeSpan.FromMinutes(2));
        if (!await Ready()) throw new InvalidDataException("7-Zip 解压工具校验失败。");
        return exe;
    }
    private static async Task<bool> HashMatches(string path, string expected, CancellationToken ct)
    {
        if (!File.Exists(path)) return false;
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }
}
