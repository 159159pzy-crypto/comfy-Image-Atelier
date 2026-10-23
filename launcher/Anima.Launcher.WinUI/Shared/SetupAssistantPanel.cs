using Anima.Launcher.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Anima.Launcher;

/// <summary>A short onboarding editor. Credentials are sent only to the local plugin, never to launcher state.</summary>
public sealed class SetupAssistantPanel : UserControl
{
    private readonly ComboBox services = new() { Header = "已有供应商", DisplayMemberPath = "Name", PlaceholderText = "选择已有供应商，或新增", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox name = new() { Header = "供应商名称", PlaceholderText = "例如：我的 AI 服务" };
    private readonly ComboBox type = new() { Header = "协议", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox url = new() { Header = "Base URL", PlaceholderText = "https://example.com/v1" };
    private readonly PasswordBox key = new() { Header = "API Key（无认证服务可留空）" };
    private readonly CheckBox clearKey = new() { Content = "清除已保存的 API Key" };
    private readonly ComboBox configuredModels = new() { Header = "已配置的文本模型", DisplayMemberPath = "Name", PlaceholderText = "也可直接填写新的模型名", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox modelName = new() { Header = "默认文本模型（LLM）", PlaceholderText = "填写供应商提供的完整模型名称" };
    private readonly ComboBox remoteModels = new() { PlaceholderText = "从供应商返回的模型中选择", HorizontalAlignment = HorizontalAlignment.Stretch, Visibility = Visibility.Collapsed };
    private readonly TextBlock status = BodyText("先启动 ComfyUI，再加载当前环境的供应商配置。");
    private readonly StackPanel editor = new() { Spacing = 12 };
    private readonly ContentControl editorHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, IsTabStop = false };
    private readonly Button save;
    private readonly Button fetch;
    private CancellationTokenSource session = new();
    private string? environment;
    private string? loadedRoot;
    private string? serviceId;
    private bool advancedParameters;
    private bool loaded;
    private bool preserveDraftOnReconnect;
    private bool updating;

    public bool IsWorking { get; private set; }
    public bool IsConnected => loaded;
    public event Action<bool>? WorkingChanged;
    public event Action? Saved;

    public SetupAssistantPanel()
    {
        var refresh = Button("加载 / 刷新", async () => await ActivateAsync(), "SetupAssistantRefresh");
        var add = Button("新增供应商", () => { SelectService(null); return Task.CompletedTask; }, "SetupAssistantAdd");
        fetch = Button("保存服务并获取模型列表", FetchModelsAsync, "SetupAssistantFetchModels");
        save = Button("保存并继续", async () => { await SaveAsync(); }, "SetupAssistantSave");
        foreach (var (control, id) in new (DependencyObject, string)[]
        {
            (services, "SetupAssistantServices"), (name, "SetupAssistantName"), (type, "SetupAssistantType"),
            (url, "SetupAssistantUrl"), (key, "SetupAssistantKey"), (clearKey, "SetupAssistantClearKey"),
            (configuredModels, "SetupAssistantConfiguredModels"), (modelName, "SetupAssistantModel"),
            (remoteModels, "SetupAssistantRemoteModels"), (status, "SetupAssistantStatus")
        }) AutomationProperties.SetAutomationId(control, id);
        var actions = new AssistantActionPanel();
        actions.Children.Add(refresh);
        actions.Children.Add(add);
        foreach (var child in new UIElement[] { services, actions, name, type, url, key, clearKey,
            configuredModels, modelName, fetch, remoteModels,
            BodyText("模型列表仅在点击上方按钮时向供应商获取，也可直接填写模型名。保存会设置此供应商的默认文本模型，不会发送提示词或生成请求。"), save })
            editor.Children.Add(child);
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(BodyText("配置一个供应商和文本模型即可使用 AI 提示词重写。密钥由当前 ComfyUI 的 Prompt Assistant 插件保存；图像反推、模型参数与系统提示词可稍后在「设置 → 提示词助手」中管理。"));
        editorHost.Content = editor;
        body.Children.Add(editorHost);
        body.Children.Add(status);
        Content = body;
        services.SelectionChanged += (_, _) => { if (!updating) SelectService(services.SelectedItem as AssistantService); };
        configuredModels.SelectionChanged += (_, _) =>
        {
            if (!updating && configuredModels.SelectedItem is AssistantModel model) modelName.Text = model.Name;
        };
        remoteModels.SelectionChanged += (_, _) =>
        {
            if (remoteModels.SelectedItem is string model) modelName.Text = model;
        };
        clearKey.Checked += (_, _) => { key.Password = ""; key.IsEnabled = false; };
        clearKey.Unchecked += (_, _) => key.IsEnabled = true;
        type.SelectionChanged += (_, _) =>
        {
            if (!updating && serviceId is null && type.SelectedItem as string == "ollama" && string.IsNullOrWhiteSpace(url.Text))
                url.Text = "http://localhost:11434";
        };
        Unloaded += (_, _) => { session.Cancel(); key.Password = ""; };
        SelectService(null);
        editorHost.IsEnabled = false;
    }

    public Task ActivateAsync(CancellationToken ct = default)
    {
        if (IsWorking) return Task.CompletedTask;
        if (session.IsCancellationRequested) { session.Dispose(); session = new(); }
        return Run(async (client, token) =>
        {
            var list = await client.Services(token);
            var selected = list.FirstOrDefault(item => item.Id == serviceId) ?? list.FirstOrDefault();
            updating = true;
            try
            {
                services.ItemsSource = list;
                if (preserveDraftOnReconnect && SameEnvironmentRoot(App.Shared.Config.ComfyRoot, loadedRoot))
                {
                    // A restarted process must not replace the unfinished form or
                    // forget an acknowledged create that still needs a model save.
                    var retained = list.FirstOrDefault(item => item.Id == serviceId);
                    services.SelectedItem = retained;
                    configuredModels.ItemsSource = retained?.Models;
                }
                else SelectService(selected);
            }
            finally { updating = false; }
            preserveDraftOnReconnect = false;
            loaded = true;
            status.Text = "已加载当前环境的配置。选择或填写供应商与文本模型后，保存并继续。";
        }, ct, connecting: true);
    }

    /// <summary>Only non-secret choices are read back; the password is never restored.</summary>
    public void ClearSession()
    {
        if (IsWorking) return;
        key.Password = "";
        loaded = false;
        environment = null;
        loadedRoot = null;
        preserveDraftOnReconnect = false;
        updating = true;
        services.ItemsSource = null;
        updating = false;
        SelectService(null);
        editorHost.IsEnabled = false;
        status.Text = "先启动 ComfyUI，再加载当前环境的供应商配置。";
    }

    private void SelectService(AssistantService? service)
    {
        updating = true;
        try
        {
            services.SelectedItem = service;
            serviceId = service?.Id;
            advancedParameters = service?.AdvancedParameters ?? false;
            name.Text = service?.Name ?? "";
            type.ItemsSource = service is not null && service.Type is not ("openai_compatible" or "ollama")
                ? new[] { service.Type } : new[] { "openai_compatible", "ollama" };
            type.SelectedItem = service?.Type ?? "openai_compatible";
            type.IsEnabled = service is null;
            url.Text = service?.BaseUrl ?? "";
            key.Password = "";
            key.Header = service?.HasApiKey == true ? "API Key（已配置，留空保留）" : "API Key（无认证服务可留空）";
            clearKey.IsChecked = false;
            clearKey.IsEnabled = service is not null;
            configuredModels.ItemsSource = service?.Models;
            var model = service?.Models.FirstOrDefault(item => item.IsDefault) ?? service?.Models.FirstOrDefault();
            configuredModels.SelectedItem = model;
            modelName.Text = model?.Name ?? "";
            remoteModels.ItemsSource = null;
            remoteModels.Visibility = Visibility.Collapsed;
        }
        finally { updating = false; }
    }

    private async Task<string> SaveServiceAsync(PromptAssistantClient client, CancellationToken ct)
    {
        var id = await client.SaveService(serviceId, type.SelectedItem as string ?? "openai_compatible", name.Text, url.Text,
            key.Password, clearKey.IsChecked == true, advancedParameters, ct, id =>
            {
                serviceId = id;
                type.IsEnabled = false;
                clearKey.IsEnabled = true;
            });
        serviceId = id;
        type.IsEnabled = false;
        key.Password = "";
        clearKey.IsChecked = false;
        clearKey.IsEnabled = true;
        var list = await client.Services(ct);
        var saved = list.Single(item => item.Id == id);
        key.Header = saved.HasApiKey ? "API Key（已配置，留空保留）" : "API Key（无认证服务可留空）";
        // Keep the user's manually entered model when refreshing the service list.
        updating = true;
        services.ItemsSource = list;
        services.SelectedItem = saved;
        configuredModels.ItemsSource = saved.Models;
        updating = false;
        return id;
    }

    private async Task FetchModelsAsync()
    {
        var fetched = await Run(async (client, ct) =>
        {
            var id = await SaveServiceAsync(client, ct);
            var models = await client.RemoteModels(id, ct);
            remoteModels.ItemsSource = models;
            remoteModels.Visibility = models.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            status.Text = models.Count > 0 ? "已获取模型列表。请选择文本模型，也可手动填写。" : "供应商未返回模型列表。可以手动填写模型名称后保存。";
        });
        if (!fetched && loaded && !session.IsCancellationRequested) status.Text += "\n仍可手动填写模型名称后保存，或跳过此步骤。";
    }

    public async Task<bool> SaveAsync(CancellationToken ct = default)
    {
        var saved = await Run(async (client, token) =>
        {
            var model = modelName.Text.Trim();
            if (model.Length == 0) throw new InvalidDataException("请填写默认文本模型名称，或跳过此步骤稍后配置。");
            var id = await SaveServiceAsync(client, token);
            var service = (await client.Services(token)).Single(item => item.Id == id);
            // Existing model parameters belong to the plugin; onboarding only changes the default choice.
            if (!service.Models.Any(item => item.Name == model))
                await client.SaveModel(id, model, .7, .9, 4096, false, token);
            await client.SetDefaultModel(id, model, token);
            status.Text = "供应商与默认文本模型已保存并回读确认。";
        }, ct);
        if (saved) Saved?.Invoke();
        return saved;
    }

    private async Task<bool> Run(Func<PromptAssistantClient, CancellationToken, Task> operation,
        CancellationToken ct = default, bool connecting = false)
    {
        if (IsWorking || !connecting && !loaded) return false;
        IsWorking = true;
        editorHost.IsEnabled = false;
        WorkingChanged?.Invoke(true);
        status.Style = (Style)Application.Current.Resources["StudioBodyStyle"];
        status.Text = connecting ? "正在加载当前环境的供应商配置…" : "正在保存或读取配置…";
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Token);
        var config = App.Shared.Config.Clone();
        try
        {
            var currentEnvironment = config.ComfyUrl + "|" + config.ComfyRoot;
            if (!connecting && environment != currentEnvironment)
            {
                loaded = false;
                preserveDraftOnReconnect = SameEnvironmentRoot(config.ComfyRoot, loadedRoot);
                key.Password = "";
                throw new InvalidOperationException("已切换 ComfyUI 环境，请重新加载供应商配置。");
            }
            var stats = await ServiceManager.Json(config.ComfyUrl + "/system_stats", cancellation.Token);
            if (stats is null || !ServiceManager.SameRoot(stats.Value, config.ComfyRoot))
            {
                MarkDisconnected(config.ComfyRoot);
                throw new InvalidOperationException("当前端口的 ComfyUI 与所选环境不一致。请重新启动对应环境后加载配置。");
            }
            using var client = new PromptAssistantClient(config.ComfyUrl);
            await client.Connect(cancellation.Token);
            await operation(client, cancellation.Token);
            environment = currentEnvironment;
            loadedRoot = config.ComfyRoot;
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            key.Password = "";
            status.Text = "配置操作已取消；已保存的内容会保留，可重新加载后继续。";
            return false;
        }
        catch (Exception error)
        {
            // A normal validation/readback error keeps the editor ready for a retry.
            // Only a failed local health check changes the primary action to restart.
            if (loaded && error is IOException or TimeoutException)
            {
                try
                {
                    var stats = await ServiceManager.Json(config.ComfyUrl + "/system_stats", cancellation.Token, TimeSpan.FromSeconds(2));
                    if (stats is null || !ServiceManager.SameRoot(stats.Value, config.ComfyRoot)) MarkDisconnected(config.ComfyRoot);
                }
                catch (TimeoutException) { MarkDisconnected(config.ComfyRoot); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { key.Password = ""; }
            }
            status.Style = (Style)Application.Current.Resources["StudioErrorTextStyle"];
            status.Text = preserveDraftOnReconnect && !loaded
                ? "ComfyUI 连接已中断，请点击「启动并配置」重新连接。当前填写内容已保留。"
                : error.Message;
            return false;
        }
        finally
        {
            IsWorking = false;
            editorHost.IsEnabled = loaded;
            WorkingChanged?.Invoke(false);
        }
    }

    private void MarkDisconnected(string root)
    {
        preserveDraftOnReconnect = (loaded || preserveDraftOnReconnect) && SameEnvironmentRoot(root, loadedRoot);
        loaded = false;
    }

    private static bool SameEnvironmentRoot(string root, string? other) => other is not null &&
        Path.TrimEndingDirectorySeparator(root).Equals(Path.TrimEndingDirectorySeparator(other), StringComparison.OrdinalIgnoreCase);

    private static TextBlock BodyText(string text) => new() { Text = text, Style = (Style)Application.Current.Resources["StudioBodyStyle"] };
    private static Button Button(string text, Func<Task> action, string id)
    {
        var button = new Button { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetAutomationId(button, id);
        AutomationProperties.SetName(button, text);
        button.Click += async (_, _) => await action();
        return button;
    }
}
