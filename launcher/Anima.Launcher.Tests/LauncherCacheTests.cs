using System.Security.Cryptography;
using System.Text;
using Anima.Launcher.Core;

internal static class LauncherCacheTests
{
    public static async Task<int> Run(string work)
    {
        var passed = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); passed++; Console.WriteLine("PASS: " + message); }
        var root = Path.Combine(work, "launcher-cache-" + Guid.NewGuid().ToString("N"));

        var basic = new Fixture(Path.Combine(root, "basic"));
        var old = await basic.Snapshot("1.0.0");
        var unknown = Path.Combine(basic.State, "versions", "0.1.0-" + new string('a', 16)); Directory.CreateDirectory(unknown);
        var external = Path.Combine(basic.State, ".net", "unmanaged"); Directory.CreateDirectory(external); File.WriteAllText(Path.Combine(external, "runtime.dll"), "preserve");
        basic.Advance(6);
        var early = await basic.Cache.MaintainAsync(basic.Context);
        Check(early.DeletedDirectories.Length == 0 && Directory.Exists(old.Assets), "cache younger than seven days remains available");
        basic.Advance(2);
        var cleaned = await basic.Cache.MaintainAsync(basic.Context);
        Check(cleaned.DeletedDirectories.SequenceEqual([old.Assets]) && !Directory.Exists(old.Assets) && cleaned.ReleasedBytes > 0,
            "registered unused snapshot is collected after retention");
        Check(Directory.Exists(unknown) && File.Exists(Path.Combine(external, "runtime.dll")), "unregistered and .NET directories are never collected");
        Check(File.Exists(basic.Cache.ReportPath) && cleaned.Skipped.Any(line => line.Contains(unknown)), "cleanup report includes actual bytes and unknown directories");
        var next = await basic.Snapshot("2.0.0"); basic.Age(next.Assets);
        var throttled = await basic.Cache.MaintainAsync(basic.Context);
        Check(throttled.DeletedDirectories.Length == 0 && Directory.Exists(next.Assets) && throttled.Skipped.Any(line => line.Contains("24")), "successful maintenance is limited to once per day");

        var references = new Fixture(Path.Combine(root, "references"));
        var current = await references.Snapshot("1.0.0"); var previous = await references.Snapshot("2.0.0");
        var pinned = await references.Snapshot("3.0.0"); var live = await references.Snapshot("4.0.0"); var unused = await references.Snapshot("5.0.0");
        var active = await references.Package("6.0.0"); var previousPackage = await references.Package("7.0.0"); var downloaded = await references.Package("8.0.0"); var unusedPackage = await references.Package("9.0.0");
        JsonFile.Write(Path.Combine(references.State, "launcher.json"), new LauncherConfig { AppRoot = current.App, PinnedAppRoot = pinned.App });
        JsonFile.Write(Path.Combine(references.State, "launcher.previous.json"), new LauncherConfig { AppRoot = previous.App });
        references.Store.Save(new LauncherUpdateState { Active = active, Previous = previousPackage, Downloaded = downloaded });
        references.Processes = new(true, [live.App]); references.Advance(8);
        var retained = await references.Cache.MaintainAsync(references.Context);
        Check(new[] { current.Assets, previous.Assets, pinned.Assets, live.Assets }.All(Directory.Exists), "current, rollback, pinned and live workbench roots are retained");
        Check(new[] { active.Executable, previousPackage.Executable, downloaded.Executable }.All(File.Exists), "active, previous and downloaded launchers are retained");
        Check(!Directory.Exists(unused.Assets) && !File.Exists(unusedPackage.Executable) && retained.DeletedDirectories.Length == 2,
            "only unused workbench and launcher package are deleted");

        var transactions = new Fixture(Path.Combine(root, "transactions"));
        var recovery = await transactions.Snapshot("1.0.0"); var candidate = await transactions.Package("2.0.0"); var before = await transactions.Package("1.0.0");
        var transaction = new UpdateTransaction { Id = Guid.NewGuid().ToString("N"), Token = new string('a', 64), Candidate = candidate, Previous = before,
            PreviousConfig = new LauncherConfig { AppRoot = recovery.App }, Phase = "recovery-required" };
        transactions.Store.WriteTransaction(transaction); transactions.Advance(8);
        await transactions.Cache.MaintainAsync(transactions.Context);
        Check(Directory.Exists(recovery.Assets) && File.Exists(candidate.Executable) && File.Exists(before.Executable), "unfinished recovery transaction protects both binaries and previous workbench even without pending pointer");

        var modifications = new Fixture(Path.Combine(root, "modifications"));
        var modified = await modifications.Snapshot("1.0.0"); var extra = await modifications.Snapshot("2.0.0"); var bytecode = await modifications.Snapshot("3.0.0");
        File.WriteAllText(Path.Combine(modified.App, "run.py"), "user edit"); File.WriteAllText(Path.Combine(extra.App, "user-photo.png"), "user data");
        Directory.CreateDirectory(Path.Combine(bytecode.App, "__pycache__")); File.WriteAllText(Path.Combine(bytecode.App, "__pycache__", "run.cpython-311.pyc"), "derived");
        modifications.Advance(8); await modifications.Cache.MaintainAsync(modifications.Context);
        Check(Directory.Exists(modified.Assets) && File.Exists(Path.Combine(extra.App, "user-photo.png")), "changed sources and added user files preserve the entire cache");
        Check(!Directory.Exists(bytecode.Assets), "generated Python bytecode does not prevent safe snapshot collection");

        var uncertain = new Fixture(Path.Combine(root, "uncertain")); var protectedUnknown = await uncertain.Snapshot("1.0.0"); uncertain.Advance(8);
        uncertain.Processes = new(false, [], "cannot inspect live Python");
        Check((await uncertain.Cache.MaintainAsync(uncertain.Context)).DeletedDirectories.Length == 0 && Directory.Exists(protectedUnknown.Assets), "unreadable process identity fails closed");
        uncertain.Processes = new(true, []);
        using (await LauncherCache.AcquireUseAsync(uncertain.State))
            Check((await uncertain.Cache.MaintainAsync(uncertain.Context)).DeletedDirectories.Length == 0 && Directory.Exists(protectedUnknown.Assets), "shared operation lease prevents concurrent maintenance");
        Check((await uncertain.Cache.MaintainAsync(uncertain.Context with { IsBusy = true })).DeletedDirectories.Length == 0, "busy UI operation postpones cache collection");
        File.WriteAllText(Path.Combine(uncertain.State, "launcher.json"), "{");
        Check((await uncertain.Cache.MaintainAsync(uncertain.Context)).DeletedDirectories.Length == 0 && Directory.Exists(protectedUnknown.Assets), "broken durable references prevent deletion");

        var malformed = new Fixture(Path.Combine(root, "malformed")); var malformedEntry = await malformed.Snapshot("1.0.0"); malformed.Advance(8);
        File.WriteAllText(malformed.Cache.IndexPath, "{");
        Check((await malformed.Cache.MaintainAsync(malformed.Context)).DeletedDirectories.Length == 0 && Directory.Exists(malformedEntry.Assets), "broken cache index never authorizes deletion");

        var legacy = new Fixture(Path.Combine(root, "legacy")); var legacySnapshot = legacy.CreateSnapshot("1.0.0");
        await legacy.Cache.RegisterSnapshotAsync("1.0.0", legacySnapshot.Assets, legacySnapshot.App);
        Check(!File.Exists(legacy.Cache.IndexPath), "unknown existing snapshots cannot be silently adopted");
        var lockEntry = await legacy.Snapshot("2.0.0"); legacy.Advance(8);
        using (var held = new FileStream(Path.Combine(lockEntry.App, "run.py"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await legacy.Cache.MaintainAsync(legacy.Context);
            Check(File.Exists(Path.Combine(lockEntry.Assets, "payload.zip")) && Directory.Exists(lockEntry.Assets), "locked snapshot files preserve the complete tree");
        }

        var corruptedPointer = new Fixture(Path.Combine(root, "transactions-corrupt")); var untouched = await corruptedPointer.Snapshot("1.0.0"); corruptedPointer.Advance(8);
        Directory.CreateDirectory(Path.Combine(corruptedPointer.Store.Root, "transactions"));
        File.WriteAllText(Path.Combine(corruptedPointer.Store.Root, "transactions", Guid.NewGuid().ToString("N") + ".json"), "{");
        Check((await corruptedPointer.Cache.MaintainAsync(corruptedPointer.Context)).DeletedDirectories.Length == 0 && Directory.Exists(untouched.Assets), "unreadable update transaction preserves caches");

        var invalidRoot = new Fixture(Path.Combine(root, "invalid-root"));
        try { await invalidRoot.Cache.RegisterSnapshotAsync("1.0.0", root, root, newlyCreated: true); throw new Exception("outside root accepted"); }
        catch (InvalidDataException) { Check(true, "external snapshot roots cannot be registered"); }

        var identities = new Fixture(Path.Combine(root, "identities")); var matchingPackage = await identities.Package("1.0.0");
        var matching = await identities.Snapshot("1.0.0", matchingPackage.Sha256); var different = await identities.Snapshot("1.0.0", new string('c', 64));
        identities.Store.Save(new LauncherUpdateState { Active = matchingPackage }); identities.Advance(8);
        await identities.Cache.MaintainAsync(identities.Context);
        Check(Directory.Exists(matching.Assets) && !Directory.Exists(different.Assets), "snapshot retention distinguishes executable hashes within the same version");
        Check(JsonFile.Read<LauncherCacheIndex>(identities.Cache.IndexPath).Entries.Single(entry => entry.Directory == matching.Assets).PackageHashes.Contains(matchingPackage.Sha256),
            "snapshot index records verified executable association");

        var downloadReferences = new Fixture(Path.Combine(root, "download-references"));
        var planned = await downloadReferences.Snapshot("1.0.0"); var unneeded = await downloadReferences.Snapshot("2.0.0");
        using (var downloader = new Downloads())
        {
            await using var service = new DownloadTaskService(downloader, downloadReferences.State);
            service.RegisterOwner("installation", "paused installation", new LauncherConfig { AppRoot = planned.App });
        }
        var taskStore = new DownloadTaskStore(downloadReferences.State); var taskState = taskStore.Load();
        taskState.Owners[0].Status = DownloadTaskStatus.Running; taskStore.Save(taskState); downloadReferences.Advance(8);
        Check((await downloadReferences.Cache.MaintainAsync(downloadReferences.Context)).DeletedDirectories.Length == 0 && Directory.Exists(unneeded.Assets),
            "persisted active download or installation postpones all cleanup");
        taskState.Owners[0].Status = DownloadTaskStatus.Canceled; taskStore.Save(taskState);
        await downloadReferences.Cache.MaintainAsync(downloadReferences.Context);
        Check(Directory.Exists(planned.Assets) && !Directory.Exists(unneeded.Assets), "canceled download owner still protects its frozen configuration and recoverable files");

        var links = new Fixture(Path.Combine(root, "links")); var linked = await links.Snapshot("1.0.0");
        var outside = Path.Combine(root, "external-data"); Directory.CreateDirectory(outside); var important = Path.Combine(outside, "keep.txt"); File.WriteAllText(important, "retain");
        try
        {
            CreateDirectoryLink(Path.Combine(linked.App, "link"), outside); links.Advance(8);
            await links.Cache.MaintainAsync(links.Context);
            Check(Directory.Exists(linked.Assets) && File.Exists(important), "reparse points are rejected without following or deleting their targets");
        }
        catch (UnauthorizedAccessException) { Console.WriteLine("SKIP: symbolic link fixture requires Windows developer mode or link privilege"); }
        catch (IOException error) when ((error.HResult & 0xffff) == 1314) { Console.WriteLine("SKIP: symbolic link fixture requires Windows developer mode or link privilege"); }

        var interrupted = new Fixture(Path.Combine(root, "interrupted")); var interruptedSnapshot = await interrupted.Snapshot("1.0.0"); interrupted.Advance(8);
        interrupted.AfterDelete = (_, count) => { if (count == 1) throw new SimulatedCrash(); };
        try { await interrupted.Cache.MaintainAsync(interrupted.Context); throw new Exception("fault was not injected"); }
        catch (SimulatedCrash) { }
        var interruptedIndex = JsonFile.Read<LauncherCacheIndex>(interrupted.Cache.IndexPath);
        Check(interruptedIndex.Entries.Single().PendingDeletionFiles is { Length: 2 } && Directory.GetFiles(interruptedSnapshot.Assets, "*", SearchOption.AllDirectories).Length == 1,
            "deletion intent and exact file manifest survive interruption after the first file");
        interrupted.AfterDelete = null;
        Check((await interrupted.Cache.MaintainAsync(interrupted.Context)).DeletedDirectories.Contains(interruptedSnapshot.Assets) && !Directory.Exists(interruptedSnapshot.Assets),
            "interrupted deletion resumes from the unchanged remaining manifest subset");

        var tamperedRecovery = new Fixture(Path.Combine(root, "tampered-recovery")); var partial = await tamperedRecovery.Snapshot("1.0.0"); tamperedRecovery.Advance(8);
        tamperedRecovery.AfterDelete = (_, count) => { if (count == 1) throw new SimulatedCrash(); };
        try { await tamperedRecovery.Cache.MaintainAsync(tamperedRecovery.Context); }
        catch (SimulatedCrash) { }
        tamperedRecovery.AfterDelete = null; File.WriteAllText(Path.Combine(partial.Assets, "user-file.txt"), "preserve");
        Check((await tamperedRecovery.Cache.MaintainAsync(tamperedRecovery.Context)).DeletedDirectories.Length == 0 && File.Exists(Path.Combine(partial.Assets, "payload.zip")),
            "recovery rejects new user files instead of weakening the original deletion manifest");

        Check(!LauncherCache.ClassifyPythonCommandLine("python.exe run.py --port 8190").Reliable
            && !LauncherCache.ClassifyPythonCommandLine("python.exe .\\run.py").Reliable
            && !LauncherCache.ClassifyPythonCommandLine("python.exe \"unclosed").Reliable,
            "relative and malformed workbench command lines postpone collection");
        Check(LauncherCache.ClassifyPythonCommandLine("python.exe main.py --listen 127.0.0.1").Reliable
            && LauncherCache.ClassifyPythonCommandLine("python.exe \"" + current.App + "\\run.py\"").ProtectedPaths.Contains(current.App),
            "Comfy main.py is unrelated while an absolute workbench path remains protected");

        var newlyRunning = new Fixture(Path.Combine(root, "newly-running")); var newlyUsed = await newlyRunning.Snapshot("1.0.0"); newlyRunning.Advance(8);
        newlyRunning.ProcessReader = read => read == 1 ? new(true, []) : new(true, [newlyUsed.App]);
        Check((await newlyRunning.Cache.MaintainAsync(newlyRunning.Context)).DeletedDirectories.Length == 0 && Directory.Exists(newlyUsed.Assets) && newlyRunning.ProcessReads == 2,
            "a workbench started during inventory is protected by the second process inspection");
        var newlyUnknown = new Fixture(Path.Combine(root, "newly-unknown")); var unknownUse = await newlyUnknown.Snapshot("1.0.0"); newlyUnknown.Advance(8);
        newlyUnknown.ProcessReader = read => new(read == 1, [], "identity changed");
        Check((await newlyUnknown.Cache.MaintainAsync(newlyUnknown.Context)).DeletedDirectories.Length == 0 && Directory.Exists(unknownUse.Assets),
            "identity uncertainty immediately before deletion stops collection");

        var repair = new Fixture(Path.Combine(root, "repair")); var repairSnapshot = await repair.Snapshot("1.0.0"); repair.Advance(8);
        repair.AfterDelete = (_, count) => { if (count == 1) throw new SimulatedCrash(); };
        try { await repair.Cache.MaintainAsync(repair.Context); } catch (SimulatedCrash) { }
        repair.AfterDelete = null;
        Check(await repair.Cache.HasPendingSnapshotRepairAsync(repairSnapshot.Assets), "launch detects its interrupted snapshot deletion before reuse");
        try { await repair.Cache.CompleteSnapshotRepairAsync(repairSnapshot.Assets); throw new Exception("missing snapshot files accepted"); }
        catch (InvalidDataException) { Check(await repair.Cache.HasPendingSnapshotRepairAsync(repairSnapshot.Assets), "incomplete repair retains its durable marker"); }
        File.WriteAllText(Path.Combine(repairSnapshot.App, "run.py"), "print('fixture')");
        Directory.CreateDirectory(Path.Combine(repairSnapshot.App, "__pycache__")); File.WriteAllText(Path.Combine(repairSnapshot.App, "__pycache__", "run.pyc"), "derived");
        await repair.Cache.CompleteSnapshotRepairAsync(repairSnapshot.Assets);
        Check(!await repair.Cache.HasPendingSnapshotRepairAsync(repairSnapshot.Assets)
            && JsonFile.Read<LauncherCacheIndex>(repair.Cache.IndexPath).Entries.Single().LastUsedAt == repair.Time,
            "restoring exact missing files clears deletion intent and refreshes retention while allowing derived bytecode");
        File.WriteAllText(Path.Combine(partial.App, "run.py"), "print('fixture')");
        try { await tamperedRecovery.Cache.CompleteSnapshotRepairAsync(partial.Assets); throw new Exception("added user file accepted"); }
        catch (InvalidDataException) { Check(File.Exists(Path.Combine(partial.Assets, "user-file.txt")), "repair refuses to adopt additional user files"); }
        File.Delete(Path.Combine(partial.Assets, "user-file.txt")); File.WriteAllText(Path.Combine(partial.Assets, "payload.zip"), "user-modified archive");
        try { await tamperedRecovery.Cache.CompleteSnapshotRepairAsync(partial.Assets); throw new Exception("modified source accepted"); }
        catch (InvalidDataException)
        {
            Check(await tamperedRecovery.Cache.HasPendingSnapshotRepairAsync(partial.Assets)
                && File.ReadAllText(Path.Combine(partial.Assets, "payload.zip")) == "user-modified archive", "repair preserves modified files and keeps the incomplete marker");
        }
        Check(!await legacy.Cache.HasPendingSnapshotRepairAsync(legacySnapshot.Assets), "unknown legacy snapshots are never classified as repairable cache");

        var trusted = new Fixture(Path.Combine(root, "verified-registration")); var verifiedBytes = Encoding.UTF8.GetBytes("already verified package");
        var verifiedSha = Convert.ToHexString(SHA256.HashData(verifiedBytes)).ToLowerInvariant();
        var verifiedPackage = new InstalledLauncher("1.0.0", trusted.Store.CandidatePath("1.0.0", verifiedSha), verifiedBytes.Length, verifiedSha);
        Directory.CreateDirectory(Path.GetDirectoryName(verifiedPackage.Executable)!); File.WriteAllBytes(verifiedPackage.Executable, verifiedBytes);
        Check(await Downloads.Verify(verifiedPackage.Executable, verifiedPackage.Size, verifiedPackage.Sha256, default), "trusted registration fixture first verifies the published bytes");
        using (var held = new FileStream(verifiedPackage.Executable, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await trusted.Cache.RegisterVerifiedPackageAsync(verifiedPackage);
        Check(JsonFile.Read<LauncherCacheIndex>(trusted.Cache.IndexPath).Entries.Single().Files.Single().Sha256 == verifiedSha,
            "verified registration reuses the digest without reopening or reading the package bytes");
        verifiedBytes[0] ^= 1; File.WriteAllBytes(verifiedPackage.Executable, verifiedBytes);
        try { await trusted.Cache.RegisterPackageAsync(verifiedPackage); throw new Exception("unverified changed package accepted"); }
        catch (IOException) { Check(true, "public package registration still hashes and rejects same-size changed content"); }
        trusted.Advance(8);
        Check((await trusted.Cache.MaintainAsync(trusted.Context)).DeletedDirectories.Length == 0 && File.Exists(verifiedPackage.Executable),
            "cleanup independently rehashes a previously verified package and preserves changed bytes");

        var importState = Path.Combine(root, "import-registration"); var importSource = Path.Combine(root, "source.exe");
        File.WriteAllText(importSource, "verified import source");
        using (var importing = new LauncherUpdates(importState, importSource, "1.0.0"))
        {
            var imported = await importing.ImportAsync(importSource, "1.0.0", default);
            var importIndex = Path.Combine(importState, "cache-index.json");
            Check(JsonFile.Read<LauncherCacheIndex>(importIndex).Entries.Single().Files.Single().Sha256 == imported.Sha256,
                "import publishes the verified file and registers that exact digest");
            File.WriteAllText(imported.Executable, "changed existing target");
            try { await importing.ImportAsync(importSource, "1.0.0", default); throw new Exception("changed target treated as trusted"); }
            catch (IOException)
            {
                Check(File.ReadAllText(imported.Executable) == "changed existing target"
                    && JsonFile.Read<LauncherCacheIndex>(importIndex).Entries.Single().Files.Single().Sha256 == imported.Sha256,
                    "an existing import target must pass verification before reaching trusted registration");
            }
        }
        Console.WriteLine("Launcher cache fixtures: " + root);
        return passed;
    }

    private sealed class SimulatedCrash : Exception;

    private static void CreateDirectoryLink(string path, string target)
    {
        try { Directory.CreateSymbolicLink(path, target); }
        catch (IOException error) when (OperatingSystem.IsWindows() && (error.HResult & 0xffff) == 1314)
        {
            // Junctions exercise the same reparse-point boundary without symbolic-link privileges.
            var script = "$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '" + path.Replace("'", "''") + "' -Target '" + target.Replace("'", "''") + "' | Out-Null";
            var info = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
                { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-NonInteractive"); info.ArgumentList.Add("-EncodedCommand");
            info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            using var process = System.Diagnostics.Process.Start(info) ?? throw new IOException("Cannot create junction fixture.");
            if (!process.WaitForExit(15000)) { process.Kill(); throw new IOException("Junction fixture timed out."); }
            if (process.ExitCode != 0) throw new IOException("Junction fixture failed.");
        }
    }

    private sealed class Fixture
    {
        public string State { get; }
        public LauncherCache Cache { get; }
        public LauncherUpdateStore Store { get; }
        public LauncherCacheContext Context { get; }
        public LauncherCacheProcessSnapshot Processes { get; set; } = new(true, []);
        public Action<string, int>? AfterDelete { get; set; }
        public Func<int, LauncherCacheProcessSnapshot>? ProcessReader { get; set; }
        public int ProcessReads { get; private set; }
        private DateTimeOffset time = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Time => time;
        public Fixture(string state)
        {
            State = state; Directory.CreateDirectory(state); Store = new(state);
            Cache = new(state, utcNow: () => time, inspectProcesses: _ => Task.FromResult(ProcessReader?.Invoke(++ProcessReads) ?? Processes), afterFileDeleted: (path, count) => AfterDelete?.Invoke(path, count));
            Context = new("", "", Path.Combine(state, "entry.exe"), "99.0.0");
        }
        public void Advance(int days) => time = time.AddDays(days);
        public (string Assets, string App) CreateSnapshot(string version)
        {
            var assets = Path.Combine(State, "versions", version + "-" + Guid.NewGuid().ToString("N")[..16]);
            var app = Path.Combine(assets, "app-" + new string('b', 16)); Directory.CreateDirectory(app);
            File.WriteAllText(Path.Combine(app, "run.py"), "print('fixture')"); File.WriteAllText(Path.Combine(assets, "payload.zip"), "fixture payload");
            return (assets, app);
        }
        public async Task<(string Assets, string App)> Snapshot(string version, string? packageSha256 = null)
        {
            var entry = CreateSnapshot(version); await Cache.RegisterSnapshotAsync(version, entry.Assets, entry.App, newlyCreated: true, packageSha256: packageSha256); return entry;
        }
        public async Task<InstalledLauncher> Package(string version)
        {
            var bytes = Encoding.UTF8.GetBytes("package " + version); var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var package = new InstalledLauncher(version, Store.CandidatePath(version, sha), bytes.Length, sha);
            Directory.CreateDirectory(Path.GetDirectoryName(package.Executable)!); await File.WriteAllBytesAsync(package.Executable, bytes);
            await Cache.RegisterPackageAsync(package); return package;
        }
        public void Age(string directory)
        {
            var index = JsonFile.Read<LauncherCacheIndex>(Cache.IndexPath); index.Entries.Single(entry => entry.Directory == directory).LastUsedAt = time.AddDays(-8); JsonFile.Write(Cache.IndexPath, index);
        }
    }
}
