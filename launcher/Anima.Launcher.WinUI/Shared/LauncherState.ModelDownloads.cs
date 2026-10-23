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
        BatchProgressChanged?.Invoke();
    }

    private void ClearBatchProgress()
    {
        modelBatch = null;
        BatchProgress = null;
        BatchProgressChanged?.Invoke();
    }

    private static long ModelRemaining(ModelStatus item) => item.Model.IsPackage
        ? ModelPackages.RemainingBytes(item.Model, item.Destination)
        : Downloads.RemainingBytes(item.Destination, item.Model.Size, item.Model.Sha256);

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
            { SetupOptionalFailures.Add(item.Model.Name + "：" + ex.Message); }
        }
        foreach (var volume in queue.Where(item => optionalIds?.Contains(item.Model.Id) != true).GroupBy(s => Path.GetPathRoot(s.Destination)))
            Safety.FreeSpace(volume.Key!, volume.Sum(ModelRemaining));
        downloadQueue = queue;
        cancelDownloads = false;
        var batch = new ModelBatchProgress(queue.Select(item => item.Model), queue.ToDictionary(
            item => item.Model.Id, item => Math.Max(0, item.Model.Size - ModelRemaining(item))));
        modelBatch = batch;
        var singleProgress = DownloadProgress();
        var requiredRemaining = queue.Where(item => optionalIds?.Contains(item.Model.Id) != true)
            .Select(item => item.Model.Id).ToHashSet();
        DownloadCancelable?.Invoke(true);
        try
        {
            foreach (var item in queue)
            {
                ct.ThrowIfCancellationRequested();
                var progress = new Progress<DownloadProgress>(value =>
                {
                    if (!ReferenceEquals(modelBatch, batch)) return;
                    singleProgress.Report(value);
                    PublishBatch(batch.Report(item.Model.Id, value));
                });
                PublishBatch(batch.Report(item.Model.Id, new(item.Model.Name,
                    Math.Max(0, item.Model.Size - ModelRemaining(item)), item.Model.Size, "准备下载")));
                try
                {
                    if (optionalIds?.Contains(item.Model.Id) == true)
                        Safety.FreeSpace(item.Destination, ModelRemaining(item));
                    await FetchModelAsync(item, progress, ct);
                    PublishBatch(batch.Complete(item.Model.Id));
                    requiredRemaining.Remove(item.Model.Id);
                    if (optionalIds is not null && requiredRemaining.Count == 0) SetupRequiredModelsReady = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException && optionalIds?.Contains(item.Model.Id) == true)
                {
                    SetupOptionalFailures.Add(item.Model.Name + "：" + ex.Message);
                    AppendLog("可选模型下载失败：" + item.Model.Name + "：" + ex.Message);
                }
            }
        }
        finally
        {
            // Invalidate posted callbacks before a later operation replaces the batch.
            modelBatch = null;
            if (cancelDownloads) foreach (var item in queue.Where(item => !item.Model.IsPackage)) Downloads.CancelPartial(item.Destination);
            DownloadCancelable?.Invoke(false);
        }
    }

    private async Task FetchModelAsync(ModelStatus item, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        if (item.Model.IsPackage)
            await ModelPackages.Fetch(Downloads, item.Model, item.Destination, Config, progress, ct);
        else
        {
            Exception? failure = null;
            var done = false;
            foreach (var source in item.Model.Urls)
            {
                try
                {
                    await Downloads.Fetch(item.Model.Name, Safety.MapSource(source, Config), item.Destination,
                        item.Model.Size, item.Model.Sha256, progress, ct);
                    done = true;
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException && item.Model.Urls.Length > 1)
                { failure = ex; AppendLog(item.Model.Name + "：当前来源失败，尝试下一来源。"); }
            }
            if (!done) throw failure ?? new IOException(item.Model.Name + " 没有可用下载来源。");
        }
        if (item.Model.Category == "diffusion_models")
            await ModelMetadata.EnsureDefaultAsync(Config, Program.Runtime, Program.Models, Program.ModelProfiles,
                item.Destination, ct, AppendLog);
    }
}
