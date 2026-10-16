using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Anima.Launcher.Pages;

/// <summary>Base for all pages: provides shared state access.</summary>
public abstract class LauncherPage : Page
{
    protected LauncherPage()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
    }

    internal LauncherState State => App.Shared;
    internal MainWindow Shell => App.Shell;
}
