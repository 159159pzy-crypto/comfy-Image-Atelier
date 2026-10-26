using System.Net;
using System.Security.Cryptography;
using Anima.Launcher.Core;

internal static class TaskServiceTests
{
    public static async Task<bool> TryHandleFixture(string[] args)
    {
        if (args.FirstOrDefault() != "--package-lock-fixture") return false;
        var path = args[1] + ".download.lock";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var lease = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Console.WriteLine("LOCKED"); Console.Out.Flush();
        await Task.Run(Console.ReadLine);
        return true;
    }

    public static async Task<int> Run(string work)
    {
        var count = 0;
        void Check(bool condition, string label) { if (!condition) throw new Exception("FAIL: " + label); count++; Console.WriteLine("PASS: " + label); }
        async Task Fails(Func<Task> action, string label)
        { try { await action(); } catch { Check(true, label); return; } throw new Exception("Expected failure: " + label); }
        var root = Path.Combine(work, "task-service-" + Guid.NewGuid().ToString("N"));
        var bytes = Enumerable.Range(0, 2048).Select(i => (byte)(i % 251)).ToArray();
        var sha = Convert.ToHexString(SHA256.HashData(bytes));
        DownloadRequest Request(string name) => new(name, ["https://example.com/" + name], Path.Combine(root, name), bytes.Length, sha) { Network = new("direct") };
        var config = new LauncherConfig { ComfyRoot = Path.Combine(root, "environment"), NetworkMode = "direct" };
        using var handler = new GateHandler(bytes);
        using var downloads = new Downloads(handler: handler);
        await using var service = new DownloadTaskService(downloads, root);
        var speed = new DownloadSpeedSmoother();
        Check(speed.Sample("Downloading", 1000, TimeSpan.FromSeconds(20)) == 1000,
            "display speed starts from the first sample without counting scheduler wait time");
        var smoothed = speed.Sample("Downloading", 0, TimeSpan.FromSeconds(21));
        Check(smoothed is > 600 and < 610, "display speed smooths a brief dip using elapsed-time weighting");
        Check(speed.Sample("RetryWaiting", 1000, TimeSpan.FromSeconds(22)) is null
            && speed.Sample("Downloading", 3000, TimeSpan.FromSeconds(90)) == 3000,
            "retry waiting clears display ETA and the resumed source starts a fresh speed window");
        speed.Reset();
        Check(speed.Sample("Downloading", 500, TimeSpan.Zero) == 500
            && speed.Sample("Verifying", 500, TimeSpan.FromSeconds(1)) is null,
            "new attempts and final verification never reuse a previous transfer speed");

        var a = service.FetchAsync(Request("a.bin"), null, default);
        var b = service.FetchAsync(Request("b.bin"), null, default);
        await handler.WaitStarted("a.bin"); await handler.WaitStarted("b.bin");
        var c = service.FetchAsync(Request("c.bin"), null, default);
        Check(handler.Active == 2 && !handler.Started("c.bin"), "scheduler limits all file transfers to two active writers");
        handler.Release("a.bin"); await a;
        await handler.WaitStarted("c.bin");
        Check(handler.MaxActive == 2, "queued file uses released slot without exceeding global concurrency");
        handler.Release("b.bin"); handler.Release("c.bin"); await Task.WhenAll(b, c);

        var slotOne = service.FetchAsync(Request("priority-slot-one.bin"), null, default);
        var slotTwo = service.FetchAsync(Request("priority-slot-two.bin"), null, default);
        await handler.WaitStarted("priority-slot-one.bin"); await handler.WaitStarted("priority-slot-two.bin");
        var priorityOwner = service.RegisterOwner("models", "priority fixture", config,
            payload: System.Text.Json.JsonSerializer.SerializeToElement(new { priority = true }));
        var priorityRun = service.RunOwnerAsync(priorityOwner.Id, ct =>
        {
            Task optional;
            using (service.WithinOwner(priorityOwner.Id, false)) optional = service.FetchAsync(Request("priority-optional.bin"), null, ct);
            Task required;
            using (service.WithinOwner(priorityOwner.Id, true)) required = service.FetchAsync(Request("priority-required.bin"), null, ct);
            return Task.WhenAll(optional, required);
        });
        handler.Release("priority-slot-one.bin"); await slotOne;
        await handler.WaitStarted("priority-required.bin");
        Check(!handler.Started("priority-optional.bin"), "required model queued later starts before an optional queued resource");
        handler.Release("priority-slot-two.bin"); handler.Release("priority-required.bin"); handler.Release("priority-optional.bin");
        await Task.WhenAll(slotTwo, priorityRun);

        var historical = service.FetchAsync(Request("historical-required.bin"), null, default);
        await handler.WaitStarted("historical-required.bin");
        var historicalRecord = service.Tasks.Single(task => task.Name == "historical-required.bin");
        await service.CancelTaskAsync(historicalRecord.Id);
        await Fails(() => historical, "historical required attempt can be canceled before a new optional attempt");
        var historicalSlotOne = service.FetchAsync(Request("historical-slot-one.bin"), null, default);
        var historicalSlotTwo = service.FetchAsync(Request("historical-slot-two.bin"), null, default);
        await handler.WaitStarted("historical-slot-one.bin"); await handler.WaitStarted("historical-slot-two.bin");
        var historicalOwner = service.RegisterOwner("models", "historical priority", config,
            payload: System.Text.Json.JsonSerializer.SerializeToElement(new { historicalPriority = true }));
        var historicalRun = service.RunOwnerAsync(historicalOwner.Id, ct =>
        {
            Task optional;
            using (service.WithinOwner(historicalOwner.Id, false)) optional = service.FetchAsync(Request("historical-required.bin"), null, ct);
            Task required;
            using (service.WithinOwner(historicalOwner.Id, true)) required = service.FetchAsync(Request("new-required.bin"), null, ct);
            return Task.WhenAll(optional, required);
        });
        handler.Release("historical-slot-one.bin"); await historicalSlotOne;
        await handler.WaitStarted("new-required.bin");
        Check(handler.Count("historical-required.bin") == 1 && !service.Tasks.Single(task => task.Id == historicalRecord.Id).Required,
            "a reused historical required task is optional for its new execution and stays behind a currently required task");
        handler.Release("historical-slot-two.bin"); handler.Release("new-required.bin"); handler.Release("historical-required.bin");
        await Task.WhenAll(historicalSlotTwo, historicalRun);

        var shared = Request("shared.bin");
        using var oneCanceled = new CancellationTokenSource();
        var first = service.FetchAsync(shared, null, oneCanceled.Token);
        await handler.WaitStarted("shared.bin");
        var second = service.FetchAsync(shared, null, default);
        oneCanceled.Cancel(); await Fails(() => first, "one merged waiter may cancel independently");
        Check(handler.Count("shared.bin") == 1 && !second.IsCompleted, "same content and destination merge into one active transfer");
        await Fails(() => service.FetchAsync(shared with { Sha256 = new string('a', 64) }, null, default),
            "different content cannot write a destination with an unfinished task");
        handler.Release("shared.bin"); await second;
        Check(!service.HasRunningTasks, "completed download returns only after releasing its execution and scheduler slot");
        Check(await Downloads.Verify(shared.Destination, shared.Size, shared.Sha256, default), "surviving merged waiter receives verified file");

        var cancelOwner = service.RegisterOwner("models", "cancel owner", config);
        var owned = service.RunOwnerAsync(cancelOwner.Id, ct => Task.WhenAll(
            service.FetchAsync(Request("cancel-owned.bin"), null, ct),
            service.FetchAsync(Request("surviving-owned.bin"), null, ct)));
        await handler.WaitStarted("cancel-owned.bin");
        await handler.WaitStarted("surviving-owned.bin");
        var ownedRecord = service.Tasks.Single(t => t.Name == "cancel-owned.bin");
        await service.CancelTaskAsync(ownedRecord.Id);
        Check(!owned.IsCompleted && service.Tasks.Single(t => t.Name == "surviving-owned.bin").Status == DownloadTaskStatus.Running,
            "canceling one file leaves unrelated files in its model batch running");
        var crashSnapshot = Path.Combine(root, "cancel-crash-snapshot");
        Directory.CreateDirectory(crashSnapshot);
        File.Copy(Path.Combine(root, "download-tasks.json"), Path.Combine(crashSnapshot, "download-tasks.json"));
        using (var restoredDownloads = new Downloads(handler: new GateHandler(bytes)))
        {
            await using var restored = new DownloadTaskService(restoredDownloads, crashSnapshot);
            Check(restored.Owners.Single(o => o.Id == cancelOwner.Id).Status == DownloadTaskStatus.Canceled
                && restored.Tasks.Single(t => t.Id == ownedRecord.Id).Status == DownloadTaskStatus.Canceled,
                "restart preserves child cancellation while its sibling was still running");
        }
        handler.Release("surviving-owned.bin");
        await Fails(() => owned, "explicit child cancellation interrupts its owning coordinator");
        Check(service.Owners.Single(o => o.Id == cancelOwner.Id).Status == DownloadTaskStatus.Canceled,
            "canceled child cannot reappear through continue-all paused owners");
        Check(service.Tasks.Single(t => t.Id == ownedRecord.Id).Status == DownloadTaskStatus.Canceled,
            "explicit file cancellation remains a canceled terminal intent");
        var newOwner = service.RegisterOwner("models", "new explicit request", config);
        service.SetTaskCompatibilityMode(ownedRecord.Id);
        var newRequest = Request("cancel-owned.bin") with { Urls = ["https://alternate.example/cancel-owned.bin"] };
        var newRun = service.RunOwnerAsync(newOwner.Id, ct => service.FetchAsync(newRequest, null, ct));
        Check(newOwner.Id != cancelOwner.Id && service.Tasks.Single(t => t.Id == ownedRecord.Id).Urls.SequenceEqual(newRequest.Urls),
            "new explicit owner after cancellation adopts its new transport settings for identical content");
        Check(service.Tasks.Single(t => t.Id == ownedRecord.Id).PreferHttpClient,
            "derived resource owner preserves an explicit compatibility-engine recovery choice");
        handler.Release("cancel-owned.bin"); await newRun;

        var pausedOwner = service.RegisterOwner("models", "paused owner", config, payload: System.Text.Json.JsonSerializer.SerializeToElement(new { choice = "paused" }));
        var paused = service.RunOwnerAsync(pausedOwner.Id, ct => service.FetchAsync(Request("paused.bin"), null, ct));
        await handler.WaitStarted("paused.bin"); await service.PauseOwnerAsync(pausedOwner.Id);
        await Fails(() => paused, "owner pause waits for child writer cancellation");
        var pausedFile = service.Tasks.Single(t => t.Name == "paused.bin");
        Check(service.Owners.Single(o => o.Id == pausedOwner.Id).Status == DownloadTaskStatus.Paused && pausedFile.Status == DownloadTaskStatus.Paused,
            "pause preserves a resumable owner and file");
        await Fails(() => service.ResumeTaskAsync(pausedFile.Id), "owner file cannot bypass required package or installation finalization");
        var frozenSha = pausedFile.Sha256;
        service.UpdateTaskSources(pausedFile.Id, ["https://alternate.example/paused.bin"], new("direct"));
        service.SetTaskCompatibilityMode(pausedFile.Id);
        Check(service.Tasks.Single(t => t.Id == pausedFile.Id) is { PreferHttpClient: true } changed
            && changed.Sha256 == frozenSha && changed.Destination == pausedFile.Destination && changed.TransportRevision == 3,
            "explicit compatibility and source changes preserve frozen identity");

        File.WriteAllBytes(pausedFile.Destination + ".part", bytes[..20]);
        var preview = service.CleanupPreview(pausedFile.Id);
        Check(preview.Paths.Contains(pausedFile.Destination + ".part") && preview.Bytes >= 20,
            "cleanup preview enumerates exact owned temporary files and bytes");
        var unrelated = Path.Combine(root, "keep.bin"); File.WriteAllBytes(unrelated, bytes);
        await service.DeletePartialAsync(pausedFile.Id);
        Check(!File.Exists(pausedFile.Destination + ".part") && File.Exists(unrelated), "explicit cleanup removes only this task's incomplete files");

        var cleanupRoot = Path.Combine(root, "cleanup-identities");
        using (var cleanupHandler = new GateHandler(bytes))
        using (var cleanupDownloads = new Downloads(handler: cleanupHandler))
        {
            await using var cleanupTasks = new DownloadTaskService(cleanupDownloads, cleanupRoot);
            var destination = Path.Combine(cleanupRoot, "same-target.bin");
            var requestA = Request("cleanup-version-a.bin") with { Destination = destination };
            var attemptA = cleanupTasks.FetchAsync(requestA, null, default);
            await cleanupHandler.WaitStarted("cleanup-version-a.bin");
            var taskA = cleanupTasks.Tasks.Single(); await cleanupTasks.CancelTaskAsync(taskA.Id);
            await Fails(() => attemptA, "cleanup fixture preserves a canceled old version record");
            var bytesB = bytes.Select(value => (byte)(value ^ 0x5a)).ToArray();
            var requestB = Request("cleanup-version-b.bin") with { Destination = destination, Sha256 = Convert.ToHexString(SHA256.HashData(bytesB)) };
            var attemptB = cleanupTasks.FetchAsync(requestB, null, default);
            await cleanupHandler.WaitStarted("cleanup-version-b.bin");
            var taskB = cleanupTasks.Tasks.Single(task => task.Name == requestB.Name);
            await cleanupTasks.PauseTaskAsync(taskB.Id); await Fails(() => attemptB, "new version may pause at the same target after the old version was canceled");
            File.WriteAllBytes(destination + ".part", bytesB[..20]);
            JsonFile.Write(destination + ".part.json", new ResumeInfo(requestB.Sha256, requestB.Size, requestB.Urls[0], null));
            await Fails(() => { cleanupTasks.CleanupPreview(taskA.Id); return Task.CompletedTask; }, "old canceled task cannot preview another paused version's partial files");
            await Fails(() => cleanupTasks.DeletePartialAsync(taskA.Id), "old canceled task cannot delete another paused version's partial files");
            Check(cleanupTasks.Tasks.Single(task => task.Id == taskB.Id).Status == DownloadTaskStatus.Paused
                && File.ReadAllBytes(destination + ".part").SequenceEqual(bytesB[..20]), "rejected old-task cleanup preserves the new version's bytes and intent");
            await cleanupTasks.CancelTaskAsync(taskB.Id);
            await Fails(() => cleanupTasks.DeletePartialAsync(taskA.Id), "on-disk metadata protects a newer version even after both tasks are canceled");

            using var heldTarget = new FileStream(destination + ".download.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var cleanup = cleanupTasks.DeletePartialAsync(taskB.Id);
            await Task.Delay(100);
            var requestC = Request("cleanup-version-c.bin") with { Destination = destination, Sha256 = new string('c', 64) };
            await Fails(() => cleanupTasks.FetchAsync(requestC, null, default), "target cleanup gate rejects a different identity while cleanup waits on its destination lease");
            await Fails(() => cleanupTasks.FetchAsync(requestB, null, default), "target cleanup gate also rejects a same-identity writer until deletion finishes");
            Check(!cleanup.IsCompleted && cleanupTasks.Tasks.All(task => task.Name != requestC.Name)
                && cleanupHandler.Count(requestC.Name) == 0, "cleanup admission is target-scoped and creates no replacement task or network request");
            File.WriteAllBytes(destination, bytesB);
            heldTarget.Dispose(); await cleanup;
            Check(await Downloads.Verify(destination, bytesB.Length, requestB.Sha256, default)
                && !File.Exists(destination + ".part"), "identity-checked cleanup removes only partial files and preserves a complete final artifact");
            await cleanupTasks.FetchAsync(requestB, null, default);
            Check(cleanupTasks.Tasks.Single(task => task.Id == taskB.Id).Status == DownloadTaskStatus.Completed,
                "target gate is released after cleanup so the selected identity can validate its final file");

            // Simulate a separate process replacing the checkpoint while cleanup is blocked on its lock.
            File.WriteAllBytes(destination + ".part", bytesB[..20]);
            JsonFile.Write(destination + ".part.json", new ResumeInfo(requestB.Sha256, requestB.Size, requestB.Urls[0], null));
            using var heldRecheck = new FileStream(destination + ".download.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var changedIdentityCleanup = cleanupTasks.DeletePartialAsync(taskB.Id);
            await Task.Delay(100);
            JsonFile.Write(destination + ".part.json", new ResumeInfo(requestA.Sha256, requestA.Size, requestA.Urls[0], null));
            heldRecheck.Dispose();
            await Fails(() => changedIdentityCleanup, "cleanup rechecks metadata under the cross-process destination lease");
            Check(File.Exists(destination + ".part") && await Downloads.Verify(destination, bytesB.Length, requestB.Sha256, default),
                "identity change while awaiting the lease preserves both partial and final files");
        }

        var o1 = service.RegisterOwner("models", "shared one", config, payload: System.Text.Json.JsonSerializer.SerializeToElement(new { choice = 1 }));
        var o2 = service.RegisterOwner("models", "shared two", config, payload: System.Text.Json.JsonSerializer.SerializeToElement(new { choice = 2 }));
        var r1 = service.RunOwnerAsync(o1.Id, ct => service.FetchAsync(Request("owners-shared.bin"), null, ct));
        await handler.WaitStarted("owners-shared.bin");
        var r2 = service.RunOwnerAsync(o2.Id, ct => service.FetchAsync(Request("owners-shared.bin"), null, ct));
        var both = service.Tasks.Single(t => t.Name == "owners-shared.bin");
        await Fails(() => { service.CleanupPreview(both.Id); return Task.CompletedTask; }, "shared unfinished owner prevents cleanup preview");
        await Fails(() => service.DeletePartialAsync(both.Id), "shared unfinished owner prevents cleanup execution");
        Check(service.Owners.Single(o => o.Id == o1.Id).Status == DownloadTaskStatus.Running, "rejected shared cleanup does not cancel a valid owner");
        handler.Release("owners-shared.bin"); await Task.WhenAll(r1, r2);

        var errorRequest = Request("error.bin"); handler.Errors.Add("error.bin");
        await Fails(() => service.FetchAsync(errorRequest, null, default), "failed engine produces a durable failed task");
        var error = service.Tasks.Single(t => t.Name == "error.bin").Error;
        Check(!error.Contains("private-signature", StringComparison.Ordinal) && !error.Contains("private-token", StringComparison.Ordinal),
            "durable failure messages redact signed URLs and token values");
        foreach (var field in new[] { "password", "secret", "api%5Fkey", "access_%74oken", "pass%77ord", "client%5Fsecret", "%6bey" })
            await Fails(() => service.FetchAsync(Request("sensitive-query.bin") with
            { Urls = ["https://example.com/model?" + field + "=private-query-value"] }, null, default),
                "stable source rejects decoded credential query key " + field);
        Check(!service.Tasks.Any(task => task.Name == "sensitive-query.bin")
            && !File.ReadAllText(Path.Combine(root, "download-tasks.json")).Contains("private-query-value", StringComparison.Ordinal)
            && handler.Count("sensitive-query.bin") == 0,
            "rejected credential URLs never reach task persistence or a download source");

        var badCheckpoint = Request("bad-checkpoint.bin") with { Urls = ["https://first.example/bad-checkpoint.bin", "https://second.example/bad-checkpoint.bin"] };
        File.WriteAllBytes(DownloadCheckpoint.AriaData(badCheckpoint.Destination), bytes[..20]);
        await Fails(() => service.FetchAsync(badCheckpoint, null, default), "invalid local checkpoint stops before attempting any source");
        var checkpointTask = service.Tasks.Single(t => t.Name == "bad-checkpoint.bin");
        Check(checkpointTask.Status == DownloadTaskStatus.NeedsReview && checkpointTask.FailureCode == "checkpoint"
            && checkpointTask.Error.Contains("兼容模式") && checkpointTask.Attempts == 1 && handler.Count("bad-checkpoint.bin") == 0,
            "local checkpoint failure persists needs-review and explicit recovery actions without source failover");
        service.SetTaskCompatibilityMode(checkpointTask.Id); handler.Release("bad-checkpoint.bin");
        await service.ResumeTaskAsync(checkpointTask.Id);
        Check(service.Tasks.Single(t => t.Id == checkpointTask.Id) is { Attempts: 2, SourceRetryCount: 0, FailureCode: null, Status: DownloadTaskStatus.Completed },
            "explicit new attempt increments its durable count and clears previous retry failure state");
        if (OperatingSystem.IsWindows())
        {
            using (var sparse = new FileStream(DownloadCheckpoint.AriaData(badCheckpoint.Destination), FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                if (!DeviceIoControl(sparse.SafeFileHandle, 0x000900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                    throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                sparse.SetLength(64L * 1024 * 1024); sparse.Flush(true);
            }
            var sparsePreview = service.CleanupPreview(checkpointTask.Id);
            Check(sparsePreview.Bytes > 0 && sparsePreview.Bytes < sparsePreview.LogicalBytes && sparsePreview.LogicalBytes >= 64L * 1024 * 1024,
                "cleanup preview reports physical allocated bytes instead of a sparse file's logical length");
        }
        Check(service.CleanupPreview(checkpointTask.Id).Paths.Contains(DownloadCheckpoint.AriaData(badCheckpoint.Destination)),
            "completed compatible download can preview its preserved obsolete native scratch");
        await service.DeletePartialAsync(checkpointTask.Id);
        Check(service.Tasks.Single(t => t.Id == checkpointTask.Id).Status == DownloadTaskStatus.Completed
            && await Downloads.Verify(badCheckpoint.Destination, badCheckpoint.Size, badCheckpoint.Sha256, default)
            && !File.Exists(DownloadCheckpoint.AriaData(badCheckpoint.Destination)),
            "cleaning obsolete scratch preserves completed status and the verified final file");

        handler.RetryOnce.Add("retry-count.bin");
        var retried = service.FetchAsync(Request("retry-count.bin"), null, default);
        await handler.WaitStarted("retry-count.bin");
        Check(service.Tasks.Single(t => t.Name == "retry-count.bin").SourceRetryCount == 1,
            "automatic source retry count is retained in the task record after Retry-After waiting");
        handler.Release("retry-count.bin"); await retried;

        var packageFiles = new[] { "config.json", "weights.bin" }.Select(name => new ModelFile(name, bytes.Length, sha, ["https://example.com/" + name])).ToArray();
        var package = new ModelItem("locked-package", "locked package", "fixture", "wd_taggers", "locked-package", [], bytes.Length * 2, "", [], "fixture", "")
            { Files = packageFiles, Revision = new string('a', 40) };
        var packageRoot = Path.Combine(root, "package-tests");
        var destinationOne = Path.Combine(packageRoot, "one"); var destinationTwo = Path.Combine(packageRoot, "two");
        using var packageHandler = new GateHandler(bytes);
        using var packageDownloads = new Downloads(handler: packageHandler);
        await using var packageTasks = new DownloadTaskService(packageDownloads, Path.Combine(root, "package-state"));
        var p1 = packageTasks.RegisterOwner("models", "package one", config, [new(package, destinationOne)]);
        var p2 = packageTasks.RegisterOwner("models", "package two", config, [new(package, destinationTwo)]);
        var packageOne = packageTasks.RunOwnerAsync(p1.Id, ct => ModelPackages.Fetch(packageDownloads, package, destinationOne, config, null, ct));
        await packageHandler.WaitStarted("config.json"); await packageHandler.WaitStarted("weights.bin");
        var packageTwo = packageTasks.RunOwnerAsync(p2.Id, ct => ModelPackages.Fetch(packageDownloads, package, destinationTwo, config, null, ct));
        await Task.Delay(150);
        Check(packageHandler.Count("config.json") == 1 && packageHandler.Count("weights.bin") == 1,
            "different package destinations serialize access to their shared historical staging identity");
        packageHandler.Release("config.json"); packageHandler.Release("weights.bin");
        await Task.WhenAll(packageOne, packageTwo);
        Check(await ModelPackages.State(package, destinationOne, default) == "已校验" && await ModelPackages.State(package, destinationTwo, default) == "已校验",
            "concurrent package coordinators publish two complete independently verified directories");

        var crossDestination = Path.Combine(packageRoot, "cross-process");
        var staging = ModelPackages.Staging(package, crossDestination);
        var start = new System.Diagnostics.ProcessStartInfo("dotnet")
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(TaskServiceTests).Assembly.Location); start.ArgumentList.Add("--package-lock-fixture"); start.ArgumentList.Add(staging);
        using var child = System.Diagnostics.Process.Start(start) ?? throw new IOException("Cannot start package lock fixture.");
        try
        {
            var ready = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (ready != "LOCKED") throw new IOException("Package lock fixture did not acquire its lease: " + ready);
            var oldCount = packageHandler.Count("config.json");
            var cross = ModelPackages.Fetch(packageDownloads, package, crossDestination, config, null, default);
            await Task.Delay(200);
            Check(!cross.IsCompleted && packageHandler.Count("config.json") == oldCount,
                "package waits on another process's staging lease before any child download or publication");
            await child.StandardInput.WriteLineAsync("release"); await child.StandardInput.FlushAsync();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await cross.WaitAsync(TimeSpan.FromSeconds(15));
            Check(await ModelPackages.State(package, crossDestination, default) == "已校验",
                "package safely continues and publishes after the external lease is released");
        }
        finally { if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); } }

        var blockedStoreRoot = Path.Combine(root, "blocked-store");
        using var blockedHandler = new GateHandler(bytes);
        using var blockedDownloads = new Downloads(handler: blockedHandler);
        await using var blockedService = new DownloadTaskService(blockedDownloads, blockedStoreRoot);
        var blockedOwner = blockedService.RegisterOwner("models", "save failure fixture", config);
        var blockedTemporary = Path.Combine(blockedStoreRoot, "download-tasks.json.tmp");
        Directory.CreateDirectory(blockedTemporary);
        await Fails(() => blockedService.RunOwnerAsync(blockedOwner.Id, _ => throw new Exception("must never execute")),
            "owner does not start when its running intent cannot be persisted");
        await Fails(() => blockedService.FetchAsync(Request("blocked-store.bin"), null, default),
            "file does not start when its queued intent cannot be persisted");
        Check(!blockedService.HasRunningTasks && !blockedHandler.Started("blocked-store.bin"),
            "persistence failure leaves no waiting execution or occupied scheduler slot");
        Directory.Delete(blockedTemporary);
        blockedHandler.Release("blocked-store.bin");
        await blockedService.FetchAsync(Request("blocked-store.bin"), null, default).WaitAsync(TimeSpan.FromSeconds(10));
        Check(blockedService.Tasks.Single().Status == DownloadTaskStatus.Completed,
            "download can be retried after task storage becomes writable");
        return count;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle handle, uint code,
        IntPtr input, uint inputLength, IntPtr output, uint outputLength, out uint returned, IntPtr overlapped);

    private sealed class GateHandler(byte[] bytes) : HttpMessageHandler
    {
        private readonly object gate = new();
        private readonly Dictionary<string, TaskCompletionSource> releases = new();
        private readonly Dictionary<string, TaskCompletionSource> started = new();
        private readonly Dictionary<string, int> counts = new();
        public HashSet<string> Errors { get; } = new();
        public HashSet<string> RetryOnce { get; } = new();
        public int Active { get; private set; }
        public int MaxActive { get; private set; }
        private TaskCompletionSource Signal(Dictionary<string, TaskCompletionSource> map, string name)
        { if (!map.TryGetValue(name, out var value)) map.Add(name, value = new(TaskCreationOptions.RunContinuationsAsynchronously)); return value; }
        public bool Started(string name) { lock (gate) return started.TryGetValue(name, out var signal) && signal.Task.IsCompleted; }
        public Task WaitStarted(string name) { lock (gate) return Signal(started, name).Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        public void Release(string name) { lock (gate) Signal(releases, name).TrySetResult(); }
        public int Count(string name) { lock (gate) return counts.GetValueOrDefault(name); }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var name = Path.GetFileName(request.RequestUri!.AbsolutePath);
            Task wait;
            lock (gate)
            {
                if (Errors.Contains(name)) throw new IOException("https://cdn.example/file?Signature=private-signature token=private-token");
                if (RetryOnce.Remove(name))
                {
                    var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                    limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(10));
                    return limited;
                }
                counts[name] = counts.GetValueOrDefault(name) + 1; Active++; MaxActive = Math.Max(MaxActive, Active);
                wait = Signal(releases, name).Task; Signal(started, name).TrySetResult();
            }
            try { await wait.WaitAsync(ct); return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }; }
            finally { lock (gate) Active--; }
        }
    }
}
