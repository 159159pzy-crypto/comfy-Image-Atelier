using Anima.Launcher.Core;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

public static class DownloadDiskSpaceTests
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, uint inputSize,
        IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);

    public static async Task<int> Run(string work)
    {
        var passed = 0;
        void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); passed++; Console.WriteLine("PASS: " + label); }
        var root = Path.Combine(work, "disk-space"); Directory.CreateDirectory(root);
        var sha = new string('a', 64);
        void AriaMetadata(string destination, long size)
        {
            JsonFile.Write(DownloadCheckpoint.AriaState(destination), new { SchemaVersion = 1, Sha256 = sha, Size = size,
                SourceUrl = "https://example.test/file", ImportedPrefixLength = 0, ImportedPrefixSha256 = (string?)null, LegacyETag = (string?)null, EngineStarted = true });
            const uint piece = 4096;
            var bitmap = (int)(((size + piece - 1) / piece + 7) / 8);
            var control = new byte[38 + bitmap];
            BinaryPrimitives.WriteUInt16BigEndian(control, 1);
            BinaryPrimitives.WriteUInt32BigEndian(control.AsSpan(10), piece);
            BinaryPrimitives.WriteUInt64BigEndian(control.AsSpan(14), (ulong)size);
            BinaryPrimitives.WriteUInt32BigEndian(control.AsSpan(30), (uint)bitmap);
            File.WriteAllBytes(DownloadCheckpoint.AriaControl(destination), control);
        }
        if (OperatingSystem.IsWindows())
        {
            var sparseDestination = Path.Combine(root, "sparse.bin");
            const long sparseSize = 64L * 1024 * 1024;
            using (var stream = new FileStream(DownloadCheckpoint.AriaData(sparseDestination), FileMode.CreateNew, FileAccess.ReadWrite))
            {
                if (!DeviceIoControl(stream.SafeFileHandle, 0x000900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
                stream.SetLength(sparseSize); stream.Write(new byte[4096]); stream.Flush(true);
            }
            AriaMetadata(sparseDestination, sparseSize);
            var sparseAllocated = DownloadDiskSpace.AllocatedBytes(DownloadCheckpoint.AriaData(sparseDestination));
            Check(sparseAllocated > 0 && sparseAllocated < sparseSize
                && DownloadDiskSpace.RemainingAllocationBytes(sparseDestination, sparseSize, sha) == sparseSize - sparseAllocated,
                "real sparse allocation is measured independently of its 64 MiB logical length");
            Check(DownloadDiskSpace.ReusableAllocatedBytes(sparseDestination, sparseSize, new string('b', 64)) == 0,
                "different checkpoint identity receives no physical-allocation credit");
            var denseDestination = Path.Combine(root, "dense.bin");
            const int denseSize = 128 * 1024;
            File.WriteAllBytes(DownloadCheckpoint.AriaData(denseDestination), new byte[denseSize]);
            AriaMetadata(denseDestination, denseSize);
            Check(DownloadCheckpoint.RetainedBytes(denseDestination, denseSize, sha) == 0
                && DownloadDiskSpace.ReusableAllocatedBytes(denseDestination, denseSize, sha) == denseSize,
                "dense aria2 preallocation reduces disk demand without claiming completed network pieces");
            var item = new InstallationDownload(denseDestination, denseSize, sha, true);
            var plan = new InstallationPlan("{}", root, Path.Combine(root, "runtime"), "anima", false, 0, 0, 0, []) { Downloads = [item] };
            var refreshed = await InstallationPlanner.RefreshSpaceEstimateAsync(plan, default);
            Check(refreshed.EnvironmentBytes == 0 && refreshed.EstimatedDownloadBytes == denseSize,
                "installation disk estimate and network transfer estimate remain separate after refresh");

            var existingVolumes = DriveInfo.GetDrives().Select(drive => drive.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missingVolume = Enumerable.Range('D', 'Z' - 'D' + 1).Select(letter => $"{(char)letter}:\\")
                .First(volume => !existingVolumes.Contains(volume));
            var unavailableDestination = Path.Combine(missingVolume, "anima-offline-volume-fixture", "model.bin");
            var unavailablePlan = plan with { Downloads = [new(unavailableDestination, denseSize, sha, true)] };
            var estimate = InstallationPlanner.EstimateVolumes(unavailablePlan).Single(volume => volume.Volume == missingVolume);
            Check(estimate.AvailableBytes is null && estimate.RequiredBytes == denseSize + DownloadDiskSpace.SafetyMargin,
                "offline volume remains visible in installation planning with its full budget and unknown free bytes");
            var unavailablePreflight = await InstallationPlanner.PreflightAsync(unavailablePlan, default);
            Check(!unavailablePreflight.CanInstall && unavailablePreflight.Issues.Any(issue => issue.Code == "volume_unavailable" && issue.Blocking)
                && unavailablePreflight.Volumes.Single(volume => volume.Volume == missingVolume).AvailableBytes is null,
                "offline installation volume returns a structured blocking preflight issue instead of throwing");
            try
            {
                await using var rejected = await DownloadDiskSpace.AcquireAsync(unavailableDestination, denseSize);
                throw new Exception("Expected unavailable-volume admission rejection");
            }
            catch (IOException) { Check(true, "download admission still rejects an unresolved physical volume despite display fallback"); }
        }
        long free = DownloadDiskSpace.SafetyMargin + 1000;
        var allocations = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var options = new DownloadDiskSpaceOptions { LedgerRoot = Path.Combine(root, "ledger"), AvailableBytes = _ => free,
            AllocatedBytes = path => allocations.GetValueOrDefault(path) };
        var first = Path.Combine(root, "budget-one.bin");
        await using (var a = await DownloadDiskSpace.AcquireAsync(first, 800, ct: default, options: options))
        {
            try
            {
                await using var rejected = await DownloadDiskSpace.AcquireAsync(Path.Combine(root, "too-large.bin"), 300, options: options);
                throw new Exception("Expected shared-space rejection");
            }
            catch (InsufficientSpaceException) { Check(true, "one volume cannot promise the same remaining free space to a second task"); }
            allocations[first + ".part"] = 400; free -= 400;
            await a.CheckAsync();
            await using var b = await DownloadDiskSpace.AcquireAsync(Path.Combine(root, "budget-two.bin"), 200, options: options);
            Check(await DownloadDiskSpace.AvailableForPlanningAsync(root, options: options) == DownloadDiskSpace.SafetyMargin,
                "claims decrease as real allocation consumes physical free bytes without double reservation");
        }
        Check(await DownloadDiskSpace.AvailableForPlanningAsync(root, options: options) == free,
            "completed reservations release their unallocated budget");

        allocations.Clear(); free = DownloadDiskSpace.SafetyMargin + 1100;
        var old = Path.Combine(root, "old-native.bin");
        allocations[DownloadCheckpoint.AriaData(old)] = 400;
        var freshPath = old + ".http-restart.part";
        allocations[freshPath] = 200;
        await using (var primary = await DownloadDiskSpace.AcquireAsync(old, 1000, DownloadCheckpoint.AriaData(old), options: options))
        await using (var fresh = await primary.ReserveFreshFileAsync(freshPath, 1000))
        {
            Check(true, "independent full restart freezes old future growth while retaining old physical bytes");
            allocations.Remove(freshPath); free += 200;
            await fresh.SetPrimaryPathAsync(freshPath); // Removed old generation before writing new bytes.
            allocations[freshPath] = 500; free -= 500;
            await using var other = await DownloadDiskSpace.AcquireAsync(Path.Combine(root, "restart-neighbor.bin"), 300, options: options);
            Check(await DownloadDiskSpace.AvailableForPlanningAsync(root, options: options) == DownloadDiskSpace.SafetyMargin,
                "deleted fresh generation resets its allocation baseline before new download growth");
            allocations.Remove(freshPath); allocations[old] = 500;
            await fresh.CheckAsync();
            Check(true, "fresh publication alias retains allocation credit during final rename");
        }
        Check(Directory.GetFiles(options.LedgerRoot!, "*.json").All(path => System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetArrayLength() == 0),
            "all finished space claims are removed from the shared ledger");
        var payload = new byte[8192];
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload));
        var package = new ModelItem("space-package", "Space package", "tagger", "wd_taggers", "space-package", [], payload.Length * 2, "", [], "test", "https://example.test/license")
        {
            Revision = "fixture", Files = [new("first.bin", payload.Length, digest, ["https://example.test/first"]),
                new("second.bin", payload.Length, digest, ["https://example.test/second"])]
        };
        var packageDestination = Path.Combine(root, "packages", "space-package");
        var staging = ModelPackages.Staging(package, packageDestination);
        Directory.CreateDirectory(staging);
        await File.WriteAllBytesAsync(Path.Combine(staging, "first.bin"), payload);
        _ = await ModelPackages.State(package, staging, default);
        Check(ModelPackages.RemainingAllocationBytes(package, packageDestination) == payload.Length,
            "package space estimate reuses completed members without counting their bytes twice");
        var memberPath = Path.Combine(staging, "first.bin");
        var originalWrite = File.GetLastWriteTimeUtc(memberPath);
        var originalCreated = File.GetCreationTimeUtc(memberPath);
        payload[0] = 1;
        await File.WriteAllBytesAsync(memberPath, payload);
        // Deterministically reproduce the same-stamp overwrite observed in the full
        // suite, instead of relying on the filesystem clock advancing between writes.
        File.SetLastWriteTimeUtc(memberPath, originalWrite);
        File.SetCreationTimeUtc(memberPath, originalCreated);
        Check(await ModelPackages.State(package, staging, default) == "哈希异常"
            && ModelPackages.RemainingAllocationBytes(package, packageDestination) == payload.Length * 2,
            "known damaged package member reserves a complete replacement while preserving its existing bytes");
        var largePayload = new byte[2 * 1024 * 1024];
        var largeDigest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(largePayload));
        var largePackage = new ModelItem("stamp-package", "Stamp package", "tagger", "wd_taggers", "stamp-package", [],
            largePayload.Length, "", [], "test", "https://example.com/license")
        { Revision = new string('a', 40), Files = [new("model.safetensors", largePayload.Length, largeDigest, ["https://example.com/model.safetensors"])] };
        var largeDestination = Path.Combine(root, "stamp-models", largePackage.RelativePath);
        Directory.CreateDirectory(largeDestination);
        var largeMember = Path.Combine(largeDestination, largePackage.Files[0].Path);
        await File.WriteAllBytesAsync(largeMember, largePayload);
        Check(await ModelPackages.State(largePackage, largeDestination, default) == "已校验", "large model package records its initial verified identity");
        void CorruptWithoutChangingStamp(string path)
        {
            var write = File.GetLastWriteTimeUtc(path);
            var created = File.GetCreationTimeUtc(path);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read)) stream.WriteByte(1);
            File.SetLastWriteTimeUtc(path, write); File.SetCreationTimeUtc(path, created);
        }
        CorruptWithoutChangingStamp(largeMember);
        var scan = await Inventory.Scan(new(root, "", new() { ["wd_taggers"] = [Path.GetDirectoryName(largeDestination)!] }),
            new(1, [largePackage]), true, default);
        Check(scan.Single().State == "哈希异常", "explicit inventory verification rehashes large members despite identical filesystem stamps");
        Check(await ModelPackages.State(largePackage, largeDestination, default) == "哈希异常",
            "known invalid large member cannot fall back to an older verified stamp");
        await File.WriteAllBytesAsync(largeMember, largePayload);
        _ = await ModelPackages.State(largePackage, largeDestination, default, forceVerification: true);
        CorruptWithoutChangingStamp(largeMember);
        using var handler = new PackageBytesHandler(largePayload);
        using var downloads = new Downloads(handler: handler);
        await ModelPackages.Fetch(downloads, largePackage, largeDestination, new() { SourceId = "official" }, null, default);
        Check(handler.Requests == 1 && await ModelPackages.State(largePackage, largeDestination, default, forceVerification: true) == "已校验",
            "package fetch revalidates an apparently cached final package before deciding to skip repair");
        var publicationDestination = Path.Combine(root, "stamp-models", "publication");
        var publicationStaging = ModelPackages.Staging(largePackage, publicationDestination);
        Directory.CreateDirectory(publicationStaging);
        var publicationMember = Path.Combine(publicationStaging, largePackage.Files[0].Path);
        await File.WriteAllBytesAsync(publicationMember, largePayload);
        _ = await ModelPackages.State(largePackage, publicationStaging, default);
        var changedAfterChildVerification = false;
        var changedProgress = new CallbackProgress(value =>
        {
            if (changedAfterChildVerification || value.Phase != "Completed" || !File.Exists(publicationMember)) return;
            changedAfterChildVerification = true;
            CorruptWithoutChangingStamp(publicationMember);
        });
        try
        {
            await ModelPackages.Fetch(downloads, largePackage, publicationDestination, new() { SourceId = "official" }, changedProgress, default);
            throw new Exception("Expected pre-publication verification failure");
        }
        catch (InvalidDataException)
        {
            Check(changedAfterChildVerification && !Directory.Exists(publicationDestination),
                "publication rehash rejects same-stamp changes after child verification and keeps the package invisible");
        }
        return passed;
    }

    private sealed class PackageBytesHandler(byte[] payload) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Requests++; return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }); }
    }
    private sealed class CallbackProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    { public void Report(DownloadProgress value) => report(value); }
}
