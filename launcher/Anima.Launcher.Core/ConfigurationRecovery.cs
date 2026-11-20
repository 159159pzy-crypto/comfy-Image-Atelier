using System.Text.Json;
using System.Text.Json.Nodes;

namespace Anima.Launcher.Core;

public static class ConfigurationRecovery
{
    // Preserve independent valid settings while validating related fields as a unit.
    public static LauncherConfig Recover(string json, out string[] restoredDefaults)
    {
        var source = JsonNode.Parse(json, new JsonNodeOptions { PropertyNameCaseInsensitive = true }) as JsonObject
            ?? throw new InvalidDataException("配置文件须为 JSON 对象。");
        if (source.ContainsKey("schemaVersion") && source["schemaVersion"]?.GetValue<int>() != 1)
            throw new InvalidDataException("此配置来自其他版本，请使用匹配的启动器。");
        var output = new JsonObject();
        var rejected = new List<string>();
        var groups = source.GroupBy(pair => pair.Key.ToLowerInvariant() switch
        {
            "networkmode" or "proxyaddress" => "network",
            "comfyport" or "webport" => "ports",
            _ => pair.Key.ToLowerInvariant(),
        });
        foreach (var group in groups)
        {
            try
            {
                foreach (var (key, value) in group)
                {
                    if (value is null && !key.Equals("externalEnvironmentMode", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("配置值为空。");
                    output[key] = value?.DeepClone();
                }
                output.Deserialize<LauncherConfig>(JsonFile.Options)!.Validate(validateArguments: false);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException or UriFormatException)
            {
                foreach (var (key, _) in group) { output.Remove(key); rejected.Add(key); }
                if (group.Key == "externalenvironmentmode") output["externalEnvironmentMode"] = "readOnly";
            }
        }
        restoredDefaults = rejected.ToArray();
        var recovered = output.Deserialize<LauncherConfig>(JsonFile.Options)!;
        recovered.Validate(validateArguments: false);
        return recovered;
    }
}
