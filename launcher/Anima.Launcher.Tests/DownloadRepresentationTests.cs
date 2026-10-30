using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Anima.Launcher.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal static class DownloadRepresentationTests
{
    public static async Task<int> Run(string work)
    {
        var root = Path.Combine(work, "download-representation"); Directory.CreateDirectory(root);
        var count = 0;
        void Check(bool valid, string name) { if (!valid) throw new Exception("FAIL: " + name); count++; Console.WriteLine("PASS: " + name); }
        var bytes = new byte[16 * 1024 * 1024];
        for (var index = 0; index < bytes.Length; index++) bytes[index] = (byte)(index % 251);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var aria2 = await BundledTools.EnsureAsync("aria2", Path.Combine(root, "runtime"));
        var options = new DownloadEngineOptions(aria2) { PollInterval = TimeSpan.FromMilliseconds(60) };
        var policy = new DownloadPolicy { RetryDelay = TimeSpan.FromMilliseconds(10), MinimumBytesPerSecond = 0 };
        await using var server = await RangeServer.Start(bytes);
        DownloadRequest Request(string name, string[]? urls = null)
        {
            var filename = name + "-1.0-py3-none-any.whl";
            return new(name, urls ?? ["https://source.invalid/" + name], Path.Combine(root, filename), bytes.Length, sha)
            { Network = new("direct"), TransferProfile = DownloadTransferProfile.Dependency,
                Dependency = new(name, "1.0", filename, sha, bytes.Length) };
        }
        void Seed(DownloadRequest request)
        {
            File.WriteAllBytes(request.Destination + ".part", bytes[..(1024 * 1024)]);
            JsonFile.Write(request.Destination + ".part.json", new ResumeInfo(sha, bytes.Length, request.Urls[0], null));
        }
        Dictionary<string, string> Snapshot(DownloadRequest request) => new[] { DownloadCheckpoint.AriaData(request.Destination),
            DownloadCheckpoint.AriaControl(request.Destination), DownloadCheckpoint.AriaState(request.Destination) }
            .ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        bool Unchanged(Dictionary<string, string>? snapshot) => snapshot is { Count: 3 }
            && snapshot.All(pair => File.Exists(pair.Key) && pair.Value == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pair.Key))));
        async Task<Exception> Failure(Func<Task> action)
        { try { await action(); } catch (Exception error) { return error; } throw new Exception("Expected rejection."); }

        var success = Request("success"); Seed(success); var sawNative = false; var fallback = 0; var hadCheckpoint = false;
        using (var handler = new ForwardHandler(server.Address))
        using (var downloads = new Downloads(handler: handler, policy: policy, engineOptions: options))
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35)))
        {
            await downloads.FetchDirectAsync(success, new Inline(progress =>
            {
                sawNative |= progress.Engine == "aria2";
                if (progress.FailureCode != "source_representation_changed") return;
                fallback++; hadCheckpoint = DownloadCheckpoint.RetainedBytes(success.Destination, success.Size, success.Sha256) > 0;
            }), timeout.Token);
        }
        Check(sawNative && fallback == 1 && hadCheckpoint && server.FullGets("success") == 1 && server.RangeGets("success") > 1,
            "real aria2 detects inconsistent strong ETags and performs exactly one independent full GET");
        Check(await Downloads.Verify(success.Destination, success.Size, sha, default) && !Downloads.PartialFiles(success.Destination).Any(File.Exists),
            "fixed size and SHA pass before publication and obsolete sparse checkpoints are removed");
        Check(server.FullRequestsWithRange == 0 && server.FullRequestsWithIfRange == 0,
            "compatibility GET inherits neither Range nor If-Range from sparse aria2 chunks");

        var corrupt = Request("corrupt"); Seed(corrupt); Dictionary<string, string>? corruptCheckpoint = null;
        using (var handler = new ForwardHandler(server.Address))
        using (var downloads = new Downloads(handler: handler, policy: policy, engineOptions: options))
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35)))
        {
            var error = await Failure(() => downloads.FetchDirectAsync(corrupt, new Inline(progress =>
            { if (progress.FailureCode == "source_representation_changed") corruptCheckpoint = Snapshot(corrupt); }), timeout.Token));
            Check(error is InvalidDataException && server.FullGets("corrupt") == 1 && !File.Exists(corrupt.Destination),
                "changed content fails the locked SHA and is never published or automatically fetched twice");
        }
        Check(Unchanged(corruptCheckpoint), "failed complete-file verification preserves original aria2 bytes, control and identity verbatim");

        var cancel = Request("cancel"); Seed(cancel); Dictionary<string, string>? cancelCheckpoint = null;
        using (var handler = new ForwardHandler(server.Address))
        using (var downloads = new Downloads(handler: handler, policy: policy, engineOptions: options))
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35)))
        {
            var error = await Failure(() => downloads.FetchDirectAsync(cancel, new Inline(progress =>
            {
                if (progress.FailureCode == "source_representation_changed") cancelCheckpoint = Snapshot(cancel);
                if (progress.Engine == "HttpClient" && progress.Phase == "Downloading" && progress.Received > 0) timeout.Cancel();
            }), timeout.Token));
            Check(error is OperationCanceledException && server.FullGets("cancel") == 1 && !File.Exists(cancel.Destination)
                && File.Exists(cancel.Destination + ".http-restart.part"), "canceling the independent full GET retains cancellation and its separate scratch file");
        }
        Check(Unchanged(cancelCheckpoint), "canceling compatibility download leaves the original sparse checkpoint unchanged");

        var mirror = Request("mirror", ["https://mirror.invalid/mirror", "https://official.invalid/stable"]); Seed(mirror);
        var mirrorFallback = false;
        using (var handler = new ForwardHandler(server.Address))
        using (var downloads = new Downloads(handler: handler, policy: policy, engineOptions: options))
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35)))
        {
            await downloads.FetchDirectAsync(mirror, new Inline(progress => mirrorFallback |= progress.Stage.Contains("切换至")), timeout.Token);
        }
        Check(mirrorFallback && server.FullGets("mirror") == 0 && server.RangeGets("stable") > 0
            && await Downloads.Verify(mirror.Destination, mirror.Size, sha, default),
            "a changed non-final source moves to the recorded next source before considering a full GET");

        var budget = Request("budget"); Seed(budget); Dictionary<string, string>? budgetCheckpoint = null;
        var budgetOptions = options with { DiskSpaceOptions = new()
        {
            // Enough for the 15 MiB still missing from the seeded prefix, but not a separate 16 MiB file.
            LedgerRoot = Path.Combine(root, "budget-ledger"), AvailableBytes = _ => DownloadDiskSpace.SafetyMargin + bytes.Length - 512 * 1024,
            AllocatedBytes = path => File.Exists(path) ? new FileInfo(path).Length : 0
        } };
        using (var handler = new ForwardHandler(server.Address))
        using (var downloads = new Downloads(handler: handler, policy: policy, engineOptions: budgetOptions))
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35)))
        {
            var error = await Failure(() => downloads.FetchDirectAsync(budget, new Inline(progress =>
            { if (progress.FailureCode == "source_representation_changed") budgetCheckpoint = Snapshot(budget); }), timeout.Token));
            Check(error is InsufficientSpaceException && server.FullGets("budget") == 0 && Unchanged(budgetCheckpoint),
                "fresh full GET reserves additional space without spending or replacing retained sparse bytes");
        }

        var unavailable = Request("unavailable"); Seed(unavailable);
        using (var handler = new ForwardHandler(server.Address))
        using (var downloads = new Downloads(handler: handler, policy: policy, engineOptions: options))
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35)))
        {
            var error = await Failure(() => downloads.FetchDirectAsync(unavailable, null, timeout.Token));
            Check(error is HttpRequestException { StatusCode: HttpStatusCode.ServiceUnavailable } && server.FullGets("unavailable") == 1,
                "even a retryable HTTP failure does not repeat the one-shot compatibility GET");
        }
        var malformed = Request("malformed"); Seed(malformed);
        using (var handler = new ForwardHandler(server.Address))
        using (var downloads = new Downloads(handler: handler, policy: policy, engineOptions: options))
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35)))
        {
            var error = await Failure(() => downloads.FetchDirectAsync(malformed, null, timeout.Token));
            Check(error is InvalidDataException && server.FullGets("malformed") == 0 && !File.Exists(malformed.Destination),
                "ordinary malformed range data is not reclassified as an ETag fallback opportunity");
        }
        Check(server.PeakStreams <= 8, "real source HTTP streams remain within the shared eight-connection budget");
        JsonFile.Write(Path.Combine(root, "evidence.json"), new { checks = count, size = bytes.Length, sha256 = sha,
            peakStreams = server.PeakStreams, fullGets = server.FullCounts, rangeGets = server.RangeCounts });
        Console.WriteLine("Download representation fixtures: " + root);
        return count;
    }

    private sealed class Inline(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    { public void Report(DownloadProgress value) => report(value); }
    private sealed class ForwardHandler(Uri address) : HttpMessageHandler
    {
        private readonly HttpClient client = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, MaxResponseDrainSize = 0 })
        { Timeout = Timeout.InfiniteTimeSpan };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var forwarded = new HttpRequestMessage(request.Method, new Uri(address, request.RequestUri!.PathAndQuery));
            foreach (var header in request.Headers) if (header.Key != "Host") forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
            return await client.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        protected override void Dispose(bool disposing) { if (disposing) client.Dispose(); base.Dispose(disposing); }
    }
    private sealed class RangeServer(WebApplication app, Uri address) : IAsyncDisposable
    {
        private int active, peak, fullWithRange, fullWithIfRange;
        public Uri Address { get; } = address;
        public ConcurrentDictionary<string, int> FullCounts { get; } = new();
        public ConcurrentDictionary<string, int> RangeCounts { get; } = new();
        public int FullGets(string key) => FullCounts.GetValueOrDefault(key);
        public int RangeGets(string key) => RangeCounts.GetValueOrDefault(key);
        public int PeakStreams => Volatile.Read(ref peak);
        public int FullRequestsWithRange => Volatile.Read(ref fullWithRange);
        public int FullRequestsWithIfRange => Volatile.Read(ref fullWithIfRange);
        public static async Task<RangeServer> Start(byte[] bytes)
        {
            var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build(); RangeServer? owner = null;
            app.Run(async context =>
            {
                var name = context.Request.Path.Value!.Trim('/');
                var ranged = RangeHeaderValue.TryParse(context.Request.Headers.Range.ToString(), out var range);
                var from = ranged ? range!.Ranges.Single().From ?? 0 : 0;
                var to = ranged ? range!.Ranges.Single().To ?? bytes.Length - 1 : bytes.Length - 1;
                (ranged ? owner!.RangeCounts : owner!.FullCounts).AddOrUpdate(name, 1, (_, value) => value + 1);
                if (!ranged)
                {
                    if (context.Request.Headers.ContainsKey("Range")) Interlocked.Increment(ref owner.fullWithRange);
                    if (context.Request.Headers.ContainsKey("If-Range")) Interlocked.Increment(ref owner.fullWithIfRange);
                    if (name == "unavailable") { context.Response.StatusCode = 503; return; }
                }
                var current = Interlocked.Increment(ref owner.active); int previous;
                do { previous = Volatile.Read(ref owner.peak); if (current <= previous) break; }
                while (Interlocked.CompareExchange(ref owner.peak, current, previous) != previous);
                try
                {
                    context.Response.StatusCode = ranged ? 206 : 200;
                    context.Response.ContentLength = to - from + 1;
                    if (ranged) context.Response.Headers.ContentRange = $"bytes {from}-{to}/{bytes.Length + (name == "malformed" ? 1 : 0)}";
                    context.Response.Headers.ETag = name == "stable" || from < 4 * 1024 * 1024 ? "\"edge-one\"" : "\"edge-two\"";
                    for (var offset = from; offset <= to;)
                    {
                        var length = (int)Math.Min(64 * 1024, to - offset + 1);
                        if (!ranged && name == "corrupt" && offset == 0)
                        {
                            var changed = bytes[..length]; changed[0] ^= 1;
                            await context.Response.Body.WriteAsync(changed, context.RequestAborted);
                        }
                        else await context.Response.Body.WriteAsync(bytes.AsMemory((int)offset, length), context.RequestAborted);
                        await context.Response.Body.FlushAsync(context.RequestAborted); offset += length;
                        if (offset <= to) await Task.Delay(!ranged && name == "cancel" ? 8 : 1, context.RequestAborted);
                    }
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
                catch (IOException) when (context.RequestAborted.IsCancellationRequested) { }
                finally { Interlocked.Decrement(ref owner.active); }
            });
            await app.StartAsync();
            owner = new(app, new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()));
            return owner;
        }
        public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); }
    }
}
