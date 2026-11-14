using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Anima.Launcher.Core;

/// <summary>A core-only update check. Its data directories and process lifetime never belong to the normal service.</summary>
public static class ComfyUpdateProbe
{
    public static readonly string[] CoreClasses = ["UNETLoader", "CLIPLoader", "VAELoader", "KSampler", "KSamplerAdvanced", "VAEDecodeTiled", "EmptyLatentImage"];
    private static readonly string[] RequiredOptions = ["--listen", "--port", "--disable-auto-launch", "--base-directory", "--user-directory", "--input-directory", "--output-directory", "--temp-directory", "--front-end-root", "--disable-all-custom-nodes"];
    private static readonly HashSet<string> ComputeFlags = new(ComfyLaunchArguments.VramModes.Concat(ComfyLaunchArguments.Precisions)
        .Concat(ComfyLaunchArguments.Attentions).Where(value => value.Length > 0).Concat([
            "--disable-smart-memory", "--cuda-malloc", "--disable-cuda-malloc", "--disable-xformers", "--cpu-vae",
            "--fp32-unet", "--fp64-unet", "--bf16-unet", "--fp16-unet", "--fp8_e4m3fn-unet", "--fp8_e5m2-unet", "--fp8_e8m0fnu-unet",
            "--fp16-vae", "--fp32-vae", "--bf16-vae", "--fp8_e4m3fn-text-enc", "--fp8_e5m2-text-enc", "--fp16-text-enc", "--fp32-text-enc", "--bf16-text-enc",
            "--force-channels-last", "--force-upcast-attention", "--dont-upcast-attention", "--disable-dynamic-vram", "--enable-dynamic-vram",
            "--force-non-blocking", "--deterministic", "--mmap-torch-files", "--disable-mmap"]), StringComparer.Ordinal);
    private static readonly HashSet<string> ComputeValues = new(["--reserve-vram", "--cuda-device", "--default-device", "--vram-headroom"], StringComparer.Ordinal);

    /// <summary>Static preflight for both the installed and fetched target parser. Never imports target Python.</summary>
    public static void ValidateSource(string cliArgsText, IEnumerable<string>? configuredArguments = null)
    {
        var options = Options(cliArgsText);
        var missing = RequiredOptions.Where(option => !options.Contains(option)).ToArray();
        if (missing.Length > 0) throw new InvalidDataException("该 ComfyUI 版本缺少隔离更新检查所需的选项，未修改环境：" + string.Join("、", missing));
        if (options.Contains("--enable-assets") && !options.Contains("--disable-assets"))
            throw new InvalidDataException("该 ComfyUI 版本无法明确禁用资源数据库扫描，不能执行隔离更新检查。");
        _ = ComputeArguments(configuredArguments ?? [], options);
    }

    public static void ValidateForRoot(LauncherConfig config) => ValidateSource(ReadSource(config), config.ComfyArguments);

    private static string ReadSource(LauncherConfig config)
    {
        var file = Path.Combine(config.ComfyRoot, "comfy", "cli_args.py");
        if (!File.Exists(file)) throw new InvalidDataException("无法读取 ComfyUI 启动选项，不能确认隔离更新检查能力：" + file);
        return File.ReadAllText(file);
    }

    private static HashSet<string> Options(string source) => Regex.Matches(source,
        """(?m)^[\t ]*[\w.]+\.add_argument\s*\(\s*["'](--[\w-]+)["']""")
        .Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    internal static List<string> BuildArguments(LauncherConfig config, string directory, int port)
    {
        var source = ReadSource(config);
        ValidateSource(source, config.ComfyArguments);
        var options = Options(source);
        var result = new List<string> { Path.Combine(Path.GetFullPath(config.ComfyRoot), "main.py"), "--listen", "127.0.0.1", "--port", port.ToString(CultureInfo.InvariantCulture),
            "--disable-auto-launch", "--disable-all-custom-nodes", "--base-directory", directory,
            "--user-directory", Path.Combine(directory, "user"), "--input-directory", Path.Combine(directory, "input"),
            "--output-directory", Path.Combine(directory, "output"), "--temp-directory", Path.Combine(directory, "temp"),
            "--front-end-root", Path.Combine(directory, "frontend") };
        if (options.Contains("--models-directory")) result.AddRange(["--models-directory", Path.Combine(directory, "models")]);
        if (options.Contains("--previews-directory")) result.AddRange(["--previews-directory", Path.Combine(directory, "previews")]);
        if (options.Contains("--database-url")) result.AddRange(["--database-url", "sqlite:///" + Path.Combine(directory, "user", "probe.db").Replace('\\', '/')]);
        if (options.Contains("--disable-assets")) result.Add("--disable-assets");
        if (options.Contains("--disable-api-nodes")) result.Add("--disable-api-nodes");
        if (options.Contains("--disable-partner-nodes")) result.Add("--disable-partner-nodes");
        if (options.Contains("--offline")) result.Add("--offline");
        result.AddRange(ComputeArguments(config.ComfyArguments, options));
        return result;
    }

    private static List<string> ComputeArguments(IEnumerable<string> arguments, HashSet<string> supported)
    {
        var result = new List<string>();
        var tokens = arguments.ToArray();
        for (var index = 0; index < tokens.Length; index++)
        {
            var pieces = tokens[index].Split('=', 2);
            var name = pieces[0];
            if (!ComputeFlags.Contains(name) && !ComputeValues.Contains(name) && name != "--directml") continue;
            if (!supported.Contains(name)) throw new InvalidDataException("目标 ComfyUI 版本不支持当前计算选项：" + name + "。请先调整启动选项；未修改环境。");
            result.Add(name);
            if (ComputeFlags.Contains(name))
            {
                if (pieces.Length > 1) throw new InvalidDataException("计算选项不接受值：" + name);
                continue;
            }
            var value = pieces.Length > 1 ? pieces[1]
                : index + 1 < tokens.Length && !tokens[index + 1].StartsWith("--", StringComparison.Ordinal) ? tokens[++index] : null;
            if (value is null && name == "--directml") continue;
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal) || value == "-h"
                || value.Any(character => character is '\0' or '\r' or '\n'))
                throw new InvalidDataException("计算选项缺少有效值：" + name);
            result.Add(value);
        }
        ComfyLaunchArguments.Validate(result);
        return result;
    }

    internal static Dictionary<string, string> BuildEnvironment(string directory)
    {
        var environment = ServiceManager.BuildRuntimeEnvironment(new LauncherConfig { ManagedComfy = true, RuntimeRoot = directory });
        environment["PYTHONDONTWRITEBYTECODE"] = "1";
        environment["PYTHONNOUSERSITE"] = "1";
        environment["PYTHONPATH"] = "";
        environment["PYTHONUSERBASE"] = Path.Combine(directory, "python-user");
        environment["HF_HUB_OFFLINE"] = "1";
        environment["TRANSFORMERS_OFFLINE"] = "1";
        return environment;
    }

    internal static bool StatsMatch(JsonElement stats, IReadOnlyList<string> arguments)
    {
        try
        {
            return stats.GetProperty("system").GetProperty("argv").EnumerateArray()
                .Select(value => value.GetString()).SequenceEqual(arguments, StringComparer.Ordinal);
        }
        catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException) { return false; }
    }

    internal static bool CommandLineMatches(string? commandLine, IReadOnlyList<string> pythonArguments)
    {
        try { return commandLine is not null && ComfyLaunchArguments.Parse(commandLine).Skip(1).SequenceEqual(pythonArguments, StringComparer.Ordinal); }
        catch (InvalidDataException) { return false; }
    }
}

public sealed partial class ServiceManager
{
    /// <summary>Checks only ComfyUI core in a disposable data root; no startup extensions, plugin imports or resource bindings.</summary>
    public async Task ProbeComfyUpdateAsync(LauncherConfig config, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("隔离更新检查需要 Windows 进程身份验证。");
        ComfyUpdateProbe.ValidateForRoot(config);
        if (!File.Exists(config.Python)) throw new FileNotFoundException("找不到更新检查使用的 Python。", config.Python);
        var parent = Path.GetFullPath(Path.Combine(stateDir, "comfy-probes"));
        var directory = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Task<string>? run = null;
        StreamWriter? writer = null;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            foreach (var leaf in new[] { "user", "input", "output", "temp", "models", "previews", "frontend", "python-user" }) Directory.CreateDirectory(Path.Combine(directory, leaf));
            var environment = ComfyUpdateProbe.BuildEnvironment(directory);
            foreach (var path in environment.Values.Where(Path.IsPathFullyQualified).Distinct(StringComparer.OrdinalIgnoreCase)) Directory.CreateDirectory(path);
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            var arguments = ComfyUpdateProbe.BuildArguments(config, directory, port);
            var marker = "ANIMA_UPDATE_PROBE_" + Guid.NewGuid().ToString("N") + ":";
            // Commands.Run owns a kill-on-close Windows job from process creation. The
            // marker identifies the actual interpreter even behind a venv redirector.
            const string bootstrap = "import os,sys,json,runpy; a=json.loads(sys.argv[1]); print(sys.argv[2]+str(os.getpid()),flush=True); sys.argv=a; sys.path.insert(0,os.path.dirname(a[0])); runpy.run_path(a[0],run_name='__main__')";
            string[] pythonArguments = ["-B", "-s", "-c", bootstrap, JsonSerializer.Serialize(arguments), marker];
            var started = new TaskCompletionSource<OwnedProcess>(TaskCreationOptions.RunContinuationsAsynchronously);
            var logPath = Path.Combine(stateDir, "logs", "ComfyUI-update-probe.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            writer = new StreamWriter(logPath, false) { AutoFlush = true };
            reservation.Stop();
            run = Commands.Run(config.Python, pythonArguments, directory, line =>
            {
                if (line.StartsWith(marker, StringComparison.Ordinal))
                {
                    if (int.TryParse(line[marker.Length..], out var pid) && ReadProcessIdentity(pid, "ComfyUI update probe") is { } identity)
                        started.TrySetResult(identity);
                    else started.TrySetException(new IOException("无法读取本次 ComfyUI 更新检查的 Python 进程身份。"));
                }
                else { lock (writer) writer.WriteLine(line); log(line); }
            }, lifetime.Token, environment, TimeSpan.FromMinutes(6));
            log("正在隔离检查 ComfyUI 本体；自定义节点与 Manager 未启用。日志：" + logPath);
            var first = await Task.WhenAny(started.Task, run).WaitAsync(ct);
            if (first == run) { await run; throw new IOException("ComfyUI 更新检查进程未启动。日志：" + logPath); }
            var identityAtStart = await started.Task;
            using var process = Process.GetProcessById(identityAtStart.Pid);
            await Ready($"http://127.0.0.1:{port}/system_stats", process, ct, "ComfyUI 更新检查", logPath);
            var probeConfig = config.Clone(); probeConfig.ComfyPort = port;
            var before = await ReadComfyProcessIdentity(probeConfig, await FindPortOwner(port, ct), ct);
            if (before.Process is null || !SameProcessIdentity(identityAtStart, before.Process)) throw new IOException("更新检查端口未由本次检查进程持有。");
            var stats = await Json(probeConfig.ComfyUrl + "/system_stats", ct);
            if (stats is null || !ComfyUpdateProbe.StatsMatch(stats.Value, arguments)) throw new IOException("ComfyUI 更新检查的隔离启动参数不匹配。");
            if (!ComfyUpdateProbe.CommandLineMatches(await ReadProcessCommandLineAsync(identityAtStart.Pid, ct), pythonArguments))
                throw new IOException("无法确认 ComfyUI 更新检查进程的实际启动参数。");
            var nodes = await Json(probeConfig.ComfyUrl + "/object_info", ct);
            if (nodes is not { ValueKind: JsonValueKind.Object } objectInfo) throw new IOException("ComfyUI 更新检查无法读取本体节点清单。");
            var missing = ComfyUpdateProbe.CoreClasses.Where(name => !objectInfo.TryGetProperty(name, out _)).ToArray();
            if (missing.Length > 0) throw new IOException("ComfyUI 本体节点缺失：" + string.Join("、", missing));
            var after = await ReadComfyProcessIdentity(probeConfig, await FindPortOwner(port, ct), ct);
            if (!ComfyProcessIdentityMatches(probeConfig, before.Process, after.Process, before.Launcher, after.Launcher))
                throw new IOException("ComfyUI 更新检查的 Python 进程身份无法确认。");
            log("ComfyUI 本体隔离检查通过。");
        }
        finally
        {
            lifetime.Cancel();
            if (run is not null)
            {
                try { await run; }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                catch (CommandFailureException) { }
                catch (TimeoutException) { }
            }
            writer?.Dispose();
            // Only our freshly generated child is eligible for cleanup. Never use an
            // environment path, normal service role, or shared process ownership file.
            if (Path.GetDirectoryName(Path.GetFullPath(directory)) == parent && Directory.Exists(directory))
            {
                try { Directory.Delete(directory, true); }
                catch (IOException error) { log("更新检查临时目录暂未清理：" + error.Message); }
                catch (UnauthorizedAccessException error) { log("更新检查临时目录暂未清理：" + error.Message); }
            }
        }
    }
}
