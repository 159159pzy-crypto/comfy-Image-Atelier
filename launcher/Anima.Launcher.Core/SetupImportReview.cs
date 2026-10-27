using System.Text.Json;

namespace Anima.Launcher.Core;

/// <summary>Read-only import readiness, usable before third-party packages are installed.</summary>
public sealed record SetupImportReview(bool NeedsRepair, string[] MissingCoreModules)
{
    public static async Task<SetupImportReview> InspectAsync(LauncherConfig config, RuntimeManifest manifest, CancellationToken ct)
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
        var needsRepair = missing.Length > 0 || nodes.Any(node => node.State == "缺失");
        if (needsRepair)
        {
            // Check the same boundary as Installer before the user starts an install.
            // A complete external interpreter can still be connected without mutation.
            try { _ = ComfyUpdateCoordinator.IndependentPythonDirectory(config); }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException("此导入环境需要补齐节点或依赖。请选择 ComfyUI 目录内的独立 .venv、venv 或独立便携 Python，或先在原环境中手动补齐后重新检查。" + ex.Message, ex);
            }
        }
        return new(needsRepair, missing);
    }
}
