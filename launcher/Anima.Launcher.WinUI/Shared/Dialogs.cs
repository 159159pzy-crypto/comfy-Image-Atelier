using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Anima.Launcher;

internal static class StudioTheme
{
    public static void ScopeResources(FrameworkElement element)
    {
        var resources = Application.Current.Resources;
        // Native templates can resolve their SDK resource aliases before the
        // application dictionary. Put the palette at the owning element scope.
        var nativeContrast = resources.MergedDictionaries
            .Select(FindNativeContrast)
            .FirstOrDefault(dictionary => dictionary is not null);
        var contrast = new ResourceDictionary();
        var nativeKeys = 0;
        foreach (var theme in new[] { "Light", "Default" })
        {
            var source = (ResourceDictionary)resources.ThemeDictionaries[theme];
            var scoped = new ResourceDictionary();
            foreach (var entry in source)
            {
                var studio = entry.Key is string key && key.StartsWith("Studio", StringComparison.Ordinal);
                if (!studio)
                {
                    // Do not let a local Default override leak into high contrast.
                    if (nativeContrast is null || !nativeContrast.TryGetValue(entry.Key, out var accessible)) continue;
                    contrast[entry.Key] = accessible;
                    if (theme == "Default") nativeKeys++;
                }
                scoped[entry.Key] = entry.Value;
            }
            element.Resources.ThemeDictionaries[theme] = scoped;
        }
        foreach (var entry in (ResourceDictionary)resources.ThemeDictionaries["HighContrast"])
            contrast[entry.Key] = entry.Value;
        element.Resources.ThemeDictionaries["HighContrast"] = contrast;
        Program.Log($"界面主题作用域：{element.GetType().Name}，已覆盖 {nativeKeys} 个原生画刷并保留高对比度。");
    }

    private static ResourceDictionary? FindNativeContrast(ResourceDictionary dictionary)
    {
        if (dictionary.ThemeDictionaries.TryGetValue("HighContrast", out var value) && value is ResourceDictionary contrast)
            return contrast;
        return dictionary.MergedDictionaries.Select(FindNativeContrast).FirstOrDefault(found => found is not null);
    }
}

internal static class Dialogs
{
    public static ContentDialog Create(XamlRoot root, string title, object content,
        string? primary = null, string close = "取消", ContentDialogButton defaultButton = ContentDialogButton.Primary)
    {
        var owner = root.Content as FrameworkElement;
        var primaryStyle = (Style)Application.Current.Resources["StudioPrimaryButtonStyle"];
        var secondaryStyle = (Style)Application.Current.Resources["StudioButtonStyle"];
        var defaultAction = primary is null ? ContentDialogButton.Close : defaultButton;
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            RequestedTheme = owner?.ActualTheme ?? ElementTheme.Default,
            Style = (Style)Application.Current.Resources["StudioDialogStyle"],
            Title = title,
            Content = content,
            PrimaryButtonText = primary ?? "",
            CloseButtonText = close,
            DefaultButton = defaultAction,
            PrimaryButtonStyle = defaultAction == ContentDialogButton.Primary ? primaryStyle : secondaryStyle,
            SecondaryButtonStyle = defaultAction == ContentDialogButton.Secondary ? primaryStyle : secondaryStyle,
            CloseButtonStyle = defaultAction == ContentDialogButton.Close ? primaryStyle : secondaryStyle,
        };
        StudioTheme.ScopeResources(dialog);
        // A ContentDialog lives in a separate popup and does not inherit the
        // window's explicit theme. Keep it aligned while it is visible as well.
        void ThemeChanged(FrameworkElement sender, object args) => dialog.RequestedTheme = sender.ActualTheme;
        var isOpen = false;
        dialog.Opened += (_, _) =>
        {
            isOpen = true;
            if (owner is not null)
            {
                dialog.RequestedTheme = owner.ActualTheme;
                owner.ActualThemeChanged += ThemeChanged;
            }
            // WinUI's DefaultButtonStates replaces the default button's Style
            // with AccentButtonStyle. Preserve its native behavior and copy our
            // layout onto the actual button after that state is applied.
            void FocusDefault()
            {
                try
                {
                    if (!isOpen) return;
                    var name = dialog.DefaultButton switch
                    {
                        ContentDialogButton.Primary => "PrimaryButton",
                        ContentDialogButton.Secondary => "SecondaryButton",
                        ContentDialogButton.Close => "CloseButton",
                        _ => "",
                    };
                    if (name.Length == 0) return;
                    if (FindButton(dialog, name) is not { } button) return;
                    // Read resolved values through their typed properties. A
                    // Setter.Value can retain a XAML/WinRT boxed value that is
                    // not safe to pass through DependencyObject.SetValue.
                    var appearance = new Button { Style = primaryStyle };
                    button.CornerRadius = appearance.CornerRadius;
                    button.MinHeight = appearance.MinHeight;
                    button.Padding = appearance.Padding;
                    button.FontSize = appearance.FontSize;
                    button.FontWeight = appearance.FontWeight;
                    // Editing forms keep their first field focused. Confirmation
                    // messages focus the selected default; a Close default is always
                    // honored, including when its content is selectable.
                    if (dialog.DefaultButton == ContentDialogButton.Close || content is ScrollViewer { Content: TextBlock })
                    {
                        button.IsTabStop = true;
                        button.Focus(FocusState.Keyboard);
                    }
                }
                catch (Exception ex)
                {
                    Program.Log($"弹窗默认按钮应用失败：{title}{Environment.NewLine}{ex}");
                }
            }
            // Defer until native initial focus selection has completed; selectable
            // message text must not take focus from Cancel in a danger confirmation.
            dialog.DispatcherQueue.TryEnqueue(FocusDefault);
        };
        dialog.Closed += (_, _) =>
        {
            isOpen = false;
            if (owner is not null) owner.ActualThemeChanged -= ThemeChanged;
        };
        return dialog;
    }

    private static Button? FindButton(DependencyObject root, string name)
    {
        if (root is Button button && button.Name == name) return button;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindButton(VisualTreeHelper.GetChild(root, i), name) is { } found) return found;
        return null;
    }

    private static ScrollViewer Message(XamlRoot root, string message) => new()
    {
        MaxHeight = Math.Clamp(root.Size.Height - 260, 120, 520),
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollMode = ScrollMode.Disabled,
        Content = new TextBlock
        {
            Text = message,
            Style = (Style)Application.Current.Resources["StudioBodyStyle"],
            IsTextSelectionEnabled = true,
        },
    };

    public static async Task<bool> Confirm(XamlRoot root, string title, string message, string yes = "确定", string no = "取消")
    {
        var dialog = Create(root, title, Message(root, message), yes, no);
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public static async Task<bool> ConfirmCancel(XamlRoot root, string title, string message, string primary = "确定")
    {
        var dialog = Create(root, title, Message(root, message), primary, defaultButton: ContentDialogButton.Close);
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public static async Task Error(XamlRoot root, string message, string title = "操作未完成")
    {
        var dialog = Create(root, title, Message(root, message), close: "关闭");
        await dialog.ShowAsync();
    }
}
