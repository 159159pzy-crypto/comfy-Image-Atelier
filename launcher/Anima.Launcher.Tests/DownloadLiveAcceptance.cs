using Anima.Launcher.Core;
using System.Diagnostics;
using System.Text.Json;

/// <summary>Explicit opt-in acceptance against a separately prepared ComfyUI root; never used by the offline suite.</summary>
public static class DownloadLiveAcceptance
{
    public static async Task Run(string comfyRoot, bool package, long pauseAtBytes = 0, string? sourceProfile = null, string? packageId = null)
    {
        comfyRoot = Path.GetFullPath(comfyRoot);
        if (!File.Exists(Path.Combine(comfyRoot, "main.py")) || !File.Exists(Path.Combine(comfyRoot, ".anima-revision.json")))
            throw new IOException("Live acceptance requires an explicitly prepared isolated ComfyUI clone.");
        var root = Environment.CurrentDirectory;
        var manifest = JsonFile.Read<ModelManifest>(Path.Combine(root, package ? "anima_webui/tagger-models.json" : "launcher/manifests/model-manifest.json"));
        var model = manifest.Models.Single(model => model.Id == (package ? packageId ?? "tagger-pixai-tagger-v0.9-timm" : "anima-base-v1"));
        var acceptance = Path.GetDirectoryName(comfyRoot)!;
        var runtime = Path.Combine(acceptance, "runtime");
        var suffix = pauseAtBytes > 0 ? "-pause" : "";
        var reportPath = Path.Combine(acceptance, model.Id + "-download" + suffix + ".json");
        var logPath = Path.Combine(acceptance, model.Id + "-download" + suffix + ".log");
        var destination = Safety.Under(Path.Combine(comfyRoot, "models", model.Category), model.RelativePath);
        var config = new LauncherConfig { ComfyRoot = comfyRoot, RuntimeRoot = runtime, SourceId = sourceProfile ?? "china", NetworkMode = "direct" };
        var options = new DownloadEngineOptions
        {
            Aria2PathResolver = async ct => await BundledTools.EnsureAsync("aria2", runtime, ct),
            NetworkPolicyResolver = () => NetworkPolicySnapshot.Capture(config)
        };
        var samples = new List<object>();
        var clock = Stopwatch.StartNew();
        long lastLog = -5000;
        var sync = new object();
        using var deadline = new CancellationTokenSource(TimeSpan.FromHours(1));
        var progress = new Inline(value =>
        {
            lock (sync)
            {
                if (pauseAtBytes > 0 && value.Received >= pauseAtBytes) deadline.Cancel();
                if (clock.ElapsedMilliseconds - lastLog < 5000 && value.Phase is not ("Completed" or "RetryWaiting" or "Verifying")) return;
                lastLog = clock.ElapsedMilliseconds;
                var sample = new { elapsedSeconds = clock.Elapsed.TotalSeconds, value.Received, value.Total, value.Engine, value.Phase,
                    value.Source, value.Connections, value.Speed, value.Stage };
                samples.Add(sample);
                var line = JsonSerializer.Serialize(sample, JsonFile.Options);
                File.AppendAllText(logPath, line + Environment.NewLine);
                Console.WriteLine($"{clock.Elapsed.TotalSeconds:F1}s {value.Received}/{value.Total} {value.Engine} {value.Source} {value.Stage}");
            }
        });
        using var downloads = new Downloads(engineOptions: options);
        Console.WriteLine($"LIVE DOWNLOAD {model.Id} {model.Size} -> {destination}");
        try
        {
            if (package) await ModelPackages.Fetch(downloads, model, destination, config, progress, deadline.Token);
            else await downloads.Fetch(model.Name, DownloadSources.ModelCandidates(model, config), destination, model.Size, model.Sha256, progress, deadline.Token);
            var verified = package ? await ModelPackages.State(model, destination, deadline.Token) == "已校验"
                : await Downloads.Verify(destination, model.Size, model.Sha256, deadline.Token);
            if (!verified) throw new InvalidDataException("Live artifact did not pass its complete digest verification.");
            JsonFile.Write(reportPath, new { status = "passed", modelId = model.Id, expectedSize = model.Size, expectedSha256 = model.Sha256,
                destination, elapsedSeconds = clock.Elapsed.TotalSeconds, packageFiles = model.Files, samples });
            Console.WriteLine("LIVE DOWNLOAD VERIFIED " + reportPath);
        }
        catch (OperationCanceledException) when (pauseAtBytes > 0 && deadline.IsCancellationRequested)
        {
            var remaining = package ? ModelPackages.RemainingBytes(model, destination) : Downloads.RemainingBytes(destination, model.Size, model.Sha256);
            JsonFile.Write(reportPath, new { status = "paused", modelId = model.Id, destination,
                elapsedSeconds = clock.Elapsed.TotalSeconds, remainingBytes = remaining, retainedBytes = model.Size - remaining, samples });
            Console.WriteLine("LIVE DOWNLOAD PAUSED " + reportPath);
        }
        catch (Exception error)
        {
            JsonFile.Write(reportPath, new { status = "failed", modelId = model.Id, destination,
                elapsedSeconds = clock.Elapsed.TotalSeconds, error = error.Message, samples });
            throw;
        }
    }
    private sealed class Inline(Action<DownloadProgress> action) : IProgress<DownloadProgress> { public void Report(DownloadProgress value) => action(value); }
}
