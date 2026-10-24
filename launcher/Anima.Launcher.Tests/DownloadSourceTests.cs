using Anima.Launcher.Core;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

public static class DownloadSourceTests
{
    public static async Task<int> Run(string work, ModelManifest? manifest = null)
    {
        var previous = Environment.GetEnvironmentVariable("ANIMA_LAUNCHER_DIRECT_NETWORK");
        try
        {
            Environment.SetEnvironmentVariable("ANIMA_LAUNCHER_DIRECT_NETWORK", null);
            return await RunIsolated(work, manifest);
        }
        finally { Environment.SetEnvironmentVariable("ANIMA_LAUNCHER_DIRECT_NETWORK", previous); }
    }

    private static async Task<int> RunIsolated(string work, ModelManifest? manifest)
    {
        var passed = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL: " + label); passed++; Console.WriteLine("PASS: " + label); }
        async Task Reject<T>(Func<Task> action, string label) where T : Exception
        {
            try { await action(); } catch (T) { Check(true, label); return; }
            throw new Exception("Expected " + typeof(T).Name + ": " + label);
        }
        var china = new LauncherConfig();
        var official = new LauncherConfig { SourceId = "official" };
        var git = "https://github.com/Comfy-Org/ComfyUI.git";
        var hf = "https://huggingface.co/owner/model/resolve/" + new string('a', 40) + "/model.bin";
        var mapping = new ModelScopeSource("owner/model", new string('b', 40), "weights/model.bin");
        var bytes = Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray();
        var sha = Convert.ToHexString(SHA256.HashData(bytes));
        var file = new ModelFile("model.bin", bytes.Length, sha, [hf]) { ModelScope = mapping };
        Check(china.SourceId == "china" && DownloadSources.Candidates(git, china).SequenceEqual([
            "https://gh-proxy.com/" + git, git]), "new configurations prefer the China Git mirror and retain official fallback");
        Check(DownloadSources.Candidates(git, official).SequenceEqual([git]), "explicit old official configurations retain official downloads");
        Check(DownloadSources.Candidates("https://download.pytorch.org/whl/cu128/torch.whl", china).SequenceEqual([
            "https://mirror.sjtu.edu.cn/pytorch-wheels/cu128/torch.whl", "https://download.pytorch.org/whl/cu128/torch.whl"]),
            "Torch CUDA wheel candidates use the matching Shanghai mirror path");
        Check(DownloadSources.PipIndexes(china).SequenceEqual([DownloadSources.ChinaPipIndex,
            "https://pypi.tuna.tsinghua.edu.cn/simple", DownloadSources.OfficialPipIndex]), "pip and uv seed receive ordered individual indexes");
        Check(DownloadSources.PythonMirrors(china).SequenceEqual(["https://gh-proxy.com/" + DownloadSources.OfficialPythonMirror,
            DownloadSources.OfficialPythonMirror]), "standalone Python downloads receive their own mirror prefix and official fallback");
        var custom = new LauncherConfig { SourceId = "custom", GitMirror = "https://local.example/{url}",
            ModelMirror = "https://models.example", PipIndex = "https://python.example/simple" };
        Check(DownloadSources.GitCandidates(git, custom).SequenceEqual(["https://local.example/" + git, git])
            && DownloadSources.PipIndexes(custom).SequenceEqual([custom.PipIndex, DownloadSources.OfficialPipIndex]),
            "custom sources retain precedence without injecting China defaults");
        var previousDirect = Environment.GetEnvironmentVariable("ANIMA_LAUNCHER_DIRECT_NETWORK");
        try
        {
            Environment.SetEnvironmentVariable("ANIMA_LAUNCHER_DIRECT_NETWORK", "1");
            var raw = "https://raw.githubusercontent.com/owner/repo/commit/file.txt";
            Check(DownloadSources.GitCandidates(git, china).SequenceEqual([git])
                && DownloadSources.GitCandidates(git, custom).SequenceEqual([git])
                && DownloadSources.Candidates(raw, china).SequenceEqual([raw])
                && DownloadSources.PythonMirrors(china).SequenceEqual([DownloadSources.OfficialPythonMirror]),
                "direct networking skips built-in and custom Git forwarding gateways including Python and raw assets");
            Check(DownloadSources.ModelCandidates(file, china).Length == 3
                && DownloadSources.PipIndexes(china)[0] == DownloadSources.ChinaPipIndex
                && DownloadSources.Candidates("https://download.pytorch.org/whl/cu128/torch.whl", china)[0].StartsWith(DownloadSources.ChinaTorch),
                "direct networking retains native ModelScope, Hugging Face, PyPI and Torch mirrors");
        }
        finally { Environment.SetEnvironmentVariable("ANIMA_LAUNCHER_DIRECT_NETWORK", previousDirect); }
        var modelCandidates = DownloadSources.ModelCandidates(file, china);
        Check(modelCandidates.Length == 3 && modelCandidates[0] == mapping.Url
            && modelCandidates[0].Contains(new string('b', 40)) && !modelCandidates[0].Contains(new string('a', 40))
            && new Uri(modelCandidates[1]).Host == "hf-mirror.com" && modelCandidates[2] == hf,
            "mapped model candidates preserve independent ModelScope revision before HF mirror and official");
        Check(DownloadSources.ModelCandidates(file with { ModelScope = null }, china).Length == 2
            && DownloadSources.ModelCandidates(file, official).SequenceEqual([hf]),
            "unmapped tagger files never invent ModelScope repositories and official mode skips mirrors");
        await Reject<InvalidDataException>(() => { (mapping with { Revision = "master" }).Validate(); return Task.CompletedTask; },
            "ModelScope mappings require immutable revisions");
        await Reject<InvalidDataException>(() => { (mapping with { Path = "../outside" }).Validate(); return Task.CompletedTask; },
            "ModelScope mappings reject path traversal");
        if (manifest is not null)
        {
            var mapped = manifest.Models.Where(model => model.ModelScope is not null).ToArray();
            Check(mapped.Length == 7 && mapped.All(model => model.CanDownload && model.ModelScope!.Revision != model.Revision),
                "all seven verified base and optional models have independent ModelScope mappings");
            foreach (var model in mapped) model.ModelScope!.Validate();
        }

        var directory = Path.Combine(work, "download-sources");
        Directory.CreateDirectory(directory);
        var mirror = "https://mirror.example/model";
        var original = "https://huggingface.co/owner/model";
        HttpResponseMessage Payload(HttpRequestMessage request)
        {
            var offset = (int)(request.Headers.Range?.Ranges.Single().From ?? 0);
            var response = new HttpResponseMessage(offset > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            { Content = new ByteArrayContent(bytes[offset..]) };
            if (offset > 0) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, bytes.Length - 1, bytes.Length);
            return response;
        }
        var progress = new CaptureProgress();
        using (var handler = new Handler(request => request.RequestUri!.Host == "mirror.example"
            ? new(HttpStatusCode.Forbidden) : Payload(request)))
        using (var downloads = new Downloads(_ => "official-secret", handler))
        {
            var target = Path.Combine(directory, "fallback.bin");
            await downloads.Fetch("model", [mirror, original], target, bytes.Length, sha, progress, default);
            Check(handler.Seen.Count == 2 && handler.Seen[0].Auth is null && handler.Seen[1].Auth == "Bearer official-secret"
                && await Downloads.Verify(target, bytes.Length, sha, default), "403 mirror failure falls back without leaking the official credential");
            Check(progress.Values.Any(value => value.Stage.Contains("HTTP 403") && value.Stage.Contains("切换至"))
                && progress.Values.Last().Stage.Contains("Hugging Face 官方"), "progress exposes the active source and fallback reason");
            await downloads.Fetch("model", [mirror, original], target, bytes.Length, sha, progress, default);
            Check(handler.Seen.Count == 2, "verified artifacts still skip all source requests");
        }
        foreach (var status in new[] { HttpStatusCode.NotFound, HttpStatusCode.TooManyRequests, HttpStatusCode.BadGateway })
        {
            using var handler = new Handler(request => request.RequestUri!.Host == "mirror.example" ? new(status) : Payload(request));
            using var downloads = new Downloads(handler: handler);
            await downloads.Fetch("model", [mirror, original], Path.Combine(directory, "status-" + (int)status), bytes.Length, sha, null, default);
            Check(handler.Seen.Count == 2, "source HTTP " + (int)status + " switches immediately to the next source");
        }
        using (var handler = new Handler(request => request.RequestUri!.Host == "mirror.example"
            ? new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[bytes.Length]) } : Payload(request)))
        using (var downloads = new Downloads(handler: handler))
        {
            var target = Path.Combine(directory, "digest.bin");
            await downloads.Fetch("model", [mirror, original], target, bytes.Length, sha, null, default);
            Check(handler.Seen.Count == 2 && await Downloads.Verify(target, bytes.Length, sha, default),
                "wrong mirror digest is discarded before downloading verified official bytes");
        }
        using (var handler = new Handler(request => request.RequestUri!.Host == "mirror.example"
            ? throw new TaskCanceledException("fixture response timeout") : Payload(request)))
        using (var downloads = new Downloads(handler: handler))
        {
            await downloads.Fetch("model", [mirror, original], Path.Combine(directory, "timeout.bin"), bytes.Length, sha, null, default);
            Check(handler.Seen.Count == 2, "source timeouts switch sources without being treated as user cancellation");
        }
        using (var cancel = new CancellationTokenSource())
        using (var handler = new Handler(_ => { cancel.Cancel(); throw new OperationCanceledException(cancel.Token); }))
        using (var downloads = new Downloads(handler: handler))
        {
            await Reject<OperationCanceledException>(() => downloads.Fetch("model", [mirror, original], Path.Combine(directory, "cancel.bin"),
                bytes.Length, sha, null, cancel.Token), "user cancellation propagates");
            Check(handler.Seen.Count == 1, "user cancellation never contacts a fallback source");
        }
        using (var handler = new Handler(Payload))
        using (var downloads = new Downloads(handler: handler))
        {
            var target = Path.Combine(directory, "conflict.bin");
            await File.WriteAllTextAsync(target, "user data");
            await Reject<IOException>(() => downloads.Fetch("model", [mirror, original], target, bytes.Length, sha, null, default),
                "existing conflicting local files remain protected");
            Check(handler.Seen.Count == 0 && await File.ReadAllTextAsync(target) == "user data", "local conflicts do not trigger source fallback");
            var required = new DriveInfo(Path.GetPathRoot(directory)!).AvailableFreeSpace + 1024;
            await Reject<InsufficientSpaceException>(() => downloads.Fetch("model", [mirror, original], Path.Combine(directory, "space.bin"),
                required, sha, null, default), "insufficient local disk space is a local failure");
            Check(handler.Seen.Count == 0, "disk space failure never contacts download sources");
        }
        using (var handler = new Handler(request => request.RequestUri!.Host == "mirror.example"
            ? new(HttpStatusCode.OK) { Content = new StreamContent(new SlowStream(bytes)) } : Payload(request)))
        using (var downloads = new Downloads(handler: handler, policy: new()
        { SlowWindow = TimeSpan.FromMilliseconds(20), MinimumBytesPerSecond = 1024 * 1024 }))
        {
            var target = Path.Combine(directory, "slow.bin");
            var updates = new CaptureProgress();
            await downloads.Fetch("model", [mirror, original], target, bytes.Length, sha, updates, default);
            Check(handler.Seen.Count == 2 && handler.Seen[1].Offset == 512 && handler.Seen[1].IfRange is null,
                "sustained low speed switches source while preserving its partial bytes and dropping the old ETag");
            Check(await Downloads.Verify(target, bytes.Length, sha, default) && updates.Values.Any(value => value.Stage.Contains("持续低速")),
                "low-speed recovery reports the reason and validates the complete artifact");
        }
        using (var handler = new Handler(request => request.RequestUri!.Host == "mirror.example"
            ? new(HttpStatusCode.Forbidden) : Payload(request)))
        using (var downloads = new Downloads(handler: handler))
        {
            var target = Path.Combine(directory, "poisoned-partial.bin");
            await File.WriteAllBytesAsync(target + ".part", new byte[512]);
            JsonFile.Write(target + ".part.json", new ResumeInfo(sha, bytes.Length, mirror, "\"old-mirror\""));
            await downloads.Fetch("model", [mirror, original], target, bytes.Length, sha, null, default);
            Check(handler.Seen.Count == 3 && handler.Seen[1].Offset == 512 && handler.Seen[2].Offset is null
                && await Downloads.Verify(target, bytes.Length, sha, default),
                "a corrupted partial from an earlier mirror gets one clean restart at the final source");
        }
        using (var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new StreamContent(new SlowStream(bytes)) }))
        using (var downloads = new Downloads(handler: handler, policy: new()
        { SlowWindow = TimeSpan.FromMilliseconds(20), MinimumBytesPerSecond = 1024 * 1024 }))
        {
            var target = Path.Combine(directory, "slow-final-source.bin");
            await downloads.Fetch("model", [original], target, bytes.Length, sha, null, default);
            Check(handler.Seen.Count == 1 && await Downloads.Verify(target, bytes.Length, sha, default),
                "the final source remains available at low speed instead of failing without another fallback");
        }
        return passed;
    }

    private sealed class CaptureProgress : IProgress<DownloadProgress>
    {
        public List<DownloadProgress> Values { get; } = [];
        public void Report(DownloadProgress value) => Values.Add(value);
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string? Auth, long? Offset, string? IfRange)> Seen { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add((request.Headers.Authorization?.ToString(), request.Headers.Range?.Ranges.Single().From,
                request.Headers.IfRange?.ToString()));
            return Task.FromResult(respond(request));
        }
    }
    private sealed class SlowStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(70, cancellationToken);
            return await base.ReadAsync(buffer[..Math.Min(512, buffer.Length)], cancellationToken);
        }
    }
}
