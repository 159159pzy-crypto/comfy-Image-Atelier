using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Anima.Launcher.Core;

internal static class LauncherUpdateTests
{
    public static async Task<int> Run(string work)
    {
        var passed = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL: " + label); passed++; Console.WriteLine("PASS: " + label); }
        async Task Reject(Func<Task> action, string label)
        {
            try { await action(); } catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidOperationException or HttpRequestException)
            { Check(true, label); return; }
            throw new Exception("Expected rejection: " + label);
        }
        var directory = Path.Combine(work, "launcher-update");
        Directory.CreateDirectory(directory);
        var executable = Path.Combine(directory, "user renamed launcher.exe");
        await File.WriteAllTextAsync(executable, "old fixture binary");
        var stateDir = Path.Combine(directory, "state");
        using var noUpdate = new LauncherUpdates(stateDir, executable, "1.1.0", handler: new UpdateHandler(_ => Json(new
        { draft = false, prerelease = false, tag_name = "1.1.0", assets = Array.Empty<object>() })));
        Check(await noUpdate.CheckAsync(true, default) is null, "current legacy release without new EXE contract is already up to date");
        Check(LauncherUpdates.ParseVersion("1.10.0") > LauncherUpdates.ParseVersion("1.9.9"), "stable versions compare numerically");
        foreach (var text in new[] { "v1.0.0", "1.0", "1.0.0-beta", "../1.0.0", "01.0.0" })
            await Reject(() => Task.FromResult(LauncherUpdates.ParseVersion(text)), "invalid version rejected: " + text);
        await Reject(() => Task.FromResult(LauncherUpdates.ReadChecksum(new string('a', 64) + "  AnimaRandomStudio.exe\n" + new string('b', 64) + "  AnimaRandomStudio.exe", "AnimaRandomStudio.exe")), "duplicate executable checksum rejected");
        var sha = new string('a', 64);
        var buildInfo = new { version = "1.2.0", runtimeIdentifier = "win-x64", packageFormat = "single-exe-v1", sourceCommit = new string('b', 40), payloadSha256 = new string('c', 64) };
        var officialBase = "https://github.com/" + LauncherUpdates.Repository + "/releases/download/1.2.0/";
        object Asset(string name, string? url = null) => new { name, browser_download_url = url ?? officialBase + name, size = 1024 };
        object Release(bool prerelease = false, string? exeUrl = null) => new { draft = false, prerelease, tag_name = "1.2.0", body = "new release", published_at = "2026-10-23T00:00:00Z",
            assets = new[] { Asset("AnimaRandomStudio.exe", exeUrl), Asset("SHA256SUMS.txt"), Asset("build-info.json") } };
        var seen = 0;
        var requestedUrls = new List<string>();
        HttpResponseMessage OfficialResponse(HttpRequestMessage request) =>
            request.RequestUri!.AbsolutePath.EndsWith("/latest") ? Json(Release())
                : request.RequestUri.AbsolutePath.EndsWith("SHA256SUMS.txt") ? Text(sha + "  AnimaRandomStudio.exe\n" + new string('f', 64) + "  AnimaRandomStudio-portable-win-x64.zip\n")
                : Json(buildInfo);
        var releaseHandler = new UpdateHandler(request =>
        {
            seen++;
            requestedUrls.Add(request.RequestUri!.AbsoluteUri);
            return OfficialResponse(request);
        });
        using var client = new LauncherUpdates(Path.Combine(directory, "official"), executable, "1.1.0", handler: releaseHandler);
        var release = await client.CheckAsync(true, default);
        Check(release is { Version: "1.2.0", Size: 1024, Notes: "new release" } && release.Sha256 == sha, "official release binds EXE size and SHA to build metadata");
        Check(requestedUrls[0] == "https://api.github.com/repos/159159pzy-crypto/comfy-Image-Atelier/releases/latest"
            && requestedUrls.Skip(1).All(url => url.StartsWith("https://github.com/159159pzy-crypto/comfy-Image-Atelier/releases/download/", StringComparison.Ordinal)),
            "update checks and metadata downloads use the new repository");
        await Reject(() => { LauncherUpdates.ValidateRelease(release! with { ExeUrl = "https://example.com/AnimaRandomStudio.exe" }); return Task.CompletedTask; }, "tampered cached release origin is revalidated before execution");
        var calls = seen;
        Check((await client.CheckAsync(false, default))?.Version == "1.2.0" && seen == calls, "automatic checks reuse the 24 hour cache");
        await client.CheckAsync(true, default);
        Check(seen == calls + 3, "manual check bypasses release cache");
        var oldBase = "https://github.com/159159pzy-crypto/comfyui-random-image-generation/releases/download/1.2.0/";
        var oldRelease = release! with { ExeUrl = oldBase + "AnimaRandomStudio.exe", BuildInfoUrl = oldBase + "build-info.json" };
        foreach (var legacyRelease in new LauncherRelease?[] { oldRelease, null })
        {
            var migrationCalls = 0;
            using var migrated = new LauncherUpdates(Path.Combine(directory, legacyRelease is null ? "legacy-negative-cache" : "legacy-release-cache"),
                executable, "1.1.0", handler: new UpdateHandler(request => { migrationCalls++; return OfficialResponse(request); }));
            var legacyPath = Path.Combine(migrated.Store.Root, "release-cache.json");
            JsonFile.Write(legacyPath, new LauncherReleaseCache(DateTimeOffset.UtcNow, legacyRelease));
            var legacyText = File.ReadAllText(legacyPath);
            Check(migrated.GetCachedRelease() is null, "migration does not expose the old repository cache");
            Check((await migrated.CheckAsync(false, default))?.ExeUrl == release!.ExeUrl && migrationCalls == 3,
                legacyRelease is null ? "old no-update cache cannot suppress the new repository check" : "old release cache triggers a fresh new repository check");
            Check(File.ReadAllText(legacyPath) == legacyText && !File.Exists(migrated.Store.StatePath),
                "repository cache migration preserves legacy cache and installed update state");
        }
        var offlineCalls = 0;
        using (var offline = new LauncherUpdates(client.Store.StateDirectory, executable, "1.1.0", handler: new UpdateHandler(_ =>
        { offlineCalls++; throw new HttpRequestException("fixture offline"); })))
        {
            Check(offline.GetCachedRelease()?.Sha256 == sha && (await offline.CheckAsync(false, default))?.Sha256 == sha && offlineCalls == 0,
                "new repository release cache survives restart and works offline");
        }
        var rejectedCacheCalls = 0;
        using (var poisoned = new LauncherUpdates(Path.Combine(directory, "foreign-scoped-cache"), executable, "1.1.0",
            handler: new UpdateHandler(request => { rejectedCacheCalls++; return OfficialResponse(request); })))
        {
            JsonFile.Write(Path.Combine(poisoned.Store.Root, "release-cache-" + LauncherUpdates.Repository.Replace('/', '-') + ".json"),
                new LauncherReleaseCache(DateTimeOffset.UtcNow, oldRelease));
            Check(poisoned.GetCachedRelease() is null && (await poisoned.CheckAsync(false, default))?.Sha256 == sha && rejectedCacheCalls == 3,
                "old repository assets copied into the new cache are rejected and refreshed");
        }
        await Reject(() => { LauncherUpdates.ValidateRelease(oldRelease); return Task.CompletedTask; },
            "old repository assets cannot be installed through the new update source");
        using var legacyAsset = new LauncherUpdates(Path.Combine(directory, "legacy-asset"), executable, "1.1.0", handler: new UpdateHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/latest") ? Json(Release(exeUrl: oldRelease.ExeUrl)) : OfficialResponse(request)));
        await Reject(() => legacyAsset.CheckAsync(true, default), "new repository release cannot advertise an old repository executable");
        using var foreign = new LauncherUpdates(Path.Combine(directory, "foreign"), executable, "1.1.0", handler: new UpdateHandler(_ => Json(Release(exeUrl: "https://example.com/AnimaRandomStudio.exe"))));
        await Reject(() => foreign.CheckAsync(true, default), "foreign release executable URL rejected before metadata download");
        using var preview = new LauncherUpdates(Path.Combine(directory, "preview"), executable, "1.1.0", handler: new UpdateHandler(_ => Json(Release(prerelease: true))));
        await Reject(() => preview.CheckAsync(true, default), "preview releases cannot enter the stable update channel");
        using var rateLimited = new LauncherUpdates(Path.Combine(directory, "rate-limit"), executable, "1.1.0", handler: new UpdateHandler(_ => new(HttpStatusCode.Forbidden)));
        await Reject(() => rateLimited.CheckAsync(true, default), "GitHub rate limits remain retryable failures");
        using var insecure = new LauncherUpdates(Path.Combine(directory, "redirect"), executable, "1.1.0", handler: new UpdateHandler(_ =>
        { var result = new HttpResponseMessage(HttpStatusCode.Redirect); result.Headers.Location = new("http://example.com/release"); return result; }));
        await Reject(() => insecure.CheckAsync(true, default), "insecure metadata redirects are rejected");

        using var local = new LauncherUpdates(stateDir, executable, "1.1.0");
        var old = await local.ImportAsync(executable, "1.1.0", default);
        Check(File.Exists(old.Executable) && !LauncherUpdateStore.SamePath(old.Executable, executable)
            && await Downloads.Verify(old.Executable, old.Size, old.Sha256, default), "current renamed EXE is preserved in immutable managed storage");
        await File.WriteAllTextAsync(old.Executable, "corrupt cached bytes");
        await Reject(() => local.ImportAsync(executable, "1.1.0", default), "ordinary import preserves a corrupt cache for diagnosis");
        var repaired = await local.ImportAsync(executable, "1.1.0", default, allowRepair: true);
        Check(repaired == old && await Downloads.Verify(old.Executable, old.Size, old.Sha256, default)
            && Directory.GetFiles(Path.GetDirectoryName(old.Executable)!, "AnimaRandomStudio.exe.corrupt-*").Length == 1,
            "verified same-version entry repairs cache while preserving corrupt backup");
        var config = new LauncherConfig { AppRoot = Path.Combine(stateDir, "versions", "old"), DataDir = Path.Combine(stateDir, "data"), PinnedAppRoot = "prior pin" };
        JsonFile.Write(Path.Combine(stateDir, "launcher.json"), config);
        var exactConfig = File.ReadAllText(Path.Combine(stateDir, "launcher.json"));
        var nextSource = Path.Combine(directory, "next.exe");
        await File.WriteAllTextAsync(nextSource, "new fixture binary");
        var next = await local.ImportAsync(nextSource, "1.2.0", default);
        var saved = local.Store.Load(); saved.Active = old; local.Store.Save(saved);
        var transaction = await local.PrepareAsync(next, config, false, default);
        var restored = local.Store.ReadTransaction(transaction.Id, transaction.Token);
        Check(restored.Previous == old && restored.Candidate == next && restored.PreviousConfigText == exactConfig && !restored.WorkbenchWasRunning,
            "durable transaction retains previous EXE, exact config and stopped workbench state");
        Check(File.ReadAllText(Path.Combine(stateDir, "launcher.json")) == exactConfig && local.Store.Load().Active == old,
            "preparation never switches config or active EXE");
        await Reject(() => Task.FromResult(local.Store.ReadTransaction(transaction.Id, new string('0', 64))), "transaction token mismatch rejected");
        await Reject(() => local.PrepareAsync(next, config, false, default), "a second transaction cannot overwrite pending recovery");
        await Reject(() => Task.FromResult(local.Store.TransactionPath("../escape")), "transaction traversal rejected");
        await Reject(() => { local.Store.ValidateInstalled(next with { Executable = nextSource }, true); return Task.CompletedTask; }, "candidate path must match managed version and hash directory");
        Check(File.Exists(executable) && File.ReadAllText(executable) == "old fixture binary", "managed import does not alter original launcher");
        var identity = LauncherUpdateStore.Identity(System.Diagnostics.Process.GetCurrentProcess());
        Check(LauncherUpdateStore.IsRunning(identity) && !LauncherUpdateStore.IsRunning(identity with { StartTimeUtcTicks = identity.StartTimeUtcTicks - 1 }),
            "launcher identity rejects recycled process IDs");
        var corrupt = local.Store.Load(); corrupt.ProtocolVersion = 999; JsonFile.Write(local.Store.StatePath, corrupt);
        await Reject(() => Task.FromResult(local.Store.Load()), "unknown durable state versions fail closed");
        var concurrent = new LauncherUpdateStore(Path.Combine(directory, "concurrent"));
        await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() => concurrent.Mutate(value =>
        { value.EntryPaths.Add("entry-" + i); return true; }))));
        Check(concurrent.Load().EntryPaths.Distinct().Count() == 12, "concurrent state mutations retain every entry without lost read-mod-write updates");
        var currentSetup = new LauncherConfig { AppRoot = Path.Combine(stateDir, "versions", "new"), PinnedAppRoot = "" };
        var setupDraft = new LauncherConfig { AppRoot = Path.Combine(stateDir, "versions", "old"), PinnedAppRoot = "old pin",
            ComfyRoot = "pending comfy path", InstallRoot = "pending install path", SourceId = "pending source", Python = "pending Python", SetupComplete = false };
        Check(RuntimePolicies.SynchronizeManagedSetupVersion(setupDraft, currentSetup, Path.Combine(stateDir, "versions"))
            && setupDraft.AppRoot == currentSetup.AppRoot && setupDraft.PinnedAppRoot == ""
            && setupDraft.ComfyRoot == "pending comfy path" && setupDraft.InstallRoot == "pending install path"
            && setupDraft.SourceId == "pending source" && setupDraft.Python == "pending Python" && !setupDraft.SetupComplete,
            "resumed onboarding follows the committed workbench without losing pending environment choices");
        setupDraft.AppRoot = "";
        Check(RuntimePolicies.SynchronizeManagedSetupVersion(setupDraft, currentSetup, Path.Combine(stateDir, "versions"))
            && setupDraft.AppRoot == currentSetup.AppRoot, "blank onboarding workbench follows the active version");
        setupDraft.AppRoot = Path.Combine(directory, "user-source"); setupDraft.PinnedAppRoot = "user pin";
        Check(!RuntimePolicies.SynchronizeManagedSetupVersion(setupDraft, currentSetup, Path.Combine(stateDir, "versions"))
            && setupDraft.AppRoot == Path.Combine(directory, "user-source") && setupDraft.PinnedAppRoot == "user pin",
            "onboarding synchronization preserves explicit source workbench directories");
        return passed;
    }
    private static HttpResponseMessage Text(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/plain") };
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private sealed class UpdateHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(respond(request)); }
    }
}
