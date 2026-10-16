using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Anima.Launcher.Pages;

public sealed partial class SettingsPage : LauncherPage
{
    private bool loading = true;
    private bool active;
    public SettingsPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        active = true;
        LoadFields();
        State.FieldsReloaded += OnReloaded;
        State.BusyChanged += OnBusy;
    }
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (!State.Busy) SyncConfig();
        active = false;
        State.FieldsReloaded -= OnReloaded;
        State.BusyChanged -= OnBusy;
        base.OnNavigatedFrom(e);
    }

    private void OnReloaded() => DispatcherQueue.TryEnqueue(() => { if (active) LoadFields(); });
    private void OnBusy(bool busy) => DispatcherQueue.TryEnqueue(() => SettingsPanel.IsEnabled = !busy);

    private void LoadFields()
    {
        loading = true;
        try
        {
            var c = State.Config;
            var draft = State.PendingSettingsFields;
            ComfyPortBox.Value = Math.Clamp(draft?.ComfyPort ?? c.ComfyPort, 1024, 65535);
            WebPortBox.Value = Math.Clamp(draft?.WebPort ?? c.WebPort, 1024, 65535);
            var theme = draft?.Theme ?? c.Theme;
            ThemeBox.SelectedIndex = theme == "dark" ? 2 : theme == "light" ? 1 : 0;
            StartOnLaunchBox.IsOn = draft?.StartOnLaunch ?? c.StartOnLaunch;
            if (CredHostBox.SelectedIndex < 0) CredHostBox.SelectedIndex = 0;
            var current = Path.GetFileName(c.AppRoot.TrimEnd(Path.DirectorySeparatorChar));
            var bundled = Path.GetFileName(Program.AppRoot.TrimEnd(Path.DirectorySeparatorChar));
            VersionText.Text = State.DevAppRoot
                ? $"当前：{c.AppRoot}（源码目录模式）；内嵌：{bundled}"
                : !string.IsNullOrWhiteSpace(c.PinnedAppRoot) ? $"当前版本：{current}（已固定回退版本）；主动应用新版才更新。"
                : current == bundled ? $"当前版本：{current}（与内嵌一致）"
                : $"当前版本：{current}；内嵌版本：{bundled}（下次启动服务时更新）";
            SettingsPanel.IsEnabled = !State.Busy;
        }
        finally { loading = false; }
    }

    public void SyncConfig()
    {
        if (loading || State.Busy) return;
        var c = State.Config;
        var comfyPort = double.IsNaN(ComfyPortBox.Value) ? c.ComfyPort : (int)Math.Clamp(ComfyPortBox.Value, 1024, 65535);
        var webPort = double.IsNaN(WebPortBox.Value) ? c.WebPort : (int)Math.Clamp(WebPortBox.Value, 1024, 65535);
        State.PendingSettingsFields = new(comfyPort, webPort,
            ThemeBox.SelectedIndex == 2 ? "dark" : ThemeBox.SelectedIndex == 1 ? "light" : "system",
            StartOnLaunchBox.IsOn);
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loading || State.Busy) return;
        SyncConfig();
        Shell.ApplyTheme(State.PendingSettingsFields?.Theme);
    }

    private void Port_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args) => SyncConfig();
    private void Startup_Changed(object sender, RoutedEventArgs e) => SyncConfig();

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (State.Busy) return;
        SyncConfig();
        await State.Run(_ =>
        {
            State.Save();
            Shell.ApplyTheme();
            State.SetStatus("设置已保存。");
            return Task.CompletedTask;
        });
    }

    private void CredSave_Click(object sender, RoutedEventArgs e)
    {
        if (State.Busy) return;
        var host = ((CredHostBox.SelectedItem as ComboBoxItem)?.Content as string) ?? "civitai.com";
        try
        {
            Credentials.Save(host, CredTokenBox.Password.Trim());
            CredTokenBox.Password = "";
            State.SetStatus("凭据已保存到 Windows 凭据管理器。");
        }
        catch (Exception ex) { State.AppendLog(ex.Message); State.SetStatus(ex.Message); }
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => Program.Open(Path.Combine(Program.StateDir, "logs"));

    private void Shortcut_Click(object sender, RoutedEventArgs e)
    {
        if (State.Busy) return;
        try { State.CreateDesktopShortcut(); }
        catch (Exception ex) { State.AppendLog(ex.Message); State.SetStatus(ex.Message); }
    }

    private async Task RunVersionTask(Func<CancellationToken, Task> action)
    {
        if (State.Busy) return;
        SyncConfig();
        await State.Run(action);
    }
    private async void ApplyVersion_Click(object sender, RoutedEventArgs e) => await RunVersionTask(State.ApplyBundledVersionAsync);
    private async void Rollback_Click(object sender, RoutedEventArgs e) => await RunVersionTask(State.RollbackVersionAsync);
}
