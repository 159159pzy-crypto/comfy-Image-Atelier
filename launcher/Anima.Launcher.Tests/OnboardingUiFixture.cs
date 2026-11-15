using System.Text.Json;
using Anima.Launcher.Core;

/// <summary>Opt-in UI states; creates only a new isolated directory and never contacts a service.</summary>
internal static class OnboardingUiFixture
{
    public static async Task Seed(string state, string repository, ModelManifest models, string scenario = "configuration-issues")
    {
        if (scenario is not ("configuration-issues" or "assistant-skipped" or "assistant-configured" or "selection-review"))
            throw new ArgumentException("Unknown onboarding UI fixture: " + scenario);
        if (Directory.Exists(state) && Directory.EnumerateFileSystemEntries(state).Any())
            throw new IOException("Onboarding UI fixture requires a new empty state directory.");
        Directory.CreateDirectory(state);
        var environment = Path.Combine(state, "fixture-environment");
        Directory.CreateDirectory(Path.Combine(environment, "comfy"));
        await File.WriteAllTextAsync(Path.Combine(environment, "main.py"),
            "raise RuntimeError('Isolated onboarding UI fixture: service startup is intentionally unavailable.')\n");
        await File.WriteAllTextAsync(Path.Combine(environment, "comfy", "__init__.py"), "");
        await File.WriteAllTextAsync(Path.Combine(environment, "comfy", "options.py"), "def enable_args_parsing():\n    pass\n");
        var categories = models.Models.Select(model => model.Category)
            .Concat(["checkpoints", "loras", "diffusion_models", "text_encoders", "vae", "upscale_models"]).Distinct().ToArray();
        await File.WriteAllTextAsync(Path.Combine(environment, "folder_paths.py"),
            "import os, json\nmodels_dir = os.path.join(os.path.dirname(__file__), 'models')\n"
            + "folder_names_and_paths = {key: ([os.path.join(models_dir, key)], {'.safetensors', '.pt', '.onnx', '.json'}) for key in json.loads("
            + JsonSerializer.Serialize(JsonSerializer.Serialize(categories)) + ")}\n");
        foreach (var category in categories) Directory.CreateDirectory(Path.Combine(environment, "models", category));
        var pathCandidates = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(directory => !directory.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
            .SelectMany(directory => new[] { "python.exe", "python" }.Select(name => Path.Combine(directory.Trim('"'), name)));
        var python = new[] { Path.Combine(repository, ".venv", "Scripts", "python.exe") }.Concat(pathCandidates)
            .FirstOrDefault(candidate => File.Exists(candidate)
                && !candidate.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
            ?? throw new IOException("Onboarding UI fixture needs a real Python interpreter for the read-only path probe.");
        var config = new LauncherConfig
        {
            ComfyRoot = environment, AppRoot = repository, DataDir = Path.Combine(state, "data"),
            RuntimeRoot = Path.Combine(state, "runtime"), Python = python, PythonExplicit = true,
            SetupComplete = false, AutoCheckUpdates = false, StartOnLaunch = false, SourceId = "official",
            NetworkMode = "custom", ProxyAddress = "http://127.0.0.1:9", ComfyPort = 19892, WebPort = 19894,
        };
        var runtime = JsonFile.Read<RuntimeManifest>(Path.Combine(repository, "launcher", "manifests", "launcher-manifest.json"));
        var profiles = JsonFile.Read<ModelProfilesManifest>(Path.Combine(repository, "launcher", "manifests", "model-profiles.json"));
        var plan = InstallationPlanner.Create(config, runtime, state, false, models, profiles) with
        {
            NodeVersionsResolved = true, ResolvedRuntime = SetupFeatures.SelectRuntime(runtime, []),
        };
        var receipt = new SetupRecord
        {
            Draft = config.Clone(), Fresh = false, Step = SetupStep.Complete,
            InstallStarted = true, InstallFinished = true, RuntimeVerified = true, ReviewedPlan = plan,
            ConfigurationIssues = scenario == "configuration-issues"
                ? [new("base", "未找到兼容的 Anima 主模型，请进入工作台选择现有模型或在模型资源页下载。", "anima-base")]
                : [],
            AssistantAvailable = scenario != "configuration-issues",
            AssistantStatus = scenario == "assistant-configured" ? AssistantSetupStatus.Configured : AssistantSetupStatus.Skipped,
            SelectionNeedsReview = scenario == "selection-review",
            OptionalModelIds = scenario == "selection-review" ? [SetupFeatures.Prefix + "prompt-assistant"] : [],
        };
        JsonFile.Write(Path.Combine(state, "launcher.json"), config);
        JsonFile.Write(Path.Combine(state, "onboarding.json"), receipt);
        JsonFile.Write(Path.Combine(state, "fixture-index.json"), new
        {
            scenario, state, environment, python,
            note = "Isolated display fixture. No downloads, dependency installation, service startup, or provider requests were performed. Restored runtime results must appear stale. Startup deliberately fails; do not install into this fixture.",
        });
        Console.WriteLine("ONBOARDING UI FIXTURE READY " + state);
    }
}
