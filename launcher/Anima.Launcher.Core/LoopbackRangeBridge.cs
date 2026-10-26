using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Anima.Launcher.Core;

/// <summary>A capability-protected, single-resource endpoint. Never acts as an open proxy and never forwards redirects.</summary>
public sealed class LoopbackRangeBridge : IAsyncDisposable
{
    public const string CapabilityHeader = "X-Anima-Transfer";
    private readonly SafeHttpSourceClient source;
    private readonly DownloadRequest request;
    private readonly string upstream;
    private readonly CancellationTokenSource lifetime;
    private readonly TaskCompletionSource<Exception> failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly byte[] capability;
    private WebApplication? app;
    private long bytesServed;
    private int activeRequests;
    private int activeStreams;
    private string? upstreamEtag;
    private readonly string route = "/downloads/" + Guid.NewGuid().ToString("N");
    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public Uri Address { get; private set; } = null!;
    public Task<Exception> Failure => failure.Task;
    public long BytesServed => Interlocked.Read(ref bytesServed);
    public int ActiveRequests => Volatile.Read(ref activeRequests);
    public int ActiveStreams => Volatile.Read(ref activeStreams);

    private LoopbackRangeBridge(SafeHttpSourceClient source, DownloadRequest request, string upstream, CancellationToken ct)
    {
        this.source = source;
        this.request = request;
        this.upstream = upstream;
        capability = Encoding.ASCII.GetBytes(Token);
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
    }

    public static async Task<LoopbackRangeBridge> StartAsync(SafeHttpSourceClient source, DownloadRequest request, string upstream, CancellationToken ct)
    {
        var bridge = new LoopbackRangeBridge(source, request, upstream, ct);
        try { await bridge.Start(ct); return bridge; }
        catch { await bridge.DisposeAsync(); throw; }
    }

    private async Task Start(CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http1);
            options.Limits.MaxRequestBodySize = 0;
            options.Limits.MaxRequestHeadersTotalSize = 8192;
            options.Limits.MaxRequestHeaderCount = 32;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            options.Limits.MinResponseDataRate = null;
            options.Limits.MaxConcurrentConnections = 12;
        });
        app = builder.Build();
        app.Run(Handle);
        await app.StartAsync(ct);
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
        Address = new Uri(addresses.Addresses.Single() + route);
    }

    private async Task Handle(HttpContext context)
    {
        var supplied = context.Request.Headers[CapabilityHeader].ToString();
        if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote)
            || context.Request.Host.Host != "127.0.0.1" || context.Request.Path != route || context.Request.QueryString.HasValue
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(supplied), capability))
        { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
        if (context.Request.Method is not ("GET" or "HEAD"))
        { context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed; return; }
        if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
        { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
        var from = 0L;
        var to = request.Size - 1;
        var ranged = context.Request.Headers.TryGetValue("Range", out var rangeText);
        if (ranged)
        {
            if (!RangeHeaderValue.TryParse(rangeText.ToString(), out var range) || range.Unit != "bytes" || range.Ranges.Count != 1)
            { RejectRange(context); return; }
            var item = range.Ranges.Single();
            if (item.From is { } start) { from = start; to = Math.Min(to, item.To ?? to); }
            else if (item.To is { } suffix && suffix > 0) from = Math.Max(0, request.Size - suffix);
            else { RejectRange(context); return; }
            if (from < 0 || from >= request.Size || to < from) { RejectRange(context); return; }
        }
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, context.RequestAborted);
        var ct = canceled.Token;
        Interlocked.Increment(ref activeRequests);
        try
        {
            context.Response.StatusCode = ranged ? StatusCodes.Status206PartialContent : StatusCodes.Status200OK;
            context.Response.ContentType = "application/octet-stream";
            context.Response.ContentLength = to - from + 1;
            context.Response.Headers.AcceptRanges = "bytes";
            context.Response.Headers.ETag = "\"sha256-" + request.Sha256.ToLowerInvariant() + "\"";
            context.Response.Headers.CacheControl = "no-store";
            if (ranged) context.Response.Headers.ContentRange = $"bytes {from}-{to}/{request.Size}";
            if (context.Request.Method == "HEAD") return;
            var buffer = new byte[131072];
            for (var offset = from; offset <= to;)
            {
                var end = Math.Min(to, offset + 32L * 1024 * 1024 - 1);
                using var response = await source.OpenAsync(upstream, offset, end, Volatile.Read(ref upstreamEtag), ct);
                SafeHttpSourceClient.ValidateRange(response.Message, offset, end, request.Size, true);
                var etag = response.Message.Headers.ETag?.ToString();
                if (etag is not null && !etag.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
                {
                    var previous = Interlocked.CompareExchange(ref upstreamEtag, etag, null);
                    if (previous is not null && !string.Equals(previous, etag, StringComparison.Ordinal))
                        throw new InvalidDataException("来源在并行传输中改变了文件 ETag，已停止此来源以避免混合内容。");
                }
                Interlocked.Increment(ref activeStreams);
                try
                {
                    await using var input = await response.Message.Content.ReadAsStreamAsync(ct);
                    var remaining = end - offset + 1;
                    while (remaining > 0)
                    {
                        var count = await source.ReadAsync(input, buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
                        if (count == 0) throw new HttpRequestException("下载来源提前结束分段响应。");
                        await context.Response.Body.WriteAsync(buffer.AsMemory(0, count), ct);
                        Interlocked.Add(ref bytesServed, count);
                        remaining -= count;
                    }
                    if (await source.ReadAsync(input, buffer.AsMemory(0, 1), ct) != 0)
                        throw new InvalidDataException("下载来源返回超出声明范围的数据。");
                }
                finally { Interlocked.Decrement(ref activeStreams); }
                offset = end + 1;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { context.Abort(); }
        catch (IOException) when (context.RequestAborted.IsCancellationRequested) { context.Abort(); }
        catch (Exception error)
        {
            if (error is OperationCanceledException) error = new TimeoutException("下载来源读取超时。", error);
            failure.TrySetResult(error);
            if (context.Response.HasStarted) context.Abort();
            else
            {
                context.Response.Headers.Clear();
                context.Response.ContentLength = 0;
                context.Response.StatusCode = error is HttpRequestException { StatusCode: { } code } ? (int)code : StatusCodes.Status502BadGateway;
                if (error is DownloadSourceException { RetryAt: { } retry })
                    context.Response.Headers.RetryAfter = retry.ToString("R", CultureInfo.InvariantCulture);
            }
        }
        finally { Interlocked.Decrement(ref activeRequests); }
    }

    private void RejectRange(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
        context.Response.Headers.ContentRange = "bytes */" + request.Size;
        context.Response.ContentLength = 0;
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        if (app is not null)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await app.StopAsync(deadline.Token); } catch (OperationCanceledException) { }
            await app.DisposeAsync();
            app = null;
        }
        lifetime.Dispose();
    }
}
