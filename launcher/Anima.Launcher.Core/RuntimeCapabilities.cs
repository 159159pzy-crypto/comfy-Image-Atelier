using System.Text.Json;

namespace Anima.Launcher.Core;

/// <summary>Installation checks for the graphs shipped by the workbench, including optional stages.</summary>
public static class RuntimeCapabilities
{
    public const string NvidiaCapabilityId = "nvidia-runtime";
    public static bool IsFaceModel(string id) => id is "face-refine" or "forbidden-vision-detect" or "forbidden-vision-segment" or "forbidden-vision-corrector";
    public static bool IsTaggerModel(string id) => id.StartsWith("tagger-", StringComparison.OrdinalIgnoreCase)
        || id.StartsWith("wd-", StringComparison.OrdinalIgnoreCase) || id.Contains("timm", StringComparison.OrdinalIgnoreCase);

    public static string[] RequiredClasses(RuntimeManifest runtime, IEnumerable<string>? selectedModelIds = null)
    {
        var selected = (selectedModelIds ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var classes = runtime.Comfy.RequiredClasses.Concat(runtime.Nodes.Where(node => node.Id switch
        {
            "forbidden-vision" or "RES4LYF" => selected.Any(IsFaceModel),
            "nvidia-rtx-vsr-pro" or "DLSS5-Comfyui" => selected.Contains(NvidiaCapabilityId),
            "ComfyUI-WD-Timm-Tagger" => selected.Any(IsTaggerModel),
            _ => true,
        }).SelectMany(node => node.RequiredClasses));
        if (selected.Any(IsTaggerModel)) classes = classes.Append("WDTimmTagger");
        return classes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    public static void ValidateObjectInfo(JsonElement objectInfo, IEnumerable<string>? selectedModelIds = null)
    {
        var issues = ObjectInfoIssues(objectInfo, selectedModelIds);
        if (issues.Length > 0) throw new InvalidOperationException(string.Join("；", issues));
    }

    public static string[] ObjectInfoIssues(JsonElement info, IEnumerable<string>? selectedModelIds = null)
    {
        var selected = (selectedModelIds ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var issues = new List<string>();
        void Inputs(string node, params string[] fields)
        {
            foreach (var field in fields)
                if (!TryInput(info, node, field, out _)) issues.Add($"{node} 缺少输入 {field}，请在启动器修复节点。");
        }
        void Choice(string node, string field, string choice)
        {
            if (!TryInput(info, node, field, out var input) || !ContainsChoice(input, choice))
                issues.Add($"{node}.{field} 不支持 {choice}，请在启动器修复对应扩展。");
        }
        Inputs("SesquiLatentUpscale", "latent", "scale", "half_precision");
        Choice("SesquiLatentUpscale", "model_format", "Wan 2.1");
        Inputs("SeedVR2Conditioning", "model", "vae_conditioning");
        Inputs("SeedVR2Preprocess", "resized_images");
        Inputs("SeedVR2PostProcessing", "images", "original_resized_images");
        Choice("SeedVR2PostProcessing", "color_correction_method", "lab");
        Choice("CLIPLoader", "type", "stable_diffusion");
        if (selected.Contains("krea2-turbo") || selected.Contains("qwen3vl-krea2")) Choice("CLIPLoader", "type", "krea2");
        Inputs("PromptExpand", "template_only");
        Inputs("ImageCaptionNode", "image", "vlm_service", "custom_rule", "custom_rule_content");
        if (selected.Any(IsFaceModel))
        {
            Inputs("ForbiddenVisionFaceProcessorIntegrated", "model", "vae", "positive", "negative", "enable_segmentation");
            Choice("KSampler", "sampler_name", "res_2m_ode");
            Choice("KSampler", "scheduler", "beta57");
            Choice("ForbiddenVisionFaceProcessorIntegrated", "sampler", "res_2m_ode");
            Choice("ForbiddenVisionFaceProcessorIntegrated", "scheduler", "beta57");
        }
        if (selected.Any(IsTaggerModel)) Inputs("WDTimmTagger", "image", "model_name", "dtype", "general_threshold", "character_threshold", "exclude_tags", "add_rating", "batch_size");
        if (selected.Contains(NvidiaCapabilityId))
        {
            Inputs("RTXVideoSuperResolution", "images", "resize_type", "quality");
            Inputs("DLSS5NeuralRender", "images", "style", "intensity", "local_tone", "local_struct", "output_mix");
        }
        return issues.ToArray();
    }

    private static bool ContainsChoice(JsonElement input, string choice)
    {
        if (input.ValueKind != JsonValueKind.Array || input.GetArrayLength() == 0) return false;
        var options = input[0];
        // V1 exposes [[choices], metadata]; V3 Combo inputs expose
        // ["COMBO", {"options": choices, ...}]. Match the workbench's reader.
        if (options.ValueKind != JsonValueKind.Array
            && !(input.GetArrayLength() > 1 && input[1].ValueKind == JsonValueKind.Object
                && input[1].TryGetProperty("options", out options))) return false;
        return options.ValueKind == JsonValueKind.Array
            && options.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && value.GetString() == choice);
    }

    private static bool TryInput(JsonElement info, string node, string name, out JsonElement value)
    {
        value = default;
        if (info.ValueKind != JsonValueKind.Object || !info.TryGetProperty(node, out var definition)
            || !definition.TryGetProperty("input", out var inputs)) return false;
        foreach (var group in new[] { "required", "optional" })
            if (inputs.TryGetProperty(group, out var fields) && fields.TryGetProperty(name, out value)) return true;
        return false;
    }
}
