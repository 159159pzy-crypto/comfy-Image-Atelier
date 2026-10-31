using System.Diagnostics;
using System.Net;

namespace Anima.Launcher.Core;

internal sealed class HttpClientTransport(SafeHttpSourceClient source, DownloadPolicy policy) : IDownloadTransport
{
    public Task FetchAsync(DownloadRequest request, string sourceUrl, IProgress<DownloadProgress>? progress,
        CancellationToken ct, DownloadTransportOptions options) =>
        FetchAsync(request, sourceUrl, progress, ct, options.DetectSlow, options.Retries, options.RestartWithoutRange, options.Space);
    public async Task FetchAsync(DownloadRequest request, string url, IProgress<DownloadProgress>? progress,
        CancellationToken ct, bool detectSlow, int retries, bool restartWithoutRange = false, DownloadDiskSpaceReservation? space = null)
    {
        await using var disk = new DiskScope(space);
        var part = request.Destination + (restartWithoutRange ? ".http-restart.part" : ".part");
        var forceFull = restartWithoutRange;
        var restarted = restartWithoutRange;
        if (restartWithoutRange)
        {
            if (await Downloads.Verify(part, request.Size, request.Sha256, ct)) { ct.ThrowIfCancellationRequested(); Publish(request, part, progress); return; }
            // Retain the independent aria2 checkpoint. Budget the complete new file before replacing any HTTP scratch data.
            await disk.FreshAsync(request.Destination, part, request.Size, ct);
            File.Delete(part); File.Delete(part + ".json");
        }
        for (var attempt = 0; ; attempt++)
        {
            try { await Attempt(request, url, part, progress, ct, detectSlow, forceFull, disk.Active); return; }
            catch (DownloadSourceException ex) when (ex.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && !restarted && !ct.IsCancellationRequested)
            {
                if (await Downloads.Verify(part, request.Size, request.Sha256, ct)) { ct.ThrowIfCancellationRequested(); Publish(request, part, progress); return; }
                // Preserve the rejected continuous prefix until a separately downloaded full artifact passes SHA.
                part = request.Destination + ".http-restart.part";
                await disk.FreshAsync(request.Destination, part, request.Size, ct);
                File.Delete(part); File.Delete(part + ".json");
                forceFull = true; restarted = true; retries = 0;
                Report(request, progress, 0, "服务器拒绝断点，重新下载一次", "Connecting");
            }
            catch (InvalidDataException) when (!restarted && !ct.IsCancellationRequested && !File.Exists(request.Destination) && (File.Exists(part) || retries > 0))
            {
                File.Delete(part); File.Delete(part + ".json");
                restarted = true;
                Report(request, progress, 0, "断点与来源不符，重新下载", "Connecting");
            }
            catch (Exception ex) when (attempt < retries && !ct.IsCancellationRequested && IsRetryable(ex))
            {
                var retryAt = DownloadSourceHealth.RetryAt(ex, policy, attempt);
                var delay = retryAt - DateTimeOffset.UtcNow;
                progress?.Report(new(request.Name, File.Exists(part) ? new FileInfo(part).Length : 0, request.Size,
                    ex is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } ? "来源限流，等待后自动续传" : "连接中断，自动续传")
                { Phase = "RetryWaiting", RetryAt = retryAt, Engine = "HttpClient", Attempt = attempt + 1, TaskId = request.TaskId });
                if (delay > policy.MaximumAutomaticRetryDelay) throw;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw new TimeoutException($"{request.Name} 下载响应超时，已保留可续传的数据；请重试或切换来源。"); }
        }
    }

    internal static bool IsRetryable(Exception ex) => ex is HttpRequestException { StatusCode: null }
        or HttpRequestException { StatusCode: >= HttpStatusCode.InternalServerError }
        or HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } or OperationCanceledException
        or TimeoutException and not SlowDownloadException;

    private async Task Attempt(DownloadRequest request, string url, string part, IProgress<DownloadProgress>? progress,
        CancellationToken ct, bool detectSlow, bool forceFull, DownloadDiskSpaceReservation? space)
    {
        var destination = request.Destination;
        var size = request.Size;
        var state = part + ".json";
        ResumeInfo? resume = null;
        try { if (File.Exists(state)) resume = JsonFile.Read<ResumeInfo>(state); } catch { }
        if (resume?.Sha256.Equals(request.Sha256, StringComparison.OrdinalIgnoreCase) != true || resume.Size != size
            || !File.Exists(part) || new FileInfo(part).Length > size)
        { File.Delete(part); File.Delete(state); resume = null; }
        var offset = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (space is not null) await space.SetPrimaryPathAsync(part, ct);
        else Safety.FreeSpace(destination, Math.Max(0, size - DownloadDiskSpace.AllocatedBytes(part)));
        while (offset != size)
        {
            if (space is not null) await space.CheckAsync(ct);
            long? end = !forceFull && size > 64L * 1024 * 1024 ? Math.Min(size - 1, offset + 32L * 1024 * 1024 - 1) : null;
            using var response = await source.OpenAsync(url, offset > 0 || end.HasValue ? offset : null, end, resume?.Url == url ? resume.ETag : null, ct);
            SafeHttpSourceClient.EnsureSuccess(response.Message);
            if (offset > 0 && response.Message.StatusCode == HttpStatusCode.OK) offset = 0;
            var expectedEnd = SafeHttpSourceClient.ValidateRange(response.Message, offset, response.Message.StatusCode == HttpStatusCode.PartialContent ? end : null, size, false);
            var responseEtag = response.Message.Headers.ETag?.ToString();
            if (response.Message.StatusCode == HttpStatusCode.PartialContent && resume?.Url == url
                && resume.ETag is { } oldEtag && !oldEtag.StartsWith("W/", StringComparison.OrdinalIgnoreCase)
                && responseEtag is not null && !responseEtag.StartsWith("W/", StringComparison.OrdinalIgnoreCase)
                && oldEtag != responseEtag) throw new InvalidDataException("下载来源改变了 ETag，不能混合续传内容。");
            resume = new(request.Sha256, size, url, response.Message.Headers.ETag?.ToString());
            JsonFile.Write(state, resume);
            await using var input = await response.Message.Content.ReadAsStreamAsync(ct);
            await using var output = new FileStream(part, offset == 0 ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.Read, 131072, true);
            var buffer = new byte[131072];
            var received = offset;
            var lastProgress = Environment.TickCount64;
            var speedStarted = Stopwatch.GetTimestamp();
            long speedBytes = 0;
            while (true)
            {
                var count = await source.ReadAsync(input, buffer, ct);
                if (count == 0) break;
                received += count;
                if (received > expectedEnd + 1) throw new InvalidDataException("下载超过声明大小。");
                await output.WriteAsync(buffer.AsMemory(0, count), ct);
                speedBytes += count;
                var elapsed = Stopwatch.GetElapsedTime(speedStarted);
                if (detectSlow && elapsed >= policy.SlowWindow)
                {
                    if (speedBytes / elapsed.TotalSeconds < policy.MinimumBytesPerSecond)
                        throw new SlowDownloadException("下载来源持续低速，已保留断点。");
                    speedStarted = Stopwatch.GetTimestamp(); speedBytes = 0;
                }
                if (Environment.TickCount64 - lastProgress > 200)
                {
                    progress?.Report(new(request.Name, received, size, "下载中")
                    { Phase = "Downloading", Engine = "HttpClient", Connections = 1, TaskId = request.TaskId,
                        Speed = elapsed.TotalSeconds > 0 ? speedBytes / elapsed.TotalSeconds : 0 });
                    lastProgress = Environment.TickCount64;
                }
            }
            await output.FlushAsync(ct);
            if (received < expectedEnd + 1) throw new HttpRequestException("连接提前结束，将从已下载位置继续。");
            offset = received;
        }
        Report(request, progress, size, "SHA-256 校验中", "Verifying");
        if (!await Downloads.Verify(part, size, request.Sha256, ct))
        {
            File.Delete(part); File.Delete(state);
            throw new InvalidDataException("下载不完整或 SHA-256 不符，已删除无效临时文件；可切换来源重试。");
        }
        ct.ThrowIfCancellationRequested();
        Publish(request, part, progress);
    }

    private static void Publish(DownloadRequest request, string part, IProgress<DownloadProgress>? progress)
    {
        Report(request, progress, request.Size, "发布已校验文件", "Publishing");
        File.Move(part, request.Destination, false);
        File.Delete(part + ".json");
        if (part == request.Destination + ".http-restart.part")
        {
            File.Delete(request.Destination + ".part");
            File.Delete(request.Destination + ".part.json");
        }
        Report(request, progress, request.Size, "完成", "Completed");
    }
    private static void Report(DownloadRequest request, IProgress<DownloadProgress>? progress, long bytes, string stage, string phase) =>
        progress?.Report(new(request.Name, bytes, request.Size, stage) { TaskId = request.TaskId, Phase = phase, Engine = "HttpClient" });

    private sealed class DiskScope(DownloadDiskSpaceReservation? primary) : IAsyncDisposable
    {
        private DownloadDiskSpaceReservation? fresh;
        public DownloadDiskSpaceReservation? Active => fresh ?? primary;
        public async Task FreshAsync(string destination, string path, long size, CancellationToken ct)
        {
            if (primary is null) Safety.FreeSpace(destination, size);
            else fresh = await primary.ReserveFreshFileAsync(path, size, ct);
        }
        public ValueTask DisposeAsync() => fresh is null ? ValueTask.CompletedTask : fresh.DisposeAsync();
    }
}
