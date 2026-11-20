using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Anima.Launcher.Core;

public sealed record ComfyRelease(string Tag, string Commit, DateTimeOffset? PublishedAt, string ReleaseUrl);
public sealed record ComfyDependencySnapshot(string ComfyCommit, string RequirementsText, string ManagerRequirementsText, string[] Constraints)
{
    public string ComfySourceKind { get; init; } = "git";
    public bool Resolved { get; init; }
    public string PythonIdentity { get; init; } = "";
    public string NodeRequirementsIdentity { get; init; } = "";
    // Missing in historical plans: those keep their original dependency scope on resume.
    public int WorkbenchDependencyContractVersion { get; init; }
    public string WorkbenchRequirementsText { get; init; } = "";
    public string WorkbenchRequirementsIdentity { get; init; } = "";
    public int WorkbenchDistributionScopeVersion { get; init; }
    public WorkbenchDistributionPlan? WorkbenchDistribution { get; init; }
    public int PlanVersion { get; init; }
    public string PackageIndex { get; init; } = "";
    public string GitSource { get; init; } = "";
    public string[] EnvironmentPackages { get; init; } = [];
    public DependencyPackage[] Packages { get; init; } = [];
    public string TransportIdentity { get; init; } = "";
}

/// <summary>Resolve the official stable release once; all later downloads use its immutable commit.</summary>
public sealed class ComfyReleases(HttpClient? client = null, Func<string, CancellationToken, Task<string>>? resolveTag = null, LauncherConfig? config = null,
    string? metadataDirectory = null) : IDisposable
{
    private readonly HttpClient http = client ?? new HttpClient(NetworkPolicySnapshot.Capture(config).CreateHttpHandler(true)) { Timeout = TimeSpan.FromSeconds(35) };
    public void Dispose() { if (client is null) http.Dispose(); }
    public const string RepositoryUrl = "https://github.com/Comfy-Org/ComfyUI.git";
    private const string Api = "https://api.github.com/repos/Comfy-Org/ComfyUI";
    private sealed record CachedRequirements(string Commit, string FileName, string Sha256, string Content);
    internal static bool MetadataUnavailable(Exception ex, CancellationToken ct) => !ct.IsCancellationRequested && ex switch
    {
        HttpRequestException httpError => httpError.StatusCode is null or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
            || (int)httpError.StatusCode >= 500,
        TimeoutException or OperationCanceledException => true,
        CommandFailureException command => command.Kind is ProcessFailureKind.Transport or ProcessFailureKind.Timeout
            or ProcessFailureKind.RateLimited or ProcessFailureKind.ServerUnavailable,
        IOException { InnerException: { } inner } => MetadataUnavailable(inner, ct),
        _ => false,
    };
    public static bool StableTag(string tag) => Regex.IsMatch(tag, @"^v?\d+\.\d+\.\d+$");

    internal static ComfyDependencySnapshot BundledDependencies(RuntimeManifest runtime)
    {
        using var stream = typeof(ComfyReleases).Assembly.GetManifestResourceStream("Anima.Launcher.Core.comfy-pinned-requirements.json")
            ?? throw new InvalidDataException("缺少内嵌 ComfyUI 依赖清单。");
        var snapshot = JsonSerializer.Deserialize<ComfyDependencySnapshot>(stream, JsonFile.Options);
        if (snapshot?.ComfyCommit != runtime.Comfy.Commit || string.IsNullOrWhiteSpace(snapshot.RequirementsText)
            || string.IsNullOrWhiteSpace(snapshot.ManagerRequirementsText))
            throw new InvalidDataException("内嵌 ComfyUI 依赖与清单提交不匹配，请重新检查官方来源。");
        return snapshot with { Constraints = runtime.PythonConstraints.Where(line => Regex.IsMatch(line,
            @"^(torch|torchvision|torchaudio)==", RegexOptions.IgnoreCase)).ToArray() };
    }

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
            var cache = metadataDirectory is null ? null : Path.Combine(metadataDirectory, release.Commit, file + ".json");
            static string Validate(string content)
            {
                if (string.IsNullOrWhiteSpace(content) || content.Length > 262144 || content.TrimStart().StartsWith('<'))
                    throw new InvalidDataException("官方依赖文件为空或格式无效。");
                return content;
            }
            if (cache is not null && File.Exists(cache))
            {
                try
                {
                    var cached = JsonFile.Read<CachedRequirements>(cache);
                    if (cached.Commit == release.Commit && cached.FileName == file && cached.Content is not null &&
                        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cached.Content))) == cached.Sha256)
                        return Validate(cached.Content);
                }
                catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException) { }
            }
            string content;
            try
            {
                using var response = await Get(url, ct);
                response.EnsureSuccessStatusCode();
                content = Validate(await response.Content.ReadAsStringAsync(ct));
            }
            catch (Exception ex) when (MetadataUnavailable(ex, ct))
            {
                using var response = await Get(Api + "/contents/" + file + "?ref=" + release.Commit, ct);
                response.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var root = document.RootElement;
                if (root.GetProperty("encoding").GetString() != "base64" || root.GetProperty("path").GetString() != file)
                    throw new InvalidDataException("官方依赖文件身份不符：" + file);
                content = Validate(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(root.GetProperty("content").GetString()!)));
            }
            if (cache is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
                var temp = cache + "." + Guid.NewGuid().ToString("N") + ".tmp";
                var cached = new CachedRequirements(release.Commit, file, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))), content);
                try { await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(cached, JsonFile.Options), ct); File.Move(temp, cache, true); }
                finally { File.Delete(temp); }
            }
            return content;
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
                NetworkPolicySnapshot.Capture(config).AddEnvironment(authorityEnvironment, new Uri(source));
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
