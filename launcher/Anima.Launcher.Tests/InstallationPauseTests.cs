using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Anima.Launcher.Core;

internal static class InstallationPauseTests
{
    // Exercise the real fresh-install preflight and tool stage. Stop before Git cloning a repository.
    public static async Task Run(string work, RuntimeManifest shipped)
    {
        var state = Directory.CreateDirectory(Path.Combine(work, "five-pauses")).FullName;
        var runtime = Path.Combine(state, "runtime");
        var config = new LauncherConfig { ComfyRoot = Path.Combine(state, "environment"), RuntimeRoot = runtime,
            SourceId = "official", NetworkMode = "direct", ComfyPort = FreePort(), WebPort = FreePort() };
        while (config.WebPort == config.ComfyPort) config.WebPort = FreePort();
        if (Commands.Find("git.exe") is null) throw new InvalidOperationException("This fresh-install fixture requires Git on PATH.");
        using var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, true))
        using (var entry = zip.CreateEntry("uv.exe", CompressionLevel.NoCompression).Open()) entry.Write(new byte[1024 * 1024]);
        var bytes = archive.ToArray();
        var sha = Convert.ToHexString(SHA256.HashData(bytes));
        var tool = new Artifact("uv", "https://qa.invalid/uv.zip", bytes.Length, sha, "uv.exe");
        var manifest = shipped with { Tools = [tool], Nodes = [], TorchWheels = [] };
        var plan = new InstallationPlan(JsonSerializer.Serialize(config, JsonFile.Options), config.ComfyRoot, runtime,
            "anima", true, 0, 0, 0, []) { ResolvedRuntime = manifest };
        var file = ArtifactCache.PathFor(runtime, tool);
        string? ownerId = null;
        long retained = 0;
        const int pauses = 5;
        for (var attempt = 0; attempt <= pauses; attempt++)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var transport = new RangeSource(bytes, attempt < pauses);
            using var downloads = new Downloads(handler: transport);
            await using var tasks = new DownloadTaskService(downloads, state);
            var remaining = bytes.Length - retained;
            var payload = JsonSerializer.SerializeToElement(plan with { EstimatedDownloadBytes = remaining }, JsonFile.Options);
            var owner = tasks.RegisterOwner("setup", "fixture install", config, payload: payload);
            ownerId ??= owner.Id;
            Require(owner.Id == ownerId && tasks.Owners.Count == 1, "resumed install created a duplicate owner");
            var installer = new Installer(state, manifest, downloads, _ => { }, null, setupProgress: update =>
            {
                if (update.StageId == "comfy" && update.Status == SetupStageStatus.Running)
                    throw new ToolStageFinished();
            });
            var run = tasks.RunOwnerAsync(owner.Id, async ct =>
            {
                try { await installer.Install(plan, ct); }
                catch (ToolStageFinished) when (attempt == pauses) { }
            }, timeout.Token);
            if (attempt < pauses)
            {
                if (await Task.WhenAny(run, transport.Waiting.Task).WaitAsync(timeout.Token) == run) await run;
                await tasks.PauseOwnerAsync(owner.Id);
                try { await run; throw new InvalidOperationException("install unexpectedly completed while paused"); }
                catch (OperationCanceledException) when (!timeout.IsCancellationRequested) { }
                var next = new FileInfo(file + ".part").Length;
                Require(transport.From == retained && next > retained && next < bytes.Length, "wrong downloaded prefix or HTTP Range");
                Require(JsonFile.Read<InstallJournal>(Path.Combine(state, "install-state.json")).Status == "interrupted"
                    && tasks.Owners.Single().Status == DownloadTaskStatus.Paused && !File.Exists(file), "pause was not persisted");
                Console.WriteLine($"PASS: pause {attempt + 1} persisted owner {owner.Id} and bytes {retained}..{next}");
                retained = next;
            }
            else
            {
                await run;
                Require(transport.From == retained && await Downloads.Verify(file, bytes.Length, sha, default)
                    && ToolInstallation.IsReady(runtime, tool) && !File.Exists(file + ".part"), "resumed tool publication failed");
                Require(JsonFile.Read<InstallJournal>(Path.Combine(state, "install-state.json")).Status == "complete"
                    && tasks.Tasks.Count == 1 && tasks.Owners.Single().Status == DownloadTaskStatus.Completed, "final stage or owner missing");
                Console.WriteLine("PASS: sixth installer run completed the tool stage and verified the archive after five durable pauses");
            }
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private sealed class ToolStageFinished : Exception;
    private sealed class RangeSource(byte[] bytes, bool pause) : HttpMessageHandler
    {
        public long From { get; private set; } = -1;
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Require(request.RequestUri?.Host == "qa.invalid", "unexpected fixture source");
            From = request.Headers.Range?.Ranges.Single().From ?? 0;
            var response = new HttpResponseMessage(From == 0 ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
                { Content = new StreamContent(new Chunks(bytes, (int)From, pause, Waiting)) };
            response.Content.Headers.ContentLength = bytes.Length - From;
            if (From > 0) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(From, bytes.Length - 1, bytes.Length);
            return Task.FromResult(response);
        }
    }
    private sealed class Chunks(byte[] bytes, int offset, bool pause, TaskCompletionSource waiting)
        : MemoryStream(bytes, offset, bytes.Length - offset, false)
    {
        private bool sent;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (pause && sent)
            {
                waiting.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            sent = true;
            return await base.ReadAsync(buffer[..Math.Min(buffer.Length, 65536)], ct);
        }
    }
}
