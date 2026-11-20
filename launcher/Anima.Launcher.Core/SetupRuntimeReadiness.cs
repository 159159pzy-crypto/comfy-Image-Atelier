using System.Text.Json;
using System.Net.Http.Json;

namespace Anima.Launcher.Core;

public sealed record SetupRuntimeIssue(string Feature, string Message, string ModelId = "");
public sealed record SetupRuntimeReadinessReport(SetupRuntimeIssue[] RequiredIssues, SetupRuntimeIssue[] OptionalIssues)
{
    public SetupRuntimeIssue[] ConfigurationIssues { get; init; } = [];
    public bool PromptAssistantAvailable { get; init; }
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
        CancellationToken ct, Action<string>? log = null, string? runtimeRoot = null, IEnumerable<string>? features = null,
        IEnumerable<string>? requiredFeatures = null)
    {
        var required = new List<SetupRuntimeIssue>();
        var optional = new List<SetupRuntimeIssue>();
        var configurationIssues = new List<SetupRuntimeIssue>();
        var selected = selectedModelIds.Where(id => !SetupFeatures.IsCapability(id)).Distinct(StringComparer.Ordinal).ToArray();
        var effectiveFeatures = (features ?? SetupFeatures.Resolve(false, selectedModelIds, config.SelectedProfile)).ToHashSet(StringComparer.Ordinal);
        var mandatoryFeatures = (requiredFeatures ?? []).ToHashSet(StringComparer.Ordinal);
        var selectedModels = models.Models.Where(model => selected.Contains(model.Id)).ToArray();
        string DescribeFailure(string context, Exception error)
        {
            log?.Invoke(context + "：" + DiagnosticRedaction.Text(error.Message));
            return error switch
            {
                OperationCanceledException or TimeoutException => context + "超时，请查看服务日志后重试。",
                HttpRequestException => context + "失败，请确认服务连接并查看日志。",
                _ when error.Message.Any(ch => ch is >= '\u4e00' and <= '\u9fff') => context + "：" + error.Message,
                _ => context + "失败，具体原因已记录到服务日志。",
            };
        }
        void FeatureIssue(string feature, string message, string modelId)
        {
            var item = new SetupRuntimeIssue(feature, message, modelId);
            if (mandatoryFeatures.Contains(feature)) required.Add(item); else optional.Add(item);
        }
        async Task<JsonElement?> Read(string endpoint, string feature)
        {
            try { return await ReadAsync(endpoint, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { required.Add(new(feature, DescribeFailure(feature + "运行状态检查", ex))); return null; }
        }
        log?.Invoke("正在检查环境连接与本次选择的功能…");
        var stats = await Read(config.ComfyUrl + "/system_stats", "ComfyUI");
        if (stats is not null && !(config.ReadOnlyConnection
            ? await ServiceManager.CheckComfyReuseAsync(config, ct) : ServiceManager.SameRoot(stats.Value, config.ComfyRoot)))
            required.Add(new("ComfyUI身份", "当前端口上的 ComfyUI 与本次安装目录不一致。"));
        var health = await Read(config.WebUrl + "/api/launcher-health", "工作台身份");
        if (health is not null && !ServiceManager.IsOurWebUi(health, config))
            required.Add(new("工作台身份", "当前端口上的工作台与本次安装目录不一致。"));
        var status = await Read(config.WebUrl + "/api/status", "工作台连接");
        if (status is not null && !LoraManagerClassificationClient.True(status.Value, "online"))
            required.Add(new("工作台连接", "工作台尚未连接本次 ComfyUI。"));
        var configuration = await Read(config.WebUrl + "/api/config", "随机数据");
        if (configuration is not null) ValidateCatalog(configuration.Value, config.ReadOnlyConnection ? configurationIssues : required);
        var nodes = await Read(config.ComfyUrl + "/object_info", "节点");
        if (effectiveFeatures.Overlaps(["prompt-assistant", "tagger", "krea2", "seedvr2", "pixel_hires"]))
        {
            if (nodes is not null)
                foreach (var node in SetupFeatures.SelectRuntime(runtime, effectiveFeatures).Nodes
                    .Where(node => SetupFeatures.NodeFeature(node.Id) is "prompt-assistant" or "tagger" or "krea2"))
                    foreach (var name in node.RequiredClasses.Concat(node.Id == "ComfyUI-WD-Timm-Tagger" ? new[] { "WDTimmTagger" } : []))
                        if (!nodes.Value.TryGetProperty(name, out _))
                            FeatureIssue(SetupFeatures.NodeFeature(node.Id)!, "所选功能缺少节点：" + name, SetupFeatures.Prefix + SetupFeatures.NodeFeature(node.Id));
        }
        // Runtime compatibility belongs to the same resolver used before generation.
        // A missing model does not prevent entering the workbench to configure it.
        try
        {
            // Every selected variant is checked explicitly. A usable 3B must not
            // make an unrecognized 7B download appear ready (or vice versa).
            var variants = new List<Dictionary<string, ModelItem>> { new() };
            foreach (var group in new[] {
                (Field: "upscalers.seedvr2.model_name", Models: selectedModels.Where(m => m.Id.StartsWith("seedvr2-", StringComparison.Ordinal) && m.Category == "diffusion_models").ToArray()),
                (Field: "upscalers.pixel_hires.model_name", Models: selectedModels.Where(m => m.Category == "upscale_models").ToArray()) })
                if (group.Models.Length > 0)
                    variants = variants.SelectMany(variant => group.Models.Select(model => new Dictionary<string, ModelItem>(variant) { [group.Field] = model })).ToList();
            var selectedVae = selectedModels.FirstOrDefault(m => m.Id == "seedvr2-vae");
            var selectedProfiles = profiles.Profiles.Where(profile => selectedModels.Any(model =>
                model.Category == "diffusion_models" && model.RelativePath == profile.ModelName
                || model.Category == "text_encoders" && model.RelativePath == profile.ClipName)).ToArray();
            if (selectedProfiles.Length == 0) selectedProfiles = [profiles.Get(config.SelectedProfile)];
            foreach (var validationProfile in selectedProfiles)
            foreach (var selectedVariant in variants)
            {
                var variant = new Dictionary<string, ModelItem>(selectedVariant);
                foreach (var binding in new[] { (Field: "model_name", Name: validationProfile.ModelName, Category: "diffusion_models"),
                    (Field: "clip_name", Name: validationProfile.ClipName, Category: "text_encoders"),
                    (Field: "vae_name", Name: validationProfile.VaeName, Category: "vae") })
                    if (selectedModels.FirstOrDefault(model => model.Category == binding.Category && model.RelativePath == binding.Name) is { } model)
                        variant[binding.Field] = model;
                if (selectedVae is not null) variant["upscalers.seedvr2.vae_name"] = selectedVae;
                var expected = variant.ToDictionary(item => item.Key, item => ResolveModelName(nodes, item.Value));
                var stages = new Dictionary<string, object>();
                foreach (var (stage, feature) in new[] { ("pixel_hires", "pixel_hires"), ("latent", "latent"),
                    ("post_sampler", "post_sampler"), ("face_refine", "face-refine"), ("seedvr2", "seedvr2"),
                    ("nvidia", "nvidia"), ("dlss", "dlss") })
                    stages[stage] = new Dictionary<string, object> { ["enabled"] = effectiveFeatures.Contains(feature) };
                foreach (var binding in expected.Where(binding => binding.Key.StartsWith("upscalers.", StringComparison.Ordinal)))
                {
                    var parts = binding.Key.Split('.');
                    ((Dictionary<string, object>)stages[parts[1]])[parts[2]] = binding.Value;
                }
                var settings = new Dictionary<string, object>
                {
                    ["pipeline_version"] = 5, ["model_profile"] = validationProfile.Id,
                    ["base_mode"] = effectiveFeatures.Contains("sesqui") ? "dual_sampler" : "normal",
                    ["loras"] = Array.Empty<object>(), ["upscalers"] = stages
                };
                foreach (var binding in expected.Where(binding => !binding.Key.Contains('.'))) settings[binding.Key] = binding.Value;
                using var response = await http.PostAsJsonAsync(config.WebUrl + "/api/resources/resolve", new { settings }, ct);
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
                    var code = issue.TryGetProperty("code", out var codeValue) ? codeValue.GetString() ?? "" : "";
                    var feature = module switch { "face_refine" => "face-refine", "base_dual" => "sesqui", _ => module };
                    var modelId = feature switch
                    {
                        "seedvr2" => variant.GetValueOrDefault("upscalers.seedvr2.model_name")?.Id ?? selectedVae?.Id ?? SetupFeatures.Prefix + "seedvr2",
                        "pixel_hires" => variant.GetValueOrDefault("upscalers.pixel_hires.model_name")?.Id ?? SetupFeatures.Prefix + "pixel_hires",
                        "face-refine" => selected.FirstOrDefault(RuntimeCapabilities.IsFaceModel) ?? "",
                        "nvidia" or "dlss" or "sesqui" => SetupFeatures.Prefix + feature,
                        _ => ""
                    };
                    var item = new SetupRuntimeIssue(module, message, modelId);
                    if (config.ReadOnlyConnection && module == "base") configurationIssues.Add(item);
                    else if (mandatoryFeatures.Contains(feature)) required.Add(item);
                    else if (effectiveFeatures.Contains(feature)) FeatureIssue(feature, message, modelId);
                    else if (module == "base" && code is "missing" or "missing_model" or "unknown_compatibility" or "incompatible_model" or "ambiguous_model" or "ambiguous_path") configurationIssues.Add(item);
                    else if (config.ReadOnlyConnection) configurationIssues.Add(item);
                    else required.Add(item);
                }
                foreach (var binding in expected)
                {
                    var feature = !binding.Key.Contains('.') ? "base" : binding.Key.Contains(".seedvr2.", StringComparison.Ordinal) ? "seedvr2" : "pixel_hires";
                    if (feature != "base" && !effectiveFeatures.Contains(feature)) continue;
                    var resolved = result.TryGetProperty("effective_settings", out var effective) ? ReadSetting(effective, binding.Key) : null;
                    if (!string.Equals(resolved?.Replace('\\', '/'), binding.Value.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                        FeatureIssue(feature, "所选模型尚未被运行环境正确识别：" + binding.Value, variant[binding.Key].Id);
                }
                if (result.TryGetProperty("replacements", out var replacements) && replacements.ValueKind == JsonValueKind.Array)
                    foreach (var item in replacements.EnumerateArray())
                        log?.Invoke("已使用兼容资源：" + item.GetProperty("from").GetString() + " → " + item.GetProperty("to").GetString());
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { required.Add(new("资源检查", DescribeFailure("实际兼容资源检查", ex))); }

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
                    catch (Exception ex) { optional.Add(new("模型完整性", DescribeFailure(model.Name + "完整性检查", ex), id)); }
                }
            }
        }
        if (required.Count == 0 && !config.ReadOnlyConnection)
        {
            try { await ModelMetadata.ConfirmPendingCoreAsync(config, http, ct, log); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { configurationIssues.Add(new("模型分类", DescribeFailure("模型分类检查", ex))); }
        }
        return new(required.Distinct().ToArray(), optional.Distinct().ToArray())
        {
            ConfigurationIssues = configurationIssues.Distinct().ToArray(),
            PromptAssistantAvailable = nodes is { } catalog && catalog.TryGetProperty("PromptExpand", out _)
        };
    }

    private static string? ReadSetting(JsonElement value, string path)
    {
        foreach (var part in path.Split('.'))
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(part, out value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static string ResolveModelName(JsonElement? nodes, ModelItem model)
    {
        var (nodeName, inputName) = model.Category switch
        {
            "vae" => ("VAELoader", "vae_name"),
            "text_encoders" => ("CLIPLoader", "clip_name"),
            "upscale_models" => ("UpscaleModelLoader", "model_name"),
            _ => ("UNETLoader", "unet_name")
        };
        if (nodes is not { } info || !info.TryGetProperty(nodeName, out var node)
            || !node.TryGetProperty("input", out var input) || !input.TryGetProperty("required", out var fields)
            || !fields.TryGetProperty(inputName, out var schema) || schema.ValueKind != JsonValueKind.Array || schema.GetArrayLength() == 0)
            return model.RelativePath;
        var choices = schema[0];
        if (choices.ValueKind != JsonValueKind.Array && !(schema.GetArrayLength() > 1 && schema[1].ValueKind == JsonValueKind.Object && schema[1].TryGetProperty("options", out choices)))
            return model.RelativePath;
        if (choices.ValueKind != JsonValueKind.Array) return model.RelativePath;
        var values = choices.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!.Replace('\\', '/')).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var name in new[] { model.RelativePath }.Concat(model.Aliases))
        {
            var exact = values.FirstOrDefault(value => value.Equals(name.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
            var matches = values.Where(value => Path.GetFileName(value).Equals(Path.GetFileName(name), StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 1) return matches[0];
            if (matches.Length > 1) return model.RelativePath;
        }
        return model.RelativePath;
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
