using Anima.Launcher.Core;

namespace Anima.Launcher;

internal sealed partial class LauncherState
{
    private ComfyRelease? checkedComfyRelease;
    private ComfyDependencySnapshot? checkedComfyDependencies;
    private string checkedComfyRoot = "";
    private string loadedComfyPlanIdentity = "";
    public string ComfyUpdateStatus { get; private set; } = "手动检查 ComfyUI 最新稳定版。更新前请停止 ComfyUI 和工作台；更新会备份源码与完整 Python 环境。";
    public event Action? ComfyUpdateChanged;
    public string ComfyOptionalDependencyStatus
    {
        get
        {
            try
            {
                var path = Path.Combine(InstallationPlanner.ResolveRuntimeRoot(Config, Program.StateDir), "optional-dependencies.json");
                if (!File.Exists(path)) return "可选加速组件尚未检查；依赖修复会检查 GPU 与 Triton，并显示具体结果。";
                var item = JsonFile.Read<OptionalDependencyStatus>(path);
                return item.Name + "：" + (item.Available ? "可用" : "不可用") + " · " + item.Detail;
            }
            catch (Exception ex) { return "无法读取可选依赖检查结果：" + ex.Message; }
        }
    }
    public bool ComfyHasUpdatePlan => checkedComfyRelease is not null && SamePath(checkedComfyRoot, Config.ComfyRoot);
    public bool ComfyUpdateReady => ComfyHasUpdatePlan && checkedComfyDependencies?.Resolved == true;
    public void RestoreComfyUpdatePlan()
    {
        var identity = Config.ComfyRoot + "\n" + Config.Python;
        if (identity == loadedComfyPlanIdentity) return;
        loadedComfyPlanIdentity = identity;
        checkedComfyRelease = null; checkedComfyDependencies = null; checkedComfyRoot = "";
        try
        {
            var plan = new ComfyUpdatePlans(Program.StateDir).Read(Config);
            if (plan is null) { ComfyUpdateStatus = "手动检查 ComfyUI 最新稳定版；启动服务不会自动更新。"; return; }
            checkedComfyRelease = plan.Release; checkedComfyDependencies = plan.Dependencies; checkedComfyRoot = plan.Root;
            ComfyUpdateStatus = $"已恢复固定计划 {plan.Release.Tag} · {plan.Release.Commit[..12]}。"
                + (plan.Dependencies.Resolved ? "依赖版本已保存，可停止服务后备份并更新。重新检查才会选择新的稳定版本。" : "依赖检查尚未完成，请停止服务后点击「固定更新依赖」继续原版本计划。");
        }
        catch (Exception ex) { ComfyUpdateStatus = "无法读取保存的更新计划：" + ex.Message; }
    }
    public bool ComfyCanRollback
    {
        get
        {
            try { return new ComfyUpdateCoordinator(Program.StateDir, "git.exe").Previous(Config)?.Status is "complete" or "updating" or "restoring" or "rollback-failed"; }
            catch { return false; }
        }
    }
    private string ComfyGit()
    {
        var found = Commands.Find("git.exe");
        if (found is not null) return found;
        var artifact = Program.Runtime.Tools.Single(tool => tool.Id == "git");
        var path = Safety.Under(ToolInstallation.DirectoryFor(InstallationPlanner.ResolveRuntimeRoot(Config, Program.StateDir), artifact), artifact.Executable);
        return File.Exists(path) ? path : throw new FileNotFoundException("未找到 Git，请先完成环境安装或安装 Git 后重试。");
    }

    public Task CheckComfyUpdateAsync()
    {
        ComfyUpdatePlan? frozen = null;
        return Run(async ct =>
        {
        RequireRoot();
        var git = ComfyGit();
        if (frozen is null)
        {
            ComfyUpdateStatus = "正在查询官方稳定版与固定版本依赖…";
            ComfyUpdateChanged?.Invoke();
            var releases = new ComfyReleases(resolveTag: (tag, token) => ComfyReleases.ResolveTagWithGit(git, tag, Program.StateDir, Config, token), config: Config);
            var release = await releases.LatestAsync(ct);
            var current = (await Commands.Run(git, ["rev-parse", "HEAD"], Config.ComfyRoot, null, ct, timeout: TimeSpan.FromSeconds(20))).Trim();
            if (current == release.Commit)
            {
                new ComfyUpdatePlans(Program.StateDir).Clear(Config);
                checkedComfyRelease = null; checkedComfyDependencies = null;
                ComfyUpdateStatus = "当前 ComfyUI 已是最新稳定版本 " + release.Tag + "。";
                ComfyUpdateChanged?.Invoke(); return;
            }
            var dependencies = await releases.DependenciesAsync(release, Program.Runtime, ct);
            frozen = new ComfyUpdatePlans(Program.StateDir).Save(Config, release, dependencies);
        }
        checkedComfyRelease = frozen.Release; checkedComfyDependencies = frozen.Dependencies; checkedComfyRoot = frozen.Root;
        loadedComfyPlanIdentity = Config.ComfyRoot + "\n" + Config.Python;
        try { await new ComfyUpdateCoordinator(Program.StateDir, git).EnsureStoppedAsync(Config, ct); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            ComfyUpdateStatus = $"已固定 {frozen.Release.Tag} · {frozen.Release.Commit[..12]}，依赖尚未检查。{ex.Message} 停止后点击「固定更新依赖」继续。";
            ComfyUpdateChanged?.Invoke(); return;
        }
        await PrepareComfyUpdateCoreAsync(ct);
        });
    }

    public Task PrepareComfyUpdateAsync() => Run(PrepareComfyUpdateCoreAsync);
    private async Task PrepareComfyUpdateCoreAsync(CancellationToken ct)
    {
        RestoreComfyUpdatePlan();
        if (!ComfyHasUpdatePlan || checkedComfyRelease is null || checkedComfyDependencies is null) throw new InvalidOperationException("请先检查稳定版本。");
        var git = ComfyGit();
        await new ComfyUpdateCoordinator(Program.StateDir, git).ValidateEnvironmentAsync(Config, ct);
        ComfyUpdateStatus = "正在固定版本 " + checkedComfyRelease.Tag + " 的完整依赖；检查完成前不会更新环境。";
        ComfyUpdateChanged?.Invoke();
        checkedComfyDependencies = await ComfyDependencyResolver.ResolveAsync(Config, checkedComfyDependencies,
            Path.Combine(InstallationPlanner.ResolveRuntimeRoot(Config, Program.StateDir), "cache"), ct, AppendLog,
            nodeRequirements: ComfyDependencyResolver.ExistingNodeRequirements(Config),
            environment: new Dictionary<string, string> { ["PATH"] = Path.GetDirectoryName(git) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH") },
            sam2Commit: Program.Runtime.Sam2Commit);
        new ComfyUpdatePlans(Program.StateDir).Save(Config, checkedComfyRelease, checkedComfyDependencies);
        ComfyUpdateStatus = $"已固定 {checkedComfyRelease.Tag} · {checkedComfyRelease.Commit[..12]} 与完整依赖快照，重启后仍可继续。更新会备份 Git、完整 Python 与配置，并保留模型和节点。";
        ComfyUpdateChanged?.Invoke();
    }

    public Task ApplyComfyUpdateAsync() => Run(async ct =>
    {
        if (!ComfyUpdateReady || checkedComfyRelease is null || checkedComfyDependencies is null) throw new InvalidOperationException("请先检查当前环境的稳定版本。");
        if (Services.IsOwnedRunning("ComfyUI"))
            throw new InvalidOperationException("启动器仍管理着运行中的 ComfyUI，请先停止后更新，避免影响其他环境的服务。");
        var config = Config.Clone();
        var release = checkedComfyRelease;
        var git = ComfyGit();
        var coordinator = new ComfyUpdateCoordinator(Program.StateDir, git, AppendLog);
        await coordinator.ValidateEnvironmentAsync(config, ct);
        if (checkedComfyDependencies.NodeRequirementsIdentity != ComfyDependencyResolver.NodeRequirementsIdentity(ComfyDependencyResolver.ExistingNodeRequirements(config), Program.Runtime.Sam2Commit))
            throw new InvalidOperationException("检查后节点依赖文件已变化，请重新检查稳定版本；没有修改环境。");
        SetStatus("正在解析稳定版依赖；通过后备份完整环境并更新…");
        var dependencies = await ComfyDependencyResolver.ResolveAsync(config, checkedComfyDependencies,
            Path.Combine(InstallationPlanner.ResolveRuntimeRoot(config, Program.StateDir), "cache"), ct, AppendLog,
            nodeRequirements: ComfyDependencyResolver.ExistingNodeRequirements(config),
            environment: new Dictionary<string, string> { ["PATH"] = Path.GetDirectoryName(git) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH") },
            sam2Commit: Program.Runtime.Sam2Commit);
        var runtime = Program.Runtime with { Comfy = Program.Runtime.Comfy with { Commit = release.Commit }, PythonConstraints = dependencies.Constraints };
        var installer = new Installer(Program.StateDir, runtime, Downloads, AppendLog, DownloadProgress());
        try
        {
            await coordinator.UpdateAsync(config, release, runtime, dependencies,
                (target, snapshot, token) => installer.RepairDependencies(target, snapshot, token),
                async (target, token) =>
                {
                    try { await Services.StartComfy(target, runtime, token); }
                    finally { await Services.StopOwnedAsync("ComfyUI", CancellationToken.None); }
                }, ct);
            checkedComfyRelease = null;
            checkedComfyDependencies = null;
            new ComfyUpdatePlans(Program.StateDir).Clear(config);
            ComfyUpdateStatus = "已更新至 " + release.Tag + "，运行检查通过。可重新启动工作台，或使用完整备份回退。";
            SetStatus(ComfyUpdateStatus);
        }
        finally { ComfyUpdateChanged?.Invoke(); }
    });

    public Task RollbackComfyUpdateAsync() => Run(async ct =>
    {
        var coordinator = new ComfyUpdateCoordinator(Program.StateDir, ComfyGit(), AppendLog);
        await coordinator.RollbackAsync(Config, ct);
        new ComfyUpdatePlans(Program.StateDir).Clear(Config);
        Config = LoadConfig();
        Reloaded();
        ComfyUpdateStatus = "已恢复更新前的 ComfyUI 源码与完整 Python 环境，可以重新启动工作台。";
        checkedComfyRelease = null;
        checkedComfyDependencies = null;
        ComfyUpdateChanged?.Invoke();
        SetStatus(ComfyUpdateStatus);
    });

    public Task RepairComfyDependenciesAsync() => Run(async ct =>
    {
        RequireRoot();
        var installer = new Installer(Program.StateDir, Program.Runtime, Downloads, AppendLog, DownloadProgress());
        await installer.RepairDependencies(Config, null, ct);
        if (!Config.ComfyArguments.Contains("--enable-manager")) Config.ComfyArguments.Add("--enable-manager");
        Save();
        Reloaded();
        ComfyUpdateStatus = "依赖修复完成，Manager 将在下次启动时启用。可选加速组件的检查结果见日志。";
        ComfyUpdateChanged?.Invoke();
        SetStatus(ComfyUpdateStatus);
    });
}
