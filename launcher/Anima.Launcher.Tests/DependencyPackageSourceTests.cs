using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Anima.Launcher.Core;

public static class DependencyPackageSourceTests
{
    public static async Task<int> Run(string work)
    {
        var previous = Environment.GetEnvironmentVariable("ANIMA_LAUNCHER_DIRECT_NETWORK");
        Environment.SetEnvironmentVariable("ANIMA_LAUNCHER_DIRECT_NETWORK", null);
        try { return await RunIsolated(work); }
        finally { Environment.SetEnvironmentVariable("ANIMA_LAUNCHER_DIRECT_NETWORK", previous); }
    }

    private static async Task<int> RunIsolated(string work)
    {
        var passed = 0;
        void Check(bool result, string description)
        { if (!result) throw new Exception("FAIL: " + description); passed++; Console.WriteLine("PASS: " + description); }
        var createProbe = typeof(DependencyPackageSources).GetMethod("CreateProbeHandler", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        using (var direct = (SocketsHttpHandler)createProbe.Invoke(null, [new NetworkPolicySnapshot("direct")])!)
            Check(!direct.UseProxy && !direct.AllowAutoRedirect && direct.MaxResponseDrainSize == 0,
                "bounded production probes never drain rejected response bodies and preserve direct policy");
        using (var proxied = (SocketsHttpHandler)createProbe.Invoke(null, [new NetworkPolicySnapshot("custom", "http://127.0.0.1:18761")])!)
            Check(proxied.UseProxy && proxied.Proxy?.GetProxy(new Uri("https://pypi.org"))?.Port == 18761
                && !proxied.AllowAutoRedirect && proxied.MaxResponseDrainSize == 0,
                "bounded production probes retain the configured proxy and safe redirect policy");
        var sha = new string('a', 64);
        const string filename = "real_package-1.2.3-py3-none-any.whl";
        var config = new LauncherConfig { SourceId = "china", NetworkMode = "direct" };
        var requests = new ConcurrentQueue<string>();
        var observed = new ConcurrentQueue<NetworkPolicySnapshot>();
        var options = Options((request, _) =>
        {
            var uri = request.RequestUri!;
            requests.Enqueue(uri.AbsoluteUri);
            if (uri.AbsolutePath.Contains("/simple/"))
            {
                if (uri.Host == "mirrors.ustc.edu.cn") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                return Task.FromResult(Index(filename, "https://" + uri.Host + "/packages/" + filename, sha,
                    metadata: uri.Host == "pypi.org"));
            }
            if (uri.AbsolutePath.EndsWith(".metadata")) return Task.FromResult(Text("Name: real-package\nVersion: 1.2.3\n"));
            return Task.FromResult(Range(request, 4096));
        }, network => observed.Enqueue(network));
        var indexes = await DependencyPackageSources.SelectIndexesAsync(config,
            ["# comment\nreal-package==1.2.3\nsecond-package==2.0\n--extra-index-url https://unused.test/simple\ngit+https://github.com/owner/repo"], default, null, options);
        Check(indexes[0] == DownloadSources.OfficialPipIndex && indexes[^1] == DownloadSources.ChinaPipIndex,
            "complete source with real metadata outranks a missing package and a wheel-only index");
        Check(requests.Where(url => url.Contains("/simple/")).All(url => url.EndsWith("/real-package/")),
            "probe uses one actual top-level package to leave bounded time for metadata and sampling");
        Check(requests.Any(url => url.EndsWith(".metadata")) && observed.All(network => network.Mode == "direct"),
            "metadata endpoint is really fetched and the requested direct policy reaches the HTTP factory");

        var custom = config.Clone(); custom.SourceId = "custom"; custom.PipIndex = "https://mirrors.ustc.edu.cn/pypi/simple";
        indexes = await DependencyPackageSources.SelectIndexesAsync(custom, ["real-package==1.2.3"], default, null, options);
        Check(indexes[0] == custom.PipIndex, "an explicit custom index retains priority even when its probe is unavailable");

        var speedOptions = Options(async (request, token) =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.Contains("/simple/")) return Index(filename, "https://" + uri.Host + "/packages/" + filename, sha, true);
            if (uri.AbsolutePath.EndsWith(".metadata")) return Text("Name: real-package\nVersion: 1.2.3\n");
            if (request.Headers.Range!.Ranges.Single().To > 0)
                await Task.Delay(uri.Host == "pypi.tuna.tsinghua.edu.cn" ? 2 : 70, token);
            return Range(request, 4096);
        });
        indexes = await DependencyPackageSources.SelectIndexesAsync(config, ["real-package==1.2.3"], default, null, speedOptions);
        Check(indexes[0] == "https://pypi.tuna.tsinghua.edu.cn/simple", "small range throughput orders equally complete metadata-capable sources");

        var package = new DependencyPackage { Name = "real-package", Version = "1.2.3", FileName = filename, Sha256 = sha,
            Url = "https://files.pythonhosted.org/packages/" + filename };
        var speedResolved = await DependencyPackageSources.ResolveAsync(package with { Size = 4096 }, config, default, speedOptions);
        Check(new Uri(speedResolved.Sources[0]).Host == "pypi.tuna.tsinghua.edu.cn", "file sources reuse measured range throughput independently of resolver metadata ranking");
        var slowSampleOptions = Options(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/simple/")) return Index(filename, "https://" + request.RequestUri.Host + "/packages/" + filename, sha, true, 4096);
            if (request.RequestUri.AbsolutePath.EndsWith(".metadata")) return Text("Name: real-package\nVersion: 1.2.3\n");
            if (request.Headers.Range!.Ranges.Single().To > 0) await Task.Delay(10000, token);
            return Range(request, 4096);
        }) with { ProbeTimeout = TimeSpan.FromMilliseconds(40) };
        await DependencyPackageSources.SelectIndexesAsync(config, ["real-package==1.2.3"], default, null, slowSampleOptions);
        var slowResolved = await DependencyPackageSources.ResolveAsync(package with { Size = 4096 }, config, default, slowSampleOptions);
        Check(slowResolved.Sources.Length == 4, "a slow sample does not suppress a source whose real artifact and metadata already succeeded");
        var resolveOptions = Options((request, _) =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.Contains("/simple/"))
            {
                var hash = uri.Host == "mirrors.ustc.edu.cn" ? new string('b', 64) : sha;
                var size = uri.Host == "pypi.tuna.tsinghua.edu.cn" ? 8192 : 4096;
                return Task.FromResult(Index(filename, "https://" + uri.Host + "/packages/" + filename, hash, false, size));
            }
            return Task.FromResult(Range(request, 4096));
        });
        var resolved = await DependencyPackageSources.ResolveAsync(package, config, default, resolveOptions);
        Check(resolved.Size == 4096 && resolved.Sha256 == sha && resolved.Name == package.Name && resolved.Version == package.Version,
            "report SHA identity is retained and a one-byte range fills its missing size");
        Check(resolved.Sources.SequenceEqual(["https://pypi.org/packages/" + filename, package.Url]),
            "sources with mismatched hashes or sizes are excluded instead of weakening identity");
        var payloadRequests = 0;
        var sizedIndexOptions = Options((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/simple/"))
                return Task.FromResult(Index(filename, "https://" + request.RequestUri.Host + "/packages/" + filename, sha, false, 4096));
            Interlocked.Increment(ref payloadRequests);
            return Task.FromResult(Range(request, 4096));
        });
        var sized = await DependencyPackageSources.ResolveAsync(package, config, default, sizedIndexOptions);
        Check(sized.Size == 4096 && sized.Sources.Length == 4 && payloadRequests == 0,
            "exact-hash Simple JSON size avoids every redundant payload range probe");
        var injectedHandlers = new ConcurrentBag<HttpMessageHandler>();
        var independentOptions = new DependencyPackageSourceOptions { HandlerFactory = _ =>
        {
            var handler = new Handler((request, _) => Task.FromResult(Index(filename,
                "https://" + request.RequestUri!.Host + "/packages/" + filename, sha, false, 4096)));
            injectedHandlers.Add(handler); return handler;
        } };
        await DependencyPackageSources.ResolveAsync(package, config, default, independentOptions);
        await DependencyPackageSources.ResolveAsync(package, config, default, independentOptions);
        Check(injectedHandlers.Count == 6 && injectedHandlers.Distinct(ReferenceEqualityComparer.Instance).Count() == 6,
            "injected test handlers stay operation-scoped and never enter the production connection pool");
        var refreshed = await DependencyPackageSources.RefreshSourcesAsync(package.Name, package.Version, filename, sha, 4096, config, default, resolveOptions);
        Check(refreshed.SequenceEqual(["https://pypi.org/packages/" + filename]), "paused source refresh returns only exact reviewed file identities");

        const string archive = "real_package-1.2.3.tar.gz";
        var archiveOptions = Options((request, _) => Task.FromResult(Index(archive,
            "https://" + request.RequestUri!.Host + "/packages/" + archive,
            request.RequestUri.Host == "mirrors.ustc.edu.cn" ? new string('b', 64) : sha, false,
            request.RequestUri.Host == "pypi.tuna.tsinghua.edu.cn" ? 8192 : 4096)));
        refreshed = await DependencyPackageSources.RefreshSourcesAsync(package.Name, package.Version, archive, sha, 4096, config, default, archiveOptions);
        Check(refreshed.SequenceEqual(["https://pypi.org/packages/" + archive]), "tar.gz refresh retains exact archive name, version, digest and size");

        var mirrorSizeRequests = 0;
        var htmlOptions = Options((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/simple/"))
                return Task.FromResult(Text($"<html><a href=\"../../packages/{filename}#sha256={sha}\" data-core-metadata=\"true\">{filename}</a></html>", "text/html"));
            Interlocked.Increment(ref mirrorSizeRequests);
            return Task.FromResult(Range(request, 4096));
        });
        var htmlConfig = config.Clone(); htmlConfig.SourceId = "official";
        refreshed = await DependencyPackageSources.RefreshSourcesAsync(package.Name, package.Version, filename, sha, 4096, htmlConfig, default, htmlOptions);
        Check(refreshed.SequenceEqual(["https://pypi.org/packages/" + filename]), "HTML simple indexes resolve relative exact-hash wheel links");
        Check(mirrorSizeRequests == 0, "verified exact-hash mirror links reuse the known size without redundant range requests");

        var deniedOptions = Options((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
            { Headers = { Location = new Uri("http://unsafe.test/simple/real-package/") } }));
        Check((await DependencyPackageSources.RefreshSourcesAsync(package.Name, package.Version, filename, sha, 4096, htmlConfig, default, deniedOptions)).Length == 0,
            "package metadata cannot bypass the common unsafe-redirect rejection");

        var unknownOptions = Options((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new UnknownLengthStream()) }));
        var unknown = await DependencyPackageSources.ResolveAsync(package, htmlConfig, default, unknownOptions);
        Check(unknown == package, "unknown length and unavailable identity remain on the compatibility path unchanged");
        var signed = package with { Url = package.Url + "?token=do-not-persist" };
        var noSources = Options((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        Check(await DependencyPackageSources.ResolveAsync(signed, htmlConfig, default, noSources) == signed,
            "signed source addresses never enter a generated durable download list");

        var proxy = config.Clone(); proxy.NetworkMode = "custom"; proxy.ProxyAddress = "http://127.0.0.1:7890";
        var policies = new ConcurrentQueue<NetworkPolicySnapshot>();
        var proxyOptions = Options((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)), policies.Enqueue);
        await DependencyPackageSources.ResolveAsync(package, proxy, default, proxyOptions);
        Check(policies.Count > 0 && policies.All(value => value.Mode == "custom" && value.ProxyAddress == proxy.ProxyAddress),
            "size and index requests all carry the configured proxy snapshot");

        var timeoutOptions = Options(async (_, token) => { await Task.Delay(10000, token); return Text("{}"); })
            with { ProbeTimeout = TimeSpan.FromMilliseconds(40) };
        var timer = System.Diagnostics.Stopwatch.StartNew();
        indexes = await DependencyPackageSources.SelectIndexesAsync(config, ["real-package==1.2.3"], default, null, timeoutOptions);
        Check(timer.Elapsed < TimeSpan.FromSeconds(2) && indexes.SequenceEqual(DownloadSources.PipIndexes(config)),
            "per-source deadlines bound stalled probes and preserve fallback order");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            try { await DependencyPackageSources.ResolveAsync(package, config, canceled.Token, options); throw new Exception("Expected cancellation"); }
            catch (OperationCanceledException) { Check(true, "caller cancellation propagates instead of being treated as source failure"); }
        }

        var byteBudgetOptions = Options((_, _) => Task.FromResult(Text(new string('x', 5000)))) with { MaximumBytes = 128 };
        indexes = await DependencyPackageSources.SelectIndexesAsync(config, ["real-package==1.2.3"], default, null, byteBudgetOptions);
        Check(indexes.SequenceEqual(DownloadSources.PipIndexes(config)), "oversized index responses are rejected within the source byte budget");

        var active = 0; var peak = 0;
        var concurrencyOptions = Options(async (_, token) =>
        {
            var count = Interlocked.Increment(ref active);
            int seen;
            do { seen = Volatile.Read(ref peak); if (count <= seen) break; } while (Interlocked.CompareExchange(ref peak, count, seen) != seen);
            try { await Task.Delay(25, token); return new(HttpStatusCode.NotFound); }
            finally { Interlocked.Decrement(ref active); }
        });
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => DependencyPackageSources.ResolveAsync(package with { Url = "" }, config, default, concurrencyOptions)));
        Check(peak > 1 && peak <= 8, "dependency probes share the existing global eight-connection limit");

        var failedProbes = 0;
        var batchOptions = Options(async (request, token) =>
        {
            if (request.RequestUri!.Host == "mirrors.ustc.edu.cn")
            {
                Interlocked.Increment(ref failedProbes);
                await Task.Delay(10000, token);
            }
            return request.RequestUri!.AbsolutePath.Contains("/simple/")
                ? Index(filename, "https://" + request.RequestUri.Host + "/packages/" + filename, sha, false, 4096)
                : Range(request, 4096);
        }) with { ProbeTimeout = TimeSpan.FromMilliseconds(80) };
        await DependencyPackageSources.SelectIndexesAsync(config, ["real-package==1.2.3"], default, null, batchOptions);
        timer.Restart();
        var batch = await Task.WhenAll(Enumerable.Range(0, 144).Select(_ => DependencyPackageSources.ResolveAsync(
            package with { Size = 4096 }, config, default, batchOptions)));
        Check(failedProbes == 1 && timer.Elapsed < TimeSpan.FromSeconds(2) && batch.All(item => item.Sources.Length == 3),
            "144 package preparation reuses source selection's one stalled probe and continues verified live sources");
        await DependencyPackageSources.ResolveAsync(package with { Size = 4096 }, proxy, default, batchOptions);
        Check(failedProbes == 2, "an explicit network change creates an independent source health key");
        await DependencyPackageSources.RefreshSourcesAsync(package.Name, package.Version, filename, sha, 4096, config, default, batchOptions);
        Check(failedProbes == 3, "explicit paused source refresh retries hosts suppressed for ordinary package preparation");
        await Task.WhenAll(Enumerable.Range(0, 144).Select(_ => DependencyPackageSources.RefreshSourcesAsync(
            package.Name, package.Version, filename, sha, 4096, config, default, batchOptions)));
        Check(failedProbes == 3, "a paused owner's 144-file source refresh shares one failed host probe for its wave");
        return passed;
    }

    private static DependencyPackageSourceOptions Options(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
        Action<NetworkPolicySnapshot>? observed = null) => new()
        { HandlerFactory = network => { observed?.Invoke(network); return new Handler(respond); } };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request, ct); }
    private static HttpResponseMessage Text(string text, string type = "text/plain") => new(HttpStatusCode.OK)
    { Content = new StringContent(text, Encoding.UTF8, type) };
    private static HttpResponseMessage Index(string filename, string url, string sha, bool metadata, long size = 0) => Text(JsonSerializer.Serialize(new
    {
        files = new[] { new Dictionary<string, object> { ["filename"] = filename, ["url"] = url,
            ["hashes"] = new { sha256 = sha }, ["core-metadata"] = metadata, ["size"] = size } }
    }), "application/vnd.pypi.simple.v1+json");
    private static HttpResponseMessage Range(HttpRequestMessage request, int size)
    {
        var requested = request.Headers.Range!.Ranges.Single();
        var from = requested.From!.Value; var to = Math.Min(size - 1, requested.To ?? size - 1);
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(new byte[to - from + 1]) };
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, size);
        return response;
    }
    private sealed class UnknownLengthStream : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
