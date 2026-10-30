using System.Collections.Concurrent;
using System.Diagnostics;
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

internal static class DependencyTransferTests
{
    public static async Task<int> Run(string work)
    {
        var passed = 0;
        void Check(bool condition, string message)
        { if (!condition) throw new Exception("FAIL: " + message); passed++; Console.WriteLine("PASS: " + message); }
        async Task Reject(Func<Task> action, string message)
        { try { await action(); } catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or OperationCanceledException) { Check(true, message); return; } throw new Exception("Expected rejection: " + message); }
        var root = Path.Combine(work, "dependency-transfers-" + Guid.NewGuid().ToString("N"));
        var bytes = Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray();
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var config = new LauncherConfig { ComfyRoot = Path.Combine(root, "environment"), NetworkMode = "direct" };
        DownloadRequest Request(string name, bool dependency)
        {
            var filename = name + (dependency ? "-1.0-py3-none-any.whl" : ".bin");
            return new(name, ["https://packages.invalid/" + filename], Path.Combine(root, filename), bytes.Length, sha)
            {
                Network = new("direct"), TransferProfile = dependency ? DownloadTransferProfile.Dependency : DownloadTransferProfile.Standard,
                Dependency = dependency ? new(name, "1.0", filename, sha, bytes.Length) : null
            };
        }
        using var handler = new GateHandler(bytes);
        using var downloads = new Downloads(handler: handler);
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdRefresh = false;
        var refreshCalls = 0;
        await using var service = new DownloadTaskService(downloads, root, dependencySourceResolver: async (identity, settings, ct) =>
        {
            Interlocked.Increment(ref refreshCalls);
            Check(identity.Sha256 == sha && identity.Size == bytes.Length && identity.Version == "1.0", "source lookup receives the original exact package identity");
            if (holdRefresh) { refreshStarted.TrySetResult(); await refreshRelease.Task.WaitAsync(ct); }
            return ["https://replacement.invalid/" + identity.FileName];
        });

        var ordinary = Enumerable.Range(0, 3).Select(i => Request("ordinary" + i, false)).ToArray();
        var ordinaryRuns = ordinary.Select(request => service.FetchAsync(request, null, default)).ToArray();
        await handler.WaitStarted(ordinary[0]); await handler.WaitStarted(ordinary[1]);
        Check(handler.Active == 2 && !handler.Started(ordinary[2]), "ordinary downloads retain two concurrent files");
        handler.Release(ordinary[0]); await ordinaryRuns[0]; await handler.WaitStarted(ordinary[2]);
        foreach (var request in ordinary) handler.Release(request);
        await Task.WhenAll(ordinaryRuns);

        var dependencies = Enumerable.Range(0, 5).Select(i => Request("dep" + i, true)).ToArray();
        var dependencyRuns = dependencies.Select(request => service.FetchAsync(request, null, default)).ToArray();
        await Task.WhenAll(dependencies.Take(4).Select(handler.WaitStarted));
        Check(handler.Active == 4 && !handler.Started(dependencies[4]), "dependency downloads admit four files and queue the fifth");
        handler.Release(dependencies[0]); await dependencyRuns[0]; await handler.WaitStarted(dependencies[4]);
        foreach (var request in dependencies) handler.Release(request);
        await Task.WhenAll(dependencyRuns);
        Check(handler.Peak == 4, "dependency scheduling does not exceed four active files");

        var mixed = new[] { Request("mixednormal0", false), Request("mixednormal1", false), Request("mixednormal2", false),
            Request("mixeddep0", true), Request("mixeddep1", true), Request("mixeddep2", true) };
        var mixedRuns = mixed.Select(request => service.FetchAsync(request, null, default)).ToArray();
        await Task.WhenAll(new[] { mixed[0], mixed[1], mixed[3], mixed[4] }.Select(handler.WaitStarted));
        Check(handler.Active == 4 && !handler.Started(mixed[2]) && !handler.Started(mixed[5]),
            "mixed batches share four total slots while ordinary files remain capped at two");
        foreach (var request in mixed) handler.Release(request);
        await Task.WhenAll(mixedRuns);

        var invalid = Request("invalid", true);
        await Reject(() => service.FetchAsync(invalid with { Dependency = invalid.Dependency! with { Sha256 = new string('a', 64) } }, null, default),
            "dependency metadata cannot disagree with the content digest");
        await Reject(() => service.FetchAsync(invalid with { TransferProfile = DownloadTransferProfile.Standard }, null, default),
            "package identity cannot silently lose its transfer profile");

        var owner = service.RegisterOwner("setup", "dependency recovery", config);
        var pausedRequest = Request("pausedpackage", true);
        var owned = service.RunOwnerAsync(owner.Id, ct => downloads.FetchDependencyAsync(new()
        {
            Name = pausedRequest.Dependency!.Name, Version = "1.0", FileName = pausedRequest.Dependency.FileName,
            Sha256 = sha, Size = bytes.Length, Sources = pausedRequest.Urls
        }, pausedRequest.Destination, config, null, ct));
        await handler.WaitStarted(pausedRequest);
        await service.PauseOwnerAsync(owner.Id);
        await Reject(() => owned, "pausing dependency owner waits for its child to stop");
        var original = service.Tasks.Single(task => task.Dependency?.Name == "pausedpackage");
        Check(original.Status == DownloadTaskStatus.Paused && original.Network?.Mode == "direct", "paused dependency keeps its owner proxy snapshot");
        var restoredDirectory = Path.Combine(root, "restored"); Directory.CreateDirectory(restoredDirectory);
        File.Copy(Path.Combine(root, "download-tasks.json"), Path.Combine(restoredDirectory, "download-tasks.json"));
        using (var restoredDownloads = new Downloads(handler: new GateHandler(bytes)))
        {
            await using var restored = new DownloadTaskService(restoredDownloads, restoredDirectory);
            var record = restored.Tasks.Single(task => task.Id == original.Id);
            Check(record.Dependency == original.Dependency && record.TransferProfile == DownloadTransferProfile.Dependency
                && record.Status == DownloadTaskStatus.Paused && !restored.HasRunningTasks,
                "restart preserves dependency identity and profile without starting network work");
        }
        var store = new DownloadTaskStore(restoredDirectory);
        var tampered = store.Load();
        tampered.Tasks.Single(task => task.Id == original.Id).Dependency = original.Dependency! with { Version = "2.0" };
        await Reject(() => Task.Run(() => store.Save(tampered)), "persisted package coordinate cannot change under the original task fingerprint");
        await Reject(() => Task.Run(() => service.UpdateOwnerSources(owner.Id, config)), "synchronous source replacement cannot drop package mirror identities");

        var sourceConfig = config.Clone(); sourceConfig.SourceId = "official";
        await service.UpdateOwnerSourcesAsync(owner.Id, sourceConfig);
        var refreshed = service.Tasks.Single(task => task.Id == original.Id);
        Check(refreshed.Dependency == original.Dependency && refreshed.Fingerprint == original.Fingerprint && refreshed.Size == original.Size
            && refreshed.Destination == original.Destination && refreshed.Urls[0].StartsWith("https://replacement.invalid/", StringComparison.Ordinal)
            && refreshed.TransportRevision == original.TransportRevision + 1,
            "asynchronous source refresh changes only transport and retains fixed package identity");
        holdRefresh = true;
        var racing = service.UpdateOwnerSourcesAsync(owner.Id, sourceConfig);
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.UpdateTaskSources(original.Id, ["https://newer.invalid/" + original.Dependency!.FileName]);
        refreshRelease.TrySetResult();
        await Reject(() => racing, "stale asynchronous source lookup cannot overwrite a newer transport choice");
        Check(service.Tasks.Single(task => task.Id == original.Id).Urls[0].StartsWith("https://newer.invalid/", StringComparison.Ordinal), "newer task source remains intact after refresh conflict");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Reject(() => service.UpdateOwnerSourcesAsync(owner.Id, sourceConfig, canceled.Token), "canceled mirror refresh does not update persisted tasks");

        var options = new DownloadEngineOptions();
        var medium = Request("medium", true) with { Size = 16L * 1024 * 1024 };
        Check(DownloadTransferPolicy.NativeThreshold(medium, options) == medium.Size && DownloadTransferPolicy.Connections(medium, options) == 4
            && DownloadTransferPolicy.MinimumSplitSize(medium) == "4M", "dependency profile enables four 4 MiB ranges at 16 MiB");
        var standard = medium with { TransferProfile = DownloadTransferProfile.Standard, Dependency = null };
        Check(DownloadTransferPolicy.NativeThreshold(standard, options) == 64L * 1024 * 1024 && DownloadTransferPolicy.Connections(standard, options) == 1
            && DownloadTransferPolicy.MinimumSplitSize(standard) == "32M", "ordinary transfer segmentation defaults remain unchanged");
        Check(refreshCalls == 2, "cancellation prevents unnecessary package index lookup");
        var resumeRoot = Path.Combine(root, "partial-resume");
        var resumeBytes = new byte[2 * 1024 * 1024];
        for (var i = 0; i < resumeBytes.Length; i++) resumeBytes[i] = (byte)(i % 251);
        var resumeSha = Convert.ToHexString(SHA256.HashData(resumeBytes)).ToLowerInvariant();
        var resumePackage = new DependencyPackage { Name = "resumable", Version = "1.0", FileName = "resumable-1.0-py3-none-any.whl",
            Sha256 = resumeSha, Size = resumeBytes.Length, Sources = ["https://initial.invalid/resumable.whl"] };
        var resumeFile = Path.Combine(resumeRoot, resumePackage.FileName);
        string resumeOwnerId;
        using (var firstHandler = new PartialHandler(resumeBytes))
        using (var firstDownloads = new Downloads(handler: firstHandler))
        {
            await using var firstService = new DownloadTaskService(firstDownloads, resumeRoot);
            resumeOwnerId = firstService.RegisterOwner("setup", "partial wheel recovery", config).Id;
            using var pause = new CancellationTokenSource();
            var progress = new InlineProgress(value => { if (value.Received >= 256 * 1024 && value.Received < value.Total) pause.Cancel(); });
            await Reject(() => firstService.RunOwnerAsync(resumeOwnerId,
                ct => firstDownloads.FetchDependencyAsync(resumePackage, resumeFile, config, progress, ct), pause.Token),
                "pausing during package bytes preserves a resumable partial file");
        }
        var retained = new FileInfo(resumeFile + ".part").Length;
        Check(retained is > 0 && retained < resumeBytes.Length, "dependency partial retains real prefix bytes before restart");
        using (var secondHandler = new PartialHandler(resumeBytes))
        using (var secondDownloads = new Downloads(handler: secondHandler))
        {
            await using var secondService = new DownloadTaskService(secondDownloads, resumeRoot, dependencySourceResolver:
                (_, _, _) => Task.FromResult(new[] { "https://resumed.invalid/resumable.whl" }));
            var before = secondService.Tasks.Single();
            await secondService.UpdateOwnerSourcesAsync(resumeOwnerId, config);
            await secondService.RunOwnerAsync(resumeOwnerId,
                ct => secondDownloads.FetchDependencyAsync(resumePackage, resumeFile, config, null, ct));
            var after = secondService.Tasks.Single();
            Check(secondHandler.LastFrom == retained && secondHandler.BytesRead < resumeBytes.Length
                && secondHandler.LastHost == "resumed.invalid", "restart and explicit mirror switch resume original dependency bytes instead of downloading again");
            Check(before.Id == after.Id && before.Dependency == after.Dependency && before.Fingerprint == after.Fingerprint
                && await Downloads.Verify(resumeFile, resumeBytes.Length, resumeSha, default),
                "resumed wheel retains task identity and passes full SHA-256 before completion");
        }
        var archive = new DependencyPackage { Name = "archive-fixture", Version = "1.0", Kind = "archive", FileName = "archive_fixture-1.0.tar.gz",
            Sha256 = sha, Size = bytes.Length, Sources = ["https://packages.invalid/archive_fixture-1.0.tar.gz"] };
        var archivePath = Path.Combine(root, archive.FileName);
        var archiveOwner = service.RegisterOwner("setup", "source archive recovery", config,
            payload: System.Text.Json.JsonSerializer.SerializeToElement(new { archive = true }));
        handler.Release(new(archive.Name, archive.Sources, archivePath, archive.Size, archive.Sha256));
        await service.RunOwnerAsync(archiveOwner.Id, ct => downloads.FetchDependencyAsync(archive, archivePath, config, null, ct));
        var archiveBefore = service.Tasks.Single(task => task.Dependency?.Name == archive.Name);
        await service.UpdateOwnerSourcesAsync(archiveOwner.Id, sourceConfig);
        var archiveAfter = service.Tasks.Single(task => task.Id == archiveBefore.Id);
        Check(archiveAfter.Dependency == archiveBefore.Dependency && archiveAfter.Dependency?.FileName.EndsWith(".tar.gz", StringComparison.Ordinal) == true
            && archiveAfter.Urls[0] == "https://replacement.invalid/" + archive.FileName
            && await Downloads.Verify(archivePath, archive.Size, archive.Sha256, default),
            "source archives share dependency scheduling, exact identity, verification and mirror refresh");
        return passed;
    }

    public static async Task<int> RunPerformance(string work)
    {
        var root = Path.Combine(work, "dependency-transfer-performance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        const int size = 16 * 1024 * 1024;
        var bytes = new byte[size];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i % 251);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var aria2 = await BundledTools.EnsureAsync("aria2", Path.Combine(root, "runtime"));
        await using var fixture = await LimitedServer.Start(bytes);
        async Task<Measurement> Measure(bool accelerated)
        {
            fixture.Reset();
            var destination = Path.Combine(root, accelerated ? "parallel" : "serial");
            using var handler = new ForwardHandler(fixture.Address);
            using var downloads = new Downloads(handler: handler, engineOptions: new(aria2) { PollInterval = TimeSpan.FromMilliseconds(100) });
            await using var service = new DownloadTaskService(downloads, destination, maxConcurrent: 1);
            var clock = Stopwatch.StartNew();
            var requests = Enumerable.Range(0, 4).Select(i =>
            {
                var filename = "fixture" + i + "-1.0-py3-none-any.whl";
                return new DownloadRequest("fixture" + i, ["https://performance.invalid/" + filename], Path.Combine(destination, filename), size, sha)
                {
                    Network = new("direct"), TransferProfile = accelerated ? DownloadTransferProfile.Dependency : DownloadTransferProfile.Standard,
                    Dependency = accelerated ? new("fixture" + i, "1.0", filename, sha, size) : null
                };
            }).ToArray();
            await Task.WhenAll(requests.Select(request => service.FetchAsync(request, null, default)));
            clock.Stop();
            foreach (var request in requests)
                if (!await Downloads.Verify(request.Destination, size, sha, default)) throw new Exception("Performance fixture content differs.");
            return new(accelerated ? "dependency-parallel" : "serial-http", clock.Elapsed.TotalSeconds, fixture.Peak, fixture.RangeRequests, requests.Sum(r => r.Size));
        }
        var serial = await Measure(false);
        var parallel = await Measure(true);
        var speedup = serial.Seconds / parallel.Seconds;
        JsonFile.Write(Path.Combine(root, "performance.json"), new { serial, parallel, speedup, source = "localhost: 2 MiB/s per response stream" });
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { serial, parallel, speedup }));
        if (serial.PeakStreams != 1 || parallel.PeakStreams is < 2 or > 8 || parallel.RangeRequests < 8)
            throw new Exception("Dependency performance fixture did not prove bounded parallel range requests.");
        if (speedup < 2) throw new Exception("Dependency transfer speedup is below 2x against the same serial fixture; see performance.json.");
        Console.WriteLine("PASS: real dependency aria2 ranges keep the global eight-connection budget and exceed 2x serial throughput");
        return 10; // Four verified files in each run, plus transport and performance assertions.
    }

    private sealed record Measurement(string Name, double Seconds, int PeakStreams, int RangeRequests, long Bytes);
    private sealed class InlineProgress(Action<DownloadProgress> callback) : IProgress<DownloadProgress>
    { public void Report(DownloadProgress value) => callback(value); }

    private sealed class PartialHandler(byte[] bytes) : HttpMessageHandler
    {
        public long LastFrom { get; private set; }
        public string? LastHost { get; private set; }
        public long BytesRead;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var from = request.Headers.Range?.Ranges.Single().From ?? 0;
            LastFrom = from; LastHost = request.RequestUri!.Host;
            var response = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
                { Content = new StreamContent(new PacedMemory(bytes, (int)from, count => Interlocked.Add(ref BytesRead, count))) };
            response.Content.Headers.ContentLength = bytes.Length - from;
            if (from > 0) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, bytes.Length - 1, bytes.Length);
            return Task.FromResult(response);
        }
    }

    private sealed class PacedMemory(byte[] bytes, int offset, Action<int> read) : MemoryStream(bytes, offset, bytes.Length - offset, false)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(15, ct);
            var count = await base.ReadAsync(buffer[..Math.Min(buffer.Length, 64 * 1024)], ct);
            read(count); return count;
        }
    }
    private sealed class GateHandler(byte[] bytes) : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> gates = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> starts = new();
        private int active, peak;
        public int Active => Volatile.Read(ref active);
        public int Peak => Volatile.Read(ref peak);
        private static string Key(DownloadRequest request) => Path.GetFileName(request.Destination);
        public bool Started(DownloadRequest request) => starts.TryGetValue(Key(request), out var started) && started.Task.IsCompleted;
        public Task WaitStarted(DownloadRequest request) => starts.GetOrAdd(Key(request), _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(TimeSpan.FromSeconds(10));
        public void Release(DownloadRequest request) => gates.GetOrAdd(Key(request), _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var key = request.RequestUri!.Segments.Last();
            var current = Interlocked.Increment(ref active);
            int previous;
            do { previous = Volatile.Read(ref peak); if (current <= previous) break; } while (Interlocked.CompareExchange(ref peak, current, previous) != previous);
            starts.GetOrAdd(key, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
            try
            {
                await gates.GetOrAdd(key, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(ct);
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            }
            finally { Interlocked.Decrement(ref active); }
        }
    }

    private sealed class ForwardHandler(Uri address) : HttpMessageHandler
    {
        private readonly HttpClient client = new(new HttpClientHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var forwarded = new HttpRequestMessage(request.Method, new Uri(address, request.RequestUri!.PathAndQuery));
            foreach (var header in request.Headers) if (header.Key != "Host") forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
            return await client.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        protected override void Dispose(bool disposing) { if (disposing) client.Dispose(); base.Dispose(disposing); }
    }

    private sealed class LimitedServer(WebApplication app, Uri address) : IAsyncDisposable
    {
        private int active, peak, rangeRequests;
        public Uri Address { get; } = address;
        public int Peak => Volatile.Read(ref peak);
        public int RangeRequests => Volatile.Read(ref rangeRequests);
        public void Reset() { if (Volatile.Read(ref active) != 0) throw new Exception("Fixture has active responses."); peak = 0; rangeRequests = 0; }
        public static async Task<LimitedServer> Start(byte[] bytes)
        {
            var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            LimitedServer? fixture = null;
            app.Run(async context =>
            {
                var current = Interlocked.Increment(ref fixture!.active);
                int old;
                do { old = Volatile.Read(ref fixture.peak); if (current <= old) break; } while (Interlocked.CompareExchange(ref fixture.peak, current, old) != old);
                try
                {
                    var from = 0L; var to = bytes.LongLength - 1;
                    if (RangeHeaderValue.TryParse(context.Request.Headers.Range.ToString(), out var range))
                    {
                        Interlocked.Increment(ref fixture.rangeRequests);
                        var part = range.Ranges.Single(); from = part.From ?? 0; to = part.To ?? to;
                        context.Response.StatusCode = 206; context.Response.Headers.ContentRange = $"bytes {from}-{to}/{bytes.Length}";
                    }
                    context.Response.Headers.AcceptRanges = "bytes";
                    context.Response.ContentLength = to - from + 1;
                    const int chunk = 64 * 1024;
                    for (var offset = from; offset <= to;)
                    {
                        var count = (int)Math.Min(chunk, to + 1 - offset);
                        await context.Response.Body.WriteAsync(bytes.AsMemory((int)offset, count), context.RequestAborted);
                        await context.Response.Body.FlushAsync(context.RequestAborted);
                        offset += count;
                        if (offset <= to) await Task.Delay(TimeSpan.FromSeconds(count / (2d * 1024 * 1024)), context.RequestAborted);
                    }
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
                finally { Interlocked.Decrement(ref fixture.active); }
            });
            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
            fixture = new(app, new Uri(addresses.Addresses.Single()));
            return fixture;
        }
        public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); }
    }
}
