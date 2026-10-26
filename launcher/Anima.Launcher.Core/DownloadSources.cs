using System.Text.RegularExpressions;

namespace Anima.Launcher.Core;

/// <summary>A verified file on ModelScope has its own repository revision, independent of Hugging Face.</summary>
public sealed record ModelScopeSource(string Repository, string Revision, string Path)
{
    public string Url
    {
        get
        {
            Validate();
            return "https://modelscope.cn/api/v1/models/" + Repository + "/repo?Revision=" + Revision
                + "&FilePath=" + Uri.EscapeDataString(Path);
        }
    }

    public void Validate()
    {
        if (!Regex.IsMatch(Repository ?? "", @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$"))
            throw new InvalidDataException("ModelScope 来源需要明确的组织和仓库。");
        Safety.Commit(Revision);
        if (string.IsNullOrWhiteSpace(Path) || Path.Contains('\\')) throw new InvalidDataException("ModelScope 文件路径无效。");
        Safety.Under(System.IO.Path.GetTempPath(), Path);
    }
}

/// <summary>Transport preference never changes an artifact's locked size, digest, or Git commit.</summary>
public static class DownloadSources
{
    public const string ChinaGitMirror = "https://gh-proxy.com/{url}";
    public const string ChinaPipIndex = "https://mirrors.ustc.edu.cn/pypi/simple";
    public const string ChinaHuggingFace = "https://hf-mirror.com";
    public const string ChinaTorch = "https://mirror.sjtu.edu.cn/pytorch-wheels/";
    public const string OfficialPipIndex = "https://pypi.org/simple";
    public const string OfficialPythonMirror = "https://github.com/astral-sh/python-build-standalone/releases/download";

    public static bool PreferChina(LauncherConfig config) => config.SourceId is "china" or "github-mirror";

    public static string[] Candidates(string original, LauncherConfig config)
    {
        var uri = Safety.Https(original);
        var result = new List<string>();
        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
        {
            // Direct acceptance runs must not replace an OS proxy with an HTTP
            // forwarding gateway. Native package/model mirrors remain available.
            if (!NetworkPolicy.Direct)
            {
                if (!string.IsNullOrWhiteSpace(config.GitMirror)) result.Add(MapGit(original, config.GitMirror));
                if (PreferChina(config)) result.Add(MapGit(original, ChinaGitMirror));
            }
        }
        else if (uri.Host.Equals("huggingface.co", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(config.ModelMirror)) result.Add(config.ModelMirror.TrimEnd('/') + uri.PathAndQuery);
            if (PreferChina(config)) result.Add(ChinaHuggingFace + uri.PathAndQuery);
        }
        else if (PreferChina(config) && uri.Host.Equals("download.pytorch.org", StringComparison.OrdinalIgnoreCase)
                 && uri.AbsolutePath.StartsWith("/whl/", StringComparison.Ordinal))
            result.Add(ChinaTorch + uri.PathAndQuery["/whl/".Length..]);
        result.Add(original);
        return DistinctValidated(result);
    }

    public static string[] GitCandidates(string original, LauncherConfig config) => Candidates(original, config);
    public static string[] ArtifactCandidates(Artifact artifact, LauncherConfig config)
    {
        Safety.ValidateArtifact(artifact);
        var native = PreferChina(config) ? artifact.Sources.Where(source => source.Kind == "nativeMirror")
            .OrderBy(source => source.Priority).Select(source => source.Url) : [];
        return DistinctValidated(native.Concat(Candidates(artifact.Url, config)));
    }
    public static string[] ModelCandidates(ModelItem model, LauncherConfig config) => ModelCandidates(model.Urls, model.ModelScope, config);
    public static string[] ModelCandidates(ModelFile file, LauncherConfig config) => ModelCandidates(file.Urls, file.ModelScope, config);

    private static string[] ModelCandidates(string[] originals, ModelScopeSource? modelScope, LauncherConfig config)
    {
        var candidates = new List<string>();
        if (PreferChina(config) && modelScope is not null) candidates.Add(modelScope.Url);
        candidates.AddRange(originals.SelectMany(url => Candidates(url, config)));
        return DistinctValidated(candidates);
    }

    public static string[] PipIndexes(LauncherConfig config)
    {
        var candidates = new List<string>();
        // The legacy default official field must not defeat the new China profile.
        if (!string.IsNullOrWhiteSpace(config.PipIndex)
            && (!PreferChina(config) || !config.PipIndex.TrimEnd('/').Equals(OfficialPipIndex, StringComparison.OrdinalIgnoreCase)))
            candidates.Add(config.PipIndex);
        if (PreferChina(config)) candidates.AddRange([ChinaPipIndex, "https://pypi.tuna.tsinghua.edu.cn/simple"]);
        candidates.Add(OfficialPipIndex);
        return DistinctValidated(candidates);
    }

    public static string[] PythonMirrors(LauncherConfig config) => Candidates(OfficialPythonMirror, config);

    public static string Label(string url)
    {
        var uri = Safety.Https(url);
        return uri.Host switch
        {
            "modelscope.cn" or "www.modelscope.cn" => "ModelScope",
            "hf-mirror.com" => "Hugging Face 国内镜像",
            "huggingface.co" => "Hugging Face 官方",
            "gh-proxy.com" => "GitHub 国内镜像",
            "github.com" => "GitHub 官方",
            "mirror.sjtu.edu.cn" => "上海交大镜像",
            "download.pytorch.org" => "PyTorch 官方",
            "registry.npmmirror.com" or "cdn.npmmirror.com" => "npmmirror 国内镜像",
            "mirror.nju.edu.cn" or "mirrors.nju.edu.cn" => "南京大学镜像",
            "mirrors.huaweicloud.com" => "华为云镜像",
            _ => uri.Host
        };
    }

    private static string MapGit(string original, string template) => template.Contains("{url}", StringComparison.Ordinal)
        ? template.Replace("{url}", original, StringComparison.Ordinal) : template.TrimEnd('/') + "/" + original;

    private static string[] DistinctValidated(IEnumerable<string> candidates) => candidates
        .Select(url => Safety.Https(url).AbsoluteUri).Distinct(StringComparer.Ordinal).ToArray();
}
