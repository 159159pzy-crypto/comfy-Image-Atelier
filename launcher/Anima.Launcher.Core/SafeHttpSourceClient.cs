using System.Net;
using System.Net.Http.Headers;
using System.Reflection;

namespace Anima.Launcher.Core;

/// <summary>The only component permitted to contact a download source. The aria2 process sees loopback URLs only.</summary>
public sealed class SafeHttpSourceClient : IDisposable
{
    // Shared by HTTP fallback and every bridge, rather than multiplied per launcher task.
    private static readonly SemaphoreSlim Connections = new(8, 8);
    private readonly HttpClient client;
    private readonly Func<string, string?> credential;
    private readonly DownloadPolicy policy;
    private readonly NetworkPolicySnapshot network;
    private readonly DownloadSourceHealth? health;

    public SafeHttpSourceClient(Func<string, string?> credential, DownloadPolicy policy, NetworkPolicySnapshot network,
        HttpMessageHandler? handler = null, DownloadSourceHealth? health = null)
    {
        this.credential = credential;
        this.policy = policy;
        this.network = network;
        this.health = health;
        client = new HttpClient(handler ?? network.CreateHttpHandler(), disposeHandler: handler is null)
        { Timeout = Timeout.InfiniteTimeSpan };
        var assembly = Assembly.GetEntryAssembly() ?? typeof(Downloads).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
            ?? assembly.GetName().Version?.ToString() ?? "unknown";
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AnimaRandomStudio", version));
    }

    public async Task<SourceResponse> OpenAsync(string url, long? from, long? to, string? etag, CancellationToken ct, string? accept = null)
    {
        var original = Safety.Https(url);
        var sourceKey = health?.Key(url, credential, network);
        if (sourceKey is not null) await health!.WaitAsync(sourceKey, policy.MaximumAutomaticRetryDelay, ct);
        var uri = original;
        for (var redirects = 0; redirects < 10; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.AcceptEncoding.ParseAdd("identity");
            if (accept is not null) request.Headers.Accept.ParseAdd(accept);
            if (from is not null)
            {
                request.Headers.Range = new RangeHeaderValue(from, to);
                if (!string.IsNullOrEmpty(etag) && !etag.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
                    request.Headers.TryAddWithoutValidation("If-Range", etag);
            }
            if (uri.Host == original.Host && uri.Port == 443 && original.Port == 443 && uri.Host is "civitai.com" or "huggingface.co")
            {
                var token = credential(uri.Host);
                if (!string.IsNullOrWhiteSpace(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            await Connections.WaitAsync(ct);
            HttpResponseMessage response;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(policy.ResponseTimeout);
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            }
            catch { Connections.Release(); throw; }
            var lease = new SourceResponse(response, Connections);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308))
            {
                if (response.StatusCode == HttpStatusCode.TooManyRequests && sourceKey is not null)
                {
                    var retry = response.Headers.RetryAfter;
                    health!.RateLimited(sourceKey, retry?.Date ?? (retry?.Delta is { } delay ? DateTimeOffset.UtcNow + delay : (DateTimeOffset?)null));
                }
                return lease;
            }
            var location = response.Headers.Location;
            lease.Dispose();
            if (location is null) throw new HttpRequestException("下载重定向缺少目标。");
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
            // Signed CDN queries are ephemeral. Never return or persist the resolved URI.
            if (uri.Scheme != "https" || uri.UserInfo.Length > 0) throw new InvalidDataException("拒绝不安全的下载重定向。");
        }
        throw new HttpRequestException("下载重定向次数过多。");
    }

    public static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var retry = response.Headers.RetryAfter;
        var retryAt = retry?.Date ?? (retry?.Delta is { } delay ? DateTimeOffset.UtcNow + delay : (DateTimeOffset?)null);
        var tokenSite = response.RequestMessage?.RequestUri is { Port: 443, Host: "huggingface.co" or "civitai.com" };
        var denied = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
        var message = denied && tokenSite
            ? "来源拒绝访问（HTTP " + (int)response.StatusCode + "）；请确认已获该模型访问权限，必要时在设置中保存对应官方站点令牌。"
            : denied
                ? "下载来源拒绝请求（HTTP " + (int)response.StatusCode + "）；可能是该来源或网络访问受限。已保留可续传内容，请重试或切换下载来源。"
                : "下载来源返回 HTTP " + (int)response.StatusCode + "。";
        throw new DownloadSourceException(message, response.StatusCode, retryAt);
    }

    public static long ValidateRange(HttpResponseMessage response, long from, long? requestedEnd, long size, bool requireRange)
    {
        EnsureSuccess(response);
        if (requireRange && response.StatusCode != HttpStatusCode.PartialContent)
            throw new RangeNotSupportedException("下载来源未按请求返回分段数据。");
        long end;
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var range = response.Content.Headers.ContentRange;
            if (range?.Unit != "bytes" || range.From != from || range.Length != size || range.To is not { } last)
                throw new InvalidDataException("服务器断点范围与清单不符。");
            end = last;
            if (requestedEnd is { } requested && end != requested)
                throw new InvalidDataException("服务器返回的分段终点与请求不符。");
        }
        else end = size - 1;
        if (end < from || end >= size) throw new InvalidDataException("下载范围不合法。");
        if (response.Content.Headers.ContentLength is { } length && length != end + 1 - from)
            throw new InvalidDataException("下载大小与锁定文件不符；可能返回了登录页或来源已更改。");
        if (response.Content.Headers.ContentEncoding.Any(value => !value.Equals("identity", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("下载来源改变了文件编码，无法安全续传。");
        return end;
    }

    public async Task<int> ReadAsync(Stream input, Memory<byte> buffer, CancellationToken ct)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(policy.IdleTimeout);
        try { return await input.ReadAsync(buffer, idle.Token); }
        catch (IOException error) { throw new HttpRequestException("下载连接中断，已保留断点。", error); }
    }

    public async Task<bool> SupportsRangesAsync(string url, long size, CancellationToken ct)
    {
        using var response = await OpenAsync(url, 0, 0, null, ct);
        EnsureSuccess(response.Message);
        if (response.Message.StatusCode == HttpStatusCode.OK)
        {
            ValidateRange(response.Message, 0, null, size, false);
            return false;
        }
        ValidateRange(response.Message, 0, 0, size, true);
        return true;
    }

    public void Dispose() => client.Dispose();
}

public sealed class SourceResponse(HttpResponseMessage message, SemaphoreSlim semaphore) : IDisposable
{
    private bool disposed;
    public HttpResponseMessage Message { get; } = message;
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Message.Dispose();
        semaphore.Release();
    }
}
