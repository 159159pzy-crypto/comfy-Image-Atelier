using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Anima.Launcher.Core;

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
    private void OnBusy(bool busy) => DispatcherQueue.TryEnqueue(() => PreferencesPanel.IsEnabled = !busy);

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
            LoadArguments(draft?.ComfyArgumentsText ?? ComfyLaunchArguments.Format(c.ComfyArguments));
            if (CredHostBox.SelectedIndex < 0) CredHostBox.SelectedIndex = 0;
            var current = Path.GetFileName(c.AppRoot.TrimEnd(Path.DirectorySeparatorChar));
            var bundled = Path.GetFileName(Program.AppRoot.TrimEnd(Path.DirectorySeparatorChar));
            VersionText.Text = State.DevAppRoot
                ? $"当前：{c.AppRoot}（源码目录模式）；内嵌：{bundled}"
                : !string.IsNullOrWhiteSpace(c.PinnedAppRoot) ? $"当前版本：{current}（已固定回退版本）；主动应用新版才更新。"
                : current == bundled ? $"当前版本：{current}（与内嵌一致）"
                : $"当前版本：{current}；内嵌版本：{bundled}（下次启动服务时更新）";
            PreferencesPanel.IsEnabled = !State.Busy;
        }
        finally { loading = false; }
        UpdateArgumentsPreview();
    }

    public void SyncConfig()
    {
        if (loading || State.Busy) return;
        var c = State.Config;
        var comfyPort = double.IsNaN(ComfyPortBox.Value) ? c.ComfyPort : (int)Math.Clamp(ComfyPortBox.Value, 1024, 65535);
        var webPort = double.IsNaN(WebPortBox.Value) ? c.WebPort : (int)Math.Clamp(WebPortBox.Value, 1024, 65535);
        var draft = new SettingsFields(comfyPort, webPort,
            ThemeBox.SelectedIndex == 2 ? "dark" : ThemeBox.SelectedIndex == 1 ? "light" : "system",
            StartOnLaunchBox.IsOn, ReadArgumentsText());
        State.PendingSettingsFields = draft.Matches(c) ? null : draft;
        UpdateArgumentsPreview();
    }

    private void LoadArguments(string text)
    {
        var options = new ComfyLaunchOptions();
        var advanced = text;
        try
        {
            var arguments = ComfyLaunchArguments.Parse(text);
            ComfyLaunchArguments.Validate(arguments);
            var parsed = ComfyLaunchArguments.ReadOptions(arguments);
            options = parsed.Options;
            advanced = ComfyLaunchArguments.Format(parsed.Advanced);
        }
        catch (InvalidDataException) { /* Preserve invalid drafts verbatim so the user can correct them. */ }
        VramModeBox.SelectedIndex = Array.IndexOf(ComfyLaunchArguments.VramModes, options.VramMode);
        PrecisionBox.SelectedIndex = Array.IndexOf(ComfyLaunchArguments.Precisions, options.Precision);
        AttentionBox.SelectedIndex = Array.IndexOf(ComfyLaunchArguments.Attentions, options.Attention);
        ManagerBox.SelectedIndex = Array.IndexOf(ComfyLaunchArguments.Managers, options.Manager);
        ReserveVramBox.Text = options.ReserveVram?.ToString("G", System.Globalization.CultureInfo.InvariantCulture) ?? "";
        DisableSmartMemoryBox.IsOn = options.DisableSmartMemory;
        AdvancedArgumentsBox.Text = advanced;
    }

    private string ReadArgumentsText()
    {
        static string Selected(ComboBox box, string[] values) => values[Math.Clamp(box.SelectedIndex, 0, values.Length - 1)];
        var options = new ComfyLaunchOptions(Selected(VramModeBox, ComfyLaunchArguments.VramModes), null,
            DisableSmartMemoryBox.IsOn, Selected(PrecisionBox, ComfyLaunchArguments.Precisions),
            Selected(AttentionBox, ComfyLaunchArguments.Attentions), Selected(ManagerBox, ComfyLaunchArguments.Managers));
        var arguments = ComfyLaunchArguments.WithOptions(options, [], validate: false);
        if (!string.IsNullOrWhiteSpace(ReserveVramBox.Text)) { arguments.Add("--reserve-vram"); arguments.Add(ReserveVramBox.Text.Trim()); }
        return (ComfyLaunchArguments.Format(arguments) + " " + AdvancedArgumentsBox.Text).Trim();
    }

    private void UpdateArgumentsPreview()
    {
        if (loading) return;
        try
        {
            var preview = ArgumentTargetConfig();
            UpdateArgumentAvailability(preview.ComfyRoot);
            preview.ComfyArguments = ComfyLaunchArguments.Parse(ReadArgumentsText());
            preview.ComfyPort = double.IsNaN(ComfyPortBox.Value) ? preview.ComfyPort : (int)ComfyPortBox.Value;
            var args = ComfyLaunchArguments.Build(preview);
            ArgumentsPreviewBox.Text = ComfyLaunchArguments.Format(new[] { string.IsNullOrWhiteSpace(preview.Python) ? "python.exe" : preview.Python }.Concat(args));
            ArgumentsErrorText.Text = "";
            ArgumentsErrorText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or UnauthorizedAccessException)
        {
            ArgumentsPreviewBox.Text = "请先修正启动参数。";
            ArgumentsErrorText.Text = ex.Message;
            ArgumentsErrorText.Visibility = Visibility.Visible;
        }
    }

    private LauncherConfig ArgumentTargetConfig()
    {
        var preview = State.Config.Clone();
        if (State.PendingEnvironmentFields is { } draft)
        {
            var chosen = draft.ComfyRoot.Trim().Trim('"');
            var nextRoot = draft.Fresh && chosen.Length > 0 ? Path.Combine(chosen, "ComfyUI") : chosen;
            if (!draft.Fresh && chosen.Length > 0 && (File.Exists(Path.Combine(chosen, "main.py")) || File.Exists(Path.Combine(chosen, "ComfyUI", "main.py")))) nextRoot = Inventory.NormalizeRoot(chosen);
            if (draft.PythonExplicit) preview.Python = draft.Python;
            else if (!string.Equals(nextRoot, preview.ComfyRoot, StringComparison.OrdinalIgnoreCase)) preview.Python = "";
            preview.ComfyRoot = nextRoot;
        }
        return preview;
    }

    private void UpdateArgumentAvailability(string root)
    {
        var supported = ComfyLaunchArguments.SupportedOptions(root);
        foreach (var (box, values) in new[] { (VramModeBox, ComfyLaunchArguments.VramModes), (PrecisionBox, ComfyLaunchArguments.Precisions), (AttentionBox, ComfyLaunchArguments.Attentions), (ManagerBox, ComfyLaunchArguments.Managers) })
            for (var i = 0; i < values.Length; i++)
                ((ComboBoxItem)box.Items[i]).IsEnabled = values[i].Length == 0 || supported is null || supported.Contains(values[i]);
        // Keep an existing unsupported value editable so it can be cleared rather than silently discarded.
        ReserveVramBox.IsEnabled = supported is null || supported.Contains("--reserve-vram") || ReserveVramBox.Text.Length > 0;
        DisableSmartMemoryBox.IsEnabled = supported is null || supported.Contains("--disable-smart-memory") || DisableSmartMemoryBox.IsOn;
        ArgumentsSupportText.Text = supported is null
            ? "尚未读取到该环境的参数定义；先展示常用选项，安装完成后启动前会再次校验。"
            : "已按所选 ComfyUI 版本禁用不支持的常用选项。已有高级参数会保留。";
    }

    private void Arguments_SelectionChanged(object sender, SelectionChangedEventArgs e) => SyncConfig();
    private void Arguments_TextChanged(object sender, TextChangedEventArgs e) => SyncConfig();
    private void Arguments_Toggled(object sender, RoutedEventArgs e) => SyncConfig();

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
            // Saving defaults explicitly also means Manager "off" is an intentional choice.
            State.Config.LaunchArgumentsConfigured = true;
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
