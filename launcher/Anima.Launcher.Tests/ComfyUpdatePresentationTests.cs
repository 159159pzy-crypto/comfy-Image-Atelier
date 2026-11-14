using Anima.Launcher.Core;

internal static class ComfyUpdatePresentationTests
{
    public static int Run()
    {
        var count = 0;
        void Check(bool condition, string label) { if (!condition) throw new Exception("FAIL: " + label); count++; Console.WriteLine("PASS: " + label); }
        var record = new ComfyUpdateBackup("root", "python", new string('a', 40), new string('b', 40), "recovery", [], [], "complete", DateTimeOffset.UtcNow)
            { FormatVersion = 1, RecoveryStrategy = "light", CoreVerified = true };
        var empty = ComfyUpdatePresentation.From(null);
        Check(!empty.RequiresRecovery && !empty.CanRestore && !empty.CoreVerified, "a fresh environment has no invented update success");
        var complete = ComfyUpdatePresentation.From(record);
        Check(complete.CanRestore && complete.CoreVerified && !complete.RequiresRecovery && !complete.Message.Contains("完整环境"), "light success offers restore and accurately names core verification");
        var repair = ComfyUpdatePresentation.From(record with { Status = "needs-repair" });
        Check(repair.RequiresRecovery && repair.NeedsDependencyRepair && !repair.CanRestore && !repair.CoreVerified, "restored source with incompatible dependencies requires explicit repair");
        foreach (var status in new[] { "updating", "restoring", "rollback-failed" })
        {
            var pending = ComfyUpdatePresentation.From(record with { Status = status });
            Check(pending.RequiresRecovery && pending.CanRestore && !pending.CoreVerified, "restart retains recovery action for " + status);
        }
        var sourceOnly = ComfyUpdatePresentation.From(record with { Status = "rolled-back", CoreVerified = false });
        Check(!sourceOnly.RequiresRecovery && !sourceOnly.CoreVerified && sourceOnly.Message.Contains("尚未"), "source-only recovery does not claim a core runtime probe");
        var legacy = ComfyUpdatePresentation.From(record with { FormatVersion = 0, RecoveryStrategy = "full", CoreVerified = false });
        Check(legacy.CanRestore && legacy.Message.Contains("完整备份"), "historical full backup remains distinguishable");
        var unknown = ComfyUpdatePresentation.From(record with { FormatVersion = 2 });
        Check(unknown.RequiresRecovery && !unknown.CanRestore && !unknown.CoreVerified, "unrecognized recovery format never enables an update or startup success");
        Check(ComfyUpdatePresentation.From(record with { Status = "unknown" }).RequiresRecovery, "unrecognized durable state cannot silently disappear");
        return count;
    }
}
