using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Anima.Launcher.Core;

public enum AssistantMode { Rewrite, Reverse }
public sealed record AssistantModel(string Name, double Temperature, double TopP, int MaxTokens, bool IsDefault);
public sealed record AssistantService(string Id, string Name, string Type, string BaseUrl, bool HasApiKey, bool AdvancedParameters, IReadOnlyList<AssistantModel> Models);
public sealed record AssistantTemplate(string Id, string Name, string Content, string Category);

/// <summary>Local Prompt Assistant API adapter. Raw service responses and keys never leave this class.</summary>
public sealed class PromptAssistantClient : IDisposable
{
    private readonly HttpClient http;
    private readonly string origin;
    private string prefix = "";
    public bool SupportsTemplateOnly { get; private set; }
    public bool SupportsVision { get; private set; }
    public PromptAssistantClient(string comfyUrl, HttpMessageHandler? handler = null)
    {
        var uri = new Uri(comfyUrl);
        if (!uri.IsLoopback || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("提示词助手仅连接本机 ComfyUI。");
        origin = uri.GetLeftPart(UriPartial.Authority);
        http = new HttpClient(handler ?? new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public static string DiscoverPrefix(JsonObject info)
    {
        var module = (info["PromptExpand"] ?? info["ImageCaptionNode"])?["python_module"]?.GetValue<string>() ?? "";
        var match = Regex.Match(module, @"^custom_nodes\.([^./\\]+)(?:\..*)?$");
        if (!match.Success || match.Groups[1].Value is "." or "..")
            throw new InvalidDataException("无法识别提示词助手所属插件，请安装或修复 Prompt Assistant。");
        return "/" + Uri.EscapeDataString(match.Groups[1].Value) + "/api";
    }

    public async Task Connect(CancellationToken ct, AssistantMode mode = AssistantMode.Rewrite)
    {
        var node = ModelType(mode) == "vlm" ? "ImageCaptionNode" : "PromptExpand";
        var info = await Send(HttpMethod.Get, "/object_info/" + node, null, ct);
        SupportsVision = HasInput(info["ImageCaptionNode"], "image") && HasInput(info["ImageCaptionNode"], "vlm_service")
            && HasInput(info["ImageCaptionNode"], "custom_rule") && HasInput(info["ImageCaptionNode"], "custom_rule_content");
        if (mode == AssistantMode.Reverse && info["ImageCaptionNode"] is null)
            info = await Send(HttpMethod.Get, "/object_info/PromptExpand", null, ct);
        prefix = DiscoverPrefix(info);
        SupportsTemplateOnly = HasInput(info["PromptExpand"], "template_only");
    }

    private static bool HasInput(JsonNode? node, string name) =>
        node?["input"]?["optional"]?[name] is not null || node?["input"]?["required"]?[name] is not null;
    private static string ModelType(AssistantMode mode) => mode switch
    {
        AssistantMode.Rewrite => "llm", AssistantMode.Reverse => "vlm",
        _ => throw new InvalidDataException("提示词助手类型无效。"),
    };
    private static string TemplateGroup(AssistantMode mode) => ModelType(mode) == "vlm" ? "vision_prompts" : "expand_prompts";

    private async Task<JsonObject> Send(HttpMethod method, string path, JsonObject? body, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(method, origin + path);
            if (body is not null) request.Content = JsonContent.Create(body);
            using var response = await http.SendAsync(request, ct);
            // Plugin error strings may contain a provider URL or API key. Never
            // surface bodies or request details in launcher logs and dialogs.
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"提示词助手操作失败（HTTP {(int)response.StatusCode}），请检查 ComfyUI 与插件配置。");
            var result = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct)
                ?? throw new InvalidDataException("提示词助手返回了空响应。");
            if (result["success"] is JsonValue success && success.TryGetValue<bool>(out var ok) && !ok)
                throw new InvalidOperationException("提示词助手未完成操作，请检查配置后重试。");
            if (result.ContainsKey("error") && !result.ContainsKey("success"))
                throw new InvalidOperationException("提示词助手未完成操作，请检查配置后重试。");
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("提示词助手响应超时，请确认 ComfyUI 正在运行。"); }
        catch (HttpRequestException) { throw new IOException("无法连接本机 Prompt Assistant，请先启动 ComfyUI。"); }
        catch (System.Text.Json.JsonException) { throw new InvalidDataException("提示词助手响应格式不兼容，请检查插件版本。"); }
    }

    private Task<JsonObject> Api(HttpMethod method, string path, JsonObject? body, CancellationToken ct)
    {
        if (prefix.Length == 0) throw new InvalidOperationException("请先连接提示词助手。");
        return Send(method, prefix + path, body, ct);
    }
    private static string Segment(string value) => Uri.EscapeDataString(value);
    private static string Text(JsonNode? node, string key, string fallback = "") => node?[key] is JsonValue v && v.TryGetValue<string>(out var text) ? text : fallback;
    private static bool Flag(JsonNode? node, string key) => node?[key] is JsonValue v && v.TryGetValue<bool>(out var flag) && flag;
    private static double Number(JsonNode? node, string key, double fallback) => node?[key] is JsonValue v && v.TryGetValue<double>(out var number) && double.IsFinite(number) ? number : fallback;

    public async Task<IReadOnlyList<AssistantService>> Services(CancellationToken ct, AssistantMode mode = AssistantMode.Rewrite)
    {
        var modelKey = ModelType(mode) + "_models";
        var result = await Api(HttpMethod.Get, "/services", null, ct);
        if (result["services"] is not JsonArray services) throw new InvalidDataException("服务列表格式不兼容。");
        return services.OfType<JsonObject>().Select(service => new AssistantService(
            Text(service, "id"), Text(service, "name"), Text(service, "type"), Text(service, "base_url"),
            Flag(service, "has_api_key") || Text(service, "api_key").Length > 0,
            Flag(service, "enable_advanced_params"),
            (service[modelKey] as JsonArray ?? []).OfType<JsonObject>().Select(model => new AssistantModel(
                Text(model, "name"), Number(model, "temperature", .7), Number(model, "top_p", .9),
                (int)Number(model, "max_tokens", 4096), Flag(model, "is_default"))).ToArray()
        )).ToArray();
    }

    public static JsonObject ServicePayload(string name, string baseUrl, string? key, bool clearKey, bool advanced)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('/')) throw new InvalidDataException("请输入服务名称，名称不能包含 /。");
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidDataException("Base URL 必须是无内嵌凭据、查询参数或片段的 HTTP / HTTPS 地址。");
        var body = new JsonObject { ["name"] = name.Trim(), ["base_url"] = baseUrl.Trim().TrimEnd('/'), ["enable_advanced_params"] = advanced };
        if (clearKey) body["api_key"] = "";
        else if (!string.IsNullOrWhiteSpace(key)) body["api_key"] = key.Trim();
        return body;
    }

    public async Task<string> SaveService(string? id, string type, string name, string baseUrl, string? key, bool clearKey, bool advanced, CancellationToken ct,
        Action<string>? serviceIdentified = null)
    {
        var body = ServicePayload(name, baseUrl, key, clearKey, advanced);
        var services = await Services(ct);
        if (services.Any(item => item.Id != id && item.Name == name.Trim()))
            throw new InvalidDataException("服务名称已存在，请使用不同名称，避免工作台无法区分服务。");
        if (id is not null && !services.Any(item => item.Id == id))
            throw new InvalidDataException("服务已被删除，请刷新列表。");
        if (id is null)
        {
            if (type is not ("openai_compatible" or "ollama")) throw new InvalidDataException("请选择 OpenAI 兼容或 Ollama 服务。");
            body["type"] = type;
            var created = await Api(HttpMethod.Post, "/services", body, ct);
            id = Text(created, "service_id");
            if (id.Length == 0) throw new InvalidDataException("插件未返回新增服务 ID；请刷新列表确认。");
            // Retain the identity even if a later parameter write/readback fails.
            // Retrying a wizard save must edit this service rather than create another.
            serviceIdentified?.Invoke(id);
            // The plugin create endpoint does not accept advanced parameters.
            await Api(HttpMethod.Put, "/services/" + Segment(id), new JsonObject { ["enable_advanced_params"] = advanced }, ct);
        }
        else await Api(HttpMethod.Put, "/services/" + Segment(id), body, ct);
        var saved = (await Services(ct)).SingleOrDefault(item => item.Id == id);
        if (saved is null || saved.Name != body["name"]!.GetValue<string>() || saved.BaseUrl.TrimEnd('/') != body["base_url"]!.GetValue<string>() || saved.AdvancedParameters != advanced)
            throw new IOException("服务已提交但回读不一致，请刷新列表后重试。");
        return id;
    }

    public async Task DeleteService(string id, CancellationToken ct)
    {
        await Api(HttpMethod.Delete, "/services/" + Segment(id), null, ct);
        if ((await Services(ct)).Any(item => item.Id == id)) throw new IOException("服务删除后仍存在，请刷新列表。");
    }

    public async Task<IReadOnlyList<string>> RemoteModels(string id, CancellationToken ct, AssistantMode mode = AssistantMode.Rewrite)
    {
        var modelType = ModelType(mode);
        var response = await Api(HttpMethod.Get, "/services/" + Segment(id) + "/models", null, ct);
        var models = response["models"]?[modelType] as JsonArray ?? [];
        return models.Select(item => item is JsonValue v && v.TryGetValue<string>(out var name) ? name : Text(item, "id", Text(item, "name")))
            .Where(name => name.Length > 0).Distinct().ToArray();
    }

    public async Task SaveModel(string id, string name, double temperature, double topP, int maxTokens, bool existing, CancellationToken ct, AssistantMode mode = AssistantMode.Rewrite)
    {
        var modelType = ModelType(mode);
        name = name.Trim();
        if (name.Length == 0 || !double.IsFinite(temperature) || temperature is < 0 or > 2 || !double.IsFinite(topP) || topP is < 0 or > 1 || maxTokens <= 0)
            throw new InvalidDataException("请填写模型名，Temperature 0–2、Top P 0–1 与大于 0 的 Max Tokens。");
        var path = "/services/" + Segment(id) + "/models";
        if (!existing)
            await Api(HttpMethod.Post, path, new JsonObject { ["model_type"] = modelType, ["model_name"] = name, ["temperature"] = temperature, ["top_p"] = topP, ["max_tokens"] = maxTokens }, ct);
        else
            foreach (var (parameter, value) in new (string, JsonNode)[] { ("temperature", JsonValue.Create(temperature)!), ("top_p", JsonValue.Create(topP)!), ("max_tokens", JsonValue.Create(maxTokens)!) })
                await Api(HttpMethod.Put, path + "/parameter", new JsonObject { ["model_type"] = modelType, ["model_name"] = name, ["parameter_name"] = parameter, ["parameter_value"] = value }, ct);
        var model = (await Services(ct, mode)).SingleOrDefault(item => item.Id == id)?.Models.SingleOrDefault(item => item.Name == name);
        if (model is null || model.Temperature != temperature || model.TopP != topP || model.MaxTokens != maxTokens)
            throw new IOException("模型参数已提交但回读不一致，请刷新后重试。");
    }

    public async Task DeleteModel(string id, string name, CancellationToken ct, AssistantMode mode = AssistantMode.Rewrite)
    {
        await Api(HttpMethod.Delete, "/services/" + Segment(id) + "/models/" + ModelType(mode) + "/" + Segment(name), null, ct);
        if ((await Services(ct, mode)).SingleOrDefault(item => item.Id == id)?.Models.Any(item => item.Name == name) == true)
            throw new IOException("模型删除后仍存在，请刷新列表。");
    }

    public async Task SetDefaultModel(string id, string name, CancellationToken ct, AssistantMode mode = AssistantMode.Rewrite)
    {
        var modelType = ModelType(mode);
        name = name.Trim();
        if (name.Length == 0) throw new InvalidDataException("请选择要设为默认的模型。");
        var service = (await Services(ct, mode)).SingleOrDefault(item => item.Id == id);
        if (service is null || !service.Models.Any(model => model.Name == name))
            throw new InvalidDataException("服务或模型已被删除，请刷新后重试。");
        await Api(HttpMethod.Put, "/services/" + Segment(id) + "/models/default",
            new JsonObject { ["model_type"] = modelType, ["model_name"] = name }, ct);
        var defaults = (await Services(ct, mode)).SingleOrDefault(item => item.Id == id)?.Models.Where(model => model.IsDefault).ToArray();
        if (defaults is null || defaults.Length != 1 || defaults[0].Name != name)
            throw new IOException("默认模型已提交但回读不一致，请刷新后重试。");
    }

    public async Task<IReadOnlyList<AssistantTemplate>> Templates(CancellationToken ct, AssistantMode mode = AssistantMode.Rewrite) => ProjectTemplates(await TemplateDocument(ct), mode);
    private Task<JsonObject> TemplateDocument(CancellationToken ct) => Api(HttpMethod.Get, "/config/system_prompts", null, ct);
    public static IReadOnlyList<AssistantTemplate> ProjectTemplates(JsonObject document, AssistantMode mode = AssistantMode.Rewrite) =>
        (document[TemplateGroup(mode)] as JsonObject ?? []).Where(pair => pair.Value is JsonObject).Select(pair =>
            new AssistantTemplate(pair.Key, Text(pair.Value, "name", pair.Key), Text(pair.Value, "content"), Text(pair.Value, "category"))).ToArray();

    public static JsonObject MergeTemplate(JsonObject latest, string id, string? name, string? content, string category = "", bool create = false, AssistantMode mode = AssistantMode.Rewrite)
    {
        var group = TemplateGroup(mode);
        var document = (JsonObject)latest.DeepClone();
        if (document[group] is not JsonObject entries) throw new InvalidDataException("系统提示词文档缺少 " + group + "。");
        if (create && entries.ContainsKey(id)) throw new InvalidDataException("新模板 ID 已存在，请刷新重试。");
        if (!create && entries[id] is not JsonObject) throw new InvalidDataException("模板已被删除，请刷新列表。");
        if (name is null)
        {
            if ((document["active_prompts"] as JsonObject ?? []).Any(pair => pair.Value is JsonValue value && value.TryGetValue<string>(out var active) && active == id))
                throw new InvalidOperationException("此模板是插件当前默认模板，请先在 Prompt Assistant 中更换默认模板后再删除。");
            entries.Remove(id);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(content)) throw new InvalidDataException("模板名称与系统提示词不能为空。");
            var entry = entries[id] as JsonObject ?? new JsonObject { ["showIn"] = new JsonArray("node"), ["tags"] = new JsonArray() };
            entry["name"] = name.Trim();
            entry["content"] = content;
            entry["role"] = "system";
            entry["category"] = category.Trim();
            if (entries[id] is null) entries[id] = entry;
        }
        return document;
    }

    public async Task<string> SaveTemplate(string? id, string name, string content, string category, CancellationToken ct, AssistantTemplate? expected = null, AssistantMode mode = AssistantMode.Rewrite)
    {
        var group = TemplateGroup(mode);
        var create = id is null;
        id ??= "studio_" + Guid.NewGuid().ToString("N");
        var latest = await TemplateDocument(ct);
        if (expected is not null && ProjectTemplates(latest, mode).SingleOrDefault(item => item.Id == id) != expected)
            throw new InvalidOperationException("此模板已被其他页面修改，请刷新并重新编辑，避免覆盖新内容。");
        var merged = MergeTemplate(latest, id, name, content, category, create, mode);
        await Api(HttpMethod.Post, "/config/system_prompts", merged, ct);
        var saved = await TemplateDocument(ct);
        if (!JsonNode.DeepEquals(saved[group]?[id], merged[group]?[id]))
            throw new IOException("模板已提交但回读不一致，请刷新列表确认。");
        return id;
    }

    public async Task DeleteTemplate(string id, CancellationToken ct, AssistantMode mode = AssistantMode.Rewrite)
    {
        var group = TemplateGroup(mode);
        var latest = await TemplateDocument(ct);
        await Api(HttpMethod.Post, "/config/system_prompts", MergeTemplate(latest, id, null, null, mode: mode), ct);
        if ((await TemplateDocument(ct))[group]?[id] is not null) throw new IOException("模板删除后仍存在，请刷新列表。");
    }

    public async Task<string> CopyTemplate(string sourceId, CancellationToken ct, AssistantMode mode = AssistantMode.Rewrite)
    {
        var group = TemplateGroup(mode);
        var latest = await TemplateDocument(ct);
        if (latest[group] is not JsonObject entries || entries[sourceId] is not JsonObject source)
            throw new InvalidDataException("原模板已被删除，请刷新列表。");
        var id = "studio_" + Guid.NewGuid().ToString("N");
        var copy = (JsonObject)source.DeepClone();
        copy["name"] = Text(source, "name", sourceId) + " 副本";
        entries[id] = copy;
        await Api(HttpMethod.Post, "/config/system_prompts", latest, ct);
        if (!JsonNode.DeepEquals((await TemplateDocument(ct))[group]?[id], copy))
            throw new IOException("模板复制后回读不一致，请刷新列表确认。");
        return id;
    }
    public void Dispose() => http.Dispose();
}
