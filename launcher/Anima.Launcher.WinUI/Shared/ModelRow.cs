using Anima.Launcher.Core;
using System.ComponentModel;

namespace Anima.Launcher;

/// <summary>View row for one model item; wraps <see cref="ModelStatus"/> with selection state.</summary>
internal sealed class ModelRow(ModelStatus status) : INotifyPropertyChanged
{
    private bool selected;
    public ModelStatus Status { get; private set; } = status;
    public ModelDownloadSnapshot? Download { get; private set; }
    private bool actionsBlocked;
    public string Group => Status.Model.Group;
    public string Name => Status.Model.Name + (Status.Model.Recommended ? " · 推荐" : "");
    public string StateText => Download is { } download ? download.Status switch
    {
        DownloadTaskStatus.Completed => Status.Missing ? Status.State : "已就绪",
        DownloadTaskStatus.Paused => "待继续",
        DownloadTaskStatus.Pausing => "正在保存断点",
        DownloadTaskStatus.Failed => "下载失败",
        DownloadTaskStatus.NeedsReview => "需要处理",
        DownloadTaskStatus.Canceled => "已取消 · 文件保留",
        DownloadTaskStatus.Queued => "排队中",
        _ => download.Phase switch { "Verifying" => "校验中", "Publishing" => "正在准备模型", "RetryWaiting" or "WaitingRetry" => "等待重试", _ => "下载中" },
    } : Status.State + (!Status.Model.CanDownload ? " / 手动" : "");
    public string SizeText => $"{Status.Model.Size / 1048576d:N0} MiB";
    public string PathText => Status.FoundPath ?? Status.Destination;
    /// <summary>Only downloadable, still-missing, not-yet-present files may be selected.</summary>
    public bool CanSelect => Download is not { Status: DownloadTaskStatus.Running or DownloadTaskStatus.Queued or DownloadTaskStatus.Pausing }
        && Status.Model.CanDownload && Status.Missing && Status.Destination.Length > 0
        && (Status.FoundPath is null || Status.Model.IsPackage);
    public bool HasDownload => Download is not null;
    public bool ShowTransfer => Download is { Status: not DownloadTaskStatus.Completed };
    public bool HasDownloadError => Download is { Status: DownloadTaskStatus.Failed or DownloadTaskStatus.NeedsReview, Error.Length: > 0 };
    public string DownloadError => Download?.Error ?? "";
    public double DownloadPercent => Download?.Percent ?? 0;
    public string TransferText => Download is not { } d ? "" : $"{d.Received / 1048576d:N1} / {d.Total / 1048576d:N1} MiB"
        + (d.Speed > 0 && d.Status == DownloadTaskStatus.Running ? $" · {d.Speed / 1048576d:N1} MiB/s" : "");
    public bool HasDownloadAction => Download is { CanPause: true } or { CanResume: true } or { CanRetry: true };
    public bool CanDownloadAction => HasDownloadAction && !actionsBlocked;
    public string DownloadActionText => Download?.CanPause == true ? "暂停" : Download?.CanRetry == true ? "重试" : "继续";
    public bool Selected
    {
        get => selected;
        set { if ((!value || CanSelect) && selected != value) { selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected))); } }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    public void UpdateDownload(ModelDownloadSnapshot? download, bool blocked)
    {
        Download = download;
        actionsBlocked = blocked;
        if (!CanSelect) selected = false;
        Refresh();
    }
    public void UpdateStatus(ModelStatus value)
    {
        Status = value;
        if (!CanSelect) selected = false;
        Refresh();
    }
}
