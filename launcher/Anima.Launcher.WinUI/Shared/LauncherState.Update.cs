using Anima.Launcher.Core;

namespace Anima.Launcher;

internal sealed partial class LauncherState
{
    private LauncherUpdates? softwareUpdates;
    private readonly CancellationTokenSource softwareLifetime = new();
    private LauncherRelease? softwareRelease;
    private InstalledLauncher? downloadedSoftware;
    private bool softwareExitPending;
    private bool softwareInitialized;
    private LauncherUpdates SoftwareUpdates => softwareUpdates ??= new(Program.StateDir, Environment.ProcessPath!, Program.Version, AppendLog);

    public event Action? SoftwareUpdateChanged;
    public event Action? SoftwareUpdateExitRequested;
    public bool CheckingSoftwareUpdate { get; private set; }
    public bool SoftwareUpdateExitPending => softwareExitPending;
    public bool DownloadingSoftwareUpdate { get; private set; }
    public bool SoftwareDownloadPaused { get; private set; }
    public double SoftwareUpdatePercent { get; private set; } = -1;
    public string SoftwareUpdateStatus { get; private set; } = "可检查启动器和工作台的新版本。便携版与安装版共用更新。";
    public bool HasSoftwareRelease => softwareRelease is not null;
    public bool SoftwareUpdateReady => downloadedSoftware is not null;
    public string SoftwareReleaseNotes => softwareRelease?.Notes ?? "";
    public string SoftwareVersionText => $"当前版本 {Program.Version}" + (softwareRelease is { } release ? $"  ·  新版本 {release.Version}（{release.Size / 1048576d:N1} MiB）"
        : downloadedSoftware is { } downloaded ? $"  ·  已下载 {downloaded.Version}（{downloaded.Size / 1048576d:N1} MiB）" : "");
    public string SoftwareUpdateNotice => SoftwareUpdateReady ? "新版已准备好，可在设置中重启更新。"
        : HasSoftwareRelease ? $"发现新版本 {softwareRelease!.Version}，可在软件内下载更新。" : "";

    private void NotifySoftwareUpdate() => SoftwareUpdateChanged?.Invoke();

    public async Task InitializeSoftwareUpdatesAsync()
    {
        if (!softwareInitialized)
        {
            softwareInitialized = true;
            try
            {
                softwareRelease = SoftwareUpdates.GetCachedRelease();
                downloadedSoftware = SoftwareUpdates.GetDownloaded();
                if (softwareRelease is not null && !IsNewerSoftwareVersion(softwareRelease.Version)) softwareRelease = null;
                MatchDownloadedSoftware();
                if (SoftwareUpdateReady) SoftwareUpdateStatus = "新版已下载并校验，可在空闲时重启更新。";
                NotifySoftwareUpdate();
            }
            catch (Exception ex) { AppendLog("读取更新状态失败：" + ex.Message); }
        }
        if (Config.AutoCheckUpdates) await CheckSoftwareUpdatesAsync(false);
    }

    private static bool IsNewerSoftwareVersion(string candidate) => Version.TryParse(candidate.TrimStart('v'), out var next)
        && Version.TryParse(Program.Version, out var current) && next > current;

    private void MatchDownloadedSoftware()
    {
        if (downloadedSoftware is not null && (!IsNewerSoftwareVersion(downloadedSoftware.Version)
            || softwareRelease is not null && (downloadedSoftware.Version != softwareRelease.Version
                || !downloadedSoftware.Sha256.Equals(softwareRelease.Sha256, StringComparison.OrdinalIgnoreCase))))
            downloadedSoftware = null;
    }

    public void SetAutomaticSoftwareUpdates(bool enabled)
    {
        if (Busy) return;
        var previous = Config.AutoCheckUpdates;
        try { Config.AutoCheckUpdates = enabled; Save(); }
        catch (Exception ex)
        {
            Config.AutoCheckUpdates = previous;
            SoftwareUpdateStatus = "无法保存自动检查设置：" + ex.Message;
            AppendLog(SoftwareUpdateStatus);
        }
        NotifySoftwareUpdate();
    }

    public async Task CheckSoftwareUpdatesAsync(bool manual)
    {
        if (CheckingSoftwareUpdate || Busy || softwareLifetime.IsCancellationRequested || (!manual && !Config.AutoCheckUpdates)) return;
        CheckingSoftwareUpdate = true;
        if (manual) SoftwareUpdateStatus = "正在检查官方稳定版本…";
        NotifySoftwareUpdate();
        try
        {
            var release = await SoftwareUpdates.CheckAsync(manual, softwareLifetime.Token);
            softwareRelease = release is not null && IsNewerSoftwareVersion(release.Version) ? release : null;
            downloadedSoftware = SoftwareUpdates.GetDownloaded();
            MatchDownloadedSoftware();
            SoftwareUpdateStatus = SoftwareUpdateReady ? "新版已下载并校验，可在空闲时重启更新。"
                : softwareRelease is not null ? $"发现 {softwareRelease.Version}。下载不会中断生图，安装需要工作台空闲。"
                : "当前已是最新稳定版本。";
        }
        catch (OperationCanceledException) when (softwareLifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            SoftwareUpdateStatus = "暂时无法检查更新，请稍后重试。";
            AppendLog("检查软件更新失败：" + ex.Message);
        }
        finally { CheckingSoftwareUpdate = false; NotifySoftwareUpdate(); }
    }

    public Task DownloadSoftwareUpdateAsync() => Run(DownloadSoftwareUpdateCoreAsync, applyDrafts: false);

    private async Task DownloadSoftwareUpdateCoreAsync(CancellationToken ct)
    {
        var release = softwareRelease ?? throw new InvalidOperationException("请先检查新版本。");
        DownloadingSoftwareUpdate = true;
        SoftwareDownloadPaused = false;
        SoftwareUpdatePercent = -1;
        SoftwareUpdateStatus = "正在下载新版；当前生图任务保持运行。";
        NotifySoftwareUpdate();
        var footerProgress = DownloadProgress();
        var progress = new Progress<DownloadProgress>(p =>
        {
            SoftwareUpdatePercent = p.Total > 0 ? p.Received * 100d / p.Total : -1;
            SoftwareUpdateStatus = $"{p.Stage} · {p.Received / 1048576d:N1} / {p.Total / 1048576d:N1} MiB";
            footerProgress.Report(p);
            NotifySoftwareUpdate();
        });
        try
        {
            downloadedSoftware = await SoftwareUpdates.DownloadAsync(release, Downloads, progress, ct, Config);
            SoftwareUpdateStatus = "下载与完整性校验完成。空闲时点击「重启并更新」。";
            SetStatus(SoftwareUpdateStatus);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            SoftwareDownloadPaused = true;
            SoftwareUpdateStatus = "下载已暂停；已下载内容保留，可继续下载。";
            throw;
        }
        catch (Exception ex)
        {
            SoftwareDownloadPaused = true;
            SoftwareUpdateStatus = "下载未完成，可重试：" + ex.Message;
            throw;
        }
        finally { DownloadingSoftwareUpdate = false; NotifySoftwareUpdate(); }
    }

    public async Task RestartForSoftwareUpdateAsync()
    {
        if (Busy || CheckingSoftwareUpdate || downloadedSoftware is null || Root is null) return;
        if (Config.SetupComplete && DevAppRoot)
        {
            SoftwareUpdateStatus = "当前工作台使用源码目录，完整更新不能自动替换源码。请改用发布版工作台后更新。";
            NotifySoftwareUpdate();
            return;
        }
        var currentEnvironment = new EnvironmentFields(FreshInstallRequested && Config.InstallRoot.Length > 0 ? Config.InstallRoot : Config.ComfyRoot,
            Config.Python, Config.SourceId, Config.GitMirror, Config.ModelMirror, Config.PipIndex, Config.CustomRepository, FreshInstallRequested, Config.PythonExplicit);
        if (PendingSettingsFields is { } settings && !settings.Matches(Config) || PendingEnvironmentFields is { } environment && environment != currentEnvironment)
        {
            SoftwareUpdateStatus = "还有未保存的设置，请先保存设置或完成环境设置，再重启更新。";
            NotifySoftwareUpdate();
            return;
        }
        var message = "即将更新启动器和工作台并重新打开软件。模型、配置和历史数据会保留；原来停止的服务保持停止。";
        if (Config.PinnedAppRoot.Length > 0) message += "\n本次完整更新将解除工作台的固定回退版本。";
        if (!await Dialogs.Confirm(Root, "重启并更新", message, "重启并更新", "稍后")) return;
        var handedOff = false;
        await Run(async ct =>
        {
            var wasRunning = await ServiceManager.PortOpen(Config.WebPort, ct);
            if (wasRunning)
            {
                var health = await ServiceManager.Json(Config.WebUrl + "/api/launcher-health", ct);
                if (!ServiceManager.IsOurWebUi(health, Config)) throw new IOException("当前工作台不属于此环境，无法安全更新。");
                if (await IsWorkbenchBusyAsync(ct)) throw new IOException("工作台正在生成或有排队任务。新版已保留，请在空闲后重试。");
            }
            var transaction = await SoftwareUpdates.PrepareAsync(downloadedSoftware, Config, wasRunning, ct);
            handedOff = SoftwareUpdates.StartCoordinator(transaction);
            if (!handedOff) throw new IOException("无法启动更新协调进程，当前版本保持运行。");
        }, applyDrafts: false);
        if (handedOff) SoftwareUpdateExitRequested?.Invoke();
        else
        {
            downloadedSoftware = SoftwareUpdates.GetDownloaded();
            MatchDownloadedSoftware();
            if (downloadedSoftware is null) SoftwareUpdateStatus = "下载文件校验未通过，请重新下载更新。";
            NotifySoftwareUpdate();
        }
    }

    /// <summary>Run before ordinary onboarding, auto-start, or update checks.</summary>
    public async Task CompleteSoftwareUpdateStartupAsync()
    {
        var context = Program.UpdateContext;
        if (context is null) return;
        task = new CancellationTokenSource();
        BusyChanged?.Invoke(true);
        try
        {
            SetStatus(Program.IsUpdateRecovery ? "正在恢复上一版本…" : "正在验证并应用新版…");
            if (Program.IsUpdateRecovery)
                await RecoverSoftwareUpdateAsync(context.PreviousConfig, context.WorkbenchWasRunning, task.Token);
            else
                await ApplySoftwareUpdateAsync(context.PreviousConfig, context.WorkbenchWasRunning, task.Token);
            await Program.CompleteUpdateAsync(true);
            SynchronizeSetupVersion();
            SetStatus(Program.IsUpdateRecovery ? "已恢复上一版本，配置和历史数据保持不变。" : "更新完成。启动器和工作台已切换到新版。");
        }
        catch (Exception ex)
        {
            AppendLog("软件更新启动检查失败：" + ex);
            try { await Program.CompleteUpdateAsync(false, ex.Message); }
            catch (Exception reportError) { AppendLog("更新结果写入失败：" + reportError.Message); }
            if (Program.IsUpdateRecovery)
                SetStatus("原版本已恢复，工作台未能启动。请查看日志并重试启动服务：" + ex.Message);
            else softwareExitPending = true;
        }
        finally
        {
            task.Dispose();
            task = null;
            BusyChanged?.Invoke(false);
            Reloaded();
            if (softwareExitPending) SoftwareUpdateExitRequested?.Invoke();
        }
    }

    public void RequestSoftwareUpdateExit()
    {
        softwareExitPending = true;
        if (Busy) task?.Cancel();
        else SoftwareUpdateExitRequested?.Invoke();
    }

    private void DisposeSoftwareUpdates()
    {
        softwareLifetime.Cancel();
        softwareUpdates?.Dispose();
        softwareLifetime.Dispose();
    }
}
