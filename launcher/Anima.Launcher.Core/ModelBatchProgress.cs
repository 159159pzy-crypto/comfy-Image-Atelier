using System.Diagnostics;

namespace Anima.Launcher.Core;

public sealed record ModelBatchProgressUpdate(string Name, string Stage, long Received, long Total,
    long FileReceived, long FileTotal, double BytesPerSecond, TimeSpan? RemainingTime, int CompletedItems, int TotalItems)
{
    // Receiving the final byte is followed by SHA-256 validation and package publication.
    public double Percent => TotalItems > 0 && CompletedItems == TotalItems ? 100
        : Total > 0 ? Math.Min(99.9, 100d * Received / Total) : 0;
}

/// <summary>Byte-weighted progress across the immutable model batch, including package and resume bytes.</summary>
public sealed class ModelBatchProgress
{
    private readonly object sync = new();
    private readonly Dictionary<string, ModelItem> models;
    private readonly Dictionary<string, long> received;
    private readonly HashSet<string> completed = new(StringComparer.Ordinal);
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private long baseline;
    private TimeSpan baselineAt;
    public long Total { get; }

    public ModelBatchProgress(IEnumerable<ModelItem> models, IReadOnlyDictionary<string, long>? initialReceived = null)
    {
        this.models = models.DistinctBy(model => model.Id).ToDictionary(model => model.Id, StringComparer.Ordinal);
        received = this.models.ToDictionary(pair => pair.Key,
            pair => Math.Clamp(initialReceived?.GetValueOrDefault(pair.Key) ?? 0, 0, Math.Max(0, pair.Value.Size)), StringComparer.Ordinal);
        Total = this.models.Values.Sum(model => Math.Max(0, model.Size));
        baseline = received.Values.Sum();
    }

    public ModelBatchProgressUpdate Report(string modelId, DownloadProgress progress)
    {
        lock (sync)
        {
            var model = Get(modelId);
            var amount = Math.Clamp(progress.Received, 0, Math.Max(0, model.Size));
            if (completed.Contains(modelId)) amount = Math.Max(0, model.Size);
            var restarted = amount < received[modelId];
            received[modelId] = amount;
            if (restarted) { baseline = received.Values.Sum(); baselineAt = clock.Elapsed; }
            return Snapshot(model, progress.Stage);
        }
    }

    public ModelBatchProgressUpdate Complete(string modelId)
    {
        lock (sync)
        {
            var model = Get(modelId);
            received[modelId] = Math.Max(0, model.Size);
            completed.Add(modelId);
            return Snapshot(model, "已校验并就绪");
        }
    }

    private ModelItem Get(string modelId) => models.TryGetValue(modelId, out var model) ? model
        : throw new ArgumentException("模型不属于当前下载批次。", nameof(modelId));

    private ModelBatchProgressUpdate Snapshot(ModelItem model, string stage)
    {
        var bytes = received.Values.Sum();
        var seconds = (clock.Elapsed - baselineAt).TotalSeconds;
        var speed = seconds >= .1 ? Math.Max(0, bytes - baseline) / seconds : 0;
        var remaining = speed > 0 && bytes < Total ? TimeSpan.FromSeconds((Total - bytes) / speed) : (TimeSpan?)null;
        return new(model.Name, stage, bytes, Total, received[model.Id], Math.Max(0, model.Size), speed, remaining,
            completed.Count, models.Count);
    }
}
