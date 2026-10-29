using Anima.Launcher.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;

namespace Anima.Launcher.Pages;

public sealed partial class LogsPage : LauncherPage
{
    private long clearedThrough;
    private int flushQueued;
    private readonly DispatcherTimer flushTimer;
    private volatile bool active;
    private BoundedLogSnapshot rendered = new("", 0);

    public LogsPage()
    {
        InitializeComponent();
        flushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        flushTimer.Tick += (_, _) => { flushTimer.Stop(); Flush(); };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        active = true;
        clearedThrough = 0;
        State.LogAppended += OnLog;
        Render();
        State.PresentationVisibilityChanged += VisibilityChanged;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        active = false;
        State.LogAppended -= OnLog;
        State.PresentationVisibilityChanged -= VisibilityChanged;
        flushTimer.Stop();
        Interlocked.Exchange(ref flushQueued, 0);
        base.OnNavigatedFrom(e);
    }

    private void VisibilityChanged(bool shown)
    {
        flushTimer.Stop();
        Interlocked.Exchange(ref flushQueued, 0);
        if (!active || !shown) return;
        Render();
    }

    // The event is only a dirty signal. Rendering one authoritative bounded tail avoids
    // racing a snapshot against an independently appended pending queue.
    private void OnLog(string line)
    {
        if (!active || !State.PresentationVisible) return;
        if (Interlocked.Exchange(ref flushQueued, 1) != 0) return;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            if (active && State.PresentationVisible) flushTimer.Start();
            else Interlocked.Exchange(ref flushQueued, 0);
        })) Interlocked.Exchange(ref flushQueued, 0);
    }

    private void Flush()
    {
        Interlocked.Exchange(ref flushQueued, 0);
        if (!active || !State.PresentationVisible) return;
        Render();
    }

    private void Render()
    {
        var snapshot = State.LogLines.Capture(clearedThrough);
        if (snapshot == rendered) return;
        var selection = snapshot.RebaseSelection(rendered, LogBox.SelectionStart, LogBox.SelectionLength);
        var scroller = FindScroller(LogBox);
        var oldOffset = scroller?.VerticalOffset;
        LogBox.Text = snapshot.Text;
        LogBox.Select(selection.Start, selection.Length);
        rendered = snapshot;
        if (scroller is not null)
            DispatcherQueue.TryEnqueue(() => scroller.ChangeView(null, FollowTailBox.IsChecked == true ? scroller.ExtentHeight : oldOffset, null, true));
    }

    private static ScrollViewer? FindScroller(DependencyObject root)
    {
        if (root is ScrollViewer scroller) return scroller;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScroller(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    private void OpenDir_Click(object sender, RoutedEventArgs e) => Program.Open(Path.Combine(Program.StateDir, "logs"));

    private void Toolbar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 610;
        Grid.SetColumn(FollowTailBox, narrow ? 0 : 1);
        Grid.SetRow(FollowTailBox, narrow ? 1 : 0);
        Grid.SetColumnSpan(LogActions, narrow ? 2 : 1);
        LogActions.Orientation = e.NewSize.Width < 390 ? Orientation.Vertical : Orientation.Horizontal;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var data = new DataPackage();
            data.SetText(LogBox.Text);
            Clipboard.SetContent(data);
            State.SetStatus("当前日志已复制。");
        }
        catch (Exception ex) { State.SetStatus("无法复制日志：" + ex.Message); }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        Interlocked.Exchange(ref flushQueued, 0);
        clearedThrough = State.LogLines.Sequence;
        rendered = State.LogLines.Capture(clearedThrough);
        LogBox.Text = "";
    }
}
