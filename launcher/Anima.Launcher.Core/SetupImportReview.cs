using System.Text.Json;

namespace Anima.Launcher.Core;

/// <summary>Read-only import readiness, usable before third-party packages are installed.</summary>
public sealed record SetupImportReview(bool NeedsRepair, string[] MissingCoreModules)
{
    public string[] PreparationIssues { get; init; } = [];

    public static async Task<SetupImportReview> InspectAsync(LauncherConfig config, RuntimeManifest manifest, CancellationToken ct, string? stateDir = null)
    {
        // Match the core imports verified after installation, without loading torch,
        // initializing a GPU, importing custom nodes, or requiring packaging/PyYAML.
        const string script = """
import importlib.util, json
print(json.dumps([name for name in ('aiohttp', 'yaml', 'torch') if importlib.util.find_spec(name) is None]))
""";
        ct.ThrowIfCancellationRequested();
        var output = await Commands.Run(config.Python, ["-B", "-c", script], config.ComfyRoot, null, ct,
            timeout: TimeSpan.FromSeconds(30));
        var missing = JsonSerializer.Deserialize<string[]>(output)
            ?? throw new InvalidDataException("无法读取导入环境的核心依赖状态。");
        var nodes = await Inventory.Nodes(config.ComfyRoot, manifest, null, ct);
        ct.ThrowIfCancellationRequested();
        var preparation = new List<string>();
        var runtimeRoot = InstallationPlanner.ResolveRuntimeRoot(config,
            stateDir ?? Path.Combine(Path.GetTempPath(), "AnimaLauncher", "import-review"));
        preparation.AddRange(await SesquiRuntimePreparation.FilesIssuesAsync(config, manifest, ct));
        if (manifest.Nodes.Any(node => node.Id == ForbiddenVisionCompatibility.NodeId))
        {
            try
            {
                var face = ForbiddenVisionCompatibility.ResolveDirectory(config.ComfyRoot);
                if (face is null) preparation.Add("Forbidden Vision 下载管理尚未准备。");
                else
                {
                    await ForbiddenVisionCompatibility.CheckOrApply(config, runtimeRoot, face, false, ct);
                    if (!File.Exists(Path.Combine(config.ComfyRoot, "custom_nodes", "skip_download_model")))
                        preparation.Add("可选模型的受管理下载标记缺失。");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException)
            { preparation.Add("Forbidden Vision 下载管理需要准备：" + error.Message); }
        }
        if (!missing.Contains("torch") && manifest.Nodes.Any(node => node.Id is "nvidia-rtx-vsr-pro" or "DLSS5-Comfyui"))
        {
            try
            {
                // A module's presence cannot prove its CUDA/native libraries work.
                // Probe only when the declared installation actually uses this SDK.
                var probe = await NvidiaRuntimePreparation.ProbeAsync(config, runtimeRoot, ct);
                if (probe.Supported)
                {
                    var status = await NvidiaRuntimePreparation.ValidateStatusAsync(config, runtimeRoot, ct);
                    if (!NvidiaRuntimePreparation.IsReadyFor(probe, status))
                        preparation.Add("当前显卡的 NVIDIA VFX 与 DLSS 运行库需要准备或重新验证。");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException)
            { preparation.Add("NVIDIA 运行库检查未通过：" + error.Message); }
        }
        var needsRepair = missing.Length > 0 || nodes.Any(node => node.State == "缺失") || preparation.Count > 0;
        if (needsRepair)
        {
            // Check the same boundary as Installer before the user starts an install.
            // A complete external interpreter can still be connected without mutation.
            try { _ = ComfyUpdateCoordinator.IndependentPythonDirectory(config); }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException("此导入环境需要补齐节点、依赖或安装准备。请选择 ComfyUI 目录内的独立 .venv、venv 或独立便携 Python，或先在原环境中手动补齐后重新检查。"
                    + string.Join("；", preparation) + ex.Message, ex);
            }
        }
        return new(needsRepair, missing) { PreparationIssues = preparation.ToArray() };
    }
}
