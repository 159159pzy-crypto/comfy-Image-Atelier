using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anima.Launcher.Core;

internal static class PromptAssistantTests
{
    // Run only against an explicitly selected isolated ComfyUI. The probe owns
    // uniquely named fixtures and removes only those fixtures in its finally.
    public static async Task LiveProbe(string comfyUrl)
    {
        using var client = new PromptAssistantClient(comfyUrl);
        await client.Connect(default);
        if (!client.SupportsTemplateOnly) throw new Exception("Live probe requires template_only support");
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });
        var info = await http.GetFromJsonAsync<JsonObject>(comfyUrl + "/object_info/PromptExpand") ?? throw new Exception("Missing node schema");
        var documentUrl = comfyUrl + PromptAssistantClient.DiscoverPrefix(info) + "/config/system_prompts";
        var before = await http.GetFromJsonAsync<JsonObject>(documentUrl) ?? throw new Exception("Missing template document");
        var suffix = Guid.NewGuid().ToString("N");
        string? serviceId = null, templateId = null, copyId = null;
        var checks = 0;
        void Check(bool passed, string name)
        {
            if (!passed) throw new Exception("FAIL: " + name);
            checks++;
            Console.WriteLine("PASS: " + name);
        }
        try
        {
            serviceId = await client.SaveService(null, "openai_compatible", "Launcher acceptance " + suffix,
                "http://127.0.0.1:9097/v1", "dummy-acceptance-key", false, true, default);
            var service = (await client.Services(default)).Single(item => item.Id == serviceId);
            Check(service.HasApiKey && service.AdvancedParameters, "live plugin persists service and key presence");
            await client.SaveService(serviceId, service.Type, service.Name + " renamed", service.BaseUrl, "", false, false, default);
            service = (await client.Services(default)).Single(item => item.Id == serviceId);
            Check(service.HasApiKey && !service.AdvancedParameters, "live blank key preserves credentials across service edits");
            await client.SaveService(serviceId, service.Type, service.Name, service.BaseUrl, null, true, true, default);
            Check(!(await client.Services(default)).Single(item => item.Id == serviceId).HasApiKey, "live explicit clear removes credentials");
            await client.SaveModel(serviceId, "acceptance/model", .4, .8, 100, false, default);
            await client.SaveModel(serviceId, "acceptance/model", .6, .7, 120, true, default);
            Check((await client.Services(default)).Single(item => item.Id == serviceId).Models.Single().MaxTokens == 120,
                "live model creation and sampling parameter updates read back");
            await client.DeleteModel(serviceId, "acceptance/model", default);
            Check(!(await client.Services(default)).Single(item => item.Id == serviceId).Models.Any(), "live model deletion accepts slash names");
            templateId = await client.SaveTemplate(null, "Launcher template " + suffix, "仅发送这段模板。\nKeep Case_24.", "acceptance", default);
            var expected = (await client.Templates(default)).Single(item => item.Id == templateId);
            await client.SaveTemplate(templateId, expected.Name + " renamed", expected.Content, expected.Category, default, expected);
            Check((await client.Templates(default)).Single(item => item.Id == templateId).Content == expected.Content, "live template rename retains ID and exact content");
            copyId = await client.CopyTemplate(templateId, default);
            Check((await client.Templates(default)).Single(item => item.Id == copyId).Content == expected.Content, "live template copy preserves content under a fresh ID");
        }
        finally
        {
            if (copyId is not null) await client.DeleteTemplate(copyId, default);
            if (templateId is not null) await client.DeleteTemplate(templateId, default);
            if (serviceId is not null) await client.DeleteService(serviceId, default);
        }
        var after = await http.GetFromJsonAsync<JsonObject>(documentUrl) ?? throw new Exception("Missing template readback");
        Check(JsonNode.DeepEquals(before, after), "live template CRUD leaves all preexisting categories, entries and active choices intact");
        Console.WriteLine($"{checks} live Prompt Assistant CRUD checks passed");
    }

    public static async Task<int> Run(string work)
    {
        var count = 0;
        void Check(bool passed, string name)
        {
            if (!passed) throw new Exception("FAIL: " + name);
            count++;
            Console.WriteLine("PASS: " + name);
        }
        async Task Reject(Func<Task> action, string name)
        {
            try { await action(); }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException or IOException) { Check(true, name); return; }
            throw new Exception("FAIL expected rejection: " + name);
        }
        var info = JsonNode.Parse("""{"PromptExpand":{"python_module":"custom_nodes.My-Prompt-Assistant.node.expand_node"}}""")!.AsObject();
        Check(PromptAssistantClient.DiscoverPrefix(info) == "/My-Prompt-Assistant/api", "assistant API prefix follows the live module directory");
        await Reject(() => Task.FromResult(PromptAssistantClient.DiscoverPrefix(new JsonObject())), "missing plugin identity has no guessed route");
        var keep = PromptAssistantClient.ServicePayload("Name", "https://provider.invalid/v1/", " ", false, true);
        var clear = PromptAssistantClient.ServicePayload("Name", "https://provider.invalid/v1", null, true, true);
        Check(!keep.ContainsKey("api_key") && clear["api_key"]!.GetValue<string>() == "", "blank key preserves credentials and explicit clear deletes them");
        await Reject(() => Task.FromResult(PromptAssistantClient.ServicePayload("Name/model", "https://provider.invalid", null, false, true)), "ambiguous service selector names rejected");
        await Reject(() => Task.FromResult(PromptAssistantClient.ServicePayload("Name", "https://secret@provider.invalid", null, false, true)), "credentials in base URL rejected");
        using var handler = new AssistantHandler();
        using var client = new PromptAssistantClient("http://127.0.0.1:8188", handler);
        await client.Connect(default);
        var services = await client.Services(default);
        Check(services.Single().HasApiKey && !JsonSerializer.Serialize(services).Contains("secret-key"), "service DTO allowlist excludes plaintext and encrypted API keys");
        Check(!handler.Paths.Any(path => path.EndsWith("/models")), "loading configured services never calls a remote model provider");
        await Reject(() => client.SaveService(null, "openai_compatible", "Existing", "https://provider.invalid/v1", null, false, true, default), "duplicate service names rejected before mutation");
        await client.SaveService("svc", "openai_compatible", "Updated", "https://provider.invalid/v1", "", false, true, default);
        Check(handler.LastServiceBody is not null && !handler.LastServiceBody.ContainsKey("api_key") && services.Single().Id == "svc", "editing a service leaves its stable identity and key intact");
        await client.SaveService("svc", "openai_compatible", "Updated", "https://provider.invalid/v1", null, true, true, default);
        Check(handler.LastServiceBody!["api_key"]!.GetValue<string>() == "", "explicit credential removal reaches plugin");
        var created = await client.SaveService(null, "ollama", "Local", "http://localhost:11434", null, false, true, default);
        Check((await client.Services(default)).Any(item => item.Id == created && item.AdvancedParameters), "new service parameters persist and are read back");
        await client.SaveModel("svc", "namespace/model", .4, .8, 1234, false, default);
        await client.SaveModel("svc", "namespace/model", .6, .7, 2345, true, default);
        Check(handler.ParameterWrites == 3 && (await client.Services(default)).Single(item => item.Id == "svc").Models.Single().MaxTokens == 2345, "model CRUD uses plugin parameter contract and verifies the result");
        var remote = await client.RemoteModels("svc", default);
        Check(remote.SequenceEqual(new[] { "remote/model" }), "remote model discovery is explicit and projected");
        await client.DeleteModel("svc", "namespace/model", default);
        Check(!(await client.Services(default)).Single(item => item.Id == "svc").Models.Any(), "model deletion handles slash names and checks readback");

        await client.Connect(default, AssistantMode.Reverse);
        Check(client.SupportsVision && !client.SupportsTemplateOnly, "vision capability does not depend on the rewrite compatibility patch");
        var readsBeforeVisionConnect = handler.Paths.Count(path => path == "/object_info/PromptExpand");
        await client.Connect(default, AssistantMode.Reverse);
        Check(handler.Paths.Count(path => path == "/object_info/PromptExpand") == readsBeforeVisionConnect,
            "vision connection resolves its own node without requiring the rewrite node");
        Check((await client.RemoteModels("svc", default, AssistantMode.Reverse)).SequenceEqual(new[] { "remote/vision" }),
            "vision discovery projects the provider VLM list");
        await client.SaveModel("svc", "shared/model", .2, .8, 100, false, default);
        await client.SaveModel("svc", "shared/model", .4, .9, 200, false, default, AssistantMode.Reverse);
        await client.SaveModel("svc", "shared/model", .5, .7, 300, true, default, AssistantMode.Reverse);
        Check((await client.Services(default)).Single(item => item.Id == "svc").Models.Single().MaxTokens == 100 &&
              (await client.Services(default, AssistantMode.Reverse)).Single(item => item.Id == "svc").Models.Single().MaxTokens == 300,
              "same-name text and vision models have independent parameters");
        Check(!JsonSerializer.Serialize(await client.Services(default, AssistantMode.Reverse)).Contains("secret-key"),
            "vision model projection excludes credentials");
        await client.DeleteModel("svc", "shared/model", default, AssistantMode.Reverse);
        Check(!(await client.Services(default, AssistantMode.Reverse)).Single(item => item.Id == "svc").Models.Any() &&
              (await client.Services(default)).Single(item => item.Id == "svc").Models.Count == 1,
              "deleting a vision model preserves the same-name text model");
        await client.DeleteModel("svc", "shared/model", default);
        await Reject(() => client.SaveModel("svc", "invalid", .5, .8, 100, false, default, (AssistantMode)99),
            "unsupported model types are rejected before mutation");
        handler.MissingVision = true;
        await client.Connect(default, AssistantMode.Reverse);
        Check(!client.SupportsVision && (await client.Services(default, AssistantMode.Reverse)).Count > 0,
            "old plugins can still configure vision while reporting the missing node");
        handler.MissingVision = false;

        var original = (JsonObject)handler.Document.DeepClone();
        var selected = (await client.Templates(default)).Single(item => item.Id == "one");
        // An unrelated edit made after the UI loaded must survive its later save.
        handler.Document["other_category"] = new JsonObject { ["new"] = "keep" };
        await client.SaveTemplate("one", "Renamed", "exact user prompt\n第二行", "new category", default, selected);
        Check(handler.Document["other_category"]?["new"]?.GetValue<string>() == "keep" &&
              JsonNode.DeepEquals(original["vision_prompts"], handler.Document["vision_prompts"]) &&
              JsonNode.DeepEquals(original["active_prompts"], handler.Document["active_prompts"]) &&
              handler.Document["expand_prompts"]?["one"]?["future"]?["setting"]?.GetValue<int>() == 4 &&
              handler.Document["expand_prompts"]?["two"]?["content"]?.GetValue<string>() == "unrelated",
              "template read-merge-write preserves other categories, active selection and unknown fields");
        await Reject(() => client.SaveTemplate("one", "Stale", "stale", "", default, selected), "stale edits cannot overwrite another template edit");
        var copyId = await client.CopyTemplate("one", default);
        Check(copyId != "one" && handler.Document["expand_prompts"]?[copyId]?["future"]?["setting"]?.GetValue<int>() == 4, "copy gets a new stable ID and retains template metadata");
        await client.DeleteTemplate(copyId, default);
        await Reject(() => client.DeleteTemplate("two", default), "active plugin template cannot be deleted without changing default");

        var beforeVision = (JsonObject)handler.Document.DeepClone();
        var visionId = await client.SaveTemplate(null, "Vision new", "Describe composition.\n保留细节", "image", default, mode: AssistantMode.Reverse);
        var visionSelected = (await client.Templates(default, AssistantMode.Reverse)).Single(item => item.Id == visionId);
        handler.Document["vision_prompts"]![visionId]!["future"] = new JsonObject { ["keep"] = true };
        await client.SaveTemplate(visionId, "Vision renamed", visionSelected.Content, "photo", default, visionSelected, AssistantMode.Reverse);
        Check(JsonNode.DeepEquals(beforeVision["expand_prompts"], handler.Document["expand_prompts"]) &&
              JsonNode.DeepEquals(beforeVision["active_prompts"], handler.Document["active_prompts"]) &&
              JsonNode.DeepEquals(beforeVision["future_root"], handler.Document["future_root"]) &&
              JsonNode.DeepEquals(beforeVision["vision_prompts"]?["vision"], handler.Document["vision_prompts"]?["vision"]) &&
              handler.Document["vision_prompts"]?[visionId]?["future"]?["keep"]?.GetValue<bool>() == true,
              "vision template editing preserves text templates, active choices, unrelated entries and unknown fields");
        await Reject(() => client.SaveTemplate(visionId, "Stale vision", "old", "", default, visionSelected, AssistantMode.Reverse),
            "vision templates retain stale-edit protection");
        var visionCopy = await client.CopyTemplate(visionId, default, AssistantMode.Reverse);
        Check((await client.Templates(default, AssistantMode.Reverse)).Single(item => item.Id == visionCopy).Content == visionSelected.Content &&
              handler.Document["vision_prompts"]?[visionCopy]?["future"]?["keep"]?.GetValue<bool>() == true,
              "vision template copying retains exact content and unknown metadata");
        await client.DeleteTemplate(visionCopy, default, AssistantMode.Reverse);
        await client.DeleteTemplate(visionId, default, AssistantMode.Reverse);
        await Reject(() => client.DeleteTemplate("vision", default, AssistantMode.Reverse),
            "the active vision template cannot be deleted");
        Check(JsonNode.DeepEquals(beforeVision, handler.Document), "vision CRUD leaves all preexisting template data intact");
        await client.DeleteService(created, default);
        Check(!(await client.Services(default)).Any(item => item.Id == created), "service deletion is verified");
        handler.Fail = true;
        try { await client.Services(default); throw new Exception("missing API failure"); }
        catch (InvalidOperationException ex) { Check(!ex.ToString().Contains("secret-key"), "upstream errors never echo provider secrets"); }

        var comfy = Path.Combine(work, "assistant-discovery");
        var plugin = Path.Combine(comfy, "custom_nodes", "Prompt-Assistant-main");
        Directory.CreateDirectory(Path.Combine(plugin, "node"));
        Directory.CreateDirectory(Path.Combine(plugin, "services"));
        File.WriteAllText(Path.Combine(plugin, "node", "expand_node.py"), "class PromptExpand(Base): pass");
        File.WriteAllText(Path.Combine(plugin, "services", "llm.py"), "class LLMService: pass");
        Check(PromptAssistantCompatibility.ResolveDirectory(comfy) == plugin, "installer resolves renamed plugin directories without duplicate install");
        Check(PromptAssistantCompatibility.IsOnlyCompatibilityChange(" M node/expand_node.py\n M services/llm.py\n") &&
              !PromptAssistantCompatibility.IsOnlyCompatibilityChange(" M services/llm.py\n M config_manager.py\n") &&
              !PromptAssistantCompatibility.IsOnlyCompatibilityChange("M  services/llm.py\n"), "repair accepts only exact worktree patch files pending source hash verification");
        Check(typeof(PromptAssistantCompatibility).Assembly.GetManifestResourceNames().Contains("Anima.Launcher.Core.patch_prompt_assistant.py"), "published Core assembly embeds the compatibility installer");
        var python = Commands.Find(OperatingSystem.IsWindows() ? "python.exe" : "python3");
        if (python is not null)
        {
            var fixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../launcher/tests/fixtures/prompt-assistant/pinned"));
            foreach (var relative in new[] { "node/expand_node.py", "services/llm.py" })
                File.Copy(Path.Combine(fixture, relative), Path.Combine(plugin, relative), true);
            var config = new LauncherConfig { ComfyRoot = comfy, Python = python };
            await PromptAssistantCompatibility.CheckOrApply(config, work, plugin, true, default);
            await PromptAssistantCompatibility.CheckOrApply(config, work, plugin, false, default);
            Check(File.Exists(Path.Combine(plugin, ".anima-template-only-v1", "node", "expand_node.py")), "embedded compatibility script applies and verifies the exact fixture with a backup");
            await PromptAssistantCompatibility.CheckOrApply(config, work, plugin, true, default);
            Check(true, "installer compatibility stage can run repeatedly");
            var nodeSource = Path.Combine(plugin, "node", "expand_node.py");
            File.AppendAllText(nodeSource, "\n# user source edit\n");
            await Reject(() => PromptAssistantCompatibility.CheckOrApply(config, work, plugin, true, default), "embedded repair refuses user edits after patching");
            Check(File.ReadAllText(nodeSource).EndsWith("# user source edit\n"), "repair preserves the rejected source");
        }
        return count;
    }

    private sealed class AssistantHandler : HttpMessageHandler
    {
        public readonly List<string> Paths = [];
        public JsonObject? LastServiceBody;
        public int ParameterWrites;
        public bool Fail;
        public bool MissingVision;
        public JsonObject Document = JsonNode.Parse("""
            {"expand_prompts":{"one":{"name":"One","content":"old","category":"A","role":"system","showIn":["frontend","node"],"tags":["keep"],"future":{"setting":4}},"two":{"name":"Two","content":"unrelated","role":"system"}},
             "vision_prompts":{"vision":{"name":"Vision","content":"vision"}},"active_prompts":{"expand":"two","vision":"vision"},"future_root":{"keep":true}}
            """)!.AsObject();
        private readonly JsonArray services = JsonNode.Parse("""
            [{"id":"svc","name":"Existing","type":"openai_compatible","base_url":"https://provider.invalid/v1","api_key":"secret-key","api_key_encrypted":"encrypted-secret-key","has_api_key":true,"llm_models":[],"vlm_models":[]}]
            """)!.AsArray();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            if (Fail) return Response(new JsonObject { ["success"] = false, ["error"] = "secret-key" }, HttpStatusCode.BadRequest);
            if (path == "/object_info/PromptExpand") return Response(JsonNode.Parse("""{"PromptExpand":{"python_module":"custom_nodes.Prompt-Assistant-main"}}""")!);
            if (path == "/object_info/ImageCaptionNode") return Response(MissingVision ? new JsonObject() : JsonNode.Parse("""{"ImageCaptionNode":{"python_module":"custom_nodes.Prompt-Assistant-main.node.image_caption_node","input":{"required":{"image":["IMAGE"],"vlm_service":[["Example/vision"]],"custom_rule":["BOOLEAN"],"custom_rule_content":["STRING"]}}}}""")!);
            if (!path.StartsWith("/Prompt-Assistant-main/api/")) throw new Exception("wrong discovered prefix");
            path = path["/Prompt-Assistant-main/api".Length..];
            var body = request.Content is null ? null : (await request.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cancellationToken))!;
            if (path == "/services")
            {
                if (request.Method == HttpMethod.Get) return Response(new JsonObject { ["success"] = true, ["services"] = services.DeepClone() });
                var service = (JsonObject)body!.DeepClone();
                service["id"] = "new";
                service["llm_models"] = new JsonArray();
                service["vlm_models"] = new JsonArray();
                services.Add(service);
                return Response(new JsonObject { ["success"] = true, ["service_id"] = "new" });
            }
            if (path == "/config/system_prompts")
            {
                if (request.Method == HttpMethod.Post) Document = (JsonObject)body!.DeepClone();
                return Response(request.Method == HttpMethod.Get ? Document.DeepClone() : new JsonObject { ["success"] = true });
            }
            var parts = path.Split('/');
            var id = Uri.UnescapeDataString(parts[2]);
            var entry = services.OfType<JsonObject>().Single(item => item["id"]!.GetValue<string>() == id);
            if (parts.Length == 3)
            {
                if (request.Method == HttpMethod.Delete) services.Remove(entry);
                else
                {
                    LastServiceBody = (JsonObject)body!.DeepClone();
                    foreach (var field in body) entry[field.Key] = field.Value?.DeepClone();
                }
            }
            else if (request.Method == HttpMethod.Get)
                return Response(JsonNode.Parse("""{"success":true,"models":{"llm":["remote/model"],"vlm":["remote/vision"]}}""")!);
            else if (request.Method == HttpMethod.Post)
            {
                var model = (JsonObject)body!.DeepClone();
                model["name"] = model["model_name"]!.DeepClone();
                entry[body["model_type"]!.GetValue<string>() + "_models"]!.AsArray().Add(model);
            }
            else if (request.Method == HttpMethod.Delete)
            {
                var name = Uri.UnescapeDataString(string.Join("/", parts.Skip(5)));
                var modelList = entry[parts[4] + "_models"]!.AsArray();
                var model = modelList.OfType<JsonObject>().Single(item => item["name"]!.GetValue<string>() == name);
                modelList.Remove(model);
            }
            else
            {
                ParameterWrites++;
                var model = entry[body!["model_type"]!.GetValue<string>() + "_models"]!.AsArray().OfType<JsonObject>().Single(item => item["name"]!.GetValue<string>() == body["model_name"]!.GetValue<string>());
                model[body!["parameter_name"]!.GetValue<string>()] = body["parameter_value"]!.DeepClone();
            }
            return Response(new JsonObject { ["success"] = true });
        }
        private static HttpResponseMessage Response(JsonNode value, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = JsonContent.Create(value) };
    }
}
