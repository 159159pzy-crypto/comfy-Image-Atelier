using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Anima.Launcher.Core;

/// <summary>Successful public Git transport preference. Repository identities and allowed sources stay unchanged.</summary>
public static class InstallationSourcePreferences
{
    public const string GitHubBase = "https://github.com/";
    private sealed record Preference(int SchemaVersion, string TransportIdentity, string NetworkIdentity, string GitBase);
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    public static string PathFor(string cacheRoot, LauncherConfig config) => Path.Combine(cacheRoot, "source-preferences", DependencyPlans.TransportIdentity(config) + ".json");

    public static string[] GitBases(string cacheRoot, LauncherConfig config)
    {
        var candidates = DownloadSources.GitCandidates(GitHubBase, config);
        var path = PathFor(cacheRoot, config);
        Preference? saved = null;
        try { lock (Gates.GetOrAdd(Path.GetFullPath(path), _ => new())) if (File.Exists(path)) saved = JsonFile.Read<Preference>(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        if (saved is null || saved.SchemaVersion != 1 || saved.TransportIdentity != DependencyPlans.TransportIdentity(config)
            || !candidates.Contains(saved.GitBase, StringComparer.Ordinal) || saved.NetworkIdentity != NetworkIdentity(config, candidates)) return candidates;
        var explicitSource = !string.IsNullOrWhiteSpace(config.GitMirror) && !NetworkPolicy.Direct ? candidates[0] : null;
        return candidates.Select((source, order) => (source, order))
            .OrderBy(item => item.source == explicitSource ? 0 : item.source == saved.GitBase ? 1 : 2)
            .ThenBy(item => item.order).Select(item => item.source).ToArray();
    }

    public static string[] GitRepositories(string cacheRoot, string repository, LauncherConfig config)
    {
        var candidates = DownloadSources.GitCandidates(repository, config);
        if (!Safety.Https(repository).Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return candidates;
        var preferred = GitBases(cacheRoot, config);
        return preferred.Select(source => RepositoryFor(source, repository, config))
            .Where(source => source is not null && candidates.Contains(source, StringComparer.Ordinal)).Cast<string>()
            .Concat(candidates).Distinct(StringComparer.Ordinal).ToArray();
    }

    public static void RememberRepository(string cacheRoot, LauncherConfig config, string repository, string source, Action<string>? log = null)
    {
        if (!Safety.Https(repository).Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return;
        var match = DownloadSources.GitCandidates(GitHubBase, config).FirstOrDefault(candidate => RepositoryFor(candidate, repository, config) == source);
        if (match is null) throw new InvalidDataException("成功的 Git 来源不属于当前允许的候选。");
        RememberGitBase(cacheRoot, config, match, log);
    }

    public static void RememberGitBase(string cacheRoot, LauncherConfig config, string source, Action<string>? log = null)
    {
        var candidates = DownloadSources.GitCandidates(GitHubBase, config);
        if (!candidates.Contains(source, StringComparer.Ordinal)) throw new InvalidDataException("成功的 Git 来源不属于当前允许的候选。");
        var path = PathFor(cacheRoot, config);
        var value = new Preference(1, DependencyPlans.TransportIdentity(config), NetworkIdentity(config, candidates), source);
        try { lock (Gates.GetOrAdd(Path.GetFullPath(path), _ => new())) JsonFile.Write(path, value); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { log?.Invoke("Git 已完成，来源偏好未保存：" + DiagnosticRedaction.Text(error.Message)); }
    }

    private static string? RepositoryFor(string source, string repository, LauncherConfig config)
    {
        if (source == GitHubBase) return Safety.Https(repository).AbsoluteUri;
        foreach (var template in new[] { config.GitMirror, DownloadSources.PreferChina(config) ? DownloadSources.ChinaGitMirror : "" })
        {
            if (string.IsNullOrWhiteSpace(template)) continue;
            string Map(string url) => Safety.Https(template.Contains("{url}", StringComparison.Ordinal)
                ? template.Replace("{url}", url, StringComparison.Ordinal) : template.TrimEnd('/') + "/" + url).AbsoluteUri;
            if (Map(GitHubBase) == source) return Map(repository);
        }
        return null;
    }

    private static string NetworkIdentity(LauncherConfig config, string[] candidates)
    {
        var network = NetworkPolicySnapshot.Capture(config);
        var values = new List<string> { network.Mode };
        foreach (var source in candidates)
        {
            // Apply resolves exactly the proxy/bypass policy passed to Git without starting a process.
            var info = new ProcessStartInfo();
            network.Apply(info, new Uri(source));
            values.Add(source + "\n" + info.Environment["HTTPS_PROXY"] + "\n" + info.Environment["NO_PROXY"]);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", values)))).ToLowerInvariant();
    }

    /// <summary>An empty/already initialized submodule command cannot prove any network source worked.</summary>
    public static bool HasPendingGitHubSubmodules(string modules, string status)
    {
        var pending = status.Replace("\r", "").Split('\n').Where(line => line.StartsWith('-') && line.Length > 42)
            .Select(line => line[42..].Split(" (", 2)[0].Trim().Trim('"')).ToHashSet(StringComparer.Ordinal);
        if (pending.Count == 0) return false;
        foreach (var section in Regex.Split(modules, @"(?m)^\s*\[submodule\s+[^\r\n]+\]\s*$"))
        {
            var path = Regex.Match(section, @"(?m)^\s*path\s*=\s*(.+?)\s*$").Groups[1].Value.Trim('"');
            var url = Regex.Match(section, @"(?m)^\s*url\s*=\s*(.+?)\s*$").Groups[1].Value.Trim('"');
            if (pending.Contains(path) && Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && uri.Scheme == "https" && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
