using Anima.Launcher.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Anima.Launcher.Pages;

public sealed partial class ModelsPage : LauncherPage
{
    private bool active;

    public ModelsPage()
    {
        InitializeComponent();
        ModelView.Source = State.Groups;
        ModelList.SelectionChanged += (_, _) => UpdateDetails();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        active = true;
        State.Scanned += OnScanned;
        State.BusyChanged += OnBusy;
        State.DownloadsChanged += OnDownloads;
        State.FieldsReloaded += OnFieldsReloaded;
        OnDownloads();
        OnBusy(State.Busy);
        UpdateDetails();
        await State.EnsureModelLibraryAsync();
        if (active) UpdateDetails();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        active = false;
        State.Scanned -= OnScanned;
        State.BusyChanged -= OnBusy;
        State.DownloadsChanged -= OnDownloads;
        State.FieldsReloaded -= OnFieldsReloaded;
        base.OnNavigatedFrom(e);
    }

    private void OnScanned(List<ModelStatus> scan) => DispatcherQueue.TryEnqueue(() =>
    {
        if (!active) return;
        if (ModelList.SelectedItem is null && ModelList.Items.Count > 0) ModelList.SelectedIndex = 0;
        UpdateDetails();
    });

    private void OnBusy(bool busy) => DispatcherQueue.TryEnqueue(() =>
    {
        if (!active) return;
        ModelActions.IsEnabled = !busy && !State.PreparingModelDownloads;
        State.RefreshModelDownloadRows();
        ModelList.IsEnabled = true;
        OpenLocationButton.IsEnabled = ModelList.SelectedItem is ModelRow;
    });

    private async void OnFieldsReloaded() { if (active) await State.EnsureModelLibraryAsync(); }

    private void OnDownloads() => DispatcherQueue.TryEnqueue(() =>
    {
        if (!active) return;
        ModelActions.IsEnabled = !State.Busy && !State.PreparingModelDownloads;
        DownloadButton.Content = State.PreparingModelDownloads ? "正在检查模型…" : "下载所选模型";
        var pending = State.Rows.Count(r => r.Download is { Status: not (DownloadTaskStatus.Completed or DownloadTaskStatus.Canceled) });
        DownloadSummary.Text = pending > 0 ? $"{pending} 个模型任务 · 可在条目中暂停、继续或重试" : "下载进度和操作会显示在对应模型中。";
        UpdateDetails();
    });

    private void UpdateDetails()
    {
        EmptyNote.Visibility = State.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (ModelList.SelectedItem is ModelRow row)
        {
            var s = row.Status;
            var source = s.Model.SourceUrl.Length > 0 ? s.Model.SourceUrl : string.Join(" ; ", s.Model.Urls);
            var verification = s.Model.IsPackage ? string.Join("\n", s.Model.Files.Select(file => file.Path + " · " + file.Sha256))
                : s.Model.CanDownload ? s.Model.Sha256 : "仅手动导入；尚未锁定自动下载清单";
            Details.Text = $"{s.Model.Name}\n位置：{s.FoundPath ?? s.Destination}\n来源：{source}\nSHA-256：{verification}\n授权：{s.Model.License} {s.Model.LicenseUrl}";
            OpenLocationButton.IsEnabled = true;
            OpenLicenseButton.IsEnabled = s.Model.SourceUrl.Length > 0 || s.Model.LicenseUrl.Length > 0 || s.Model.Urls.Length > 0;
        }
        else
        {
            Details.Text = "选择一个模型查看来源、校验与授权。";
            OpenLocationButton.IsEnabled = false;
            OpenLicenseButton.IsEnabled = false;
        }
    }

    private async Task RunModelTask(Func<CancellationToken, Task> action)
    {
        if (State.Busy) return;
        Shell.SyncPagesIntoConfig();
        await State.Run(action);
    }
    private async void Rescan_Click(object sender, RoutedEventArgs e) { Shell.SyncPagesIntoConfig(); await State.RefreshModelLibraryAsync(); }
    private async void HashScan_Click(object sender, RoutedEventArgs e) { Shell.SyncPagesIntoConfig(); await State.RefreshModelLibraryAsync(true); }
    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        Shell.SyncPagesIntoConfig();
        await State.StartSelectedModelDownloadsAsync();
    }
    private async void TestImage_Click(object sender, RoutedEventArgs e) => await RunModelTask(State.TestGenerationAsync);

    private void AllDownloads_Click(object sender, RoutedEventArgs e) => Shell.OpenDownloads(2);
    private async void ModelDownloadAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ModelRow row }) await State.ActOnModelDownloadAsync(row);
    }
    private void ModelDownloadDetails_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ModelRow row }) Shell.OpenDownloads(2, row.Download?.OwnerId, row.Status.Model.Id);
    }

    private void SelectBase_Click(object sender, RoutedEventArgs e)
    {
        if (!State.Busy) State.SelectBaseGroup();
    }

    private void DownloadActions_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 560;
        Grid.SetColumn(DownloadButton, narrow ? 0 : 1);
        Grid.SetRow(DownloadButton, narrow ? 1 : 0);
        Grid.SetColumnSpan(DownloadButton, narrow ? 2 : 1);
        DownloadButton.HorizontalAlignment = narrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
    }

    private void FolderActions_SizeChanged(object sender, SizeChangedEventArgs e) =>
        FolderActions.Orientation = e.NewSize.Width < 460 ? Orientation.Vertical : Orientation.Horizontal;

    private void DetailActions_SizeChanged(object sender, SizeChangedEventArgs e) =>
        DetailActions.Orientation = e.NewSize.Width < 360 ? Orientation.Vertical : Orientation.Horizontal;

    private async void Folder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string category } anchor)
            await State.OpenModelFolderAsync(category, anchor is MenuFlyoutItem ? MoreFoldersButton : anchor);
    }

    private async void OpenModelDir_Click(object sender, RoutedEventArgs e)
    {
        if (ModelList.SelectedItem is not ModelRow row) return;
        try
        {
            var location = row.Status.FoundPath ?? row.Status.Destination;
            var folder = row.Status.Model.IsPackage ? location : Path.GetDirectoryName(location);
            if (string.IsNullOrWhiteSpace(folder)) throw new IOException("此模型目录尚未确定，请重新扫描。");
            Directory.CreateDirectory(folder);
            Program.Open(folder);
        }
        catch (Exception ex) { if (State.Root is { } root) await Dialogs.Error(root, ex.Message); }
    }

    private void OpenLicense_Click(object sender, RoutedEventArgs e)
    {
        if (ModelList.SelectedItem is not ModelRow row) return;
        var model = row.Status.Model;
        var url = model.SourceUrl.Length > 0 ? model.SourceUrl : model.LicenseUrl.Length > 0 ? model.LicenseUrl : model.Urls.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(url)) Program.Open(url);
    }
}
