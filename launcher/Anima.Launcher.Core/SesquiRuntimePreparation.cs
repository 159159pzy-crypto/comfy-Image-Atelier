namespace Anima.Launcher.Core;

/// <summary>The Wan 2.1 adapter is bundled by SesquiLSR, but node registration does not load it.</summary>
public static class SesquiRuntimePreparation
{
    public static readonly Artifact Weight = new("sesqui-wan21",
        "https://raw.githubusercontent.com/LoganBooker/SesquiLSR/821e6db7412089f2a5c0a106c42d3c1a396b9515/models/upscaler_Wan21.safetensors",
        12373048, "d1b4c2c1d80b2af81e6bbfd38501ffb5adffb9f7c0ed2599555b837d0d2d015a", "")
    { FileName = "upscaler_Wan21.safetensors" };

    public static string? ResolveDirectory(string comfyRoot)
    {
        var custom = Path.Combine(comfyRoot, "custom_nodes");
        if (!Directory.Exists(custom)) return null;
        var matches = Directory.EnumerateDirectories(custom).Where(path => !Path.GetFileName(path).StartsWith('.')
            && !new[] { ".disabled", ".anima-installing", ".extracting" }.Any(suffix => path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            && File.Exists(Path.Combine(path, "nodes.py"))
            && File.ReadAllText(Path.Combine(path, "nodes.py")).Contains("class SesquiLatentUpscale")).ToArray();
        if (matches.Length > 1) throw new InvalidDataException("发现多个 SesquiLSR 节点，请只保留一个启用的安装。");
        return matches.SingleOrDefault();
    }

    public static async Task<string[]> FilesIssuesAsync(LauncherConfig config, RuntimeManifest runtime, CancellationToken ct)
    {
        if (!runtime.Nodes.Any(node => node.Id == "SesquiLSR")) return [];
        try
        {
            var node = ResolveDirectory(config.ComfyRoot);
            if (node is null) return ["缺少 SesquiLSR 节点及其 Wan 2.1 放大权重。"];
            var path = Path.Combine(node, "models", Weight.FileName);
            return await Downloads.Verify(path, Weight.Size, Weight.Sha256, ct) ? []
                : ["SesquiLSR 的 upscaler_Wan21.safetensors 缺失或校验失败，请在启动器修复安装。"];
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return ["无法验证 SesquiLSR 放大权重：" + error.Message]; }
    }

    public static async Task PrepareAsync(LauncherConfig config, RuntimeManifest runtime, string runtimeRoot,
        Downloads downloads, Action<string> log, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (!runtime.Nodes.Any(node => node.Id == "SesquiLSR")) return;
        var node = ResolveDirectory(config.ComfyRoot) ?? throw new InvalidOperationException("缺少 SesquiLSR 节点，无法准备放大权重。");
        var models = Path.Combine(node, "models");
        var destination = Path.Combine(models, Weight.FileName);
        if (await Downloads.Verify(destination, Weight.Size, Weight.Sha256, ct)) return;
        if (Directory.Exists(models) && (File.GetAttributes(models) & FileAttributes.ReparsePoint) != 0
            || File.Exists(destination) && (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("SesquiLSR 权重路径是链接，已保留原文件，请使用节点内的独立模型目录。");
        log("正在补齐 SesquiLSR 的 Wan 2.1 放大权重…");
        var cached = await ArtifactCache.FetchAsync(runtimeRoot, Weight, config, downloads, progress, ct);
        Directory.CreateDirectory(models);
        var staging = destination + ".anima-" + Guid.NewGuid().ToString("N");
        File.Copy(cached, staging);
        if (!await Downloads.Verify(staging, Weight.Size, Weight.Sha256, ct)) throw new InvalidDataException("SesquiLSR 权重发布前校验失败。");
        ct.ThrowIfCancellationRequested();
        if (File.Exists(destination)) File.Move(destination, destination + ".invalid-" + Guid.NewGuid().ToString("N"));
        File.Move(staging, destination);
        log("SesquiLSR 放大权重已通过 SHA-256 校验。");
    }
}
