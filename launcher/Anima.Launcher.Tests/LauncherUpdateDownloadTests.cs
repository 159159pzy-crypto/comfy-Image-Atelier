using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Anima.Launcher.Core;

/// <summary>Transport fixtures exercise updater publication gates, using only isolated local test binaries.</summary>
internal static class LauncherUpdateDownloadTests
{
    public static async Task<int> Run(string work)
    {
        var passed = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL: " + label); passed++; Console.WriteLine("PASS: " + label); }
        async Task Reject<T>(Func<Task> operation, string label) where T : Exception
        {
            try { await operation(); } catch (T) { Check(true, label); return; }
            throw new Exception("Expected " + typeof(T).Name + ": " + label);
        }
        var directory = Path.Combine(work, "update-downloads");
        Directory.CreateDirectory(directory);
        var source = Path.ChangeExtension(Assembly.GetEntryAssembly()!.Location, ".exe");
        var bytes = await File.ReadAllBytesAsync(source);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var build = JsonSerializer.SerializeToElement(new
        {
            version = "1.2.0", runtimeIdentifier = "win-x64", packageFormat = "single-exe-v1",
            sourceCommit = new string('a', 40), payloadSha256 = new string('b', 64),
        });
        var url = "https://github.com/" + LauncherUpdates.Repository + "/releases/download/1.2.0/";
        var release = new LauncherRelease("1.2.0", "1.2.0", "offline fixture", DateTimeOffset.UtcNow,
            url + "AnimaRandomStudio.exe", bytes.Length, sha, url + "build-info.json", build);

        var offlineCalls = 0;
        using (var offline = new LauncherUpdates(Path.Combine(directory, "offline"), source, "1.1.0", handler: new Handler(_ =>
        { offlineCalls++; throw new HttpRequestException("fixture offline"); })))
        {
            JsonFile.Write(Path.Combine(offline.Store.Root, "release-cache.json"), new LauncherReleaseCache(DateTimeOffset.UtcNow, release));
            Check((await offline.CheckAsync(false, default))?.Version == release.Version && offlineCalls == 0,
                "an offline automatic updater reuses its recent release cache without network access");
            await Reject<HttpRequestException>(() => offline.CheckAsync(true, default), "an explicit offline update check returns a retryable network error");
            Check(offline.GetCachedRelease()?.Sha256 == sha && !File.Exists(offline.Store.StatePath),
                "failed offline check preserves cached release information without activating anything");
        }

        async Task<(LauncherUpdates Client, string Target, InstalledLauncher Old, string Config)> Prepare(string name, JsonElement? packageBuild = null)
        {
            var client = new LauncherUpdates(Path.Combine(directory, name), source, "1.1.0");
            var old = await client.ImportAsync(source, "1.1.0", default);
            client.Store.Save(new LauncherUpdateState { Active = old });
            var configPath = Path.Combine(client.Store.StateDirectory, "launcher.json");
            JsonFile.Write(configPath, new LauncherConfig { AppRoot = Path.Combine(client.Store.StateDirectory, "versions", "old"), DataDir = Path.Combine(client.Store.StateDirectory, "user-data"), PinnedAppRoot = "keep-pin" });
            var exact = File.ReadAllText(configPath);
            var target = client.Store.CandidatePath(release.Version, sha);
            var targetDirectory = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(targetDirectory);
            // Only test executables use sidecars. The production EXE remains a
            // true self-contained package; its packaging smoke test is separate.
            foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory))
                File.Copy(file, Path.Combine(targetDirectory, Path.GetFileName(file)), true);
            File.WriteAllText(Path.Combine(targetDirectory, ".launcher-update-test-fixture"), "package");
            JsonFile.Write(Path.Combine(targetDirectory, "fixture-build-info.json"), packageBuild ?? build);
            return (client, target, old, exact);
        }
        void CheckUnchanged((LauncherUpdates Client, string Target, InstalledLauncher Old, string Config) fixture, string label)
        {
            var state = fixture.Client.Store.Load();
            Check(state.Active == fixture.Old && state.PendingTransactionId is null
                && File.ReadAllText(Path.Combine(fixture.Client.Store.StateDirectory, "launcher.json")) == fixture.Config, label);
        }

        var paused = await Prepare("resume");
        using (paused.Client)
        {
            using (var cancellation = new CancellationTokenSource())
            using (var download = new Downloads(handler: new Handler(_ => new(HttpStatusCode.OK) { Content = new StreamContent(new PausingStream(bytes, cancellation)) })))
                await Reject<OperationCanceledException>(() => paused.Client.DownloadAsync(release, download, null, cancellation.Token),
                    "pausing a software update propagates cancellation before package execution");
            Check(!File.Exists(paused.Target) && new FileInfo(paused.Target + ".part").Length == 1000
                && paused.Client.GetDownloaded() is null, "paused update retains resumable bytes without publishing a ready version");
            CheckUnchanged(paused, "paused software update preserves active executable, configuration and rollback pin");
            var resumedOffset = -1L;
            using (var download = new Downloads(handler: new Handler(request =>
            {
                resumedOffset = request.Headers.Range?.Ranges.Single().From ?? -1;
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(bytes[1000..]) };
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(1000, bytes.Length - 1, bytes.Length);
                return response;
            })))
            {
                var ready = await paused.Client.DownloadAsync(release, download, null, default);
                Check(resumedOffset == 1000 && ready == paused.Client.GetDownloaded()
                    && await Downloads.Verify(ready.Executable, ready.Size, ready.Sha256, default),
                    "software update resumes by Range and publishes readiness only after hash and package validation");
            }
            CheckUnchanged(paused, "a downloaded and inspected update remains inactive until explicit restart");
            var tampered = bytes.ToArray(); tampered[^1] ^= 1;
            await File.WriteAllBytesAsync(paused.Target, tampered);
            var config = JsonFile.Read<LauncherConfig>(Path.Combine(paused.Client.Store.StateDirectory, "launcher.json"));
            await Reject<InvalidDataException>(() => paused.Client.PrepareAsync(paused.Client.GetDownloaded()!, config, false, default),
                "tampering after a successful download is rejected before creating a restart transaction");
            CheckUnchanged(paused, "a corrupted downloaded candidate cannot change the active executable or configuration");
            var backups = Directory.GetFiles(Path.GetDirectoryName(paused.Target)!, "AnimaRandomStudio.exe.corrupt-*");
            Check(paused.Client.GetDownloaded() is null && paused.Client.Store.Load().Downloaded is null
                && !File.Exists(paused.Target) && backups.Length == 1 && (await File.ReadAllBytesAsync(backups[0])).SequenceEqual(tampered),
                "a damaged ready candidate is preserved separately and no longer blocks the download button");
            using (var download = new Downloads(handler: new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })))
            {
                var repaired = await paused.Client.DownloadAsync(release, download, null, default);
                Check(repaired == paused.Client.GetDownloaded() && await Downloads.Verify(repaired.Executable, repaired.Size, repaired.Sha256, default),
                    "a rejected damaged candidate can be downloaded and inspected again inside the updater");
                await File.WriteAllBytesAsync(paused.Target, tampered);
                repaired = await paused.Client.DownloadAsync(release, download, null, default);
                Check(await Downloads.Verify(repaired.Executable, repaired.Size, repaired.Sha256, default)
                    && Directory.GetFiles(Path.GetDirectoryName(paused.Target)!, "AnimaRandomStudio.exe.corrupt-*").Length == 2,
                    "downloading again repairs an existing damaged candidate while retaining both corrupt backups");
            }
            CheckUnchanged(paused, "repairing the download cache never activates a version or edits user configuration");
        }

        foreach (var role in new[] { "active", "previous", "pending" })
        {
            var protectedFixture = await Prepare("protected-" + role);
            using var client = protectedFixture.Client;
            var candidate = new InstalledLauncher(release.Version, protectedFixture.Target, release.Size, release.Sha256);
            var damaged = bytes.ToArray(); damaged[^1] ^= 1;
            await File.WriteAllBytesAsync(candidate.Executable, damaged);
            var state = client.Store.Load(); state.Downloaded = candidate;
            if (role == "active") state.Active = candidate;
            else if (role == "previous") state.Previous = candidate;
            else
            {
                var transaction = new UpdateTransaction { Id = Guid.NewGuid().ToString("N"), Token = new string('d', 64),
                    Candidate = candidate, Previous = protectedFixture.Old,
                    PreviousConfig = JsonFile.Read<LauncherConfig>(Path.Combine(client.Store.StateDirectory, "launcher.json")) };
                client.Store.WriteTransaction(transaction); state.PendingTransactionId = transaction.Id;
            }
            client.Store.Save(state);
            var exactState = File.ReadAllText(client.Store.StatePath);
            using var download = new Downloads(handler: new Handler(_ => throw new Exception("Protected cache must not reach the network.")));
            await Reject<IOException>(() => client.DownloadAsync(release, download, null, default), "damaged " + role + " executable cannot be replaced as a download cache");
            Check((await File.ReadAllBytesAsync(candidate.Executable)).SequenceEqual(damaged)
                && Directory.GetFiles(Path.GetDirectoryName(candidate.Executable)!, "AnimaRandomStudio.exe.corrupt-*").Length == 0
                && File.ReadAllText(client.Store.StatePath) == exactState,
                "download repair preserves the " + role + " executable and its durable recovery state");
        }

        var corrupt = await Prepare("bad-sha");
        using (corrupt.Client)
        {
            var badBytes = bytes.ToArray(); badBytes[^1] ^= 1;
            using var download = new Downloads(handler: new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(badBytes) }));
            await Reject<InvalidDataException>(() => corrupt.Client.DownloadAsync(release, download, null, default),
                "software update rejects corrupt executable bytes before launching package inspection");
            Check(corrupt.Client.GetDownloaded() is null && !File.Exists(corrupt.Target)
                && !File.Exists(Path.Combine(Path.GetDirectoryName(corrupt.Target)!, "fixture-package-inspected.txt")),
                "a bad update SHA never becomes an executable candidate or an inspected package");
            CheckUnchanged(corrupt, "a rejected executable download preserves the installed version and user configuration");
        }

        var otherBuild = JsonSerializer.SerializeToElement(new
        {
            version = "1.2.0", runtimeIdentifier = "win-x64", packageFormat = "single-exe-v1",
            sourceCommit = new string('c', 40), payloadSha256 = new string('b', 64),
        });
        var wrongMetadata = await Prepare("wrong-metadata", otherBuild);
        using (wrongMetadata.Client)
        using (var download = new Downloads(handler: new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })))
        {
            await Reject<InvalidDataException>(() => wrongMetadata.Client.DownloadAsync(release, download, null, default),
                "software updater rejects a valid EXE whose embedded build provenance differs from the release");
            Check(wrongMetadata.Client.GetDownloaded() is null, "failed package provenance does not publish downloaded readiness");
            CheckUnchanged(wrongMetadata, "package provenance rejection preserves the active executable and configuration");
        }
        return passed;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }
    private sealed class PausingStream(byte[] bytes, CancellationTokenSource pause) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (Position >= 1000) pause.Cancel();
            ct.ThrowIfCancellationRequested();
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, 1000)], ct);
        }
    }
}
