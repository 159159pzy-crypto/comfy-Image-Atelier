using System.Security.Cryptography;
using Anima.Launcher.Core;

internal static class SetupCompletionTests
{
    public static async Task<int> Run(string work)
    {
        var count = 0;
        void Check(bool value, string label)
        {
            if (!value) throw new Exception("FAIL: " + label);
            count++;
            Console.WriteLine("PASS: " + label);
        }
        void Reject(Action action, string label)
        {
            try { action(); }
            catch (InvalidOperationException) { Check(true, label); return; }
            throw new Exception("Expected rejection: " + label);
        }

        var directory = Path.Combine(work, "setup-completion", "existing comfy 中文");
        Directory.CreateDirectory(directory);
        var payload = "locked fixture model"u8.ToArray();
        var sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        ModelItem Model(string id) => new(id, id, "基础模型", "diffusion_models", id + ".safetensors", [],
            payload.Length, sha, ["https://huggingface.co/example/resolve/locked/" + id], "test", "");
        var required = new[] { Model("base"), Model("clip"), Model("vae") };
        ModelStatus Missing(ModelItem model) => new(model, "缺失", null, Path.Combine(directory, model.RelativePath));
        SetupModelReview Review(IEnumerable<ModelStatus> statuses) => SetupModelReview.Create(required, statuses,
            status => Downloads.RemainingBytes(status.Destination, status.Model.Size, status.Model.Sha256),
            status => string.Join("\n", status.Model.Urls));
        var missing = required.Select(Missing).ToArray();
        var review = Review(missing);
        Check(review.Readiness == SetupModelReadiness.NeedsDownload && review.DownloadableItems.Length == 3,
            "completion identifies only explicit required model downloads");
        Check(review.RemainingBytes == payload.Length * 3 && review.Items.All(item => item.Source.StartsWith("https://"))
            && review.Items[0].OpenDirectory == directory, "model preview includes sources, real destinations and remaining bytes");
        Check(!Directory.EnumerateFiles(directory).Any(), "previewing models never starts a download or creates model files");
        Check(Review([]).Readiness == SetupModelReadiness.ScanFailed
            && Review(missing.Take(2)).Readiness == SetupModelReadiness.ScanFailed,
            "empty and incomplete scans never report ready");
        Check(SetupModelReview.Create([], [], _ => 0, _ => "").Readiness == SetupModelReadiness.ScanFailed,
            "an empty required-model set cannot accidentally authorize readiness");

        var corrupt = missing[0] with { State = "哈希异常", FoundPath = missing[0].Destination };
        review = Review([corrupt, missing[1], missing[2]]);
        Check(review.Readiness == SetupModelReadiness.NeedsAttention && !review.Items[0].CanDownload
            && review.Items[0].Issue.Contains("保留") && review.DownloadableItems.Length == 2,
            "damaged existing single files are protected while safe missing models remain downloadable");
        Check(Review([missing[0] with { State = "目录待确认", Destination = "" }, missing[1], missing[2]])
            is { Readiness: SetupModelReadiness.NeedsAttention } unknownPath
            && unknownPath.Items[0].OpenDirectory == "" && !unknownPath.Items[0].CanDownload,
            "an ambiguous directory never becomes a guessed download location");
        var manual = required[0] with { Sha256 = "", Urls = [] };
        var manualReview = SetupModelReview.Create([manual], [Missing(manual)], _ => 0, _ => "");
        Check(manualReview.Readiness == SetupModelReadiness.NeedsAttention && manualReview.DownloadableItems.IsEmpty,
            "manual-only models remain actionable without exposing an unsafe download");
        var package = required[0] with { RelativePath = "package", Files = [new("model.bin", payload.Length, sha, required[0].Urls)], Revision = "locked" };
        var packagePath = Path.Combine(directory, "package");
        var packageReview = SetupModelReview.Create([package], [new(package, "文件不完整", packagePath, packagePath)], _ => payload.Length, _ => "fixture");
        Check(packageReview.Readiness == SetupModelReadiness.NeedsDownload && packageReview.Items[0].CanDownload
            && packageReview.Items[0].OpenDirectory == packagePath, "incomplete model packages can safely resume using their package directory");
        Check(Review([missing[0] with { State = "unexpected" }, missing[1], missing[2]]).Readiness == SetupModelReadiness.ScanFailed,
            "unknown inventory states do not silently become usable models");

        File.WriteAllBytes(missing[1].Destination + ".part", payload[..4]);
        JsonFile.Write(missing[1].Destination + ".part.json", new ResumeInfo(sha, payload.Length, required[1].Urls[0], null));
        Check(Review(missing).RemainingBytes == payload.Length * 3 - 4, "preview accounts for reusable partial downloads");

        var active = new LauncherConfig
        {
            ComfyRoot = directory, Python = Path.Combine(directory, "python.exe"),
            RuntimeRoot = directory + ".anima-runtime", SourceId = "china", ModelMirror = "https://hf-mirror.com",
            ComfyArguments = ["--lowvram"], InstallPending = false,
        };
        var plan = new SetupModelDownloadPlan(active, review);
        Check(plan.ModelIds.SequenceEqual(["clip", "vae"]), "download confirmation captures only safe reviewed model identities");
        Check(plan.Matches(active) && !plan.CreateConfig().InstallPending && plan.CreateConfig().InstallRoot == "",
            "model download plan preserves an imported environment without becoming a fresh installation");
        active.ModelMirror = "https://changed.example.com";
        active.ComfyArguments.Add("--cpu");
        Check(!plan.Matches(active) && plan.CreateConfig().ModelMirror == "https://hf-mirror.com"
            && plan.CreateConfig().ComfyArguments.SequenceEqual(["--lowvram"]),
            "source and runtime edits cannot mutate a confirmed model download snapshot");
        var retry = plan.CreateConfig();
        retry.ComfyRoot = "changed";
        Check(plan.CreateConfig().ComfyRoot == directory, "resuming gets an independent copy of the reviewed runtime configuration");
        var pending = active.Clone();
        pending.InstallPending = true;
        Reject(() => _ = new SetupModelDownloadPlan(pending, review), "model supplementation never treats an unfinished installation as complete");
        Reject(() => _ = new SetupModelDownloadPlan(active, Review([])), "failed model checks cannot authorize any downloads");
        Reject(() => _ = new SetupModelDownloadPlan(active, manualReview), "manual-only review does not create an empty download operation");

        var beforeReview = SetupCompletionPresentation.Resolve(SetupModelReadiness.NeedsDownload);
        var confirmed = SetupCompletionPresentation.Resolve(SetupModelReadiness.NeedsDownload, reviewVisible: true, canDownload: true);
        Check(beforeReview.PrimaryAction == SetupCompletionAction.ReviewModels && confirmed.PrimaryAction == SetupCompletionAction.DownloadModels,
            "completion requires a visible review before offering the download action");
        Check(beforeReview.AllowOpenAnyway && confirmed.AllowOpenAnyway,
            "model recommendations never force downloads before opening the workbench");
        Check(SetupCompletionPresentation.Resolve(SetupModelReadiness.NeedsAttention, true, true).PrimaryAction == SetupCompletionAction.DownloadModels
            && SetupCompletionPresentation.Resolve(SetupModelReadiness.NeedsAttention, true, false).PrimaryAction == SetupCompletionAction.ReviewModels,
            "mixed model issues offer safe downloads while manual-only issues offer recheck");
        foreach (var state in new[] { SetupModelReadiness.Unknown, SetupModelReadiness.ScanFailed })
            Check(SetupCompletionPresentation.Resolve(state).PrimaryAction == SetupCompletionAction.ReviewModels
                && SetupCompletionPresentation.Resolve(state).AllowOpenAnyway, $"{state} stays distinct from ready but permits explicit opening");
        Check(SetupCompletionPresentation.Resolve(SetupModelReadiness.Checking).PrimaryAction == SetupCompletionAction.None,
            "in-flight checks cannot expose a stale confirmation action");
        Check(SetupCompletionPresentation.Resolve(SetupModelReadiness.NeedsDownload, canResume: true).PrimaryLabel == "继续下载"
            && SetupCompletionPresentation.Resolve(SetupModelReadiness.NeedsDownload, canResume: true, hasError: true).PrimaryLabel == "重试下载",
            "paused and failed model tasks lead to the model resume path");
        Check(SetupCompletionPresentation.Resolve(SetupModelReadiness.NeedsDownload, startFailed: true).PrimaryAction == SetupCompletionAction.StartWorkbench,
            "a failed explicit workbench start retries startup rather than downloading models");

        // Real inventory hashing catches same-size damage; successful scans restore readiness.
        var probe = new ProbeResult(directory, active.Python, new() { ["diffusion_models"] = [directory] });
        foreach (var model in required) File.WriteAllBytes(Path.Combine(directory, model.RelativePath), payload);
        var manifest = new ModelManifest(1, required);
        var valid = Review(await Inventory.Scan(probe, manifest, true, default));
        Check(valid.Readiness == SetupModelReadiness.Ready && valid.DownloadableItems.IsEmpty
            && SetupCompletionPresentation.Resolve(valid.Readiness).PrimaryAction == SetupCompletionAction.StartWorkbench,
            "verified required files change completion to opening the workbench");
        var tagger = required[0] with { Category = "wd_taggers" };
        Check(!ModelProbePolicy.NeedsTaggerRuntime(required) && ModelProbePolicy.NeedsTaggerRuntime([required[0], tagger]),
            "required generation models bypass optional tagger discovery while tagger selections retain runtime probing");
        foreach (var name in new[] { "tagger-copy-a", "tagger-copy-b" })
        {
            var node = Path.Combine(directory, "custom_nodes", name);
            Directory.CreateDirectory(node);
            File.WriteAllText(Path.Combine(node, "__init__.py"), "# duplicate optional-node fixture");
            File.WriteAllText(Path.Combine(node, "nodes.py"), "class WDTimmTagger: pass\nWD_TAGGER_DIR = 'fixture'");
        }
        var duplicateRejected = false;
        try { TaggerRuntime.ResolveNode(directory); }
        catch (InvalidDataException) { duplicateRejected = true; }
        Check(duplicateRejected, "fixture reproduces the duplicate optional-node failure of a full inventory scan");
        Check(Review(await Inventory.Scan(probe, manifest, true, default)).Readiness == SetupModelReadiness.Ready,
            "completion model-only verification still succeeds with unrelated duplicate optional nodes");
        var broken = payload.ToArray(); broken[0] ^= 0xff;
        File.WriteAllBytes(missing[0].Destination, broken);
        var damaged = Review(await Inventory.Scan(probe, manifest, true, default));
        Check(damaged.Readiness == SetupModelReadiness.NeedsAttention && damaged.Items[0].Status.State == "哈希异常"
            && File.ReadAllBytes(missing[0].Destination).SequenceEqual(broken), "same-size corruption is detected and the existing file remains intact");
        File.WriteAllText(Path.Combine(directory, "main.py"), "# completion fixture");
        File.WriteAllText(active.Python, "");
        var receipt = new SetupRecord { Fresh = false, Step = SetupStep.Complete, InstallStarted = true, InstallFinished = true, Draft = active.Clone() };
        var receiptFile = Path.Combine(directory, "onboarding.json");
        JsonFile.Write(receiptFile, receipt);
        Check(SetupRouting.Resolve(active, JsonFile.Read<SetupRecord>(receiptFile)) == SetupStep.Complete && !active.SetupComplete,
            "restart during model preparation restores completion without marking startup successful or installing again");
        return count;
    }
}
