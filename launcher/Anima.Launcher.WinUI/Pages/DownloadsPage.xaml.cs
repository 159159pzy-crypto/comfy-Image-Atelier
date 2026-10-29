using Anima.Launcher.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace Anima.Launcher.Pages;

internal sealed record DownloadsTarget(string? OwnerId = null, string? ModelId = null);

public sealed partial class DownloadsPage : LauncherPage
{
    private readonly ObservableCollection<TransferRow> rows = [];
    private readonly Dictionary<string, TransferRow> rowIndex = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool active;
    private bool refreshing;
    private bool applyTarget;
    private DownloadsTarget target = new();

    public DownloadsPage()
    {
        InitializeComponent();
        TaskList.ItemsSource = rows;
        timer.Tick += (_, _) => RefreshCountdowns();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        active = true;
        State.DownloadsChanged += Changed;
        ShowTarget(e.Parameter as DownloadsTarget ?? new());
        State.PresentationVisibilityChanged += VisibilityChanged;
    }
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        active = false;
        timer.Stop();
        State.DownloadsChanged -= Changed;
        State.PresentationVisibilityChanged -= VisibilityChanged;
        base.OnNavigatedFrom(e);
    }
    internal void ShowTarget(DownloadsTarget value)
    {
        target = value;
        applyTarget = true;
        ReturnButton.Content = Shell.DownloadsReturnText;
        Refresh();
    }
    private void Return_Click(object sender, RoutedEventArgs e) => Shell.ReturnFromDownloads();
    private void Changed() { if (active && State.PresentationVisible) Refresh(); }
    private void VisibilityChanged(bool visible) { timer.Stop(); if (active && visible) Refresh(); }
    private void RefreshCountdowns()
    {
        if (!active || !State.PresentationVisible) { timer.Stop(); return; }
        foreach (var row in rows) row.RefreshCountdown();
        if (!rows.Any(row => row.Record.RetryAt > DateTimeOffset.UtcNow)) timer.Stop();
    }
    private void Refresh()
    {
        if (!active || !State.PresentationVisible || refreshing) return;
        refreshing = true;
        try
        {
            if (State.DownloadTasks.RecoveryWarning is { } warning) Notice.Text = warning;
            var snapshot = State.DownloadView;
            var owners = new[] { new DownloadOwnerRecord { Id = "", Title = "全部任务" } }
                .Concat(snapshot.Owners.OrderByDescending(o => o.CreatedAt)).ToArray();
            var selectedOwner = applyTarget ? target.OwnerId : (OwnerBox.SelectedItem as DownloadOwnerRecord)?.Id;
            if (applyTarget && string.IsNullOrEmpty(selectedOwner) && target.ModelId is { } modelId)
                selectedOwner = owners.FirstOrDefault(o => o.Kind == "models"
                    && SamePath(o.EnvironmentRoot, State.Config.ComfyRoot) && o.Models.Any(m => m.Model.Id == modelId))?.Id;
            // Keep selection stable while transport progress changes.
            if (applyTarget || OwnerBox.ItemsSource is not DownloadOwnerRecord[] old || !old.Select(o => o.Id).SequenceEqual(owners.Select(o => o.Id)))
            {
                OwnerBox.ItemsSource = owners;
                OwnerBox.SelectedItem = owners.FirstOrDefault(o => o.Id == selectedOwner) ?? owners.FirstOrDefault();
            }
            applyTarget = false;
            selectedOwner = (OwnerBox.SelectedItem as DownloadOwnerRecord)?.Id;
            var owner = owners.FirstOrDefault(o => o.Id == selectedOwner);
            var tasks = snapshot.Tasks.Where(t => owner is null || owner.Id.Length == 0 || BelongsTo(t, owner, target.ModelId))
                .OrderBy(t => t.CreatedAt).ToArray();
            var ids = tasks.Select(t => t.Id).ToHashSet();
            for (var i = rows.Count - 1; i >= 0; i--) if (!ids.Contains(rows[i].Id)) { rowIndex.Remove(rows[i].Id); rows.RemoveAt(i); }
            foreach (var task in tasks)
            {
                if (!rowIndex.TryGetValue(task.Id, out var row)) { row = new(task); rowIndex.Add(task.Id, row); rows.Add(row); }
                else row.Update(task);
            }
            if (TaskList.SelectedItem is null && rows.Count > 0) TaskList.SelectedIndex = 0;
            ShowSelection();
            if (rows.Any(row => row.Record.RetryAt > DateTimeOffset.UtcNow)) timer.Start(); else timer.Stop();
        }
        finally { refreshing = false; }
    }
    private void Owner_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (refreshing) return;
        target = new((OwnerBox.SelectedItem as DownloadOwnerRecord)?.Id);
        Refresh();
    }
    private void Task_Changed(object sender, SelectionChangedEventArgs e) => ShowSelection();
    private TransferRow? Selected => TaskList.SelectedItem as TransferRow;
    private void ShowSelection()
    {
        SelectionText.Text = Selected is { } row
            ? row.Record.Destination + "\n" + (string.IsNullOrEmpty(row.Record.Error) ? row.Detail : row.Record.Error)
            : State.DownloadView.Tasks.Count == 0 && State.DownloadView.Owners.Count == 0
                ? "还没有下载任务。返回模型资源、安装环境或设置后，可以按需开始下载。"
                : "此任务尚无文件记录；模型发布和安装步骤完成后，整个任务才会完成。";
        var owner = SelectedOwner();
        ContinueOwnerButton.Content = target.ModelId is not null && owner?.Kind == "models" ? "继续此模型" : "继续此批次";
        OwnerStatusText.Text = owner is null ? "文件校验与批次完成分别记录；包发布和安装步骤成功后批次才完成。"
            : owner.Title + " · " + (owner.Status switch { DownloadTaskStatus.Completed => "已完成", DownloadTaskStatus.Failed => "失败，需要重试",
                DownloadTaskStatus.Running => "处理中", DownloadTaskStatus.Pausing => "正在保存断点", DownloadTaskStatus.Queued => "排队",
                DownloadTaskStatus.Canceled => "已取消", DownloadTaskStatus.NeedsReview => "需要处理", _ => "待继续" })
                + (string.IsNullOrEmpty(owner.Detail) ? "" : " · " + owner.Detail)
                + (string.IsNullOrEmpty(owner.Error) ? "" : "\n" + owner.Error);
    }
    private DownloadOwnerRecord? SelectedOwner(bool live = false)
    {
        IReadOnlyList<DownloadOwnerRecord> all = live ? State.DownloadTasks.Owners : State.DownloadView.Owners;
        if (OwnerBox.SelectedItem is DownloadOwnerRecord { Id.Length: > 0 } chosen)
        {
            var current = all.FirstOrDefault(o => o.Id == chosen.Id);
            if (current is not null && target.ModelId is { } modelId && current.ResourceOwnerIds.TryGetValue(modelId, out var resourceId))
                return all.FirstOrDefault(o => o.Id == resourceId) ?? current;
            return current;
        }
        if (Selected is not { } row) return null;
        return all.Where(o => o.Kind != "file" && BelongsTo(row.Record, o, null))
            .OrderBy(o => o.ResourceOwnerIds.Count > 0)
            .ThenBy(o => o.Status is DownloadTaskStatus.Canceled or DownloadTaskStatus.Completed)
            .ThenByDescending(o => o.CreatedAt).FirstOrDefault();
    }

    private static bool BelongsTo(DownloadTaskRecord task, DownloadOwnerRecord owner, string? modelId)
    {
        var intents = modelId is null ? owner.Models : owner.Models.Where(m => m.Model.Id == modelId);
        var identities = intents.ToArray();
        var related = task.OwnerIds.Contains(owner.Id) || task.OwnerIds.Any(owner.ResourceOwnerIds.ContainsValue);
        // A derived single-model task may not yet have fetched a file. Match its frozen
        // content to retained parent records, so detail views remain useful before resume.
        if (owner.Kind == "models" && identities.Any(m => Matches(task, m))) return true;
        return related && (modelId is null || identities.Any(m => Matches(task, m)));
    }

    private static bool Matches(DownloadTaskRecord task, ModelDownloadIntent intent)
    {
        if (!intent.Model.IsPackage)
            return SamePath(task.Destination, intent.Destination) && task.Size == intent.Model.Size
                && task.Sha256.Equals(intent.Model.Sha256, StringComparison.OrdinalIgnoreCase);
        var staging = ModelPackages.Staging(intent.Model, intent.Destination);
        return intent.Model.Files.Any(file => SamePath(task.Destination, Safety.Under(staging, file.Path))
            && task.Size == file.Size && task.Sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase));
    }

    private static bool SamePath(string left, string right) => !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right)
        && Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);
    private async Task Act(Func<Task> action)
    {
        try { Notice.Text = ""; await action(); }
        catch (OperationCanceledException) { Notice.Text = "已停止，文件保留。"; }
        catch (Exception ex) { Notice.Text = ex.Message; }
        Refresh();
    }
    private async void ContinueAll_Click(object sender, RoutedEventArgs e) => await Act(State.ContinueAllDownloadsAsync);
    private async void PauseAll_Click(object sender, RoutedEventArgs e) => await Act(() => State.DownloadTasks.PauseAllAsync());
    private async void Network_Click(object sender, RoutedEventArgs e)
    {
        var config = State.Config;
        var mode = new ComboBox { Header = "网络连接", HorizontalAlignment = HorizontalAlignment.Stretch };
        mode.Items.Add("系统代理"); mode.Items.Add("直接连接"); mode.Items.Add("自定义 HTTP 代理");
        mode.SelectedIndex = config.NetworkMode switch { "direct" => 1, "custom" => 2, _ => 0 };
        var proxy = new TextBox { Header = "HTTP 代理地址", Text = config.ProxyAddress, PlaceholderText = "http://127.0.0.1:7890" };
        var source = new ComboBox { Header = "下载来源", ItemsSource = Program.Sources.Profiles, DisplayMemberPath = nameof(SourceProfile.Name), HorizontalAlignment = HorizontalAlignment.Stretch };
        source.SelectedItem = Program.Sources.Profiles.FirstOrDefault(p => p.Id == config.SourceId);
        var git = new TextBox { Header = "Git HTTPS 镜像模板", Text = config.GitMirror };
        var hf = new TextBox { Header = "Hugging Face 镜像", Text = config.ModelMirror };
        var pip = new TextBox { Header = "Python 包索引", Text = config.PipIndex };
        source.SelectionChanged += (_, _) =>
        {
            if (source.SelectedItem is SourceProfile profile && profile.Id != "custom")
            { git.Text = profile.GitTemplate; hf.Text = profile.HuggingFaceBase; pip.Text = profile.PipIndex; }
        };
        var panel = new StackPanel { Spacing = 10 };
        foreach (var control in new FrameworkElement[] { mode, proxy, source, git, hf, pip }) panel.Children.Add(control);
        panel.Children.Add(new TextBlock { Text = "保存仅影响新任务。暂停任务可选择使用新来源继续，固定版本与目标保持不变。", TextWrapping = TextWrapping.Wrap });
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "网络与下载来源", PrimaryButtonText = "保存", CloseButtonText = "取消", Content = new ScrollViewer { Content = panel, MaxHeight = 420 } };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await Act(() => { State.SaveDownloadSettings((source.SelectedItem as SourceProfile)?.Id ?? config.SourceId,
                git.Text, hf.Text, pip.Text, mode.SelectedIndex switch { 1 => "direct", 2 => "custom", _ => "system" }, proxy.Text); return Task.CompletedTask; });
    }
    private async void ContinueOwner_Click(object sender, RoutedEventArgs e)
    {
        if (OwnerBox.SelectedItem is DownloadOwnerRecord { Id.Length: > 0 } owner)
            await Act(async () =>
            {
                if (owner.Kind == "models" && target.ModelId is { } modelId)
                {
                    var resources = State.DownloadTasks.EnsureResourceOwners(owner.Id);
                    var resource = resources.FirstOrDefault(o => o.Models.Any(m => m.Model.Id == modelId));
                    if (resource is null) throw new InvalidOperationException("原任务中已找不到此模型，请返回模型资源重新确认。");
                    await State.ResumeDownloadOwnerAsync(resource.Id);
                }
                else await State.ResumeDownloadOwnerAsync(owner.Id);
            });
        else await Act(State.ContinueAllDownloadsAsync);
    }
    private async Task ContinueSelected(bool newSources)
    {
        if (Selected is not { } row) return;
        var owner = SelectedOwner(live: true);
        if (owner is not null) await State.ResumeDownloadResourceAsync(row.Id, owner.Id, newSources);
        else
        {
            if (newSources) throw new InvalidOperationException("请从原资源入口创建新的来源策略。");
            await State.DownloadTasks.ResumeTaskAsync(row.Id);
        }
    }
    private async void Continue_Click(object sender, RoutedEventArgs e) => await Act(() => ContinueSelected(false));
    private async void NewSource_Click(object sender, RoutedEventArgs e) => await Act(() => ContinueSelected(true));
    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } row) await Act(() => State.DownloadTasks.PauseTaskAsync(row.Id));
    }
    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } row) await Act(() => State.DownloadTasks.CancelTaskAsync(row.Id));
    }
    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        await Act(async () =>
        {
            var preview = State.DownloadTasks.CleanupPreview(row.Id);
            if (preview.Paths.Length == 0) { Notice.Text = "没有可以删除的独占临时文件。"; return; }
            if (State.Root is not { } root) return;
            if (await Dialogs.Confirm(root, "删除未完成文件", $"将删除此任务独占的 {preview.Bytes / 1048576d:N1} MiB 临时数据：\n" + string.Join("\n", preview.Paths), "删除文件", "保留"))
                await State.DownloadTasks.DeletePartialAsync(row.Id);
        });
    }
    private async void Compatibility_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row || State.Root is not { } root) return;
        await Act(async () =>
        {
            if (await Dialogs.Confirm(root, "兼容模式重新下载", $"将保留现有 aria2 断点，使用独立的兼容暂存重新下载此文件，可能额外需要 {row.Record.Size / 1073741824d:N2} GiB。开始前会重新检查磁盘空间，完成后仍须通过原 SHA-256 校验。\n请先暂停任务。", "使用兼容模式", "保留原引擎"))
            {
                State.DownloadTasks.SetTaskCompatibilityMode(row.Id);
                await ContinueSelected(false);
            }
        });
    }
    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        await Act(() => { var folder = Path.GetDirectoryName(row.Record.Destination)!; Directory.CreateDirectory(folder); Program.Open(folder); return Task.CompletedTask; });
    }

    private sealed class TransferRow(DownloadTaskRecord record) : INotifyPropertyChanged
    {
        public DownloadTaskRecord Record { get; private set; } = record;
        public string Id => Record.Id;
        public string Name => Record.Name;
        public double Percent => Record.Status == DownloadTaskStatus.Completed ? 100 : Math.Min(99.9, 100d * Record.Received / Math.Max(1, Record.Size));
        public string Summary => Label(Record) + $" · {Record.Received / 1048576d:N1} / {Record.Size / 1048576d:N1} MiB"
            + (Record.Speed is > 0 && Record.Status == DownloadTaskStatus.Running ? $" · {Record.Speed / 1048576d:N1} MiB/s · 剩余 {TimeSpan.FromSeconds(Math.Min(359999, Math.Max(0, Record.Size - Record.Received) / Record.Speed.Value)):hh\\:mm\\:ss}" : "")
            + (Record.Connections is > 0 ? $" · {Record.Connections} 连接" : "");
        public string Detail => (Record.Source is { Length: > 0 } source ? "来源：" + source : "等待选择来源")
            + (Record.Engine is { } engine ? " · " + engine : "")
            + (Record.RetryAt > DateTimeOffset.UtcNow ? $" · {Math.Ceiling((Record.RetryAt.Value - DateTimeOffset.UtcNow).TotalSeconds):N0} 秒后重试" : "")
            + (Record.Error.Length > 0 ? "\n" + Record.Error : "");
        private static string Label(DownloadTaskRecord t) => t.Status switch
        {
            DownloadTaskStatus.Completed => "文件已校验 · 批次状态见上方", DownloadTaskStatus.Paused => "待继续", DownloadTaskStatus.Pausing => "正在保存断点",
            DownloadTaskStatus.Canceled => "已取消 · 文件保留", DownloadTaskStatus.Failed => "失败", DownloadTaskStatus.NeedsReview => "需要处理",
            DownloadTaskStatus.Queued => "排队", _ => t.Phase switch { "Verifying" => "校验中", "Publishing" => "发布中", "RetryWaiting" or "WaitingRetry" => "等待重试", "Connecting" => "连接来源", _ => "下载中" },
        };
        public event PropertyChangedEventHandler? PropertyChanged;
        public override string ToString() => Name + " · " + Label(Record);
        public void Update(DownloadTaskRecord value)
        {
            var before = (Name, Summary, Percent, Detail);
            Record = value;
            if (before != (Name, Summary, Percent, Detail)) PropertyChanged?.Invoke(this, new(""));
        }
        public void RefreshCountdown() { if (Record.RetryAt is not null) PropertyChanged?.Invoke(this, new(nameof(Detail))); }
    }
}
