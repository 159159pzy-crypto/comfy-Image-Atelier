using Anima.Launcher.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Anima.Launcher;

/// <summary>Edits the existing plugin through its local API; stores no separate config.</summary>
public sealed class PromptAssistantPanel : UserControl
{
    private readonly ComboBox modePicker = new() { Header = "配置用途", ItemsSource = new[] { "文本重写", "图像反推" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox services = new() { HorizontalAlignment = HorizontalAlignment.Stretch, DisplayMemberPath = "Name", PlaceholderText = "先加载服务与模板" };
    private readonly ComboBox models = new() { HorizontalAlignment = HorizontalAlignment.Stretch, DisplayMemberPath = "Name", PlaceholderText = "此服务已添加的 LLM 模型" };
    private readonly ComboBox templates = new() { HorizontalAlignment = HorizontalAlignment.Stretch, DisplayMemberPath = "Name", PlaceholderText = "选择系统提示词" };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock serviceDetail = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock templateDetail = new() { TextWrapping = TextWrapping.Wrap, MaxLines = 3 };
    private readonly StackPanel body = new() { Spacing = 10, Margin = new Thickness(0, 10, 0, 0) };
    private readonly List<Button> loadedActions = [];
    private bool loaded;
    private string? loadedEnvironment;

    public PromptAssistantPanel()
    {
        body.Children.Add(new TextBlock
        {
            Text = "连接当前 ComfyUI 的 Prompt Assistant。文本重写与图像反推共用服务，分别管理模型与系统提示词。配置由插件保存，Anima / Krea 2 共用。",
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(Row(Button("加载 / 刷新", async () => await Run(async (client, ct) => await Reload(client, ct))),
            Button("检查 / 修复重写纯模板支持", Repair)));
        body.Children.Add(modePicker);
        body.Children.Add(new TextBlock { Text = "共用服务", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        body.Children.Add(services);
        body.Children.Add(serviceDetail);
        body.Children.Add(Row(Action("新增服务", () => ServiceEditor(null)), Action("编辑服务", () => ServiceEditor(SelectedService())),
            Action("删除服务", DeleteService)));
        body.Children.Add(models);
        body.Children.Add(Row(Action("添加模型", () => ModelEditor(false)), Action("编辑参数", () => ModelEditor(true)), Action("删除模型", DeleteModel)));
        body.Children.Add(new TextBlock { Text = "系统提示词", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        body.Children.Add(templates);
        body.Children.Add(templateDetail);
        body.Children.Add(Row(Action("新增模板", () => TemplateEditor(null)), Action("编辑模板", () => TemplateEditor(SelectedTemplate())),
            Action("复制模板", CopyTemplate), Action("删除模板", DeleteTemplate)));
        body.Children.Add(status);
        Content = new Expander
        {
            Header = "提示词助手", HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = body,
        };
        services.SelectionChanged += (_, _) =>
        {
            var service = services.SelectedItem as AssistantService;
            models.ItemsSource = service?.Models;
            models.SelectedIndex = service?.Models.Count > 0 ? 0 : -1;
            serviceDetail.Text = service is null ? "" : $"{service.Type} · {service.BaseUrl}\nAPI Key：{(service.HasApiKey ? "已配置" : "未配置")}；模型参数：{(service.AdvancedParameters ? "启用" : "使用服务默认值")}";
        };
        templates.SelectionChanged += (_, _) => templateDetail.Text = (templates.SelectedItem as AssistantTemplate)?.Content ?? "";
        modePicker.SelectionChanged += async (_, _) =>
        {
            models.PlaceholderText = Mode == AssistantMode.Reverse ? "此服务已添加的 VLM 视觉模型" : "此服务已添加的 LLM 模型";
            models.ItemsSource = null;
            templates.ItemsSource = null;
            templateDetail.Text = "";
            if (!loaded) return;
            loaded = false;
            UpdateActions();
            await Run(async (client, ct) => await Reload(client, ct));
        };
        UpdateActions();
    }

    private Button Action(string text, Func<Task> action)
    {
        var button = Button(text, action);
        loadedActions.Add(button);
        return button;
    }
    private static Button Button(string text, Func<Task> action)
    {
        var button = new Button { Content = text };
        button.Click += async (_, _) => await action();
        return button;
    }
    private static StackPanel Row(params UIElement[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }
    private void UpdateActions() { foreach (var action in loadedActions) action.IsEnabled = loaded; }
    private AssistantMode Mode => modePicker.SelectedIndex == 1 ? AssistantMode.Reverse : AssistantMode.Rewrite;
    private AssistantService? SelectedService() => services.SelectedItem as AssistantService;
    private AssistantTemplate? SelectedTemplate() => templates.SelectedItem as AssistantTemplate;

    private async Task Run(Func<PromptAssistantClient, CancellationToken, Task> operation)
    {
        if (!IsEnabled) return;
        IsEnabled = false;
        status.Text = "正在连接提示词助手…";
        try
        {
            var config = App.Shared.Config.Clone();
            var environment = config.ComfyUrl + "|" + config.ComfyRoot;
            var stats = await ServiceManager.Json(config.ComfyUrl + "/system_stats");
            if (stats is null || !ServiceManager.SameRoot(stats.Value, config.ComfyRoot))
                throw new InvalidOperationException("此端口的 ComfyUI 与启动器所选目录不一致，请先检查环境并启动对应服务。");
            using var client = new PromptAssistantClient(config.ComfyUrl);
            await client.Connect(CancellationToken.None, Mode);
            if (loaded && loadedEnvironment != environment)
            {
                await Reload(client, CancellationToken.None);
                loadedEnvironment = environment;
                status.Text = "已切换到当前环境的服务与模板，请重新选择要编辑的项目。";
                return;
            }
            loadedEnvironment = environment;
            await operation(client, CancellationToken.None);
        }
        catch (Exception error) { status.Text = error.Message; }
        finally { IsEnabled = true; UpdateActions(); }
    }

    private async Task Reload(PromptAssistantClient client, CancellationToken ct, string? serviceId = null, string? templateId = null)
    {
        serviceId ??= SelectedService()?.Id;
        templateId ??= SelectedTemplate()?.Id;
        var selectedModel = (models.SelectedItem as AssistantModel)?.Name;
        var serviceList = await client.Services(ct, Mode);
        var templateList = await client.Templates(ct, Mode);
        services.ItemsSource = serviceList;
        services.SelectedItem = serviceList.FirstOrDefault(item => item.Id == serviceId) ?? serviceList.FirstOrDefault();
        models.SelectedItem = SelectedService()?.Models.FirstOrDefault(item => item.Name == selectedModel) ?? SelectedService()?.Models.FirstOrDefault();
        templates.ItemsSource = templateList;
        templates.SelectedItem = templateList.FirstOrDefault(item => item.Id == templateId) ?? templateList.FirstOrDefault();
        loaded = true;
        status.Text = Mode == AssistantMode.Reverse
            ? client.SupportsVision
                ? "已加载图像反推配置。请添加支持图像输入的 VLM 模型，保存后在反推台刷新服务与模板。"
                : "已加载配置，但运行中的插件缺少兼容的图像反推节点。请更新 Prompt Assistant 并重新启动 ComfyUI。"
            : client.SupportsTemplateOnly
                ? "已加载。保存后，在工作台重写区刷新服务与模板即可选用。"
                : "已加载配置，但运行中的插件尚不支持纯模板重写。请停止 ComfyUI 后点击“检查 / 修复重写纯模板支持”，再重新启动。";
    }

    private async Task Repair()
    {
        if (App.Shared.Busy || !IsEnabled) return;
        await App.Shared.Run(async ct =>
        {
            var config = App.Shared.Config;
            if (string.IsNullOrWhiteSpace(config.ComfyRoot) || !File.Exists(Path.Combine(config.ComfyRoot, "main.py")) || !File.Exists(config.Python))
                throw new InvalidOperationException("请先在安装与环境设置中导入有效的 ComfyUI 和 Python。");
            if (await ServiceManager.PortOpen(config.ComfyPort, ct))
            {
                using var client = new PromptAssistantClient(config.ComfyUrl);
                await client.Connect(ct);
                status.Text = client.SupportsTemplateOnly
                    ? "运行中的 Prompt Assistant 已支持纯模板重写，无需修复。"
                    : "ComfyUI 正在运行。请先自行停止它，再点击修复；已应用补丁的实例也需要重启才会生效。";
                App.Shared.SetStatus(status.Text);
                return;
            }
            var plugin = PromptAssistantCompatibility.ResolveDirectory(config.ComfyRoot)
                ?? throw new InvalidOperationException("未找到 Prompt Assistant，请先安装或修复环境依赖。");
            await PromptAssistantCompatibility.CheckOrApply(config, Program.StateDir, plugin, true, ct);
            status.Text = "纯模板兼容补丁已就绪。启动 ComfyUI 后刷新服务与模板。";
            App.Shared.SetStatus(status.Text);
        });
    }

    private async Task ShowEditor(string title, StackPanel fields, Func<Task> save)
    {
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed) };
        fields.Children.Add(error);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = title,
            Content = new ScrollViewer { Content = fields, MaxHeight = 560, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary,
        };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try { await save(); }
            catch (Exception ex) { error.Text = ex.Message; args.Cancel = true; }
            finally { deferral.Complete(); }
        };
        await dialog.ShowAsync();
    }

    private Task ServiceEditor(AssistantService? selected) => Run(async (client, ct) =>
    {
        var name = new TextBox { Header = "服务名称", Text = selected?.Name ?? "", MinWidth = 370 };
        var type = new ComboBox { Header = "协议", ItemsSource = new[] { "openai_compatible", "ollama" }, IsEnabled = selected is null, HorizontalAlignment = HorizontalAlignment.Stretch };
        type.SelectedItem = selected?.Type ?? "openai_compatible";
        if (selected is not null && type.SelectedItem is null) type.ItemsSource = new[] { selected.Type };
        type.SelectedIndex = type.SelectedIndex < 0 ? 0 : type.SelectedIndex;
        var url = new TextBox { Header = "Base URL", Text = selected?.BaseUrl ?? "", PlaceholderText = "https://example.com/v1" };
        var key = new PasswordBox { Header = selected?.HasApiKey == true ? "API Key（已配置，留空保留）" : "API Key（无认证服务可留空）" };
        var clear = new CheckBox { Content = "清除已保存的 API Key", IsEnabled = selected is not null };
        var advanced = new CheckBox { Content = "启用所设置的模型参数", IsChecked = selected?.AdvancedParameters ?? true };
        clear.Checked += (_, _) => { key.Password = ""; key.IsEnabled = false; };
        clear.Unchecked += (_, _) => key.IsEnabled = true;
        var fields = new StackPanel { Spacing = 10 };
        foreach (var control in new UIElement[] { name, type, url, key, clear, advanced }) fields.Children.Add(control);
        await ShowEditor(selected is null ? "新增共用服务" : "编辑共用服务", fields, async () =>
        {
            var id = await client.SaveService(selected?.Id, type.SelectedItem as string ?? "openai_compatible", name.Text, url.Text, key.Password, clear.IsChecked == true, advanced.IsChecked == true, ct);
            key.Password = "";
            await Reload(client, ct, serviceId: id);
        });
        key.Password = "";
    });

    private Task DeleteService() => Run(async (client, ct) =>
    {
        if (SelectedService() is not { } service) { status.Text = "请先选择服务。"; return; }
        if (!await Dialogs.ConfirmCancel(XamlRoot, "删除服务", $"删除“{service.Name}”及其模型配置？其他使用此服务的 ComfyUI 工作流也会受影响。", "删除")) return;
        await client.DeleteService(service.Id, ct);
        await Reload(client, ct);
    });

    private Task ModelEditor(bool existing) => Run(async (client, ct) =>
    {
        if (SelectedService() is not { } service) { status.Text = "请先选择服务。"; return; }
        var selected = existing ? models.SelectedItem as AssistantModel : null;
        if (existing && selected is null) { status.Text = "请先选择模型。"; return; }
        var name = new TextBox { Header = "模型名称", Text = selected?.Name ?? "", IsReadOnly = existing, MinWidth = 370 };
        var temperature = new NumberBox { Header = "Temperature", Minimum = 0, Maximum = 2, Value = selected?.Temperature ?? .7, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, SmallChange = .1 };
        var topP = new NumberBox { Header = "Top P", Minimum = 0, Maximum = 1, Value = selected?.TopP ?? .9, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, SmallChange = .05 };
        var tokens = new NumberBox { Header = "Max Tokens", Minimum = 1, Maximum = int.MaxValue, Value = selected?.MaxTokens ?? 4096, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        var fields = new StackPanel { Spacing = 10 };
        fields.Children.Add(name);
        if (!existing)
        {
            var available = new ComboBox { PlaceholderText = "也可手动填写模型名称", HorizontalAlignment = HorizontalAlignment.Stretch };
            var fetchStatus = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var fetch = Button("从供应商获取模型列表", async () =>
            {
                try
                {
                    available.ItemsSource = await client.RemoteModels(service.Id, ct, Mode);
                    fetchStatus.Text = Mode == AssistantMode.Reverse ? "已获取模型列表，请选择支持图像输入的视觉模型。" : "已获取可用模型。";
                }
                catch (Exception error) { fetchStatus.Text = error.Message; }
            });
            available.SelectionChanged += (_, _) => { if (available.SelectedItem is string modelName) name.Text = modelName; };
            fields.Children.Add(fetch); fields.Children.Add(available); fields.Children.Add(fetchStatus);
        }
        fields.Children.Add(temperature); fields.Children.Add(topP); fields.Children.Add(tokens);
        if (!service.AdvancedParameters) fields.Children.Add(new TextBlock { Text = "该服务尚未启用模型参数；可在编辑服务中开启。", TextWrapping = TextWrapping.Wrap });
        await ShowEditor(existing ? "编辑模型参数" : Mode == AssistantMode.Reverse ? "添加 VLM 视觉模型" : "添加 LLM 模型", fields, async () =>
        {
            await client.SaveModel(service.Id, name.Text, temperature.Value, topP.Value, double.IsFinite(tokens.Value) ? (int)tokens.Value : 0, existing, ct, Mode);
            await Reload(client, ct, serviceId: service.Id);
        });
    });

    private Task DeleteModel() => Run(async (client, ct) =>
    {
        if (SelectedService() is not { } service || models.SelectedItem is not AssistantModel model) { status.Text = "请先选择模型。"; return; }
        if (!await Dialogs.ConfirmCancel(XamlRoot, "删除模型", $"从“{service.Name}”删除模型“{model.Name}”？", "删除")) return;
        await client.DeleteModel(service.Id, model.Name, ct, Mode);
        await Reload(client, ct, serviceId: service.Id);
    });

    private Task TemplateEditor(AssistantTemplate? selected) => Run(async (client, ct) =>
    {
        var name = new TextBox { Header = "模板名称", Text = selected?.Name ?? "", MinWidth = 370 };
        var category = new TextBox { Header = "分类（可选）", Text = selected?.Category ?? "" };
        var content = new TextBox { Header = "系统提示词", Text = selected?.Content ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 230 };
        var fields = new StackPanel { Spacing = 10 };
        fields.Children.Add(name); fields.Children.Add(category); fields.Children.Add(content);
        fields.Children.Add(new TextBlock { Text = Mode == AssistantMode.Reverse
            ? "此模板决定 AI 如何描述图片，包括输出语言、画面细节和提示词格式。"
            : "按原文发送所选系统提示词；语言与重写要求由此模板决定。", TextWrapping = TextWrapping.Wrap });
        await ShowEditor(selected is null ? "新增系统提示词" : "编辑系统提示词", fields, async () =>
        {
            var id = await client.SaveTemplate(selected?.Id, name.Text, content.Text, category.Text, ct, selected, Mode);
            await Reload(client, ct, templateId: id);
        });
    });

    private Task CopyTemplate() => Run(async (client, ct) =>
    {
        if (SelectedTemplate() is not { } template) { status.Text = "请先选择模板。"; return; }
        var id = await client.CopyTemplate(template.Id, ct, Mode);
        await Reload(client, ct, templateId: id);
    });

    private Task DeleteTemplate() => Run(async (client, ct) =>
    {
        if (SelectedTemplate() is not { } template) { status.Text = "请先选择模板。"; return; }
        if (!await Dialogs.ConfirmCancel(XamlRoot, "删除系统提示词", $"删除模板“{template.Name}”？使用此模板的工作台需要重新选择。", "删除")) return;
        await client.DeleteTemplate(template.Id, ct, Mode);
        await Reload(client, ct);
    });
}
