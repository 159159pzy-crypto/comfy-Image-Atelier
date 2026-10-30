using Anima.Launcher.Core;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Buffers.Binary;

public static class DownloadEngineTests
{
    public static async Task<int> Run(string work)
    {
        var directory = Path.Combine(work, "download-engine");
        Directory.CreateDirectory(directory);
        var passed = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
        async Task<Exception> Reject(Func<Task> action, string name)
        { try { await action(); } catch (Exception error) { Check(true, name); return error; } throw new Exception("Expected failure: " + name); }
        var data = Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();
        var sha = Convert.ToHexString(SHA256.HashData(data));
        const string url = "https://example.invalid/model";
        var fastPolicy = new DownloadPolicy { RetryDelay = TimeSpan.FromMilliseconds(10) };
        DownloadRequest Request(string name, long? size = null, string? digest = null) => new(name, [url], Path.Combine(directory, name), size ?? data.Length, digest ?? sha)
        { Network = new("direct") };

        var retryCalls = 0;
        using (var handler = new Handler(_ =>
        {
            if (Interlocked.Increment(ref retryCalls) > 1) return new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
            var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(180));
            return limited;
        }))
        using (var downloads = new Downloads(handler: handler, policy: fastPolicy))
        {
            var clock = Stopwatch.StartNew();
            await downloads.FetchDirectAsync(Request("retry-after"), null, default);
            Check(retryCalls == 2 && clock.ElapsedMilliseconds >= 150, "single source 429 respects Retry-After before retrying");
        }
        var rangeCalls = 0;
        var freshGet = false;
        var keptRejectedPrefix = false;
        using (var handler = new Handler(request =>
        {
            if (Interlocked.Increment(ref rangeCalls) == 1) return new(HttpStatusCode.RequestedRangeNotSatisfiable);
            freshGet = request.Headers.Range is null;
            keptRejectedPrefix = File.ReadAllBytes(Path.Combine(directory, "range-416.part")).SequenceEqual(data[..1000]);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
        }))
        using (var downloads = new Downloads(handler: handler, policy: fastPolicy))
        {
            var request = Request("range-416");
            File.WriteAllBytes(request.Destination + ".part", data[..1000]);
            JsonFile.Write(request.Destination + ".part.json", new ResumeInfo(sha, data.Length, url, null));
            await downloads.FetchDirectAsync(request, null, default);
            Check(rangeCalls == 2 && freshGet && keptRejectedPrefix && await Downloads.Verify(request.Destination, data.Length, sha, default)
                && !File.Exists(request.Destination + ".part"), "HTTP 416 retains the rejected prefix until one independent full GET passes SHA");
        }

        var budgetRequest = Request("range-416-space"); var budgetCalls = 0;
        File.WriteAllBytes(budgetRequest.Destination + ".part", data[..1000]);
        JsonFile.Write(budgetRequest.Destination + ".part.json", new ResumeInfo(sha, data.Length, url, null));
        var budgetOptions = new DownloadEngineOptions { DiskSpaceOptions = new()
        {
            LedgerRoot = Path.Combine(directory, "fresh-budget-ledger"),
            AvailableBytes = _ => DownloadDiskSpace.SafetyMargin + 3500,
            AllocatedBytes = path => File.Exists(path) ? new FileInfo(path).Length : 0
        } };
        using (var handler = new Handler(_ => { budgetCalls++; return new(HttpStatusCode.RequestedRangeNotSatisfiable); }))
        using (var downloads = new Downloads(handler: handler, engineOptions: budgetOptions))
        {
            var error = await Reject(() => downloads.FetchDirectAsync(budgetRequest, null, default), "416 full restart performs a new physical-space reservation before starting its GET");
            Check(error is InsufficientSpaceException && budgetCalls == 1 && !File.Exists(budgetRequest.Destination + ".http-restart.part")
                && File.ReadAllBytes(budgetRequest.Destination + ".part").SequenceEqual(data[..1000]), "rejected fresh-space budget leaves the prior prefix untouched");
        }
        var concurrentBudget = new DownloadEngineOptions { DiskSpaceOptions = new()
        {
            LedgerRoot = Path.Combine(directory, "concurrent-budget-ledger"),
            AvailableBytes = _ => DownloadDiskSpace.SafetyMargin + data.Length * 3 / 2,
            AllocatedBytes = path => File.Exists(path) ? new FileInfo(path).Length : 0
        } };
        using (var handler = new PatternHandler(data.Length, 1000))
        using (var downloads = new Downloads(handler: handler, engineOptions: concurrentBudget))
        {
            var first = downloads.FetchDirectAsync(Request("budget-first"), null, default);
            while (handler.Requests.Count == 0) await Task.Delay(10);
            var error = await Reject(() => downloads.FetchDirectAsync(Request("budget-second"), null, default), "concurrent downloads reserve the same volume before contacting upstream");
            Check(error is InsufficientSpaceException && handler.Requests.Count == 1, "second download cannot spend the first transfer's reserved physical capacity");
            await first;
            await downloads.FetchDirectAsync(Request("budget-second"), null, default);
            Check(handler.Requests.Count == 2, "completed download releases its claim for the next transfer");
        }

        using (var handler = new PatternHandler(data.Length))
        using (var source = new SafeHttpSourceClient(_ => null, fastPolicy, new("direct"), handler))
        await using (var bridge = await LoopbackRangeBridge.StartAsync(source, Request("bridge"), url, default))
        using (var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }))
        {
            using var noToken = await client.GetAsync(bridge.Address);
            Check(noToken.StatusCode == HttpStatusCode.NotFound && handler.Requests.Count == 0, "loopback bridge rejects missing capabilities without contacting upstream");
            using var wrongHost = new HttpRequestMessage(HttpMethod.Get, bridge.Address);
            wrongHost.Headers.Host = "hostile.example"; wrongHost.Headers.Add(LoopbackRangeBridge.CapabilityHeader, bridge.Token);
            using var badHostResponse = await client.SendAsync(wrongHost);
            Check(badHostResponse.StatusCode == HttpStatusCode.NotFound, "loopback bridge rejects foreign Host headers");
            using var range = new HttpRequestMessage(HttpMethod.Get, bridge.Address);
            range.Headers.Add(LoopbackRangeBridge.CapabilityHeader, bridge.Token); range.Headers.Range = new(7, 35);
            using var response = await client.SendAsync(range);
            Check(response.StatusCode == HttpStatusCode.PartialContent && (await response.Content.ReadAsByteArrayAsync()).SequenceEqual(data[7..36])
                && response.Headers.Location is null, "bridge streams only the requested locked range without redirect headers");
            using var multiple = new HttpRequestMessage(HttpMethod.Get, bridge.Address);
            multiple.Headers.Add(LoopbackRangeBridge.CapabilityHeader, bridge.Token); multiple.Headers.TryAddWithoutValidation("Range", "bytes=0-1,3-4");
            using var multipleResponse = await client.SendAsync(multiple);
            Check(multipleResponse.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable, "bridge rejects multipart ranges");
        }

        var hosts = new List<string>();
        using (var handler = new Handler(request =>
        {
            hosts.Add(request.RequestUri!.Scheme + ":" + request.RequestUri.Host);
            return new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("http://insecure.invalid/model") } };
        }))
        using (var source = new SafeHttpSourceClient(_ => "credential", fastPolicy, new("direct"), handler))
        {
            var error = await Reject(async () => { using var unused = await source.OpenAsync("https://huggingface.co/model", 0, 0, null, default); }, "upstream HTTPS downgrade is rejected");
            Check(error is InvalidDataException && hosts.SequenceEqual(["https:huggingface.co"]), "downgrade destination is never contacted");
        }
        var credentials = new List<string?>();
        using (var handler = new Handler(request =>
        {
            credentials.Add(request.Headers.Authorization?.ToString());
            if (request.RequestUri!.Host == "huggingface.co") return new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://cdn.invalid/model?signature=secret") } };
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(data[..1]) };
            response.Content.Headers.ContentRange = new(0, 0, data.Length); return response;
        }))
        using (var source = new SafeHttpSourceClient(_ => "credential", fastPolicy, new("direct"), handler))
        {
            using var response = await source.OpenAsync("https://huggingface.co/model", 0, 0, null, default);
            Check(credentials.SequenceEqual(["Bearer credential", null]), "official authorization never crosses redirect hosts");
        }

        var etagCalls = 0;
        using (var handler = new Handler(request =>
        {
            var range = request.Headers.Range!.Ranges.Single(); var from = range.From!.Value; var to = range.To!.Value;
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(data[(int)from..((int)to + 1)]) };
            response.Content.Headers.ContentRange = new(from, to, data.Length);
            response.Headers.ETag = new(Interlocked.Increment(ref etagCalls) == 1 ? "\"v1\"" : "\"v2\""); return response;
        }))
        using (var source = new SafeHttpSourceClient(_ => null, fastPolicy, new("direct"), handler))
        await using (var bridge = await LoopbackRangeBridge.StartAsync(source, Request("etag"), url, default))
        using (var client = new HttpClient(new HttpClientHandler { UseProxy = false }))
        {
            client.DefaultRequestHeaders.Add(LoopbackRangeBridge.CapabilityHeader, bridge.Token);
            using var first = new HttpRequestMessage(HttpMethod.Get, bridge.Address); first.Headers.Range = new(0, 15);
            using var firstResponse = await client.SendAsync(first); _ = await firstResponse.Content.ReadAsByteArrayAsync();
            using var changed = new HttpRequestMessage(HttpMethod.Get, bridge.Address); changed.Headers.Range = new(16, 31);
            using var changedResponse = await client.SendAsync(changed);
            Check(changedResponse.StatusCode == HttpStatusCode.BadGateway && await bridge.Failure is DownloadSourceRepresentationChangedException,
                "bridge rejects changed strong ETag even when upstream ignores If-Range");
        }

        var aria2 = await BundledTools.EnsureAsync("aria2", Path.Combine(directory, "runtime"), default);
        var options = new DownloadEngineOptions(aria2) { MinimumAria2Bytes = 0, PollInterval = TimeSpan.FromMilliseconds(80), NetworkPolicyResolver = () => new("direct") };
        var resolverCalls = 0;
        using (var handler = new PatternHandler(data.Length))
        using (var downloads = new Downloads(handler: handler, engineOptions: new DownloadEngineOptions
        { Aria2PathResolver = _ => { resolverCalls++; return Task.FromResult<string?>(aria2); } }))
        {
            await downloads.FetchDirectAsync(Request("small-http"), null, default);
            Check(resolverCalls == 0, "small new files use HTTP without starting or unpacking the native engine");
            var error = await Reject(() => downloads.FetchDirectAsync(Request("signed-url") with
            { Urls = [url + "?X-Amz-Signature=fixture-secret"] }, null, default), "standalone downloads reject signed input URLs before checkpoint persistence");
            Check(error is InvalidDataException && !File.Exists(Request("signed-url").Destination + ".download.lock"), "signed URL rejection creates no task sidecars");
        }
        const long largeSize = 96L * 1024 * 1024;
        var largeSha = PatternHash(largeSize);
        using (var handler = new PatternHandler(largeSize, 1))
        using (var downloads = new Downloads(handler: handler, policy: fastPolicy, engineOptions: options))
        {
            var request = Request("parallel.bin", largeSize, largeSha);
            var connections = 0;
            await downloads.FetchDirectAsync(request, new Inline(value => connections = Math.Max(connections, value.Connections ?? 0)), default);
            Check(await Downloads.Verify(request.Destination, largeSize, largeSha, default), "real aria2 downloads through loopback and publishes only verified bytes");
            Check(handler.PeakStreams >= 2 && handler.PeakStreams <= 4 && connections >= 2,
                "large file uses concurrent upstream ranges within the four connection limit");
            Check(!Downloads.PartialFiles(request.Destination).Any(File.Exists), "successful aria2 publication removes only its checkpoint sidecars");
        }
        using (var handler = new PatternHandler(largeSize, 5))
        using (var downloads = new Downloads(handler: handler, policy: fastPolicy, engineOptions: options))
        {
            var request = Request("paused.bin", largeSize, largeSha);
            using var cancel = new CancellationTokenSource();
            var error = await Reject(() => downloads.FetchDirectAsync(request, new Inline(value => { if (value.Received >= 2 * 1024 * 1024) cancel.Cancel(); }), cancel.Token), "real aria2 pause interrupts transfer");
            Check(error is OperationCanceledException && File.Exists(DownloadCheckpoint.AriaControl(request.Destination)), "pause saves an aria2 control file");
            var retained = largeSize - Downloads.RemainingBytes(request.Destination, largeSize, largeSha);
            Check(retained > 0 && retained < largeSize && !File.Exists(request.Destination), "resume estimate uses saved complete-piece bitmap rather than sparse file length");
            var ownedRemains = Process.GetProcessesByName("aria2c").Any(process =>
            { using (process) { try { return string.Equals(process.MainModule?.FileName, aria2, StringComparison.OrdinalIgnoreCase); } catch { return false; } } });
            Check(!ownedRemains, "paused transfer leaves no owned aria2 process running");
            var firstCount = handler.Requests.Count;
            await downloads.FetchDirectAsync(request, null, default);
            Check(await Downloads.Verify(request.Destination, largeSize, largeSha, default)
                && handler.Requests.Skip(firstCount).Any(range => range.From > 0), "paused aria2 transfer resumes saved ranges and passes final SHA");
        }
        const long migrationSize = 8L * 1024 * 1024;
        using (var pattern = new PatternHandler(256L * 1024 * 1024, 8))
        {
            var contactedHosts = new ConcurrentQueue<string>();
            using var handler = new Handler(message => { contactedHosts.Enqueue(message.RequestUri!.Host); return pattern.Response(message); });
            using var downloads = new Downloads(handler: handler, policy: fastPolicy, engineOptions: options);
            var request = Request("native-crash.bin", 256L * 1024 * 1024, PatternHash(256L * 1024 * 1024))
                with { Urls = [url, "https://unused-backup.invalid/model"] };
            var killed = new HashSet<int>(); var restartReports = 0;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var error = await Reject(() => downloads.FetchDirectAsync(request, new Inline(value =>
            {
                if (value.FailureCode == "engine_crash") restartReports++;
                if (value.Phase != "Downloading" || killed.Count >= 2
                    || !File.Exists(DownloadCheckpoint.AriaControl(request.Destination))
                    || DownloadCheckpoint.RetainedBytes(request.Destination, request.Size, request.Sha256) == 0) return;
                foreach (var process in Process.GetProcessesByName("aria2c"))
                    using (process)
                    {
                        try
                        {
                            if (string.Equals(process.MainModule?.FileName, aria2, StringComparison.OrdinalIgnoreCase) && killed.Add(process.Id))
                                process.Kill(entireProcessTree: false);
                        }
                        catch (InvalidOperationException) { }
                    }
            }), timeout.Token), "two real native-only crashes stop after exactly one engine restart");
            Check(error is DownloadCheckpointException && killed.Count == 2 && restartReports == 1
                && File.Exists(DownloadCheckpoint.AriaControl(request.Destination)) && !File.Exists(request.Destination)
                && contactedHosts.All(host => host != "unused-backup.invalid"),
                "exhausted engine restart budget preserves control and does not blame or contact another source");
        }
        var migrationSha = PatternHash(migrationSize);
        using (var handler = new PatternHandler(migrationSize))
        using (var downloads = new Downloads(handler: handler, policy: fastPolicy, engineOptions: options))
        {
            var request = Request("legacy.bin", migrationSize, migrationSha);
            await WritePattern(request.Destination + ".part", 4L * 1024 * 1024);
            JsonFile.Write(request.Destination + ".part.json", new ResumeInfo(migrationSha, migrationSize, url, "\"fixture-v1\""));
            await downloads.FetchDirectAsync(request, null, default);
            Check(handler.Requests.Any(range => range.From >= 4L * 1024 * 1024) && await Downloads.Verify(request.Destination, migrationSize, migrationSha, default),
                "legacy contiguous prefix migrates into aria2 without losing downloaded bytes");
        }
        using (var handler = new PatternHandler(data.Length, 10))
        using (var downloads = new Downloads(handler: handler, policy: fastPolicy))
        {
            var request = Request("same-target.bin");
            await Task.WhenAll(downloads.FetchDirectAsync(request, null, default), downloads.FetchDirectAsync(request, null, default));
            Check(handler.Requests.Count == 1 && await Downloads.Verify(request.Destination, data.Length, sha, default),
                "standalone Downloads serializes the same destination and reuses the first verified result");
        }
        using (var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }))
        using (var downloads = new Downloads(handler: handler, policy: fastPolicy, engineOptions: options))
        {
            var request = Request("no-range.bin");
            var engine = "";
            await downloads.FetchDirectAsync(request, new Inline(value => engine = value.Engine ?? engine), default);
            Check(engine == "HttpClient" && await Downloads.Verify(request.Destination, data.Length, sha, default)
                && !Downloads.PartialFiles(request.Destination).Any(File.Exists), "source without Range support uses compatible single-stream download");
        }
        using (var handler = new PatternHandler(data.Length))
        using (var downloads = new Downloads(handler: handler, policy: fastPolicy, engineOptions: options))
        {
            var request = Request("missing-control.bin");
            File.WriteAllBytes(DownloadCheckpoint.AriaData(request.Destination), new byte[data.Length]);
            JsonFile.Write(DownloadCheckpoint.AriaState(request.Destination), new { schemaVersion = 1, sha256 = sha, size = data.Length, sourceUrl = url, engineStarted = true });
            var error = await Reject(() => downloads.FetchDirectAsync(request, null, default), "missing aria2 control is not interpreted as a contiguous partial");
            Check(error is DownloadCheckpointException && handler.Requests.Count == 0
                && Downloads.RemainingBytes(request.Destination, data.Length, sha) == data.Length, "sparse file length cannot claim downloaded bytes");
            await downloads.FetchDirectAsync(request with { PreferHttpClient = true }, null, default);
            Check(await Downloads.Verify(request.Destination, data.Length, sha, default) && File.Exists(DownloadCheckpoint.AriaData(request.Destination)),
                "explicit compatibility restart writes independent HTTP scratch and preserves aria2 data");
            await Downloads.CancelPartialAsync(request.Destination);
            Check(File.Exists(request.Destination) && !Downloads.PartialFiles(request.Destination).Any(File.Exists),
                "explicit partial cleanup preserves the completed destination");
        }
        var fullGets = 0;
        using (var handler = new PatternHandler(data.Length))
        using (var downloads = new Downloads(handler: handler, policy: fastPolicy, engineOptions: options))
        {
            var request = Request("corrupt-control.bin") with { Urls = [url, "https://second.invalid/model"] };
            File.WriteAllBytes(DownloadCheckpoint.AriaData(request.Destination), new byte[data.Length]);
            JsonFile.Write(DownloadCheckpoint.AriaState(request.Destination), new { schemaVersion = 1, sha256 = sha, size = data.Length, sourceUrl = url, engineStarted = true });
            File.WriteAllBytes(DownloadCheckpoint.AriaControl(request.Destination), [1, 2, 3]);
            var error = await Reject(() => downloads.FetchDirectAsync(request, null, default), "malformed local checkpoint is a reviewable local failure");
            Check(error is DownloadCheckpointException && handler.Requests.Count == 0, "local checkpoint corruption does not trigger source fallback or remote requests");
        }
        using (var handler = new Handler(message =>
        {
            if (message.Headers.Range is not null) return new(HttpStatusCode.RequestedRangeNotSatisfiable);
            Interlocked.Increment(ref fullGets); return new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
        }))
        using (var downloads = new Downloads(handler: handler, policy: fastPolicy, engineOptions: options))
        {
            var request = Request("aria2-416.bin");
            File.WriteAllBytes(DownloadCheckpoint.AriaData(request.Destination), new byte[data.Length]);
            JsonFile.Write(DownloadCheckpoint.AriaState(request.Destination), new { schemaVersion = 1, sha256 = sha, size = data.Length, sourceUrl = url, engineStarted = true });
            var control = new byte[39]; BinaryPrimitives.WriteUInt16BigEndian(control, 1);
            BinaryPrimitives.WriteUInt32BigEndian(control.AsSpan(10), 1048576); BinaryPrimitives.WriteUInt64BigEndian(control.AsSpan(14), (ulong)data.Length);
            BinaryPrimitives.WriteUInt32BigEndian(control.AsSpan(30), 1);
            File.WriteAllBytes(DownloadCheckpoint.AriaControl(request.Destination), control);
            await downloads.FetchDirectAsync(request, null, default);
            Check(fullGets == 1 && await Downloads.Verify(request.Destination, data.Length, sha, default)
                && !Downloads.PartialFiles(request.Destination).Any(File.Exists),
                "aria2 416 publishes one independently verified full GET before cleaning obsolete sparse checkpoints");
        }
        var limitedOnce = 0;
        using (var pattern = new PatternHandler(largeSize, 1))
        using (var handler = new Handler(message =>
        {
            if (message.Headers.Range?.Ranges.Single().From is >= 32L * 1024 * 1024 && Interlocked.Exchange(ref limitedOnce, 1) == 0)
            {
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(180)); return limited;
            }
            return pattern.Response(message);
        }))
        using (var downloads = new Downloads(handler: handler, policy: fastPolicy, engineOptions: options))
        {
            var request = Request("mid-transfer-429.bin", largeSize, largeSha);
            var waited = false;
            await downloads.FetchDirectAsync(request, new Inline(value => waited |= value.Phase == "RetryWaiting" && value.RetryAt is not null), default);
            Check(limitedOnce == 1 && waited && await Downloads.Verify(request.Destination, largeSize, largeSha, default),
                "upstream 429 during concurrent transfer pauses the engine and resumes after host Retry-After policy");
        }
        var seenHosts = new List<string>();
        using (var handler = new Handler(message =>
        {
            seenHosts.Add(message.RequestUri!.Host);
            return message.RequestUri.Host == "cold-mirror.invalid" ? new(HttpStatusCode.BadGateway)
                : new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
        }))
        using (var downloads = new Downloads(handler: handler, policy: fastPolicy))
        {
            var candidates = new[] { "https://cold-mirror.invalid/model", "https://healthy-source.invalid/model" };
            await downloads.FetchDirectAsync(Request("health-first") with { Urls = candidates }, null, default);
            await downloads.FetchDirectAsync(Request("health-next") with { Urls = candidates }, null, default);
            Check(seenHosts.SequenceEqual(["cold-mirror.invalid", "healthy-source.invalid", "healthy-source.invalid"]),
                "recent successful source is reused while the failed host is cooling down");
        }
        var external = Path.Combine(directory, "external-file"); File.WriteAllBytes(external, data);
        var linked = Request("linked-part.bin");
        try
        {
            File.CreateSymbolicLink(linked.Destination + ".part", external);
            JsonFile.Write(linked.Destination + ".part.json", new ResumeInfo(sha, data.Length, url, null));
            using var handler = new PatternHandler(data.Length); using var downloads = new Downloads(handler: handler);
            var error = await Reject(() => downloads.FetchDirectAsync(linked, null, default), "linked partial paths are rejected before reading or appending");
            Check(error is IOException && handler.Requests.Count == 0 && File.ReadAllBytes(external).SequenceEqual(data), "rejecting a partial link preserves its external target");
        }
        catch (Exception error) when (error is UnauthorizedAccessException || error is IOException && (error.HResult & 0xffff) == 1314)
        {
            if (!OperatingSystem.IsWindows()) throw;
            var outsideDirectory = Path.Combine(directory, "external-directory"); Directory.CreateDirectory(outsideDirectory);
            File.WriteAllText(Path.Combine(outsideDirectory, "sentinel"), "unchanged");
            var powershell = Commands.Find("powershell.exe") ?? throw new IOException("A junction fixture requires Windows PowerShell.");
            var command = "New-Item -ItemType Junction -Path '" + (linked.Destination + ".part").Replace("'", "''")
                + "' -Target '" + outsideDirectory.Replace("'", "''") + "' | Out-Null";
            await Commands.Run(powershell, ["-NoProfile", "-NonInteractive", "-Command", command], directory, null, default);
            using var handler = new PatternHandler(data.Length); using var downloads = new Downloads(handler: handler);
            var rejected = await Reject(() => downloads.FetchDirectAsync(linked, null, default), "junction partial paths are rejected before IO");
            Check(rejected is IOException && rejected.Message.Contains("暂存路径") && handler.Requests.Count == 0
                && File.ReadAllText(Path.Combine(outsideDirectory, "sentinel")) == "unchanged", "junction rejection preserves the external directory");
        }
        Console.WriteLine("Download engine fixtures: " + directory);
        return passed;
    }

    private sealed class Inline(Action<DownloadProgress> action) : IProgress<DownloadProgress> { public void Report(DownloadProgress value) => action(value); }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> action) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(action(request)); }
    private sealed class PatternHandler(long size, int delay = 0) : HttpMessageHandler
    {
        private int streams;
        private int peak;
        public int PeakStreams => peak;
        public ConcurrentQueue<(long From, long To)> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(Response(request));
        public HttpResponseMessage Response(HttpRequestMessage request)
        {
            var range = request.Headers.Range?.Ranges.Single();
            var from = range?.From ?? 0; var to = range?.To ?? size - 1;
            Requests.Enqueue((from, to));
            var response = new HttpResponseMessage(range is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            { Content = new StreamContent(new PatternStream(from, to - from + 1, delay, () =>
                { var count = Interlocked.Increment(ref streams); int old; do { old = peak; if (count <= old) break; } while (Interlocked.CompareExchange(ref peak, count, old) != old); },
                () => Interlocked.Decrement(ref streams))) };
            response.Content.Headers.ContentLength = to - from + 1;
            if (range is not null) response.Content.Headers.ContentRange = new(from, to, size);
            response.Headers.ETag = new("\"fixture-v1\"");
            return response;
        }
    }
    private sealed class PatternStream(long start, long length, int delay, Action opened, Action closed) : Stream
    {
        private long position;
        private bool initialized, disposed;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!initialized) { initialized = true; opened(); }
            if (position >= length) return 0;
            if (delay > 0) await Task.Delay(delay, ct);
            var count = (int)Math.Min(buffer.Length, length - position);
            Fill(buffer.Span[..count], start + position); position += count; return count;
        }
        protected override void Dispose(bool disposing) { if (!disposed) { disposed = true; if (initialized) closed(); } base.Dispose(disposing); }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => length; public override long Position { get => position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { var read = (int)Math.Min(count, length - position); Fill(buffer.AsSpan(offset, read), start + position); position += read; return read; }
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private static void Fill(Span<byte> buffer, long start) { for (var i = 0; i < buffer.Length; i++) buffer[i] = (byte)((start + i) % 251); }
    private static string PatternHash(long length)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[131072];
        for (long offset = 0; offset < length; offset += buffer.Length) { var count = (int)Math.Min(buffer.Length, length - offset); Fill(buffer.AsSpan(0, count), offset); hash.AppendData(buffer, 0, count); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static async Task WritePattern(string path, long length)
    {
        await using var file = File.Create(path); var buffer = new byte[131072];
        for (long offset = 0; offset < length; offset += buffer.Length) { var count = (int)Math.Min(buffer.Length, length - offset); Fill(buffer.AsSpan(0, count), offset); await file.WriteAsync(buffer.AsMemory(0, count)); }
    }
}
