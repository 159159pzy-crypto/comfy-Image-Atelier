using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Anima.Launcher.Core;

internal static class TaskStoreTests
{
    public static async Task<int> Run(string work)
    {
        var count = 0;
        void Check(bool condition, string label) { if (!condition) throw new Exception("FAIL: " + label); count++; Console.WriteLine("PASS: " + label); }
        void Fails(Action action, string label) { try { action(); } catch (InvalidDataException) { Check(true, label); return; } catch (InvalidOperationException) { Check(true, label); return; } throw new Exception("Expected rejection: " + label); }
        var root = Path.Combine(work, "task-store-" + Guid.NewGuid().ToString("N"));
        var body = "frozen download fixture"u8.ToArray();
        var sha = Convert.ToHexString(SHA256.HashData(body));
        var model = new ModelItem("fixture", "fixture", "fixture", "diffusion_models", "model.bin", [], body.Length, sha,
            ["https://example.com/model.bin"], "fixture", "");
        var config = new LauncherConfig { ComfyRoot = Path.Combine(root, "environment"), NetworkMode = "direct" };
        var destination = Path.Combine(config.ComfyRoot, "models", "model.bin");
        using var handler = new CountingHandler(body);
        using var downloads = new Downloads(handler: handler);
        await using var service = new DownloadTaskService(downloads, root);
        var owner = service.RegisterOwner("models", "frozen models", config, [new(model, destination)]);
        Check(handler.Requests == 0 && owner.Status == DownloadTaskStatus.Paused, "creating durable intent does not start networking");
        var duplicate = service.RegisterOwner("models", "renamed display", config, [new(model, destination)]);
        Check(duplicate.Id == owner.Id && service.Owners.Count == 1, "same frozen owner deduplicates without a caller-maintained id");
        var snapshot = service.Owners.Single(); snapshot.Configuration.ComfyRoot = "changed"; snapshot.Models[0].Model.Urls[0] = "https://other.example/changed";
        Check(service.Owners.Single().Configuration.ComfyRoot == config.ComfyRoot
            && service.Owners.Single().Models[0].Model.Urls[0] == model.Urls[0], "UI owner snapshots cannot mutate persisted identities");
        Fails(() => service.RegisterOwner("models", "changed", config, [new(model with { Size = body.Length + 1 }, destination)], id: owner.Id),
            "changing size or version cannot reuse an owner id");
        var signedModel = model with { Urls = ["https://example.com/file?X-Amz-Signature=private-value"] };
        Fails(() => service.RegisterOwner("models", "signed", config, [new(signedModel, destination)]), "signed input URLs are rejected before persistence");
        Fails(() => service.RegisterOwner("software", "secret payload", config, payload: JsonSerializer.SerializeToElement(new { apiKey = "secret-value" })),
            "opaque owner payload cannot persist credentials");
        Fails(() => service.RegisterOwner("software", "signed payload", config,
            payload: JsonSerializer.SerializeToElement(new { url = "https://example.com/file?Signature=private-value" })),
            "opaque owner payload cannot persist signed CDN URLs");
        Fails(() => service.RegisterOwner("models", "signed display source", config,
            [new(model with { SourceUrl = "https://example.com/file?Signature=private-value" }, destination)]),
            "signed URLs in model display metadata are rejected before registration");
        var signedConfiguration = config.Clone(); signedConfiguration.ModelMirror = "https://example.com/?Signature=private-value";
        Fails(() => service.RegisterOwner("models", "signed configuration", signedConfiguration, [new(model, destination)]),
            "signed URLs in owner source configuration are rejected before registration");
        Check(service.Owners.Count == 1, "rejected registrations do not poison the durable task database");

        var next = config.Clone(); next.ComfyRoot = Path.Combine(root, "other-environment"); next.SelectedProfile = "krea2";
        next.SourceId = "official"; next.ModelMirror = "https://mirror.example";
        var switched = service.UpdateOwnerSources(owner.Id, next);
        Check(switched.Configuration.ComfyRoot == config.ComfyRoot && switched.Configuration.SelectedProfile == config.SelectedProfile
            && switched.Models[0].Destination == destination && switched.Models[0].Model.Sha256 == sha
            && switched.Configuration.SourceId == "official", "explicit source switch preserves environment, profile, destination and artifact identity");
        Check(switched.TransportRevision == 2, "source switch is a recorded transport revision");
        await service.RunOwnerAsync(owner.Id, async ct =>
        {
            await downloads.Fetch(model.Name, model.Urls, destination, model.Size, model.Sha256, null, ct);
            Check(service.Owners.Single().CompletedModelIds.Count == 0, "completed file alone does not mark a model or package published");
            service.MarkModelCompleted(owner.Id, model.Id);
        });
        Check(service.Owners.Single().CompletedModelIds.SequenceEqual([model.Id]), "coordinator records model completion only after final publication work");
        Check(service.Tasks.Single().Network?.Mode == "direct", "owner captures frozen network policy instead of mutable defaults");
        Check(service.Owners.Single().Status == DownloadTaskStatus.Completed, "only successful owner coordination records completion");
        var path = Path.Combine(root, "download-tasks.json");
        Check(!File.ReadAllText(path).Contains("private-value", StringComparison.Ordinal) && !File.ReadAllText(path).Contains("secret-value", StringComparison.Ordinal),
            "task JSON contains neither rejected tokens nor signed URLs");

        await service.DisposeAsync();
        var store = new DownloadTaskStore(root);
        var saved = store.Load(); saved.Tasks[0].Status = DownloadTaskStatus.Running; saved.Owners[0].Status = DownloadTaskStatus.Running;
        saved.Tasks[0].Error = "磁盘写入失败，需要处理。"; saved.Owners[0].Error = "包发布失败，需要保留原目录检查。"; store.Save(saved);
        var before = handler.Requests;
        using var restoredHandler = new CountingHandler(body);
        using var restoredDownloads = new Downloads(handler: restoredHandler);
        await using var restored = new DownloadTaskService(restoredDownloads, root);
        Check(restored.Tasks.Single().Status == DownloadTaskStatus.Paused && restored.Owners.Single().Status == DownloadTaskStatus.Paused
            && handler.Requests == before && restoredHandler.Requests == 0, "restart recovers interrupted list as paused without resuming any network request");
        Check(restored.Owners.Single().Models[0].Model.Sha256 == sha && restored.Tasks.Single().Destination == destination,
            "restart preserves exact artifact and destination");
        Check(restored.Tasks.Single().Error == saved.Tasks[0].Error && restored.Owners.Single().Error == saved.Owners[0].Error,
            "restart preserves the actual last file and owner errors while marking them paused");
        await restored.CancelOwnerAsync(owner.Id);
        var replacement = restored.RegisterOwner("models", "new request", config, [new(model, destination)]);
        Check(replacement.Id != owner.Id && restored.Owners.Single(o => o.Id == owner.Id).Status == DownloadTaskStatus.Canceled,
            "new explicit request does not silently reactivate a canceled owner");

        var corruptRoot = Path.Combine(root, "corrupt"); Directory.CreateDirectory(corruptRoot);
        var corruptPath = Path.Combine(corruptRoot, "download-tasks.json"); File.WriteAllText(corruptPath, "{broken");
        var corruptStore = new DownloadTaskStore(corruptRoot); _ = corruptStore.Load();
        Check(corruptStore.LoadError is not null && File.ReadAllText(corruptPath) == "{broken", "corrupt task store is preserved with a readable recovery warning");
        try { corruptStore.Save(new()); throw new Exception("Expected write refusal"); }
        catch (IOException) { Check(File.ReadAllText(corruptPath) == "{broken", "failed recovery does not overwrite the original task store"); }
        var changed = store.Load(); changed.Tasks[0].Destination = Path.Combine(root, "redirected.bin");
        Fails(() => store.Save(changed), "tampered stored destination is rejected by immutable fingerprint");
        await EnvironmentReviews(Path.Combine(root, "historic-reviews"), config, Check);
        return count;
    }

    private static async Task EnvironmentReviews(string state, LauncherConfig config, Action<bool, string> check)
    {
        var plan = new InstallationPlan(JsonSerializer.Serialize(config, JsonFile.Options), config.ComfyRoot, config.RuntimeRoot,
            config.SelectedProfile, false, 10, 0, 10, []);
        DownloadOwnerRecord Owner(InstallationPlan review, string kind = "setup")
        {
            var item = new DownloadOwnerRecord { Kind = kind, Title = kind, Configuration = review.CreateConfig(),
                EnvironmentRoot = review.ComfyRoot, Payload = JsonSerializer.SerializeToElement(review, JsonFile.Options) };
            item.Fingerprint = DownloadTaskIdentity.Owner(item);
            return item;
        }
        var baseline = Owner(plan);
        var progress = Owner(plan with { EstimatedDownloadBytes = 1 });
        // Old retries can update an obsolete record after the replacement has been registered.
        baseline.UpdatedAt = DateTimeOffset.UtcNow.AddDays(1);
        var revised = plan with { ReviewedDependencyAdditions = new(new string('b', 64), ["node-extension==1"]) };
        var accepted = Owner(revised);
        var feature = Owner(plan with { Purpose = FeaturePreparation.Purpose, RequestedChoices = ["prompt-assistant"] }, "install");
        var otherConfig = config.Clone(); otherConfig.ComfyRoot = Path.Combine(state, "other");
        var other = Owner(plan with { ComfyRoot = otherConfig.ComfyRoot, ConfigurationJson = JsonSerializer.Serialize(otherConfig, JsonFile.Options) });
        DownloadTaskRecord FileTask(string name, params string[] owners)
        {
            var item = new DownloadTaskRecord { Name = name, Destination = Path.Combine(state, name), Sha256 = new string('a', 64),
                Size = 3, Urls = ["https://example.com/" + name], Status = DownloadTaskStatus.Paused, OwnerIds = [.. owners] };
            item.Fingerprint = DownloadTaskIdentity.Artifact(item.Request());
            return item;
        }
        var onlyOld = FileTask("obsolete.whl", baseline.Id);
        var shared = FileTask("shared.whl", baseline.Id, feature.Id);
        var persisted = new DownloadTaskStore(state);
        persisted.Save(new() { Owners = [baseline, progress, feature, other, accepted], Tasks = [onlyOld, shared] });
        using var downloads = new Downloads();
        await using var service = new DownloadTaskService(downloads, state);
        var history = service.GetSnapshot();
        check(history.Owners.Count == 4 && history.Owners.All(o => o.Id != baseline.Id)
            && history.Tasks.All(t => t.OwnerIds.Contains(progress.Id) && !t.OwnerIds.Contains(baseline.Id)),
            "legacy duplicate progress receipts merge by registration order and retain child file ownership");
        var retired = history.Owners.Single(o => o.Id == progress.Id);
        check(retired.SupersededBy == accepted.Id && retired.Status == DownloadTaskStatus.Canceled
            && history.Owners.Where(o => o.Id != progress.Id).All(o => o.SupersededBy == null && o.Status == DownloadTaskStatus.Paused),
            "legacy review with newer approved packages supersedes only this environment installation");
        check(history.Tasks.Single(t => t.Id == onlyOld.Id).Status == DownloadTaskStatus.Canceled
            && history.Tasks.Single(t => t.Id == shared.Id).Status == DownloadTaskStatus.Paused,
            "superseded exclusive file reservations are released and shared downloads remain resumable");
        async Task Refuses(string id)
        {
            try { await service.RunOwnerAsync(id, _ => throw new Exception("stale coordinator executed")); }
            catch (InvalidOperationException error) when (error.Message.Contains("最新")) { return; }
            throw new Exception("superseded owner accepted");
        }
        await Refuses(retired.Id);
        check(service.RegisterOwner("setup", "resume", config, payload: baseline.Payload, resumeEnvironmentReview: true).Id == accepted.Id,
            "a stale wizard receipt recovers the latest approved plan without registering an obsolete review");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = service.RunOwnerAsync(accepted.Id, async _ => { started.SetResult(); await stop.Task; });
        await started.Task;
        var next = revised with { ExternalRepairBackupId = Guid.NewGuid().ToString("N") };
        DownloadOwnerRecord Confirm() => service.RegisterOwner("install", "repair review", config, payload: JsonSerializer.SerializeToElement(next, JsonFile.Options));
        try
        {
            try { _ = Confirm(); throw new Exception("concurrent replacement accepted"); }
            catch (InvalidOperationException error) when (error.Message.Contains("暂停"))
            { check(service.Owners.Count == 4, "active environment review cannot be replaced during execution"); }
        }
        finally { stop.SetResult(); await active; }
        var latest = Confirm();
        await Refuses(accepted.Id);
        check(latest.Id != accepted.Id && service.Owners.Single(o => o.Id == accepted.Id).Status == DownloadTaskStatus.Completed
            && service.Owners.Single(o => o.Id == accepted.Id).SupersededBy == latest.Id,
            "confirming an external repair creates a new owner and preserves the completed history");
        await service.DisposeAsync();
        using var newDownloads = new Downloads();
        await using var restarted = new DownloadTaskService(newDownloads, state);
        var candidates = restarted.Owners.Where(o => o.Status is DownloadTaskStatus.Paused or DownloadTaskStatus.Failed or DownloadTaskStatus.NeedsReview)
            .Select(o => o.Id).ToHashSet();
        check(restarted.RecoveryWarning is null && candidates.SetEquals([latest.Id, feature.Id, other.Id])
            && restarted.RegisterOwner("setup", "resume", config, payload: baseline.Payload, resumeEnvironmentReview: true).Id == latest.Id,
            "after restart bulk continue runs only the current review plus independent features and environments");
    }

    private sealed class CountingHandler(byte[] body) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }
}
