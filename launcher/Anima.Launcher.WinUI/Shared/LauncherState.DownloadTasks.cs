using Anima.Launcher.Core;
using Microsoft.UI.Dispatching;
using System.Text.Json;

namespace Anima.Launcher;

internal sealed partial class LauncherState
{
    public DownloadTaskService DownloadTasks { get; private set; } = null!;
    public event Action? DownloadsChanged;
    private DispatcherQueue downloadUi = null!;
    private readonly Dictionary<string, Task> ownerExecutions = new();
    private bool shuttingDown;
    private string? activeDownloadOwner;
    private LauncherConfig? savedDownloadSettings;
    private InstallationPlan? activeInstallationPlan;
    private InstallationPlan? FrozenModelPlan => activeInstallationPlan ?? (SetupActive && Setup.InstallStarted ? SetupPlan : null);
    private RuntimeManifest OperationRuntime => DownloadIntegrationPolicy.ModelRuntime(FrozenModelPlan, Program.Runtime);
    private ModelManifest OperationModels => FrozenModelPlan is { FrozenModels.Length: > 0 } plan
        ? new(Program.Models.SchemaVersion, plan.FrozenModels.Concat(Program.Models.Models.Where(m => !plan.FrozenModels.Any(f => f.Id == m.Id))).ToArray()) : Program.Models;
    private ModelProfilesManifest OperationProfiles => FrozenModelPlan?.FrozenProfile is { } profile
        ? Program.ModelProfiles with { Profiles = Program.ModelProfiles.Profiles.Where(p => p.Id != profile.Id).Append(profile).ToArray() } : Program.ModelProfiles;
    private sealed record FrozenModelContext(ModelProfileDefinition Profile, ModelItem[] ProfileModels, RuntimeManifest Runtime);
    private JsonElement CaptureModelContext(LauncherConfig config) => JsonSerializer.SerializeToElement(new FrozenModelContext(
        OperationProfiles.Get(config.SelectedProfile), OperationProfiles.RequiredModels(OperationModels, config.SelectedProfile),
        OperationRuntime), JsonFile.Options);

    private void InitializeDownloadTasks()
    {
        downloadUi = DispatcherQueue.GetForCurrentThread();
        savedDownloadSettings = Config.Clone();
        DownloadTasks = new(Downloads, Program.StateDir);
        if (DownloadTasks.RecoveryWarning is { } warning) AppendLog(warning);
        InitializePresentation();
        DownloadTasks.Changed += NotifyDownloads;
    }

    private void UpdateDurableModelProgress()
    {
        if (Busy) return; // The installation coordinator publishes its own complete batch.
        var items = Rows.Select(r => r.Download).Where(d => d is not null).Cast<ModelDownloadSnapshot>().ToArray();
        if (!items.Any(i => i.Status is DownloadTaskStatus.Running or DownloadTaskStatus.Queued or DownloadTaskStatus.Pausing)) return;
        var total = items.Sum(i => i.Total);
        var received = items.Sum(i => i.Received);
        var speed = items.Sum(i => i.Speed);
        var completed = items.Count(i => i.IsCompleted);
        var remaining = speed > 0 ? TimeSpan.FromSeconds(Math.Min(359999, Math.Max(0, total - received) / speed)) : (TimeSpan?)null;
        PublishBatch(new("模型任务", "在模型条目中暂停或继续；更多操作可打开查看全部下载", received, total, received, total, speed, remaining, completed, items.Length));
    }

    public bool HasActiveDownloads => DownloadTasks.HasRunningTasks;
    private bool HasActiveModelDownload() => DownloadTasks.Owners.Any(o => o.Kind == "models" && o.Status == DownloadTaskStatus.Running);

    public void SaveDownloadSettings(string source, string git, string hf, string pip, string networkMode, string proxy)
    {
        var next = Config.Clone();
        next.SourceId = source;
        next.GitMirror = git.Trim();
        next.ModelMirror = hf.Trim();
        next.PipIndex = pip.Trim();
        next.NetworkMode = networkMode;
        next.ProxyAddress = proxy.Trim();
        next.Validate();
        if (configWriteError is not null) throw new IOException(configWriteError);
        JsonFile.Write(configFile, next);
        Config = next;
        savedDownloadSettings = next.Clone();
        // Only new work uses these values. Running and paused owners keep their snapshot.
        if (PendingEnvironmentFields is { } draft)
            PendingEnvironmentFields = draft with { SourceId = source, GitMirror = git.Trim(), HfMirror = hf.Trim(), PipIndex = pip.Trim() };
        SetStatus("下载设置已保存。新任务使用新设置；暂停任务可选择按原设置或新来源继续。");
    }

    private LauncherConfig WithSavedDownloadSettings(LauncherConfig configuration)
    {
        return savedDownloadSettings is { } saved ? DownloadIntegrationPolicy.WithDownloadSettings(configuration, saved) : configuration;
    }

    private LauncherConfig ModelDownloadConfiguration()
    {
        var frozen = activeDownloadOwner is { } id ? DownloadTasks.Owners.Single(owner => owner.Id == id).Configuration
            : FrozenModelPlan?.CreateConfig();
        return frozen is null ? WithSavedDownloadSettings(Config).Clone() : DownloadIntegrationPolicy.WithDownloadSettings(Config, frozen);
    }

    public async Task StartSelectedModelDownloadsAsync()
    {
        if (Busy || SetupActive || preparingModelDownloads || shuttingDown) return;
        using var cacheUse = await LauncherCache.AcquireUseAsync(Program.StateDir, modelInspectionLifetime.Token);
        var inspection = TrackModelInspection();
        DownloadOwnerRecord[] owners = [];
        preparingModelDownloads = true;
        try
        {
            RefreshModelDownloadRows();
            NotifyDownloads();
            var ids = Rows.Where(r => r.Selected).Select(r => r.Status.Model.Id).Distinct().ToArray();
            if (ids.Length == 0) { SetStatus("请勾选需要下载的模型。"); return; }
            ApplyPendingFields();
            var snapshot = Config.Clone();
            var originalContext = ModelContext(Config);
            ResolveModelConfiguration(snapshot);
            var context = CaptureModelContext(snapshot);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(modelInspectionLifetime.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(30));
            using var lease = await EnvironmentOperationLease.AcquireAsync(snapshot.ComfyRoot, false, timeout.Token);
            var selected = await ResolveModelIdsAsync(ids, true, timeout.Token, snapshot);
            foreach (var item in selected.Where(s => s.Missing)) ValidateDownload(item);
            await CheckModelSpaceAsync(selected.Where(s => s.Missing), timeout.Token);
            if (Busy || originalContext != ModelContext(Config) || shuttingDown) return;
            owners = selected.Select(item => DownloadTasks.RegisterOwner("models", "模型 · " + item.Model.Name, snapshot,
                [new ModelDownloadIntent(item.Model, item.FoundPath ?? item.Destination)], payload: context)).ToArray();
        }
        catch (OperationCanceledException) when (shuttingDown) { }
        catch (Exception ex)
        {
            if (shuttingDown) AppendLog("模型预检：" + ex.Message);
            else TaskFailed?.Invoke(ex);
        }
        finally
        {
            try
            {
                preparingModelDownloads = false;
                if (!shuttingDown)
                {
                    RefreshModelDownloadRows();
                    NotifyDownloads();
                }
            }
            finally { CompleteModelInspection(inspection); }
        }
        await Task.WhenAll(owners.Select(owner => ResumeDownloadOwnerAsync(owner.Id)));
    }

    public async Task ContinueAllDownloadsAsync()
    {
        foreach (var legacy in DownloadTasks.Owners.Where(o => o.Kind == "models" && o.Models.Length > 1
            && o.Status is DownloadTaskStatus.Paused or DownloadTaskStatus.Failed or DownloadTaskStatus.NeedsReview))
            DownloadTasks.EnsureResourceOwners(legacy.Id);
        var canceledFiles = DownloadTasks.Tasks.Where(t => t.Status == DownloadTaskStatus.Canceled).SelectMany(t => t.OwnerIds).ToHashSet();
        var owners = DownloadTasks.Owners.Where(o => !o.IsResourceGroup && !canceledFiles.Contains(o.Id)
            && o.Status is DownloadTaskStatus.Paused or DownloadTaskStatus.Failed or DownloadTaskStatus.NeedsReview).ToArray();
        var software = DownloadIntegrationPolicy.LatestSoftwareOwner(owners, Program.Version);
        var skippedSoftware = owners.Count(o => o.Kind == "software" && o.Id != software?.Id);
        // Installation coordinators own an environment gate. Software candidates share one ready-state slot.
        foreach (var owner in owners.Where(o => o.Kind == "setup" || o.Kind == "install")) await ResumeDownloadOwnerAsync(owner.Id);
        await Task.WhenAll(owners.Where(o => o.Kind == "models" || o.Id == software?.Id).Select(o => ResumeDownloadOwnerAsync(o.Id)));
        await Task.WhenAll(DownloadTasks.Tasks.Where(t => t.OwnerIds.Count == 0 && t.Status is DownloadTaskStatus.Paused or DownloadTaskStatus.Failed)
            .Select(t => DownloadTasks.ResumeTaskAsync(t.Id)));
        if (skippedSoftware > 0)
            SetStatus($"一键继续仅选择最高的新软件版本；其余 {skippedSoftware} 个旧版或无效更新任务已保留，未自动应用更新。");
    }

    public async Task ResumeDownloadOwnerAsync(string id, bool newSources = false)
    {
        if (shuttingDown || ownerExecutions.ContainsKey(id)) return;
        IDisposable? softwareReservation = null;
        try
        {
            using var cacheUse = await LauncherCache.AcquireUseAsync(Program.StateDir, modelInspectionLifetime.Token);
            var owner = DownloadTasks.Owners.Single(o => o.Id == id);
            if (owner.Kind == "models" && owner.Models.Length > 1)
            {
                var resources = DownloadTasks.EnsureResourceOwners(id);
                await Task.WhenAll(resources.Where(o => o.Status is not (DownloadTaskStatus.Canceled or DownloadTaskStatus.Completed))
                    .Select(o => ResumeDownloadOwnerAsync(o.Id, newSources)));
                return;
            }
            if (owner.Kind == "software")
            {
                if (CheckingSoftwareUpdate || (softwareReservation = softwareDownloadGate.TryEnter()) is null)
                { SetStatus("请等待当前软件版本检查或下载结束后继续此更新，任务已保留。"); return; }
            }
            if (newSources)
            {
                DownloadTasks.UpdateOwnerSources(id, WithSavedDownloadSettings(Config));
                owner = DownloadTasks.Owners.Single(o => o.Id == id);
            }
            if (owner.Kind is "setup" or "install")
            {
                if (Busy) { SetStatus("请等待当前环境操作结束后继续安装。"); return; }
                var plan = owner.Payload?.Deserialize<InstallationPlan>(JsonFile.Options)
                    ?? throw new InvalidDataException("安装任务缺少已确认的固定计划。");
                foreach (var active in DownloadTasks.Owners.Where(o => o.Kind == "models" && o.Status == DownloadTaskStatus.Running
                    && SamePath(o.EnvironmentRoot, owner.EnvironmentRoot)))
                    await DownloadTasks.PauseOwnerAsync(active.Id);
                await Run(async ct =>
                {
                    activeDownloadOwner = id;
                    try { await DownloadTasks.RunOwnerAsync(id, token => ResumeFrozenInstallationAsync(owner, plan, token), ct); }
                    finally { activeDownloadOwner = null; }
                },
                    applyDrafts: false, resumeConfig: owner.Configuration, resumeFresh: plan.Fresh, setupOperation: true);
                return;
            }
            if (Busy && owner.Kind == "models" && SamePath(owner.EnvironmentRoot, Config.ComfyRoot))
                throw new InvalidOperationException("此环境正在安装或维护，请等待环境操作结束后继续模型下载。");
            var execution = DownloadTasks.RunOwnerAsync(id, ct => owner.Kind switch
            {
                "models" => DownloadFrozenModelsAsync(owner, ct),
                "software" => DownloadFrozenSoftwareAsync(owner, ct),
                _ => throw new InvalidOperationException("该任务需要从原安装步骤继续：" + owner.Title),
            }, CancellationToken.None);
            ownerExecutions[id] = execution;
            try { await execution; }
            finally { ownerExecutions.Remove(id); }
        }
        catch (OperationCanceledException) { SetStatus("下载已停止，进度已保留。"); }
        catch (Exception ex) { AppendLog(ex.Message); SetStatus("下载未完成：" + ex.Message); }
        finally { softwareReservation?.Dispose(); }
    }

    private async Task DownloadFrozenModelsAsync(DownloadOwnerRecord owner, CancellationToken ct, IProgress<DownloadProgress>? progress = null)
    {
        // Setup already owns the exclusive lease for this environment on the UI coordinator.
        using var environmentLease = Busy && SamePath(owner.EnvironmentRoot, Config.ComfyRoot)
            ? null : await EnvironmentOperationLease.AcquireAsync(owner.EnvironmentRoot, false, ct);
        var failures = new List<string>();
        var frozen = owner.Payload?.Deserialize<FrozenModelContext>(JsonFile.Options);
        var catalog = new ModelManifest(Program.Models.SchemaVersion, owner.Models.Select(m => m.Model)
            .Concat(frozen?.ProfileModels ?? []).Concat(Program.Models.Models).DistinctBy(m => m.Id).ToArray());
        var profiles = frozen is null ? Program.ModelProfiles : new ModelProfilesManifest(1, frozen.Profile.Id, [frozen.Profile]);
        await Task.WhenAll(owner.Models.OrderByDescending(m => m.Required).Select(async intent =>
        {
            var token = ct;
            try
            {
                using var scope = DownloadTasks.WithinOwner(owner.Id, intent.Required);
                if (intent.Model.IsPackage)
                    await ModelPackages.Fetch(Downloads, intent.Model, intent.Destination, owner.Configuration, progress, token);
                else
                    await Downloads.Fetch(intent.Model.Name, DownloadSources.ModelCandidates(intent.Model, owner.Configuration), intent.Destination,
                        intent.Model.Size, intent.Model.Sha256, progress, token);
                if (intent.Model.Category == "diffusion_models")
                    await ModelMetadata.EnsureDefaultAsync(owner.Configuration, frozen?.Runtime ?? Program.Runtime, catalog, profiles, intent.Destination, token, AppendLog);
                DownloadTasks.MarkModelCompleted(owner.Id, intent.Model.Id);
                PublishCompletedModel(owner.Configuration, intent);
            }
            catch (Exception ex) when (!intent.Required && ex is not OperationCanceledException)
            {
                lock (failures) failures.Add(intent.Model.Name + "：" + ex.Message);
            }
        }));
        if (failures.Count > 0) throw new IOException("必需模型已就绪；可选项尚未完成：\n" + string.Join("\n", failures));
        downloadUi.TryEnqueue(() => SetStatus("模型已全部校验并发布。运行中的 ComfyUI 可在空闲时重启以刷新清单。"));
    }

    public async Task ResumeDownloadResourceAsync(string taskId, string ownerId, bool newSources)
    {
        var owner = DownloadTasks.Owners.Single(o => o.Id == ownerId);
        var file = DownloadTasks.Tasks.Single(t => t.Id == taskId);
        if (owner.Kind != "models" || owner.Models.Length <= 1)
        { await ResumeDownloadOwnerAsync(ownerId, newSources); return; }
        var intent = owner.Models.SingleOrDefault(m => m.Model.IsPackage
            ? m.Model.Files.Any(member => SamePath(Safety.Under(ModelPackages.Staging(m.Model, m.Destination), member.Path), file.Destination))
            : SamePath(m.Destination, file.Destination)) ?? throw new InvalidDataException("此文件无法对应到原模型身份。");
        // A file retry retains the whole package identity, but does not restart unrelated models in its former batch.
        var resource = DownloadTasks.EnsureResourceOwners(owner.Id).Single(o => o.Models.Any(m => m.Model.Id == intent.Model.Id));
        await ResumeDownloadOwnerAsync(resource.Id, newSources);
    }

    private async Task DownloadFrozenSoftwareAsync(DownloadOwnerRecord owner, CancellationToken ct)
    {
        var release = owner.Payload?.Deserialize<LauncherRelease>(JsonFile.Options)
            ?? throw new InvalidDataException("更新任务缺少固定发布信息。");
        LauncherUpdates.ValidateRelease(release);
        if (!IsNewerSoftwareVersion(release.Version)) throw new InvalidOperationException("此更新不高于当前版本，任务已保留，无需继续下载。");
        downloadedSoftware = null;
        softwareRelease = release;
        await DownloadSoftwareUpdateCoreAsync(ct, release, owner.Configuration);
    }

    private async Task ResumeFrozenInstallationAsync(DownloadOwnerRecord owner, InstallationPlan plan, CancellationToken ct)
    {
        string? installedCommit = null;
        var receipt = Path.Combine(plan.ComfyRoot, ".anima-revision.json");
        if (File.Exists(receipt))
        {
            var revision = JsonFile.Read<JsonElement>(receipt);
            if (revision.TryGetProperty("commit", out var commit)) installedCommit = commit.GetString();
        }
        var recovery = DownloadOwnerRecovery.Prepare(owner, plan, Setup, Program.Models, Program.ModelProfiles, installedCommit);
        Config = WithSavedDownloadSettings(recovery.Configuration);
        plan = recovery.Plan;
        activeInstallationPlan = plan;
        FreshInstallRequested = plan.Fresh;
        if (owner.Kind == "setup")
        {
            Setup = recovery.Setup;
            SetupPlan = plan;
            SetupActive = true;
            ResetSetupStages();
            await ExecuteReviewedSetupAsync(plan, ct);
        }
        else await InstallEnvironmentAsync(ct, plan);
    }

    public async Task PauseDownloadsAndExitAsync()
    {
        shuttingDown = true;
        var inspections = StopModelInspectionsAsync();
        task?.Cancel();
        await DownloadTasks.PauseAllAsync();
        await inspections;
        var running = ownerExecutions.Values.ToArray();
        if (running.Length > 0) { try { await Task.WhenAll(running); } catch { } }
        // Cancellation of install subprocesses is awaited by the existing environment coordinator.
        while (Busy) await Task.Delay(50);
        await DownloadTasks.DisposeAsync();
    }
}
