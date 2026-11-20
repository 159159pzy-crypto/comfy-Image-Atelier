using Anima.Launcher.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Anima.Launcher.Pages;

public sealed partial class ModelsPage : LauncherPage
{
    private bool active;
    private string? displayedFeatureOwner;

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
        UpdateFeatureProgress();
    });

    private async void OnFieldsReloaded() { if (active) await State.EnsureModelLibraryAsync(); }

    private void OnDownloads() => DispatcherQueue.TryEnqueue(() =>
    {
        if (!active) return;
        ModelActions.IsEnabled = !State.Busy && !State.PreparingModelDownloads;
        DownloadButton.IsEnabled = !State.Config.ReadOnlyConnection;
        PrepareFeaturesButton.IsEnabled = !State.Config.ReadOnlyConnection;
        PrepareFeaturesButton.Content = State.PreparingModelDownloads ? "正在检查资源…" : "一键准备完整功能";
        var pending = State.Rows.Count(r => r.Download is { Status: not (DownloadTaskStatus.Completed or DownloadTaskStatus.Canceled) });
        DownloadSummary.Text = State.Config.ReadOnlyConnection ? LauncherConfig.ReadOnlyMaintenanceNotice
            : pending > 0 ? $"{pending} 个模型任务 · 可在条目中暂停、继续或重试" : "下载进度和操作会显示在对应模型中。";
        UpdateDetails();
        UpdateFeatureProgress();
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
    private async void PrepareFeatures_Click(object sender, RoutedEventArgs e)
    {
        if (State.Busy || State.PreparingModelDownloads) return;
        Shell.SyncPagesIntoConfig();
        try
        {
            var selectedModels = State.Rows.Where(row => row.Selected).Select(row => row.Status.Model.Id).ToHashSet(StringComparer.Ordinal);
            var choices = State.FeaturePreparationChoices;
            var body = new StackPanel { Spacing = 12 };
            body.Children.Add(new TextBlock { Text = "选择本次需要准备的功能。已有文件会复用；选择完成后先检查详细清单与下载量。", TextWrapping = TextWrapping.Wrap });
            var boxes = choices.Select(choice =>
            {
                var label = new StackPanel { Spacing = 3 };
                label.Children.Add(new TextBlock { Text = choice.Label, TextWrapping = TextWrapping.Wrap });
                label.Children.Add(new TextBlock { Text = choice.Description, TextWrapping = TextWrapping.Wrap, FontSize = 12 });
                var box = new CheckBox { Content = label, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    IsChecked = choice.ModelIds.Length > 0 && selectedModels.Contains(choice.ModelIds[0]) };
                body.Children.Add(box);
                return (Choice: choice, Box: box);
            }).ToArray();
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
            body.Children.Add(error);
            var dialog = Dialogs.Create(XamlRoot, "准备完整功能", new ScrollViewer
            {
                MaxHeight = Math.Clamp(XamlRoot.Size.Height - 260, 120, 520), Content = body,
                HorizontalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            }, "检查所选功能");
            dialog.PrimaryButtonClick += (_, args) =>
            {
                if (!boxes.Any(item => item.Box.IsChecked == true))
                { args.Cancel = true; error.Text = "请至少选择一项功能。"; }
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var plan = await State.PreviewFeaturePreparationAsync(boxes.Where(item => item.Box.IsChecked == true)
                .Select(item => item.Choice.Id).ToArray());
            if (plan is null || !active) return;
            if (State.FeaturePreparationPreflightIssue.Length > 0)
            {
                await Dialogs.Error(XamlRoot, State.FeaturePreparationPreflightIssue + "\n\n" + FeaturePreparation.Describe(plan), "准备清单 · 需要先停止服务");
                return;
            }
            if (await Dialogs.Confirm(XamlRoot, "确认完整功能准备", FeaturePreparation.Describe(plan), "确认并开始准备"))
                await State.StartFeaturePreparationAsync(plan);
        }
        catch (Exception ex) { await Dialogs.Error(XamlRoot, ex.Message); }
    }

    private void UpdateFeatureProgress()
    {
        var progress = State.FeaturePreparationProgress;
        displayedFeatureOwner = progress?.OwnerId;
        FeatureProgressPanel.Visibility = progress is null ? Visibility.Collapsed : Visibility.Visible;
        if (progress is null) return;
        var status = progress.Status switch
        {
            DownloadTaskStatus.Completed => "完整功能已准备并通过运行检查",
            DownloadTaskStatus.Running => "正在准备完整功能",
            DownloadTaskStatus.Pausing => "正在保存准备进度",
            DownloadTaskStatus.Failed => "完整功能尚未准备好，可处理后重试",
            DownloadTaskStatus.NeedsReview => "准备需要处理后继续",
            DownloadTaskStatus.Canceled => "已取消准备，文件保留",
            _ => "完整功能准备待继续"
        };
        FeatureStatusText.Text = status + (progress.Detail.Length > 0 ? "\n" + progress.Detail : "");
        FeatureProgressBar.IsIndeterminate = progress.Indeterminate;
        FeatureProgressBar.Value = progress.Percent;
        FeatureTransferText.Text = progress.Total > 0
            ? $"已下载 {progress.Received / 1048576d:N1} / {progress.Total / 1048576d:N1} MiB"
                + (progress.Speed > 0 ? $" · {progress.Speed / 1048576d:N1} MiB/s" : "")
            : progress.Status == DownloadTaskStatus.Completed ? "所选完整功能已通过运行检查。" : "插件和运行文件准备完成后还会启动服务检查。";
        FeatureErrorText.Text = progress.Error;
        FeatureErrorText.Visibility = progress.Error.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        PauseFeatureButton.Visibility = progress.Status is DownloadTaskStatus.Running or DownloadTaskStatus.Pausing ? Visibility.Visible : Visibility.Collapsed;
        PauseFeatureButton.IsEnabled = progress.Status == DownloadTaskStatus.Running;
        ResumeFeatureButton.Visibility = progress.Status is DownloadTaskStatus.Queued or DownloadTaskStatus.Paused or DownloadTaskStatus.Failed or DownloadTaskStatus.NeedsReview ? Visibility.Visible : Visibility.Collapsed;
        ResumeFeatureButton.IsEnabled = !State.Busy && !State.PreparingModelDownloads;
        ResumeFeatureButton.Content = progress.Status is DownloadTaskStatus.Failed or DownloadTaskStatus.NeedsReview ? "重试准备" : "继续准备";
    }
    private async void PauseFeature_Click(object sender, RoutedEventArgs e)
    {
        if (displayedFeatureOwner is not { } owner) return;
        try { await State.PauseInstallationDownloadAsync(owner); }
        catch (Exception ex) { await Dialogs.Error(XamlRoot, ex.Message); }
    }
    private async void ResumeFeature_Click(object sender, RoutedEventArgs e)
    {
        if (displayedFeatureOwner is { } owner && !State.Busy) await State.ResumeDownloadOwnerAsync(owner);
    }
    private void FeatureDetails_Click(object sender, RoutedEventArgs e)
    {
        if (displayedFeatureOwner is { } owner) Shell.OpenDownloads(2, owner);
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
        Grid.SetColumn(PrepareFeaturesButton, narrow ? 0 : 1);
        Grid.SetRow(PrepareFeaturesButton, narrow ? 1 : 0);
        Grid.SetColumnSpan(PrepareFeaturesButton, narrow ? 2 : 1);
        PrepareFeaturesButton.HorizontalAlignment = narrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
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
            LauncherState.EnsureModelFolder(State.Config, folder);
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
