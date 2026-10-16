using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Anima.Launcher.Core;

if (args.FirstOrDefault() == "--http-fixture")
{
    await ReleaseRecoveryTests.ServeFixture(args);
    return;
}

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
var manifestDir = Path.Combine(root, "launcher", "manifests");
var manifest = JsonFile.Read<RuntimeManifest>(Path.Combine(manifestDir, "launcher-manifest.json"));
var models = JsonFile.Read<ModelManifest>(Path.Combine(manifestDir, "model-manifest.json"));
if (args.Contains("--install-new"))
{
    var targetRoot = Path.GetFullPath(args[Array.IndexOf(args, "--install-new") + 1]);
    var state = targetRoot + ".launcher-state";
    Directory.CreateDirectory(state);
    var configuration = new LauncherConfig { ComfyRoot = targetRoot, ComfyPort = 18888, WebPort = 18990, AppRoot = root, DataDir = Path.Combine(state, "data") };
    using var dl = new Downloads();
    var logLock = new object();
    var installer = new Installer(state, manifest, dl, line => { lock (logLock) File.AppendAllText(Path.Combine(state, "install.log"), line + Environment.NewLine); }, null);
    try { await installer.Install(configuration, true, default); JsonFile.Write(Path.Combine(state, "launcher.json"), configuration); Console.WriteLine("CLEAN INSTALL OK " + state); }
    catch (Exception ex) { Console.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}
var work = Path.Combine(root, "output", "launcher-tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(work);
var count = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception("FAIL: " + label); count++; Console.WriteLine("PASS: " + label); }
async Task Throws(Func<Task> operation, string label) { try { await operation(); } catch { Check(true, label); return; } throw new Exception("Expected failure: " + label); }
async Task ThrowsType<T>(Func<Task> operation, string label) where T : Exception
{
    try { await operation(); }
    catch (T) { Check(true, label); return; }
    throw new Exception("Expected " + typeof(T).Name + ": " + label);
}
count += await ReleaseRecoveryTests.Run(work, manifest);
if (args.Contains("--recovery-only"))
{
    Console.WriteLine($"{count} recovery checks passed; artifacts: {work}");
    return;
}
Safety.Validate(manifest, models); Check(true, "shipped manifests validate");
var requiredV2Classes = new[] { "PromptSelector", "QuickGroupManager", "ResolutionPreset", "PromptExpand", "KreaSeedVarianceEnhancer", "ModelPassThrough", "VRAM_Debug", "Lora Loader (LoraManager)", "Any Switch (rgthree)", "ComfyMathExpression" };
var installedNodeClasses = manifest.Nodes.SelectMany(node => node.RequiredClasses).ToHashSet(StringComparer.Ordinal);
Check(requiredV2Classes.All(installedNodeClasses.Contains), "v2 graph dependencies are pinned and startup-checked");
await Throws(() => Task.FromResult(Safety.Under(work, "../outside")), "path traversal rejected");
await Throws(() => Task.FromResult(Safety.Under(work, @"C:\outside")), "absolute destination rejected");
await Throws(() => Task.FromResult(Safety.Https("http://example.com/model")), "HTTP rejected");
await Throws(() => Task.FromResult(Safety.Https("https://user:secret@example.com/model")), "URL credentials rejected");
await Throws(() => Task.FromResult(Safety.Https("https://example.com/?token=secret")), "query tokens rejected");
await Throws(() => { Safety.Commit("main"); return Task.CompletedTask; }, "unlocked Git ref rejected");
await Throws(() => { Safety.FreeSpace(work, long.MaxValue - 268435456); return Task.CompletedTask; }, "disk space checked before download");

var data = Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();
var hash = Convert.ToHexString(SHA256.HashData(data));
var target = Path.Combine(work, "model.bin");
var handler = new FakeHandler();
handler.Callback = request => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
using var download = new Downloads(_ => "private-token", handler);
await download.Fetch("model", "https://huggingface.co/a", target, data.Length, hash, null, default);
Check(File.ReadAllBytes(target).SequenceEqual(data), "verified download atomically committed");
Check(handler.Seen.Single().Auth == "Bearer private-token", "credential only sent to official host");
await download.Fetch("model", "https://huggingface.co/a", target, data.Length, hash, null, default);
Check(handler.Seen.Count == 1, "existing valid file avoids network");
await Throws(() => download.Fetch("model", "https://huggingface.co/a", target, data.Length, new string('0', 64), null, default), "existing conflicting file preserved");

var resumed = Path.Combine(work, "resumed.bin");
File.WriteAllBytes(resumed + ".part", data[..1000]);
JsonFile.Write(resumed + ".part.json", new ResumeInfo(hash, data.Length, "https://huggingface.co/a", "\"v1\""));
handler.Callback = request =>
{
    Check(request.Headers.Range?.Ranges.Single().From == 1000, "resume sends correct Range");
    var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(data[1000..]) };
    response.Content.Headers.ContentRange = new ContentRangeHeaderValue(1000, data.Length - 1, data.Length);
    return Task.FromResult(response);
};
await download.Fetch("model", "https://hf-mirror.com/a", resumed, data.Length, hash, null, default);
Check(File.ReadAllBytes(resumed).SequenceEqual(data), "resume across mirror keeps content identity");
Check(handler.Seen.Last().Auth is null, "mirror never receives official credential");

var ignored = Path.Combine(work, "ignored-range.bin");
File.WriteAllBytes(ignored + ".part", data[..1000]);
JsonFile.Write(ignored + ".part.json", new ResumeInfo(hash, data.Length, "https://example.com/a", null));
handler.Callback = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
await download.Fetch("model", "https://example.com/a", ignored, data.Length, hash, null, default);
Check(File.ReadAllBytes(ignored).SequenceEqual(data), "server ignoring Range restarts rather than appends");

handler.Callback = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[data.Length]) });
var bad = Path.Combine(work, "bad.bin");
await Throws(() => download.Fetch("bad", "https://example.com/a", bad, data.Length, hash, null, default), "bad SHA rejected");
Check(!File.Exists(bad) && !File.Exists(bad + ".part"), "bad content never becomes final model");
handler.Callback = request => Task.FromResult(request.RequestUri!.Host == "huggingface.co" ? new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://cdn.example.com/a?signature=abc") } } : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
await download.Fetch("redirect", "https://huggingface.co/a", Path.Combine(work, "redirect.bin"), data.Length, hash, null, default);
Check(handler.Seen.Last().Auth is null, "credential stripped on cross-host redirect");
handler.Callback = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("http://example.com/a") } });
await Throws(() => download.Fetch("redirect", "https://example.com/a", Path.Combine(work, "http.bin"), data.Length, hash, null, default), "HTTPS downgrade redirect rejected");
handler.Callback = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
await Throws(() => download.Fetch("auth", "https://civitai.com/a", Path.Combine(work, "auth.bin"), data.Length, hash, null, default), "authentication failure actionable");

var legacy = Path.Combine(work, "clip"); Directory.CreateDirectory(legacy); File.WriteAllBytes(Path.Combine(legacy, "encoder.safetensors"), data);
var item = new ModelItem("enc", "enc", "base", "text_encoders", "encoder.safetensors", [], data.Length, hash, ["https://example.com/a"], "test", "");
var probe = new ProbeResult(work, "", new() { ["text_encoders"] = [Path.Combine(work, "text_encoders"), legacy] });
var scan = await Inventory.Scan(probe, new(1, [item]), true, default);
Check(scan.Single().State == "已校验" && scan.Single().FoundPath == Path.Combine(legacy, "encoder.safetensors"), "legacy clip recognized without duplicate download");
probe.Paths["text_encoders"] = [legacy, Path.Combine(work, "text_encoders")];
scan = await Inventory.Scan(probe, new(1, [item]), false, default);
Check(scan.Single().Destination.StartsWith(legacy), "configured default path honored");
Directory.CreateDirectory(Path.Combine(legacy, "sub")); File.Move(Path.Combine(legacy, "encoder.safetensors"), Path.Combine(legacy, "sub", "encoder.safetensors"));
scan = await Inventory.Scan(probe, new(1, [item]), true, default);
Check(!scan.Single().Missing, "subdirectory model found");
var detectorDir = Path.Combine(work, "external-detectors"); Directory.CreateDirectory(detectorDir);
File.WriteAllBytes(Path.Combine(detectorDir, "face.pt"), data);
probe.Paths["ultralytics_bbox"] = [detectorDir];
scan = await Inventory.Scan(probe, new(1, [item with { Category = "ultralytics", RelativePath = "bbox/face.pt" }]), true, default);
Check(scan.Single().State == "已校验" && scan.Single().Destination == Path.Combine(detectorDir, "face.pt"), "Impact extra detector roots recognized without duplicated bbox prefix");
var portable = Path.Combine(work, "portable"); Directory.CreateDirectory(Path.Combine(portable, "ComfyUI")); Directory.CreateDirectory(Path.Combine(portable, "python_embeded"));
File.WriteAllText(Path.Combine(portable, "ComfyUI", "main.py"), ""); File.WriteAllText(Path.Combine(portable, "ComfyUI", "folder_paths.py"), ""); File.WriteAllText(Path.Combine(portable, "python_embeded", "python.exe"), "");
Check(Inventory.NormalizeRoot(portable) == Path.Combine(portable, "ComfyUI") && Inventory.FindPython(Path.Combine(portable, "ComfyUI"), "").Contains("python_embeded"), "portable parent and bundled Python detected");
var toolsEnv = Environment.GetEnvironmentVariable("ANIMA_TOOLS_DIR");
var legacyToolsEnv = Environment.GetEnvironmentVariable("COMFYUI_ANIMA_TOOLS");
try
{
    Environment.SetEnvironmentVariable("ANIMA_TOOLS_DIR", null);
    Environment.SetEnvironmentVariable("COMFYUI_ANIMA_TOOLS", null);
    string AddTools(string comfyRoot, string name)
    {
        var folder = Path.Combine(comfyRoot, "custom_nodes", name);
        Directory.CreateDirectory(Path.Combine(folder, "js"));
        foreach (var filename in AnimaToolsDiscovery.DataFiles) File.WriteAllText(Path.Combine(folder, "js", filename), "[]");
        return folder;
    }
    var discoveryRoot = Path.Combine(work, "目录 with spaces");
    Check(AnimaToolsDiscovery.Resolve(discoveryRoot) is null, "absent tools remain optional");
    foreach (var name in new[] { AnimaToolsDiscovery.NodeId, AnimaToolsDiscovery.NodeId + "-main", AnimaToolsDiscovery.NodeId + "-master", "renamed 中文 provider" })
    {
        var instance = Path.Combine(discoveryRoot, name);
        var folder = AddTools(instance, name);
        Check(AnimaToolsDiscovery.Resolve(instance) == folder, "data signature discovers " + name);
    }
    var activeRoot = Path.Combine(discoveryRoot, "alias integration");
    var alias = AddTools(activeRoot, AnimaToolsDiscovery.NodeId + "-main");
    AddTools(activeRoot, "ignored.disabled");
    AddTools(activeRoot, "ignored.anima-installing");
    AddTools(activeRoot, ".ignored");
    Check(AnimaToolsDiscovery.Resolve(activeRoot) == alias, "disabled and staging copies do not create ambiguity");
    var animaManifest = manifest with { Nodes = [manifest.Nodes.Single(node => node.Id == AnimaToolsDiscovery.NodeId)] };
    var resolvedNodes = Inventory.ResolveNodeDirectories(activeRoot, animaManifest);
    var discoveredNodes = await Inventory.Nodes(activeRoot, animaManifest, null, default);
    Check(discoveredNodes.Single().Path == alias && discoveredNodes.Single().State != "缺失", "inventory reports the ZIP install's actual directory");
    var installPlan = Installer.PlanNodes(activeRoot, animaManifest).Single();
    Check(installPlan.Directory == alias && installPlan.PreserveExisting, "installer preserves alias installation without cloning a second copy");
    var startArguments = ServiceManager.WebUiArguments(new LauncherConfig { ComfyRoot = activeRoot, AppRoot = root }, "resources.json", resolvedNodes);
    Check(startArguments[startArguments.IndexOf("--anima-tools-dir") + 1] == alias, "WebUI startup receives the same resolved tools directory");
    var missingArguments = ServiceManager.WebUiArguments(new LauncherConfig { ComfyRoot = discoveryRoot, AppRoot = root }, "resources.json", Inventory.ResolveNodeDirectories(discoveryRoot, animaManifest));
    Check(!missingArguments.Contains("--anima-tools-dir"), "missing optional tools do not become an invalid explicit path");

    Environment.SetEnvironmentVariable("ANIMA_TOOLS_DIR", Path.Combine(work, "not-present"));
    Check(AnimaToolsDiscovery.Resolve(activeRoot, alias) == alias, "explicit selection has precedence over environment");
    await Throws(() => Task.FromResult(AnimaToolsDiscovery.Resolve(activeRoot)), "invalid selected environment never falls back to an automatic match");
    Environment.SetEnvironmentVariable("ANIMA_TOOLS_DIR", alias);
    Environment.SetEnvironmentVariable("COMFYUI_ANIMA_TOOLS", Path.Combine(work, "not-present-either"));
    Check(AnimaToolsDiscovery.Resolve(activeRoot) == alias, "primary environment wins over legacy environment");
    await Throws(() => Task.FromResult(AnimaToolsDiscovery.Resolve(activeRoot, Path.Combine(work, "invalid-explicit"))), "invalid explicit selection never falls back to valid environment");
    Environment.SetEnvironmentVariable("ANIMA_TOOLS_DIR", null);
    Environment.SetEnvironmentVariable("COMFYUI_ANIMA_TOOLS", alias);
    Check(AnimaToolsDiscovery.Resolve(activeRoot) == alias, "legacy environment remains supported");
    Environment.SetEnvironmentVariable("COMFYUI_ANIMA_TOOLS", null);
    var incompleteRoot = Path.Combine(discoveryRoot, "incomplete");
    Directory.CreateDirectory(Path.Combine(incompleteRoot, "custom_nodes", AnimaToolsDiscovery.NodeId + "-master", "js"));
    await Throws(() => Task.FromResult(Installer.PlanNodes(incompleteRoot, animaManifest)), "incomplete known installation blocks a duplicate clone");
    var partialRoot = Path.Combine(discoveryRoot, "partially renamed provider");
    var partialJs = Path.Combine(partialRoot, "custom_nodes", "custom provider name", "js");
    Directory.CreateDirectory(partialJs);
    File.WriteAllText(Path.Combine(partialJs, AnimaToolsDiscovery.DataFiles[0]), "[]");
    await Throws(() => Task.FromResult(Installer.PlanNodes(partialRoot, animaManifest)), "a renamed provider with only some data files blocks a duplicate clone");
    AddTools(activeRoot, AnimaToolsDiscovery.NodeId);
    await Throws(() => Task.FromResult(AnimaToolsDiscovery.Resolve(activeRoot)), "multiple valid providers require explicit selection");
    Check(AnimaToolsDiscovery.Resolve(activeRoot, alias) == alias, "explicit selection resolves multiple valid providers");
    var unusedState = Path.Combine(work, "ambiguous-install-state");
    var isolatedHandler = new FakeHandler();
    using var isolatedDownloads = new Downloads(null, isolatedHandler);
    var rejectedInstaller = new Installer(unusedState, animaManifest, isolatedDownloads, _ => { }, null);
    try { await rejectedInstaller.Install(new LauncherConfig { ComfyRoot = activeRoot, Python = "must-not-run.exe" }, false, default); throw new Exception("Ambiguous install unexpectedly ran"); }
    catch (InvalidDataException error) { Check(error.Message.Contains("多个") && isolatedHandler.Seen.Count == 0 && !Directory.Exists(unusedState), "ambiguous installation fails before commands, downloads, or state writes"); }
}
finally
{
    Environment.SetEnvironmentVariable("ANIMA_TOOLS_DIR", toolsEnv);
    Environment.SetEnvironmentVariable("COMFYUI_ANIMA_TOOLS", legacyToolsEnv);
}
await Throws(() => { new LauncherConfig { SchemaVersion = 9 }.Validate(); return Task.CompletedTask; }, "future config schema rejected");

var interrupted = Path.Combine(work, "interrupted.bin");
var retryAttempt = 0;
handler.Callback = request =>
{
    if (retryAttempt++ == 0)
    {
        var content = new StreamContent(new MemoryStream(data[..1000]));
        content.Headers.ContentLength = data.Length;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }
    Check(request.Headers.Range?.Ranges.Single().From == 1000, "early EOF retries from retained bytes");
    var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(data[1000..]) };
    response.Content.Headers.ContentRange = new ContentRangeHeaderValue(1000, data.Length - 1, data.Length);
    return Task.FromResult(response);
};
await download.Fetch("retry", "https://example.com/a", interrupted, data.Length, hash, null, default);
Check(File.ReadAllBytes(interrupted).SequenceEqual(data), "retry completes and validates interrupted download");
var paused = Path.Combine(work, "paused.bin");
using (var cancellation = new CancellationTokenSource())
{
    handler.Callback = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new PausingStream(data, cancellation)) });
    await Throws(() => download.Fetch("pause", "https://example.com/a", paused, data.Length, hash, null, cancellation.Token), "pause interrupts active transfer");
}
Check(!File.Exists(paused) && new FileInfo(paused + ".part").Length == 1000, "pause retains a resumable partial without publishing final file");
Downloads.CancelPartial(paused);
Check(!File.Exists(paused + ".part") && !File.Exists(paused + ".part.json"), "cancel removes only partial state");
var bigData = new byte[65 * 1024 * 1024]; Array.Fill(bigData, (byte)37);
var bigHash = Convert.ToHexString(SHA256.HashData(bigData));
var ranges = new List<(long, long)>();
handler.Callback = request =>
{
    var range = request.Headers.Range!.Ranges.Single(); var from = range.From!.Value; var to = range.To!.Value; ranges.Add((from, to));
    var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(bigData, (int)from, (int)(to - from + 1)) };
    response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, bigData.Length);
    return Task.FromResult(response);
};
await download.Fetch("large", "https://example.com/large", Path.Combine(work, "large.bin"), bigData.Length, bigHash, null, default);
Check(ranges.Count == 3 && ranges[1].Item1 == 32 * 1024 * 1024 && ranges[2].Item2 == bigData.Length - 1, "large artifacts use continuous bounded ranges");
var zip = Path.Combine(work, "evil.zip");
using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) { using var writer = new StreamWriter(archive.CreateEntry("../escaped.txt").Open()); writer.Write("bad"); }
await Throws(() => { Installer.ExtractZip(zip, Path.Combine(work, "extract")); return Task.CompletedTask; }, "zip traversal rejected");

// Deterministic failures must not loop forever behind a retained .part file.
var stale = Path.Combine(work, "stale-range.bin");
File.WriteAllBytes(stale + ".part", data[..1000]);
JsonFile.Write(stale + ".part.json", new ResumeInfo(hash, data.Length, "https://example.com/s", null));
var staleCalls = 0;
handler.Callback = _ =>
{
    staleCalls++;
    if (staleCalls == 1)
    {
        var wrong = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(data) };
        wrong.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, data.Length - 1, data.Length); // From=0 ≠ requested 1000
        return Task.FromResult(wrong);
    }
    // After self-heal the partial is gone; offset 0 issues no Range and gets a full response.
    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
};
await download.Fetch("stale", "https://example.com/s", stale, data.Length, hash, null, default);
Check(staleCalls == 2 && File.ReadAllBytes(stale).SequenceEqual(data), "range mismatch self-heals with one fresh retry");

var managed = Path.Combine(work, "managed-root"); Directory.CreateDirectory(managed);
File.WriteAllText(Path.Combine(managed, ".anima-revision.json"), "{}");
Check(Installer.ConstraintsFor(true, manifest, "torch==9.9.9").SequenceEqual(manifest.PythonConstraints), "managed root uses full lock");
Check(Installer.ConstraintsFor(false, manifest, "torch==9.9.9\n").SequenceEqual(new[] { "torch==9.9.9" }), "imported root freezes only torch pins");

var sam2 = "numpy\n git+https://github.com/facebookresearch/sam2 \nsam2 @ git+https://github.com/facebookresearch/sam2@aaaaaaaa\ngit+https://github.com/facebookresearch/sam2.git@v1.0\nfoo==1";
var pinned = Installer.PinSam2(sam2, manifest.Sam2Commit);
Check(pinned is not null && pinned.Split('\n').Count(line => line == "git+https://github.com/facebookresearch/sam2@" + manifest.Sam2Commit) == 3, "sam2 variants all pinned to locked commit");
Check(Installer.PinSam2("numpy==1\nfoo==2", manifest.Sam2Commit) is null, "requirements without sam2 untouched");

var fakeConfig = new LauncherConfig { AppRoot = Path.Combine(work, "app"), DataDir = Path.Combine(work, "data"), ComfyPort = 18188 };
var goodHealth = JsonDocument.Parse("{\"app_dir\":\"" + fakeConfig.AppRoot.Replace("\\", "\\\\") + "\",\"data_dir\":\"" + fakeConfig.DataDir.Replace("\\", "\\\\") + "\",\"comfy_url\":\"http://127.0.0.1:18188\"}").RootElement.Clone();
Check(ServiceManager.IsOurWebUi(goodHealth, fakeConfig), "matching launcher-health reused");
Check(!ServiceManager.IsOurWebUi(null, fakeConfig), "missing health rejected");
Check(!ServiceManager.IsOurWebUi(JsonDocument.Parse("{}").RootElement, fakeConfig), "empty health object rejected without throwing");
Check(!ServiceManager.IsOurWebUi(JsonDocument.Parse("{\"app_dir\":null,\"comfy_url\":null}").RootElement, fakeConfig), "null fields rejected without throwing");
Check(!ServiceManager.IsOurWebUi(JsonDocument.Parse("[]").RootElement, fakeConfig), "non-object health rejected");

// Version switching helpers: workbench identification + owned-process records.
Check(ServiceManager.IsWorkbenchHealth(JsonDocument.Parse("{\"app\":\"anima-random-studio\",\"app_dir\":\"x\",\"data_dir\":\"y\",\"comfy_url\":\"z\"}").RootElement), "workbench health identified by app field");
Check(!ServiceManager.IsWorkbenchHealth(JsonDocument.Parse("{\"app\":\"comfyui\"}").RootElement), "foreign app rejected");
Check(!ServiceManager.IsWorkbenchHealth(null), "missing health not ours");
Check(!ServiceManager.IsWorkbenchHealth(goodHealth), "health without app field is not identified as workbench");
var legacyOwned = JsonFile.Read<OwnedProcess[]>(WriteJson(work, "owned-legacy.json", "[{\"pid\":123,\"startTimeUtcTicks\":456,\"executable\":\"python.exe\",\"role\":\"WebUI\"}]"));
Check(legacyOwned.Single().ShutdownToken is null, "legacy process record without token deserializes");
var tokenOwned = JsonFile.Read<OwnedProcess[]>(WriteJson(work, "owned-token.json", "[{\"pid\":123,\"startTimeUtcTicks\":456,\"executable\":\"python.exe\",\"role\":\"WebUI\",\"shutdownToken\":\"abc\"}]"));
Check(tokenOwned.Single().ShutdownToken == "abc", "shutdown token round-trips");

// Resource discovery may be incomplete before a user imports a model. Opening the
// workbench must still work, and duplicate basenames must never choose a random file.
var noModels = ServiceManager.ResourceBindings(JsonDocument.Parse("{}").RootElement);
Check(noModels.Count == 0, "empty model inventory does not block service readiness");
var resourceNodes = JsonDocument.Parse("""
    {"UNETLoader":{"input":{"required":{"unet_name":[["A/anima-base-v1.0.safetensors","B/anima-base-v1.0.safetensors","Krea/krea2_turbo_int8_convrot.safetensors"]]}}},
     "CLIPLoader":{"input":{"required":{"clip_name":[["Encoders/qwen_3_06b_base.safetensors"]]}}},
     "VAELoader":{"input":{"required":{"vae_name":[["qwen_image_vae.safetensors"]]}}}}
    """).RootElement;
var availableBindings = ServiceManager.ResourceBindings(resourceNodes);
Check(!availableBindings.ContainsKey("anima-base-v1.0.safetensors") && availableBindings["krea2_turbo_int8_convrot.safetensors"] == "Krea/krea2_turbo_int8_convrot.safetensors", "ambiguous base model omitted while available profile stays usable");
Check(availableBindings["qwen_3_06b_base.safetensors"] == "Encoders/qwen_3_06b_base.safetensors" && availableBindings["qwen_image_vae.safetensors"] == "qwen_image_vae.safetensors", "available encoder and shared VAE preserve their actual relative paths");
var exactNodes = JsonDocument.Parse("""{"UNETLoader":{"input":{"required":{"unet_name":[["anima-base-v1.0.safetensors","Other/anima-base-v1.0.safetensors"]]}}}}""").RootElement;
Check(ServiceManager.ResourceBindings(exactNodes)["anima-base-v1.0.safetensors"] == "anima-base-v1.0.safetensors", "unambiguous exact model path wins over matching subfolder basename");

foreach (var terminal in new[] { "completed", "stopped", "error", "cancelled" })
    Check(RuntimePolicies.WorkbenchActivity(JsonDocument.Parse("{\"batch\":{\"status\":\"" + terminal + "\"},\"queue\":[]}").RootElement) == WorkbenchActivity.Idle, "retained " + terminal + " batch permits version switching");
foreach (var activeStatus in new[] { "running", "stopping" })
    Check(RuntimePolicies.WorkbenchActivity(JsonDocument.Parse("{\"batch\":{\"status\":\"" + activeStatus + "\"},\"queue\":[]}").RootElement) == WorkbenchActivity.Busy, activeStatus + " batch blocks version switching");
Check(RuntimePolicies.WorkbenchActivity(JsonDocument.Parse("{\"batch\":null,\"queue\":[]}").RootElement) == WorkbenchActivity.Idle, "unused workbench is idle");
Check(RuntimePolicies.WorkbenchActivity(JsonDocument.Parse("{\"batch\":null,\"queue\":[{}]}").RootElement) == WorkbenchActivity.Busy, "queued work blocks version switching even without active batch");
Check(RuntimePolicies.WorkbenchActivity(null) == WorkbenchActivity.Unknown && RuntimePolicies.WorkbenchActivity(JsonDocument.Parse("{}").RootElement) == WorkbenchActivity.Unknown && RuntimePolicies.WorkbenchActivity(JsonDocument.Parse("{\"batch\":{},\"queue\":[]}").RootElement) == WorkbenchActivity.Unknown, "missing or malformed activity never authorizes an automatic stop");
var versionsRoot = Path.Combine(work, "versions");
var oldVersion = Path.Combine(versionsRoot, "app-old");
var newVersion = Path.Combine(versionsRoot, "app-new");
Check(RuntimePolicies.ShouldSwitchBundledVersion(oldVersion, newVersion, versionsRoot, true, false), "managed old snapshot may upgrade when unpinned");
Check(!RuntimePolicies.ShouldSwitchBundledVersion(oldVersion, newVersion, versionsRoot, true, true), "user rollback pin survives the next launcher start");
Check(!RuntimePolicies.ShouldSwitchBundledVersion(oldVersion, newVersion, versionsRoot, false, false) && !RuntimePolicies.ShouldSwitchBundledVersion(newVersion, newVersion, versionsRoot, true, false), "first setup and current snapshot do not trigger an upgrade");
Check(!RuntimePolicies.ShouldSwitchBundledVersion(root, newVersion, versionsRoot, true, false) && !RuntimePolicies.ShouldSwitchBundledVersion(Path.Combine(work, "versions-other", "app-old"), newVersion, versionsRoot, true, false), "source checkouts and sibling paths are never automatic upgrade targets");
var scriptPath = Path.Combine(oldVersion, "run.py");
Check(RuntimePolicies.CommandLineMatchesSnapshot("python.exe \"" + scriptPath + "\" --port 8190", oldVersion), "snapshot ownership matches the absolute run.py argument");
Check(!RuntimePolicies.CommandLineMatchesSnapshot("python.exe -c print(1) --output \"" + oldVersion + "\"", oldVersion) && !RuntimePolicies.CommandLineMatchesSnapshot("python.exe \"" + scriptPath + ".other\"", oldVersion), "directory mentions and similar script names do not authorize process termination");
var originalConfig = new LauncherConfig { ComfyRoot = Path.Combine(work, "original"), ComfyArguments = ["--lowvram"], PinnedAppRoot = oldVersion };
var taskConfig = RuntimePolicies.SnapshotConfig(originalConfig);
originalConfig.ComfyRoot = Path.Combine(work, "changed");
originalConfig.ComfyArguments.Add("--cpu");
Check(taskConfig.ComfyRoot != originalConfig.ComfyRoot && taskConfig.ComfyArguments.SequenceEqual(new[] { "--lowvram" }) && taskConfig.PinnedAppRoot == oldVersion, "task snapshot preserves paths, rollback policy, and independent argument list");
await ThrowsType<InvalidDataException>(() => Task.FromResult(RuntimePolicies.SnapshotConfig(new LauncherConfig { WebPort = 8188 })), "invalid draft fails before becoming a task snapshot");
var legacyConfig = JsonSerializer.Deserialize<LauncherConfig>("{\"setupComplete\":true}", JsonFile.Options)!;
Check(!legacyConfig.StartOnLaunch && legacyConfig.SelectedProfile == "anima" && legacyConfig.PinnedAppRoot == "", "legacy configuration defaults to an explicit start and Anima profile");
var serviceRuntime = Path.Combine(work, "service-runtime");
var serviceConfig = new LauncherConfig { ManagedComfy = true, RuntimeRoot = serviceRuntime };
var serviceEnvironment = ServiceManager.BuildRuntimeEnvironment(serviceConfig);
Check(serviceEnvironment["HF_HOME"] == Path.Combine(serviceRuntime, "cache", "huggingface")
    && serviceEnvironment["HF_HUB_CACHE"] == serviceEnvironment["HUGGINGFACE_HUB_CACHE"]
    && serviceEnvironment["TORCH_HOME"] == Path.Combine(serviceRuntime, "cache", "torch")
    && serviceEnvironment["TEMP"] == Path.Combine(serviceRuntime, "temp") && serviceEnvironment["TMP"] == serviceEnvironment["TEMP"],
    "managed services route model caches and temporary files to the selected runtime volume");
Check(serviceEnvironment.Where(pair => pair.Key != "LORA_MANAGER_PORTABLE").All(pair => pair.Value.StartsWith(serviceRuntime + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    && !Directory.Exists(serviceRuntime), "runtime environment planning confines all overrides to the chosen root without creating directories");
var inheritedHfHome = Environment.GetEnvironmentVariable("HF_HOME");
var serviceInfo = Commands.Info("python.exe", [], work, serviceEnvironment);
Check(serviceInfo.Environment["HF_HOME"] == serviceEnvironment["HF_HOME"] && serviceInfo.Environment["TMP"] == serviceEnvironment["TMP"]
    && Environment.GetEnvironmentVariable("HF_HOME") == inheritedHfHome, "runtime cache overrides apply only to child process configuration");
var legacyServiceEnvironment = ServiceManager.BuildRuntimeEnvironment(new LauncherConfig { ManagedComfy = true });
Check(legacyServiceEnvironment.Count == 1 && legacyServiceEnvironment["LORA_MANAGER_PORTABLE"] == "1", "legacy managed environments without a runtime path retain their cache defaults");
var importedServiceEnvironment = ServiceManager.BuildRuntimeEnvironment(new LauncherConfig { RuntimeRoot = serviceRuntime });
var importedServiceInfo = Commands.Info("python.exe", [], work, importedServiceEnvironment);
importedServiceInfo.Environment.TryGetValue("HF_HOME", out var importedHfHome);
Check(importedServiceEnvironment.Count == 0 && importedHfHome == inheritedHfHome,
    "imported environments preserve inherited model-cache settings even when an installer cache root is configured");
Check(ServiceManager.BuildRuntimeEnvironment(new LauncherConfig { RuntimeRoot = serviceRuntime }, hasManagedMarker: true)["HF_HOME"] == serviceEnvironment["HF_HOME"],
    "a managed repository marker restores runtime cache routing for an older saved configuration");
var profiles = JsonFile.Read<ModelProfilesManifest>(Path.Combine(manifestDir, "model-profiles.json"));
var animaModels = profiles.RequiredModels(models, "anima");
var kreaModels = profiles.RequiredModels(models, "krea2");
Check(animaModels.Length == 3 && animaModels.All(m => m.Id is "anima-base-v1" or "qwen-text" or "qwen-vae"), "Anima install selects only its three required base resources");
Check(kreaModels.Length == 3 && kreaModels.All(m => m.Id is "krea2-turbo" or "qwen3vl-krea2" or "qwen-vae"), "Krea install selects its profile resources and shared VAE");
await ThrowsType<InvalidDataException>(() => Task.FromResult(profiles.RequiredModels(models, "unknown")), "unknown model profile is rejected before installation");
var installParent = Path.Combine(work, "fresh-install-parent");
var installDraft = new LauncherConfig { InstallRoot = installParent, SelectedProfile = "anima", ComfyArguments = ["--lowvram"] };
var planState = Path.Combine(work, "plan-state");
var installationPlan = InstallationPlanner.Create(installDraft, manifest, planState, true, models, profiles);
Check(installationPlan.ComfyRoot == Path.Combine(installParent, "ComfyUI") && installationPlan.RuntimeRoot == Path.Combine(installParent, "runtime") && installationPlan.CreateConfig().Python == Path.Combine(installParent, "ComfyUI", ".venv", "Scripts", "python.exe"), "fresh install derives sibling environment and runtime directories from one parent");
installDraft.InstallRoot = Path.Combine(work, "later-edit");
installDraft.ComfyArguments.Add("--cpu");
var disposablePlanConfig = installationPlan.CreateConfig();
disposablePlanConfig.ComfyArguments.Clear();
Check(installationPlan.ComfyRoot == Path.Combine(installParent, "ComfyUI") && installationPlan.CreateConfig().ComfyArguments.SequenceEqual(new[] { "--lowvram" }), "installation plan stays unchanged after UI edits and returned-config mutations");
Check(!Directory.Exists(installParent) && !Directory.Exists(planState), "creating an installation plan performs no installation or directory writes");
var volumeEstimate = InstallationPlanner.EstimateVolumes(installationPlan);
Check(volumeEstimate.Length == 1 && volumeEstimate[0].RequiredBytes == installationPlan.EnvironmentBytes + installationPlan.RuntimeBytes + 256L * 1024 * 1024, "same-volume installation includes both environment and runtime reserves once");
var legacyInstallRoot = Path.Combine(work, "legacy-comfy-root");
var legacyPlan = InstallationPlanner.Create(new LauncherConfig { ComfyRoot = legacyInstallRoot }, manifest, planState, true);
Check(legacyPlan.ComfyRoot == legacyInstallRoot && legacyPlan.RuntimeRoot == planState, "legacy direct ComfyUI install path remains compatible");
await ThrowsType<InvalidDataException>(() => Task.FromResult(InstallationPlanner.Create(new LauncherConfig { ComfyRoot = legacyInstallRoot, RuntimeRoot = Path.Combine(legacyInstallRoot, "runtime") }, manifest, planState, true)), "nested runtime directory is rejected before installation");
Check(InstallationPlanner.IsNvidiaDriverSupported("570.65") && InstallationPlanner.IsNvidiaDriverSupported("580.10") && !InstallationPlanner.IsNvidiaDriverSupported("570.64") && !InstallationPlanner.IsNvidiaDriverSupported("not-a-driver"), "CUDA driver preflight accepts the minimum and rejects older or invalid versions");

var remainingTarget = Path.Combine(work, "remaining-download.bin");
Check(Downloads.RemainingBytes(remainingTarget, data.Length, hash) == data.Length, "missing download reserves its full size");
File.WriteAllBytes(remainingTarget + ".part", data[..1000]);
JsonFile.Write(remainingTarget + ".part.json", new ResumeInfo(hash, data.Length, "https://example.com/model", null));
Check(Downloads.RemainingBytes(remainingTarget, data.Length, hash) == data.Length - 1000, "matching resume identity credits only the retained partial bytes");
JsonFile.Write(remainingTarget + ".part.json", new ResumeInfo(new string('0', 64), data.Length, "https://example.com/model", null));
Check(Downloads.RemainingBytes(remainingTarget, data.Length, hash) == data.Length, "wrong partial checksum identity receives no disk-space credit");
File.WriteAllText(remainingTarget + ".part.json", "broken");
Check(Downloads.RemainingBytes(remainingTarget, data.Length, hash) == data.Length, "corrupt partial metadata retains a conservative full allocation");
JsonFile.Write(remainingTarget + ".part.json", new ResumeInfo(hash, 500, "https://example.com/model", null));
Check(Downloads.RemainingBytes(remainingTarget, 500, hash) == 500, "oversized partial receives no disk-space credit");
File.WriteAllBytes(remainingTarget, data);
Check(Downloads.RemainingBytes(remainingTarget, data.Length, hash) == 0, "existing final file requires no duplicate allocation before integrity verification");

var resumeManifest = manifest with
{
    Nodes = [],
    Tools = [new Artifact("resume-tool", "https://example.com/tool.zip", data.Length, hash, "tool.exe")],
    TorchWheels = [new Artifact("resume-wheel", "https://example.com/torch.whl", data.Length, hash, "torch.whl")],
};
var resumedConfig = new LauncherConfig { InstallRoot = Path.Combine(work, "resume-install") };
var resumeState = Path.Combine(work, "resume-install-state");
var emptyResumePlan = InstallationPlanner.Create(resumedConfig, resumeManifest, resumeState, true);
var runtimeCache = Path.Combine(emptyResumePlan.RuntimeRoot, "cache");
var wheelCache = Path.Combine(runtimeCache, "wheels", "torch.whl");
Directory.CreateDirectory(Path.GetDirectoryName(wheelCache)!);
File.WriteAllBytes(wheelCache + ".part", data[..1000]);
JsonFile.Write(wheelCache + ".part.json", new ResumeInfo(hash, data.Length, "https://example.com/torch.whl", null));
var partialResumePlan = InstallationPlanner.Create(resumedConfig, resumeManifest, resumeState, true);
Check(partialResumePlan.RuntimeBytes == emptyResumePlan.RuntimeBytes - 1000 && partialResumePlan.EstimatedDownloadBytes == emptyResumePlan.EstimatedDownloadBytes - 1000, "resumed installation reserves only missing wheel bytes");
File.WriteAllBytes(wheelCache, data);
File.WriteAllBytes(Path.Combine(runtimeCache, "resume-tool.zip"), data);
var cachedPlan = await InstallationPlanner.RefreshSpaceEstimateAsync(emptyResumePlan, default);
Check(cachedPlan.EstimatedDownloadBytes == 0 && cachedPlan.RuntimeBytes == cachedPlan.RuntimeReserveBytes, "verified complete caches remove download allocation from the frozen plan");
var corruptWheel = data.ToArray(); corruptWheel[0] ^= 1;
File.WriteAllBytes(wheelCache, corruptWheel);
await ThrowsType<InvalidDataException>(() => InstallationPlanner.RefreshSpaceEstimateAsync(emptyResumePlan, default), "same-size corrupt cache blocks preflight instead of being trusted");
Check(File.ReadAllBytes(wheelCache).SequenceEqual(corruptWheel), "space preflight never deletes or rewrites a corrupt cache");
File.WriteAllBytes(wheelCache, data);

var resumedEnvironment = emptyResumePlan.CreateConfig();
var resumedVenv = Path.Combine(resumedEnvironment.ComfyRoot, ".venv");
Directory.CreateDirectory(Path.Combine(resumedVenv, "Scripts"));
File.WriteAllText(Path.Combine(resumedVenv, "Scripts", "python.exe"), "fixture");
File.WriteAllText(Path.Combine(resumedVenv, "pyvenv.cfg"), "home = fixture");
JsonFile.Write(Path.Combine(resumedEnvironment.ComfyRoot, ".anima-revision.json"), resumeManifest.Comfy);
File.WriteAllLines(Path.Combine(resumedEnvironment.ComfyRoot, ".anima-constraints.txt"), resumeManifest.PythonConstraints);
foreach (var (package, version) in new[] { ("torch", resumeManifest.TorchVersion), ("torchvision", resumeManifest.TorchVisionVersion), ("torchaudio", resumeManifest.TorchAudioVersion) })
{
    var metadata = Path.Combine(resumedVenv, "Lib", "site-packages", package + "-" + version + ".dist-info");
    Directory.CreateDirectory(metadata);
    File.WriteAllText(Path.Combine(metadata, "RECORD"), "");
    File.WriteAllText(Path.Combine(metadata, "METADATA"), "Name: " + package + "\nVersion: " + version + "\n");
}
JsonFile.Write(Path.Combine(resumeState, "install-state.json"), new InstallJournal(resumedEnvironment.ComfyRoot, "验证运行依赖", "interrupted", DateTime.UtcNow));
var completedResumePlan = InstallationPlanner.Create(resumedConfig, resumeManifest, resumeState, true);
Check(completedResumePlan.EnvironmentBytes < 512L * 1024 * 1024 && completedResumePlan.RuntimeBytes < 512L * 1024 * 1024, "completed core stages and valid caches allow low-space continuation without a second full install reserve");
InstallationPlanner.PreserveCompletedStages(resumedEnvironment, resumeManifest, resumeState);
JsonFile.Write(Path.Combine(resumeState, "install-state.json"), new InstallJournal(resumedEnvironment.ComfyRoot, "准备隔离安装工具", "running", DateTime.UtcNow));
var restartedResumePlan = InstallationPlanner.Create(resumedConfig, resumeManifest, resumeState, true);
Check(restartedResumePlan.EnvironmentBytes == completedResumePlan.EnvironmentBytes, "completed stage evidence survives a restarted journal's earlier stage");
var changedLock = resumeManifest with { PythonConstraints = resumeManifest.PythonConstraints.Append("new-test-dependency==1.0").ToArray() };
var changedResumePlan = InstallationPlanner.Create(resumedConfig, changedLock, resumeState, true);
Check(changedResumePlan.EnvironmentBytes > completedResumePlan.EnvironmentBytes + 8L * 1024 * 1024 * 1024, "changed dependency lock does not reuse another manifest's completion record");

File.WriteAllText(Path.Combine(resumedEnvironment.ComfyRoot, "main.py"), "fixture");
File.WriteAllText(Path.Combine(resumedEnvironment.ComfyRoot, "folder_paths.py"), "fixture");
var unrelatedDefaultModel = Path.Combine(resumedEnvironment.ComfyRoot, "models", animaModels[0].Category, animaModels[0].RelativePath);
Directory.CreateDirectory(Path.GetDirectoryName(unrelatedDefaultModel)!);
File.WriteAllText(unrelatedDefaultModel, "import must not verify this model");
var importedPlan = InstallationPlanner.Create(resumedEnvironment, resumeManifest, resumeState, false, models, profiles);
Check(importedPlan.Downloads.Length == 0 && importedPlan.EstimatedDownloadBytes == 0, "import repair does not reserve optional base models or fresh-install tools");
var importedPrepared = await InstallationPlanner.PreparePlanAsync(importedPlan, models, profiles, "missing-probe-script.py", default);
Check(ReferenceEquals(importedPrepared, importedPlan) && (await InstallationPlanner.RefreshSpaceEstimateAsync(importedPlan, default)).EstimatedDownloadBytes == 0, "import planning skips model probes and unrelated corrupt default model files");
var deferredPathsPreflight = await InstallationPlanner.PreflightAsync(importedPlan with { ModelPathsDeferred = true }, default);
Check(deferredPathsPreflight.CanInstall && deferredPathsPreflight.Issues.Single(issue => issue.Code == "model_paths_deferred").Blocking == false,
    "deferred model path discovery remains visible without blocking dependency recovery");

var smallModels = models with { Models = models.Models.Select(m => m with { Size = data.Length, Sha256 = hash }).ToArray() };
var externalModelRoot = Path.Combine(work, "external-model-paths");
var existingExternalModel = Path.Combine(externalModelRoot, "diffusion_models", "nested", animaModels[0].RelativePath);
Directory.CreateDirectory(Path.GetDirectoryName(existingExternalModel)!);
File.WriteAllBytes(existingExternalModel, corruptWheel);
var separateVolume = Path.GetPathRoot(work)!.Equals(@"Z:\", StringComparison.OrdinalIgnoreCase) ? @"Y:\" : @"Z:\";
var separateModelRoot = Path.Combine(separateVolume, "anima-preflight-test-" + Guid.NewGuid().ToString("N"));
var externalProbe = new ProbeResult(resumedEnvironment.ComfyRoot, resumedEnvironment.Python, new Dictionary<string, string[]>
{
    ["diffusion_models"] = [Path.Combine(externalModelRoot, "diffusion_models")],
    ["text_encoders"] = [Path.Combine(separateModelRoot, "text_encoders")],
    ["vae"] = [Path.Combine(separateModelRoot, "vae")],
});
var relocatedPlan = await InstallationPlanner.ResolveModelDestinationsAsync(
    InstallationPlanner.Create(resumedConfig, resumeManifest, resumeState, true, smallModels, profiles), externalProbe, smallModels, profiles, default);
Check(relocatedPlan.Downloads.Single(a => a.Destination == existingExternalModel).InEnvironment && relocatedPlan.EstimatedDownloadBytes == 2 * data.Length, "resumed planning reuses an existing model from effective paths without hashing it twice");
var relocatedVolumes = InstallationPlanner.EstimateVolumes(relocatedPlan);
Check(relocatedVolumes.Single(v => v.Volume == separateVolume).RequiredBytes == 2 * data.Length + 256L * 1024 * 1024
    && relocatedVolumes.Single(v => v.Volume.Equals(Path.GetPathRoot(work), StringComparison.OrdinalIgnoreCase)).RequiredBytes == relocatedPlan.EnvironmentReserveBytes + relocatedPlan.RuntimeReserveBytes + 256L * 1024 * 1024,
    "each effective model destination is budgeted on its own volume while component reserves stay on their installation volumes");
await ThrowsType<InvalidDataException>(() => InstallationPlanner.RefreshSpaceEstimateAsync(relocatedPlan, default), "preflight verifies the existing effective model path instead of a hardcoded models directory");
File.WriteAllBytes(existingExternalModel, data);
Check((await InstallationPlanner.RefreshSpaceEstimateAsync(relocatedPlan, default)).EstimatedDownloadBytes == 2 * data.Length, "verified external models need no duplicate download allocation");

var metadataRoot = Path.Combine(work, "model-metadata");
Directory.CreateDirectory(metadataRoot);
var skippedMetadata = await ModelMetadata.EnsureDefaultAsync(new LauncherConfig { SelectedProfile = "krea2" }, manifest, models, profiles, Path.Combine(metadataRoot, "krea.safetensors"), default);
Check(!skippedMetadata.Created, "Krea profile never receives automatic Anima metadata");
var preservedModel = Path.Combine(metadataRoot, "preserved", profiles.Get("anima").ModelName);
Directory.CreateDirectory(Path.GetDirectoryName(preservedModel)!);
var preservedSidecar = Path.ChangeExtension(preservedModel, ".metadata.json");
File.WriteAllText(preservedSidecar, "user-authored metadata");
var preservedMetadata = await ModelMetadata.EnsureDefaultAsync(new LauncherConfig(), manifest, models, profiles, preservedModel, default);
Check(!preservedMetadata.Created && File.ReadAllText(preservedSidecar) == "user-authored metadata", "existing model metadata is preserved without reading or rewriting the model");
var invalidModel = Path.Combine(metadataRoot, profiles.Get("anima").ModelName);
File.WriteAllText(invalidModel, "incorrect model fixture");
await ThrowsType<InvalidDataException>(() => ModelMetadata.EnsureDefaultAsync(new LauncherConfig(), manifest, models, profiles, invalidModel, default), "unverified default model cannot receive trusted Anima metadata");
Check(!File.Exists(Path.ChangeExtension(invalidModel, ".metadata.json")), "invalid default model leaves no metadata sidecar");

using (var cancelledStop = new CancellationTokenSource())
{
    cancelledStop.Cancel();
    var cancelledState = Path.Combine(work, "cancelled-stop");
    var cancelledManager = new ServiceManager(cancelledState, _ => { });
    await ThrowsType<OperationCanceledException>(() => cancelledManager.StopOwnedAsync(ct: cancelledStop.Token), "cancelled stop request propagates even with no owned processes");
    Check(!Directory.Exists(cancelledState), "cancelled empty stop never writes ownership state");
}

var damagedState = Path.Combine(work, "damaged-process-state");
Directory.CreateDirectory(damagedState);
File.WriteAllText(Path.Combine(damagedState, "processes.json"), "{damaged");
var damagedLogs = new List<string>();
var recoveredManager = new ServiceManager(damagedState, damagedLogs.Add);
recoveredManager.RecoverOwned();
Check(!recoveredManager.IsOwnedRunning("WebUI") && !recoveredManager.IsOwnedRunning("ComfyUI") && !File.Exists(Path.Combine(damagedState, "processes.json")) && Directory.GetFiles(damagedState, "processes.json.corrupt-*").Length == 1, "damaged process record is backed up without acquiring any process ownership");
Check(damagedLogs.Count == 1, "damaged process record gives an actionable recovery log");
var invalidOwnerState = Path.Combine(work, "invalid-process-owner");
JsonFile.Write(Path.Combine(invalidOwnerState, "processes.json"), new[] { new OwnedProcess(Environment.ProcessId, 1, "wrong.exe", "WebUI") });
var invalidOwner = new ServiceManager(invalidOwnerState, _ => { });
invalidOwner.RecoverOwned();
Check(!invalidOwner.IsOwnedRunning("WebUI"), "PID match with wrong start time or executable never acquires ownership");

// An isolated loopback listener intentionally never replies; no user's services
// are touched and no HTTP installation or GPU work is needed for these checks.
var silentListener = new TcpListener(IPAddress.Loopback, 0);
silentListener.Start();
try
{
    var silentPort = ((IPEndPoint)silentListener.LocalEndpoint).Port;
    var silentUrl = $"http://127.0.0.1:{silentPort}/health";
    await ThrowsType<TimeoutException>(() => ServiceManager.Json(silentUrl, requestTimeout: TimeSpan.FromMilliseconds(120)), "HTTP deadline is reported as timeout, not user pause");
    using var userCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(120));
    await ThrowsType<OperationCanceledException>(() => ServiceManager.Json(silentUrl, userCancellation.Token, TimeSpan.FromSeconds(5)), "HTTP user cancellation propagates without timeout reclassification");
    using var stoppedProbe = new CancellationTokenSource();
    stoppedProbe.Cancel();
    await ThrowsType<OperationCanceledException>(() => ServiceManager.PortOpen(silentPort, stoppedProbe.Token), "cancelled port probe stops immediately");
}
finally { silentListener.Stop(); }

var downloadTimeoutHandler = new FakeHandler { Callback = _ => Task.FromException<HttpResponseMessage>(new TaskCanceledException("simulated response timeout")) };
using (var timeoutDownload = new Downloads(handler: downloadTimeoutHandler))
    await ThrowsType<TimeoutException>(() => timeoutDownload.Fetch("timeout", "https://example.com/timeout", Path.Combine(work, "download-timeout.bin"), data.Length, hash, null, default), "exhausted download timeout is distinct from a user pause");
var expectedVersion = Assembly.GetEntryAssembly()!.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? Assembly.GetEntryAssembly()!.GetName().Version!.ToString();
Check(downloadTimeoutHandler.UserAgents.All(agent => agent == "AnimaRandomStudio/" + expectedVersion), "download user agent follows the executable build version");

var cmd = Commands.Find("cmd.exe");
if (cmd is not null) await Throws(async () => { await Commands.Run(cmd, ["/c", "ping", "-n", "30", "127.0.0.1"], work, null, CancellationToken.None, timeout: TimeSpan.FromMilliseconds(500)); }, "command timeout kills hung process");

if (args.Contains("--live"))
{
    var comfy = args[Array.IndexOf(args, "--live") + 1];
    var python = Inventory.FindPython(comfy, "");
    var actual = await Inventory.Probe(comfy, python, Path.Combine(root, "launcher", "scripts", "probe_comfy.py"), default);
    var found = await Inventory.Scan(actual, models, false, default);
    JsonFile.Write(Path.Combine(work, "live-inventory.json"), found);
    Check(found.All(i => !i.Missing), "live imported ComfyUI model inventory complete");
    Check(found.Single(i => i.Model.Id == "qwen-text").FoundPath!.Contains("clip"), "live legacy encoder recognized");
    var config = new LauncherConfig { ComfyRoot = comfy, Python = python, AppRoot = root, DataDir = Path.Combine(work, "data"), WebPort = 18990, ComfyPort = args.Contains("--owned") ? 18888 : 8188 };
    var manager = new ServiceManager(work, Console.WriteLine);
    try
    {
    await manager.Start(config, manifest, default);
    Check(await ServiceManager.Json(config.WebUrl + "/api/status") is { } live && live.GetProperty("online").GetBoolean(), "real WebUI sees imported ComfyUI");
    if (args.Contains("--generate"))
    {
        var queue = (await ServiceManager.Json(config.ComfyUrl + "/queue"))!.Value;
        Check(queue.GetProperty("queue_running").GetArrayLength() == 0 && queue.GetProperty("queue_pending").GetArrayLength() == 0, "GPU queue idle before acceptance image");
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
        using var response = await client.PostAsync(config.WebUrl + "/api/batches", new StringContent("{\"count\":1,\"width\":512,\"height\":512,\"steps\":4,\"extra_prompt\":\"1girl, portrait, gentle smile, simple background, safe\",\"hires\":{\"enabled\":false}}", System.Text.Encoding.UTF8, "application/json"));
        var text = await response.Content.ReadAsStringAsync();
        Check(response.IsSuccessStatusCode, "acceptance image submitted: " + text);
        var complete = false;
        for (var attempt = 0; attempt < 180; attempt++)
        {
            await Task.Delay(2000);
            var current = (await ServiceManager.Json(config.WebUrl + "/api/batches/current"))!.Value;
            var batch = current.GetProperty("batch");
            var state = batch.GetProperty("status").GetString();
            if (state == "completed") { JsonFile.Write(Path.Combine(work, "generation-result.json"), current); complete = true; break; }
            if (state is "failed" or "error") throw new Exception(current.ToString());
        }
        Check(complete, "real GPU generation completed");
    }
    }
    finally { var recovered = new ServiceManager(work, Console.WriteLine); recovered.RecoverOwned(); recovered.StopOwned(); }
    Check(!await ServiceManager.PortOpen(config.WebPort), "recovered owned WebUI stopped");
    Check(await ServiceManager.PortOpen(config.ComfyPort) == !args.Contains("--owned"), "ComfyUI ownership respected on shutdown");
}
Console.WriteLine($"{count} checks passed; artifacts: {work}");

static string WriteJson(string dir, string name, string content) { var path = Path.Combine(dir, name); File.WriteAllText(path, content); return path; }

sealed class FakeHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, Task<HttpResponseMessage>> Callback = null!;
    public List<(string Url, string? Auth)> Seen = [];
    public List<string> UserAgents = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Seen.Add((request.RequestUri!.ToString(), request.Headers.Authorization?.ToString()));
        UserAgents.Add(request.Headers.UserAgent.ToString());
        return Callback(request);
    }
}

sealed class PausingStream(byte[] bytes, CancellationTokenSource source) : MemoryStream(bytes)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (Position >= 1000) source.Cancel();
        cancellationToken.ThrowIfCancellationRequested();
        return base.ReadAsync(buffer[..Math.Min(buffer.Length, 1000)], cancellationToken);
    }
}
