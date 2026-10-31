using System.Text.Json;
using System.Text.Json.Nodes;
using Anima.Launcher.Core;

internal static class RuntimeCapabilityTests
{
    public static int Run(string work, RuntimeManifest runtime)
    {
        var count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }
        var baseline = RuntimeCapabilities.RequiredClasses(runtime);
        Check(baseline.Contains("SesquiLatentUpscale") && baseline.Contains("SeedVR2Conditioning") && baseline.Contains("VAEEncode"), "fresh-install baseline covers actual default and image-to-image nodes");
        Check(!baseline.Contains("ForbiddenVisionFaceProcessorIntegrated") && !baseline.Contains("RTXVideoSuperResolution") && !baseline.Contains("WDTimmTagger"), "unselected optional features do not block baseline generation");
        Check(RuntimeCapabilities.RequiredClasses(runtime, ["forbidden-vision-detect"]).Contains("ForbiddenVisionFaceProcessorIntegrated"), "selected face models require their actual node");
        Check(RuntimeCapabilities.RequiredClasses(runtime, [RuntimeCapabilities.NvidiaCapabilityId]).Contains("DLSS5NeuralRender"), "supported NVIDIA preparation requires the complete two-node chain");
        Check(RuntimeCapabilities.RequiredClasses(runtime, ["tagger-wd-vit-tagger-v3"]).Contains("WDTimmTagger"), "selected tagger is checked even though it is optional at general startup");

        var info = new JsonObject();
        void Node(string name, params (string, JsonNode)[] fields) => info[name] = new JsonObject { ["input"] = new JsonObject { ["required"] = new JsonObject(fields.Select(field => KeyValuePair.Create<string, JsonNode?>(field.Item1, field.Item2))) } };
        JsonArray Type(string name) => new(name);
        JsonArray Choices(params string[] values) => new(new JsonArray(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()));
        Node("SesquiLatentUpscale", ("latent", Type("LATENT")), ("scale", Type("FLOAT")), ("half_precision", Type("BOOLEAN")), ("model_format", Choices("Wan 2.1")));
        Node("SeedVR2Conditioning", ("model", Type("MODEL")), ("vae_conditioning", Type("LATENT")));
        Node("SeedVR2Preprocess", ("resized_images", Type("IMAGE")));
        Node("SeedVR2PostProcessing", ("images", Type("IMAGE")), ("original_resized_images", Type("IMAGE")), ("color_correction_method", Choices("lab", "none")));
        Node("CLIPLoader", ("type", Choices("stable_diffusion", "krea2")));
        Node("PromptExpand", ("template_only", Type("BOOLEAN")));
        Node("ImageCaptionNode", ("image", Type("IMAGE")), ("vlm_service", Choices("fixture")), ("custom_rule", Type("BOOLEAN")), ("custom_rule_content", Type("STRING")));
        JsonElement Snapshot() => JsonSerializer.SerializeToElement(info);
        Check(RuntimeCapabilities.ObjectInfoIssues(Snapshot()).Length == 0, "complete baseline schemas pass without face or NVIDIA nodes");
        var seedFields = (JsonObject)info["SeedVR2PostProcessing"]!["input"]!["required"]!;
        seedFields["color_correction_method"] = new JsonArray("COMBO", new JsonObject
        { ["default"] = "lab", ["multiselect"] = false, ["options"] = new JsonArray("lab", "wavelet", "adain", "none") });
        Check(RuntimeCapabilities.ObjectInfoIssues(Snapshot()).Length == 0,
            "ComfyUI V3 COMBO metadata accepts SeedVR2's actual lab default without changing workbench settings");
        ((JsonObject)seedFields["color_correction_method"]![1]!)["options"] = new JsonArray("none");
        Check(RuntimeCapabilities.ObjectInfoIssues(Snapshot()).Any(issue => issue.Contains("color_correction_method")),
            "a V3 default label alone cannot pass readiness when lab is absent from actual choices");
        ((JsonObject)seedFields["color_correction_method"]![1]!)["options"] = JsonValue.Create("lab");
        Check(RuntimeCapabilities.ObjectInfoIssues(Snapshot()).Any(issue => issue.Contains("color_correction_method")),
            "malformed V3 choice metadata still fails readiness");
        ((JsonObject)seedFields["color_correction_method"]![1]!)["options"] = new JsonArray("lab", "none");
        Check(RuntimeCapabilities.ObjectInfoIssues(Snapshot(), ["forbidden-vision-detect"]).Any(issue => issue.Contains("res_2m_ode")), "face selection requires RES4LYF enum registration rather than class import alone");
        ((JsonObject)info["SesquiLatentUpscale"]!["input"]!["required"]!).Remove("scale");
        Check(RuntimeCapabilities.ObjectInfoIssues(Snapshot()).Any(issue => issue.Contains("scale")), "registered but incompatible Sesqui node fails readiness");
        ((JsonObject)info["SeedVR2Conditioning"]!["input"]!["required"]!).Remove("vae_conditioning");
        Check(RuntimeCapabilities.ObjectInfoIssues(Snapshot()).Any(issue => issue.Contains("vae_conditioning")), "all SeedVR2 names alone do not prove the conditioning contract");

        var directory = Path.Combine(work, "nvidia-requirements");
        Directory.CreateDirectory(directory);
        var child = Path.Combine(directory, "child.txt");
        var root = Path.Combine(directory, "requirements.txt");
        File.WriteAllText(child, "nvidia-vfx\nnumpy\n");
        File.WriteAllText(root, "-r child.txt\nPillow\n");
        var prepared = ComfyDependencyResolver.PrepareNodeRequirements(root, Path.Combine(directory, "prepared"), null);
        var preparedFiles = ComfyDependencyResolver.RequirementFiles([prepared]);
        var fixedText = preparedFiles.Select(File.ReadAllText).Single(text => text.Contains("nvidia-vfx @"));
        Check(fixedText.Split('\n').Count(line => line.StartsWith("nvidia-vfx @")) == 3 && fixedText.Contains("\nnumpy\n"), "fixed NVIDIA sources preserve adjacent and nested dependencies");
        Check(File.ReadAllText(child) == "nvidia-vfx\nnumpy\n" && prepared != root, "pinning never changes node requirements or its git cleanliness");
        Check(fixedText.Contains("pypi.nvidia.com") && fixedText.Contains("#sha256=") && !fixedText.Contains("extra-index-url"), "VFX resolves official verified wheels without enabling an alternate global index");
        Check(NvidiaRuntimePreparation.WheelFor("cp310")!.FileName.Contains("cp310-cp310")
            && NvidiaRuntimePreparation.WheelFor("cp311")!.FileName.Contains("cp311-cp311")
            && NvidiaRuntimePreparation.WheelFor("cp312")!.FileName.Contains("cp312-abi3")
            && NvidiaRuntimePreparation.WheelFor("cp313")!.FileName.Contains("cp312-abi3")
            && NvidiaRuntimePreparation.WheelFor("cp39") is null, "imported interpreters select the correct official ABI without changing Python");
        var probe = new NvidiaRuntimeProbe(true, "rtx40", "NVIDIA RTX 4060", "cp311", "3.11.9", "2.13", "0.1.0.1", true);
        var verified = new NvidiaRuntimeStatus(true, true, "rtx40", "verified") { Gpu = probe.Gpu };
        Check(NvidiaRuntimePreparation.IsReadyFor(probe, verified)
            && !NvidiaRuntimePreparation.IsReadyFor(probe, null)
            && !NvidiaRuntimePreparation.IsReadyFor(probe with { VfxVersion = "wrong" }, verified)
            && !NvidiaRuntimePreparation.IsReadyFor(probe with { Gpu = "NVIDIA RTX 4070" }, verified)
            && !NvidiaRuntimePreparation.IsReadyFor(probe with { VcRuntime = false }, verified),
            "native readiness requires matching GPU, official SDK, VC runtime, and a completed validation receipt");
        Check(NvidiaRuntimePreparation.IsReadyFor(probe with { Supported = false }, null), "unsupported NVIDIA hardware does not block base generation");
        return count;
    }
}
