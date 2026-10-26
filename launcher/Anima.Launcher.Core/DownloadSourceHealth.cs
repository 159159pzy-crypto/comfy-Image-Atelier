using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Anima.Launcher.Core;

/// <summary>Short-lived, process-only host health. Credentials contribute only a salted scope digest, never persisted values.</summary>
public sealed class DownloadSourceHealth
{
    internal static readonly DownloadSourceHealth Shared = new();
    private readonly byte[] salt = RandomNumberGenerator.GetBytes(32);
    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private sealed class Entry
    {
        public readonly object Sync = new();
        public DateTimeOffset Cooldown, RateLimitUntil, Success, WaitUntil;
        public Task Wait = Task.CompletedTask;
    }
    internal string Key(string url, Func<string, string?> credentials, NetworkPolicySnapshot network)
    {
        var uri = Safety.Https(url);
        var token = uri.Port == 443 && uri.Host is "huggingface.co" or "civitai.com" ? credentials(uri.Host) : null;
        var scope = "anonymous";
        if (!string.IsNullOrEmpty(token))
        {
            var bytes = Encoding.UTF8.GetBytes(token);
            var combined = new byte[salt.Length + bytes.Length]; salt.CopyTo(combined, 0); bytes.CopyTo(combined, salt.Length);
            scope = Convert.ToHexString(SHA256.HashData(combined));
            CryptographicOperations.ZeroMemory(bytes); CryptographicOperations.ZeroMemory(combined);
        }
        return uri.Host + ":" + uri.Port + "|" + network.Mode + "|" + network.ProxyAddress + "|" + scope;
    }
    internal DateTimeOffset? Cooldown(string key)
    {
        if (!entries.TryGetValue(key, out var entry)) return null;
        lock (entry.Sync) return entry.Cooldown > DateTimeOffset.UtcNow ? entry.Cooldown : null;
    }
    internal string[] Order(string[] urls, Func<string, string> key)
    {
        var now = DateTimeOffset.UtcNow;
        return urls.Select((url, index) =>
        {
            var entry = entries.GetOrAdd(key(url), _ => new());
            lock (entry.Sync) return (Url: url, Index: index, Cooldown: entry.Cooldown, Success: entry.Success);
        }).OrderBy(item => item.Cooldown > now ? 1 : 0)
            .ThenBy(item => item.Cooldown > now ? item.Cooldown : DateTimeOffset.MinValue)
            .ThenByDescending(item => item.Success).ThenBy(item => item.Index).Select(item => item.Url).ToArray();
    }
    internal async Task WaitAsync(string key, TimeSpan maximumWait, CancellationToken ct)
    {
        if (!entries.TryGetValue(key, out var entry)) return;
        while (true)
        {
            Task wait;
            lock (entry.Sync)
            {
                var delay = entry.Cooldown - DateTimeOffset.UtcNow;
                if (delay <= TimeSpan.Zero) return;
                if (delay > maximumWait)
                    throw new DownloadSourceException("来源要求较长冷却，断点已保留，请稍后继续。", HttpStatusCode.TooManyRequests, entry.Cooldown);
                // One shared deadline per credential scope, not an exponential wait per aria2 connection.
                if (entry.WaitUntil != entry.Cooldown || entry.Wait.IsCompleted)
                { entry.WaitUntil = entry.Cooldown; entry.Wait = Task.Delay(delay); }
                wait = entry.Wait;
            }
            await wait.WaitAsync(ct);
        }
    }
    internal void RateLimited(string key, DateTimeOffset? retryAt)
    {
        var entry = entries.GetOrAdd(key, _ => new());
        var until = retryAt ?? DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2 + Random.Shared.NextDouble() * .4);
        lock (entry.Sync)
        {
            if (until > entry.RateLimitUntil) entry.RateLimitUntil = until;
            if (until > entry.Cooldown) entry.Cooldown = until;
        }
    }
    internal void Failure(string key)
    {
        var entry = entries.GetOrAdd(key, _ => new());
        lock (entry.Sync)
        {
            var until = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
            if (until > entry.Cooldown) entry.Cooldown = until;
        }
    }
    internal void Succeeded(string key)
    {
        var entry = entries.GetOrAdd(key, _ => new());
        // A concurrently completing file must not cancel a newer 429 affecting the same credential scope.
        lock (entry.Sync) { entry.Cooldown = entry.RateLimitUntil; entry.Success = DateTimeOffset.UtcNow; }
    }
    internal static DateTimeOffset RetryAt(Exception error, DownloadPolicy policy, int attempt)
    {
        if (error is DownloadSourceException { RetryAt: { } specified }) return specified;
        var delay = policy.RetryDelay.TotalMilliseconds * Math.Pow(2, attempt) * (1 + Random.Shared.NextDouble() * .2);
        return DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(delay);
    }
}
