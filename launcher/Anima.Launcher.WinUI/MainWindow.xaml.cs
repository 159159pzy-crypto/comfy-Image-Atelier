using H.NotifyIcon;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Drawing;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Brush = Microsoft.UI.Xaml.Media.Brush;

namespace Anima.Launcher;

public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    internal LauncherState State { get; }

    private readonly bool forceShow;
    private TaskbarIcon? tray;
    private volatile bool reallyClose;
    private bool minimizeNotified;
    private int selectedPage;
    private bool downloadCancelable;
    private string statusBrushKey = "StudioSecondaryBrush";
    private readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromHours(1) };

    public MainWindow(App app, bool forceShow)
    {
        this.forceShow = forceShow;
        InitializeComponent();
        StudioTheme.ScopeResources(RootGrid);
        Title = "Anima Random Studio · 安装与启动";
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min((int)(1160 * scale), Math.Max(640, area.Width - (int)(32 * scale)));
        var height = Math.Min((int)(820 * scale), Math.Max(480, area.Height - (int)(32 * scale)));
        AppWindow.Resize(new SizeInt32(width, height));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = Math.Min((int)(760 * scale), width);
            presenter.PreferredMinimumHeight = Math.Min((int)(600 * scale), height);
        }
        var size = AppWindow.Size;
        AppWindow.Move(new PointInt32(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2));
        try { AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "app.ico")); } catch { }

        State = new LauncherState(() => Content?.XamlRoot, ShowWindow)
        {
            ForceShow = forceShow,
            HideWindow = () => { try { if (tray is not null) AppWindow.Hide(); } catch { } },
        };
        app.State = State; // Pages resolve state via App.Shared during navigation.
        WireState();
        State.SoftwareUpdateExitRequested += () => DispatcherQueue.TryEnqueue(() => Exit(false));
        Program.UpdateExitRequested += OnSoftwareUpdateExit;
        updateTimer.Tick += async (_, _) => await State.CheckSoftwareUpdatesAsync(false);

        GoToPage(State.SetupActive ? 5 : 0);
        RootGrid.ActualThemeChanged += (_, _) => RefreshThemeChrome();
        RootGrid.SizeChanged += (_, e) => BrandCopy.Visibility = e.NewSize.Width < 720 ? Visibility.Collapsed : Visibility.Visible;

        CreateTray();
        StartShowSignalListener();
        AppWindow.Closing += OnClosing;
        Activated += OnActivated;
        ApplyTheme();
        Program.Log("原生窗口就绪");
    }

    private void WireState()
    {
        State.StatusChanged += text => DispatcherQueue.TryEnqueue(() =>
        {
            StatusText.Text = text;
            statusBrushKey = "StudioSecondaryBrush";
            StatusText.Foreground = ThemeBrush(statusBrushKey);
        });
        State.ProgressChanged += value => DispatcherQueue.TryEnqueue(() =>
        {
            TaskProgress.IsIndeterminate = value < 0;
            if (value >= 0) TaskProgress.Value = value;
        });
        State.BusyChanged += busy => DispatcherQueue.TryEnqueue(() =>
        {
            if (busy) { TaskProgress.Value = 0; TaskProgress.IsIndeterminate = true; }
            else { TaskProgress.IsIndeterminate = false; downloadCancelable = false; }
            UpdateTaskState();
        });
        State.DownloadCancelable += on => DispatcherQueue.TryEnqueue(() => { downloadCancelable = on; UpdateTaskState(); });
        State.TaskStateChanged += () => DispatcherQueue.TryEnqueue(UpdateTaskState);
        State.TaskProgressChanged += text => DispatcherQueue.TryEnqueue(() =>
        {
            ProgressDetail.Text = text;
            ProgressDetail.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
        });
        State.Scanned += scan => DispatcherQueue.TryEnqueue(() => State.RebuildRows(scan));
        State.NavigateRequested += index => DispatcherQueue.TryEnqueue(() => GoToPage(index));
        State.TaskFailed += ex => DispatcherQueue.TryEnqueue(async () =>
        {
            State.AppendLog(ex.Message);
            statusBrushKey = "StudioDangerBrush";
            StatusText.Foreground = ThemeBrush(statusBrushKey);
            StatusText.Text = ex.Message;
            ShowWindow();
            if (!State.SetupActive && Content?.XamlRoot is { } root) await Dialogs.Error(root, ex.Message);
        });
        State.FieldsReloaded += () => DispatcherQueue.TryEnqueue(() => ApplyTheme());
        State.SetupChanged += () => DispatcherQueue.TryEnqueue(UpdateSetupChrome);
    }

    // --- Navigation ---

    private void UpdateTaskState()
    {
        PauseButton.Visibility = State.Busy ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = State.Busy && downloadCancelable ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.Visibility = !State.Busy && State.CanRetry ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out var index)) GoToPage(index);
    }

    internal void GoToPage(int index)
    {
        if (index == 1 && Anima.Launcher.Core.SetupRouting.OpenWizardFromEnvironmentPage(State.Config, State.SetupActive))
        {
            OpenSetup();
            return;
        }
        // Tray and background service actions cannot abandon a running wizard.
        if (State.SetupActive && index != 3) index = 5;
        selectedPage = Math.Clamp(index, 0, 5);
        var page = selectedPage switch
        {
            1 => typeof(Pages.EnvironmentPage),
            2 => typeof(Pages.ModelsPage),
            3 => typeof(Pages.LogsPage),
            4 => typeof(Pages.SettingsPage),
            5 => typeof(Pages.SetupPage),
            _ => typeof(Pages.ServicesPage),
        };
        if (ContentFrame.CurrentSourcePageType != page) ContentFrame.Navigate(page);
        UpdateNavigation();
        UpdateSetupChrome();
    }

    private void UpdateSetupChrome()
    {
        NormalNavigation.Visibility = State.SetupActive ? Visibility.Collapsed : Visibility.Visible;
        SetupReturnButton.Visibility = State.SetupActive && selectedPage != 5 ? Visibility.Visible : Visibility.Collapsed;
        TaskFooter.Visibility = State.SetupActive ? Visibility.Collapsed : Visibility.Visible;
    }

    internal void ShowSetupLogs() => GoToPage(3);
    internal void OpenSetup()
    {
        if (State.Busy && !State.SetupActive) { State.SetStatus("请等待当前任务完成，再打开安装向导。"); return; }
        try
        {
            if (!State.SetupActive)
            {
                ReadPagesIntoConfig();
                State.ApplyPendingFields();
                State.BeginSetup();
            }
            if (State.SetupActive) GoToPage(5);
        }
        catch (Exception ex) { State.AppendLog(ex.Message); State.SetStatus(ex.Message); }
    }
    private void SetupReturn_Click(object sender, RoutedEventArgs e) => GoToPage(5);

    internal void CancelSetup()
    {
        if (State.Busy) return;
        State.SuspendSetup();
        GoToPage(0);
    }

    internal Brush ThemeBrush(string key)
    {
        var theme = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast ? "HighContrast"
            : RootGrid.ActualTheme == ElementTheme.Light ? "Light" : "Default";
        var dictionary = (ResourceDictionary)Application.Current.Resources.ThemeDictionaries[theme];
        return (Brush)dictionary[key];
    }

    private void UpdateNavigation()
    {
        var buttons = new[] { HomeNav, EnvironmentNav, ModelsNav, LogsNav, SettingsNav };
        for (var i = 0; i < buttons.Length; i++)
        {
            buttons[i].Background = i == selectedPage ? ThemeBrush("StudioSurfaceBrush") : new SolidColorBrush(Colors.Transparent);
            buttons[i].Foreground = ThemeBrush(i == selectedPage ? "StudioTextBrush" : "StudioSecondaryBrush");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(buttons[i], i == selectedPage ? "当前页面" : "");
        }
    }

    // --- Lifecycle ---

    private bool firstActivate = true;
    private async void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (!firstActivate) return;
        firstActivate = false;
        try
        {
            if (Program.UpdateContext is not null)
            {
                RootInteraction.IsEnabled = false;
                await State.CompleteSoftwareUpdateStartupAsync();
                RootInteraction.IsEnabled = true;
                // The coordinator controls service startup and preserves prior state.
                // Do not run normal auto-start again after either commit or recovery.
                if (reallyClose || State.SoftwareUpdateExitPending) return;
            }
            _ = State.InitializeSoftwareUpdatesAsync();
            updateTimer.Start();
            var info = await State.DetectSystem();
            State.SystemInfoText = info;
            State.Reloaded();
            await State.RefreshSetupCompletionAsync();
            if (Program.UpdateContext is not null) return;
            if (!State.SetupActive) State.OfferUpgradeIfNeeded();
            // Opening the launcher is read-only by default. Starting services is an
            // explicit primary action unless the user opted in to auto-start.
            if (!State.SetupActive && State.Config.SetupComplete && State.Config.StartOnLaunch && !State.Config.InstallPending && !State.FreshInstallRequested && !forceShow)
                await State.Run(async ct =>
                {
                    await State.StartServicesAsync(ct);
                });
        }
        catch (Exception ex)
        {
            State.AppendLog(ex.ToString());
            statusBrushKey = "StudioDangerBrush";
            StatusText.Foreground = ThemeBrush(statusBrushKey);
            StatusText.Text = ex.Message;
        }
    }

    private void OnSoftwareUpdateExit() => DispatcherQueue.TryEnqueue(State.RequestSoftwareUpdateExit);

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (reallyClose) return;
        if (tray is null)
        {
            // No tray icon means a hidden window would be unreachable. While a task
            // runs, refuse to close so the user can pause it; otherwise allow the
            // real exit — owned services keep running like "退出（保留服务）".
            if (State.Busy)
            {
                args.Cancel = true;
                StatusText.Text = "托盘不可用且任务进行中；请先暂停任务再退出。";
                return;
            }
            Program.Log("托盘不可用，关闭窗口将直接退出（服务保留）。");
            return;
        }
        args.Cancel = true;
        try { AppWindow.Hide(); } catch { }
        if (minimizeNotified) return;
        minimizeNotified = true;
        try { tray?.ShowNotification("Anima Random Studio", "已最小化到托盘，任务继续运行。"); } catch { }
    }

    public void ShowWindow()
    {
        if (reallyClose) return;
        try { AppWindow.Show(); } catch { }
        try { AppWindow.MoveInZOrderAtTop(); } catch { }
        try { Activate(); } catch { }
    }

    private void Exit(bool stop)
    {
        if (State.Busy) { ShowWindow(); StatusText.Text = "请先暂停当前任务再退出。"; return; }
        reallyClose = true;
        try
        {
            updateTimer.Stop();
            Program.UpdateExitRequested -= OnSoftwareUpdateExit;
            if (stop) State.Services.StopOwned();
            try { tray?.Dispose(); } catch { }
            tray = null;
            State.Dispose();
            Close();
        }
        catch (Exception ex)
        {
            Program.Log("退出清理失败：" + ex);
            // The user asked to quit; never leave a silent process behind.
            try { Environment.Exit(0); } catch { }
        }
    }

    // --- Theme ---

    internal void ApplyTheme(string? overrideTheme = null)
    {
        if (Content is not FrameworkElement element) return;
        element.RequestedTheme = (overrideTheme ?? State.PendingSettingsFields?.Theme ?? State.Config.Theme) switch
        {
            "dark" => ElementTheme.Dark,
            "light" => ElementTheme.Light,
            _ => ElementTheme.Default,
        };
        RefreshThemeChrome();
    }

    private void RefreshThemeChrome()
    {
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = RootGrid.ActualTheme == ElementTheme.Dark ? Colors.White : Colors.Black;
        StatusText.Foreground = ThemeBrush(statusBrushKey);
        UpdateNavigation();
    }

    // --- Tray ---

    private void CreateTray()
    {
        try
        {
            var menu = new MenuFlyout();
            void Item(string text, Action action)
            {
                // ContextMenuMode defaults to PopupMenu: the items are cloned into a
                // native Win32 menu that only executes Command — the Click event is
                // never forwarded, so items must use Command rather than Click.
                var item = new MenuFlyoutItem { Text = text };
                item.Command = new RelayCommand(action);
                menu.Items.Add(item);
            }
            Item("打开工作台", () => Program.Open(State.Config.WebUrl));
            Item("打开 ComfyUI", () => Program.Open(State.Config.ComfyUrl));
            Item("启动服务", () => { if (!State.Busy) { ReadPagesIntoConfig(); _ = State.Run(State.StartServicesAsync); } });
            Item("停止本程序服务", () => _ = State.StopServicesAsync());
            Item("重新检查模型和节点", () => { if (!State.Busy) { ShowWindow(); ReadPagesIntoConfig(); GoToPage(2); _ = State.Run(ct => State.Scan(ct)); } });
            Item("打开环境 / 安装页面", () => { ShowWindow(); GoToPage(1); });
            Item("设置", () => { ShowWindow(); GoToPage(4); });
            Item("打开日志目录", () => Program.Open(Path.Combine(Program.StateDir, "logs")));
            menu.Items.Add(new MenuFlyoutSeparator());
            Item("退出（保留服务）", () => Exit(false));
            Item("停止服务并退出", () => Exit(true));

            tray = new TaskbarIcon
            {
                ToolTipText = "Anima Random Studio",
                ContextFlyout = menu,
                LeftClickCommand = new RelayCommand(ShowWindow),
            };
            // Environment.ProcessPath points to dotnet.exe under `dotnet run`; prefer the app file next to the exe.
            var iconPath = Environment.ProcessPath!;
            var sibling = Path.Combine(AppContext.BaseDirectory, Path.GetFileNameWithoutExtension(iconPath) + ".exe");
            if (Path.GetFileName(iconPath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
                iconPath = Directory.GetFiles(AppContext.BaseDirectory, "*.exe").FirstOrDefault() ?? iconPath;
            else if (File.Exists(sibling)) iconPath = sibling;
            try { tray.Icon = Icon.ExtractAssociatedIcon(iconPath) ?? SystemIcons.Application; } catch { }
            // The default ForceCreate(true) puts the whole process into Windows
            // Efficiency Mode, which throttles the installs/downloads this app runs.
            tray.ForceCreate(enablesEfficiencyMode: false);
        }
        catch (Exception ex)
        {
            Program.Log("托盘图标创建失败：" + ex.Message);
            try { tray?.Dispose(); } catch { }
            tray = null;
        }
    }

    /// <summary>Surface the window when a second instance signals the named event.</summary>
    private void StartShowSignalListener()
    {
        var signal = Program.ShowSignal;
        if (signal is null) return;
        new Thread(() =>
        {
            try
            {
                while (signal.WaitOne())
                {
                    if (reallyClose) return;
                    DispatcherQueue.TryEnqueue(() => { if (!reallyClose) ShowWindow(); });
                }
            }
            catch (ObjectDisposedException) { }
            catch (AbandonedMutexException) { }
        })
        { IsBackground = true, Name = "ShowSignal" }.Start();
    }

    // --- Footer commands ---

    private async void Retry_Click(object sender, RoutedEventArgs e) => await State.ResumeTaskAsync();

    private async void Stop_Click(object sender, RoutedEventArgs e) => await State.StopServicesAsync();

    private void Pause_Click(object sender, RoutedEventArgs e) => State.PauseTask();

    private void Cancel_Click(object sender, RoutedEventArgs e) => State.CancelTaskAndDownloads();

    private void OpenWebUI_Click(object sender, RoutedEventArgs e) => Program.Open(State.Config.WebUrl);
    private void OpenComfy_Click(object sender, RoutedEventArgs e) => Program.Open(State.Config.ComfyUrl);

    private async void Rescan_Click(object sender, RoutedEventArgs e)
    {
        ShowWindow();
        if (State.Busy) return;
        ReadPagesIntoConfig();
        GoToPage(2);
        await State.Run(ct => State.Scan(ct));
    }

    private void GoEnvironment_Click(object sender, RoutedEventArgs e) => GoToPage(1);
    private void GoSettings_Click(object sender, RoutedEventArgs e) => GoToPage(4);
    private void OpenLogs_Click(object sender, RoutedEventArgs e) => Program.Open(Path.Combine(Program.StateDir, "logs"));
    private void ExitKeep_Click(object sender, RoutedEventArgs e) => Exit(false);
    private void ExitStop_Click(object sender, RoutedEventArgs e) => Exit(true);

    /// <summary>Push current page field values into config before running tasks.</summary>
    internal void SyncPagesIntoConfig() => ReadPagesIntoConfig();

    private void ReadPagesIntoConfig()
    {
        if (ContentFrame.Content is Pages.EnvironmentPage env) env.SyncConfig();
        if (ContentFrame.Content is Pages.SettingsPage settings) settings.SyncConfig();
    }
}
