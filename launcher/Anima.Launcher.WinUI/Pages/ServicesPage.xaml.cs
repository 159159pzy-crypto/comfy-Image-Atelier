using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Anima.Launcher.Pages;

public sealed partial class ServicesPage : LauncherPage
{
    private readonly DispatcherTimer timer;
    private ServiceInfo comfy = new();
    private ServiceInfo studio = new();
    private bool probing;
    private bool active;
    private CancellationTokenSource? probe;
    private int visibilityRevision;

    public ServicesPage()
    {
        InitializeComponent();
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += async (_, _) => await Refresh();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        active = true;
        State.BusyChanged += OnBusy;
        State.FieldsReloaded += OnReloaded;
        State.SoftwareUpdateChanged += OnReloaded;
        State.PresentationVisibilityChanged += VisibilityChanged;
        Apply();
        await Refresh();
        if (active && State.PresentationVisible) timer.Start();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        active = false;
        timer.Stop();
        probe?.Cancel();
        State.BusyChanged -= OnBusy;
        State.FieldsReloaded -= OnReloaded;
        State.SoftwareUpdateChanged -= OnReloaded;
        State.PresentationVisibilityChanged -= VisibilityChanged;
        base.OnNavigatedFrom(e);
    }

    private void OnBusy(bool busy) => DispatcherQueue.TryEnqueue(() => { if (active) Apply(); });
    private void OnReloaded() => DispatcherQueue.TryEnqueue(() => { if (active) Apply(); });

    private void VisibilityChanged(bool visible)
    {
        visibilityRevision++;
        timer.Stop();
        probe?.Cancel();
        if (active && visible) { _ = Refresh(); timer.Start(); }
    }

    private async Task Refresh()
    {
        if (probing || !active || !State.PresentationVisible) return;
        probing = true;
        var revision = visibilityRevision;
        using var lifetime = new CancellationTokenSource();
        probe = lifetime;
        try
        {
            // These probes are independent; a slow backend should not delay the other.
            var comfyTask = State.ProbeComfyAsync(lifetime.Token);
            var studioTask = State.ProbeWebUIAsync(lifetime.Token);
            await Task.WhenAll(comfyTask, studioTask);
            comfy = await comfyTask;
            studio = await studioTask;
            if (studio.Running && comfy.Status == "needs_restart")
            {
                studio.Status = "needs_restart";
                studio.Detail = comfy.Detail;
            }
            if (active && State.PresentationVisible && !lifetime.IsCancellationRequested) Apply();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { State.AppendLog("服务状态检测：" + ex.Message); }
        finally
        {
            probe = null;
            probing = false;
            if (active && State.PresentationVisible && revision != visibilityRevision) _ = Refresh();
        }
    }

    private void Apply()
    {
        SoftwareUpdateNotice.Visibility = State.SoftwareUpdateNotice.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SoftwareUpdateNotice.Description = State.SoftwareUpdateNotice;
        var busy = State.Busy;
        var draft = State.PendingEnvironmentFields;
        var selectedRoot = draft?.ComfyRoot ?? State.Config.ComfyRoot;
        var fresh = draft?.Fresh ?? (State.FreshInstallRequested || State.Config.InstallPending);
        ComfyCard.Update(comfy, busy || fresh || State.Config.InstallPending);
        StudioCard.Update(studio, busy || fresh || State.Config.InstallPending);
        var configured = !string.IsNullOrWhiteSpace(selectedRoot) && !fresh;
        var ready = !fresh && draft is null && State.PendingSettingsFields is null && comfy.ArgumentsMatch && studio.Running && (studio.Ours || studio.Managed);
        WelcomeTitle.Text = ready ? "工作台已就绪。" : configured ? "准备好下一次创作。" : "创作，从这里开始。";
        WelcomeDescription.Text = ready
            ? "服务运行正常，打开工作台继续创作。"
            : configured ? "一键启动 ComfyUI 和 Studio，模型与创作记录会继续保留。"
            : "无需手动安装 Python 或 Git。选择安装位置，自动准备环境与 Anima 基础模型。";
        EnvironmentPath.Text = string.IsNullOrWhiteSpace(selectedRoot) ? "已有 ComfyUI？在安装环境中直接导入。" : selectedRoot;
        StartAllButton.Content = ready ? "打开创作工作台" : configured ? "启动并打开工作台" : "开始安装";
        StartAllButton.IsEnabled = !busy;
        ConfigureButton.Content = configured ? "管理安装环境" : "导入已有 ComfyUI";
        ConfigureButton.IsEnabled = !busy;
    }

    private void Welcome_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 760;
        Grid.SetRow(WelcomeActions, narrow ? 1 : 0);
        Grid.SetColumn(WelcomeActions, narrow ? 0 : 1);
        WelcomeActions.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
    }

    private void Folders_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 820;
        Grid.SetRow(FolderActions, narrow ? 1 : 0);
        Grid.SetColumn(FolderActions, narrow ? 0 : 1);
    }

    private void Services_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 880;
        Grid.SetColumnSpan(ComfyCard, narrow ? 2 : 1);
        Grid.SetColumnSpan(StudioCard, narrow ? 2 : 1);
        Grid.SetRow(StudioCard, narrow ? 1 : 0);
        Grid.SetColumn(StudioCard, narrow ? 0 : 1);
        ServicesGrid.RowSpacing = narrow ? 12 : 0;
        ServicesGrid.ColumnSpacing = narrow ? 0 : 14;
    }

    private async Task RunService(Func<CancellationToken, Task> action)
    {
        if (State.Busy) return;
        Shell.SyncPagesIntoConfig();
        await State.Run(action);
        await Refresh();
    }

    private async void ComfyStart_Click(object sender, RoutedEventArgs e) => await RunService(State.StartComfyAsync);
    private async void StudioStart_Click(object sender, RoutedEventArgs e) => await RunService(State.StartWebUIAsync);

    private async void ComfyStop_Click(object sender, RoutedEventArgs e)
    {
        await State.StopServiceAsync("ComfyUI", studio.Running);
        await Refresh();
    }
    private async void StudioStop_Click(object sender, RoutedEventArgs e)
    {
        await State.StopServiceAsync("WebUI", studio.Running);
        await Refresh();
    }
    private void ComfyOpen_Click(object sender, RoutedEventArgs e) => Program.Open(comfy.Url);
    private void StudioOpen_Click(object sender, RoutedEventArgs e) => Program.Open(studio.Url);

    private async void StartAll_Click(object sender, RoutedEventArgs e)
    {
        if (State.Busy) return;
        var draft = State.PendingEnvironmentFields;
        if (string.IsNullOrWhiteSpace(draft?.ComfyRoot ?? State.Config.ComfyRoot) || (draft?.Fresh ?? (State.FreshInstallRequested || State.Config.InstallPending))) { Shell.GoToPage(1); return; }
        // Always recheck live parameters on an explicit start: the periodic snapshot may be stale.
        await RunService(State.StartServicesAsync);
    }
    private void Configure_Click(object sender, RoutedEventArgs e)
    {
        Shell.GoToPage(1);
        if (string.IsNullOrWhiteSpace(State.Config.ComfyRoot)) State.SetSetupMode(false);
    }

    private void SoftwareUpdate_Click(object sender, RoutedEventArgs e) => Shell.GoToPage(4);

    private async void Folder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string category } anchor)
            await State.OpenModelFolderAsync(category, anchor is MenuFlyoutItem ? MoreFoldersButton : anchor);
    }
}
