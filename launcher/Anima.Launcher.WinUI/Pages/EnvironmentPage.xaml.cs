using Anima.Launcher.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
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
        previewConfirmed = false;
        previewVersion++;
        LoadFields();
        State.FieldsReloaded += OnReloaded;
        State.BusyChanged += OnBusy;
        State.InstallSummaryChanged += OnSummary;
        State.ComfyUpdateChanged += OnComfyUpdate;
        State.NodeUpdateChanged += OnNodeUpdate;
        UpdateComfyMaintenance();
        UpdateNodeMaintenance();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (!State.Busy && !State.SetupActive) SyncConfig();
        active = false;
        previewConfirmed = false;
        previewVersion++;
        State.FieldsReloaded -= OnReloaded;
        State.BusyChanged -= OnBusy;
        State.InstallSummaryChanged -= OnSummary;
        State.ComfyUpdateChanged -= OnComfyUpdate;
        State.NodeUpdateChanged -= OnNodeUpdate;
        base.OnNavigatedFrom(e);
    }

    private void OnReloaded() => DispatcherQueue.TryEnqueue(() => { if (active) LoadFields(); });
    private void OnBusy(bool busy) => DispatcherQueue.TryEnqueue(() =>
    {
        ConfigurationPanel.IsEnabled = !busy;
        OpenSetupButton.IsEnabled = !busy;
        UpdateComfyMaintenance();
        UpdateNodeMaintenance();
        ApplyInstallEligibility();
    });
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
        SyncConfig();
        await State.Run(_ => { State.Save(); State.SetStatus("下载设置已保存，下次下载和更新时使用。"); return Task.CompletedTask; });
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
