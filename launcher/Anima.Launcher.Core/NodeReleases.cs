using System.Text.Json;
using System.Text.RegularExpressions;

namespace Anima.Launcher.Core;

public sealed record NodeRelease(string Id, string Url, string Branch, string Commit)
{
    public bool? Archived { get; init; }
}

/// <summary>Resolve a node's official default branch once; downloads use the resulting immutable SHA.</summary>
public sealed class NodeReleases(HttpClient? client = null,
    Func<Repository, CancellationToken, Task<NodeRelease>>? resolveHead = null)
{
    private static readonly HttpClient Shared = new(NetworkPolicy.HttpHandler(allowAutoRedirect: true)) { Timeout = TimeSpan.FromSeconds(35) };
    private readonly HttpClient http = client ?? Shared;

    public static string RepositoryName(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com"
            || uri.UserInfo.Length != 0 || !uri.IsDefaultPort || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("节点版本检查只接受官方 GitHub HTTPS 仓库地址。");
        var name = uri.AbsolutePath.Trim('/');
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (!Regex.IsMatch(name, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$") || name.Split('/').Any(part => part is "." or ".."))
            throw new InvalidDataException("无法识别节点仓库地址：" + url);
        return name;
    }

    private async Task<JsonDocument> Get(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("AnimaRandomStudio/1.2");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    public async Task<NodeRelease> LatestAsync(Repository repository, CancellationToken ct)
    {
        var name = RepositoryName(repository.Url);
        try
        {
            using var metadata = await Get("https://api.github.com/repos/" + name, ct);
            var data = metadata.RootElement;
            if (data.TryGetProperty("disabled", out var disabled) && disabled.GetBoolean())
                throw new InvalidDataException("节点官方仓库已停用：" + repository.Id);
            var branch = data.GetProperty("default_branch").GetString() ?? "";
            if (string.IsNullOrWhiteSpace(branch) || branch.Any(char.IsControl)) throw new InvalidDataException("节点默认分支无效：" + repository.Id);
            var canonical = data.TryGetProperty("full_name", out var fullName) ? fullName.GetString() ?? name : name;
            var url = "https://github.com/" + canonical + ".git";
            canonical = RepositoryName(url);
            using var head = await Get("https://api.github.com/repos/" + canonical + "/commits/" + Uri.EscapeDataString(branch), ct);
            var commit = head.RootElement.GetProperty("sha").GetString() ?? "";
            Safety.Commit(commit);
            return new(repository.Id, url, branch, commit)
            { Archived = data.TryGetProperty("archived", out var archived) ? archived.GetBoolean() : null };
        }
        catch (Exception ex) when (resolveHead is not null && (ex is HttpRequestException || ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            var resolved = await resolveHead(repository, ct);
            if (resolved.Id != repository.Id || RepositoryName(resolved.Url) != name || string.IsNullOrWhiteSpace(resolved.Branch))
                throw new InvalidDataException("节点官方 HEAD 回退结果身份不匹配。");
            Safety.Commit(resolved.Commit);
            return resolved;
        }
    }

    public async Task<NodeRelease[]> ResolveAsync(IEnumerable<Repository> repositories, CancellationToken ct)
    {
        using var slots = new SemaphoreSlim(3);
        return await Task.WhenAll(repositories.Select(async repository =>
        {
            await slots.WaitAsync(ct);
            try { return await LatestAsync(repository, ct); }
            finally { slots.Release(); }
        }));
    }

    public static async Task<NodeRelease> ResolveHeadWithGit(string git, Repository repository, string cwd, CancellationToken ct)
    {
        var name = RepositoryName(repository.Url);
        var url = "https://github.com/" + name + ".git";
        var environment = new Dictionary<string, string>
        {
            ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_SYSTEM"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
            ["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null", ["GIT_CONFIG_COUNT"] = "0",
        };
        var output = await Commands.Run(git, ["ls-remote", "--symref", url, "HEAD"], cwd, null, ct, environment, timeout: TimeSpan.FromSeconds(45));
        return ParseRemoteHead(repository.Id, url, output);
    }

    public static NodeRelease ParseRemoteHead(string id, string url, string output)
    {
        _ = RepositoryName(url);
        var branches = Regex.Matches(output, @"(?m)^ref: refs/heads/([^\r\n\t]+)\tHEAD\r?$");
        var commits = Regex.Matches(output, @"(?m)^([a-f0-9]{40})\tHEAD\r?$");
        if (branches.Count != 1 || commits.Count != 1) throw new InvalidDataException("官方节点仓库未返回唯一默认分支与完整 HEAD 提交：" + id);
        var branch = branches[0];
        var commit = commits[0];
        Safety.Commit(commit.Groups[1].Value);
        return new(id, url, branch.Groups[1].Value, commit.Groups[1].Value);
    }
}
