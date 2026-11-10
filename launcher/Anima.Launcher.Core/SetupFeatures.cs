namespace Anima.Launcher.Core;

/// <summary>One selection drives new-plan nodes, artifacts, review and runtime checks.</summary>
public static class SetupFeatures
{
    public const int PolicyVersion = 1;
    public const string Prefix = "capability:";
    public static bool IsCapability(string id) => id.StartsWith(Prefix, StringComparison.Ordinal);
    public static string[] Resolve(bool fresh, IEnumerable<string>? selected, string profile = "anima")
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (fresh && profile == "anima") result.Add("sesqui"); // Anima defaults to dual sampling; Krea defaults to normal.
        if (profile == "krea2") result.Add("krea2");
        foreach (var id in selected ?? [])
        {
            if (IsCapability(id))
            {
                var feature = id[Prefix.Length..];
                if (feature is not ("sesqui" or "nvidia" or "dlss" or "prompt-assistant"))
                    throw new InvalidDataException("未知功能选项：" + id);
                result.Add(feature);
            }
            else if (id.StartsWith("seedvr2-", StringComparison.Ordinal)) result.Add("seedvr2");
            else if (RuntimeCapabilities.IsFaceModel(id)) result.Add("face-refine");
            else if (RuntimeCapabilities.IsTaggerModel(id)) result.Add("tagger");
            else if (id is "krea2-turbo" or "qwen3vl-krea2") result.Add("krea2");
        }
        return result.Order(StringComparer.Ordinal).ToArray();
    }

    public static string? NodeFeature(string id) => id switch
    {
        "SesquiLSR" => "sesqui",
        "forbidden-vision" or "RES4LYF" => "face-refine",
        "nvidia-rtx-vsr-pro" => "nvidia",
        "DLSS5-Comfyui" => "dlss",
        "ComfyUI-WD-Timm-Tagger" => "tagger",
        "prompt-assistant" => "prompt-assistant",
        "krea-seed-variance-enhancer" => "krea2",
        "comfyui-impact-pack" or "comfyui-impact-subpack" or "comfyui-easy-use" => "legacy-detailers",
        _ => null,
    };

    public static RuntimeManifest SelectRuntime(RuntimeManifest runtime, IEnumerable<string> features)
    {
        var selected = features.ToHashSet(StringComparer.Ordinal);
        return runtime with
        {
            Nodes = runtime.Nodes.Where(node => NodeFeature(node.Id) is not { } feature || selected.Contains(feature)).ToArray(),
            Comfy = runtime.Comfy with { RequiredClasses = runtime.Comfy.RequiredClasses.Where(name => !name.StartsWith("SeedVR2", StringComparison.Ordinal) || selected.Contains("seedvr2")).ToArray() }
        };
    }
}
