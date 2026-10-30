using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Anima.Launcher.Core;

/// <summary>Injection is scoped to a single operation; tests never change process-wide HTTP settings.</summary>
public sealed record DependencyPackageSourceOptions
{
    internal string HealthScope { get; } = Guid.NewGuid().ToString("N");
    public Func<NetworkPolicySnapshot, HttpMessageHandler>? HandlerFactory { get; init; }
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(8);
    public int MaximumBytes { get; init; } = 2 * 1024 * 1024;
}

/// <summary>Small, bounded source probes. Package versions and hashes always come from the reviewed plan.</summary>
public static class DependencyPackageSources
{
    private const string SimpleAccept = "application/vnd.pypi.simple.v1+json, application/vnd.pypi.simple.v1+html;q=0.8, text/html;q=0.7";
    private static readonly ConcurrentDictionary<string, DateTimeOffset> UnavailableSources = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, DateTimeOffset> CheckedSources = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FirstProbeGates = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Score> SourceScores = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, DateTimeOffset> ExplicitRefreshes = new(StringComparer.Ordinal);
    private static readonly object RefreshSync = new();
    private static readonly ConcurrentDictionary<string, HttpMessageHandler> ProbeHandlers = new(StringComparer.Ordinal);
    private static readonly Regex Requirement = new(@"^\s*([A-Za-z0-9][A-Za-z0-9._-]*)(?:\[[^\]]*\])?\s*(?:(?:===|==)\s*([^\s,;#*]+))?", RegexOptions.Compiled);
    private static readonly Regex Anchor = new(@"<a\b(?<attrs>[^>]{0,16384})>(?<text>.*?)</a\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex Attribute = new("(?<key>[\\w:-]+)\\s*=\\s*(?:\"(?<value>[^\"]*)\"|'(?<value>[^']*)'|(?<value>[^\\s>]+))", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private sealed record Requested(string Name, string Version);
    private sealed record Link(string FileName, string Url, string Sha256, long Size, bool Metadata, string MetadataHash);
    private sealed record Score(string Index, int Order, int Available, int Metadata, double BytesPerSecond);

    public static Task<string[]> SelectIndexesAsync(LauncherConfig config, IEnumerable<string> requirements, CancellationToken ct,
        Action<string>? log = null) => SelectIndexesAsync(config, requirements, ct, log, null);

    public static async Task<string[]> SelectIndexesAsync(LauncherConfig config, IEnumerable<string> requirements, CancellationToken ct,
        Action<string>? log, DependencyPackageSourceOptions? options)
    {
        ct.ThrowIfCancellationRequested();
        var indexes = DownloadSources.PipIndexes(config);
        // One real requirement leaves time for its metadata and transfer sample within
        // the shared eight-second budget. Counting three serial package checks rewards
        // wheel-only mirrors before a slower metadata-capable source can finish.
        var packages = TopLevelPackages(requirements).DistinctBy(p => p.Name).Take(1).ToArray();
        if (packages.Length == 0 || indexes.Length < 2) return indexes;
        var network = NetworkPolicySnapshot.Capture(config);
        var profile = ProfileKey(config);
        var scores = await Task.WhenAll(indexes.Select((index, order) => ProbeIndex(index, order, packages, network, profile, options, ct)));
        ct.ThrowIfCancellationRequested();
        var explicitIndex = config.SourceId == "custom" && !string.IsNullOrWhiteSpace(config.PipIndex)
            ? config.PipIndex.TrimEnd('/') : null;
        var ordered = scores.OrderBy(s => explicitIndex is not null && s.Index.TrimEnd('/').Equals(explicitIndex, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenByDescending(s => s.Available).ThenByDescending(s => s.Metadata)
            .ThenByDescending(s => s.BytesPerSecond).ThenBy(s => s.Order).Select(s => s.Index).ToArray();
        foreach (var score in scores)
            log?.Invoke($"依赖来源测量：{score.Index} · 必要包 {score.Available}/{packages.Length}，独立元数据 {score.Metadata}/{packages.Length}，样本 {score.BytesPerSecond / 1024:0.0} KiB/s。");
        var winner = scores.Single(s => s.Index == ordered[0]);
        log?.Invoke($"依赖来源预检：{winner.Index} · 必要包 {winner.Available}/{packages.Length}，独立元数据 {winner.Metadata}/{packages.Length}。");
        return ordered;
    }

    public static Task<DependencyPackage> ResolveAsync(DependencyPackage package, LauncherConfig config, CancellationToken ct) =>
        ResolveAsync(package, config, ct, null);

    public static async Task<DependencyPackage> ResolveAsync(DependencyPackage package, LauncherConfig config, CancellationToken ct,
        DependencyPackageSourceOptions? options)
    {
        ct.ThrowIfCancellationRequested();
        if (package.Kind is not ("wheel" or "archive") || !Identity(package.Name, package.Version, package.FileName, package.Sha256)) return package;
        var network = NetworkPolicySnapshot.Capture(config);
        var original = StableUrl(package.Url, package.FileName);
        var size = package.Size;
        var matches = await FindFiles(package.Name, package.Version, package.FileName, package.Sha256, size, config, options, ct);
        var sizes = matches.Where(match => match.Size > 0).Select(match => match.Size).Distinct().ToArray();
        if (size <= 0 && sizes.Length == 1) size = sizes[0];
        // A Simple JSON file record already binds its size to the exact report hash.
        // Probe a payload only if indexes omit size or disagree about it.
        if (size <= 0)
        {
            foreach (var candidate in (original is null ? Array.Empty<string>() : [original]).Concat(matches.Select(match => match.Url)).Distinct().Take(4))
            {
                var key = HealthKey("artifact:" + new Uri(candidate).GetLeftPart(UriPartial.Authority), network, ProfileKey(config), options);
                using var ticket = await EnterProbe(key, false, ct);
                try
                {
                    if (ticket.Allowed)
                    {
                        using var session = new ProbeSession(network, options, ct);
                        size = await ReadSize(session, candidate);
                        if (size > 0) break;
                    }
                }
                catch (Exception ex) when (Recoverable(ex, ct)) { MarkUnavailable(key, ex); }
            }
        }
        if (size <= 0) return package;
        var sources = matches.Where(match => match.Size == 0 || match.Size == size).Select(match => match.Url).ToList();
        // The report supplies the expected hash; either exact-hash index metadata or a
        // checked range supplies size. Every transfer verifies the complete SHA-256.
        if (original is not null) sources.Add(original);
        if (sources.Count == 0) return package;
        return package with { Size = size, Sources = sources.Distinct(StringComparer.Ordinal).ToArray() };
    }

    public static Task<string[]> RefreshSourcesAsync(string name, string version, string filename, string sha, long size,
        LauncherConfig config, CancellationToken ct) => RefreshSourcesAsync(name, version, filename, sha, size, config, ct, null);

    public static async Task<string[]> RefreshSourcesAsync(string name, string version, string filename, string sha, long size,
        LauncherConfig config, CancellationToken ct, DependencyPackageSourceOptions? options)
    {
        ct.ThrowIfCancellationRequested();
        if (size <= 0 || !Identity(name, version, filename, sha)) return [];
        return (await FindFiles(name, version, filename, sha, size, config, options, ct, refresh: true))
            .Where(match => match.Size == size).Select(match => match.Url).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static async Task<Link[]> FindFiles(string name, string version, string filename, string sha, long size,
        LauncherConfig config, DependencyPackageSourceOptions? options, CancellationToken ct, bool refresh = false)
    {
        var network = NetworkPolicySnapshot.Capture(config);
        var profile = ProfileKey(config);
        var indexes = DownloadSources.PipIndexes(config);
        var explicitIndex = config.SourceId == "custom" ? config.PipIndex.TrimEnd('/') : null;
        indexes = indexes.Select((index, order) => (Index: index, Order: order,
                Speed: SourceScores.TryGetValue(HealthKey(index, network, profile, options), out var score) ? score.BytesPerSecond : 0))
            .OrderBy(item => explicitIndex is not null && item.Index.TrimEnd('/').Equals(explicitIndex, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenByDescending(item => item.Speed).ThenBy(item => item.Order).Select(item => item.Index).ToArray();
        var lists = await Task.WhenAll(indexes.Select(async index =>
        {
            var key = HealthKey(index, network, profile, options);
            using var ticket = await EnterProbe(key, refresh, ct);
            if (!ticket.Allowed) return [];
            try
            {
                using var session = new ProbeSession(network, options, ct);
                var found = (await ReadIndex(session, index, name)).Where(link => link.FileName == filename
                    && link.Sha256.Equals(sha, StringComparison.OrdinalIgnoreCase)).ToArray();
                var accepted = new List<Link>();
                foreach (var link in found.Take(4))
                {
                    if (size > 0 && link.Size > 0 && link.Size != size) continue;
                    // Exact filename plus SHA-256 already identifies the same content.
                    // Reuse its established size when an HTML mirror omits lengths;
                    // the downloader still verifies the complete size and digest.
                    var measured = link.Size > 0 ? link.Size : size;
                    if (size <= 0 || measured == size) accepted.Add(link with { Size = measured });
                }
                return accepted.ToArray();
            }
            catch (Exception ex) when (Recoverable(ex, ct)) { MarkUnavailable(key, ex); return []; }
        }));
        ct.ThrowIfCancellationRequested();
        return lists.SelectMany(list => list).ToArray();
    }

    private static async Task<Score> ProbeIndex(string index, int order, Requested[] packages, NetworkPolicySnapshot network, string profile,
        DependencyPackageSourceOptions? options, CancellationToken ct)
    {
        var available = 0;
        var metadata = 0;
        double speed = 0;
        using var session = new ProbeSession(network, options, ct);
        Link? sample = null;
        try
        {
            foreach (var package in packages)
            {
                var links = await ReadIndex(session, index, package.Name);
                var matching = links.Where(link => WheelMatches(link.FileName, package.Name, package.Version))
                    .OrderByDescending(link => link.Metadata).ThenBy(link => link.FileName, StringComparer.Ordinal).ToArray();
                if (matching.Length == 0) continue;
                var link = matching[0];
                // An index listing alone does not prove that the listed artifact is accessible.
                var measured = await ReadSize(session, link.Url);
                if (measured <= 0 || link.Size > 0 && measured != link.Size) continue;
                available++;
                sample ??= link with { Size = measured };
                if (!link.Metadata) continue;
                var uri = new UriBuilder(link.Url) { Path = new Uri(link.Url).AbsolutePath + ".metadata" };
                var bytes = await session.Read(uri.Uri.AbsoluteUri);
                if (link.MetadataHash.Length > 0 && !Convert.ToHexString(SHA256.HashData(bytes)).Equals(link.MetadataHash, StringComparison.OrdinalIgnoreCase)) continue;
                var body = Encoding.UTF8.GetString(bytes);
                var actualName = Regex.Match(body, @"(?im)^Name:\s*([^\r\n]+)").Groups[1].Value.Trim();
                var actualVersion = Regex.Match(body, @"(?im)^Version:\s*([^\r\n]+)").Groups[1].Value.Trim();
                if (ComfyDependencyResolver.NormalizeName(actualName) == package.Name
                    && WheelMatches(link.FileName, package.Name, actualVersion)) metadata++;
            }
            if (sample is not null && session.Remaining > 0)
            {
                var count = (int)Math.Min(Math.Min(sample.Size, 64 * 1024), session.Remaining);
                var timer = Stopwatch.StartNew();
                var read = await session.Sample(sample.Url, count, sample.Size);
                speed = read / Math.Max(timer.Elapsed.TotalSeconds, .001);
            }
        }
        catch (Exception ex) when (Recoverable(ex, ct))
        {
            // Once the real artifact responded, exhausting the remaining sampling
            // budget measures slowness; it does not prove that this source is down.
            if (available == 0) MarkUnavailable(HealthKey(index, network, profile, options), ex);
        }
        var key = HealthKey(index, network, profile, options);
        CheckedSources[key] = DateTimeOffset.UtcNow;
        return SourceScores[key] = new(index, order, available, metadata, speed);
    }

    private static string ProfileKey(LauncherConfig config) => config.SourceId + "|" + string.Join("|", DownloadSources.PipIndexes(config).Order(StringComparer.Ordinal));
    private static string PoolKey(NetworkPolicySnapshot network)
    {
        var values = new List<string> { network.Mode, network.ProxyAddress };
        if (network.Mode == "system")
        {
            foreach (var name in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY", "http_proxy", "https_proxy", "all_proxy", "no_proxy" })
                values.Add(name + "=" + Environment.GetEnvironmentVariable(name));
            if (OperatingSystem.IsWindows())
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
                foreach (var name in new[] { "ProxyEnable", "ProxyServer", "ProxyOverride", "AutoConfigURL" }) values.Add(name + "=" + key?.GetValue(name));
            }
            // Include explicit DefaultProxy replacements without invoking GetProxy or
            // IsBypassed here: WPAD/PAC discovery outside the HTTP deadline can block.
            var proxy = HttpClient.DefaultProxy;
            values.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(proxy).ToString());
            if (proxy is WebProxy manual)
            {
                values.Add(manual.Address?.AbsoluteUri ?? "direct");
                values.Add(manual.BypassProxyOnLocal.ToString());
                values.AddRange(manual.BypassList);
            }
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", values))));
    }
    private static string HealthKey(string index, NetworkPolicySnapshot network, string profile, DependencyPackageSourceOptions? options) =>
        index.TrimEnd('/') + "|" + PoolKey(network) + "|" + profile + "|" + (options?.HealthScope ?? "production");
    private static async Task<ProbeTicket> EnterProbe(string key, bool refresh, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (refresh)
        {
            // Owner-level source changes refresh many files. Recheck a suppressed host
            // once for that wave, then share its result instead of spending 8s per file.
            // A changed network/source configuration always has a different key.
            lock (RefreshSync)
            {
                var now = DateTimeOffset.UtcNow;
                if (!ExplicitRefreshes.TryGetValue(key, out var previous) || previous + TimeSpan.FromMinutes(2) <= now)
                {
                    ExplicitRefreshes[key] = now;
                    UnavailableSources.TryRemove(key, out _);
                    CheckedSources.TryRemove(key, out _);
                }
            }
        }
        bool Down() => UnavailableSources.TryGetValue(key, out var until) && until > DateTimeOffset.UtcNow;
        bool Checked() => CheckedSources.TryGetValue(key, out var at) && at + TimeSpan.FromMinutes(2) > DateTimeOffset.UtcNow;
        if (Down()) return new(key, null, false);
        if (Checked()) return new(key, null, true);
        var gate = FirstProbeGates.GetOrAdd(key, _ => new(1, 1));
        await gate.WaitAsync(ct);
        if (Down()) { gate.Release(); return new(key, null, false); }
        if (Checked()) { gate.Release(); return new(key, null, true); }
        // The first real package request doubles as the host probe. Other packages
        // wait for its outcome instead of each spending an independent timeout.
        return new(key, gate, true);
    }
    private sealed class ProbeTicket(string key, SemaphoreSlim? gate, bool allowed) : IDisposable
    {
        public bool Allowed { get; } = allowed;
        public void Dispose()
        {
            if (gate is null) return;
            CheckedSources[key] = DateTimeOffset.UtcNow;
            gate.Release();
        }
    }
    private static void MarkUnavailable(string key, Exception error)
    {
        // A single missing package, oversized index or hash mismatch says nothing about
        // the other packages on this host. Only host/transport failures suppress later probes.
        if (error is OperationCanceledException || error is HttpRequestException http
            && (http.StatusCode is null or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests || (int)http.StatusCode >= 500))
            UnavailableSources[key] = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(2);
    }

    private static IEnumerable<Requested> TopLevelPackages(IEnumerable<string> requirements)
    {
        foreach (var input in requirements)
        {
            var content = File.Exists(input) ? File.ReadAllText(input) : input;
            foreach (var line in content.Replace("\r", "").Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith('#') || trimmed.StartsWith('-') || trimmed.Contains(" @ ")
                    || trimmed.StartsWith("git+", StringComparison.OrdinalIgnoreCase)) continue;
                var match = Requirement.Match(trimmed);
                if (match.Success) yield return new(ComfyDependencyResolver.NormalizeName(match.Groups[1].Value), match.Groups[2].Value);
            }
        }
    }

    private static bool Identity(string name, string version, string filename, string sha) =>
        Regex.IsMatch(name, @"^[A-Za-z0-9][A-Za-z0-9._-]*$") && Regex.IsMatch(version, @"^[A-Za-z0-9.!+_-]+$")
        && Regex.IsMatch(sha, @"^[A-Fa-f0-9]{64}$") && filename == Path.GetFileName(filename)
        && !filename.Contains('\\') && !filename.Contains('/') && (WheelMatches(filename, name, version) || ArchiveMatches(filename, name, version));

    private static bool ArchiveMatches(string filename, string name, string version)
    {
        var extension = new[] { ".tar.gz", ".tar.bz2", ".tar.xz", ".zip" }.FirstOrDefault(suffix => filename.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        if (extension is null || version.Length == 0) return false;
        var stem = filename[..^extension.Length];
        var suffix = "-" + version;
        return stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            && ComfyDependencyResolver.NormalizeName(stem[..^suffix.Length]) == ComfyDependencyResolver.NormalizeName(name);
    }

    private static bool WheelMatches(string filename, string name, string version)
    {
        var fields = filename.Split('-');
        return fields.Length >= 5 && filename.EndsWith(".whl", StringComparison.OrdinalIgnoreCase)
            && ComfyDependencyResolver.NormalizeName(fields[0]) == ComfyDependencyResolver.NormalizeName(name)
            && (version.Length == 0 || fields[1].Equals(version, StringComparison.OrdinalIgnoreCase))
            && (filename.EndsWith("-any.whl", StringComparison.OrdinalIgnoreCase) || filename.EndsWith("-win_amd64.whl", StringComparison.OrdinalIgnoreCase));
    }

    private static string? StableUrl(string value, string? filename = null)
    {
        try
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed)) return null;
            var uri = Safety.Https(new UriBuilder(parsed) { Fragment = "" }.Uri.AbsoluteUri);
            if (filename is not null && Uri.UnescapeDataString(uri.AbsolutePath.Split('/').Last()) != filename) return null;
            var text = new UriBuilder(uri) { Fragment = "" }.Uri.AbsoluteUri;
            // Reuse the durable task policy for credentials and signed query strings.
            DownloadTaskIdentity.ValidatePayload(JsonSerializer.SerializeToElement(text));
            return text;
        }
        catch (Exception ex) when (ex is InvalidDataException or UriFormatException or ArgumentException) { return null; }
    }

    private static async Task<Link[]> ReadIndex(ProbeSession session, string index, string name)
    {
        var url = index.TrimEnd('/') + "/" + Uri.EscapeDataString(ComfyDependencyResolver.NormalizeName(name)) + "/";
        var bytes = await session.Read(url, SimpleAccept);
        var text = Encoding.UTF8.GetString(bytes);
        if (text.TrimStart().StartsWith('{'))
        {
            using var document = JsonDocument.Parse(text);
            if (!document.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) return [];
            return files.EnumerateArray().Select(file => JsonLink(file, url)).Where(link => link is not null).Cast<Link>().ToArray();
        }
        var links = new List<Link>();
        foreach (Match anchor in Anchor.Matches(text))
        {
            var attributes = Attribute.Matches(anchor.Groups["attrs"].Value).Cast<Match>().GroupBy(m => m.Groups["key"].Value, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => WebUtility.HtmlDecode(group.First().Groups["value"].Value), StringComparer.OrdinalIgnoreCase);
            if (!attributes.TryGetValue("href", out var href) || !Uri.TryCreate(new Uri(url), href, out var uri)) continue;
            var filename = Uri.UnescapeDataString(uri.AbsolutePath.Split('/').Last());
            var stable = StableUrl(uri.AbsoluteUri, filename);
            if (stable is null) continue;
            var hash = Regex.Match(uri.Fragment, @"(?:^#|&)sha256=([a-fA-F0-9]{64})(?:&|$)").Groups[1].Value;
            var metadata = attributes.GetValueOrDefault("data-core-metadata", attributes.GetValueOrDefault("data-dist-info-metadata", ""));
            links.Add(new(filename, stable, hash, 0, metadata.Length > 0 && metadata != "false",
                Regex.Match(metadata, @"^sha256=([a-fA-F0-9]{64})$").Groups[1].Value));
        }
        return links.ToArray();
    }

    private static Link? JsonLink(JsonElement file, string index)
    {
        if (file.ValueKind != JsonValueKind.Object
            || !file.TryGetProperty("filename", out var filenameValue) || filenameValue.ValueKind != JsonValueKind.String
            || !file.TryGetProperty("url", out var urlValue) || urlValue.ValueKind != JsonValueKind.String) return null;
        var filename = filenameValue.GetString()!;
        if (!Uri.TryCreate(new Uri(index), urlValue.GetString(), out var uri) || StableUrl(uri.AbsoluteUri, filename) is not { } url) return null;
        var hash = file.TryGetProperty("hashes", out var hashes) && hashes.ValueKind == JsonValueKind.Object
            && hashes.TryGetProperty("sha256", out var sha) && sha.ValueKind == JsonValueKind.String ? sha.GetString()! : "";
        var size = file.TryGetProperty("size", out var length) && length.ValueKind == JsonValueKind.Number
            && length.TryGetInt64(out var measured) && measured > 0 ? measured : 0;
        var hasMetadata = file.TryGetProperty("core-metadata", out var metadata) || file.TryGetProperty("dist-info-metadata", out metadata);
        var metadataHash = hasMetadata && metadata.ValueKind == JsonValueKind.Object && metadata.TryGetProperty("sha256", out var metadataSha)
            && metadataSha.ValueKind == JsonValueKind.String ? metadataSha.GetString()! : "";
        return new(filename, url, hash, size, hasMetadata && metadata.ValueKind is JsonValueKind.True or JsonValueKind.Object, metadataHash);
    }

    private static async Task<long> ReadSize(ProbeSession session, string url)
    {
        using var response = await session.Client.OpenAsync(url, 0, 0, null, session.Token);
        SafeHttpSourceClient.EnsureSuccess(response.Message);
        var content = response.Message.Content;
        if (content.Headers.ContentType?.MediaType is "text/html" or "application/json") return 0;
        if (response.Message.StatusCode == HttpStatusCode.PartialContent)
        {
            var range = content.Headers.ContentRange;
            return range?.Unit == "bytes" && range.From == 0 && range.To == 0 && range.Length > 0 ? range.Length.Value : 0;
        }
        return response.Message.StatusCode == HttpStatusCode.OK ? content.Headers.ContentLength ?? 0 : 0;
    }

    private static bool Recoverable(Exception ex, CancellationToken ct) => !ct.IsCancellationRequested
        && ex is HttpRequestException or IOException or InvalidDataException or OperationCanceledException or JsonException or RegexMatchTimeoutException;

    private static SocketsHttpHandler CreateProbeHandler(NetworkPolicySnapshot network)
    {
        // Keep the shared network validation/proxy policy, but never drain an unread
        // oversized body after a bounded metadata probe rejects it.
        using var policy = network.CreateHttpHandler();
        return new() { UseProxy = policy.UseProxy, Proxy = policy.Proxy,
            AllowAutoRedirect = policy.AllowAutoRedirect, MaxResponseDrainSize = 0 };
    }

    private sealed class ProbeSession : IDisposable
    {
        private readonly CancellationTokenSource timeout;
        private readonly HttpMessageHandler? handler;
        public SafeHttpSourceClient Client { get; }
        public CancellationToken Token => timeout.Token;
        public int Remaining { get; private set; }
        public ProbeSession(NetworkPolicySnapshot network, DependencyPackageSourceOptions? options, CancellationToken ct)
        {
            options ??= new();
            if (options.ProbeTimeout <= TimeSpan.Zero || options.MaximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options));
            Remaining = Math.Min(2 * 1024 * 1024, options.MaximumBytes);
            timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(options.ProbeTimeout < TimeSpan.FromSeconds(8) ? options.ProbeTimeout : TimeSpan.FromSeconds(8));
            handler = options.HandlerFactory?.Invoke(network);
            var transport = handler ?? ProbeHandlers.GetOrAdd(PoolKey(network), _ => CreateProbeHandler(network));
            Client = new(_ => null, new() { ResponseTimeout = TimeSpan.FromSeconds(8), IdleTimeout = TimeSpan.FromSeconds(8) }, network, transport);
        }
        public async Task<byte[]> Read(string url, string? accept = null)
        {
            if (Remaining <= 0) throw new InvalidDataException("依赖来源预检超过流量预算。");
            using var response = await Client.OpenAsync(url, null, null, null, Token, accept);
            SafeHttpSourceClient.EnsureSuccess(response.Message);
            if (response.Message.Content.Headers.ContentLength is { } length && length > Remaining)
                throw new InvalidDataException("依赖来源元数据超过预检流量预算。");
            await using var input = await response.Message.Content.ReadAsStreamAsync(Token);
            using var output = new MemoryStream();
            var buffer = new byte[Math.Min(16 * 1024, Remaining)];
            while (Remaining > 0)
            {
                var count = await Client.ReadAsync(input, buffer.AsMemory(0, Math.Min(buffer.Length, Remaining)), Token);
                if (count == 0) return output.ToArray();
                Remaining -= count;
                output.Write(buffer, 0, count);
            }
            if (response.Message.Content.Headers.ContentLength == output.Length) return output.ToArray();
            throw new InvalidDataException("依赖来源元数据耗尽预检流量预算。");
        }
        public async Task<int> Sample(string url, int count, long size)
        {
            if (count <= 0) return 0;
            using var response = await Client.OpenAsync(url, 0, count - 1, null, Token);
            SafeHttpSourceClient.ValidateRange(response.Message, 0, count - 1, size, true);
            await using var input = await response.Message.Content.ReadAsStreamAsync(Token);
            var buffer = new byte[Math.Min(16 * 1024, count)];
            var read = 0;
            while (read < count)
            {
                var received = await Client.ReadAsync(input, buffer.AsMemory(0, Math.Min(buffer.Length, count - read)), Token);
                if (received == 0) break;
                read += received;
                Remaining -= received;
            }
            return read;
        }
        public void Dispose() { Client.Dispose(); handler?.Dispose(); timeout.Dispose(); }
    }
}
