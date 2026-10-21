using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Anima.Launcher.Core;

internal static class ModelPackageTests
{
    public static async Task<int> Run(string work, RuntimeManifest runtime, ModelManifest taggers)
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); count++; Console.WriteLine("PASS: " + name); }
        async Task Fails(Func<Task> operation, string name)
        { try { await operation(); } catch { Check(true, name); return; } throw new Exception("Expected failure: " + name); }
        Safety.Validate(runtime, taggers);
        Check(taggers.Models.Length == 8 && taggers.Models.All(model => model.IsPackage && model.CanDownload), "eight complete tagger model packages validate");
        Check(taggers.Models.Single(model => model.Recommended).Name == "pixai-tagger-v0.9-timm", "PixAI 0.9 recommendation does not auto-select models");
        Check(taggers.Models.All(model => model.Files.All(file => file.Urls.All(url => url.Contains("/resolve/" + model.Revision + "/")))), "all tagger downloads use immutable source revisions");
        Check(taggers.Models.Single(model => model.Name == "pixai-tagger-v1.0-timm").Files.Any(file => file.Path == "timm_arch.py"), "PixAI 1.0 custom architecture is locked in its package");
        Check(runtime.Nodes.Single(node => node.Id == TaggerRuntime.NodeId).RequiredClasses.Length == 0, "optional tagger never blocks generation startup");
        var bytes = new Dictionary<string, byte[]>
        {
            ["config.json"] = "{\"architecture\":\"fixture\"}"u8.ToArray(),
            ["model.safetensors"] = Enumerable.Range(0, 8192).Select(i => (byte)(i % 251)).ToArray(),
            ["selected_tags.csv"] = "tag,category\nbird,0"u8.ToArray(),
            ["timm_arch.py"] = "# fixture custom architecture"u8.ToArray(),
        };
        var files = bytes.Select(pair => new ModelFile(pair.Key, pair.Value.Length, Convert.ToHexString(SHA256.HashData(pair.Value)), ["https://example.com/" + pair.Key])).ToArray();
        var model = new ModelItem("package", "test", "optional", "wd_taggers", "test", [], files.Sum(file => file.Size), "", [], "test", "")
        { Files = files, Revision = new string('a', 40) };
        Safety.Validate(runtime, new(1, [model]));
        await Fails(() => { Safety.Validate(runtime, new(1, [model with { Files = [files[0], files[0]] }])); return Task.CompletedTask; }, "duplicate package file rejected");
        await Fails(() => { Safety.Validate(runtime, new(1, [model with { Files = [files[0] with { Path = "../config.json" }] }])); return Task.CompletedTask; }, "package file traversal rejected");
        await Fails(() => { Safety.Validate(runtime, new(1, [model with { Revision = "main" }])); return Task.CompletedTask; }, "mutable package revision rejected");
        var root = Path.Combine(work, "package-cases");
        var runtimeRoot = Path.Combine(root, "runtime-root");
        var runtimeTarget = Path.Combine(runtimeRoot, "models", "wd_taggers");
        string? Resolve(object folders, object argv) => TaggerRuntime.ResolveDirectory(JsonSerializer.SerializeToElement(folders),
            JsonSerializer.SerializeToElement(new { system = new { argv } }));
        var runtimeFolders = new { wd_taggers = new[] { runtimeTarget } };
        var mainScript = Path.Combine(runtimeRoot, "main.py");
        Check(Resolve(runtimeFolders, new[] { mainScript }) == runtimeTarget, "runtime tagger path agrees with absolute entry script");
        Check(Resolve(new { wd_taggers = new[] { runtimeTarget, runtimeTarget + Path.DirectorySeparatorChar } }, new[] { mainScript }) == runtimeTarget,
            "equivalent runtime paths deduplicate after normalization");
        Check(Resolve(new { custom_nodes = new[] { Path.Combine(runtimeRoot, "custom_nodes") } }, new[] { mainScript }) is null,
            "missing runtime wd_taggers registration never guesses from custom_nodes");
        Check(Resolve(new { wd_taggers = new[] { runtimeTarget, Path.Combine(root, "extra-taggers") } }, new[] { mainScript }) is null,
            "multiple runtime tagger roots are ambiguous");
        Check(Resolve(new { wd_taggers = new[] { Path.Combine(root, "other-taggers") } }, new[] { mainScript }) is null,
            "runtime tagger root must match actual startup root");
        Check(Resolve(runtimeFolders, new[] { "main.py" }) is null, "relative entrypoint cannot establish runtime model root");
        Check(Resolve(runtimeFolders, new[] { mainScript, "--models-directory", Path.Combine(runtimeRoot, "models") }) == runtimeTarget,
            "absolute models-directory confirms runtime tagger target");
        Check(Resolve(runtimeFolders, new[] { mainScript, "--base-directory=" + runtimeRoot }) == runtimeTarget,
            "attached absolute base-directory confirms runtime tagger target");
        Check(Resolve(runtimeFolders, new[] { mainScript, "--models-directory", Path.Combine(runtimeRoot, "models"), "--base-directory", Path.Combine(root, "other") }) == runtimeTarget,
            "explicit models-directory takes precedence after both overrides validate");
        Check(Resolve(runtimeFolders, new[] { mainScript, "--models-directory", "models" }) is null,
            "relative directory override cannot be inferred from configured environment");
        Check(Resolve(runtimeFolders, new[] { mainScript, "--models-directory", Path.Combine(runtimeRoot, "models"), "--base-directory", "relative" }) is null,
            "invalid lower-priority override still makes runtime path uncertain");
        Check(Resolve(runtimeFolders, new[] { mainScript, "--base-directory", runtimeRoot, "--base-directory=" + runtimeRoot }) is null,
            "duplicate runtime directory flags are rejected");
        Check(Resolve(runtimeFolders, new[] { mainScript, "--models-directory" }) is null,
            "missing runtime directory value is rejected");
        Check(Resolve(runtimeFolders, new object[] { mainScript, 5 }) is null,
            "non-string runtime argv is rejected");
        var taggerRoot = Path.Combine(root, "wd_taggers");
        var destination = Path.Combine(taggerRoot, "test");
        Directory.CreateDirectory(Path.Combine(taggerRoot, "unrelated"));
        foreach (var file in bytes) File.WriteAllBytes(Path.Combine(taggerRoot, "unrelated", file.Key), file.Value);
        var probe = new ProbeResult(root, "", new() { ["wd_taggers"] = [taggerRoot] });
        var scanned = (await Inventory.Scan(probe, new(1, [model]), false, default)).Single();
        Check(scanned.Missing && scanned.FoundPath is null && scanned.Destination == destination, "other model config filenames never satisfy an exact model package");
        var unknown = (await Inventory.Scan(new(root, "", new()), new(1, [model]), false, default)).Single();
        Check(unknown.State == "目录待确认" && unknown.Destination == "", "unknown runtime directory prevents guessed download targets");
        using var handler = new PackageHandler();
        handler.Callback = (request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes[Path.GetFileName(request.RequestUri!.AbsolutePath)]) });
        using var downloads = new Downloads(handler: handler);
        await ModelPackages.Fetch(downloads, model, destination, new(), null, default);
        Check(await ModelPackages.State(model, destination, default) == "已校验", "all package files verified before publishing complete directory");
        Check(!Directory.Exists(ModelPackages.Staging(model, destination)), "successful staging directory atomically moved away");
        var requests = handler.Requests;
        await ModelPackages.Fetch(downloads, model, destination, new(), null, default);
        Check(handler.Requests == requests, "valid complete package avoids network");
        File.WriteAllText(Path.Combine(destination, "preprocess.json"), "{}");
        Check(await ModelPackages.State(model, destination, default) == "文件不完整", "unlocked optional preprocess file cannot change tagger behavior");
        await ModelPackages.Fetch(downloads, model, destination, new(), null, default);
        Check(!File.Exists(Path.Combine(destination, "preprocess.json")), "repair publishes clean package while preserving extra-file original in backup");
        File.Delete(Path.Combine(destination, "timm_arch.py"));
        Check(await ModelPackages.State(model, destination, default) == "文件不完整", "missing architecture file disables complete model");
        await ModelPackages.Fetch(downloads, model, destination, new(), null, default);
        Check(await ModelPackages.State(model, destination, default) == "已校验" && Directory.EnumerateDirectories(Path.Combine(taggerRoot + ".anima-packages", "backups")).Any(), "repair publishes complete package and retains damaged original directory");
        File.WriteAllText(Path.Combine(destination, "config.json"), new string('x', (int)files[0].Size));
        Check(await ModelPackages.State(model, destination, default) == "哈希异常", "equal-size changed package config fails hash verification");
        var badDestination = Path.Combine(taggerRoot, "bad-hash");
        handler.Callback = (request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[bytes[Path.GetFileName(request.RequestUri!.AbsolutePath)].Length]) });
        await Fails(() => ModelPackages.Fetch(downloads, model, badDestination, new(), null, default), "bad package hash rejected");
        Check(!Directory.Exists(badDestination), "failed package never becomes runtime-visible");
        var cancelDestination = Path.Combine(taggerRoot, "cancel");
        using var cancel = new CancellationTokenSource();
        handler.Callback = (request, _) =>
        {
            var name = Path.GetFileName(request.RequestUri!.AbsolutePath);
            if (name == "model.safetensors") { cancel.Cancel(); throw new OperationCanceledException(cancel.Token); }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes[name]) });
        };
        await Fails(() => ModelPackages.Fetch(downloads, model, cancelDestination, new(), null, cancel.Token), "cancel interrupts package download");
        var stage = ModelPackages.Staging(model, cancelDestination);
        Check(!Directory.Exists(cancelDestination) && File.Exists(Path.Combine(stage, "config.json")), "cancel retains verified package files outside ComfyUI model directory");
        File.WriteAllText(Path.Combine(stage, "config.json"), "damaged stage");
        var weight = files.Single(file => file.Path == "model.safetensors");
        File.WriteAllBytes(Path.Combine(stage, weight.Path) + ".part", bytes[weight.Path][..1024]);
        JsonFile.Write(Path.Combine(stage, weight.Path) + ".part.json", new ResumeInfo(weight.Sha256, weight.Size, weight.Urls[0], "\"v1\""));
        var resumed = false;
        handler.Callback = (request, _) =>
        {
            var name = Path.GetFileName(request.RequestUri!.AbsolutePath);
            var offset = request.Headers.Range?.Ranges.Single().From ?? 0;
            if (name == weight.Path) resumed = offset == 1024;
            var response = new HttpResponseMessage(offset > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = new ByteArrayContent(bytes[name][(int)offset..]) };
            if (offset > 0) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, bytes[name].Length - 1, bytes[name].Length);
            return Task.FromResult(response);
        };
        await ModelPackages.Fetch(downloads, model, cancelDestination, new(), null, default);
        Check(resumed && await ModelPackages.State(model, cancelDestination, default) == "已校验", "retry resumes package partial and reuses verified prior files");
        var aliasRoot = Path.Combine(root, "node-alias");
        var alias = Path.Combine(aliasRoot, "custom_nodes", "ComfyUI-WD-Timm-Tagger-main");
        Directory.CreateDirectory(alias);
        File.WriteAllText(Path.Combine(alias, "__init__.py"), "");
        File.WriteAllText(Path.Combine(alias, "nodes.py"), "class WDTimmTagger:\n WD_TAGGER_DIR = 'models'");
        Check(TaggerRuntime.ResolveNode(aliasRoot) == alias, "tagger ZIP aliases detected by node signature");
        var plan = Installer.PlanNodes(aliasRoot, runtime with { Nodes = [runtime.Nodes.Single(node => node.Id == TaggerRuntime.NodeId)] }).Single();
        Check(plan.PreserveExisting && plan.Directory == alias, "existing optional tagger install preserved without duplicate clone");
        return count;
    }

    private sealed class PackageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Callback { get; set; } = null!;
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Requests++; return Callback(request, ct); }
    }
}
