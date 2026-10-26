using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Anima.Launcher.Core;

internal sealed class Aria2Transport(SafeHttpSourceClient source, DownloadPolicy policy, DownloadEngineOptions options, string executable) : IDownloadTransport
{
    // This transport instance is shared across the request's source attempts.
    private int engineRestarts;
    public Task FetchAsync(DownloadRequest request, string sourceUrl, IProgress<DownloadProgress>? progress,
        CancellationToken ct, DownloadTransportOptions transferOptions)
    {
        if (transferOptions.RestartWithoutRange) throw new ArgumentException("A native range engine cannot perform a no-range restart.", nameof(transferOptions));
        return FetchAsync(request, sourceUrl, progress, ct, transferOptions.DetectSlow, transferOptions.Retries, transferOptions.Space);
    }
    public async Task FetchAsync(DownloadRequest request, string url, IProgress<DownloadProgress>? progress, CancellationToken ct, bool detectSlow, int retries,
        DownloadDiskSpaceReservation? space = null)
    {
        var attempt = 0;
        while (true)
        {
            try { await Attempt(request, url, progress, ct, detectSlow, space); return; }
            catch (DownloadEngineCrashedException error) when (!ct.IsCancellationRequested)
            {
                if (engineRestarts++ >= 1)
                    throw new DownloadCheckpointException("并行引擎连续退出，已保留检查点；自动恢复已尝试一次，请检查引擎或选择兼容重新下载。", error);
                progress?.Report(new(request.Name, DownloadCheckpoint.RetainedBytes(request.Destination, request.Size, request.Sha256), request.Size,
                    "并行引擎意外退出，尝试恢复一次")
                { Phase = "RetryWaiting", Engine = "aria2", TaskId = request.TaskId, Attempt = 1, FailureCode = "engine_crash" });
                if (policy.RetryDelay > TimeSpan.Zero) await Task.Delay(policy.RetryDelay, ct);
            }
            catch (Exception error) when (attempt < retries && !ct.IsCancellationRequested && HttpClientTransport.IsRetryable(error))
            {
                var retryAt = DownloadSourceHealth.RetryAt(error, policy, attempt);
                progress?.Report(new(request.Name, DownloadCheckpoint.RetainedBytes(request.Destination, request.Size, request.Sha256), request.Size,
                    error is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } ? "来源限流，等待后自动续传" : "连接中断，自动续传")
                { Phase = "RetryWaiting", RetryAt = retryAt, Engine = "aria2", TaskId = request.TaskId, Attempt = attempt + 1 });
                var delay = retryAt - DateTimeOffset.UtcNow;
                if (delay > policy.MaximumAutomaticRetryDelay) throw;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
                attempt++;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw new TimeoutException("并行下载来源响应超时，断点已保留。"); }
        }
    }

    private async Task Attempt(DownloadRequest request, string url, IProgress<DownloadProgress>? progress, CancellationToken ct, bool detectSlow,
        DownloadDiskSpaceReservation? space)
    {
        var data = DownloadCheckpoint.AriaData(request.Destination);
        if (await Downloads.Verify(data, request.Size, request.Sha256, ct)) { Publish(request, progress); return; }
        Aria2Checkpoint checkpoint;
        try { checkpoint = await DownloadCheckpoint.PrepareAsync(request, url, ct); }
        catch (Exception error) when (error is InvalidDataException or JsonException)
        { throw new DownloadCheckpointException("本地并行检查点无效，已保留文件供检查；切换来源不能修复此状态。", error); }
        if (space is not null) await space.SetPrimaryPathAsync(data, ct);
        else Safety.FreeSpace(request.Destination, Math.Max(0, request.Size - DownloadDiskSpace.AllocatedBytes(data)));
        if (!await source.SupportsRangesAsync(url, request.Size, ct)) throw new RangeNotSupportedException("此来源不支持分段下载。");
        await using var bridge = await LoopbackRangeBridge.StartAsync(source, request, url, ct);
        await using var engine = await Aria2Process.StartAsync(executable, options, ct);
        var connections = request.Size < 64L * 1024 * 1024 ? 1 : Math.Clamp(options.ConnectionsPerFile, 1, 4);
        var downloadOptions = new Dictionary<string, string>
        {
            ["dir"] = Path.GetDirectoryName(data)!, ["out"] = Path.GetFileName(data),
            ["continue"] = "true", ["always-resume"] = "true", ["auto-file-renaming"] = "false", ["allow-overwrite"] = "false",
            ["split"] = connections.ToString(), ["max-connection-per-server"] = connections.ToString(), ["min-split-size"] = "32M",
            ["piece-length"] = "1M", ["file-allocation"] = "none", ["check-integrity"] = "false",
            ["max-tries"] = "1", ["retry-wait"] = "0", ["http-accept-gzip"] = "false",
            ["header"] = LoopbackRangeBridge.CapabilityHeader + ": " + bridge.Token,
            ["connect-timeout"] = "10", ["timeout"] = Math.Max(30, (int)policy.IdleTimeout.TotalSeconds + 10).ToString(),
            ["all-proxy"] = "", ["http-proxy"] = "", ["https-proxy"] = "", ["no-proxy"] = "*"
        };
        DownloadCheckpoint.MarkStarted(request, checkpoint);
        string? gid = null;
        var slowClock = Stopwatch.StartNew();
        var diskClock = Stopwatch.StartNew();
        long speedBaseline = 0;
        try
        {
            var added = await engine.CallAsync("aria2.addUri", new object[] { new[] { bridge.Address.AbsoluteUri }, downloadOptions }, ct);
            gid = added.GetString() ?? throw new IOException("并行下载引擎没有返回任务标识。");
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (space is not null && diskClock.Elapsed >= TimeSpan.FromSeconds(1))
                { await space.CheckAsync(ct); diskClock.Restart(); }
                if (bridge.Failure.IsCompletedSuccessfully) throw await bridge.Failure;
                if (engine.HasExited) throw new DownloadEngineCrashedException("并行下载引擎意外退出，已保留检查点。");
                var status = await engine.CallAsync("aria2.tellStatus", new object[] { gid,
                    new[] { "status", "completedLength", "totalLength", "downloadSpeed", "connections", "errorCode" } }, ct);
                var state = status.GetProperty("status").GetString();
                if (bridge.Failure.IsCompletedSuccessfully) throw await bridge.Failure;
                var received = Number(status, "completedLength");
                var reportedSize = Number(status, "totalLength");
                if (reportedSize > 0 && reportedSize != request.Size) throw new InvalidDataException("并行引擎报告的文件大小与锁定清单不符。");
                progress?.Report(new(request.Name, Math.Clamp(received, 0, request.Size), request.Size,
                    state == "complete" ? "SHA-256 校验中" : "下载中")
                { TaskId = request.TaskId, Phase = state == "complete" ? "Verifying" : "Downloading", Engine = "aria2",
                    Speed = Number(status, "downloadSpeed"), Connections = (int)Number(status, "connections") });
                if (state == "complete") break;
                if (state is "error" or "removed")
                {
                    // Upstream policy failures are communicated directly by the bridge, not inferred from console strings.
                    if (bridge.Failure.IsCompletedSuccessfully) throw await bridge.Failure;
                    var code = status.TryGetProperty("errorCode", out var value) ? value.GetString() : "unknown";
                    throw new HttpRequestException("并行下载引擎传输失败（代码 " + code + "），已保留断点。");
                }
                if (detectSlow && bridge.ActiveStreams > 0 && slowClock.Elapsed >= policy.SlowWindow)
                {
                    var bytes = bridge.BytesServed;
                    if ((bytes - speedBaseline) / slowClock.Elapsed.TotalSeconds < policy.MinimumBytesPerSecond)
                        throw new SlowDownloadException("下载来源持续低速，已保留并行断点。");
                    speedBaseline = bytes; slowClock.Restart();
                }
                else if (bridge.ActiveStreams == 0) { speedBaseline = bridge.BytesServed; slowClock.Restart(); }
                await Task.Delay(options.PollInterval, ct);
            }
        }
        catch (Exception error) when (!ct.IsCancellationRequested && engine.HasExited && error is not DownloadEngineCrashedException)
        { throw new DownloadEngineCrashedException("并行下载引擎在 RPC 操作期间退出，已保留检查点。", error); }
        finally
        {
            if (gid is not null)
            {
                using var stop = new CancellationTokenSource(options.StopTimeout);
                try { await engine.CallAsync("aria2.pause", new object[] { gid }, stop.Token); } catch { }
            }
            // Graceful shutdown saves the control file; disposal has an owned-process kill fallback.
            await engine.StopAsync();
        }
        ct.ThrowIfCancellationRequested();
        if (!await Downloads.Verify(data, request.Size, request.Sha256, ct))
        {
            DownloadCheckpoint.Cleanup(request.Destination, includeLegacy: false);
            throw new InvalidDataException("并行下载完整 SHA-256 校验失败，未发布文件。");
        }
        ct.ThrowIfCancellationRequested();
        Publish(request, progress);
    }

    private static long Number(JsonElement json, string property) => json.TryGetProperty(property, out var value)
        && long.TryParse(value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText(), out var number) ? number : 0;
    private static void Report(DownloadRequest request, IProgress<DownloadProgress>? progress, long received, string stage, string phase) =>
        progress?.Report(new(request.Name, received, request.Size, stage) { TaskId = request.TaskId, Engine = "aria2", Phase = phase });
    private static void Publish(DownloadRequest request, IProgress<DownloadProgress>? progress)
    {
        Report(request, progress, request.Size, "发布已校验文件", "Publishing");
        File.Move(DownloadCheckpoint.AriaData(request.Destination), request.Destination, false);
        DownloadCheckpoint.Cleanup(request.Destination);
        Report(request, progress, request.Size, "完成", "Completed");
    }
}

internal sealed class Aria2Process : IAsyncDisposable
{
    private readonly Process process;
    private readonly DownloadProcessJob job;
    private readonly HttpClient rpc;
    private readonly string secret;
    private readonly DownloadEngineOptions options;
    private int id;
    private bool stopped;
    public bool HasExited => process.HasExited;

    private Aria2Process(Process process, DownloadProcessJob job, int port, string secret, DownloadEngineOptions options)
    {
        this.process = process; this.job = job; this.secret = secret; this.options = options;
        rpc = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
        { BaseAddress = new Uri("http://127.0.0.1:" + port + "/jsonrpc"), Timeout = Timeout.InfiniteTimeSpan };
    }

    public static async Task<Aria2Process> StartAsync(string executable, DownloadEngineOptions options, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable)) throw new DownloadEngineUnavailableException("并行下载引擎文件不可用。");
        for (var attempt = 0; ; attempt++)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            var secret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
            foreach (var value in new[] { "--no-conf=true", "--no-netrc=true", "--enable-rpc=true", "--rpc-listen-all=false",
                "--rpc-allow-origin-all=false", "--rpc-listen-port=" + port, "--rpc-secret=" + secret,
                "--max-concurrent-downloads=1", "--auto-save-interval=1", "--enable-dht=false", "--enable-dht6=false",
                "--enable-peer-exchange=false", "--bt-enable-lpd=false", "--follow-torrent=false", "--follow-metalink=false",
                "--stop-with-process=" + Environment.ProcessId, "--console-log-level=error", "--summary-interval=0",
                "--all-proxy=", "--http-proxy=", "--https-proxy=", "--no-proxy=*" }) start.ArgumentList.Add(value);
            foreach (var key in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" }) start.Environment[key] = "";
            start.Environment["NO_PROXY"] = "*"; start.Environment["no_proxy"] = "*";
            var process = new Process { StartInfo = start };
            var job = new DownloadProcessJob();
            Aria2Process? owned = null;
            try
            {
                if (!process.Start()) throw new IOException("无法启动下载引擎。");
                job.Assign(process);
                // Drain without storing URLs, headers, capability tokens or unbounded child output.
                process.OutputDataReceived += (_, _) => { }; process.ErrorDataReceived += (_, _) => { };
                process.BeginOutputReadLine(); process.BeginErrorReadLine();
                owned = new(process, job, port, secret, options);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(options.StartTimeout);
                while (true)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    if (process.HasExited) throw new IOException("下载引擎启动后退出，代码 " + process.ExitCode + "。");
                    try { await owned.CallAsync("aria2.getVersion", [], deadline.Token); return owned; }
                    catch (HttpRequestException) { await Task.Delay(100, deadline.Token); }
                }
            }
            catch (Exception error)
            {
                if (owned is not null) await owned.DisposeAsync();
                else
                {
                    try { if (!process.HasExited) process.Kill(true); } catch { }
                    job.Dispose(); process.Dispose();
                }
                ct.ThrowIfCancellationRequested();
                if (attempt < 1) continue;
                throw new DownloadEngineUnavailableException("并行下载引擎无法启动，将使用兼容下载。", error);
            }
        }
    }

    public async Task<JsonElement> CallAsync(string method, object[] arguments, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var parameters = new object[arguments.Length + 1]; parameters[0] = "token:" + secret;
        Array.Copy(arguments, 0, parameters, 1, arguments.Length);
        using var content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = Interlocked.Increment(ref id), method, @params = parameters }), Encoding.UTF8, "application/json");
        using var response = await rpc.PostAsync("", content, timeout.Token);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        if (json.RootElement.TryGetProperty("error", out _)) throw new IOException("下载引擎 RPC 操作失败：" + method);
        return json.RootElement.GetProperty("result").Clone();
    }

    public async Task StopAsync()
    {
        if (stopped) return;
        stopped = true;
        using var deadline = new CancellationTokenSource(options.StopTimeout);
        try
        {
            if (!process.HasExited)
            {
                try { await CallAsync("aria2.shutdown", [], deadline.Token); } catch { }
                await process.WaitForExitAsync(deadline.Token);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            job.Dispose();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
    }
    public async ValueTask DisposeAsync() { await StopAsync(); rpc.Dispose(); process.Dispose(); }
}
