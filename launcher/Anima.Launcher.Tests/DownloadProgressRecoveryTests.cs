using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Anima.Launcher.Core;

internal static class DownloadProgressRecoveryTests
{
    public static async Task<int> Run(string work)
    {
        var count = 0;
        void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); count++; Console.WriteLine("PASS: " + label); }
        var root = Path.Combine(work, "download-progress-recovery");
        Directory.CreateDirectory(root);
        var aria2 = await BundledTools.EnsureAsync("aria2", Path.Combine(root, "runtime"), default);
        var bytes = new byte[32 * 1024 * 1024];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i % 251);
        var sha = Convert.ToHexString(SHA256.HashData(bytes));
        var policy = new DownloadPolicy { RetryDelay = TimeSpan.FromMilliseconds(10), MinimumBytesPerSecond = 0 };
        foreach (var native in new[] { false, true })
        {
            using var handler = new InterruptedSource(bytes, failures: 1);
            using var downloads = new Downloads(handler: handler, policy: policy,
                engineOptions: new(aria2) { MinimumAria2Bytes = 0, ConnectionsPerFile = 1, PollInterval = TimeSpan.FromMilliseconds(60) });
            var request = new DownloadRequest("interrupted public wheel", ["https://files.pythonhosted.org/fixture.whl", "https://mirror.invalid/fixture.whl"],
                Path.Combine(root, native ? "native.whl" : "http.whl"), bytes.Length, sha)
            { PreferHttpClient = !native, TransferProfile = DownloadTransferProfile.Dependency, Network = new("direct"),
                Dependency = new("fixture", "1.0", native ? "native.whl" : "http.whl", sha, bytes.Length) };
            var retries = new ConcurrentQueue<DownloadProgress>();
            await downloads.FetchDirectAsync(request, new Inline(value => { if (value.Phase == "RetryWaiting") retries.Enqueue(value); }), default);
            Check(await Downloads.Verify(request.Destination, bytes.Length, sha, default)
                && handler.MirrorRequests == 0 && handler.Ranges.Any(range => range.From > 0),
                (native ? "real aria2 loopback" : "HTTP prefix") + " resumes a source after an actual stream cut before touching the denied mirror");
            Check(retries.Count == 1 && retries.Single().Attempt == 1 && retries.Single().Received > 0,
                "partial-source recovery publishes its bounded retry and retained byte count for " + (native ? "aria2" : "HTTP"));
        }
        using (var handler = new InterruptedSource(bytes, failures: int.MaxValue))
        using (var downloads = new Downloads(handler: handler, policy: policy))
        {
            var request = new DownloadRequest("repeated interruption", ["https://files.pythonhosted.org/fixture.whl", "https://mirror.invalid/fixture.whl"],
                Path.Combine(root, "bounded.whl"), bytes.Length, sha) { PreferHttpClient = true, Network = new("direct") };
            var retries = 0;
            Exception? failure = null;
            try { await downloads.FetchDirectAsync(request, new Inline(value => { if (value.Phase == "RetryWaiting") retries++; }), default); }
            catch (DownloadSourceException error) { failure = error; }
            Check(failure is DownloadSourceException { StatusCode: HttpStatusCode.Forbidden }
                && handler.BodyRequests == 3 && retries == 2 && handler.MirrorRequests == 1,
                "repeated in-progress interruption exhausts exactly two continuations then switches source without an infinite loop");
            Check(DownloadCheckpoint.RetainedBytes(request.Destination, request.Size, request.Sha256) > 0 && !File.Exists(request.Destination)
                && !failure!.Message.Contains("令牌") && failure.Message.Contains("403"),
                "public mirror denial preserves resumable bytes and never advises a PyPI access token");
        }
        using (var handler = new InterruptedSource(bytes, failures: int.MaxValue))
        using (var downloads = new Downloads(handler: handler, policy: policy))
        using (var cancel = new CancellationTokenSource())
        {
            var request = new DownloadRequest("cancel recovery", ["https://files.pythonhosted.org/fixture.whl", "https://mirror.invalid/fixture.whl"],
                Path.Combine(root, "cancel.whl"), bytes.Length, sha) { PreferHttpClient = true, Network = new("direct") };
            try
            {
                await downloads.FetchDirectAsync(request, new Inline(value => { if (value.Phase == "RetryWaiting") cancel.Cancel(); }), cancel.Token);
                throw new Exception("Expected cancellation");
            }
            catch (OperationCanceledException)
            {
                Check(handler.BodyRequests == 1 && handler.MirrorRequests == 0 && !File.Exists(request.Destination),
                    "cancelling during continuation backoff stops immediately and never starts another source");
            }
        }
        using (var handler = new InterruptedSource(bytes, failures: 1, denyAfterCut: true))
        using (var downloads = new Downloads(handler: handler, policy: policy))
        {
            var request = new DownloadRequest("denied recovery", ["https://files.pythonhosted.org/fixture.whl", "https://mirror.invalid/fixture.whl"],
                Path.Combine(root, "denied.whl"), bytes.Length, sha) { PreferHttpClient = true, Network = new("direct") };
            try { await downloads.FetchDirectAsync(request, null, default); throw new Exception("Expected denial"); }
            catch (DownloadSourceException error)
            {
                Check(error.StatusCode == HttpStatusCode.Forbidden && handler.BodyRequests == 2 && handler.MirrorRequests == 1,
                    "a 403 after a partial transfer is never retried as a transport failure");
            }
        }
        foreach (var host in new[] { "files.pythonhosted.org", "pypi.tuna.tsinghua.edu.cn", "huggingface.co" })
        {
            using var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
            { RequestMessage = new(HttpMethod.Get, "https://" + host + "/fixture") };
            try { SafeHttpSourceClient.EnsureSuccess(response); throw new Exception("Expected denial"); }
            catch (DownloadSourceException error)
            { Check(error.Message.Contains("令牌") == (host == "huggingface.co"), "access-token guidance is scoped to a supported official token site: " + host); }
        }
        return count;
    }

    private sealed class Inline(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    { public void Report(DownloadProgress value) => report(value); }
    private sealed class InterruptedSource(byte[] bytes, int failures, bool denyAfterCut = false) : HttpMessageHandler
    {
        private int bodies, mirrors;
        public int BodyRequests => bodies;
        public int MirrorRequests => mirrors;
        public ConcurrentQueue<(long From, long To)> Ranges { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Host == "mirror.invalid")
            { Interlocked.Increment(ref mirrors); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { RequestMessage = request }); }
            var range = request.Headers.Range?.Ranges.Single();
            var from = range?.From ?? 0;
            var to = range?.To ?? bytes.LongLength - 1;
            Ranges.Enqueue((from, to));
            var body = to > from ? Interlocked.Increment(ref bodies) : 0;
            if (denyAfterCut && body > 1) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { RequestMessage = request });
            Stream content = new MemoryStream(bytes, checked((int)from), checked((int)(to - from + 1)), false);
            if (body > 0 && body <= failures) content = new CutStream(content, 8L * 1024 * 1024);
            var response = new HttpResponseMessage(range is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            { RequestMessage = request, Content = new StreamContent(content) };
            response.Content.Headers.ContentLength = to - from + 1;
            if (range is not null) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, bytes.Length);
            response.Headers.ETag = new EntityTagHeaderValue("\"unchanged-public-artifact\"");
            return Task.FromResult(response);
        }
    }
    private sealed class CutStream(Stream inner, long cut) : Stream
    {
        private long received;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (received >= cut) throw new IOException("injected connection reset after response bytes");
            var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, cut - received)], ct);
            received += read;
            return read;
        }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => received; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
