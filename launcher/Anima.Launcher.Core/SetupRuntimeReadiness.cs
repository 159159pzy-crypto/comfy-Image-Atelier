using System.Text.Json;
using System.Net.Http.Json;

namespace Anima.Launcher.Core;

public sealed record SetupRuntimeIssue(string Feature, string Message, string ModelId = "");
public sealed record SetupRuntimeReadinessReport(SetupRuntimeIssue[] RequiredIssues, SetupRuntimeIssue[] OptionalIssues)
{
    public SetupRuntimeIssue[] ConfigurationIssues { get; init; } = [];
    public bool RequiredReady => RequiredIssues.Length == 0;
    public bool Ready => RequiredReady && OptionalIssues.Length == 0;
}

/// <summary>Installation-only verification against existing ComfyUI, Manager and workbench APIs.</summary>
public sealed class SetupRuntimeReadiness : IDisposable
{
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    public SetupRuntimeReadiness(HttpClient? http = null)
    {
        this.http = http ?? new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(45) };
        ownsHttp = http is null;
    }

    public async Task<SetupRuntimeReadinessReport> CheckAsync(LauncherConfig config, RuntimeManifest runtime,
        ModelManifest models, ModelProfilesManifest profiles, IEnumerable<string> selectedModelIds,
        CancellationToken ct, Action<string>? log = null, string? runtimeRoot = null, IEnumerable<string>? features = null)
    {
        var required = new List<SetupRuntimeIssue>();
        var optional = new List<SetupRuntimeIssue>();
        var configurationIssues = new List<SetupRuntimeIssue>();
        var selected = selectedModelIds.Where(id => !SetupFeatures.IsCapability(id)).Distinct(StringComparer.Ordinal).ToArray();
        var effectiveFeatures = (features ?? SetupFeatures.Resolve(false, selectedModelIds, config.SelectedProfile)).ToHashSet(StringComparer.Ordinal);
        async Task<JsonElement?> Read(string endpoint, string feature)
        {
            try { return await ReadAsync(endpoint, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { required.Add(new(feature, "无法读取运行状态：" + ex.Message)); return null; }
        }
        log?.Invoke("正在检查环境连接与本次选择的功能…");
        var stats = await Read(config.ComfyUrl + "/system_stats", "ComfyUI");
        if (stats is not null && !ServiceManager.SameRoot(stats.Value, config.ComfyRoot))
            required.Add(new("ComfyUI身份", "当前端口上的 ComfyUI 与本次安装目录不一致。"));
        var health = await Read(config.WebUrl + "/api/launcher-health", "工作台身份");
        if (health is not null && !ServiceManager.IsOurWebUi(health, config))
            required.Add(new("工作台身份", "当前端口上的工作台与本次安装目录不一致。"));
        var status = await Read(config.WebUrl + "/api/status", "工作台连接");
        if (status is not null && !LoraManagerClassificationClient.True(status.Value, "online"))
            required.Add(new("工作台连接", "工作台尚未连接本次 ComfyUI。"));
        var configuration = await Read(config.WebUrl + "/api/config", "随机数据");
        if (configuration is not null) ValidateCatalog(configuration.Value, required);
        if (effectiveFeatures.Overlaps(["prompt-assistant", "tagger", "krea2"]))
        {
            var nodes = await Read(config.ComfyUrl + "/object_info", "节点");
            if (nodes is not null)
                foreach (var node in SetupFeatures.SelectRuntime(runtime, effectiveFeatures).Nodes
                    .Where(node => SetupFeatures.NodeFeature(node.Id) is "prompt-assistant" or "tagger" or "krea2"))
                    foreach (var name in node.RequiredClasses.Concat(node.Id == "ComfyUI-WD-Timm-Tagger" ? new[] { "WDTimmTagger" } : []))
                        if (!nodes.Value.TryGetProperty(name, out _))
                            optional.Add(new("可选节点", "所选功能缺少节点：" + name, SetupFeatures.Prefix + SetupFeatures.NodeFeature(node.Id)));
        }
        // Runtime compatibility belongs to the same resolver used before generation.
        // A missing model does not prevent entering the workbench to configure it.
        try
        {
            var stages = new Dictionary<string, object>();
            foreach (var (stage, feature) in new[] { ("pixel_hires", "pixel_hires"), ("latent", "latent"),
                ("post_sampler", "post_sampler"), ("face_refine", "face-refine"), ("seedvr2", "seedvr2"),
                ("nvidia", "nvidia"), ("dlss", "dlss") })
                stages[stage] = new { enabled = effectiveFeatures.Contains(feature) };
            using var response = await http.PostAsJsonAsync(config.WebUrl + "/api/resources/resolve", new
            {
                settings = new { pipeline_version = 5, model_profile = config.SelectedProfile,
                    base_mode = effectiveFeatures.Contains("sesqui") ? "dual_sampler" : "normal", loras = Array.Empty<object>(), upscalers = stages }
            }, ct);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var result = document.RootElement;
            if (!result.TryGetProperty("version", out var version) || version.GetInt32() != 1
                || !result.TryGetProperty("issues", out var issues) || issues.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("工作台资源检查接口不兼容，请更新工作台后重试。");
            foreach (var issue in issues.EnumerateArray())
            {
                var module = issue.TryGetProperty("module", out var moduleValue) ? moduleValue.GetString() ?? "base" : "base";
                var message = issue.TryGetProperty("message", out var messageValue) ? messageValue.GetString() ?? "资源待配置" : "资源待配置";
                var feature = module == "face_refine" ? "face-refine" : module;
                var modelId = feature switch
                {
                    "seedvr2" => selected.FirstOrDefault(id => id.StartsWith("seedvr2-", StringComparison.Ordinal)) ?? "",
                    "face-refine" => selected.FirstOrDefault(RuntimeCapabilities.IsFaceModel) ?? "",
                    "nvidia" or "dlss" => SetupFeatures.Prefix + feature,
                    _ => ""
                };
                var item = new SetupRuntimeIssue(module, message, modelId);
                if (modelId.Length > 0 && effectiveFeatures.Contains(feature)) optional.Add(item);
                else configurationIssues.Add(item);
            }
            if (result.TryGetProperty("replacements", out var replacements) && replacements.ValueKind == JsonValueKind.Array)
                foreach (var item in replacements.EnumerateArray())
                    log?.Invoke("已使用兼容资源：" + item.GetProperty("from").GetString() + " → " + item.GetProperty("to").GetString());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { required.Add(new("资源检查", "无法检查实际兼容资源：" + ex.Message)); }

        // Only explicitly selected official downloads are verified against their catalog.
        // Compatible alternatives are never represented as a successful official download.
        if (selected.Length > 0)
        {
            var folders = await Read(config.ComfyUrl + "/internal/folder_paths", "模型目录");
            if (folders is not null)
            {
                var paths = new Dictionary<string, string[]>(StringComparer.Ordinal);
                foreach (var property in folders.Value.EnumerateObject())
                    if (property.Value.ValueKind == JsonValueKind.Array)
                        paths[property.Name] = property.Value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                            .Select(item => item.GetString()!).Where(Path.IsPathFullyQualified).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (stats is not null)
                {
                    var tagger = TaggerRuntime.ResolveDirectory(folders.Value, stats.Value);
                    paths["wd_taggers"] = tagger is null ? [] : [tagger];
                    var directory = ResolveModelsDirectory(stats.Value);
                    paths["forbidden_vision"] = directory is null ? [] : [Path.Combine(directory, "forbidden_vision")];
                }
                foreach (var id in selected)
                {
                    var model = models.Models.SingleOrDefault(model => model.Id == id);
                    if (model is null) { optional.Add(new("模型清单", "安装清单缺少所选下载：" + id, id)); continue; }
                    if (!paths.TryGetValue(model.Category, out var roots) || roots.Length == 0)
                    { optional.Add(new("模型目录", "所选下载的目录尚未就绪：" + model.Category, id)); continue; }
                    try
                    {
                        var scanned = await Inventory.Scan(new(config.ComfyRoot, config.Python, paths), new(models.SchemaVersion, [model]), true, ct);
                        if (scanned.Single().Missing) optional.Add(new("模型完整性", model.Name + "：" + scanned.Single().State, id));
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex) { optional.Add(new("模型完整性", model.Name + "：" + ex.Message, id)); }
                }
            }
        }
        if (required.Count == 0)
        {
            try { await ModelMetadata.ConfirmPendingCoreAsync(config, http, ct, log); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { configurationIssues.Add(new("模型分类", ex.Message)); }
        }
        return new(required.Distinct().ToArray(), optional.Distinct().ToArray()) { ConfigurationIssues = configurationIssues.Distinct().ToArray() };
    }

    internal static void ValidateProfile(JsonElement resources, string id, List<SetupRuntimeIssue> issues)
    {
        if (!resources.TryGetProperty("model_profiles", out var profiles) || !profiles.TryGetProperty(id, out var profile)
            || !profile.TryGetProperty("available", out var available))
        { issues.Add(new("生图模型", "工作台未返回 " + id + " 档案的可用性。")); return; }
        foreach (var name in new[] { "model", "clip", "vae" })
            if (!LoraManagerClassificationClient.True(available, name)) issues.Add(new("生图模型", id + " 的 " + name + " 尚未在工作台就绪。"));
    }
    internal static void ValidateSeed(JsonElement resources, ModelProfileDefinition profile, List<SetupRuntimeIssue> issues)
    {
        if (!ContainsResource(resources, "seedvr2_models", profile.Seedvr2Model)) issues.Add(new("SeedVR2", "SeedVR2 修复模型未出现在运行清单中。", "seedvr2-model"));
        if (!ContainsResource(resources, "seedvr2_vaes", profile.Seedvr2Vae)) issues.Add(new("SeedVR2", "SeedVR2 专用 VAE 未出现在运行清单中。", "seedvr2-vae"));
    }
    internal static bool ContainsResource(JsonElement resources, string key, string name)
    {
        if (!resources.TryGetProperty(key, out var choices) || choices.ValueKind != JsonValueKind.Array) return false;
        var values = choices.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString()!.Replace('\\', '/')).ToArray();
        name = name.Replace('\\', '/');
        return values.Contains(name, StringComparer.OrdinalIgnoreCase)
            || values.Count(value => Path.GetFileName(value).Equals(Path.GetFileName(name), StringComparison.OrdinalIgnoreCase)) == 1;
    }
    internal static void ValidateCatalog(JsonElement configuration, List<SetupRuntimeIssue> issues)
    {
        if (!configuration.TryGetProperty("catalog", out var catalog) || !LoraManagerClassificationClient.True(catalog, "available")
            || !catalog.TryGetProperty("counts", out var counts) || counts.ValueKind != JsonValueKind.Object)
        { issues.Add(new("随机数据", "Anima Tools 随机数据尚未被工作台读取。")); return; }
        if (catalog.TryGetProperty("discovery", out var discovery)
            && LoraManagerClassificationClient.Text(discovery, "status") != "available")
            issues.Add(new("随机数据", "Anima Tools 数据目录检查失败：" + LoraManagerClassificationClient.Text(discovery, "message")));
        // Custom items contribute to counts too. An imported custom pool must not
        // hide a failed original data file. Other optional startup warnings remain
        // visible in the workbench without blocking the required runtime here.
        if (configuration.TryGetProperty("warnings", out var warnings) && warnings.ValueKind == JsonValueKind.Array)
            foreach (var warning in warnings.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!))
                if (warning.StartsWith("提示词数据文件无法读取:", StringComparison.Ordinal)
                    && AnimaToolsDiscovery.DataFiles.Any(file => warning.Contains(file, StringComparison.OrdinalIgnoreCase)))
                    issues.Add(new("随机数据", warning));
        foreach (var section in new[] { "character", "clothing", "pose", "background", "expression" })
            if (!counts.TryGetProperty(section, out var count) || count.ValueKind != JsonValueKind.Number || !count.TryGetInt32(out var size) || size <= 0)
                issues.Add(new("随机数据", "Anima Tools 数据为空：" + section));
    }
    internal static string? ResolveModelsDirectory(JsonElement stats)
    {
        try
        {
            var args = stats.GetProperty("system").GetProperty("argv").EnumerateArray().Select(item => item.GetString()).ToArray();
            if (args.Length == 0 || args.Any(value => value is null)) return null;
            var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < args.Length; index++)
            {
                var parts = args[index]!.Split('=', 2);
                if (parts[0] is not ("--base-directory" or "--models-directory")) continue;
                var value = parts.Length == 2 ? parts[1] : args.ElementAtOrDefault(index + 1);
                if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value) || overrides.ContainsKey(parts[0])) return null;
                overrides.Add(parts[0], Path.GetFullPath(value));
            }
            if (overrides.TryGetValue("--models-directory", out var models)) return models;
            if (overrides.TryGetValue("--base-directory", out var root)) return Path.Combine(root, "models");
            return Path.IsPathFullyQualified(args[0]!) && Path.GetExtension(args[0]) == ".py"
                ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[0]!))!, "models") : null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException) { return null; }
    }
    private async Task<JsonElement> ReadAsync(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("接口未返回有效 JSON 对象。");
        return document.RootElement.Clone();
    }
    public void Dispose() { if (ownsHttp) http.Dispose(); }
}
