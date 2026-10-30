using Anima.Launcher.Core;

namespace Anima.Launcher;

internal sealed record NodeUpdateRow(string Name, string Status, string Version, string Detail);

internal sealed partial class LauncherState
{
    private NodeUpdatePlan? checkedNodePlan;
    private string loadedNodePlanIdentity = "";
    public string NodeUpdateStatus { get; private set; } = "手动检查插件与自定义节点的最新版本。启动服务不会自动更新节点。";
    public IReadOnlyList<NodeUpdateRow> NodeUpdateRows { get; private set; } = [];
    public event Action? NodeUpdateChanged;
    private bool NodePlanEnvironmentMatches => checkedNodePlan is { } plan
        && SamePath(plan.Root, Config.ComfyRoot) && SamePath(plan.Python, Config.Python)
        && RuntimeDependencies.NodeCommit(Config.ComfyRoot) == plan.CoreCommit;
    public bool NodesHaveUpdatePlan => NodePlanEnvironmentMatches && checkedNodePlan!.Entries.Any(entry => entry.CanUpdate);
    public bool NodesUpdateReady => NodesHaveUpdatePlan && checkedNodePlan!.CanApply;
    public bool NodesCanRollback
    {
        get
        {
            try { return new NodeUpdateCoordinator(Program.StateDir, "git.exe").Previous(Config)?.Status is "complete" or "updating" or "restoring" or "rollback-failed"; }
            catch { return false; }
        }
    }

    public void RestoreNodeUpdatePlan()
    {
        var identity = Config.ComfyRoot + "\n" + Config.Python + "\n" + RuntimeDependencies.NodeCommit(Config.ComfyRoot);
        if (identity == loadedNodePlanIdentity) return;
        loadedNodePlanIdentity = identity;
        checkedNodePlan = null;
        NodeUpdateRows = [];
        try
        {
            var plan = new NodeUpdateCoordinator(Program.StateDir, "git.exe").LoadPlan(Config);
            if (plan is null) { NodeUpdateStatus = "手动检查插件与自定义节点的最新版本；启动服务不会自动更新节点。"; return; }
            SetNodePlan(plan);
            NodeUpdateStatus = NodePlanEnvironmentMatches
                ? "已恢复上次固定的节点更新计划。" + (plan.CanApply ? "依赖已检查，可停止服务后备份并更新。" : "停止服务后继续固定节点依赖；重新检查才会选择新的提交。")
                : "ComfyUI 版本或 Python 环境已变化，请重新检查节点更新。原计划保留供核对。";
        }
        catch (Exception ex) { NodeUpdateStatus = "无法读取节点更新计划：" + ex.Message; }
    }

    private void SetNodePlan(NodeUpdatePlan plan)
    {
        checkedNodePlan = plan;
        loadedNodePlanIdentity = Config.ComfyRoot + "\n" + Config.Python + "\n" + RuntimeDependencies.NodeCommit(Config.ComfyRoot);
        static string Short(string sha) => sha.Length > 12 ? sha[..12] : sha;
        NodeUpdateRows = plan.Entries.Select(entry => new NodeUpdateRow(entry.Name,
            entry.CanUpdate ? "可更新" : entry.TargetSha.Length > 0 && entry.TargetSha == entry.BeforeSha ? "已是最新" : "已跳过",
            entry.TargetSha.Length > 0 ? Short(entry.BeforeSha) + " → " + Short(entry.TargetSha) + (entry.Branch.Length > 0 ? " · " + entry.Branch : "")
                : entry.BeforeSha.Length > 0 ? "当前提交 " + Short(entry.BeforeSha) : "无法确认当前版本",
            entry.Reason)).ToArray();
    }

    public Task CheckNodeUpdatesAsync()
    {
        NodeUpdatePlan? frozen = null;
        return Run(async ct =>
        {
            RequireRoot();
            var git = ComfyGit();
            var coordinator = new NodeUpdateCoordinator(Program.StateDir, git, AppendLog, dependencyProgress: DependencyProgressObserver());
            if (frozen is null)
            {
                NodeUpdateStatus = "正在检查已启用的插件与自定义节点，并固定目标提交…";
                NodeUpdateChanged?.Invoke();
                frozen = await coordinator.CheckAsync(Config, Program.Runtime, ct);
            }
            SetNodePlan(frozen);
            NodeUpdateChanged?.Invoke();
            var count = frozen.Entries.Count(entry => entry.CanUpdate);
            if (count == 0)
            {
                NodeUpdateStatus = "检查完成，没有可自动更新的节点。已是最新或跳过原因见下方。";
                NodeUpdateChanged?.Invoke(); return;
            }
            NodeUpdateStatus = frozen.CanApply
                ? $"已固定 {count} 个节点及完整依赖，可备份并更新。重新检查才会选择新的提交。"
                : $"已固定 {count} 个节点的目标提交。{frozen.Note} 点击「固定节点依赖」继续原计划。";
            NodeUpdateChanged?.Invoke();
        });
    }

    private async Task PrepareNodeUpdatesCoreAsync(NodeUpdateCoordinator coordinator, CancellationToken ct)
    {
        if (!NodesHaveUpdatePlan || checkedNodePlan is null) throw new InvalidOperationException("请先检查当前环境的节点更新。");
        NodeUpdateStatus = "正在检查目标节点的完整依赖；此时不会修改已安装节点。";
        NodeUpdateChanged?.Invoke();
        SetNodePlan(await coordinator.PrepareAsync(Config, checkedNodePlan, Program.Runtime, ct));
        NodeUpdateStatus = $"已固定 {checkedNodePlan!.Entries.Count(entry => entry.CanUpdate)} 个节点及兼容依赖。更新前会完整备份节点、Python 与配置；模型和 ComfyUI 版本保留。";
        NodeUpdateChanged?.Invoke();
    }

    public Task ApplyNodeUpdatesAsync() => Run(async ct =>
    {
        RestoreNodeUpdatePlan();
        var coordinator = new NodeUpdateCoordinator(Program.StateDir, ComfyGit(), AppendLog, dependencyProgress: DependencyProgressObserver());
        if (!NodesUpdateReady)
        {
            await PrepareNodeUpdatesCoreAsync(coordinator, ct);
            return;
        }
        var config = Config.Clone();
        var plan = checkedNodePlan!;
        var runtime = Program.Runtime with { Comfy = Program.Runtime.Comfy with { Commit = plan.CoreCommit }, PythonConstraints = plan.Dependencies!.Constraints };
        var installer = CreateInstaller(runtime);
        NodeUpdateStatus = "正在备份节点、完整 Python 与配置，并更新已固定的节点版本…";
        NodeUpdateChanged?.Invoke();
        try
        {
            await coordinator.ApplyAsync(config, plan,
                (target, dependencies, token) => installer.RepairDependencies(target, dependencies, token),
                async (target, token) =>
                {
                    var path = Path.Combine(Program.StateDir, "logs", "ComfyUI.log");
                    var offset = File.Exists(path) ? new FileInfo(path).Length : 0;
                    if (offset > 10 * 1024 * 1024) offset = 0; // ServiceManager rotates the prior log before starting.
                    try
                    {
                        await Services.StartComfy(target, runtime, token);
                        if (File.Exists(path))
                        {
                            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                            input.Position = input.Length >= offset ? offset : 0;
                            using var reader = new StreamReader(input);
                            var currentLog = (await reader.ReadToEndAsync(token)).Replace('\\', '/');
                            var failed = plan.Entries.Where(entry => entry.CanUpdate && currentLog.Split('\n').Any(line =>
                                line.Contains("IMPORT FAILED", StringComparison.OrdinalIgnoreCase)
                                && line.Contains(entry.Directory.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))).Select(entry => entry.Name).ToArray();
                            if (failed.Length > 0) throw new InvalidOperationException("更新后节点加载失败：" + string.Join("、", failed));
                        }
                    }
                    finally { await Services.StopOwnedAsync("ComfyUI", CancellationToken.None); }
                }, ct);
            checkedNodePlan = null;
            NodeUpdateRows = NodeUpdateRows.Select(row => row.Status == "可更新" ? row with { Status = "已更新" } : row).ToArray();
            NodeUpdateStatus = "插件与自定义节点已更新，启动检查通过。需要时可回退节点、完整 Python 与配置。";
            SetStatus(NodeUpdateStatus);
        }
        catch (Exception ex)
        {
            NodeUpdateStatus = "节点更新未完成：" + ex.Message;
            throw;
        }
        finally { NodeUpdateChanged?.Invoke(); }
    });

    public Task RollbackNodeUpdatesAsync() => Run(async ct =>
    {
        await new NodeUpdateCoordinator(Program.StateDir, ComfyGit(), AppendLog).RollbackAsync(Config, ct);
        Config = LoadConfig();
        checkedNodePlan = null;
        NodeUpdateRows = [];
        loadedNodePlanIdentity = Config.ComfyRoot + "\n" + Config.Python + "\n" + RuntimeDependencies.NodeCommit(Config.ComfyRoot);
        Reloaded();
        NodeUpdateStatus = "已恢复上次节点更新前的源码、完整 Python 与配置。";
        NodeUpdateChanged?.Invoke();
        SetStatus(NodeUpdateStatus);
    });
}
