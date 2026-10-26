using Anima.Launcher.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

/// <summary>Explicit opt-in, real socket/native-engine acceptance; no public network or installed models are used.</summary>
public static class DownloadPerformanceTests
{
    public static async Task<int> Run(string work)
    {
        var directory = Path.Combine(work, "download-performance"); Directory.CreateDirectory(directory);
        var aria2 = await BundledTools.EnsureAsync("aria2", Path.Combine(directory, "runtime"), default);
        var options = new DownloadEngineOptions(aria2) { PollInterval = TimeSpan.FromMilliseconds(100), MinimumAria2Bytes = 0 };
        const long limitedSize = 256L * 1024 * 1024, unlimitedSize = 512L * 1024 * 1024;
        var limitedSha = Hash(limitedSize); var unlimitedSha = Hash(unlimitedSize);
        await using var server = await FixtureServer.StartAsync(limitedSize, unlimitedSize);
        var measures = new List<Measurement>(); var passed = 0;
        void Check(bool condition, string label) { if (!condition) throw new Exception("FAIL: " + label); passed++; Console.WriteLine("PASS: " + label); }
        DownloadRequest Request(string name, bool limited) => new(name, ["https://performance.invalid/" + (limited ? "limited/" : "unlimited/") + name],
            Path.Combine(directory, name), limited ? limitedSize : unlimitedSize, limited ? limitedSha : unlimitedSha) { Network = new("direct") };
        async Task<Measurement> Managed(string name, bool limited, bool native, int count = 1)
        {
            server.Reset(); using var memory = new MemorySampler(aria2); var clock = Stopwatch.StartNew();
            using var handler = new ForwardToFixture(server.Address);
            using var downloads = new Downloads(handler: handler, engineOptions: native ? options : new());
            var requests = Enumerable.Range(0, count).Select(index => Request(name + "-" + index, limited)).ToArray();
            await Task.WhenAll(requests.Select(request => downloads.FetchDirectAsync(request, null, default)));
            clock.Stop(); memory.Finish();
            foreach (var request in requests) Check(await Downloads.Verify(request.Destination, request.Size, request.Sha256, default), name + " SHA-256 and publication " + Path.GetFileName(request.Destination));
            var result = memory.Measure(name, requests.Sum(request => request.Size), clock.Elapsed.TotalSeconds, server.TransferSeconds, server.PeakStreams);
            measures.Add(result); Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result)); return result;
        }
        var single = await Managed("http-single-limited", true, false);
        var parallel = await Managed("aria2-bridge-limited", true, true);
        var speedup = single.EndToEndSeconds / parallel.EndToEndSeconds;
        Check(parallel.PeakUpstreamStreams is > 1 and <= 4, "native per-file upstream concurrency is bounded at four");

        server.Reset(); using (var memory = new MemorySampler(aria2))
        {
            var clock = Stopwatch.StartNew(); var file = Path.Combine(directory, "aria2-direct-unlimited");
            var start = new ProcessStartInfo(aria2) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "--no-conf=true", "--no-netrc=true", "--split=4", "--max-connection-per-server=4", "--min-split-size=32M",
                "--piece-length=1M", "--file-allocation=none", "--allow-overwrite=false", "--auto-file-renaming=false", "--max-tries=1",
                "--console-log-level=error", "--summary-interval=0", "--enable-dht=false", "--enable-dht6=false", "--enable-peer-exchange=false", "--bt-enable-lpd=false",
                "--all-proxy=", "--http-proxy=", "--https-proxy=", "--no-proxy=*", "--stop-with-process=" + Environment.ProcessId,
                "--dir=" + directory, "--out=" + Path.GetFileName(file), new Uri(server.Address, "unlimited/direct").AbsoluteUri }) start.ArgumentList.Add(arg);
            foreach (var variable in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" }) start.Environment[variable] = "";
            using var process = Process.Start(start) ?? throw new IOException("aria2 direct baseline failed to start");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(); await stdout; await stderr; clock.Stop(); memory.Finish();
            Check(process.ExitCode == 0 && await Downloads.Verify(file, unlimitedSize, unlimitedSha, default), "direct native localhost baseline completed with the same SHA-256");
            var result = memory.Measure("aria2-direct-unlimited", unlimitedSize, clock.Elapsed.TotalSeconds, server.TransferSeconds, server.PeakStreams);
            measures.Add(result); Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
        }
        var unlimited = await Managed("aria2-bridge-unlimited", false, true);
        var concurrent = await Managed("aria2-two-files-limited", true, true, 2);
        Check(concurrent.PeakUpstreamStreams is > 4 and <= 8, "two native files use at most eight upstream streams in total");
        var direct = measures.Single(item => item.Name == "aria2-direct-unlimited");
        var report = new
        {
            Timestamp = DateTimeOffset.UtcNow, Platform = Environment.OSVersion.ToString(), Runtime = Environment.Version.ToString(),
            LimitedBytesPerSecondPerConnection = 8L * 1024 * 1024, LimitedEndToEndSpeedup = speedup,
            UnlimitedEndToEndOverheadSeconds = unlimited.EndToEndSeconds - direct.EndToEndSeconds,
            UnlimitedSourceTransferOverheadSeconds = unlimited.SourceTransferSeconds - direct.SourceTransferSeconds,
            Notes = "Real Kestrel upstream sockets; test-only handler maps fixture HTTPS URI to loopback. Timing includes native startup/shutdown, SHA and publication. Native direct baseline is a test-only HTTP loopback command; production engine always uses capability bridge. Memory samples include the fixture server in this test host; native peak sums only processes whose executable equals this isolated fixture engine path.",
            Measurements = measures
        };
        var path = Path.Combine(directory, "performance-report.json"); JsonFile.Write(path, report);
        Console.WriteLine("Performance report: " + path);
        Check(speedup >= 2, "four-connection engine is at least twice as fast as single connection on a per-connection-limited source (" + speedup.ToString("F2") + "x)");
        return passed;
    }

    private sealed record Measurement(string Name, long Bytes, double EndToEndSeconds, double SourceTransferSeconds, int PeakUpstreamStreams,
        long HostPrivateBytesBaseline, long HostPrivateBytesPeak, long HostWorkingSetBaseline, long HostWorkingSetPeak, long NativeWorkingSetPeak);
    private sealed class MemorySampler : IDisposable
    {
        private readonly Process host = Process.GetCurrentProcess(); private readonly Timer timer; private readonly string executable;
        private readonly long baselinePrivate, baselineWorking; private long peakPrivate, peakWorking, peakNative;
        public MemorySampler(string executable) { this.executable = Path.GetFullPath(executable); host.Refresh(); baselinePrivate = peakPrivate = host.PrivateMemorySize64; baselineWorking = peakWorking = host.WorkingSet64; timer = new Timer(_ => Sample(), null, 0, 50); }
        private void Sample()
        {
            lock (host)
            {
                try { host.Refresh(); peakPrivate = Math.Max(peakPrivate, host.PrivateMemorySize64); peakWorking = Math.Max(peakWorking, host.WorkingSet64); } catch { }
                long native = 0;
                foreach (var process in Process.GetProcessesByName("aria2c")) { using (process) { try { if (string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)) native += process.WorkingSet64; } catch { } } }
                peakNative = Math.Max(peakNative, native);
            }
        }
        public void Finish() { timer.Change(Timeout.Infinite, Timeout.Infinite); Sample(); }
        public Measurement Measure(string name, long size, double seconds, double transferSeconds, int streams)
        { lock (host) return new(name, size, seconds, transferSeconds, streams, baselinePrivate, peakPrivate, baselineWorking, peakWorking, peakNative); }
        public void Dispose() { timer.Dispose(); host.Dispose(); }
    }
    private sealed class ForwardToFixture(Uri address) : HttpMessageHandler
    {
        private readonly HttpClient client = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var forwarded = new HttpRequestMessage(request.Method, new Uri(address, request.RequestUri!.PathAndQuery));
            foreach (var header in request.Headers) if (header.Key != "Host") forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
            return await client.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        protected override void Dispose(bool disposing) { if (disposing) client.Dispose(); base.Dispose(disposing); }
    }
    private sealed class FixtureServer : IAsyncDisposable
    {
        private readonly WebApplication app; private readonly long limitedSize, unlimitedSize;
        private int active, peak; private long first, last;
        public Uri Address { get; }
        public int PeakStreams => peak;
        public double TransferSeconds => first == 0 ? 0 : (last - first) / (double)Stopwatch.Frequency;
        private FixtureServer(WebApplication app, Uri address, long limitedSize, long unlimitedSize)
        { this.app = app; Address = address; this.limitedSize = limitedSize; this.unlimitedSize = unlimitedSize; }
        public static async Task<FixtureServer> StartAsync(long limitedSize, long unlimitedSize)
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] }); builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var app = builder.Build(); FixtureServer? fixture = null; app.Run(context => fixture!.Handle(context)); await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            fixture = new(app, new Uri(address + "/"), limitedSize, unlimitedSize); return fixture;
        }
        public void Reset() { if (active != 0) throw new InvalidOperationException("Fixture still has active streams"); peak = 0; first = last = 0; }
        private async Task Handle(HttpContext context)
        {
            var limited = context.Request.Path.StartsWithSegments("/limited"); var size = limited ? limitedSize : unlimitedSize;
            var from = 0L; var to = size - 1;
            if (context.Request.Headers.TryGetValue("Range", out var value))
            {
                if (!RangeHeaderValue.TryParse(value, out var range) || range.Ranges.Count != 1) { context.Response.StatusCode = 416; return; }
                var part = range.Ranges.Single(); from = part.From ?? 0; to = Math.Min(part.To ?? size - 1, size - 1);
                if (from > to) { context.Response.StatusCode = 416; return; }
                context.Response.StatusCode = 206; context.Response.Headers.ContentRange = $"bytes {from}-{to}/{size}";
            }
            context.Response.ContentLength = to - from + 1; context.Response.Headers.ETag = "\"benchmark-v1\""; context.Response.Headers.AcceptRanges = "bytes";
            if (context.Request.Method == "HEAD") return;
            var count = Interlocked.Increment(ref active); int old;
            do { old = peak; if (count <= old) break; } while (Interlocked.CompareExchange(ref peak, count, old) != old);
            Interlocked.CompareExchange(ref first, Stopwatch.GetTimestamp(), 0);
            var buffer = new byte[128 * 1024]; var clock = Stopwatch.StartNew(); long written = 0;
            try
            {
                while (written < to - from + 1)
                {
                    var length = (int)Math.Min(buffer.Length, to - from + 1 - written);
                    if (limited && to - from > 1)
                    {
                        var delay = TimeSpan.FromSeconds((written + length) / (8d * 1024 * 1024)) - clock.Elapsed;
                        if (delay > TimeSpan.Zero) await Task.Delay(delay, context.RequestAborted);
                    }
                    await context.Response.Body.WriteAsync(buffer.AsMemory(0, length), context.RequestAborted); written += length;
                }
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            catch (IOException) { }
            finally { Interlocked.Exchange(ref last, Stopwatch.GetTimestamp()); Interlocked.Decrement(ref active); }
        }
        public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); }
    }
    private static string Hash(long size)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[128 * 1024];
        for (long offset = 0; offset < size; offset += buffer.Length) digest.AppendData(buffer, 0, (int)Math.Min(buffer.Length, size - offset));
        return Convert.ToHexString(digest.GetHashAndReset());
    }
}
