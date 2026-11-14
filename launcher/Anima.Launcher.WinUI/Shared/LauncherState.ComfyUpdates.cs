using Anima.Launcher.Core;

namespace Anima.Launcher;

internal sealed partial class LauncherState
{
    private ComfyRelease? checkedComfyRelease;
    private ComfyDependencySnapshot? checkedComfyDependencies;
    private string checkedComfyRoot = "";
    private string loadedComfyPlanIdentity = "";
    private int comfyVersionRequest;
    public string ComfyCurrentVersion { get; private set; } = "当前版本：尚未读取";
    public string ComfyTargetVersion => checkedComfyRelease is { } release && ComfyHasUpdatePlan
        ? $"目标稳定版：{release.Tag} · {release.Commit[..12]}"
            + (release.PublishedAt is { } date ? $"\n发布时间：{date.ToLocalTime():yyyy-MM-dd HH:mm}" : "")
        : "目标稳定版：点击检查更新获取";
    public string ComfyReleaseUrl => checkedComfyRelease is { } release && ComfyHasUpdatePlan
        ? "https://github.com/Comfy-Org/ComfyUI/releases/tag/" + Uri.EscapeDataString(release.Tag) : "";
    public string ComfyUpdateStatus { get; private set; } = ComfyUpdatePresentation.From(null).Message;
    public event Action? ComfyUpdateChanged;
    private ComfyUpdateBackup? ComfyRecovery(LauncherConfig? config = null)
    {
        var target = config ?? Config;
        if (string.IsNullOrWhiteSpace(target.ComfyRoot)) return null;
        var record = new ComfyUpdateCoordinator(Program.StateDir, "git.exe").Previous(target);
        if (record is not null && (!SamePath(record.Root, target.ComfyRoot) || !SamePath(record.Python, target.Python)))
            throw new InvalidDataException("此目录存在其他 Python 环境的更新记录，请先恢复原解释器设置。");
        return record;
    }
    public bool ComfyRecoveryPending
    {
        get { try { return ComfyUpdatePresentation.From(ComfyRecovery()).RequiresRecovery; } catch { return true; } }
    }
    public bool ComfyNeedsDependencyRepair
    {
        get { try { return ComfyUpdatePresentation.From(ComfyRecovery()).NeedsDependencyRepair; } catch { return false; } }
    }
    public bool ComfyCanRollback
    {
        get { try { return ComfyUpdatePresentation.From(ComfyRecovery()).CanRestore; } catch { return false; } }
    }
    public bool ComfyCoreVerified
    {
        get { try { return ComfyUpdatePresentation.From(ComfyRecovery()).CoreVerified; } catch { return false; } }
    }
    public string ComfyOptionalDependencyStatus
    {
        get
        {
            try
            {
                var path = Path.Combine(InstallationPlanner.ResolveRuntimeRoot(Config, Program.StateDir), "optional-dependencies.json");
                if (!File.Exists(path)) return "可选加速组件尚未检查；需要时可使用下方「Manager 与可选组件维护」。";
                var item = JsonFile.Read<OptionalDependencyStatus>(path);
                return item.Name + "：" + (item.Available ? "可用" : "不可用") + " · " + item.Detail;
            }
            catch (Exception ex) { return "无法读取可选依赖检查结果：" + ex.Message; }
        }
    }
    public bool ComfyHasUpdatePlan => checkedComfyRelease is not null && SamePath(checkedComfyRoot, Config.ComfyRoot)
        && loadedComfyPlanIdentity == Config.ComfyRoot + "\n" + Config.Python;
    public bool ComfyUpdateReady => ComfyHasUpdatePlan && !ComfyRecoveryPending;
    private void SetComfyUpdateStatus(string message)
    {
        ComfyUpdateStatus = message;
        SetStatus(message);
        ComfyUpdateChanged?.Invoke();
    }
    private void ComfyUpdatePhase(string phase) => SetComfyUpdateStatus(phase switch
    {
        "fetching" => "正在获取并核对固定版本源码…",
        "preparing" => "正在准备、下载并校验固定依赖…",
        "prepared" => "依赖已准备，正在创建轻量恢复点…",
        "source-checkout" => "正在更新 ComfyUI 源码…",
        "dependencies" => "正在安装已准备的必要依赖…",
        "health-check" => "正在验证 ComfyUI 核心启动…",
        "complete" => "ComfyUI 核心启动验证通过。",
        "restoring-source" => "正在恢复更新前的源码…",
        "restoring-configuration" => "正在核对并恢复必要配置…",
        "recovery-check" => "源码已恢复，正在检查依赖与核心启动…",
        "dependency-review" => "源码已恢复，依赖待修复。请点击「修复更新依赖」。",
        "recovered" => "源码与必要配置已恢复，核心启动检查通过。",
        _ => "正在处理 ComfyUI 更新…",
    });
    public void RestoreComfyUpdatePlan()
    {
        var identity = Config.ComfyRoot + "\n" + Config.Python;
        if (identity == loadedComfyPlanIdentity) return;
        loadedComfyPlanIdentity = identity;
        checkedComfyRelease = null; checkedComfyDependencies = null; checkedComfyRoot = "";
        ComfyCurrentVersion = "当前版本：尚未读取";
        try
        {
            var recovery = ComfyRecovery();
            var plan = new ComfyUpdatePlans(Program.StateDir).Read(Config);
            if (plan is not null)
            {
                checkedComfyRelease = plan.Release; checkedComfyDependencies = plan.Dependencies; checkedComfyRoot = plan.Root;
            }
            ComfyUpdateStatus = recovery is not null ? ComfyUpdatePresentation.From(recovery).Message
                : plan is not null ? $"已恢复固定目标 {plan.Release.Tag}。点击「更新 ComfyUI」继续；重新检查才会选择新版本。"
                : ComfyUpdatePresentation.From(null).Message;
        }
        catch (Exception ex) { ComfyUpdateStatus = "无法读取更新记录：" + ex.Message; }
    }
    private string ComfyGit()
    {
        var found = Commands.Find("git.exe");
        if (found is not null) return found;
        var artifact = Program.Runtime.Tools.Single(tool => tool.Id == "git");
        var path = Safety.Under(ToolInstallation.DirectoryFor(InstallationPlanner.ResolveRuntimeRoot(Config, Program.StateDir), artifact), artifact.Executable);
        return File.Exists(path) ? path : throw new FileNotFoundException("未找到 Git，请先完成环境安装或安装 Git 后重试。");
    }
    public async Task RefreshComfyVersionAsync()
    {
        var request = ++comfyVersionRequest;
        var config = Config.Clone();
        var text = "当前版本：尚未配置环境";
        try
        {
            if (config.ComfyRoot.Length > 0)
            {
                var git = ComfyGit();
                var commit = (await Commands.Run(git, ["rev-parse", "HEAD"], config.ComfyRoot, null, CancellationToken.None, timeout: TimeSpan.FromSeconds(15))).Trim();
                Safety.Commit(commit);
                string tag = "";
                try { tag = (await Commands.Run(git, ["describe", "--tags", "--exact-match", "HEAD"], config.ComfyRoot, null, CancellationToken.None, timeout: TimeSpan.FromSeconds(10))).Trim(); }
                catch (Exception error) when (error is IOException or InvalidOperationException) { }
                text = "当前版本：" + (ComfyReleases.StableTag(tag) ? tag + " · " : "提交 ") + commit[..12];
            }
        }
        catch (Exception ex) { text = "当前版本：无法读取 · " + ex.Message; }
        if (request != comfyVersionRequest || config.ComfyRoot != Config.ComfyRoot || config.Python != Config.Python) return;
        ComfyCurrentVersion = text;
        ComfyUpdateChanged?.Invoke();
    }
    private void RequireComfyRecoveryComplete()
    {
        var view = ComfyUpdatePresentation.From(ComfyRecovery());
        if (view.RequiresRecovery) throw new InvalidOperationException(view.Message);
    }
    private string ComfyCancellationMessage(string operation)
    {
        string message;
        try
        {
            var recovery = ComfyRecovery();
            message = operation == "check" ? "检查已停止，没有修改环境。再次检查会查询官方最新稳定版。"
                : recovery is { Status: "rolled-back" or "needs-repair" or "rollback-failed" or "updating" or "restoring" }
                    ? ComfyUpdatePresentation.From(recovery).Message
                    : "准备已暂停，当前源码未切换。已下载资源保留；继续将复用固定目标与校验后的缓存。";
        }
        catch (Exception error) { message = "操作已停止，但恢复记录无法读取；请保留记录并查看日志：" + error.Message; }
        SetComfyUpdateStatus(message);
        return message;
    }
    private Task RunComfyTaskAsync(Func<CancellationToken, Task> action, string operation = "update") => Run(async ct =>
    {
        try { await action(ct); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            SetComfyUpdateStatus(error.Message);
            throw;
        }
        finally { await RefreshComfyVersionAsync(); ComfyUpdateChanged?.Invoke(); }
    }, cancellationMessage: () => ComfyCancellationMessage(operation), allowRetry: () => !ComfyRecoveryPending);

    public Task CheckComfyUpdateAsync() => RunComfyTaskAsync(async ct =>
    {
        RequireRoot();
        RequireComfyRecoveryComplete();
        var git = ComfyGit();
        SetComfyUpdateStatus("正在查询官方最新稳定版本…");
        var releases = new ComfyReleases(resolveTag: (tag, token) => ComfyReleases.ResolveTagWithGit(git, tag, Program.StateDir, Config, token), config: Config);
        var release = await releases.LatestAsync(ct);
        var current = (await Commands.Run(git, ["rev-parse", "HEAD"], Config.ComfyRoot, null, ct, timeout: TimeSpan.FromSeconds(20))).Trim();
        if (current == release.Commit)
        {
            new ComfyUpdatePlans(Program.StateDir).Clear(Config);
            checkedComfyRelease = null; checkedComfyDependencies = null; checkedComfyRoot = "";
            SetComfyUpdateStatus("当前 ComfyUI 已是最新稳定版本 " + release.Tag + "。");
            return;
        }
        // No dependency download, solver, or stop requirement while checking.
        var plan = new ComfyUpdatePlans(Program.StateDir).Save(Config, release, new(release.Commit, "", "", []));
        checkedComfyRelease = plan.Release; checkedComfyDependencies = plan.Dependencies; checkedComfyRoot = plan.Root;
        loadedComfyPlanIdentity = Config.ComfyRoot + "\n" + Config.Python;
        SetComfyUpdateStatus($"发现稳定版 {release.Tag}。停止 ComfyUI 和工作台后点击「更新 ComfyUI」；只保存轻量恢复点。");
    }, operation: "check");

    public Task PrepareComfyUpdateAsync() => ApplyComfyUpdateAsync();
    public Task ApplyComfyUpdateAsync() => RunComfyTaskAsync(async ct =>
    {
        RestoreComfyUpdatePlan();
        RequireComfyRecoveryComplete();
        if (!ComfyHasUpdatePlan || checkedComfyRelease is null || checkedComfyDependencies is null) throw new InvalidOperationException("请先检查当前环境的稳定版本。");
        var config = Config.Clone();
        var release = checkedComfyRelease;
        var git = ComfyGit();
        var coordinator = new ComfyUpdateCoordinator(Program.StateDir, git, AppendLog) { Progress = ComfyUpdatePhase };
        SetComfyUpdateStatus("正在检查环境与独立启动验证条件…");
        await coordinator.ValidateEnvironmentAsync(config, ct);
        ComfyUpdateProbe.ValidateForRoot(config);
        var source = checkedComfyDependencies;
        if (source.RequirementsText.Length == 0)
            source = await new ComfyReleases(config: config).DependenciesAsync(release, Program.Runtime, ct);
        // Old plans are upgraded at the same commit, never silently selecting a new release.
        if (source.Resolved && (source.PlanVersion != 1 || source.WorkbenchDependencyContractVersion == 0))
            source = new(source.ComfyCommit, source.RequirementsText, source.ManagerRequirementsText, source.Constraints);
        SetComfyUpdateStatus("正在检查固定版本依赖；准备完成前不会切换源码…");
        var dependencies = await ResolveComfyUpdateDependenciesAsync(config, source, git, ct);
        checkedComfyDependencies = dependencies;
        new ComfyUpdatePlans(Program.StateDir).Save(config, release, dependencies);
        var runtime = Program.Runtime with { Comfy = Program.Runtime.Comfy with { Commit = release.Commit }, PythonConstraints = dependencies.Constraints };
        var installer = CreateInstaller(runtime);
        PreparedDependencyInstallation? prepared = null;
        await coordinator.UpdateAsync(config, release, runtime, dependencies,
            (target, _, token) => installer.InstallComfyUpdateDependenciesAsync(target,
                prepared ?? throw new InvalidOperationException("依赖资源尚未准备完成。"), token),
            ValidateComfyCoreAsync, ct,
            prepareDependencies: async (target, snapshot, token) =>
                prepared = await installer.PrepareComfyUpdateDependenciesAsync(target, snapshot, token),
            validateRecovery: ValidateRestoredComfyAsync,
            validateTargetSource: (target, parser, _) =>
            {
                ComfyUpdateProbe.ValidateSource(parser, target.ComfyArguments);
                return Task.CompletedTask;
            });
        checkedComfyRelease = null; checkedComfyDependencies = null; checkedComfyRoot = "";
        new ComfyUpdatePlans(Program.StateDir).Clear(config);
        SetComfyUpdateStatus($"已更新至 {release.Tag}，ComfyUI 核心启动验证通过。可启动工作台，或恢复上次版本。");
    });

    private Task<ComfyDependencySnapshot> ResolveComfyUpdateDependenciesAsync(LauncherConfig config, ComfyDependencySnapshot snapshot, string git, CancellationToken ct)
        => ComfyDependencyResolver.ResolveAsync(config, snapshot,
            Path.Combine(InstallationPlanner.ResolveRuntimeRoot(config, Program.StateDir), "cache"), ct, AppendLog,
            nodeRequirements: ComfyDependencyResolver.ExistingNodeRequirements(config),
            environment: new Dictionary<string, string> { ["PATH"] = Path.GetDirectoryName(git) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
                ["PYTHONDONTWRITEBYTECODE"] = "1", ["PYTHONNOUSERSITE"] = "1" },
            sam2Commit: Program.Runtime.Sam2Commit, dependencyProgress: DependencyProgressObserver(), includeWorkbench: true);

    private async Task ValidateComfyCoreAsync(LauncherConfig config, CancellationToken ct)
    {
        SetComfyUpdateStatus("正在检查依赖和 ComfyUI 核心启动；验证使用独立临时目录…");
        await Commands.Run(config.Python, ["-B", "-m", "pip", "check"], config.ComfyRoot, AppendLog, ct, timeout: TimeSpan.FromMinutes(2));
        await Services.ProbeComfyUpdateAsync(config, ct);
    }
    private async Task ValidateRestoredComfyAsync(LauncherConfig config, CancellationToken ct)
    {
        var report = await WorkbenchDependencies.InspectFilesAsync(config,
            new[] { "requirements.txt", "manager_requirements.txt" }.Select(name => Path.Combine(config.ComfyRoot, name)), ct);
        if (!report.Ready) throw new InvalidOperationException("恢复版本的依赖声明尚未通过检查：" + string.Join("；", report.Issues.Select(issue => issue.Message)));
        await ValidateComfyCoreAsync(config, ct);
    }
    public Task RollbackComfyUpdateAsync() => RunComfyTaskAsync(async ct =>
    {
        var coordinator = new ComfyUpdateCoordinator(Program.StateDir, ComfyGit(), AppendLog) { Progress = ComfyUpdatePhase };
        await coordinator.RollbackAsync(Config, ct, ValidateRestoredComfyAsync);
        new ComfyUpdatePlans(Program.StateDir).Clear(Config);
        Config = LoadConfig();
        checkedComfyRelease = null; checkedComfyDependencies = null; checkedComfyRoot = "";
        SetComfyUpdateStatus(ComfyUpdatePresentation.From(ComfyRecovery()).Message);
        Reloaded();
    });
    public Task RepairComfyUpdateDependenciesAsync() => RunComfyTaskAsync(async ct =>
    {
        RequireRoot();
        var config = Config.Clone();
        var git = ComfyGit();
        var coordinator = new ComfyUpdateCoordinator(Program.StateDir, git, AppendLog);
        var recovery = ComfyRecovery();
        if (recovery?.Status != "needs-repair")
            throw new InvalidOperationException("当前没有待修复的更新恢复记录。常规维护请使用「Manager 与可选组件维护」。");
        await coordinator.ValidateEnvironmentAsync(config, ct);
        ComfyUpdateProbe.ValidateForRoot(config);
        var commit = (await Commands.Run(git, ["rev-parse", "HEAD"], config.ComfyRoot, null, ct)).Trim();
        if (commit != recovery.PreviousCommit)
            throw new InvalidOperationException("恢复后源码版本已改变，请先处理版本差异，原恢复记录保持不变。");
        var requirements = await Commands.Run(git, ["show", commit + ":requirements.txt"], config.ComfyRoot, null, ct);
        var manager = await Commands.Run(git, ["show", commit + ":manager_requirements.txt"], config.ComfyRoot, null, ct);
        SetComfyUpdateStatus("正在准备当前源码版本的依赖修复；GPU 版本保持固定…");
        var dependencies = await ResolveComfyUpdateDependenciesAsync(config, new(commit, requirements, manager, []), git, ct);
        var installer = CreateInstaller(Program.Runtime with { Comfy = Program.Runtime.Comfy with { Commit = commit } });
        var prepared = await installer.PrepareComfyUpdateDependenciesAsync(config, dependencies, ct);
        await installer.InstallComfyUpdateDependenciesAsync(config, prepared, ct);
        await coordinator.CompleteDependencyRepairAsync(config, ValidateComfyCoreAsync, ct);
        SetComfyUpdateStatus("当前版本依赖已修复，ComfyUI 核心启动验证通过。插件将在正常启动工作台时检查。");
    });

    // Explicit advanced maintenance retains optional-component behavior.
    public Task RepairComfyDependenciesAsync() => Run(async ct =>
    {
        RequireRoot();
        RequireComfyRecoveryComplete();
        var installer = CreateInstaller(Program.Runtime);
        await installer.RepairDependencies(Config, null, ct);
        if (!Config.ComfyArguments.Contains("--enable-manager")) Config.ComfyArguments.Add("--enable-manager");
        Save(); Reloaded();
        SetComfyUpdateStatus("依赖维护完成，Manager 将在下次启动时启用。可选加速组件结果见日志。");
    });
}
