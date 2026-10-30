using Anima.Launcher.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System.Text.Json;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Anima.Launcher.Pages;

public sealed partial class EnvironmentPage : LauncherPage
{
    private bool loading = true;
    private bool pythonExplicit;
    private bool active;
    private bool previewing;
    private bool previewConfirmed;
    private int previewVersion;
    private int previewRequestVersion;
    private string? displayedInstallationOwner;
    private string? displayedConfiguration;

    public EnvironmentPage()
    {
        InitializeComponent();
        SourceBox.ItemsSource = Program.Sources.Profiles;
        SourceBox.DisplayMemberPath = nameof(SourceProfile.Name);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        active = true;
        var configuration = JsonSerializer.Serialize(State.Config, JsonFile.Options);
        if (!Shell.ReturningFromDownloads || displayedConfiguration != configuration)
        {
            previewConfirmed = false;
            previewVersion++;
            LoadFields();
        }
        displayedConfiguration = configuration;
        State.FieldsReloaded += OnReloaded;
        State.BusyChanged += OnBusy;
        State.InstallSummaryChanged += OnSummary;
        State.ComfyUpdateChanged += OnComfyUpdate;
        State.NodeUpdateChanged += OnNodeUpdate;
        State.DownloadsChanged += OnDownloadsChanged;
        State.DependencyProgressChanged += OnDependencyProgress;
        UpdateComfyMaintenance();
        UpdateNodeMaintenance();
        UpdateInstallationProgress();
        UpdateDependencyProgress();
        ConfigurationPanel.IsEnabled = !State.Busy;
        OpenSetupButton.IsEnabled = !State.Busy;
        ApplyInstallEligibility();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (!State.Busy && !State.SetupActive) SyncConfig();
        active = false;
        State.FieldsReloaded -= OnReloaded;
        State.BusyChanged -= OnBusy;
        State.InstallSummaryChanged -= OnSummary;
        State.ComfyUpdateChanged -= OnComfyUpdate;
        State.NodeUpdateChanged -= OnNodeUpdate;
        State.DownloadsChanged -= OnDownloadsChanged;
        State.DependencyProgressChanged -= OnDependencyProgress;
        base.OnNavigatedFrom(e);
    }

    private void OnReloaded() => DispatcherQueue.TryEnqueue(() => { if (active) { LoadFields(); UpdateInstallationProgress(); } });
    private void OnBusy(bool busy) => DispatcherQueue.TryEnqueue(() =>
    {
        ConfigurationPanel.IsEnabled = !busy;
        OpenSetupButton.IsEnabled = !busy;
        UpdateComfyMaintenance();
        UpdateNodeMaintenance();
        ApplyInstallEligibility();
        UpdateInstallationProgress();
        UpdateDependencyProgress();
    });
    private void OnDependencyProgress() { if (active) UpdateDependencyProgress(); }
    private void UpdateDependencyProgress()
    {
        var progress = State.DependencyProgressView;
        DependencyProgressPanel.Visibility = progress is null ? Visibility.Collapsed : Visibility.Visible;
        if (progress is null) return;
        DependencyHeading.Text = progress.Heading;
        DependencyDetails.Text = progress.Detail;
        DependencyQuietNotice.Text = progress.QuietNotice;
        DependencyQuietNotice.Visibility = progress.QuietNotice.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        DependencyProgressBar.IsIndeterminate = State.Busy && progress.Indeterminate;
        DependencyProgressBar.Value = progress.Percent;
        var running = State.Busy && State.DependencyActivity?.Status == DependencyProgressStatus.Running;
        PauseDependencyButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        PauseDependencyButton.IsEnabled = running && !dependencyPauseRequested;
        if (!running) dependencyPauseRequested = false;
        ResumeDependencyButton.Visibility = State.CanRetry ? Visibility.Visible : Visibility.Collapsed;
        ResumeDependencyButton.IsEnabled = State.CanRetry;
        ResumeDependencyButton.Content = State.DependencyActivity?.Status == DependencyProgressStatus.Failed ? "重试" : "继续";
    }
    private bool dependencyPauseRequested;
    private void PauseDependency_Click(object sender, RoutedEventArgs e)
    {
        dependencyPauseRequested = true;
        PauseDependencyButton.IsEnabled = false;
        State.PauseTask();
    }
    private async void ResumeDependency_Click(object sender, RoutedEventArgs e)
    {
        if (State.CanRetry) await State.ResumeTaskAsync();
    }
    private void DependencyLogs_Click(object sender, RoutedEventArgs e) => Shell.GoToPage(3);
    private void OnDownloadsChanged() => DispatcherQueue.TryEnqueue(() => { if (active) UpdateInstallationProgress(); });
    private void UpdateInstallationProgress()
    {
        var progress = State.InstallationDownloadProgress;
        displayedInstallationOwner = progress?.OwnerId;
        InstallationProgressPanel.Visibility = progress is null ? Visibility.Collapsed : Visibility.Visible;
        if (progress is null) return;
        var status = progress.Status switch
        {
            DownloadTaskStatus.Running => "正在安装", DownloadTaskStatus.Pausing => "正在暂停",
            DownloadTaskStatus.Completed => "安装已完成", DownloadTaskStatus.Canceled => "已取消，文件保留",
            DownloadTaskStatus.Failed => "安装未完成，可以重试", DownloadTaskStatus.NeedsReview => "需要处理后继续",
            _ => "安装待继续"
        };
        InstallationStatusText.Text = status + (progress.Detail.Length > 0 ? "\n" + progress.Detail : "");
        InstallationProgressBar.IsIndeterminate = progress.Indeterminate;
        InstallationProgressBar.Value = progress.Percent;
        InstallationTransferText.Text = progress.Total > 0
            ? $"已下载 {progress.Received / 1048576d:N1} / {progress.Total / 1048576d:N1} MiB"
                + (progress.Speed > 0 ? $" · {progress.Speed / 1048576d:N1} MiB/s" : "")
                + (progress.Indeterminate ? "\n正在处理安装步骤。" : "")
            : progress.Status is DownloadTaskStatus.Running or DownloadTaskStatus.Pausing ? "正在处理安装步骤。" : "已完成的下载和安装步骤会在继续时复用。";
        InstallationErrorText.Text = progress.Error;
        InstallationErrorText.Visibility = progress.Error.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        PauseInstallationButton.Visibility = progress.Status is DownloadTaskStatus.Running or DownloadTaskStatus.Pausing ? Visibility.Visible : Visibility.Collapsed;
        PauseInstallationButton.IsEnabled = progress.Status == DownloadTaskStatus.Running;
        ResumeInstallationButton.Visibility = progress.Status is DownloadTaskStatus.Queued or DownloadTaskStatus.Paused or DownloadTaskStatus.Failed or DownloadTaskStatus.NeedsReview ? Visibility.Visible : Visibility.Collapsed;
        ResumeInstallationButton.IsEnabled = !State.Busy;
        ResumeInstallationButton.Content = progress.Status is DownloadTaskStatus.Failed or DownloadTaskStatus.NeedsReview ? "重试安装" : "继续安装";
    }
    private async void PauseInstallation_Click(object sender, RoutedEventArgs e)
    {
        if (displayedInstallationOwner is not { } owner) return;
        try { await State.PauseInstallationDownloadAsync(owner); }
        catch (Exception error) { await ShowError(error); }
    }
    private async void ResumeInstallation_Click(object sender, RoutedEventArgs e)
    {
        if (displayedInstallationOwner is not { } owner || State.Busy) return;
        await State.ResumeDownloadOwnerAsync(owner);
    }
    private void InstallationDetails_Click(object sender, RoutedEventArgs e) => Shell.OpenDownloads(1, displayedInstallationOwner);
    private void OnSummary(string text)
    {
        var reviewedHere = previewing && State.InstallPreviewReady;
        var requestVersion = previewRequestVersion;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!active) return;
            InstallSummary.Text = text;
            previewConfirmed = reviewedHere && previewVersion == requestVersion;
            ApplyInstallEligibility();
        });
    }

    private void OnComfyUpdate() => DispatcherQueue.TryEnqueue(() => { if (active) UpdateComfyMaintenance(); });
    private void UpdateComfyMaintenance()
    {
        State.RestoreComfyUpdatePlan();
        ComfyVersionStatus.Text = State.ComfyUpdateStatus;
        CurrentEnvironmentText.Text = "ComfyUI：" + State.Config.ComfyRoot + "\nPython：" + State.Config.Python;
        OptionalDependencyStatusText.Text = State.ComfyOptionalDependencyStatus;
        ComfyMaintenancePanel.IsEnabled = !State.Busy && !State.Config.InstallPending && State.Config.ComfyRoot.Length > 0;
        UpdateComfyButton.IsEnabled = State.ComfyHasUpdatePlan;
        UpdateComfyButton.Content = State.ComfyUpdateReady ? "备份并更新 ComfyUI" : "固定更新依赖";
        RollbackComfyButton.IsEnabled = State.ComfyCanRollback;
    }
    private async void CheckComfy_Click(object sender, RoutedEventArgs e) { SyncConfig(); await State.CheckComfyUpdateAsync(); }
    private async void UpdateComfy_Click(object sender, RoutedEventArgs e) { SyncConfig(); if (State.ComfyUpdateReady) await State.ApplyComfyUpdateAsync(); else await State.PrepareComfyUpdateAsync(); }
    private async void RollbackComfy_Click(object sender, RoutedEventArgs e) { SyncConfig(); await State.RollbackComfyUpdateAsync(); }
    private async void RepairComfy_Click(object sender, RoutedEventArgs e) { SyncConfig(); await State.RepairComfyDependenciesAsync(); }
    private void OnNodeUpdate() => DispatcherQueue.TryEnqueue(() => { if (active) UpdateNodeMaintenance(); });
    private void UpdateNodeMaintenance()
    {
        State.RestoreNodeUpdatePlan();
        NodeVersionStatus.Text = State.NodeUpdateStatus;
        NodeUpdateList.ItemsSource = State.NodeUpdateRows;
        NodeMaintenancePanel.IsEnabled = !State.Busy && !State.Config.InstallPending && State.Config.ComfyRoot.Length > 0;
        UpdateNodesButton.IsEnabled = State.NodesHaveUpdatePlan;
        UpdateNodesButton.Content = State.NodesUpdateReady ? "备份并更新节点" : "固定节点依赖";
        RollbackNodesButton.IsEnabled = State.NodesCanRollback;
    }
    private async void CheckNodes_Click(object sender, RoutedEventArgs e) { SyncConfig(); await State.CheckNodeUpdatesAsync(); }
    private async void UpdateNodes_Click(object sender, RoutedEventArgs e) { SyncConfig(); await State.ApplyNodeUpdatesAsync(); }
    private async void RollbackNodes_Click(object sender, RoutedEventArgs e) { SyncConfig(); await State.RollbackNodeUpdatesAsync(); }
    private void OpenSetup_Click(object sender, RoutedEventArgs e) => Shell.OpenSetup();
    private async void SaveSources_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            State.SaveDownloadSettings((SourceBox.SelectedItem as SourceProfile)?.Id ?? "official",
                GitMirrorBox.Text, HfMirrorBox.Text, PipBox.Text,
                (NetworkModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "system", ProxyBox.Text);
        }
        catch (Exception ex) { await ShowError(ex); }
    }

    private void LoadFields()
    {
        loading = true;
        try
        {
            var c = State.Config;
            var draft = State.PendingEnvironmentFields;
            var fresh = draft?.Fresh ?? (c.ComfyRoot.Length == 0 || State.FreshInstallRequested);
            RootBox.Text = draft?.ComfyRoot ?? (fresh ? c.InstallRoot : c.ComfyRoot);
            PythonBox.Text = draft?.Python ?? c.Python;
            pythonExplicit = draft?.PythonExplicit ?? c.PythonExplicit;
            SourceBox.SelectedItem = Program.Sources.Profiles.FirstOrDefault(s => s.Id == (draft?.SourceId ?? c.SourceId)) ?? Program.Sources.Profiles[0];
            GitMirrorBox.Text = draft?.GitMirror ?? c.GitMirror;
            HfMirrorBox.Text = draft?.HfMirror ?? c.ModelMirror;
            PipBox.Text = draft?.PipIndex ?? c.PipIndex;
            NetworkModeBox.SelectedIndex = c.NetworkMode switch { "direct" => 1, "custom" => 2, _ => 0 };
            ProxyBox.Text = c.ProxyAddress;
            CustomRepoBox.Text = draft?.CustomRepository ?? c.CustomRepository;
            SystemInfo.Text = State.SystemInfoText;
            ImportMode.IsChecked = !fresh;
            FreshMode.IsChecked = fresh;
            InstallSummary.Text = State.InstallSummaryText;
            ConfigurationPanel.IsEnabled = !State.Busy;
            OpenSetupButton.IsEnabled = !State.Busy;
            ApplyMode();
            ApplyInstallEligibility();
            UpdateComfyMaintenance();
            UpdateNodeMaintenance();
        }
        finally { loading = false; }
    }

    // This is a draft only. LauncherState.Run atomically validates and applies it
    // after taking the task gate, so changing pages cannot mutate an active install.
    public void SyncConfig()
    {
        if (loading || State.Busy) return;
        State.PendingEnvironmentFields = new(
            RootBox.Text, PythonBox.Text,
            (SourceBox.SelectedItem as SourceProfile)?.Id ?? "official",
            GitMirrorBox.Text, HfMirrorBox.Text, PipBox.Text, CustomRepoBox.Text,
            FreshMode.IsChecked == true, pythonExplicit);
    }

    private void ApplyMode()
    {
        var fresh = FreshMode.IsChecked == true;
        RootLabel.Text = fresh ? "02  选择安装位置" : "02  选择 ComfyUI 目录";
        RootBox.PlaceholderText = fresh ? @"例如 D:\AnimaStudio" : @"例如 D:\ComfyUI";
        RootHelp.Text = fresh
            ? "在所选文件夹内分别创建 ComfyUI 和 runtime。建议选择空间充足的位置。"
            : "选择包含 main.py 的 ComfyUI 目录，或便携版的上级目录。";
        PythonSection.Visibility = fresh ? Visibility.Collapsed : Visibility.Visible;
        ImportCheckButton.Visibility = fresh ? Visibility.Collapsed : Visibility.Visible;
        InstallButton.Content = fresh ? "一键安装并准备模型" : "补齐环境与缺失节点";
        InstallHelp.Text = fresh
            ? "自动准备 Python、Git、PyTorch、ComfyUI、所需节点和 Anima 基础模型。完成后点击「启动并打开工作台」。"
            : "保留现有节点版本与模型文件，只补齐缺失组件。已有环境可先点击「检查 / 导入目录」。";
        ProfileText.Text = fresh ? "Anima · 底模 / 文本编码器 / VAE"
            : Program.ModelProfiles.Get(State.Config.SelectedProfile).Label + " · 复用已有模型";
    }

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        ApplyMode();
        InvalidateSummary();
        SyncConfig();
    }

    private void Root_Changed(object sender, TextChangedEventArgs e)
    {
        if (loading) return;
        if (!pythonExplicit)
        {
            loading = true;
            PythonBox.Text = "";
            loading = false;
        }
        InvalidateSummary();
        SyncConfig();
    }

    private void Python_Changed(object sender, TextChangedEventArgs e)
    {
        if (loading) return;
        pythonExplicit = !string.IsNullOrWhiteSpace(PythonBox.Text);
        InvalidateSummary();
        SyncConfig();
    }

    private void Input_Changed(object sender, TextChangedEventArgs e)
    {
        if (loading) return;
        InvalidateSummary();
        SyncConfig();
    }

    private void InvalidateSummary()
    {
        previewVersion++;
        previewConfirmed = false;
        InstallSummary.Text = "安装选项已更新。点击「检查安装内容」查看当前下载与磁盘空间。";
        ApplyInstallEligibility();
    }

    private void ApplyInstallEligibility()
    {
        InstallButton.IsEnabled = previewConfirmed && !State.Busy;
        InstallGateNote.Text = previewConfirmed ? "已检查当前安装选项，可以开始安装。" : "请先检查上方安装内容，再开始安装。";
    }

    private void Source_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loading) return;
        if (SourceBox.SelectedItem is SourceProfile p && p.Id != "custom")
        {
            loading = true;
            GitMirrorBox.Text = p.GitTemplate;
            HfMirrorBox.Text = p.HuggingFaceBase;
            PipBox.Text = p.PipIndex;
            loading = false;
        }
        InvalidateSummary();
        SyncConfig();
    }

    private async void PickRoot_Click(object sender, RoutedEventArgs e)
    {
        if (State.Busy) return;
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, Win32Interop.GetWindowFromWindowId(Shell.AppWindow.Id));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null && !State.Busy) RootBox.Text = folder.Path;
        }
        catch (Exception ex) { await ShowError(ex); }
    }

    private async void PickPython_Click(object sender, RoutedEventArgs e)
    {
        if (State.Busy) return;
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add(".exe");
            InitializeWithWindow.Initialize(picker, Win32Interop.GetWindowFromWindowId(Shell.AppWindow.Id));
            var file = await picker.PickSingleFileAsync();
            if (file is not null && !State.Busy)
            {
                pythonExplicit = true;
                PythonBox.Text = file.Path;
                SyncConfig();
            }
        }
        catch (Exception ex) { await ShowError(ex); }
    }

    private async Task RunSetup(Func<CancellationToken, Task> action)
    {
        if (State.Busy) return;
        SyncConfig();
        await State.Run(action);
    }
    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (State.Busy) return;
        previewConfirmed = false;
        previewRequestVersion = previewVersion;
        previewing = true;
        try { await RunSetup(State.PreviewInstallAsync); }
        finally { previewing = false; ApplyInstallEligibility(); }
    }
    private async void Check_Click(object sender, RoutedEventArgs e) => await RunSetup(State.CheckImportAsync);
    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (!previewConfirmed || State.Busy) return;
        previewConfirmed = false;
        ApplyInstallEligibility();
        await RunSetup(State.InstallAsync);
    }

    private async Task ShowError(Exception ex)
    {
        State.AppendLog(ex.Message);
        State.SetStatus(ex.Message);
        if (State.Root is { } root) await Dialogs.Error(root, ex.Message);
    }
}
