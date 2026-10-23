using Anima.Launcher.Core;

internal static class ModelBatchProgressTests
{
    public static async Task<int> Run()
    {
        var count = 0;
        void Check(bool value, string label)
        { if (!value) throw new Exception("FAIL: " + label); count++; Console.WriteLine("PASS: " + label); }
        ModelItem Item(string id, long size) => new(id, id, "fixture", "diffusion_models", id, [], size, "", [], "", "");
        var small = Item("small", 10);
        var large = Item("large-package", 90);
        var batch = new ModelBatchProgress([small, large, small], new Dictionary<string, long> { [large.Id] = 30 });
        var initial = batch.Report(small.Id, new(small.Name, 0, small.Size, "开始下载"));
        Check(initial.Total == 100 && initial.TotalItems == 2 && initial.Received == 30 && initial.CompletedItems == 0,
            "batch deduplicates identities and starts from actual resume bytes");
        await Task.Delay(120);
        var unchanged = batch.Report(small.Id, new(small.Name, 0, small.Size, "连接中"));
        Check(unchanged.BytesPerSecond == 0 && unchanged.RemainingTime is null,
            "existing partial bytes do not inflate transfer speed or create a false ETA");
        var tinyDone = batch.Complete(small.Id);
        Check(tinyDone.Received == 40 && tinyDone.Percent == 40 && tinyDone.CompletedItems == 1,
            "completion is weighted by bytes rather than giving each model equal weight");
        var verifying = batch.Report(large.Id, new(large.Name, 90, 90, "校验 SHA-256"));
        Check(verifying.Received == 100 && verifying.Percent == 99.9 && verifying.CompletedItems == 1,
            "receiving the last package byte never claims verification and publication are complete");
        var done = batch.Complete(large.Id);
        Check(done.Percent == 100 && done.CompletedItems == done.TotalItems && done.RemainingTime is null,
            "only explicit successful model completion reaches 100 percent");
        Check(batch.Complete(large.Id).Received == 100
            && batch.Report(large.Id, new(large.Name, 20, 90, "late callback")).Received == 100,
            "duplicate completion and late callbacks cannot double-count or undo verified models");
        var retry = new ModelBatchProgress([large], new Dictionary<string, long> { [large.Id] = 80 });
        var restarted = retry.Report(large.Id, new(large.Name, 0, 90, "断点与来源不符，重新下载"));
        Check(restarted.Received == 0 && restarted.BytesPerSecond == 0 && restarted.RemainingTime is null,
            "range restart resets actual bytes and speed without retaining invalid resume progress");
        var clamped = retry.Report(large.Id, new(large.Name, 999, 999, "callback"));
        Check(clamped.Received == 90 && clamped.Total == 90 && clamped.Percent == 99.9,
            "progress callbacks cannot change the reviewed batch total or exceed its size");
        var rejected = false;
        try { retry.Complete("not-selected"); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "progress from an unselected model is rejected");
        var empty = new ModelBatchProgress([]);
        Check(empty.Total == 0, "empty batch has no fabricated work");
        return count;
    }
}
