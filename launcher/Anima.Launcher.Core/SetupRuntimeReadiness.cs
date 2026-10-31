using System.Text.Json;

namespace Anima.Launcher.Core;

public sealed record SetupRuntimeIssue(string Feature, string Message, string ModelId = "");
public sealed record SetupRuntimeReadinessReport(SetupRuntimeIssue[] RequiredIssues, SetupRuntimeIssue[] OptionalIssues)
{
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
        CancellationToken ct, Action<string>? log = null, string? runtimeRoot = null)
    {
        var required = new List<SetupRuntimeIssue>();
        var optional = new List<SetupRuntimeIssue>();
        var requiredModels = profiles.RequiredModels(models, config.SelectedProfile).Select(model => model.Id)
            .Concat(new[] { "seedvr2-model", "seedvr2-vae" }).ToHashSet(StringComparer.Ordinal);
        var selected = selectedModelIds.Concat(requiredModels).Distinct(StringComparer.Ordinal).ToArray();
        async Task<JsonElement?> Read(string endpoint, string feature)
        {
            try { return await ReadAsync(endpoint, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { required.Add(new(feature, "无法读取运行状态：" + ex.Message)); return null; }
        }
        log?.Invoke("正在检查实际节点、模型分类与工作台资源…");
        var stats = await Read(config.ComfyUrl + "/system_stats", "ComfyUI");
        if (stats is not null && !ServiceManager.SameRoot(stats.Value, config.ComfyRoot))
            required.Add(new("ComfyUI身份", "当前端口上的 ComfyUI 与本次安装目录不一致。"));
        var nodes = await Read(config.ComfyUrl + "/object_info", "节点");
        var health = await Read(config.WebUrl + "/api/launcher-health", "工作台身份");
        if (health is not null && !ServiceManager.IsOurWebUi(health, config))
            required.Add(new("工作台身份", "当前端口上的工作台与本次安装目录不一致。"));
        var status = await Read(config.WebUrl + "/api/status", "工作台连接");
        if (status is not null && !LoraManagerClassificationClient.True(status.Value, "online"))
            required.Add(new("工作台连接", "工作台尚未连接本次 ComfyUI。"));
        var resources = await Read(config.WebUrl + "/api/resources?model_profile=" + Uri.EscapeDataString(config.SelectedProfile) + "&refresh=true", "模型分类");
        var configuration = await Read(config.WebUrl + "/api/config", "随机数据");

        // Production installation passes its resolved runtime root. Leaving it null
        // permits pure HTTP contract tests without launching hardware probe processes.
        if (!string.IsNullOrWhiteSpace(runtimeRoot))
        {
            try
            {
                required.AddRange((await SesquiRuntimePreparation.FilesIssuesAsync(config, runtime, ct))
                    .Select(message => new SetupRuntimeIssue("潜空间放大模型", message)));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { required.Add(new("潜空间放大模型", "无法验证潜空间放大权重：" + ex.Message)); }
            try
            {
                var probe = await NvidiaRuntimePreparation.ProbeAsync(config, runtimeRoot, ct);
                if (probe.Supported)
                {
                    var native = await NvidiaRuntimePreparation.ValidateStatusAsync(config, runtimeRoot, ct);
                    if (!NvidiaRuntimePreparation.IsReadyFor(probe, native))
                        required.Add(new("NVIDIA运行库", native?.Detail ?? "当前显卡的 NVIDIA 修复放大运行库尚未完成验证。"));
                    if (nodes is not null)
                    {
                        var nativeIds = new[] { RuntimeCapabilities.NvidiaCapabilityId };
                        foreach (var name in RuntimeCapabilities.RequiredClasses(runtime, nativeIds).Except(RuntimeCapabilities.RequiredClasses(runtime)))
                            if (!nodes.Value.TryGetProperty(name, out _)) required.Add(new("NVIDIA节点", "缺少节点或节点导入失败：" + name));
                        required.AddRange(RuntimeCapabilities.ObjectInfoIssues(nodes.Value, nativeIds).Except(RuntimeCapabilities.ObjectInfoIssues(nodes.Value))
                            .Select(message => new SetupRuntimeIssue("NVIDIA节点", message)));
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { required.Add(new("NVIDIA运行库", "无法验证当前显卡的运行库：" + ex.Message)); }
        }

        if (nodes is not null)
        {
            foreach (var name in RuntimeCapabilities.RequiredClasses(runtime))
                if (!nodes.Value.TryGetProperty(name, out _)) required.Add(new("节点", "缺少节点或节点导入失败：" + name));
            var baselineIssues = RuntimeCapabilities.ObjectInfoIssues(nodes.Value);
            required.AddRange(baselineIssues.Select(message => new SetupRuntimeIssue("节点参数", message)));
            foreach (var id in selected.Where(id => !requiredModels.Contains(id)))
            {
                foreach (var name in RuntimeCapabilities.RequiredClasses(runtime, [id]).Except(RuntimeCapabilities.RequiredClasses(runtime)))
                    if (!nodes.Value.TryGetProperty(name, out _)) optional.Add(new("可选节点", "缺少节点或节点导入失败：" + name, id));
                optional.AddRange(RuntimeCapabilities.ObjectInfoIssues(nodes.Value, [id]).Except(baselineIssues)
                    .Select(message => new SetupRuntimeIssue("可选节点参数", message, id)));
            }
        }
        if (resources is not null)
        {
            ValidateProfile(resources.Value, config.SelectedProfile, required);
            var primaryProfile = profiles.Get(config.SelectedProfile);
            if (nodes is not null)
            {
                var bindings = ServiceManager.ResourceBindings(nodes.Value);
                primaryProfile = primaryProfile with { Seedvr2Model = bindings.GetValueOrDefault(primaryProfile.Seedvr2Model, primaryProfile.Seedvr2Model),
                    Seedvr2Vae = bindings.GetValueOrDefault(primaryProfile.Seedvr2Vae, primaryProfile.Seedvr2Vae) };
            }
            ValidateSeed(resources.Value, primaryProfile, required);
            foreach (var profile in profiles.Profiles.Where(profile => profile.Id != config.SelectedProfile))
            {
                var profileModels = profiles.RequiredModels(models, profile.Id).Select(model => model.Id).ToArray();
                // A shared VAE is not a request to install another generation profile.
                var requested = profileModels.Except(requiredModels).FirstOrDefault(selected.Contains);
                if (requested is null) continue;
                var failures = new List<SetupRuntimeIssue>();
                ValidateProfile(resources.Value, profile.Id, failures);
                optional.AddRange(failures.Select(issue => issue with { ModelId = requested }));
            }
        }
        if (configuration is not null) ValidateCatalog(configuration.Value, required);

        // Model presence is verified in the running process's actual directories,
        // not the pre-install probe or the launcher's own cached rows.
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
                // Forbidden Vision does not register a folder_paths category: its
                // loader uses models_dir/forbidden_vision regardless of extra YAML.
                var modelsDirectory = ResolveModelsDirectory(stats.Value);
                paths["forbidden_vision"] = modelsDirectory is null ? [] : [Path.Combine(modelsDirectory, "forbidden_vision")];
            }
            foreach (var id in selected)
            {
                if (id == RuntimeCapabilities.NvidiaCapabilityId) continue;
                var destination = requiredModels.Contains(id) ? required : optional;
                var model = models.Models.SingleOrDefault(model => model.Id == id);
                if (model is null) { destination.Add(new("模型清单", "安装清单缺少模型：" + id, id)); continue; }
                if (!paths.TryGetValue(model.Category, out var roots) || roots.Length == 0)
                { destination.Add(new("模型目录", "运行中的 ComfyUI 未提供有效模型目录：" + model.Category, id)); continue; }
                try
                {
                    var scanned = await Inventory.Scan(new(config.ComfyRoot, config.Python, paths), new(models.SchemaVersion, [model]), true, ct);
                    if (scanned.Single().Missing) destination.Add(new("模型完整性", model.Name + "：" + scanned.Single().State, id));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { destination.Add(new("模型完整性", model.Name + "：" + ex.Message, id)); }
            }
        }
        if (required.Count == 0)
        {
            try { await ModelMetadata.ConfirmPendingCoreAsync(config, http, ct, log); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { required.Add(new("模型分类", ex.Message)); }
        }
        return new(required.Distinct().ToArray(), optional.Distinct().ToArray());
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
