using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Anima.Launcher.Pages;

internal sealed record DownloadsScrollPosition(ScrollViewer View, int Index, string Name, double Horizontal, double Vertical);

/// <summary>Base for all pages: provides shared state access.</summary>
public abstract class LauncherPage : Page
{
    private DispatcherTimer? downloadsScrollRestore;
    protected LauncherPage()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
    }

    internal LauncherState State => App.Shared;
    internal MainWindow Shell => App.Shell;

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        downloadsScrollRestore?.Stop();
        downloadsScrollRestore = null;
        base.OnNavigatedFrom(e);
    }

    internal DownloadsScrollPosition[] CaptureDownloadsScroll() => Descendants(this).OfType<ScrollViewer>()
        .Select((view, index) => new DownloadsScrollPosition(view, index, view.Name, view.HorizontalOffset, view.VerticalOffset)).ToArray();

    internal void RestoreDownloadsScroll(IReadOnlyList<DownloadsScrollPosition> offsets)
    {
        downloadsScrollRestore?.Stop();
        if (offsets.Count == 0) return;
        // The source visual tree must be captured before Frame.Navigate, and restored
        // after GoBack. Cached pages can load/layout after OnNavigatedTo has returned.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        downloadsScrollRestore = timer;
        var attempts = 0;
        var settled = 0;
        timer.Tick += (_, _) =>
        {
            if (!ReferenceEquals(downloadsScrollRestore, timer)) { timer.Stop(); return; }
            if (++attempts > 12) { timer.Stop(); downloadsScrollRestore = null; return; }
            if (!IsLoaded) return;
            UpdateLayout();
            var current = Descendants(this).OfType<ScrollViewer>().ToArray();
            var ready = true;
            foreach (var offset in offsets)
            {
                // Prefer the cached instance; the ordinal/name fallback also handles
                // a control template recreated while the page was out of the frame.
                var view = current.FirstOrDefault(item => ReferenceEquals(item, offset.View))
                    ?? current.ElementAtOrDefault(offset.Index);
                if (view is null || view.Name != offset.Name) { ready = false; continue; }
                var horizontal = Math.Min(offset.Horizontal, view.ScrollableWidth);
                var vertical = Math.Min(offset.Vertical, view.ScrollableHeight);
                if (Math.Abs(view.HorizontalOffset - horizontal) > 0.5 || Math.Abs(view.VerticalOffset - vertical) > 0.5)
                {
                    view.ChangeView(horizontal, vertical, null, true);
                    ready = false;
                }
                if (view.ActualHeight <= 0 || view.ScrollableHeight + 0.5 < offset.Vertical
                    || view.ScrollableWidth + 0.5 < offset.Horizontal) ready = false;
            }
            settled = ready ? settled + 1 : 0;
            if (settled >= 2) { timer.Stop(); downloadsScrollRestore = null; }
        };
        timer.Start();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
