using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Anima.Launcher.Core;

public static class Commands
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(30);
    public static ProcessStartInfo Info(string exe, IEnumerable<string> args, string cwd, IDictionary<string, string>? env = null)
    {
        var info = new ProcessStartInfo(exe) { WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        info.Environment["PYTHONUTF8"] = "1";
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        info.Environment["PIP_DISABLE_PIP_VERSION_CHECK"] = "1";
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (env is not null) foreach (var pair in env) info.Environment[pair.Key] = pair.Value;
        return info;
    }
    public static async Task<string> Run(string exe, IEnumerable<string> args, string cwd, Action<string>? log, CancellationToken ct, IDictionary<string, string>? env = null, TimeSpan? timeout = null)
    {
        ct.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = Info(exe, args, cwd, env) };
        var output = new StringBuilder();
        var errors = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (output) output.AppendLine(e.Data); log?.Invoke(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (errors) errors.AppendLine(e.Data); log?.Invoke(e.Data); } };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout ?? DefaultTimeout);
        process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
        try { await process.WaitForExitAsync(deadline.Token); process.WaitForExit(); }
        catch
        {
            try { process.Kill(true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            if (!ct.IsCancellationRequested && deadline.IsCancellationRequested) throw new TimeoutException($"{Path.GetFileName(exe)} 执行超时，已终止；请查看日志确认是否长时间无输出。");
            throw;
        }
        if (process.ExitCode != 0)
        {
            var tail = errors.ToString().TrimEnd();
            var detail = tail.Length > 1200 ? tail[^1200..] : tail;
            throw new InvalidOperationException($"{Path.GetFileName(exe)} 失败，退出码 {process.ExitCode}。{(detail.Length > 0 ? "末尾输出：" + detail : "请查看日志。")}");
        }
        return output.ToString();
    }
    public static string? Find(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try { var path = Path.Combine(dir.Trim('"'), name); if (File.Exists(path)) return path; } catch { }
        }
        return null;
    }
}

public sealed class PortConflictException(int port) : Exception($"端口 {port} 被其他程序或其他目录的实例占用，请修改端口后重试。") { public int Port { get; } = port; }
public sealed record OwnedProcess(int Pid, long StartTimeUtcTicks, string Executable, string Role, string? ShutdownToken = null);

public sealed class ServiceManager(string stateDir, Action<string> log)
{
    private readonly List<Process> owned = [];
    private readonly List<OwnedProcess> records = [];
    private static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
    public static async Task<JsonElement?> Json(string url, CancellationToken ct = default, TimeSpan? requestTimeout = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(requestTimeout ?? TimeSpan.FromSeconds(30));
        try { using var d = JsonDocument.Parse(await Http.GetStringAsync(url, deadline.Token)); return d.RootElement.Clone(); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("本地服务响应超时，请查看服务日志后重试。"); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }
    public static async Task<bool> PortOpen(int port, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try { using var client = new TcpClient(); await client.ConnectAsync("127.0.0.1", port, deadline.Token); return true; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return false; }
    }
    public static bool SameRoot(JsonElement stats, string root)
    {
        try
        {
            if (stats.ValueKind != JsonValueKind.Object || !stats.TryGetProperty("system", out var sys) || sys.ValueKind != JsonValueKind.Object || !sys.TryGetProperty("argv", out var argv) || argv.ValueKind != JsonValueKind.Array) return false;
            foreach (var arg in argv.EnumerateArray())
            {
                if (arg.ValueKind != JsonValueKind.String) continue;
                var text = arg.GetString();
                if (text is not null && Path.IsPathRooted(text) && Path.GetFileName(text).Equals("main.py", StringComparison.OrdinalIgnoreCase))
                    return Path.GetFullPath(text).Equals(Path.Combine(Path.GetFullPath(root), "main.py"), StringComparison.OrdinalIgnoreCase);
            }
        }
        catch { return false; }
        return false;
    }
    public static bool IsOurWebUi(JsonElement? health, LauncherConfig config)
    {
        try
        {
            if (health is null) return false;
            var value = health.Value;
            if (value.ValueKind != JsonValueKind.Object) return false;
            // Any missing or malformed field means the port belongs to something else.
            if (!value.TryGetProperty("app_dir", out var app) || app.ValueKind != JsonValueKind.String || app.GetString() is not { Length: > 0 } appDir) return false;
            if (!value.TryGetProperty("data_dir", out var data) || data.ValueKind != JsonValueKind.String || data.GetString() is not { Length: > 0 } dataDir) return false;
            if (!value.TryGetProperty("comfy_url", out var comfy) || comfy.ValueKind != JsonValueKind.String || comfy.GetString() is not { Length: > 0 } comfyUrl) return false;
            return Path.GetFullPath(appDir).Equals(Path.GetFullPath(config.AppRoot), StringComparison.OrdinalIgnoreCase)
                && Path.GetFullPath(dataDir).Equals(Path.GetFullPath(config.DataDir), StringComparison.OrdinalIgnoreCase)
                && comfyUrl.TrimEnd('/') == config.ComfyUrl;
        }
        catch { return false; }
    }
    /// <summary>Ensure ComfyUI is reachable: reuse a same-root external instance or start a managed one, then verify nodes and write resource bindings.</summary>
    public async Task StartComfy(LauncherConfig config, RuntimeManifest manifest, CancellationToken ct)
    {
        config.Validate();
        var before = owned.Count;
        try
        {
            if (await PortOpen(config.ComfyPort, ct))
            {
                var stats = await Json(config.ComfyUrl + "/system_stats", ct);
                if (stats is null || !SameRoot(stats.Value, config.ComfyRoot)) throw new PortConflictException(config.ComfyPort);
                log("复用已有 ComfyUI（外部进程不会被停止）。");
            }
            else
            {
                var assistant = PromptAssistantCompatibility.ResolveDirectory(config.ComfyRoot);
                if (assistant is not null)
                    await PromptAssistantCompatibility.CheckOrApply(config, stateDir, assistant, true, ct);
                var args = new List<string> { Path.Combine(config.ComfyRoot, "main.py"), "--listen", "127.0.0.1", "--port", config.ComfyPort.ToString(), "--disable-auto-launch" };
                if (config.Python.Contains("python_embed", StringComparison.OrdinalIgnoreCase)) args.Add("--windows-standalone-build");
                args.AddRange(config.ComfyArguments);
                var env = PrepareRuntimeEnvironment(config);
                var process = await StartProcess(config.Python, args, config.ComfyRoot, "ComfyUI", ct, env);
                await Ready(config.ComfyUrl + "/system_stats", process, ct);
            }
            var nodes = await Json(config.ComfyUrl + "/object_info", ct) ?? throw new IOException("ComfyUI 节点清单不可读取。");
            // The v2 graph keeps SeedVR2, RTX and face refinement in optional
            // groups.  Do not make their plugin classes (or the removed v1
            // pixel-upscale/detailer classes) a startup prerequisite: the
            // WebUI removes a disabled group before submission and reports an
            // actionable resource issue only when the stage is enabled.
            string[] core = ["UNETLoader", "CLIPLoader", "VAELoader", "KSampler", "KSamplerAdvanced", "VAEDecodeTiled", "EmptyLatentImage"];
            var missing = manifest.Nodes.SelectMany(n => n.RequiredClasses).Concat(core).Where(n => !nodes.TryGetProperty(n, out _)).ToArray();
            if (missing.Length > 0) throw new InvalidOperationException("缺少节点或导入失败：" + string.Join("、", missing));
            if (nodes.TryGetProperty("PromptExpand", out var expand) &&
                (!expand.TryGetProperty("input", out var inputs) ||
                 !inputs.TryGetProperty("optional", out var optional) ||
                 !optional.TryGetProperty("template_only", out _)))
                log("当前 ComfyUI 尚未加载纯模板兼容更新；重写功能需停止 ComfyUI 后从启动器重新启动，其他功能可继续使用。");
            var bindings = ResourceBindings(nodes);
            var resourceFile = Path.Combine(stateDir, "resource-paths.json");
            JsonFile.Write(resourceFile, bindings);
        }
        catch
        {
            RollbackStarted(before);
            Save(); throw;
        }
    }

    /// <summary>Ensure ComfyUI then start (or reuse) the Anima WebUI and verify it reports online.</summary>
    public async Task StartWebUI(LauncherConfig config, RuntimeManifest manifest, CancellationToken ct)
    {
        config.Validate();
        var nodeDirectories = Inventory.ResolveNodeDirectories(config.ComfyRoot, manifest);
        var before = owned.Count;
        try
        {
            await StartComfy(config, manifest, ct);
            var resourceFile = Path.Combine(stateDir, "resource-paths.json");
            if (await PortOpen(config.WebPort, ct))
            {
                var current = await Json(config.WebUrl + "/api/launcher-health", ct);
                if (!IsOurWebUi(current, config)) throw new PortConflictException(config.WebPort);
                log("复用已有 Anima WebUI。");
            }
            else
            {
                // Per-run token lets the launcher ask this instance to stop during a version
                // switch. Only payloads that implement the endpoint get the argument —
                // rolling back to an older snapshot must not crash its argparse.
                var supportsShutdown = SupportsShutdownEndpoint(config.AppRoot);
                var shutdownToken = supportsShutdown ? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant() : null;
                var argv = WebUiArguments(config, resourceFile, nodeDirectories);
                if (shutdownToken is not null) { argv.Add("--shutdown-token"); argv.Add(shutdownToken); }
                var process = await StartProcess(config.Python, argv, config.AppRoot, "WebUI", ct, PrepareRuntimeEnvironment(config), shutdownToken);
                await Ready(config.WebUrl + "/api/launcher-health", process, ct);
            }
            var status = await Json(config.WebUrl + "/api/status", ct);
            if (status is null || !status.Value.TryGetProperty("online", out var online) || !online.GetBoolean()) throw new IOException("工作台已启动，但尚未连接 ComfyUI，请查看日志。");
            log("ComfyUI 与工作台已就绪。");
        }
        catch
        {
            RollbackStarted(before);
            Save(); throw;
        }
    }

    public static List<string> WebUiArguments(LauncherConfig config, string resourceFile, IReadOnlyDictionary<string, string> nodeDirectories)
    {
        var args = new List<string> { Path.Combine(config.AppRoot, "run.py"), "--host", "127.0.0.1", "--port", config.WebPort.ToString(), "--comfy-url", config.ComfyUrl, "--data-dir", config.DataDir, "--resource-paths", resourceFile, "--no-browser" };
        if (nodeDirectories.TryGetValue(AnimaToolsDiscovery.NodeId, out var tools) && Directory.Exists(tools))
        {
            args.Add("--anima-tools-dir");
            args.Add(tools);
        }
        return args;
    }

    /// <summary>Child-process overrides only; never changes the launcher's or user's environment.</summary>
    public static Dictionary<string, string> BuildRuntimeEnvironment(LauncherConfig config, bool hasManagedMarker = false)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Imported environments retain their existing cache and LoRA Manager settings.
        if (!config.ManagedComfy && !hasManagedMarker) return env;
        env["LORA_MANAGER_PORTABLE"] = "1";
        // Legacy configurations did not choose a runtime volume. Preserve their defaults.
        if (string.IsNullOrWhiteSpace(config.RuntimeRoot)) return env;
        var runtime = Path.GetFullPath(config.RuntimeRoot.Trim().Trim('"'));
        var cache = Path.Combine(runtime, "cache");
        var huggingFace = Path.Combine(cache, "huggingface");
        var hub = Path.Combine(huggingFace, "hub");
        env["HF_HOME"] = huggingFace;
        env["HF_HUB_CACHE"] = hub;
        env["HUGGINGFACE_HUB_CACHE"] = hub;
        env["TRANSFORMERS_CACHE"] = hub;
        env["HF_ASSETS_CACHE"] = Path.Combine(huggingFace, "assets");
        env["HF_XET_CACHE"] = Path.Combine(huggingFace, "xet");
        env["TORCH_HOME"] = Path.Combine(cache, "torch");
        env["TORCH_EXTENSIONS_DIR"] = Path.Combine(cache, "torch", "extensions");
        env["TORCHINDUCTOR_CACHE_DIR"] = Path.Combine(cache, "torch", "inductor");
        env["TRITON_CACHE_DIR"] = Path.Combine(cache, "triton");
        env["CUDA_CACHE_PATH"] = Path.Combine(cache, "cuda");
        env["XDG_CACHE_HOME"] = cache;
        env["PIP_CACHE_DIR"] = Path.Combine(cache, "pip");
        env["UV_CACHE_DIR"] = Path.Combine(cache, "uv");
        env["TEMP"] = env["TMP"] = env["TMPDIR"] = Path.Combine(runtime, "temp");
        return env;
    }

    private static Dictionary<string, string> PrepareRuntimeEnvironment(LauncherConfig config)
    {
        var env = BuildRuntimeEnvironment(config, File.Exists(Path.Combine(config.ComfyRoot, ".anima-revision.json")));
        // Python tempfile may fall back to the system drive when its configured
        // directory does not exist. Create cache/temp roots before bootstrapping.
        foreach (var path in env.Where(pair => pair.Key != "LORA_MANAGER_PORTABLE").Select(pair => pair.Value).Distinct(StringComparer.OrdinalIgnoreCase))
            Directory.CreateDirectory(path);
        return env;
    }

    /// <summary>Start ComfyUI and the WebUI together; rolls back everything started during this call on failure.</summary>
    public async Task Start(LauncherConfig config, RuntimeManifest manifest, CancellationToken ct)
    {
        config.Validate();
        var before = owned.Count;
        try { await StartWebUI(config, manifest, ct); }
        catch
        {
            RollbackStarted(before);
            Save(); throw;
        }
    }
    public static Dictionary<string, string> ResourceBindings(JsonElement nodes)
    {
        var bindings = new Dictionary<string, string>();
        (string Node, string Input, string Name)[] resources = [
            ("UNETLoader", "unet_name", "anima-base-v1.0.safetensors"),
            ("UNETLoader", "unet_name", "krea2_turbo_int8_convrot.safetensors"),
            ("CLIPLoader", "clip_name", "qwen_3_06b_base.safetensors"),
            ("CLIPLoader", "clip_name", "qwen3vl_4b_fp8_scaled.safetensors"),
            ("VAELoader", "vae_name", "qwen_image_vae.safetensors"),
            ("UNETLoader", "unet_name", "seedvr2_3b_int8_convrot.safetensors"),
            ("VAELoader", "vae_name", "ema_vae_fp16.safetensors")
        ];
        if (nodes.ValueKind != JsonValueKind.Object) return bindings;
        foreach (var item in resources)
        {
            string[] values = [];
            if (nodes.TryGetProperty(item.Node, out var node) && node.ValueKind == JsonValueKind.Object && node.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object && input.TryGetProperty("required", out var fields) && fields.ValueKind == JsonValueKind.Object && fields.TryGetProperty(item.Input, out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                if (choices[0].ValueKind == JsonValueKind.Array) values = choices[0].EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray();
                else if (choices.GetArrayLength() > 1 && choices[1].ValueKind == JsonValueKind.Object && choices[1].TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array) values = options.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray();
            }
            var exact = values.FirstOrDefault(x => x.Replace('\\', '/') == item.Name);
            var matches = values.Where(x => Path.GetFileName(x.Replace('\\', '/')) == Path.GetFileName(item.Name)).ToArray();
            if (exact is not null) bindings[item.Name] = exact;
            else if (matches.Length == 1) bindings[item.Name] = matches[0].Replace('\\', '/');
            // Missing or ambiguous resources belong in the workbench's resource checks.
            // They must not prevent opening the workbench to configure another model.
        }
        return bindings;
    }
    private async Task<Process> StartProcess(string exe, IEnumerable<string> args, string cwd, string role, CancellationToken ct, IDictionary<string, string>? env = null, string? shutdownToken = null)
    {
        var logs = Path.Combine(stateDir, "logs"); Directory.CreateDirectory(logs);
        var logPath = Path.Combine(logs, role + ".log");
        if (File.Exists(logPath) && new FileInfo(logPath).Length > 10 * 1024 * 1024) File.Move(logPath, logPath + ".previous", true);
        // Give the long-lived child real log file handles, not pipes owned by the launcher.
        // Otherwise closing the tray would break Python stdout/stderr on the next write.
        const string script = "import subprocess,sys,json,os; f=open(sys.argv[1],'ab',buffering=0); p=subprocess.Popen(json.loads(sys.argv[2]),cwd=sys.argv[3],stdin=subprocess.DEVNULL,stdout=f,stderr=f,creationflags=0x08000000); print(p.pid)";
        var pid = await Commands.Run(exe, ["-c", script, logPath, JsonSerializer.Serialize(new[] { exe }.Concat(args)), cwd], cwd, null, ct, env);
        Process process;
        try
        {
            if (!int.TryParse(pid.Trim(), out var id)) throw new InvalidDataException("服务引导未返回进程号。");
            process = Process.GetProcessById(id);
            var _ = process.StartTime; // Throws if the child already exited; keep inside the guard.
        }
        catch
        {
            TryKill(pid.Trim());
            throw;
        }
        string module;
        try { module = process.MainModule?.FileName ?? exe; } catch { module = exe; }
        log($"{role} 已启动；日志：{logPath}");
        owned.Add(process); records.Add(new(process.Id, process.StartTime.ToUniversalTime().Ticks, module, role, shutdownToken)); Save();
        return process;
    }
    private static void TryKill(string pidText)
    {
        try { if (int.TryParse(pidText, out var id) && Process.GetProcessById(id) is { HasExited: false } p) p.Kill(true); } catch { }
    }
    private static async Task Ready(string url, Process process, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(6));
        try
        {
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new IOException($"服务启动失败，退出码 {process.ExitCode}；请查看日志。");
                // A slow first request may happen while custom nodes load. The overall
                // startup deadline, rather than a single HTTP request, controls readiness.
                try { if (await Json(url, timeout.Token) is not null) return; }
                catch (TimeoutException) { }
                await Task.Delay(750, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("服务在 6 分钟内未就绪，请查看服务日志后重试。"); }
    }
    public void RecoverOwned()
    {
        var path = Path.Combine(stateDir, "processes.json");
        if (!File.Exists(path)) return;
        OwnedProcess[] saved;
        try { saved = JsonFile.Read<OwnedProcess[]>(path); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // A damaged ownership file must never make the launcher unusable or
            // authorize stopping a process. Keep the evidence and recover no owners.
            var backup = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..8];
            try { File.Move(path, backup); log("进程恢复记录损坏，已备份：" + backup); }
            catch { log("进程恢复记录不可读取，已跳过所有权恢复：" + path); }
            return;
        }
        foreach (var record in saved)
        {
            try
            {
                if (record is null || record.Pid <= 0 || record.StartTimeUtcTicks <= 0 || record.Role is not ("ComfyUI" or "WebUI") || string.IsNullOrWhiteSpace(record.Executable) || records.Any(r => r.Pid == record.Pid)) continue;
                var process = Process.GetProcessById(record.Pid);
                if (process.StartTime.ToUniversalTime().Ticks == record.StartTimeUtcTicks && string.Equals(process.MainModule?.FileName, record.Executable, StringComparison.OrdinalIgnoreCase)) { owned.Add(process); records.Add(record); }
                else process.Dispose();
            }
            catch { }
        }
    }
    private void Save()
    {
        records.RemoveAll(r => !owned.Any(p => p.Id == r.Pid && !p.HasExited));
        JsonFile.Write(Path.Combine(stateDir, "processes.json"), records.ToArray());
    }
    private static bool Stop(Process p)
    {
        try { if (p.HasExited) return true; p.Kill(true); return p.WaitForExit(5000); }
        catch { try { return p.HasExited; } catch { return false; } }
    }
    private void RollbackStarted(int before)
    {
        foreach (var p in owned.Skip(before).ToArray())
        {
            if (Stop(p)) { owned.Remove(p); p.Dispose(); }
            else log($"进程 {p.Id} 未能在 5 秒内停止，已保留其所有权记录，可从服务页重试停止。");
        }
    }
    /// <summary>True when a service with this role ("ComfyUI" or "WebUI") is currently owned and running.</summary>
    public bool IsOwnedRunning(string role) => records.Where(r => r.Role == role).Any(r => owned.Any(p => p.Id == r.Pid && !p.HasExited));

    /// <summary>True when the payload's server.py implements /api/launcher-shutdown.</summary>
    public static bool SupportsShutdownEndpoint(string appRoot)
    {
        try { return File.ReadAllText(Path.Combine(appRoot, "anima_webui", "server.py")).Contains("launcher-shutdown"); }
        catch { return false; }
    }

    /// <summary>Reported app kind string of our workbench on /api/launcher-health.</summary>
    public const string WorkbenchAppName = "anima-random-studio";

    /// <summary>True when the health payload identifies our workbench app.</summary>
    public static bool IsWorkbenchHealth(JsonElement? health)
    {
        try { return health is { } value && value.ValueKind == JsonValueKind.Object && value.TryGetProperty("app", out var app) && app.GetString() == WorkbenchAppName; }
        catch { return false; }
    }

    /// <summary>
    /// Free the WebUI port when it is held by an instance of our own workbench.
    /// Bind the endpoint's OS port owner to its process identity and full workbench
    /// context. Other owned WebUI processes may belong to another environment.
    /// Unowned old snapshots also require a matching run.py command line.
    /// Returns false when the port is held by something we must not stop.
    /// </summary>
    public async Task<bool> TryStopWebUiAsync(LauncherConfig config, string versionsRoot, CancellationToken ct = default)
    {
        if (!await PortOpen(config.WebPort, ct)) return true;
        var health = await Json(config.WebUrl + "/api/launcher-health", ct);
        if (!IsWorkbenchHealth(health) || !IsOurWebUi(health, config)) return false;
        var pid = await FindPortOwner(config.WebPort, ct);
        if (pid <= 0) return !await PortOpen(config.WebPort, ct);
        try
        {
            using var process = Process.GetProcessById(pid);
            var identity = new OwnedProcess(pid, process.StartTime.ToUniversalTime().Ticks, process.MainModule!.FileName!, "WebUI");
            var record = records.SingleOrDefault(r => r.Role == "WebUI" && SameProcessIdentity(r, identity));
            if (record is null)
            {
                if (!PathUnder(versionsRoot, config.AppRoot)) { log("端口上的工作台不在快照目录内，不接管。"); return false; }
                if (!await ProcessCommandLineMatchesAsync(pid, config.AppRoot, ct)) { log($"PID {pid} 的命令行不属于旧快照，不接管。"); return false; }
            }
            // Recheck after command-line/port inspection, before either sending a
            // token or terminating a process. A reused PID never inherits ownership.
            var latest = await Json(config.WebUrl + "/api/launcher-health", ct);
            if (!IsWorkbenchHealth(latest) || !IsOurWebUi(latest, config)
                || await FindPortOwner(config.WebPort, ct) != pid || !ProcessStillMatches(identity)) return false;
            ct.ThrowIfCancellationRequested();
            if (!string.IsNullOrEmpty(record?.ShutdownToken) && await ShutdownRemote(config.WebUrl, record.ShutdownToken, ct))
            {
                log($"已请求工作台进程 {pid} 的停机端点。");
                var closed = await WaitPortClosed(config.WebPort, ct);
                if (process.HasExited) ForgetOwned(pid);
                return closed;
            }
            if (!ProcessStillMatches(identity) || await FindPortOwner(config.WebPort, ct) != pid) return false;
            ct.ThrowIfCancellationRequested();
            process.Kill(true);
            await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
            ForgetOwned(pid);
            log($"已结束目标工作台进程 {pid}；其他环境的工作台保持运行。");
            return await WaitPortClosed(config.WebPort, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { return await WaitPortClosed(config.WebPort, ct); }
    }

    public static bool SameProcessIdentity(OwnedProcess recorded, OwnedProcess current) =>
        recorded.Pid > 0 && recorded.Pid == current.Pid && recorded.StartTimeUtcTicks > 0
        && recorded.StartTimeUtcTicks == current.StartTimeUtcTicks
        && !string.IsNullOrWhiteSpace(recorded.Executable)
        && string.Equals(recorded.Executable, current.Executable, StringComparison.OrdinalIgnoreCase);

    private static bool ProcessStillMatches(OwnedProcess identity)
    {
        try
        {
            using var current = Process.GetProcessById(identity.Pid);
            return !current.HasExited && SameProcessIdentity(identity,
                new(current.Id, current.StartTime.ToUniversalTime().Ticks, current.MainModule!.FileName!, "WebUI"));
        }
        catch { return false; }
    }

    private void ForgetOwned(int pid)
    {
        foreach (var process in owned.Where(p => p.Id == pid).ToArray()) { owned.Remove(process); process.Dispose(); }
        records.RemoveAll(r => r.Pid == pid);
        Save();
    }

    private static bool PathUnder(string root, string path)
    {
        try
        {
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static async Task<bool> ShutdownRemote(string webUrl, string token, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var body = new StringContent(JsonSerializer.Serialize(new { token }), Encoding.UTF8, "application/json");
            using var response = await Http.PostAsync(webUrl + "/api/launcher-shutdown", body, deadline.Token);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    private static async Task<bool> WaitPortClosed(int port, CancellationToken ct)
    {
        for (var i = 0; i < 40; i++)
        {
            if (!await PortOpen(port, ct)) return true;
            await Task.Delay(250, ct);
        }
        return false;
    }

    /// <summary>True when PID's command line launches run.py from the given app dir (any interpreter).</summary>
    public static async Task<bool> ProcessCommandLineMatchesAsync(int pid, string appDir, CancellationToken ct = default)
    {
        var commandLine = await ProcessCommandLine(pid, ct);
        return RuntimePolicies.CommandLineMatchesSnapshot(commandLine, appDir);
    }

    /// <summary>Process command line via WMIC (absent on some newer Windows) with a PowerShell fallback.</summary>
    private static async Task<string?> ProcessCommandLine(int pid, CancellationToken ct)
    {
        foreach (var (exe, args, prefix) in new (string, string[], string)[]
        {
            ("wmic.exe", ["process", "where", $"processid={pid}", "get", "commandline", "/format:list"], "CommandLine="),
            ("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", $"(Get-CimInstance Win32_Process -Filter 'ProcessId={pid}').CommandLine"], ""),
        })
        {
            var path = Commands.Find(exe);
            if (path is null) continue;
            try
            {
                var output = await Commands.Run(path, args, Environment.CurrentDirectory, null, ct, timeout: TimeSpan.FromSeconds(10));
                var text = output.Trim();
                if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) text = text[prefix.Length..].Trim();
                if (text.Length > 0) return text;
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }
        return null;
    }

    /// <summary>Owning PID of a 127.0.0.1 LISTEN socket on <paramref name="port"/>; 0 when unknown.</summary>
    private static async Task<int> FindPortOwner(int port, CancellationToken ct)
    {
        try
        {
            // netstat is the simplest reliable owner lookup available everywhere;
            // lines look like: "  TCP    127.0.0.1:8190    0.0.0.0:0    LISTENING    16728".
            var output = await Commands.Run("netstat.exe", ["-ano"], Environment.CurrentDirectory, null, ct, timeout: TimeSpan.FromSeconds(5));
            var owners = new HashSet<int>();
            foreach (var line in output.Split('\n'))
            {
                var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 5 && parts[0] == "TCP" && parts[^2] == "LISTENING" && (parts[1] == "127.0.0.1:" + port || parts[1] == "0.0.0.0:" + port || parts[1] == "[::]:" + port) && int.TryParse(parts[^1], out var pid)) owners.Add(pid);
            }
            return owners.Count == 1 ? owners.Single() : 0;
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return 0;
    }

    /// <summary>Keep the UI responsive while waiting for owned processes to exit.</summary>
    public async Task StopOwnedAsync(string? role = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var pids = records.Where(r => role is null || r.Role == role).Select(r => r.Pid).ToHashSet();
        var failed = new List<int>();
        try
        {
            foreach (var p in owned.Where(p => pids.Contains(p.Id)).ToArray())
            {
                ct.ThrowIfCancellationRequested();
                var id = p.Id;
                try
                {
                    if (!p.HasExited) p.Kill(true);
                    await p.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
                    owned.Remove(p);
                    p.Dispose();
                }
                catch (OperationCanceledException) { throw; }
                catch { failed.Add(id); }
            }
        }
        finally { Save(); }
        if (failed.Count > 0) throw new IOException($"服务未能在 5 秒内停止，进程 {string.Join(", ", failed)} 的所有权已保留，请重试或查看日志。");
        log("已停止启动器拥有的" + (role ?? "全部服务") + "；外部服务保持运行。");
    }

    /// <summary>Stop only the managed processes of one role; external services are never touched.</summary>
    public void StopOwned(string role)
    {
        var pids = records.Where(r => r.Role == role).Select(r => r.Pid).ToHashSet();
        var failed = new List<int>();
        foreach (var p in owned.Where(p => pids.Contains(p.Id)).ToArray())
        {
            if (Stop(p)) { owned.Remove(p); p.Dispose(); }
            else failed.Add(p.Id);
        }
        Save();
        if (failed.Count > 0) throw new IOException($"{role} 未能在 5 秒内停止，进程 {string.Join(", ", failed)} 的所有权已保留，请重试或查看日志。");
        log($"已停止启动器拥有的 {role}；外部服务保持运行。");
    }
    public void StopOwned()
    {
        var failed = new List<int>();
        foreach (var p in owned.ToArray())
        {
            if (Stop(p)) { owned.Remove(p); p.Dispose(); }
            else failed.Add(p.Id);
        }
        Save();
        if (failed.Count > 0) throw new IOException($"部分服务未能在 5 秒内停止，进程 {string.Join(", ", failed)} 的所有权已保留，请重试或查看日志。");
        log("已停止启动器拥有的服务；外部服务保持运行。");
    }
}
