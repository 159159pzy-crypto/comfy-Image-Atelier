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

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        active = true;
        State.Scanned += OnScanned;
        State.BusyChanged += OnBusy;
        OnBusy(State.Busy);
        UpdateDetails();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        active = false;
        State.Scanned -= OnScanned;
        State.BusyChanged -= OnBusy;
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
        ModelActions.IsEnabled = !busy;
        ModelList.IsEnabled = !busy;
        OpenLocationButton.IsEnabled = !busy && ModelList.SelectedItem is ModelRow;
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
            OpenLocationButton.IsEnabled = !State.Busy;
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
    private async void Rescan_Click(object sender, RoutedEventArgs e) => await RunModelTask(ct => State.Scan(ct));
    private async void HashScan_Click(object sender, RoutedEventArgs e) => await RunModelTask(ct => State.Scan(ct, true));
    private async void Download_Click(object sender, RoutedEventArgs e) => await RunModelTask(State.CaptureSelectedModelDownload());
    private async void TestImage_Click(object sender, RoutedEventArgs e) => await RunModelTask(State.TestGenerationAsync);

    private void SelectBase_Click(object sender, RoutedEventArgs e)
    {
        if (!State.Busy) State.SelectBaseGroup();
    }

    private async void Folder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string category } anchor)
            await State.OpenModelFolderAsync(category, anchor is MenuFlyoutItem ? MoreFoldersButton : anchor);
    }

    private async void OpenModelDir_Click(object sender, RoutedEventArgs e)
    {
        if (State.Busy || ModelList.SelectedItem is not ModelRow row) return;
        var modelId = row.Status.Model.Id;
        await RunModelTask(ct => State.OpenModelLocationAsync(modelId, ct));
    }

    private void OpenLicense_Click(object sender, RoutedEventArgs e)
    {
        if (ModelList.SelectedItem is not ModelRow row) return;
        var model = row.Status.Model;
        var url = model.SourceUrl.Length > 0 ? model.SourceUrl : model.LicenseUrl.Length > 0 ? model.LicenseUrl : model.Urls.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(url)) Program.Open(url);
    }
}
