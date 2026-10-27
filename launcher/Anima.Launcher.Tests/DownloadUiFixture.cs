using System.Net;
using System.Text.Json;
using Anima.Launcher.Core;

internal static class DownloadUiFixture
{
    public static async Task Seed(string state, string repository, ModelManifest manifest, bool longError = false)
    {
        if (Directory.Exists(state) && Directory.EnumerateFileSystemEntries(state).Any()) throw new IOException("UI fixture requires a new empty state directory.");
        Directory.CreateDirectory(state);
        var environment = Path.Combine(state, "fixture-environment");
        Directory.CreateDirectory(environment);
        Directory.CreateDirectory(Path.Combine(environment, "comfy"));
        await File.WriteAllTextAsync(Path.Combine(environment, "main.py"), "raise RuntimeError('Isolated UI fixture: service startup is intentionally unavailable.')\n");
        await File.WriteAllTextAsync(Path.Combine(environment, "comfy", "__init__.py"), "");
        await File.WriteAllTextAsync(Path.Combine(environment, "comfy", "options.py"), "def enable_args_parsing():\n    pass\n");
        var categories = manifest.Models.Select(model => model.Category).Concat(["checkpoints", "loras", "diffusion_models", "text_encoders", "vae", "upscale_models"]).Distinct().ToArray();
        await File.WriteAllTextAsync(Path.Combine(environment, "folder_paths.py"),
            "import os, json\nmodels_dir = os.path.join(os.path.dirname(__file__), 'models')\n"
            + "folder_names_and_paths = {key: ([os.path.join(models_dir, key)], {'.safetensors', '.pt', '.onnx', '.json'}) for key in json.loads("
            + JsonSerializer.Serialize(JsonSerializer.Serialize(categories)) + ")}\n");
        foreach (var category in categories) Directory.CreateDirectory(Path.Combine(environment, "models", category));
        var python = new[] { Path.Combine(repository, ".venv", "Scripts", "python.exe"), Path.Combine(repository, "venv", "Scripts", "python.exe"),
                Commands.Find("python.exe"), Commands.Find("python") }
            .FirstOrDefault(candidate => candidate is not null && File.Exists(candidate) && !candidate.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
            ?? throw new IOException("UI fixture needs a real Python interpreter for the read-only model-path probe.");
        var config = new LauncherConfig { ComfyRoot = environment, AppRoot = repository, DataDir = Path.Combine(state, "data"),
            RuntimeRoot = Path.Combine(state, "runtime"), Python = python, PythonExplicit = true,
            SetupComplete = true, AutoCheckUpdates = false, StartOnLaunch = false, SourceId = "official",
            NetworkMode = "custom", ProxyAddress = "http://127.0.0.1:9", ComfyPort = 19892, WebPort = 19894 };
        JsonFile.Write(Path.Combine(state, "launcher.json"), config);
        var runtime = JsonFile.Read<RuntimeManifest>(Path.Combine(repository, "launcher", "manifests", "launcher-manifest.json"));
        var profiles = JsonFile.Read<ModelProfilesManifest>(Path.Combine(repository, "launcher", "manifests", "model-profiles.json"));
        var singles = manifest.Models.Where(model => model.CanDownload && !model.IsPackage).Take(3).ToArray();
        if (singles.Length < 3) throw new InvalidDataException("UI fixture needs three downloadable single-file models.");
        var package = manifest.Models.First(model => model.CanDownload && model.IsPackage);
        ModelDownloadIntent Intent(ModelItem model) => new(model, Path.Combine(environment, "models", model.Category, model.RelativePath));
        var ids = new Dictionary<string, string>();
        using var handler = new WaitingHandler();
        using var downloads = new Downloads(handler: handler);
        await using (var tasks = new DownloadTaskService(downloads, state))
        {
            async Task Pause(string key, string kind, string title, ModelDownloadIntent[] models, Func<CancellationToken, Task> action, JsonElement? payload = null)
            {
                while (handler.Started.CurrentCount > 0) await handler.Started.WaitAsync();
                var owner = tasks.RegisterOwner(kind, title, config, models, payload);
                ids[key] = owner.Id;
                var run = tasks.RunOwnerAsync(owner.Id, action);
                if (!await handler.Started.WaitAsync(TimeSpan.FromSeconds(10))) throw new TimeoutException("Fixture transfer did not reach its isolated handler.");
                await tasks.PauseOwnerAsync(owner.Id);
                try { await run; } catch (OperationCanceledException) { }
            }
            Task Fetch(ModelDownloadIntent intent, CancellationToken ct) => intent.Model.IsPackage
                ? Task.WhenAll(intent.Model.Files.Select(file => downloads.Fetch(intent.Model.Name + " / " + file.Path, file.Urls,
                    Safety.Under(ModelPackages.Staging(intent.Model, intent.Destination), file.Path), file.Size, file.Sha256, null, ct)))
                : downloads.Fetch(intent.Model.Name, intent.Model.Urls, intent.Destination, intent.Model.Size, intent.Model.Sha256, null, ct);
            var single = Intent(singles[0]);
            await Pause("singleModel", "models", "隔离验收 · 单模型暂停", [single], ct => Fetch(single, ct));
            var legacy = singles.Skip(1).Select(Intent).ToArray();
            await Pause("legacyModels", "models", "隔离验收 · 旧版多模型批次", legacy, ct => Task.WhenAll(legacy.Select(intent => Fetch(intent, ct))));
            var packageIntent = Intent(package);
            await Pause("modelPackage", "models", "隔离验收 · 模型包暂停", [packageIntent], ct => Fetch(packageIntent, ct));

            const string version = "99.0.0";
            var releaseUrl = "https://github.com/" + LauncherUpdates.Repository + "/releases/download/" + version + "/";
            var release = new LauncherRelease(version, version, "隔离界面验收版本，只用于恢复状态与导航检查。", DateTimeOffset.UtcNow,
                releaseUrl + "AnimaRandomStudio.exe", 100 * 1048576, new string('c', 64), releaseUrl + "build-info.json",
                JsonSerializer.SerializeToElement(new { version, runtimeIdentifier = "win-x64", packageFormat = "single-exe-v1" }));
            LauncherUpdates.ValidateRelease(release);
            await Pause("software", "software", "隔离验收 · 启动器更新待继续", [],
                ct => downloads.Fetch("启动器 " + release.Version, [release.ExeUrl], new LauncherUpdateStore(state).CandidatePath(release.Version, release.Sha256), release.Size, release.Sha256, null, ct),
                JsonSerializer.SerializeToElement(release, JsonFile.Options));

            var frozenModels = profiles.RequiredModels(manifest, config.SelectedProfile);
            var plan = InstallationPlanner.Create(config, runtime, state, false, manifest, profiles) with
            {
                ResolvedRuntime = runtime, NodeVersionsResolved = true,
                FrozenModels = frozenModels, FrozenProfile = profiles.Get(config.SelectedProfile),
                ModelIds = [.. frozenModels.Select(model => model.Id)]
            };
            // This frozen plan is valid recovery data; no installer or dependency process is executed.
            var tool = runtime.Tools.First();
            await Pause("installation", "install", "隔离验收 · 环境安装待继续", [],
                ct => downloads.Fetch(tool.Id, [tool.Url], ArtifactCache.PathFor(config.RuntimeRoot, tool), tool.Size, tool.Sha256, null, ct),
                JsonSerializer.SerializeToElement(plan, JsonFile.Options));
        }
        var store = new DownloadTaskStore(state);
        var snapshot = store.Load();
        if (store.LoadError is not null) throw new InvalidDataException(store.LoadError);
        const string error = "隔离界面验收注入的长错误：来源在传输过程中改变了文件标识，检查点已保留。请检查目标目录、文件身份与可用空间，选择按原设置继续或使用新来源。此消息仅用于验证窄窗口中的换行、滚动和键盘操作，不代表真实模型下载失败。";
        foreach (var item in snapshot.Tasks)
        {
            item.Received = Math.Min(item.Size / 4, 32 * 1048576); // Display-only progress; no fake weight files are created.
            if (longError) { item.Status = DownloadTaskStatus.NeedsReview; item.Error = error; }
        }
        foreach (var owner in snapshot.Owners)
        {
            owner.Detail = owner.Kind == "install" ? "已固定安装计划；等待继续环境准备。" : "隔离界面验收：任务已暂停。";
            if (longError) { owner.Status = DownloadTaskStatus.NeedsReview; owner.Error = error; }
        }
        store.Save(snapshot);
        JsonFile.Write(Path.Combine(state, "fixture-index.json"), new
        {
            state, environment, python, ownerIds = ids,
            models = singles.Append(package).Select(model => new { model.Id, model.Name, model.Category, model.RelativePath }),
            note = "No network requests were sent. Auto-start and update checks are disabled; explicit transfers use a closed loopback proxy. Received bytes are display fixtures, not downloaded model files. Do not run installation or apply updates."
        });
        Console.WriteLine("UI FIXTURE READY " + state);
    }

    private sealed class WaitingHandler : HttpMessageHandler
    {
        public SemaphoreSlim Started { get; } = new(0);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Started.Release();
            await Task.Delay(Timeout.Infinite, ct);
            return new(HttpStatusCode.ServiceUnavailable);
        }
        protected override void Dispose(bool disposing) { if (disposing) Started.Dispose(); base.Dispose(disposing); }
    }
}
