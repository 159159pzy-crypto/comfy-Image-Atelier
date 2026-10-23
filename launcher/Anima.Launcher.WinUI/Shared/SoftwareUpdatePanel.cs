using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Anima.Launcher;

/// <summary>The same update workflow is used by portable and installed launchers.</summary>
public sealed class SoftwareUpdatePanel : UserControl
{
    private readonly TextBlock version = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly TextBlock notes = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly ToggleSwitch automatic = new() { OnContent = "自动检查已开启", OffContent = "仅手动检查" };
    private readonly Button check = new() { Content = "检查更新" };
    private readonly Button download = new() { Content = "下载更新" };
    private readonly Button install = new() { Content = "重启并更新" };
    private readonly Button pause = new() { Content = "暂停下载" };
    private readonly ProgressBar progress = new() { Minimum = 0, Maximum = 100, Height = 5 };
    private readonly Expander releaseNotes;
    private bool loading;
    private bool subscribed;
    private LauncherState State => App.Shared;

    public SoftwareUpdatePanel()
    {
        var resources = Application.Current.Resources;
        version.Style = (Style)resources["BodyStrongTextBlockStyle"];
        status.Style = (Style)resources["StudioBodyStyle"];
        notes.Style = (Style)resources["StudioBodyStyle"];
        install.Style = (Style)resources["StudioPrimaryButtonStyle"];
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock { Text = "软件更新", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        body.Children.Add(version);
        body.Children.Add(status);
        body.Children.Add(new Card
        {
            Header = "自动检查新版本",
            Description = "每 24 小时检查一次；下载和重启由你决定。",
            ActionContent = automatic,
        });
        var buttons = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, MaximumRowsOrColumns = 4, ItemWidth = 145, ItemHeight = 48 };
        foreach (var button in new[] { check, download, pause, install })
        {
            button.Margin = new Thickness(0, 0, 8, 8);
            AutomationProperties.SetName(button, button.Content.ToString());
            buttons.Children.Add(button);
        }
        body.Children.Add(buttons);
        body.Children.Add(progress);
        releaseNotes = new Expander
        {
            Header = "更新说明",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new ScrollViewer { MaxHeight = 240, Content = notes, HorizontalScrollMode = ScrollMode.Disabled },
        };
        body.Children.Add(releaseNotes);
        Content = new Border { Style = (Style)resources["StudioPanelStyle"], Padding = new Thickness(22), Child = body };
        automatic.Toggled += AutomaticChanged;
        check.Click += async (_, _) => await State.CheckSoftwareUpdatesAsync(true);
        download.Click += async (_, _) => await State.DownloadSoftwareUpdateAsync();
        pause.Click += (_, _) => State.PauseTask();
        install.Click += async (_, _) =>
        {
            App.Shell.SyncPagesIntoConfig();
            await State.RestartForSoftwareUpdateAsync();
        };
        Loaded += (_, _) =>
        {
            if (subscribed) return;
            subscribed = true;
            State.SoftwareUpdateChanged += RefreshRequested;
            State.BusyChanged += BusyChanged;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            if (!subscribed) return;
            subscribed = false;
            State.SoftwareUpdateChanged -= RefreshRequested;
            State.BusyChanged -= BusyChanged;
        };
        AutomationProperties.SetName(this, "软件更新");
    }

    private void BusyChanged(bool _) => RefreshRequested();
    private void RefreshRequested() => DispatcherQueue.TryEnqueue(() => { if (subscribed) Refresh(); });

    private void Refresh()
    {
        loading = true;
        try
        {
            version.Text = State.SoftwareVersionText;
            status.Text = State.SoftwareUpdateStatus;
            automatic.IsOn = State.Config.AutoCheckUpdates;
            automatic.IsEnabled = !State.Busy;
            check.IsEnabled = !State.CheckingSoftwareUpdate && !State.Busy;
            check.Content = State.CheckingSoftwareUpdate ? "正在检查…" : "检查更新";
            download.Visibility = State.HasSoftwareRelease && !State.SoftwareUpdateReady ? Visibility.Visible : Visibility.Collapsed;
            download.IsEnabled = !State.Busy && !State.CheckingSoftwareUpdate;
            download.Content = State.SoftwareDownloadPaused ? "继续下载" : "下载更新";
            pause.Visibility = State.DownloadingSoftwareUpdate ? Visibility.Visible : Visibility.Collapsed;
            pause.IsEnabled = State.DownloadingSoftwareUpdate;
            install.Visibility = State.SoftwareUpdateReady ? Visibility.Visible : Visibility.Collapsed;
            install.IsEnabled = State.SoftwareUpdateReady && !State.Busy && !State.CheckingSoftwareUpdate;
            progress.Visibility = State.DownloadingSoftwareUpdate || State.SoftwareDownloadPaused ? Visibility.Visible : Visibility.Collapsed;
            progress.IsIndeterminate = State.DownloadingSoftwareUpdate && State.SoftwareUpdatePercent < 0;
            progress.Value = Math.Clamp(State.SoftwareUpdatePercent, 0, 100);
            notes.Text = State.SoftwareReleaseNotes;
            releaseNotes.Visibility = notes.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { loading = false; }
    }

    private void AutomaticChanged(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        State.SetAutomaticSoftwareUpdates(automatic.IsOn);
        Refresh();
    }
}
