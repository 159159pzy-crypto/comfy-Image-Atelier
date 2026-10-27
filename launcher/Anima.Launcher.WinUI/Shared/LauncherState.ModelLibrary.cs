using Anima.Launcher.Core;

namespace Anima.Launcher;

internal sealed partial class LauncherState
{
    private bool preparingModelDownloads;
    private string modelLibraryContext = "";
    private int modelScanRevision;
    private readonly CancellationTokenSource modelInspectionLifetime = new();
    private readonly object modelInspectionSync = new();
    private readonly HashSet<TaskCompletionSource> modelInspections = [];
    public bool PreparingModelDownloads => preparingModelDownloads;
    private static string ModelContext(LauncherConfig config) => System.Text.Json.JsonSerializer.Serialize(new
    { config.ComfyRoot, config.Python, config.ComfyArguments, config.SelectedProfile });
    private string CurrentModelRoot()
    {
        try { return Inventory.NormalizeRoot(Config.ComfyRoot); }
        catch { return Config.ComfyRoot; }
    }
    private static void ResolveModelConfiguration(LauncherConfig configuration)
    {
        configuration.ComfyRoot = Inventory.NormalizeRoot(configuration.ComfyRoot);
        configuration.Python = Inventory.FindPython(configuration.ComfyRoot, configuration.Python);
    }

    public void RefreshModelDownloadRows()
    {
        var snapshot = DownloadTasks.GetSnapshot();
        var environment = CurrentModelRoot();
        foreach (var row in Rows)
            row.UpdateDownload(ModelDownloadProjection.ForModel(environment, row.Status.Model, row.Status.Destination,
                snapshot.Owners, snapshot.Tasks), Busy || preparingModelDownloads || shuttingDown);
    }

    public async Task EnsureModelLibraryAsync()
    {
        if (Busy || SetupActive || shuttingDown || string.IsNullOrWhiteSpace(Config.ComfyRoot)) return;
        if (modelLibraryContext == ModelContext(Config) && Rows.Count > 0) { RefreshModelDownloadRows(); return; }
        // Durable intents remain accessible even if the current Python/path probe cannot run.
        var environment = CurrentModelRoot();
        var intents = DownloadTasks.Owners.Where(o => o.Kind == "models" && SamePath(o.EnvironmentRoot, environment))
            .OrderByDescending(o => o.UpdatedAt).SelectMany(o => o.Models).DistinctBy(m => m.Model.Id).ToArray();
        RebuildRows(intents.Select(m => new ModelStatus(m.Model, "缺失", null, m.Destination)).ToList());
        // Do not treat the fallback as a successful scan; a later visit may recover the probe.
        modelLibraryContext = "";
        await RefreshModelLibraryAsync(false, false);
    }

    public async Task RefreshModelLibraryAsync(bool hashes = false, bool applyDrafts = true)
    {
        if (Busy || SetupActive || shuttingDown) return;
        var inspection = TrackModelInspection();
        var revision = ++modelScanRevision;
        try
        {
            if (applyDrafts) ApplyPendingFields();
            var configuration = Config.Clone();
            if (string.IsNullOrWhiteSpace(configuration.ComfyRoot)) return;
            var context = ModelContext(configuration);
            ResolveModelConfiguration(configuration);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(modelInspectionLifetime.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(hashes ? 30 : 2));
            using var lease = await EnvironmentOperationLease.AcquireAsync(configuration.ComfyRoot, false, timeout.Token);
            var scan = await ResolveModelIdsAsync(OperationModels.Models.Select(m => m.Id).ToArray(), hashes, timeout.Token, configuration);
            if (revision != modelScanRevision || context != ModelContext(Config) || Busy || shuttingDown) return;
            // Refresh existing objects where possible so a completion does not discard UI selection or scroll.
            if (modelLibraryContext == context && Rows.Count == scan.Count
                && Rows.Select(r => r.Status.Model.Id).SequenceEqual(scan.Select(s => s.Model.Id)))
                foreach (var pair in Rows.Zip(scan)) pair.First.UpdateStatus(pair.Second);
            else RebuildRows(scan);
            modelLibraryContext = context;
            RefreshModelDownloadRows();
            DownloadsChanged?.Invoke();
            SetStatus($"已扫描：模型 {scan.Count(s => !s.Missing)}/{scan.Count}。勾选后才下载。");
        }
        catch (OperationCanceledException) when (shuttingDown) { }
        catch (Exception ex)
        {
            AppendLog("模型扫描：" + ex.Message);
            if (!shuttingDown) SetStatus("模型扫描未完成：" + ex.Message);
        }
        finally { CompleteModelInspection(inspection); }
    }

    private TaskCompletionSource TrackModelInspection()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (modelInspectionSync) modelInspections.Add(completion);
        return completion;
    }

    private void CompleteModelInspection(TaskCompletionSource completion)
    {
        lock (modelInspectionSync) modelInspections.Remove(completion);
        completion.TrySetResult();
    }

    private async Task StopModelInspectionsAsync()
    {
        modelInspectionLifetime.Cancel();
        Task[] pending;
        lock (modelInspectionSync) pending = modelInspections.Select(item => item.Task).ToArray();
        // Probe cancellation terminates its Python process tree before it completes.
        await Task.WhenAll(pending);
    }

    public async Task ActOnModelDownloadAsync(ModelRow row)
    {
        if (Busy || preparingModelDownloads || shuttingDown) return;
        try
        {
            RefreshModelDownloadRows();
            if (row.Download is not { } state) return;
            var owners = DownloadTasks.EnsureResourceOwners(state.OwnerId);
            var owner = owners.Single(o => o.Models.Any(m => m.Model.Id == row.Status.Model.Id));
            if (state.CanPause) await DownloadTasks.PauseOwnerAsync(owner.Id);
            else if (state.CanResume || state.CanRetry) await ResumeDownloadOwnerAsync(owner.Id);
        }
        catch (Exception ex) { AppendLog(ex.Message); SetStatus("模型操作未完成：" + ex.Message); }
        finally { RefreshModelDownloadRows(); DownloadsChanged?.Invoke(); }
    }

    private void PublishCompletedModel(LauncherConfig configuration, ModelDownloadIntent intent)
    {
        downloadUi.TryEnqueue(() =>
        {
            if (!SamePath(configuration.ComfyRoot, CurrentModelRoot()) || shuttingDown) return;
            ++modelScanRevision; // An earlier scan must not overwrite a newly verified publication.
            var row = Rows.FirstOrDefault(r => r.Status.Model.Id == intent.Model.Id && SamePath(r.Status.Destination, intent.Destination)
                && r.Status.Model.Revision == intent.Model.Revision && r.Status.Model.Sha256 == intent.Model.Sha256);
            if (row is null) return;
            row.UpdateStatus(new(intent.Model, "已校验", intent.Destination, intent.Destination));
            RefreshModelDownloadRows();
            DownloadsChanged?.Invoke();
        });
    }
}
