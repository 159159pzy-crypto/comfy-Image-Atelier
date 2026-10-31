using Anima.Launcher.Core;

namespace Anima.Launcher;

internal sealed partial class LauncherState
{
    private ModelBatchProgress? modelBatch;
    public ModelBatchProgressUpdate? BatchProgress { get; private set; }
    public event Action? BatchProgressChanged;

    private void PublishBatch(ModelBatchProgressUpdate update)
    {
        BatchProgress = update;
        SetupDownloadPercent = (int)update.Percent;
        PublishPercent((int)update.Percent);
        ProgressText = $"整批 {update.CompletedItems}/{update.TotalItems} 项 · {update.Received / 1048576d:N1} / {update.Total / 1048576d:N1} MiB"
            + (update.BytesPerSecond > 0 ? $" · {update.BytesPerSecond / 1048576d:N1} MiB/s" : "")
            + (update.RemainingTime is { } remaining ? $" · 剩余 {remaining:hh\\:mm\\:ss}" : "");
        RequestPresentation(DisplayChange.ProgressText);
        RequestPresentation(DisplayChange.Batch);
    }

    private void ClearBatchProgress()
    {
        modelBatch = null;
        BatchProgress = null;
        RequestPresentation(DisplayChange.Batch);
    }

    private static long ModelRemaining(ModelStatus item) => item.Model.IsPackage
        ? ModelPackages.RemainingBytes(item.Model, item.Destination)
        : Downloads.RemainingBytes(item.Destination, item.Model.Size, item.Model.Sha256);

    private static long ModelAllocationRemaining(ModelStatus item) => item.Model.IsPackage
        ? ModelPackages.RemainingAllocationBytes(item.Model, item.Destination)
        : File.Exists(item.Destination) ? 0 : DownloadDiskSpace.RemainingAllocationBytes(item.Destination, item.Model.Size, item.Model.Sha256);

    private static async Task CheckModelSpaceAsync(IEnumerable<ModelStatus> models, CancellationToken ct)
    {
        foreach (var volume in models.GroupBy(item => DownloadDiskSpace.VolumeRoot(item.Destination)))
        {
            var required = volume.Sum(ModelAllocationRemaining);
            var available = await DownloadDiskSpace.AvailableForPlanningAsync(volume.Key, ct);
            if (available < DownloadDiskSpace.SafetyMargin || required > available - DownloadDiskSpace.SafetyMargin)
                throw new InsufficientSpaceException(volume.Key, required);
        }
    }

    private static void ValidateDownload(ModelStatus item)
    {
        if (string.IsNullOrWhiteSpace(item.Destination)) throw new IOException($"{item.Model.Name} 的实际模型目录待确认，请检查模型路径后重新扫描。");
        if (!item.Model.CanDownload) throw new InvalidOperationException($"{item.Model.Name} 暂无可验证下载，请从资源页的来源手动安装。");
        if (item.FoundPath is not null && !item.Model.IsPackage) throw new IOException($"已有文件校验异常，已保留原文件：{item.FoundPath}。请备份处理后重试。");
    }

    private async Task DownloadModelsAsync(List<ModelStatus> selected, CancellationToken ct, ISet<string>? optionalIds = null)
    {
        var queue = new List<ModelStatus>();
        foreach (var item in selected)
        {
            try { ValidateDownload(item); queue.Add(item); }
            catch (Exception ex) when (optionalIds?.Contains(item.Model.Id) == true)
            { SetupOptionalFailures.Add(item.Model.Name + "：" + ex.Message); setupFailedOptionalModelIds.Add(item.Model.Id); }
        }
        await CheckModelSpaceAsync(queue.Where(item => optionalIds?.Contains(item.Model.Id) != true), ct);
        downloadQueue = queue;
        cancelDownloads = false;
        var batch = new ModelBatchProgress(queue.Select(item => item.Model), queue.ToDictionary(
            item => item.Model.Id, item => Math.Max(0, item.Model.Size - ModelRemaining(item))));
        modelBatch = batch;
        var singleProgress = DownloadProgress();
        DownloadCancelable?.Invoke(true);
        try
        {
            await Task.WhenAll(queue.OrderBy(item => optionalIds?.Contains(item.Model.Id) == true).Select(async item =>
            {
                using var priorityScope = activeDownloadOwner is null ? null : DownloadTasks.WithinOwner(activeDownloadOwner, optionalIds?.Contains(item.Model.Id) != true);
                ct.ThrowIfCancellationRequested();
                var progress = new Progress<DownloadProgress>(value =>
                {
                    if (!ReferenceEquals(modelBatch, batch)) return;
                    if (queue.Count == 1) singleProgress.Report(value);
                    PublishBatch(batch.Report(item.Model.Id, value));
                });
                PublishBatch(batch.Report(item.Model.Id, new(item.Model.Name,
                    Math.Max(0, item.Model.Size - ModelRemaining(item)), item.Model.Size, "准备下载")));
                try
                {
                    if (optionalIds?.Contains(item.Model.Id) == true)
                        await CheckModelSpaceAsync([item], ct);
                    await FetchModelAsync(item, progress, ct);
                    PublishBatch(batch.Complete(item.Model.Id));
                }
                catch (Exception ex) when (ex is not OperationCanceledException && optionalIds?.Contains(item.Model.Id) == true)
                {
                    SetupOptionalFailures.Add(item.Model.Name + "：" + ex.Message);
                    setupFailedOptionalModelIds.Add(item.Model.Id);
                    AppendLog("可选模型下载失败：" + item.Model.Name + "：" + ex.Message);
                }
            }));
        }
        finally
        {
            // Invalidate posted callbacks before a later operation replaces the batch.
            modelBatch = null;
            DownloadCancelable?.Invoke(false);
        }
    }

    private async Task FetchModelAsync(ModelStatus item, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        var configuration = ModelDownloadConfiguration();
        if (item.Model.IsPackage)
            await ModelPackages.Fetch(Downloads, item.Model, item.Destination, configuration, progress, ct);
        else
            await Downloads.Fetch(item.Model.Name, DownloadSources.ModelCandidates(item.Model, configuration), item.Destination,
                item.Model.Size, item.Model.Sha256, progress, ct);
        if (item.Model.Category == "diffusion_models")
            await ModelMetadata.EnsureDefaultAsync(configuration, OperationRuntime, OperationModels, OperationProfiles,
                item.Destination, ct, AppendLog);
    }
}
