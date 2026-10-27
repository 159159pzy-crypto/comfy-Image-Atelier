using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anima.Launcher.Core;

internal static class ResourceOwnerTests
{
    public static async Task<int> Run(string work)
    {
        var count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); count++; Console.WriteLine("PASS: " + message); }
        async Task Interrupted(Task task) { try { await task; throw new Exception("Expected interrupted transfer"); } catch (OperationCanceledException) { } }
        var root = Path.Combine(work, "resource-owners-" + Guid.NewGuid().ToString("N"));
        var bytes = "resource owner fixture"u8.ToArray();
        var sha = Convert.ToHexString(SHA256.HashData(bytes));
        ModelItem Model(string id) => new(id, id, "fixture", "diffusion_models", id + ".bin", [], bytes.Length, sha,
            ["https://example.com/" + id + ".bin"], "fixture", "");
        var first = Model("first"); var second = Model("second");
        var package = Model("package") with { Category = "wd_taggers", RelativePath = "package", Size = bytes.Length * 2, Sha256 = "", Urls = [],
            Files = [new("config.json", bytes.Length, sha, ["https://example.com/config.json"]), new("weights.bin", bytes.Length, sha, ["https://example.com/weights.bin"])],
            Revision = new string('a', 40) };
        var config = new LauncherConfig { ComfyRoot = Path.Combine(root, "environment"), NetworkMode = "direct" };
        ModelDownloadIntent Intent(ModelItem model) => new(model, Path.Combine(config.ComfyRoot, "models", model.Category, model.RelativePath));
        var intents = new[] { Intent(first), Intent(second), Intent(package) };
        var state = Path.Combine(root, "legacy-state");
        string parentId;
        using (var handler = new GateHandler(bytes))
        using (var downloads = new Downloads(handler: handler))
        await using (var tasks = new DownloadTaskService(downloads, state, 4))
        {
            var parent = tasks.RegisterOwner("models", "legacy multi-model batch", config, intents,
                JsonSerializer.SerializeToElement(new { frozenContext = "retained" }));
            parentId = parent.Id;
            var run = tasks.RunOwnerAsync(parent.Id, ct => Task.WhenAll(intents.SelectMany(intent => intent.Model.IsPackage
                ? intent.Model.Files.Select(file => downloads.Fetch(intent.Model.Name, file.Urls,
                    Safety.Under(ModelPackages.Staging(intent.Model, intent.Destination), file.Path), file.Size, file.Sha256, null, ct))
                : [downloads.Fetch(intent.Model.Name, intent.Model.Urls, intent.Destination, intent.Model.Size, intent.Model.Sha256, null, ct)])));
            await handler.WaitStarted("first.bin"); await handler.WaitStarted("weights.bin");
            try { tasks.EnsureResourceOwners(parent.Id); throw new Exception("Expected live split rejection"); }
            catch (InvalidOperationException) { Check(tasks.Owners.Count == 1, "live legacy batch cannot be split underneath its existing coordinator"); }
            await tasks.PauseOwnerAsync(parent.Id); await Interrupted(run);
        }
        var store = new DownloadTaskStore(state);
        var saved = store.Load();
        var original = saved.Owners.Single(); original.Status = DownloadTaskStatus.Failed; original.Error = "legacy failure";
        original.CompletedModelIds.Add(first.Id);
        var firstFile = saved.Tasks.Single(task => task.Destination == intents[0].Destination);
        firstFile.Status = DownloadTaskStatus.Completed; firstFile.Received = firstFile.Size;
        var canceledFile = saved.Tasks.Single(task => task.Destination == intents[1].Destination);
        canceledFile.Status = DownloadTaskStatus.Canceled; canceledFile.CancellationRequested = true;
        var packageFile = saved.Tasks.Single(task => Path.GetFileName(task.Destination) == "config.json");
        packageFile.Status = DownloadTaskStatus.Completed; packageFile.Received = packageFile.Size;
        var failedFile = saved.Tasks.Single(task => Path.GetFileName(task.Destination) == "weights.bin");
        failedFile.Status = DownloadTaskStatus.Failed; failedFile.Error = "fixture connection failed";
        store.Save(saved);
        // Exercise the actual old JSON shape, with no resource-owner relationship field.
        var legacyJson = JsonNode.Parse(File.ReadAllText(store.FilePath))!;
        foreach (var node in legacyJson["owners"]!.AsArray()) node!.AsObject().Remove("resourceOwnerIds");
        File.WriteAllText(store.FilePath, legacyJson.ToJsonString());

        using (var handler = new GateHandler(bytes))
        using (var downloads = new Downloads(handler: handler))
        await using (var tasks = new DownloadTaskService(downloads, state, 4))
        {
            Check(tasks.RecoveryWarning is null && tasks.Owners.Single().ResourceOwnerIds.Count == 0,
                "legacy task JSON without resource links loads without changing its frozen identity");
            var children = tasks.EnsureResourceOwners(parentId);
            var parent = tasks.Owners.Single(owner => owner.Id == parentId);
            Check(children.Length == 3 && parent.ResourceOwnerIds.Count == 3 && tasks.Owners.Count == 4 && handler.Requests == 0,
                "legacy split atomically creates three independent model owners without networking");
            Check(children.All(child => child.Configuration.ComfyRoot == config.ComfyRoot && child.Payload?.GetProperty("frozenContext").GetString() == "retained")
                && children.Select(child => child.Models.Single().Destination).SequenceEqual(intents.Select(intent => intent.Destination)),
                "resource split retains the frozen environment, package identity, payload and destination");
            Check(tasks.EnsureResourceOwners(parentId).Select(child => child.Id).SequenceEqual(children.Select(child => child.Id)) && tasks.Owners.Count == 4,
                "repeated legacy split reuses exactly the persisted child owners");
            var complete = children.Single(child => child.Models[0].Model.Id == first.Id);
            var canceled = children.Single(child => child.Models[0].Model.Id == second.Id);
            var pendingPackage = children.Single(child => child.Models[0].Model.Id == package.Id);
            Check(complete.Status == DownloadTaskStatus.Completed && canceled.Status == DownloadTaskStatus.Canceled && canceled.CancellationRequested
                && pendingPackage.Status == DownloadTaskStatus.Failed && pendingPackage.Error.Contains("fixture connection failed"),
                "split preserves published models, canceled intent and the actual recoverable resource error");
            Check(tasks.Tasks.All(task => task.OwnerIds.Contains(parentId) && children.Count(child => task.OwnerIds.Contains(child.Id)) == 1),
                "old files are associated only with their exact resource child while preserving legacy history");
            Check(parent.CompletedModelIds.SequenceEqual([first.Id]) && parent.Status == DownloadTaskStatus.Failed && !parent.CancellationRequested,
                "one canceled model does not make another failed model unrecoverable through the legacy group");
            var failedSnapshot = tasks.GetSnapshot();
            Check(ModelDownloadProjection.ForModel(config.ComfyRoot, package, intents[2].Destination, failedSnapshot.Owners, failedSnapshot.Tasks)!.CanRetry,
                "failed resource still exposes retry after process restart and legacy migration");
            var snapshot = tasks.GetSnapshot(); snapshot.Owners.Single(owner => owner.Id == parentId).ResourceOwnerIds.Clear();
            Check(tasks.GetSnapshot().Owners.Single(owner => owner.Id == parentId).ResourceOwnerIds.Count == 3,
                "consistent task snapshot is detached from mutable persistent relationships");
            var explicitRequest = tasks.RegisterOwner("models", "new explicit request", config, [intents[0]]);
            var replacementUrls = new[] { "https://new-source.example/first.bin" };
            handler.Release("first.bin");
            await tasks.RunOwnerAsync(explicitRequest.Id, ct => downloads.Fetch(first.Name, replacementUrls, intents[0].Destination,
                first.Size, first.Sha256, null, ct));
            Check(tasks.Tasks.Single(task => task.Destination == intents[0].Destination).Urls.SequenceEqual(replacementUrls),
                "unfinished legacy sibling cannot prevent a fresh explicit model request from adopting its new source");
            handler.Release("config.json"); handler.Release("weights.bin");
            await tasks.RunOwnerAsync(pendingPackage.Id, async ct =>
            {
                await ModelPackages.Fetch(downloads, package, intents[2].Destination, config, null, ct);
                Check(!tasks.Owners.Single(owner => owner.Id == parentId).CompletedModelIds.Contains(package.Id),
                    "verified child files alone do not mark the legacy package published");
                tasks.MarkModelCompleted(pendingPackage.Id, package.Id);
            });
            var final = tasks.GetSnapshot();
            var displayed = ModelDownloadProjection.ForModel(config.ComfyRoot, package, intents[2].Destination, final.Owners, final.Tasks)!;
            Check(displayed.IsCompleted && displayed.Error.Length == 0 && displayed.OwnerId == pendingPackage.Id
                && final.Owners.Single(owner => owner.Id == parentId).CompletedModelIds.Contains(package.Id),
                "resource completion clears its stale legacy error and updates the parent completion count");
            Check(final.Owners.Single(owner => owner.Id == canceled.Id).Status == DownloadTaskStatus.Canceled && handler.Count("second.bin") == 0,
                "continuing a sibling model never resurrects an explicitly canceled model");
            Check(store.Load().Owners.Single(owner => owner.Id == parentId).ResourceOwnerIds[package.Id] == pendingPackage.Id,
                "resource relationships and aggregate completion survive a real store round trip");
            try { await tasks.RunOwnerAsync(parentId, _ => Task.CompletedTask); throw new Exception("Expected legacy coordinator rejection"); }
            catch (InvalidOperationException) { Check(true, "converted parent cannot restart a multi-model coordinator"); }
        }

        var atomicState = Path.Combine(root, "atomic-state");
        using (var handler = new GateHandler(bytes))
        using (var downloads = new Downloads(handler: handler))
        await using (var tasks = new DownloadTaskService(downloads, atomicState))
        {
            var parent = tasks.RegisterOwner("models", "atomic split", config, intents.Take(2));
            var file = Path.Combine(atomicState, "download-tasks.json");
            var originalJson = File.ReadAllText(file);
            var blockedTemporary = file + ".tmp"; Directory.CreateDirectory(blockedTemporary);
            try { tasks.EnsureResourceOwners(parent.Id); throw new Exception("Expected atomic write failure"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Check(tasks.Owners.Count == 1 && tasks.Owners.Single().ResourceOwnerIds.Count == 0 && File.ReadAllText(file) == originalJson,
                    "failed resource migration restores the entire in-memory state and leaves original disk JSON intact");
            }
            Directory.Delete(blockedTemporary);
            await tasks.DisposeAsync();
            // A process can exit while writing the atomic temporary file. The committed
            // JSON remains authoritative when a fresh service instance starts.
            await File.WriteAllTextAsync(blockedTemporary, "{\"owners\":[{\"interrupted\":");
            using var restartedHandler = new GateHandler(bytes);
            using var restartedDownloads = new Downloads(handler: restartedHandler);
            await using var restarted = new DownloadTaskService(restartedDownloads, atomicState);
            Check(restarted.Owners.Count == 1 && restarted.Owners.Single().Id == parent.Id
                && restarted.Owners.Single().ResourceOwnerIds.Count == 0 && restartedHandler.Requests == 0,
                "restart after interrupted migration ignores incomplete temporary JSON and restores the original unsplit intent");
            var children = restarted.EnsureResourceOwners(parent.Id);
            Check(children.Length == 2 && restarted.Owners.Count == 3
                && restarted.EnsureResourceOwners(parent.Id).Select(child => child.Id).SequenceEqual(children.Select(child => child.Id)),
                "interrupted legacy migration can be retried after restart without losing or duplicating model owners");
            foreach (var child in children)
                await restarted.RunOwnerAsync(child.Id, _ => { restarted.MarkModelCompleted(child.Id, child.Models[0].Model.Id); return Task.CompletedTask; });
            Check(restarted.Owners.Single(owner => owner.Id == parent.Id).Status == DownloadTaskStatus.Completed
                && restarted.Owners.Single(owner => owner.Id == parent.Id).Error.Length == 0,
                "only all published resources complete the legacy parent and clear its error");
            var tampered = new DownloadTaskStore(atomicState).Load();
            tampered.Owners.Single(owner => owner.Id == parent.Id).ResourceOwnerIds[first.Id] = children[1].Id;
            try { new DownloadTaskStore(atomicState).Save(tampered); throw new Exception("Expected invalid link rejection"); }
            catch (InvalidDataException) { Check(true, "store rejects a child relationship that redirects a model to another resource"); }
        }

        using (var handler = new GateHandler(bytes))
        using (var downloads = new Downloads(handler: handler))
        await using (var tasks = new DownloadTaskService(downloads, Path.Combine(root, "independent-state"), 4))
        {
            var packageIntent = intents[2] with { Destination = Path.Combine(root, "independent-package") };
            var packageOwner = tasks.RegisterOwner("models", "independent package", config, [packageIntent]);
            var siblingIntent = intents[1] with { Destination = Path.Combine(root, "independent-sibling.bin") };
            var siblingOwner = tasks.RegisterOwner("models", "independent sibling", config, [siblingIntent]);
            var packageRun = tasks.RunOwnerAsync(packageOwner.Id, ct => ModelPackages.Fetch(downloads, package, packageIntent.Destination, config, null, ct));
            var siblingRun = tasks.RunOwnerAsync(siblingOwner.Id, ct => downloads.Fetch(second.Name, second.Urls, siblingIntent.Destination, second.Size, second.Sha256, null, ct));
            await handler.WaitStarted("config.json"); await handler.WaitStarted("weights.bin"); await handler.WaitStarted("second.bin");
            await tasks.PauseOwnerAsync(packageOwner.Id); await Interrupted(packageRun);
            Check(tasks.Tasks.Where(task => task.OwnerIds.Contains(packageOwner.Id)).All(task => task.Status == DownloadTaskStatus.Paused)
                && tasks.Owners.Single(owner => owner.Id == siblingOwner.Id).Status == DownloadTaskStatus.Running && !siblingRun.IsCompleted,
                "pausing a package stops every package member while the separately owned sibling keeps downloading");
            handler.Release("second.bin"); await siblingRun;
            Check(tasks.Owners.Single(owner => owner.Id == siblingOwner.Id).Status == DownloadTaskStatus.Completed,
                "independent sibling can finish while the package remains paused");
        }
        var sharedState = Path.Combine(root, "shared-resource-state");
        string sharedParentId;
        string sharedChildId;
        using (var handler = new GateHandler(bytes))
        using (var downloads = new Downloads(handler: handler))
        await using (var tasks = new DownloadTaskService(downloads, sharedState))
        {
            var explicitOwner = tasks.RegisterOwner("models", "existing single model", config, [intents[0]]);
            sharedChildId = explicitOwner.Id;
            var singleRun = tasks.RunOwnerAsync(explicitOwner.Id, ct => Task.Delay(Timeout.Infinite, ct));
            var legacy = tasks.RegisterOwner("models", "old batch with active shared model", config, intents.Take(2));
            sharedParentId = legacy.Id;
            var children = tasks.EnsureResourceOwners(legacy.Id);
            Check(children.Single(child => child.Models[0].Model.Id == first.Id).Id == explicitOwner.Id && tasks.Owners.Count == 3,
                "legacy split reuses an already running single-model owner instead of creating a duplicate coordinator");
            Check(tasks.Owners.Single(owner => owner.Id == legacy.Id).Status == DownloadTaskStatus.Running,
                "legacy group reflects its reused active resource owner");
            await tasks.PauseOwnerAsync(explicitOwner.Id); await Interrupted(singleRun);
        }
        var restartStore = new DownloadTaskStore(sharedState);
        var restartData = restartStore.Load();
        restartData.Owners.Single(owner => owner.Id == sharedChildId).Status = DownloadTaskStatus.Running;
        restartData.Owners.Single(owner => owner.Id == sharedParentId).Status = DownloadTaskStatus.Running;
        restartStore.Save(restartData);
        using (var handler = new GateHandler(bytes))
        using (var downloads = new Downloads(handler: handler))
        await using (var tasks = new DownloadTaskService(downloads, sharedState))
        {
            var parent = tasks.Owners.Single(owner => owner.Id == sharedParentId);
            Check(parent.Status == DownloadTaskStatus.Paused && parent.ResourceOwnerIds[first.Id] == sharedChildId
                && tasks.EnsureResourceOwners(parent.Id).All(child => child.Status == DownloadTaskStatus.Paused) && handler.Requests == 0,
                "restart retains shared resource links and recovers all interrupted children and their parent without network activity");
        }
        var failedRestart = restartStore.Load();
        var failedChild = failedRestart.Owners.Single(owner => owner.Id == sharedChildId);
        failedChild.Status = DownloadTaskStatus.Failed; failedChild.Error = "persistent model failure";
        var reviewChildId = failedRestart.Owners.Single(owner => owner.Id == sharedParentId).ResourceOwnerIds[second.Id];
        var reviewChild = failedRestart.Owners.Single(owner => owner.Id == reviewChildId);
        reviewChild.Status = DownloadTaskStatus.NeedsReview; reviewChild.Error = "persistent checkpoint review";
        restartStore.Save(failedRestart);
        using (var handler = new GateHandler(bytes))
        using (var downloads = new Downloads(handler: handler))
        await using (var tasks = new DownloadTaskService(downloads, sharedState))
        {
            var data = tasks.GetSnapshot();
            var failure = ModelDownloadProjection.ForModel(config.ComfyRoot, first, intents[0].Destination, data.Owners, data.Tasks)!;
            var review = ModelDownloadProjection.ForModel(config.ComfyRoot, second, intents[1].Destination, data.Owners, data.Tasks)!;
            Check(failure.Status == DownloadTaskStatus.Failed && failure.CanRetry && failure.Error == "persistent model failure"
                && review.Status == DownloadTaskStatus.NeedsReview && review.CanRetry && review.Error == "persistent checkpoint review" && handler.Requests == 0,
                "restart preserves failed and needs-review owners with their retry actions and original errors");
        }
        long availableBytes = DownloadDiskSpace.SafetyMargin + bytes.Length - 1;
        var diskOptions = new DownloadDiskSpaceOptions { LedgerRoot = Path.Combine(root, "disk-budget-ledger"),
            AvailableBytes = _ => availableBytes, AllocatedBytes = _ => 0 };
        var diskIntent = intents[0] with { Destination = Path.Combine(root, "disk-budget-model.bin") };
        using (var handler = new GateHandler(bytes))
        using (var downloads = new Downloads(handler: handler, engineOptions: new() { DiskSpaceOptions = diskOptions }))
        await using (var tasks = new DownloadTaskService(downloads, Path.Combine(root, "disk-budget-state")))
        {
            var owner = tasks.RegisterOwner("models", "disk budget fixture", config, [diskIntent]);
            async Task Fetch(CancellationToken ct)
            {
                await downloads.Fetch(first.Name, first.Urls, diskIntent.Destination, first.Size, first.Sha256, null, ct);
                tasks.MarkModelCompleted(owner.Id, first.Id);
            }
            try { await tasks.RunOwnerAsync(owner.Id, Fetch); throw new Exception("Expected insufficient disk budget"); }
            catch (InsufficientSpaceException)
            {
                var failed = tasks.GetSnapshot();
                var row = ModelDownloadProjection.ForModel(config.ComfyRoot, first, diskIntent.Destination, failed.Owners, failed.Tasks)!;
                Check(row.Status == DownloadTaskStatus.Failed && row.CanRetry && row.Error.Length > 0
                    && handler.Requests == 0 && !File.Exists(diskIntent.Destination) && !Downloads.PartialFiles(diskIntent.Destination).Any(File.Exists),
                    "insufficient disk budget fails the durable model with retry available before network or model data is written");
                Check(failed.Owners.Single().CompletedModelIds.Count == 0 && failed.Owners.Single().Fingerprint == owner.Fingerprint
                    && failed.Tasks.Single().Status == DownloadTaskStatus.Failed,
                    "disk failure preserves frozen model identity and cannot mark the model or file complete");
            }
            availableBytes += 1024 * 1024;
            handler.Release("first.bin");
            await tasks.RunOwnerAsync(owner.Id, Fetch);
            var ready = tasks.GetSnapshot();
            Check(ready.Owners.Single().Id == owner.Id && ready.Owners.Single().Status == DownloadTaskStatus.Completed
                && ready.Tasks.Single().Status == DownloadTaskStatus.Completed
                && await Downloads.Verify(diskIntent.Destination, first.Size, first.Sha256, default),
                "the same model owner retries successfully after disk capacity becomes available and only then publishes completion");
        }
        return count;
    }

    private sealed class GateHandler(byte[] bytes) : HttpMessageHandler
    {
        private readonly object sync = new();
        private readonly Dictionary<string, TaskCompletionSource> starts = new();
        private readonly Dictionary<string, TaskCompletionSource> releases = new();
        private readonly Dictionary<string, int> counts = new();
        private TaskCompletionSource Signal(Dictionary<string, TaskCompletionSource> map, string name)
        { if (!map.TryGetValue(name, out var source)) map.Add(name, source = new(TaskCreationOptions.RunContinuationsAsynchronously)); return source; }
        public int Requests { get { lock (sync) return counts.Values.Sum(); } }
        public int Count(string name) { lock (sync) return counts.GetValueOrDefault(name); }
        public Task WaitStarted(string name) { lock (sync) return Signal(starts, name).Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        public void Release(string name) { lock (sync) Signal(releases, name).TrySetResult(); }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var name = Path.GetFileName(request.RequestUri!.AbsolutePath);
            Task wait;
            lock (sync) { counts[name] = counts.GetValueOrDefault(name) + 1; Signal(starts, name).TrySetResult(); wait = Signal(releases, name).Task; }
            await wait.WaitAsync(ct);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }
    }
}
