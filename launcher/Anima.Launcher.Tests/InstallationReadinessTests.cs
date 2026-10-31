using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Anima.Launcher.Core;

internal static class InstallationReadinessTests
{
    public static async Task<int> Run(string work, RuntimeManifest runtime)
    {
        var count = 0;
        void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); count++; Console.WriteLine("PASS: " + label); }
        async Task Fails(Func<Task> action, string label)
        {
            try { await action(); }
            catch (Exception ex) when (ex is InvalidDataException or HttpRequestException or TimeoutException)
            { Check(true, label); return; }
            throw new Exception("Expected failure: " + label);
        }
        var root = Path.Combine(work, "installation-readiness");
        var config = new LauncherConfig { ComfyRoot = Path.Combine(root, "ComfyUI"), AppRoot = Path.Combine(root, "app"), DataDir = Path.Combine(root, "data") };
        Directory.CreateDirectory(config.ComfyRoot);
        var modelPath = Path.Combine(config.ComfyRoot, "models", "diffusion_models", "anima-base-v1.0.safetensors");
        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        await File.WriteAllTextAsync(modelPath, "fixture model");
        var pending = new PendingModelClassification(modelPath, new FileInfo(modelPath).Length, ModelMetadata.AnimaSha256);
        Task<FileStream> FixtureVerified(PendingModelClassification item, CancellationToken _) => Task.FromResult(File.OpenRead(item.ModelPath));

        await ModelMetadata.RegisterPendingAsync(config, pending, default);
        await ModelMetadata.RegisterPendingAsync(config, pending, default);
        Check(ModelMetadata.ReadPending(config).Length == 1, "classification registration is durable and idempotent");
        using (var handler = new ManagerHandler(modelPath) { NewManager = true })
        using (var http = new HttpClient(handler))
        using (var manager = new LoraManagerClassificationClient(config, http, pollInterval: TimeSpan.Zero))
        {
            await ModelMetadata.SynchronizePendingCoreAsync(config, manager, FixtureVerified, default);
            Check(handler.FirstScans == 1 && handler.BaseModel == "Anima", "new Manager first-run gate is completed before classifying the model");
            Check(handler.Posts.All(post => !post.TryGetProperty("sidecar_storage_mode", out _)) && handler.Title == "User title" && handler.Tags.SequenceEqual(["keep"]),
                "Manager owns centralized paths and targeted classification preserves user metadata");
            Check(!File.Exists(Path.ChangeExtension(modelPath, ".metadata.json")), "classification never writes a guessed adjacent sidecar");
            Check(ModelMetadata.ReadPending(config).Length == 1, "Manager synchronization alone never clears crash-recoverable work");
            handler.WebPath = Path.Combine(root, "another", Path.GetFileName(modelPath));
            await Fails(() => ModelMetadata.ConfirmPendingCoreAsync(config, http, default), "same basename in a different directory cannot confirm classification");
            Check(ModelMetadata.ReadPending(config).Length == 1, "failed workbench readback retains durable classification work");
            handler.WebPath = modelPath;
            await ModelMetadata.ConfirmPendingCoreAsync(config, http, default);
            Check(ModelMetadata.ReadPending(config).Length == 0, "only exact recognized workbench classification clears the pending record");
        }
        using (var handler = new ManagerHandler(modelPath))
        using (var http = new HttpClient(handler))
        using (var manager = new LoraManagerClassificationClient(config, http, pollInterval: TimeSpan.Zero))
        {
            await manager.InitializeAsync(default);
            await manager.ClassifyAsync(pending, default);
            Check(handler.FirstScans == 0 && handler.Scans == 1 && handler.BaseModel == "Anima", "legacy Manager without first-run endpoints uses its existing scan API");
            Check(handler.Posts.Count == 2 && handler.Posts[0].EnumerateObject().Count() == 1
                && handler.Posts[1].EnumerateObject().Count() == 2, "valid sidecar is synchronized before changing only base_model");
        }
        using (var handler = new ManagerHandler(modelPath) { Indexed = true, MetadataValid = false })
        using (var http = new HttpClient(handler))
        using (var manager = new LoraManagerClassificationClient(config, http))
        {
            await Fails(() => manager.ClassifyAsync(pending, default), "corrupt or absent effective metadata cannot be recreated with save-metadata");
            Check(handler.Posts.Count == 0, "invalid user metadata is never overwritten");
        }
        using (var handler = new ManagerHandler(modelPath) { Indexed = true, BaseModel = "SDXL" })
        using (var http = new HttpClient(handler))
        using (var manager = new LoraManagerClassificationClient(config, http))
        {
            await Fails(() => manager.ClassifyAsync(pending, default), "explicit conflicting user classification requires attention");
            Check(handler.BaseModel == "SDXL" && handler.Posts.All(post => !post.TryGetProperty("base_model", out _)), "explicit user classification is preserved");
        }
        using (var handler = new ManagerHandler(modelPath) { InvalidPagination = true })
        using (var http = new HttpClient(handler))
        using (var manager = new LoraManagerClassificationClient(config, http))
        {
            await Fails(() => manager.ClassifyAsync(pending, default), "invalid Manager pagination cannot authorize a classification write");
            Check(handler.Posts.Count == 0, "malformed inventory produces no metadata changes");
        }
        await ModelMetadata.RegisterPendingAsync(config, pending, default);
        using (var handler = new ManagerHandler(modelPath))
        using (var http = new HttpClient(handler))
        using (var manager = new LoraManagerClassificationClient(config, http))
        {
            await Fails(() => ModelMetadata.SynchronizePendingCoreAsync(config, manager, ModelMetadata.VerifyFileAsync, default), "changed model bytes fail SHA verification before classification");
            Check(handler.Posts.Count == 0 && ModelMetadata.ReadPending(config).Length == 1, "failed hash verification preserves both model and pending work");
        }
        File.Delete(ModelMetadata.PendingPath(config));

        var staleSidecar = Path.ChangeExtension(modelPath, ".metadata.json");
        await File.WriteAllTextAsync(staleSidecar, "stale adjacent user file");
        var official = new ModelManifest(1, [new("anima-base-v1", "Anima", "fixture", "diffusion_models", Path.GetFileName(modelPath), [], pending.Size,
            ModelMetadata.AnimaSha256, ["https://example.com/model"], "fixture", "https://example.com/license")]);
        var officialProfiles = new ModelProfilesManifest(1, "anima", [new("anima", "Anima", Path.GetFileName(modelPath), "clip", "vae", "stable_diffusion", "seed", "seedvae")]);
        var queued = await ModelMetadata.EnsureDefaultCoreAsync(config, official, officialProfiles, modelPath, FixtureVerified, default);
        Check(queued.Queued && ModelMetadata.ReadPending(config).Length == 1 && await File.ReadAllTextAsync(staleSidecar) == "stale adjacent user file",
            "stale adjacent metadata cannot suppress verified official-model classification in centralized mode");
        File.Delete(ModelMetadata.PendingPath(config));

        var modelSpecs = new[] {
            ("anima-base-v1", "diffusion_models", "anima-base-v1.0.safetensors"),
            ("qwen-text", "text_encoders", "qwen.safetensors"), ("qwen-vae", "vae", "qwen_vae.safetensors"),
            ("seedvr2-model", "diffusion_models", "seedvr2.safetensors"), ("seedvr2-vae", "vae", "seedvr2_vae.safetensors"),
            ("krea2-turbo", "diffusion_models", "krea.safetensors"), ("qwen3vl-krea2", "text_encoders", "krea_clip.safetensors"),
            ("forbidden-vision-detect", "forbidden_vision", "face.pt") };
        var bytes = Encoding.UTF8.GetBytes("verified fixture");
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var manifestModels = modelSpecs.Select(spec => new ModelItem(spec.Item1, spec.Item1, "fixture", spec.Item2, spec.Item3, [], bytes.Length, hash, ["https://example.com/model"], "fixture", "https://example.com/license")).ToArray();
        foreach (var model in manifestModels.Where(model => model.Category != "forbidden_vision"))
        {
            var path = Path.Combine(config.ComfyRoot, "models", model.Category, model.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes);
        }
        var models = new ModelManifest(1, manifestModels);
        var profiles = new ModelProfilesManifest(1, "anima", [new("anima", "Anima", "anima-base-v1.0.safetensors", "qwen.safetensors", "qwen_vae.safetensors", "stable_diffusion", "seedvr2.safetensors", "seedvr2_vae.safetensors"),
            new("krea2", "Krea", "krea.safetensors", "krea_clip.safetensors", "qwen_vae.safetensors", "krea2", "seedvr2.safetensors", "seedvr2_vae.safetensors")]);
        var minimalRuntime = runtime with { Comfy = runtime.Comfy with { RequiredClasses = [] }, Nodes = [] };
        using (var handler = new ReadinessHandler(config))
        using (var http = new HttpClient(handler))
        using (var checker = new SetupRuntimeReadiness(http))
        {
            var ready = await checker.CheckAsync(config, minimalRuntime, models, profiles, [], default);
            Check(ready.Ready, "baseline runtime requires working nodes, classified generation models, SeedVR2 and random data");
            Check(!ready.OptionalIssues.Any(issue => issue.Feature == "生图模型"), "mandatory shared VAE does not implicitly select optional Krea");
            var optional = await checker.CheckAsync(config, minimalRuntime, models, profiles, ["forbidden-vision-detect"], default);
            Check(optional.RequiredReady && optional.OptionalIssues.Any(issue => issue.ModelId == "forbidden-vision-detect"), "selected optional face failure never becomes a mandatory generation failure");
            handler.Online = false;
            Check(!(await checker.CheckAsync(config, minimalRuntime, models, profiles, [], default)).RequiredReady, "offline workbench cannot complete installation");
            handler.Online = true;
            handler.CatalogReady = false;
            Check((await checker.CheckAsync(config, minimalRuntime, models, profiles, [], default)).RequiredIssues.Any(issue => issue.Feature == "随机数据"), "missing Anima Tools data blocks ready state despite HTTP success");
            handler.CatalogReady = true;
            handler.DiscoveryStatus = "incomplete";
            Check((await checker.CheckAsync(config, minimalRuntime, models, profiles, [], default)).RequiredIssues.Any(issue => issue.Feature == "随机数据"), "incomplete original Anima Tools files block readiness even when pool counts are positive");
            handler.DiscoveryStatus = "available";
            foreach (var filename in AnimaToolsDiscovery.DataFiles)
            {
                handler.Warnings = ["提示词数据文件无法读取: C:/fixture/js/" + filename + ": 数据文件无法解析"];
                Check((await checker.CheckAsync(config, minimalRuntime, models, profiles, [], default)).RequiredIssues.Any(issue => issue.Feature == "随机数据"), "custom pool counts cannot hide an original data parse failure: " + filename);
            }
            handler.Warnings = ["可选历史记录无法读取", "角色官方数据无法读取: character_official_data.json"];
            Check((await checker.CheckAsync(config, minimalRuntime, models, profiles, [], default)).Ready, "unrelated optional startup warnings do not block generation readiness");
            handler.ExpressionCount = 0;
            Check((await checker.CheckAsync(config, minimalRuntime, models, profiles, [], default)).RequiredIssues.Any(issue => issue.Message.Contains("expression")), "built-in default expressions must also be loaded");
            handler.ExpressionCount = 1;
            handler.Warnings = [];
            handler.AnimaAvailable = false;
            Check((await checker.CheckAsync(config, minimalRuntime, models, profiles, [], default)).RequiredIssues.Any(issue => issue.Feature == "生图模型"), "unknown/unavailable Anima classification blocks ready state");
        }
        using (var values = JsonDocument.Parse("{\"seedvr2_models\":[\"a/seedvr2.safetensors\",\"b/seedvr2.safetensors\"]}"))
            Check(!SetupRuntimeReadiness.ContainsResource(values.RootElement, "seedvr2_models", "seedvr2.safetensors"), "ambiguous model basename is never treated as a usable binding");
        using (var alias = JsonDocument.Parse("{\"VAELoader\":{\"input\":{\"required\":{\"vae_name\":[[\"SeedVR2/seedvr2_ema_vae_fp16.safetensors\"]]}}}}"))
            Check(ServiceManager.ResourceBindings(alias.RootElement)["ema_vae_fp16.safetensors"] == "SeedVR2/seedvr2_ema_vae_fp16.safetensors", "historical SeedVR2 VAE alias binds to its real subdirectory without renaming");
        using (var alias = JsonDocument.Parse("{\"VAELoader\":{\"input\":{\"required\":{\"vae_name\":[[\"a/seedvr2_ema_vae_fp16.safetensors\",\"b/seedvr2_ema_vae_fp16.safetensors\"]]}}}}"))
            Check(!ServiceManager.ResourceBindings(alias.RootElement).ContainsKey("ema_vae_fp16.safetensors"), "ambiguous historical aliases never select an arbitrary model");
        return count;
    }

    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private sealed class ManagerHandler : HttpMessageHandler
    {
        private readonly string path;
        public ManagerHandler(string path) { this.path = path; WebPath = path; }
        public bool NewManager, Indexed, InvalidPagination;
        public bool MetadataValid = true;
        public string BaseModel = "Unknown", Title = "User title";
        public string[] Tags = ["keep"];
        public string WebPath;
        public int FirstScans, Scans;
        public List<JsonElement> Posts = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var endpoint = request.RequestUri!.AbsolutePath;
            if (endpoint == "/api/lm/init-status") return NewManager ? Json(new { status = FirstScans == 0 ? "awaiting_first_scan" : "complete" }) : Json(new { error = "old" }, HttpStatusCode.NotFound);
            if (endpoint == "/api/lm/first-scan/start") { FirstScans++; Indexed = true; Posts.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone()); return Json(new { success = true }); }
            if (endpoint == "/api/lm/checkpoints/scan") { Scans++; Indexed = true; return Json(new { status = "success" }); }
            if (endpoint == "/api/lm/checkpoints/list") return InvalidPagination ? Json(new { items = new object[0], total_pages = -1 })
                : Json(new { items = Indexed ? new[] { new { file_path = path.Replace('\\', '/'), base_model = BaseModel, model_name = Title, tags = Tags } } : [], total_pages = Indexed ? 1 : 0, total = Indexed ? 1 : 0 });
            if (endpoint == "/api/lm/checkpoints/metadata") return MetadataValid ? Json(new { success = true, metadata = new { } }) : Json(new { success = false }, HttpStatusCode.NotFound);
            if (endpoint == "/api/lm/checkpoints/save-metadata")
            {
                var data = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
                Posts.Add(data);
                if (data.TryGetProperty("base_model", out var value)) BaseModel = value.GetString()!;
                return Json(new { success = true });
            }
            if (endpoint == "/api/resources") return Json(new { model_items = new[] { new { file_path = WebPath, model_profile = "anima", classification_status = "recognized" } } });
            throw new Exception("Unexpected test request: " + request.RequestUri);
        }
    }
    private sealed class ReadinessHandler(LauncherConfig config) : HttpMessageHandler
    {
        public bool Online = true, CatalogReady = true, AnimaAvailable = true;
        public string DiscoveryStatus = "available";
        public string[] Warnings = [];
        public int ExpressionCount = 1;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            object value = request.RequestUri!.AbsolutePath switch
            {
                "/system_stats" => new { system = new { argv = new[] { Path.Combine(config.ComfyRoot, "main.py") } } },
                "/api/launcher-health" => new { app_dir = config.AppRoot, data_dir = config.DataDir, comfy_url = config.ComfyUrl },
                "/api/status" => new { online = Online },
                "/api/resources" => new { model_profiles = new { anima = new { available = new { model = AnimaAvailable, clip = true, vae = true } } }, seedvr2_models = new[] { "seedvr2.safetensors" }, seedvr2_vaes = new[] { "seedvr2_vae.safetensors" } },
                "/api/config" => new { catalog = new { available = CatalogReady, discovery = new { status = DiscoveryStatus, message = "fixture" }, counts = new { character = 1, clothing = 2, pose = 3, background = 4, expression = ExpressionCount } }, warnings = Warnings },
                "/internal/folder_paths" => new Dictionary<string, string[]> { ["diffusion_models"] = [Path.Combine(config.ComfyRoot, "models", "diffusion_models")], ["text_encoders"] = [Path.Combine(config.ComfyRoot, "models", "text_encoders")], ["vae"] = [Path.Combine(config.ComfyRoot, "models", "vae")] },
                "/object_info" => new Dictionary<string, object> {
                    ["SesquiLatentUpscale"] = Node(new() { ["latent"] = new[] { "LATENT" }, ["scale"] = new[] { "FLOAT" }, ["half_precision"] = new[] { "BOOLEAN" }, ["model_format"] = new[] { new[] { "Wan 2.1" } } }),
                    ["CLIPLoader"] = Node(new() { ["type"] = new[] { new[] { "stable_diffusion" } } }),
                    ["PromptExpand"] = Node(new() { ["template_only"] = new[] { "BOOLEAN" } }),
                    ["SeedVR2Conditioning"] = Node(new() { ["model"] = new[] { "MODEL" }, ["vae_conditioning"] = new[] { "LATENT" } }),
                    ["SeedVR2Preprocess"] = Node(new() { ["resized_images"] = new[] { "IMAGE" } }),
                    ["SeedVR2PostProcessing"] = Node(new() { ["images"] = new[] { "IMAGE" }, ["original_resized_images"] = new[] { "IMAGE" },
                        ["color_correction_method"] = new object[] { "COMBO", new { options = new[] { "lab", "wavelet", "adain", "none" }, @default = "lab" } } }),
                    ["ImageCaptionNode"] = Node(new() { ["image"] = new[] { "IMAGE" }, ["vlm_service"] = new[] { "STRING" }, ["custom_rule"] = new[] { "BOOLEAN" }, ["custom_rule_content"] = new[] { "STRING" } }) },
                _ => throw new Exception("Unexpected readiness request: " + request.RequestUri),
            };
            return Task.FromResult(Json(value));
        }
        private static object Node(Dictionary<string, object> fields) => new { input = new { required = fields } };
    }
}
