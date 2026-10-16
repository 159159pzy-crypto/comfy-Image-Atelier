using System.Collections.Concurrent;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;

namespace Anima.Launcher.Pages;

public sealed partial class LogsPage : LauncherPage
{
    private const int MaxLines = 2000;
    private readonly ConcurrentQueue<string> pending = new();
    private readonly Queue<string> visible = new();
    private readonly DispatcherTimer flushTimer;
    private volatile bool active;

    public LogsPage()
    {
        InitializeComponent();
        flushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        flushTimer.Tick += (_, _) => Flush();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        active = true;
        visible.Clear();
        lock (State.LogLines)
            foreach (var line in State.LogLines.TakeLast(MaxLines)) visible.Enqueue(line);
        Render();
        State.LogAppended += OnLog;
        flushTimer.Start();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        active = false;
        State.LogAppended -= OnLog;
        flushTimer.Stop();
        while (pending.TryDequeue(out _)) { }
        base.OnNavigatedFrom(e);
    }

    // No UI access from process-output threads. A timer batches bursts and bounds
    // work per frame, avoiding a dispatcher item and full string copy per line.
    private void OnLog(string line)
    {
        if (!active) return;
        pending.Enqueue(line.Length > 20000 ? line[..20000] + " …" : line);
        while (pending.Count > MaxLines * 2) pending.TryDequeue(out _);
    }

    private void Flush()
    {
        var changed = false;
        while (pending.TryDequeue(out var line))
        {
            visible.Enqueue(line);
            changed = true;
        }
        while (visible.Count > MaxLines) visible.Dequeue();
        if (changed) Render();
    }

    private void Render()
    {
        var text = string.Join("\r\n", visible);
        // A very verbose single command must not make the text editor unbounded.
        if (text.Length > 240000) text = text[^240000..];
        var scroller = FindScroller(LogBox);
        var oldOffset = scroller?.VerticalOffset;
        LogBox.Text = text;
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
        while (pending.TryDequeue(out _)) { }
        visible.Clear();
        LogBox.Text = "";
    }
}
