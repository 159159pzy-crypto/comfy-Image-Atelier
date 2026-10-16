using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Anima.Launcher;

/// <summary>A status card for one managed service: name + status badge + URL + start/stop/open.</summary>
public sealed partial class ServiceCard : UserControl
{
    private const double NarrowThreshold = 650;

    public ServiceCard()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var state = e.NewSize.Width < NarrowThreshold ? "Narrow" : "Wide";
        VisualStateManager.GoToState(this, state, true);
    }

    public event RoutedEventHandler? StartClicked;
    public event RoutedEventHandler? StopClicked;
    public event RoutedEventHandler? OpenClicked;

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(ServiceCard), new PropertyMetadata("", (d, e) => ((ServiceCard)d).NameText.Text = (string)e.NewValue));
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public static readonly DependencyProperty GlyphTextProperty =
        DependencyProperty.Register(nameof(GlyphText), typeof(string), typeof(ServiceCard), new PropertyMetadata("", (d, e) => ((ServiceCard)d).Glyph.Glyph = (string)e.NewValue));
    public string GlyphText { get => (string)GetValue(GlyphTextProperty); set => SetValue(GlyphTextProperty, value); }

    /// <summary>Render status + button enablement for a service snapshot.</summary>
    public void Update(ServiceInfo info, bool busy)
    {
        UrlText.Text = info.Url;
        var (label, state) = info.Status switch
        {
            "ready" => ("已就绪", 2),
            "external" => ("已就绪 · 外部服务", 2),
            "starting" => ("正在启动…", 1),
            "conflict" => ("端口被占用", 3),
            "error" => ("尚未就绪", 3),
            "stopped" => ("已停止", 0),
            _ => ("未配置", 0),
        };

        StatusText.Text = label;
        var theme = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast ? "HighContrast"
            : ActualTheme == ElementTheme.Light ? "Light" : "Default";
        var colors = (ResourceDictionary)Application.Current.Resources.ThemeDictionaries[theme];
        var brush = (Brush)colors[state == 2 ? "StudioSuccessBrush" : state == 1 ? "StudioWarningBrush" : state == 3 ? "StudioDangerBrush" : "StudioTertiaryBrush"];
        StatusText.Foreground = brush;
        StatusDot.Fill = brush;

        // Only managed services can be stopped; only running services can be opened.
        StartButton.IsEnabled = !busy && info.Configured && !info.Running && info.Status != "starting";
        StopButton.IsEnabled = !busy && info.Managed;
        OpenButton.IsEnabled = info.Running && (info.Ours || info.Managed);

        if (!info.Configured)
        {
            HintText.Text = "先在「环境」页选择 ComfyUI 目录并完成首次配置。";
            HintText.Visibility = Visibility.Visible;
        }
        else if (info.Status == "conflict")
        {
            HintText.Text = "该端口被其他程序占用；外部服务不归启动器停止。";
            HintText.Visibility = Visibility.Visible;
        }
        else if (info.Status == "external")
        {
            HintText.Text = "正在复用外部服务，启动器不会停止它。";
            HintText.Visibility = Visibility.Visible;
        }
        else if (info.Status == "starting")
        {
            HintText.Text = "正在等待服务响应，首次加载模型可能需要一些时间。";
            HintText.Visibility = Visibility.Visible;
        }
        else if (info.Status == "error" || !string.IsNullOrWhiteSpace(info.Detail))
        {
            HintText.Text = string.IsNullOrWhiteSpace(info.Detail) ? "服务还未通过健康检查，可查看日志后重试。" : info.Detail;
            HintText.Visibility = Visibility.Visible;
        }
        else HintText.Visibility = Visibility.Collapsed;
    }

    private void Start_Click(object sender, RoutedEventArgs e) => StartClicked?.Invoke(this, e);
    private void Stop_Click(object sender, RoutedEventArgs e) => StopClicked?.Invoke(this, e);
    private void Open_Click(object sender, RoutedEventArgs e) => OpenClicked?.Invoke(this, e);
}
