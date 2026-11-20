using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using Anima.Launcher.Core;

internal static class OnboardingTests
{
    public static async Task<int> Run(string work, RuntimeManifest manifest)
    {
        var passed = 0;
        void Check(bool success, string label)
        {
            if (!success) throw new Exception("FAIL: " + label);
            passed++;
            Console.WriteLine("PASS: " + label);
        }

        var state = Path.Combine(work, "onboarding");
        var environment = Path.Combine(state, "installation");
        var comfy = Path.Combine(environment, "ComfyUI");
        var python = Path.Combine(comfy, ".venv", "Scripts", "python.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(python)!);
        File.WriteAllText(Path.Combine(comfy, "main.py"), "# isolated fixture");
        File.WriteAllText(python, "");
        var configured = new LauncherConfig { InstallRoot = environment, ComfyRoot = comfy, Python = python };
        var first = new LauncherConfig();

        var directRoot = Path.Combine(state, "existing-comfy");
        Directory.CreateDirectory(directRoot);
        File.WriteAllText(Path.Combine(directRoot, "main.py"), "");
        File.WriteAllText(Path.Combine(directRoot, "folder_paths.py"), "");
        try
        {
            InstallationPlanner.Create(new() { InstallRoot = directRoot }, manifest, state, true);
            throw new Exception("Nested installation was accepted.");
        }
        catch (InvalidDataException ex) { Check(ex.Message.Contains("导入") && !Directory.Exists(Path.Combine(directRoot, "ComfyUI")), "existing ComfyUI root is rejected before planning a nested installation"); }
        Directory.CreateDirectory(Path.Combine(directRoot, "ComfyUI"));
        File.WriteAllText(Path.Combine(directRoot, "ComfyUI", "main.py"), "");
        Check(Inventory.NormalizeRoot(directRoot) == directRoot, "a selected environment takes precedence over a partial nested checkout");
        if (OperatingSystem.IsWindows())
        {
            var mounted = DriveInfo.GetDrives().Select(drive => drive.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var absent = Enumerable.Range('D', 'Z' - 'D' + 1).Select(letter => $"{(char)letter}:\\").FirstOrDefault(volume => !mounted.Contains(volume));
            if (absent is not null)
                Check(Inventory.UnavailableEnvironmentVolumes(Path.Combine(absent, "comfy"), Path.Combine(absent, "runtime"), "", state).SequenceEqual([absent]), "offline environment drive is identified once without losing the saved directories");
            try { Inventory.InstallLocation(@"\\localhost\unavailable-share"); throw new Exception("UNC installation was accepted."); }
            catch (InvalidDataException ex) { Check(ex.Message.Contains("映射"), "UNC paths offer a mapped drive before probing an unavailable share"); }
        }
        try
        {
            Inventory.FindPython(comfy, Path.Combine(state, "missing-python.exe"), true);
            throw new Exception("Explicit missing Python was silently replaced.");
        }
        catch (FileNotFoundException) { Check(Inventory.FindPython(comfy, "") == python, "only automatically selected interpreters can fall back"); }
        var invalid = JsonSerializer.Serialize(new { comfyRoot = comfy, python, webPort = 22, selectedProfile = "missing", networkMode = "direct" });
        var repairedConfig = ConfigurationRecovery.Recover(invalid, out var defaults);
        Check(repairedConfig.ComfyRoot == comfy && repairedConfig.Python == python && repairedConfig.NetworkMode == "direct"
            && repairedConfig.SelectedProfile == "anima" && repairedConfig.WebPort == 8190 && defaults.Length == 2, "field recovery keeps the original environment and valid settings");
        var zipPath = Path.Combine(state, "payload.zip");
        using (var archive = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("run.py").Open())) writer.Write("verified");
        var snapshotRoot = Path.Combine(state, "snapshot");
        var backups = Path.Combine(state, "backups");
        BundledResourceRepair.EnsureWorkbench(zipPath, snapshotRoot, backups, _ => { });
        var script = Path.Combine(snapshotRoot, "run.py");
        Check(File.ReadAllText(script) == "verified", "missing embedded workbench member is restored from the package");
        File.WriteAllText(script, "modified");
        BundledResourceRepair.EnsureWorkbench(zipPath, snapshotRoot, backups, _ => { });
        Check(File.ReadAllText(script) == "verified" && File.ReadAllText(Directory.GetFiles(backups).Single()) == "modified",
            "corrupt embedded member is restored atomically and the previous bytes are recoverable");

        var legacyReceipt = JsonSerializer.Deserialize<SetupRecord>("{}", JsonFile.Options)!;
        Check(legacyReceipt.ConfigurationIssues.Length == 0 && !legacyReceipt.SelectionNeedsReview,
            "legacy onboarding receipts default to no remembered configuration issues or pending selection review");
        var configurationIssue = new SetupRuntimeIssue("base", "请选择兼容的 Anima 主模型", "anima-base");
        var remembered = new SetupRecord { RuntimeVerified = true, ConfigurationIssues = [configurationIssue] };
        var restored = JsonSerializer.Deserialize<SetupRecord>(JsonSerializer.Serialize(remembered, JsonFile.Options), JsonFile.Options)!;
        restored.MarkRuntimeCheckStale();
        Check(!restored.RuntimeVerified && restored.ConfigurationIssues.Single() == configurationIssue,
            "restoring runtime results keeps issue context but marks the result stale");
        var failedCheckRejected = false;
        try { restored.RecordRuntimeCheck(new([new("base", "service unavailable")], [])); }
        catch (InvalidOperationException) { failedCheckRejected = true; }
        Check(failedCheckRejected && !restored.RuntimeVerified && restored.ConfigurationIssues.Single() == configurationIssue,
            "failed runtime verification cannot clear the previous resource issues");
        restored.RecordRuntimeCheck(new([], []) { ConfigurationIssues = [configurationIssue] });
        Check(restored.RuntimeVerified && restored.ConfigurationIssues.Single() == configurationIssue,
            "successful runtime check preserves actionable configuration issues without blocking entry");
        restored.MarkRuntimeCheckStale();
        restored.RecordRuntimeCheck(new([], []));
        Check(restored.RuntimeVerified && restored.ConfigurationIssues.Length == 0,
            "only a new successful check with no configuration issues clears the previous result");
        foreach (var (status, expected) in new[] { (AssistantSetupStatus.Pending, "可选"), (AssistantSetupStatus.Skipped, "稍后配置"),
                     (AssistantSetupStatus.Configured, "已配置"), (AssistantSetupStatus.Legacy, "未检查") })
        {
            var display = SetupAssistantPresentation.Resolve(new() { AssistantStatus = status, AssistantAvailable = true });
            Check(display.Status == expected && display.Configured == (status == AssistantSetupStatus.Configured),
                "assistant progress distinguishes " + status);
        }
        Check(SetupAssistantPresentation.Resolve(new() { AssistantAvailable = false, AssistantStatus = AssistantSetupStatus.Configured })
                is { Status: "暂不可用", Configured: false }, "missing assistant cannot appear configured because of an old receipt");
        var editedSelection = new SetupRecord { Draft = configured.Clone(), Fresh = false, InstallStarted = true,
            InstallFinished = true, SelectionNeedsReview = true, Step = SetupStep.Review };
        Check(SetupRouting.Resolve(configured, editedSelection) == SetupStep.Location,
            "restart with unconfirmed feature changes requires review instead of resuming an old plan");
        var pendingSelectionConfig = configured.Clone();
        pendingSelectionConfig.InstallPending = true;
        Check(SetupRouting.Resolve(pendingSelectionConfig, editedSelection) == SetupStep.Location,
            "pending installation cannot bypass a new unconfirmed feature selection after restart");
        var sameDraft = new SetupRecord { Draft = configured.Clone(), RuntimeVerified = true };
        sameDraft.UpdateDraft(sameDraft.Draft.Clone());
        Check(!sameDraft.SelectionNeedsReview && sameDraft.RuntimeVerified,
            "saving unchanged location fields does not invalidate a confirmed selection or runtime receipt");
        SetupRecord AssistantRecord(bool fresh = true) => new() { Draft = configured.Clone(), Fresh = fresh,
            AssistantStatus = AssistantSetupStatus.Configured, AssistantAvailable = true };
        foreach (var change in new Action<LauncherConfig>[]
        {
            draft => draft.SourceId = "custom",
            draft => { draft.Python += ".other"; draft.PythonExplicit = true; },
            draft => draft.InstallRoot += Path.DirectorySeparatorChar,
        })
        {
            var sameTargetAssistant = AssistantRecord();
            var replacement = sameTargetAssistant.Draft.Clone(); change(replacement);
            sameTargetAssistant.UpdateDraft(replacement);
            Check(sameTargetAssistant.AssistantStatus == AssistantSetupStatus.Configured && sameTargetAssistant.AssistantAvailable == true,
                "source, interpreter and equivalent-path edits retain assistant configuration for the same target");
        }
        var changedTargetAssistant = AssistantRecord();
        var newTarget = changedTargetAssistant.Draft.Clone(); newTarget.InstallRoot += "-new";
        changedTargetAssistant.UpdateDraft(newTarget);
        Check(changedTargetAssistant.AssistantStatus == AssistantSetupStatus.Pending && changedTargetAssistant.AssistantAvailable is null
            && !SetupAssistantPresentation.Resolve(changedTargetAssistant).Configured,
            "changing the effective installation target discards only the old assistant completion receipt");
        changedTargetAssistant.AssistantAvailable = true;
        Check(SetupRouting.AfterModels(changedTargetAssistant) == SetupStep.Assistant,
            "a newly installed assistant in the new target still visits provider configuration");
        var importTargetAssistant = AssistantRecord(false);
        var importTarget = importTargetAssistant.Draft.Clone(); importTarget.InstallRoot += "-unused";
        importTargetAssistant.UpdateDraft(importTarget);
        Check(importTargetAssistant.AssistantStatus == AssistantSetupStatus.Configured && importTargetAssistant.AssistantAvailable == true,
            "editing an unused install root during import preserves the actual target's assistant receipt");
        importTarget.ComfyRoot += "-new";
        importTargetAssistant.UpdateDraft(importTarget);
        Check(importTargetAssistant.AssistantStatus == AssistantSetupStatus.Pending && importTargetAssistant.AssistantAvailable is null,
            "changing the imported ComfyUI directory resets the previous environment's assistant receipt");
        var sameModeTarget = AssistantRecord(); sameModeTarget.SetInstallationMode(false);
        Check(sameModeTarget.AssistantStatus == AssistantSetupStatus.Configured && sameModeTarget.AssistantAvailable == true,
            "switching install mode without changing the effective target preserves assistant configuration");
        foreach (var fresh in new[] { false, true })
        {
            var differentModeTarget = AssistantRecord(fresh);
            differentModeTarget.Draft.InstallRoot += "-new";
            differentModeTarget.SetInstallationMode(!fresh);
            Check(differentModeTarget.AssistantStatus == AssistantSetupStatus.Pending && differentModeTarget.AssistantAvailable is null,
                "switching install mode to a different effective target resets assistant availability and completion");
        }
        foreach (var (name, change) in new (string, Action<LauncherConfig>)[]
        {
            ("download source", draft => draft.SourceId = "custom"),
            ("Python interpreter", draft => { draft.Python += ".other"; draft.PythonExplicit = true; }),
            ("installation directory", draft => { draft.InstallRoot += "-other"; draft.ComfyRoot = Path.Combine(draft.InstallRoot, "ComfyUI"); }),
        })
        {
            var changedDraft = new SetupRecord { Draft = configured.Clone(), Fresh = true, InstallStarted = true,
                Step = SetupStep.Location, RuntimeVerified = true };
            var replacement = changedDraft.Draft.Clone();
            change(replacement);
            changedDraft.UpdateDraft(replacement);
            changedDraft.Step = SetupStep.Review; // Inspection completed; no installation confirmation yet.
            var restoredDraft = JsonSerializer.Deserialize<SetupRecord>(JsonSerializer.Serialize(changedDraft, JsonFile.Options), JsonFile.Options)!;
            var pendingTarget = replacement.Clone(); pendingTarget.InstallPending = true;
            Check(restoredDraft.SelectionNeedsReview && !restoredDraft.RuntimeVerified
                && SetupRouting.Resolve(pendingTarget, restoredDraft) == SetupStep.Location,
                "changing " + name + " on a paused setup still requires confirmation after checking and restarting");
        }
        var dependencyDetails = new[]
        {
            @"依赖需要补齐：aiohttp 已安装 3.8.1，要求 >=3.9（F:\创作环境（测试）\ComfyUI\requirements.txt:12）",
            @"需要安装插件：ComfyUI-LoRA-Manager（F:\创作环境\ComfyUI\custom_nodes\ComfyUI-LoRA-Manager）",
            @"依赖尚未验证：无法读取依赖文件：[Errno 2] 'F:\创作环境\missing requirements.txt'（F:\创作环境\missing requirements.txt）",
            "SesquiLSR 的 upscaler_Wan21.safetensors 缺失或校验失败，请在启动器修复安装。",
        };
        var dependencyPresentation = SetupDependencyReviewPresentation.Create(dependencyDetails);
        Check(dependencyPresentation.Summary.Contains("aiohttp 已安装 3.8.1，要求 >=3.9")
            && dependencyPresentation.Summary.Contains("ComfyUI-LoRA-Manager")
            && dependencyPresentation.Summary.Contains("upscaler_Wan21.safetensors"),
            "dependency summary preserves package versions, plugin names and actionable runtime filenames");
        Check(!dependencyPresentation.Summary.Contains(@"F:\") && !dependencyPresentation.Summary.Contains("requirements.txt")
            && dependencyPresentation.Summary.Contains("尚未验证") && dependencyPresentation.Summary.Contains("具体原因见详情"),
            "dependency summary folds source paths and unreadable-file details without claiming unknown dependencies can be repaired");
        Check(dependencyDetails.All(dependencyPresentation.Details.Contains),
            "dependency details retain every complete original diagnostic and source path");
        var parentheticalDependency = SetupDependencyReviewPresentation.Create([
            @"依赖需要补齐：example（GPU extra） 已安装 1.0，要求 >=2.0（F:\目录（含括号）\requirements.txt:2）"]);
        Check(parentheticalDependency.Summary == "• 依赖需要补齐：example（GPU extra） 已安装 1.0，要求 >=2.0",
            "folding a trailing source preserves package explanation parentheses and handles parentheses inside directories");
        var importGuidance = "无法检查导入环境：此导入环境需要补齐节点、依赖或安装准备。请选择 ComfyUI 目录内的独立 .venv、venv 或独立便携 Python，或先在原环境中手动补齐后重新检查。";
        var sharedInterpreterMessage = importGuidance + dependencyDetails[0] + "；" + dependencyDetails[1]
            + "更新需要本环境内的 .venv、venv 或独立便携 Python；无法确认专用的共享或外部解释器不会被修改。";
        var sharedInterpreterPresentation = SetupDependencyReviewPresentation.ForPreflight([new("import_probe", sharedInterpreterMessage)]);
        Check(sharedInterpreterPresentation.Summary.StartsWith("需处理：此导入环境需要补齐插件或依赖。", StringComparison.Ordinal)
            && sharedInterpreterPresentation.Summary.Contains("独立 .venv、venv 或独立便携 Python")
            && sharedInterpreterPresentation.Summary.Contains("先在原环境中手动补齐，再重新检查")
            && !sharedInterpreterPresentation.Summary.Contains(@"F:\"),
            "shared-interpreter preflight gives both recovery choices and folds the exact preparation list into details");
        Check(sharedInterpreterPresentation.Details == "需处理：" + sharedInterpreterMessage,
            "blocked preflight keeps its exact full diagnostic in expandable details");
        var compositeFileError = importGuidance
            + @"需要安装插件：prompt-assistant（C:\test\onboarding\fixture-environment\custom_nodes\prompt-assistant）；"
            + @"依赖尚未验证：无法读取依赖文件：[Errno 2] No such file or directory: 'C:\test\onboarding\fixture-environment\requirements.txt'（C:\test\onboarding\fixture-environment\requirements.txt）"
            + "更新需要本环境内的 .venv、venv 或独立便携 Python；无法确认专用的共享或外部解释器不会被修改。";
        var compositePresentation = SetupDependencyReviewPresentation.ForPreflight([new("import_probe", compositeFileError)]);
        Check(compositePresentation.Summary == sharedInterpreterPresentation.Summary
            && !compositePresentation.Summary.Contains("Errno") && !compositePresentation.Summary.Contains("requirements.txt")
            && !compositePresentation.Summary.Contains("更新需要")
            && compositePresentation.Details == "需处理：" + compositeFileError,
            "real compound external-Python failure keeps nested file exceptions and adjacent updater text out of the summary while preserving the full diagnostic");

        Check(SetupRouting.Resolve(first, null) == SetupStep.Welcome, "first unconfigured launch enters welcome");
        Check(SetupRouting.Resolve(configured, null) is null, "legacy environment opens overview before its first successful workbench launch");
        configured.SetupComplete = true;
        Check(SetupRouting.Resolve(configured, null) is null, "previously launched environment opens overview");
        configured.SetupComplete = false;
        var unavailable = configured.Clone();
        unavailable.ComfyRoot = Path.Combine(state, "currently-unavailable");
        Check(SetupRouting.Resolve(unavailable, null) is null, "temporarily unavailable legacy root is not silently treated as a fresh installation");

        SetupRecord Record(SetupStep step = SetupStep.Location) => new() { Step = step, Draft = configured.Clone() };
        var saved = Record(SetupStep.Review);
        Check(SetupRouting.Resolve(first, saved) == SetupStep.Location, "review draft must be rechecked after restart");
        Check(SetupRouting.Resolve(configured, saved) == SetupStep.Location, "explicit wizard draft resumes without using stale review approval");
        saved.Step = SetupStep.Welcome;
        Check(SetupRouting.Resolve(first, saved) == SetupStep.Welcome, "welcome draft stays on welcome");
        saved.Step = SetupStep.Location;
        Check(SetupRouting.Resolve(first, saved) == SetupStep.Location, "unconfigured location draft resumes");
        saved.Step = SetupStep.Installing;
        Check(SetupRouting.Resolve(first, saved) == SetupStep.Location, "unstarted installation screen cannot pretend an install has begun");

        saved.Suspended = true;
        Check(SetupRouting.Resolve(first, saved) is null, "deferred wizard restores the overview without claiming completion");
        saved.Suspended = false;
        saved.InstallStarted = true;
        Check(SetupRouting.Resolve(first, saved) == SetupStep.Installing, "interrupted draft can recover even if active config was not saved");
        Check(SetupRouting.Resolve(configured, saved) == SetupStep.Installing, "matching interrupted installation enters recovery");
        configured.InstallPending = true;
        Check(SetupRouting.Resolve(configured, null) == SetupStep.Installing, "legacy pending flag takes priority without an onboarding receipt");
        saved.Step = SetupStep.Finished;
        Check(SetupRouting.Resolve(configured, saved) == SetupStep.Installing, "pending install takes priority over an ended wizard receipt");
        saved.Draft.ComfyRoot = Path.Combine(state, "unrelated");
        saved.Draft.InstallRoot = "";
        Check(SetupRouting.Resolve(configured, saved) == SetupStep.Installing, "pending current environment takes priority over a foreign receipt");
        configured.InstallPending = false;
        Check(SetupRouting.Resolve(configured, saved) is null, "foreign onboarding receipt never hijacks an established environment");

        saved = Record(SetupStep.Complete);
        saved.InstallStarted = saved.InstallFinished = true;
        Check(SetupRouting.Resolve(configured, saved) == SetupStep.Complete && !configured.SetupComplete,
            "installed but never launched wizard restores completion independently of SetupComplete");
        var external = new SetupRecord { Draft = new() { ExternalEnvironmentMode = "readOnly" },
            AssistantStatus = AssistantSetupStatus.Pending };
        foreach (var present in new[] { false, true })
        {
            external.AssistantAvailable = present;
            Check(SetupRouting.AfterModels(external) == SetupStep.Assistant,
                "read-only imports show the optional assistant step, plugin present = " + present);
        }
        external.AssistantStatus = AssistantSetupStatus.Skipped;
        Check(SetupRouting.AfterModels(external) == SetupStep.Complete, "read-only users can skip without writing plugin settings");
        saved.Suspended = true;
        Check(SetupRouting.Resolve(configured, saved) is null && !configured.SetupComplete,
            "suspending failed completion restores overview and preserves the launch flag");
        saved.Suspended = false;
        saved.ModelsPending = saved.OptionalReviewAtCompletion = true;
        Check(SetupRouting.Resolve(configured, saved) == SetupStep.Complete,
            "optional issues discovered on the final page restore that page");
        saved.ModelsPending = saved.OptionalReviewAtCompletion = false;
        saved.Step = SetupStep.Installing;
        Check(SetupRouting.Resolve(configured, saved) == SetupStep.Complete, "finished install receipt repairs an out-of-date installing step");
        saved.Step = SetupStep.Finished;
        Check(SetupRouting.Resolve(configured, saved) is null && !configured.SetupComplete,
            "later-open ends onboarding without changing the successful-launch marker");
        Check(SetupRouting.Resolve(first, saved) == SetupStep.Welcome, "ended receipt without any active environment does not hide welcome");
        saved.Step = SetupStep.Complete;
        Check(SetupRouting.Resolve(first, saved) == SetupStep.Location, "completion cannot be trusted without a matching active configuration");
        File.Delete(python);
        Check(SetupRouting.Resolve(configured, saved) == SetupStep.Location, "deleted Python invalidates restored completion");
        File.WriteAllText(python, "");
        File.Delete(Path.Combine(comfy, "main.py"));
        Check(SetupRouting.Resolve(configured, saved) == SetupStep.Location, "deleted ComfyUI entry point invalidates restored completion");
        File.WriteAllText(Path.Combine(comfy, "main.py"), "# isolated fixture");

        var upgradeModels = new ModelManifest(1, new[] {
            ("anima-base-v1", "diffusion_models", "anima.safetensors"),
            ("qwen-text", "text_encoders", "clip.safetensors"), ("qwen-vae", "vae", "vae.safetensors"),
            ("seedvr2-model", "diffusion_models", "seed.safetensors"), ("seedvr2-vae", "vae", "seedvae.safetensors")
        }.Select(item => new ModelItem(item.Item1, item.Item1, "fixture", item.Item2, item.Item3, [], 1,
            new string('a', 64), ["https://example.com/fixture"], "fixture", "https://example.com/license")).ToArray());
        var upgradeProfile = new ModelProfileDefinition("anima", "Anima", "anima.safetensors", "clip.safetensors", "vae.safetensors", "stable_diffusion", "seed.safetensors", "seedvae.safetensors");
        var upgradeProfiles = new ModelProfilesManifest(1, "anima", [upgradeProfile]);
        var currentPlan = new InstallationPlan(JsonSerializer.Serialize(configured, JsonFile.Options), comfy,
            Path.Combine(state, "runtime"), "anima", false, 0, 0, 0, [])
        {
            ResolvedRuntime = manifest, RuntimeArtifactsIdentity = RuntimeArtifactPlanning.Identity,
            ModelIds = upgradeModels.Models.Select(model => model.Id).ToImmutableArray(),
            MandatoryModelIds = upgradeModels.Models.Select(model => model.Id).ToImmutableArray(),
            FrozenModels = upgradeModels.Models, FrozenProfile = upgradeProfile,
        };
        var legacyPlan = currentPlan with { RuntimeArtifactsIdentity = "", MandatoryModelIds = [],
            ModelIds = currentPlan.ModelIds.Take(3).ToImmutableArray(), FrozenModels = upgradeModels.Models.Take(3).ToArray() };
        var importWithoutDownloads = new SetupRecord
        {
            Fresh = false, InstallStarted = true, InstallFinished = true, Step = SetupStep.Complete,
            Draft = configured.Clone(), AssistantStatus = AssistantSetupStatus.Skipped,
            ReviewedPlan = currentPlan with { ResourcePolicyVersion = SetupFeatures.PolicyVersion,
                ModelIds = [], MandatoryModelIds = [], FrozenModels = [] },
        };
        var restartDir = Directory.CreateDirectory(Path.Combine(state, "failed-completion-restart")).FullName;
        var savedConfig = Path.Combine(restartDir, "launcher.json");
        var savedSetup = Path.Combine(restartDir, "setup.json");
        JsonFile.Write(savedSetup, importWithoutDownloads);
        var failedLaunch = JsonFile.Read<SetupRecord>(savedSetup);
        string browserNotice = "", browserLog = "";
        var opened = WorkbenchBrowser.TryOpen(configured.WebUrl, _ =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(restartDir, "missing-browser.exe"))
                { UseShellExecute = false }), text => browserLog = text, text => browserNotice = text);
        Check(!opened && browserNotice.Contains(configured.WebUrl) && browserLog.Contains("打开浏览器失败"),
            "browser process creation failure offers the ready workbench URL and records the native error");
        failedLaunch.Suspended = true;
        JsonFile.Write(savedSetup, failedLaunch);
        JsonFile.Write(savedConfig, configured);
        var restoredConfig = JsonFile.Read<LauncherConfig>(savedConfig);
        failedLaunch = JsonFile.Read<SetupRecord>(savedSetup);
        Check(SetupRouting.ResolveForRuntime(restoredConfig, failedLaunch, manifest, upgradeModels, upgradeProfiles).Step is null
            && !restoredConfig.SetupComplete && failedLaunch.Step == SetupStep.Complete,
            "after failed browser launch and return to overview, restart reads the retained unfinished setup from disk");
        Check(SetupRouting.ResolveForRuntime(restoredConfig, failedLaunch, manifest, upgradeModels, upgradeProfiles, resume: true).Step == SetupStep.Complete
            && !failedLaunch.Suspended && failedLaunch.ReviewedPlan?.ComfyRoot == configured.ComfyRoot,
            "explicitly reopening the wizard after restart returns to completion for the same reviewed environment");
        Check(SetupCompletionResources.RequiredDownloads(importWithoutDownloads, null, upgradeModels, upgradeProfiles, "anima").Length == 0,
            "import completion with no selected model downloads does not invent mandatory generation models");
        var restoredImport = JsonSerializer.Deserialize<SetupRecord>(JsonSerializer.Serialize(importWithoutDownloads, JsonFile.Options), JsonFile.Options)!;
        restoredImport.MarkRuntimeCheckStale();
        Check(SetupRouting.ResolveForRuntime(configured, restoredImport, manifest, upgradeModels, upgradeProfiles).Step == SetupStep.Complete
            && SetupCompletionResources.RequiredDownloads(restoredImport, null, upgradeModels, upgradeProfiles, "anima").Length == 0,
            "reopened import uses the reviewed empty model contract for both completion preview and launch verification");
        Check(SetupCompletionResources.RequiredDownloads(restoredImport, currentPlan, upgradeModels, upgradeProfiles, "anima").Length == 5,
            "an active frozen installation contract takes priority over an older completion receipt");
        Check(SetupCompletionResources.RequiredDownloads(new() { Fresh = true }, null, upgradeModels, upgradeProfiles, "anima").Length == 3
            && SetupCompletionResources.RequiredDownloads(new() { Fresh = false, ReviewedPlan = legacyPlan }, null, upgradeModels, upgradeProfiles, "anima").Length == 3,
            "unplanned fresh setup and legacy frozen contracts retain their required generation models");
        foreach (var step in new[] { SetupStep.Complete, SetupStep.Assistant })
        {
            var upgradeReceipt = Record(step);
            upgradeReceipt.InstallStarted = upgradeReceipt.InstallFinished = true;
            upgradeReceipt.AssistantStatus = step == SetupStep.Assistant ? AssistantSetupStatus.Pending : AssistantSetupStatus.Skipped;
            upgradeReceipt.ReviewedPlan = legacyPlan;
            var before = JsonSerializer.Serialize(upgradeReceipt, JsonFile.Options);
            var decision = SetupRouting.ResolveForRuntime(configured, upgradeReceipt, manifest, upgradeModels, upgradeProfiles);
            Check(decision.Step == step && !decision.RequiresRepairReview
                && JsonSerializer.Serialize(upgradeReceipt, JsonFile.Options) == before,
                $"legacy unfinished {step} retains its frozen plan without adding optional downloads");
            upgradeReceipt.ReviewedPlan = currentPlan;
            Check(SetupRouting.ResolveForRuntime(configured, upgradeReceipt, manifest, upgradeModels, upgradeProfiles).Step == step,
                $"current unfinished {step} keeps its position and still requires fresh runtime verification");
        }
        var noPlan = Record(SetupStep.Complete);
        noPlan.InstallStarted = noPlan.InstallFinished = true;
        Check(SetupRouting.ResolveForRuntime(configured, noPlan, manifest, upgradeModels, upgradeProfiles)
            is { Step: SetupStep.Location, RequiresRepairReview: true },
            "old completion without a frozen plan returns to review an existing-environment repair");
        noPlan.ReviewedPlan = currentPlan with { ComfyRoot = Path.Combine(state, "foreign") };
        Check(SetupRouting.ResolveForRuntime(configured, noPlan, manifest, upgradeModels, upgradeProfiles).RequiresRepairReview,
            "completion never resumes a frozen plan for a different environment");
        noPlan.Step = SetupStep.Finished;
        noPlan.ReviewedPlan = legacyPlan;
        Check(SetupRouting.ResolveForRuntime(configured, noPlan, manifest, upgradeModels, upgradeProfiles).Step is null
            && !SetupRouting.OpenWizardFromEnvironmentPage(configured, false),
            "an explicitly finished legacy wizard stays closed while its maintenance entry can create a new repair plan");
        saved.SchemaVersion = 999;
        Check(SetupRouting.Resolve(configured, saved) is null && SetupRouting.Resolve(first, saved) == SetupStep.Welcome,
            "unknown onboarding schema is ignored without discarding current configuration");
        saved.SchemaVersion = 1;
        saved.Step = (SetupStep)999;
        Check(SetupRouting.Resolve(first, saved) == SetupStep.Welcome, "unknown wizard step cannot be resumed");
        saved.Step = SetupStep.Location;
        saved.Draft = null!;
        Check(SetupRouting.Resolve(first, saved) == SetupStep.Welcome, "malformed null draft safely falls back to welcome");

        var unexpanded = new LauncherConfig { InstallRoot = environment, SourceId = "mirror" };
        Check(SetupRouting.TargetsMatch(configured, unexpanded, true), "fresh draft install location expands to its actual ComfyUI target");
        Check(!SetupRouting.TargetsMatch(configured, unexpanded, false), "import never derives its target from a stale install-root field");
        var imported = configured.Clone();
        imported.ComfyRoot = comfy.ToUpperInvariant() + Path.DirectorySeparatorChar;
        Check(SetupRouting.TargetsMatch(configured, imported, false), "target identity accepts case and a trailing separator on Windows");
        imported.ComfyRoot = "\0";
        Check(!SetupRouting.TargetsMatch(configured, imported, false), "invalid target path cannot match another environment");

        var draftFile = Path.Combine(state, "onboarding.json");
        saved = Record();
        saved.Fresh = false;
        saved.Draft.SourceId = "mirror";
        saved.Draft.GitMirror = "https://mirror.example.com/{url}";
        JsonFile.Write(draftFile, saved);
        var loaded = JsonFile.Read<SetupRecord>(draftFile);
        Check(!loaded.Fresh && loaded.Step == SetupStep.Location && loaded.Draft.Python == python && loaded.Draft.SourceId == "mirror",
            "onboarding receipt persists import mode, location, source and interpreter");
        Check(configured.SourceId == "china" && configured.GitMirror == "",
            "editing and saving onboarding draft leaves active environment sources unchanged");

        var gate = new SetupReviewGate();
        Check(!gate.CanInstall && !gate.Complete(0, true), "installation is disabled before any review");
        var original = gate.Begin();
        Check(!gate.CanInstall && gate.Complete(original, true) && gate.CanInstall, "successful current review enables install");
        Check(!gate.Complete(original, false) && gate.CanInstall, "duplicate completion cannot overwrite an accepted review");
        gate.Invalidate();
        Check(!gate.CanInstall, "editing location invalidates successful review immediately");
        original = gate.Begin();
        gate.Invalidate();
        Check(!gate.Complete(original, true) && !gate.CanInstall, "source change rejects late success from previous review");
        original = gate.Begin();
        gate.Invalidate();
        Check(!gate.Complete(original, true) && !gate.CanInstall, "installation-mode change rejects late success from previous review");
        original = gate.Begin();
        var latest = gate.Begin();
        Check(!gate.Complete(original, true) && !gate.CanInstall, "overlapping older review cannot enable installation");
        Check(gate.Complete(latest, false) && !gate.CanInstall, "current failed review stays disabled");
        latest = gate.Begin();
        Check(gate.Complete(latest, true) && gate.CanInstall, "retry can authorize installation after a failed review");
        original = gate.Begin();
        latest = gate.Begin();
        Check(gate.Complete(latest, true) && !gate.Complete(original, false) && gate.CanInstall,
            "late failed review cannot replace a newer successful review");

        var events = new List<SetupProgress>();
        var legacyStages = new List<string>();
        var stageConfig = new LauncherConfig { ComfyRoot = comfy };
        using var offline = new Downloads(handler: new FakeHandler { Callback = _ => throw new Exception("No network expected in stage tests.") });
        var installer = new Installer(state, manifest, offline, _ => { }, null, legacyStages.Add, events.Add);
        var stage = typeof(Installer).GetMethod("Stage", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Task RunStage(string id, string label, Func<Task> action) =>
            (Task)stage.Invoke(installer, [stageConfig, id, label, action])!;

        foreach (var id in new[] { "tools", "comfy", "python", "torch", "dependencies", "nodes", "validation" })
        {
            events.Clear();
            await RunStage(id, "fixture " + id, () => Task.CompletedTask);
            Check(events.Select(e => e.Status).SequenceEqual([SetupStageStatus.Running, SetupStageStatus.Complete]) && events.All(e => e.StageId == id),
                $"stage {id} emits stable running and complete events");
        }
        Check(legacyStages.Count == 7 && legacyStages[^1] == "fixture validation", "typed progress preserves the legacy stage callback");
        var journal = JsonFile.Read<InstallJournal>(Path.Combine(state, "install-state.json"));
        Check(journal.Stage == "fixture validation" && journal.Status == "complete", "typed stage events retain the compatible completion journal");

        events.Clear();
        var failure = new IOException("isolated download failure");
        try { await RunStage("nodes", "fixture failure", () => Task.FromException(failure)); throw new Exception("Expected fixture failure."); }
        catch (IOException ex) { Check(ReferenceEquals(ex, failure), "stage failure preserves its original exception"); }
        Check(events.Select(e => e.Status).SequenceEqual([SetupStageStatus.Running, SetupStageStatus.Failed])
            && events[^1].Detail.Contains(failure.Message), "stage failure includes actionable details without claiming completion");
        Check(JsonFile.Read<InstallJournal>(Path.Combine(state, "install-state.json")).Status == "interrupted",
            "failed stage remains resumable in the existing installation journal");
        events.Clear();
        try { await RunStage("tools", "fixture pause", () => Task.FromCanceled(new CancellationToken(true))); throw new Exception("Expected fixture cancellation."); }
        catch (OperationCanceledException) { Check(true, "pausing a stage preserves cancellation for the install task"); }
        Check(events.Select(e => e.Status).SequenceEqual([SetupStageStatus.Running, SetupStageStatus.Paused]),
            "user pause is distinct from stage failure");
        events.Clear();
        await RunStage("nodes", "fixture node retry", () => Task.CompletedTask);
        await RunStage("nodes", "fixture next node", () => Task.CompletedTask);
        Check(events.Count == 4 && events.All(e => e.StageId == "nodes") && events[^1].Status == SetupStageStatus.Complete,
            "retry and multiple node substeps share a stable stage identity");
        return passed;
    }
}
