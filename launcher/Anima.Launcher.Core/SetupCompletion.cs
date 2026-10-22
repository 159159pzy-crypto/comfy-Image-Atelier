using System.Collections.Immutable;
using System.Text.Json;

namespace Anima.Launcher.Core;

public enum SetupModelReadiness { Unknown, Checking, NeedsDownload, NeedsAttention, Ready, ScanFailed }
public enum SetupCompletionOperation { None, ReviewModels, DownloadModels, StartWorkbench }
public enum SetupCompletionAction { None, ReviewModels, DownloadModels, ResumeModels, StartWorkbench }

/// <summary>Optional tagger runtime discovery is needed only for tagger model selections.</summary>
public static class ModelProbePolicy
{
    public static bool NeedsTaggerRuntime(IEnumerable<ModelItem> models) => models.Any(model => model.Category == "wd_taggers");
}

public sealed record SetupModelReviewItem(ModelStatus Status, bool CanDownload, long RemainingBytes, string Source, string Issue)
{
    public string Name => Status.Model.Name;
    public string Location => Status.FoundPath ?? Status.Destination;
    public string OpenDirectory => string.IsNullOrWhiteSpace(Location) ? ""
        : Status.Model.IsPackage ? Location : Path.GetDirectoryName(Location) ?? "";
}

/// <summary>A successful scan is required; an empty or incomplete result never means ready.</summary>
public sealed record SetupModelReview(SetupModelReadiness Readiness, ImmutableArray<SetupModelReviewItem> Items)
{
    public ImmutableArray<SetupModelReviewItem> DownloadableItems => Items.Where(item => item.CanDownload).ToImmutableArray();
    public long RemainingBytes => DownloadableItems.Sum(item => item.RemainingBytes);

    public static SetupModelReview Create(IEnumerable<ModelItem> requiredModels, IEnumerable<ModelStatus> scan,
        Func<ModelStatus, long> remainingBytes, Func<ModelStatus, string> source)
    {
        var required = requiredModels.DistinctBy(model => model.Id).ToArray();
        var statuses = scan.GroupBy(status => status.Model.Id).ToDictionary(group => group.Key, group => group.First());
        var items = ImmutableArray.CreateBuilder<SetupModelReviewItem>();
        var incomplete = required.Length == 0;
        foreach (var model in required)
        {
            if (!statuses.TryGetValue(model.Id, out var status))
            {
                incomplete = true;
                items.Add(new(new(model, "未检查", null, ""), false, 0, "", "尚未取得此模型的检查结果，请重新检查。"));
                continue;
            }
            var issue = "";
            var canDownload = false;
            if (status.Missing)
            {
                if (string.IsNullOrWhiteSpace(status.Destination)) issue = "下载目录尚未确认，请检查模型路径后重新检查。";
                else if (!model.CanDownload) issue = "此模型暂不支持可验证的自动下载，请从来源手动安装后重新检查。";
                else if (status.FoundPath is not null && !model.IsPackage)
                    issue = "已有模型文件校验异常，已保留原文件。请先备份并处理该文件，再重新检查。";
                else canDownload = true;
            }
            else if (status.State is not ("已存在" or "已校验"))
            {
                incomplete = true;
                issue = "无法确认此模型是否可用，请重新检查。";
            }
            items.Add(new(status, canDownload, canDownload ? Math.Max(0, remainingBytes(status)) : 0, source(status), issue));
        }
        var result = items.ToImmutable();
        var readiness = incomplete ? SetupModelReadiness.ScanFailed
            : result.Any(item => item.Issue.Length > 0) ? SetupModelReadiness.NeedsAttention
            : result.Any(item => item.CanDownload) ? SetupModelReadiness.NeedsDownload : SetupModelReadiness.Ready;
        return new(readiness, result);
    }
}

/// <summary>Reviewed model identities and runtime configuration, never an installation plan.</summary>
public sealed class SetupModelDownloadPlan
{
    private readonly string configuration;
    public ImmutableArray<string> ModelIds { get; }

    public SetupModelDownloadPlan(LauncherConfig config, SetupModelReview review)
    {
        if (config.InstallPending || string.IsNullOrWhiteSpace(config.ComfyRoot))
            throw new InvalidOperationException("请先完成环境安装，再准备模型。");
        if (review.Readiness is SetupModelReadiness.ScanFailed or SetupModelReadiness.Unknown or SetupModelReadiness.Checking)
            throw new InvalidOperationException("模型检查尚未完成，请重新检查。");
        ModelIds = review.DownloadableItems.Select(item => item.Status.Model.Id).Distinct().ToImmutableArray();
        if (ModelIds.IsEmpty) throw new InvalidOperationException("没有可自动补齐的模型，请查看检查结果。");
        configuration = JsonSerializer.Serialize(RuntimePolicies.SnapshotConfig(config), JsonFile.Options);
    }

    public LauncherConfig CreateConfig() => JsonSerializer.Deserialize<LauncherConfig>(configuration, JsonFile.Options)!;
    public bool Matches(LauncherConfig config) => configuration == JsonSerializer.Serialize(config, JsonFile.Options);
}

public sealed record SetupCompletionPresentation(string Title, string Description, string PrimaryLabel,
    SetupCompletionAction PrimaryAction, bool AllowOpenAnyway)
{
    public static SetupCompletionPresentation Resolve(SetupModelReadiness readiness, bool reviewVisible = false,
        bool canDownload = false, bool canResume = false, bool startFailed = false, bool hasError = false)
    {
        if (startFailed) return new("工作台还没有启动。", "环境配置已保留。处理提示的问题后，可以重试启动。",
            "重试启动", SetupCompletionAction.StartWorkbench, false);
        if (canResume) return new(hasError ? "模型下载需要重试。" : "模型下载已暂停。", "已完成的文件会保留，继续时会重新确认实际位置与文件状态。",
            hasError ? "重试下载" : "继续下载", SetupCompletionAction.ResumeModels, true);
        return readiness switch
        {
            SetupModelReadiness.Ready => new("环境已准备好。", "基础模型已找到。打开工作台时会先确认服务连接正常。", "打开工作台", SetupCompletionAction.StartWorkbench, false),
            SetupModelReadiness.Checking => new("正在检查基础模型。", "正在确认实际模型位置和文件状态，请稍候。", "正在检查…", SetupCompletionAction.None, false),
            SetupModelReadiness.ScanFailed => new("还不能确认模型状态。", "环境配置已保留。请重新检查，也可以先打开工作台查看。", "重新检查模型", SetupCompletionAction.ReviewModels, true),
            SetupModelReadiness.NeedsAttention => new("还有模型需要处理。", "请查看下方说明。已有异常文件会保留，可自动补齐的模型可以单独下载。",
                reviewVisible && canDownload ? "下载可补齐模型" : "重新检查模型", reviewVisible && canDownload ? SetupCompletionAction.DownloadModels : SetupCompletionAction.ReviewModels, true),
            SetupModelReadiness.NeedsDownload => new("环境已连接，还需准备基础模型。", "先确认缺少的模型、下载量和保存位置，再开始下载。也可以先打开工作台。",
                reviewVisible && canDownload ? "开始下载" : "准备基础模型", reviewVisible && canDownload ? SetupCompletionAction.DownloadModels : SetupCompletionAction.ReviewModels, true),
            _ => new("环境已连接。", "检查基础模型后，即可确认下一步。", "检查基础模型", SetupCompletionAction.ReviewModels, true),
        };
    }
}
