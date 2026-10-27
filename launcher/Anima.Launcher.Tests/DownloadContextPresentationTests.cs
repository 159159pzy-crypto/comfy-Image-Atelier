using System.Text.Json;
using Anima.Launcher.Core;

internal static class DownloadContextPresentationTests
{
    public static int Run(string work)
    {
        var count = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception("FAIL " + message);
            Console.WriteLine("PASS " + message);
            count++;
        }
        var environment = Path.Combine(work, "context-environment");
        var old = new DownloadOwnerRecord { Kind = "install", EnvironmentRoot = environment, Status = DownloadTaskStatus.Failed, UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1) };
        var completed = new DownloadOwnerRecord { Kind = "setup", EnvironmentRoot = environment, Status = DownloadTaskStatus.Completed };
        var unrelated = new DownloadOwnerRecord { Kind = "install", EnvironmentRoot = environment + "-other", Status = DownloadTaskStatus.Running };
        var snapshot = new DownloadTaskDatabase { Owners = [old, completed, unrelated] };
        Check(DownloadContextPresentation.InstallationOwner(snapshot, environment)?.Id == completed.Id,
            "installation page uses the current environment's latest outcome instead of an old failed attempt");
        old.Status = DownloadTaskStatus.Running;
        Check(DownloadContextPresentation.InstallationOwner(snapshot, environment + Path.DirectorySeparatorChar)?.Id == old.Id,
            "active current installation takes priority and trailing directory separators do not hide it");
        Check(DownloadContextPresentation.InstallationOwner(snapshot, "") is null,
            "an unconfigured environment cannot display another environment's installation");
        var file = new DownloadTaskRecord { OwnerIds = [old.Id], Size = 100, Received = 100, Status = DownloadTaskStatus.Completed };
        snapshot.Tasks.Add(file);
        var installing = DownloadContextPresentation.Progress(snapshot, old);
        Check(installing.Indeterminate && installing.Percent < 100,
            "downloaded files keep the installation indeterminate until its coordinator completes");
        old.Status = DownloadTaskStatus.Completed;
        Check(DownloadContextPresentation.Progress(snapshot, old).Percent == 100
            && !DownloadContextPresentation.Progress(snapshot, old).Indeterminate,
            "installation completion is driven by the owner rather than transferred bytes");
        old.Status = DownloadTaskStatus.Paused;
        file.Status = DownloadTaskStatus.Paused; file.Received = 30; file.Speed = 999;
        var paused = DownloadContextPresentation.Progress(snapshot, old);
        Check(paused.Percent == 30 && paused.Speed == 0 && !paused.Indeterminate,
            "paused progress retains byte counts and suppresses stale speed");

        DownloadOwnerRecord Software(string version, DownloadTaskStatus status = DownloadTaskStatus.Paused)
        {
            var url = "https://github.com/" + LauncherUpdates.Repository + "/releases/download/" + version + "/";
            var release = new LauncherRelease(version, version, "fixture", DateTimeOffset.UtcNow,
                url + "AnimaRandomStudio.exe", 100, new string('c', 64), url + "build-info.json",
                JsonSerializer.SerializeToElement(new { version, runtimeIdentifier = "win-x64", packageFormat = "single-exe-v1" }));
            return new() { Kind = "software", Status = status, Payload = JsonSerializer.SerializeToElement(release, JsonFile.Options) };
        }
        var oldest = Software("1.9.0"); oldest.UpdatedAt = DateTimeOffset.UtcNow.AddDays(1);
        var latest = Software("1.10.0", DownloadTaskStatus.Failed);
        var canceledFileOwner = Software("2.0.0");
        var malformed = Software("3.0.0"); malformed.Payload = JsonSerializer.SerializeToElement(new { version = "3.0.0" });
        var software = new DownloadTaskDatabase { Owners = [oldest, latest, canceledFileOwner, malformed, Software("4.0.0", DownloadTaskStatus.Completed)],
            Tasks = [new() { OwnerIds = [canceledFileOwner.Id], Status = DownloadTaskStatus.Canceled }] };
        Check(DownloadContextPresentation.RecoverableSoftwareOwner(software, "1.3.0")?.Id == latest.Id,
            "settings recovery selects the highest valid version and excludes canceled child transfers");
        Check(DownloadContextPresentation.RecoverableSoftwareOwner(software, "1.10.0") is null,
            "settings recovery excludes obsolete versions, malformed releases, and completed candidates");
        var newerAvailable = DownloadContextPresentation.SoftwareRelease(Software("1.11.0"));
        Check(DownloadContextPresentation.RecoverableSoftwareOwner(software, "1.3.0", newerAvailable) is null,
            "an older paused download cannot replace the newer release found by an update check");
        var revisedAvailable = DownloadContextPresentation.SoftwareRelease(latest)! with { Sha256 = new string('d', 64) };
        Check(DownloadContextPresentation.RecoverableSoftwareOwner(software, "1.3.0", revisedAvailable) is null,
            "a paused artifact cannot replace a newly checked release with a different hash");
        Check(DownloadContextPresentation.RecoverableSoftwareOwner(software, "1.3.0", DownloadContextPresentation.SoftwareRelease(oldest))?.Id == latest.Id,
            "a stale release cache still allows recovery of a newer validated saved download");
        Check(DownloadContextPresentation.SoftwareRelease(latest)?.Version == "1.10.0"
            && DownloadContextPresentation.SoftwareRelease(malformed) is null,
            "software resume uses the original validated release payload");
        Check(latest.Status == DownloadTaskStatus.Failed && canceledFileOwner.Status == DownloadTaskStatus.Paused,
            "software presentation never mutates paused or canceled download records");
        return count;
    }
}
