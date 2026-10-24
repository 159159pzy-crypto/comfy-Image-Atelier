using System.Net;
using System.Text.Json;
using Anima.Launcher.Core;

internal static class NodeReleaseTests
{
    public static async Task<int> Run(string work, RuntimeManifest runtime)
    {
        var count = 0;
        void Check(bool condition, string label) { if (!condition) throw new Exception("FAIL: " + label); count++; Console.WriteLine("PASS: " + label); }
        async Task Reject(Func<Task> action, string label)
        {
            try { await action(); } catch (InvalidDataException) { Check(true, label); return; }
            throw new Exception("FAIL: " + label);
        }
        var sha = new string('b', 40);
        var repository = new Repository("local-node-id", "https://github.com/old-owner/old-node.git", new string('a', 40), ["RequiredNode"]);
        var requests = new List<string>();
        using var renamedHttp = new HttpClient(new Handler(request =>
        {
            requests.Add(request.RequestUri!.AbsoluteUri);
            return Json(request.RequestUri.AbsolutePath.Contains("/commits/")
                ? "{\"sha\":\"" + sha + "\"}"
                : "{\"full_name\":\"new-owner/new-node\",\"default_branch\":\"release/current\",\"archived\":false,\"disabled\":false}");
        }));
        var latest = await new NodeReleases(renamedHttp).LatestAsync(repository, default);
        Check(latest.Id == repository.Id && latest.Url == "https://github.com/new-owner/new-node.git" && latest.Commit == sha
            && latest.Branch == "release/current", "node metadata follows the official default branch and canonical repository while retaining its local identity");
        Check(requests.Count == 2 && requests.All(url => url.StartsWith("https://api.github.com/repos/"))
            && requests[1].EndsWith("/commits/release%2Fcurrent", StringComparison.OrdinalIgnoreCase), "node head authority uses only official API requests and escapes branch names");
        using var limitedHttp = new HttpClient(new Handler(_ => new(HttpStatusCode.Forbidden)));
        var fallbackCalled = false;
        var fallback = new NodeReleases(limitedHttp, (node, _) =>
        {
            fallbackCalled = true;
            return Task.FromResult(NodeReleases.ParseRemoteHead(node.Id, node.Url, "ref: refs/heads/master\tHEAD\n" + sha + "\tHEAD\n"));
        });
        Check((await fallback.LatestAsync(repository, default)).Commit == sha && fallbackCalled, "API rate limits can fall back to the official symbolic HEAD without choosing a mirror version");
        using var invalidHttp = new HttpClient(new Handler(request => Json(request.RequestUri!.AbsolutePath.Contains("/commits/")
            ? "{\"sha\":\"short\"}" : "{\"default_branch\":\"main\"}")));
        await Reject(() => new NodeReleases(invalidHttp).LatestAsync(repository, default), "a node release requires a complete immutable commit");
        using var archivedHttp = new HttpClient(new Handler(request => Json(request.RequestUri!.AbsolutePath.Contains("/commits/")
            ? "{\"sha\":\"" + sha + "\"}" : "{\"default_branch\":\"main\",\"archived\":true}")));
        Check((await new NodeReleases(archivedHttp).LatestAsync(repository, default)).Archived == true, "archived repositories retain an explicit status instead of appearing actively maintained");
        Check((await fallback.LatestAsync(repository, default)).Archived is null, "Git HEAD fallback leaves archive status unknown rather than asserting an active repository");
        using var disabledHttp = new HttpClient(new Handler(_ => Json("{\"disabled\":true}")));
        await Reject(() => new NodeReleases(disabledHttp).LatestAsync(repository, default), "disabled repositories cannot silently enter a new install plan");
        foreach (var url in new[] { "http://github.com/owner/repo", "https://mirror.invalid/https://github.com/owner/repo", "https://user:token@github.com/owner/repo", "https://github.com/owner/repo/tree/main" })
            await Reject(() => new NodeReleases(renamedHttp).LatestAsync(repository with { Url = url }, default), "node resolution rejects ambiguous or non-authoritative repository URLs");
        await Reject(() => Task.FromResult(NodeReleases.ParseRemoteHead(repository.Id, repository.Url, sha + "\tHEAD\n")), "a detached or ambiguous remote HEAD cannot masquerade as a default branch");
        await Reject(() => Task.FromResult(NodeReleases.ParseRemoteHead(repository.Id, repository.Url,
            "ref: refs/heads/main\tHEAD\n" + sha + "\tHEAD\n" + sha + "\tHEAD\n")), "duplicate remote HEAD answers are rejected");

        var directory = Path.Combine(work, "node-install-plan");
        var root = Path.Combine(directory, "ComfyUI");
        var state = Path.Combine(directory, "state");
        var existing = repository with { Id = "existing-node", Url = "https://github.com/owner/existing-node.git" };
        var missing = repository with { Id = "missing-node", Url = "https://github.com/owner/missing-node.git" };
        var nodeRuntime = runtime with { Nodes = [existing, missing] };
        var installed = existing with { Commit = new string('c', 40) };
        var existingDirectory = Path.Combine(root, "custom_nodes", existing.Id);
        Directory.CreateDirectory(existingDirectory);
        JsonFile.Write(Path.Combine(existingDirectory, ".anima-revision.json"), installed);
        File.WriteAllText(Path.Combine(existingDirectory, "user-source.py"), "preserve existing source");
        var config = new LauncherConfig { ComfyRoot = root, RuntimeRoot = Path.Combine(directory, "runtime"), Python = Path.Combine(root, ".venv", "Scripts", "python.exe") };
        var plan = new InstallationPlan(JsonSerializer.Serialize(config, JsonFile.Options), root, config.RuntimeRoot, "anima", true, 0, 0, 0, []);
        using var comfyHttp = new HttpClient(new Handler(request => Json(request.RequestUri!.AbsolutePath.EndsWith("/releases/latest")
            ? "{\"tag_name\":\"v1.2.3\",\"draft\":false,\"prerelease\":false}"
            : request.RequestUri.AbsolutePath.EndsWith("requirements.txt") ? "fixture==1\n" : "{\"sha\":\"" + sha + "\"}")));
        var nodeRequests = new List<string>();
        using var nodesHttp = new HttpClient(new Handler(request =>
        {
            nodeRequests.Add(request.RequestUri!.AbsolutePath);
            return Json(request.RequestUri.AbsolutePath.Contains("/commits/") ? "{\"sha\":\"" + sha + "\"}" : "{\"default_branch\":\"main\"}");
        }));
        var resolved = await InstallationPlanner.ResolveStableAsync(plan, nodeRuntime, state, default, new ComfyReleases(comfyHttp), new NodeReleases(nodesHttp));
        Check(resolved.NodeVersions.Length == 1 && resolved.NodeVersions[0].Id == missing.Id && nodeRequests.All(path => !path.Contains("existing-node")),
            "fresh planning resolves only nodes that will be newly installed and preserves existing node source versions");
        Check(resolved.ResolvedRuntime!.Nodes.Single(node => node.Id == missing.Id).Commit == sha
            && resolved.ResolvedRuntime.Nodes.Single(node => node.Id == existing.Id).Commit == installed.Commit,
            "the reviewed runtime locks both new latest commits and existing installed commits");
        Check(File.ReadAllText(Path.Combine(existingDirectory, "user-source.py")) == "preserve existing source", "node release review never writes existing source files");
        var onboarding = Path.Combine(directory, "onboarding.json");
        JsonFile.Write(onboarding, new SetupRecord { Draft = config, ReviewedPlan = resolved, InstallStarted = true });
        var restored = JsonFile.Read<SetupRecord>(onboarding).ReviewedPlan!;
        Check(restored.NodeVersions.Single().Commit == sha && restored.ResolvedRuntime!.Nodes.Single(node => node.Id == missing.Id).Commit == sha,
            "onboarding persists the exact reviewed node repositories and commits");
        using var offline = new HttpClient(new Handler(_ => throw new HttpRequestException("offline fixture")));
        var resumed = await InstallationPlanner.ResolveStableAsync(plan, nodeRuntime, state, default, new ComfyReleases(offline), new NodeReleases(offline));
        Check(resumed.NodeVersions.Single() == resolved.NodeVersions.Single() && resumed.ResolvedRuntime!.Nodes.Select(node => node.Commit).SequenceEqual(resolved.ResolvedRuntime.Nodes.Select(node => node.Commit)),
            "a restarted offline installation reuses every frozen node commit without consulting latest again");
        Check((await InstallationPlanner.ResolveStableAsync(restored, nodeRuntime, state, default, new ComfyReleases(offline), new NodeReleases(offline))).NodeVersions.Single().Commit == sha,
            "an in-memory reviewed plan also retains its node snapshot during retry");
        Check(resolved.Describe().Contains(missing.Id) && resolved.Describe().Contains(sha[..12]), "install review exposes the fixed per-node commit before installation");
        var preserved = InstallationPlanner.PreserveExistingNodeVersions(root, nodeRuntime);
        Check(preserved.Nodes.Single(node => node.Id == existing.Id).Commit == installed.Commit && nodeRuntime.Nodes.Single(node => node.Id == existing.Id).Commit == repository.Commit,
            "dependency repair can preserve an explicitly updated managed node without mutating the bundled manifest");
        var importPlan = plan with { Fresh = false, ResolvedRuntime = nodeRuntime };
        var importState = Path.Combine(directory, "import-state");
        var imported = await InstallationPlanner.ResolveStableAsync(importPlan, nodeRuntime, importState, default, new ComfyReleases(offline), new NodeReleases(nodesHttp));
        Check(imported.NodeVersions.Single().Id == missing.Id && imported.NodeVersions.Single().Commit == sha
            && imported.Release is null && imported.Dependencies is null, "adding a missing node resolves latest without querying or upgrading the imported ComfyUI core");
        var importResume = await InstallationPlanner.ResolveStableAsync(importPlan, nodeRuntime, importState, default, new ComfyReleases(offline), new NodeReleases(offline));
        Check(importResume.NodeVersions.Single() == imported.NodeVersions.Single(), "missing-node installation has its own frozen restart snapshot for imported environments");
        InstallationPlanner.MarkStableInstallComplete(config, importState);
        var newerSha = new string('d', 40);
        using var newerNodesHttp = new HttpClient(new Handler(request => Json(request.RequestUri!.AbsolutePath.Contains("/commits/")
            ? "{\"sha\":\"" + newerSha + "\"}" : "{\"default_branch\":\"main\"}")));
        var nextMissing = await InstallationPlanner.ResolveStableAsync(importPlan, nodeRuntime, importState, default, new ComfyReleases(offline), new NodeReleases(newerNodesHttp));
        Check(nextMissing.NodeVersions.Single().Commit == newerSha && nextMissing.ResolvedRuntime!.Nodes.Single(node => node.Id == existing.Id).Commit == installed.Commit,
            "a later missing-node operation can resolve a newer commit after completion while still retaining existing source");
        return count;
    }

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
}
