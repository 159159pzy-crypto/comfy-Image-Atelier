using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Anima.Launcher.Core;

public sealed record ComfyRelease(string Tag, string Commit, DateTimeOffset? PublishedAt, string ReleaseUrl);
public sealed record ComfyDependencySnapshot(string ComfyCommit, string RequirementsText, string ManagerRequirementsText, string[] Constraints)
{
    public bool Resolved { get; init; }
    public string PythonIdentity { get; init; } = "";
    public string NodeRequirementsIdentity { get; init; } = "";
}

/// <summary>Resolve the official stable release once; all later downloads use its immutable commit.</summary>
public sealed class ComfyReleases(HttpClient? client = null, Func<string, CancellationToken, Task<string>>? resolveTag = null, LauncherConfig? config = null)
{
    private static readonly HttpClient Shared = new(NetworkPolicy.HttpHandler(allowAutoRedirect: true)) { Timeout = TimeSpan.FromSeconds(35) };
    private readonly HttpClient http = client ?? Shared;
    public const string RepositoryUrl = "https://github.com/Comfy-Org/ComfyUI.git";
    private const string Api = "https://api.github.com/repos/Comfy-Org/ComfyUI";
    public static bool StableTag(string tag) => Regex.IsMatch(tag, @"^v?\d+\.\d+\.\d+$");

    private async Task<HttpResponseMessage> Get(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("AnimaRandomStudio/1.2");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        return await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
    }

    public async Task<ComfyRelease> LatestAsync(CancellationToken ct)
    {
        string tag;
        DateTimeOffset? published = null;
        using (var response = await Get(Api + "/releases/latest", ct))
        {
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                using var fallback = await Get("https://github.com/Comfy-Org/ComfyUI/releases/latest", ct);
                fallback.EnsureSuccessStatusCode();
                var uri = fallback.RequestMessage?.RequestUri;
                const string prefix = "/Comfy-Org/ComfyUI/releases/tag/";
                if (uri?.Host != "github.com" || !uri.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("官方最新稳定版跳转无效，请稍后重试。");
                tag = Uri.UnescapeDataString(uri.AbsolutePath[prefix.Length..]);
            }
            else
            {
                response.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var root = doc.RootElement;
                if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
                    throw new InvalidDataException("官方返回了预发布版本，未将其用于稳定安装。");
                tag = root.GetProperty("tag_name").GetString() ?? "";
                if (root.TryGetProperty("published_at", out var date) && date.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(date.GetString(), out var parsed)) published = parsed;
            }
        }
        if (!StableTag(tag)) throw new InvalidDataException("无法确认 ComfyUI 正式稳定版本号。");
        string commit;
        try
        {
            using var response = await Get(Api + "/commits/" + Uri.EscapeDataString(tag), ct);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            commit = doc.RootElement.GetProperty("sha").GetString() ?? "";
        }
        catch (HttpRequestException)
        {
            try
            {
                using var patch = await Get("https://github.com/Comfy-Org/ComfyUI/commit/" + Uri.EscapeDataString(tag) + ".patch", ct);
                patch.EnsureSuccessStatusCode();
                var body = await patch.Content.ReadAsStringAsync(ct);
                var match = Regex.Match(body, @"\AFrom ([a-f0-9]{40}) ");
                if (!match.Success) throw new InvalidDataException("官方版本提交无法验证。");
                commit = match.Groups[1].Value;
            }
            catch (Exception ex) when (resolveTag is not null && ex is HttpRequestException or InvalidDataException)
            { commit = await resolveTag(tag, ct); }
        }
        Safety.Commit(commit);
        return new(tag, commit, published, "https://github.com/Comfy-Org/ComfyUI/releases/tag/" + Uri.EscapeDataString(tag));
    }

    public async Task<ComfyDependencySnapshot> DependenciesAsync(ComfyRelease release, RuntimeManifest runtime, CancellationToken ct)
    {
        config?.Validate();
        Safety.Commit(release.Commit);
        async Task<string> Read(string file)
        {
            var url = "https://raw.githubusercontent.com/Comfy-Org/ComfyUI/" + release.Commit + "/" + file;
            Exception? last = null;
            // Requirements are executable installation instructions, not a hash-locked
            // binary. Read their snapshot from the official origin only.
            foreach (var source in new[] { url })
            {
                try
                {
                    using var response = await Get(source, ct);
                    response.EnsureSuccessStatusCode();
                    var content = await response.Content.ReadAsStringAsync(ct);
                    if (string.IsNullOrWhiteSpace(content) || content.Length > 262144 || content.TrimStart().StartsWith('<')) throw new InvalidDataException("官方依赖文件为空或格式无效。");
                    return content;
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { last = ex; }
            }
            throw new IOException("读取固定版本依赖失败：" + file, last);
        }
        var requirements = await Read("requirements.txt");
        var manager = await Read("manager_requirements.txt");
        var gpuPins = runtime.PythonConstraints.Where(line => Regex.IsMatch(line, @"^(torch|torchvision|torchaudio)==", RegexOptions.IgnoreCase)).ToArray();
        return new(release.Commit, requirements, manager, gpuPins);
    }

    public static async Task<string> ResolveTagWithGit(string git, string tag, string cwd, LauncherConfig config, CancellationToken ct)
    {
        if (!StableTag(tag)) throw new InvalidDataException("无效稳定版本标签。");
        // Mirrors may transport a commit once known; only the official repository
        // may decide which commit a stable version denotes.
        var sources = new[] { RepositoryUrl };
        Exception? last = null;
        foreach (var source in sources)
        {
            try
            {
                var authorityEnvironment = new Dictionary<string, string>
                {
                    ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_SYSTEM"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
                    ["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null", ["GIT_CONFIG_COUNT"] = "0",
                };
                var text = await Commands.Run(git, ["ls-remote", source, "refs/tags/" + tag, "refs/tags/" + tag + "^{}"], cwd, null, ct,
                    authorityEnvironment, timeout: TimeSpan.FromSeconds(45));
                var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var match = lines.FirstOrDefault(line => line.TrimEnd().EndsWith("refs/tags/" + tag + "^{}", StringComparison.Ordinal))
                    ?? lines.FirstOrDefault(line => line.TrimEnd().EndsWith("refs/tags/" + tag, StringComparison.Ordinal));
                var sha = match?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                Safety.Commit(sha);
                return sha;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { last = ex; }
        }
        throw new IOException("无法解析官方稳定版标签的完整提交。", last);
    }
}
