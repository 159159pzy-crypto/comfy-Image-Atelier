using Anima.Launcher.Core;
using Anima.Launcher;

internal static class ModelDownloadProjectionTests
{
    public static int Run(string work)
    {
        var count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); count++; Console.WriteLine("PASS: " + message); }
        var environment = Path.Combine(work, "projection-environment");
        var otherEnvironment = Path.Combine(work, "projection-other-environment");
        var destination = Path.Combine(environment, "models", "weights.bin");
        var model = new ModelItem("fixture", "fixture", "fixture", "diffusion_models", "weights.bin", [], 100, new string('a', 64),
            ["https://example.com/weights.bin"], "fixture", "");
        DownloadOwnerRecord Owner(string root, DownloadTaskStatus status, DateTimeOffset? created = null) => new()
        {
            Kind = "models", EnvironmentRoot = root, Models = [new(model, destination)], Status = status,
            CreatedAt = created ?? DateTimeOffset.UtcNow, Phase = status.ToString(),
        };
        DownloadTaskRecord File(DownloadOwnerRecord owner, string path, long size, string sha, long received, DownloadTaskStatus status) => new()
        {
            OwnerIds = [owner.Id], Destination = path, Size = size, Sha256 = sha, Received = received,
            Status = status, Speed = 20, Phase = "Downloading",
        };
        var old = Owner(environment, DownloadTaskStatus.Failed, DateTimeOffset.UtcNow.AddDays(-1)); old.Error = "obsolete failure";
        var active = Owner(environment, DownloadTaskStatus.Running);
        var foreign = Owner(otherEnvironment, DownloadTaskStatus.Running, DateTimeOffset.UtcNow.AddHours(1));
        var file = File(active, destination, model.Size, model.Sha256, 40, DownloadTaskStatus.Running);
        var foreignFile = File(foreign, destination, model.Size, model.Sha256, 90, DownloadTaskStatus.Running);
        var wrongFile = File(active, destination, model.Size, new string('b', 64), 99, DownloadTaskStatus.Running);
        var snapshot = ModelDownloadProjection.ForModel(environment + Path.DirectorySeparatorChar, model, destination,
            [old, active, foreign], [file, foreignFile, wrongFile])!;
        Check(snapshot.OwnerId == active.Id && snapshot.Received == 40 && snapshot.Speed == 20 && snapshot.CanPause && snapshot.Error.Length == 0,
            "model row selects its current environment and exact artifact without stale failure or foreign progress");
        Check(ModelDownloadProjection.ForModel(environment, model with { Sha256 = new string('c', 64) }, destination,
            [active], [file]) is null, "changed model content does not inherit progress for a previous file version");
        Check(ModelDownloadProjection.ForModel(environment, model, Path.Combine(environment, "other", "weights.bin"),
            [active], [file]) is null, "identical basenames in a different destination do not match a model row");
        var copy = File(active, destination, model.Size, model.Sha256, 55, DownloadTaskStatus.Running);
        copy.UpdatedAt = file.UpdatedAt.AddSeconds(1);
        Check(ModelDownloadProjection.ForModel(environment, model, destination, [active], [file, copy])!.Received == 55,
            "duplicate historical file records cannot double-count one model artifact");
        var canceled = Owner(environment, DownloadTaskStatus.Canceled, active.CreatedAt.AddMinutes(1));
        active.Status = DownloadTaskStatus.Failed;
        snapshot = ModelDownloadProjection.ForModel(environment, model, destination, [old, active, canceled], [file])!;
        Check(snapshot.OwnerId == canceled.Id && !snapshot.CanPause && !snapshot.CanResume && !snapshot.CanRetry,
            "a newer cancellation hides older failures and never exposes an automatic resume action");
        var completed = Owner(environment, DownloadTaskStatus.Completed, canceled.CreatedAt.AddMinutes(1));
        snapshot = ModelDownloadProjection.ForModel(environment, model, destination, [old, canceled, completed], [file])!;
        Check(snapshot.IsCompleted && snapshot.Received == 100 && snapshot.Percent == 100 && snapshot.Error.Length == 0,
            "latest completed model clears earlier errors even when its files were reused without a new transfer record");

        var package = model with { Id = "package", Size = 150, Sha256 = "", Revision = new string('c', 40),
            Files = [new("config.json", 50, new string('d', 64), ["https://example.com/config.json"]),
                new("weights.bin", 100, new string('e', 64), ["https://example.com/weights.bin"])] };
        var packageDestination = Path.Combine(environment, "models", "package");
        var packageOwner = Owner(environment, DownloadTaskStatus.Running); packageOwner.Models = [new(package, packageDestination)];
        var staging = ModelPackages.Staging(package, packageDestination);
        var files = package.Files.Select(item => File(packageOwner, Path.Combine(staging, item.Path), item.Size, item.Sha256, item.Size, DownloadTaskStatus.Completed)).ToArray();
        snapshot = ModelDownloadProjection.ForModel(environment, package, packageDestination, [packageOwner], files)!;
        Check(snapshot.Received == 150 && snapshot.Total == 150 && snapshot.Percent == 99.9 && !snapshot.IsCompleted && snapshot.CanPause,
            "fully transferred package remains incomplete until coordinator publication succeeds");
        packageOwner.CompletedModelIds.Add(package.Id);
        snapshot = ModelDownloadProjection.ForModel(environment, package, packageDestination, [packageOwner], files)!;
        Check(snapshot.IsCompleted && snapshot.Percent == 100 && !snapshot.CanPause,
            "package completion follows published model identity rather than individual file status");
        packageOwner.CompletedModelIds.Clear(); packageOwner.Status = DownloadTaskStatus.Paused; packageOwner.Error = "paused package";
        files[0].Status = DownloadTaskStatus.Running; // Shared physical file may still be used by another owner.
        snapshot = ModelDownloadProjection.ForModel(environment, package, packageDestination, [packageOwner], files)!;
        Check(snapshot.CanResume && snapshot.Speed == 0 && !snapshot.CanPause && snapshot.Error == "paused package",
            "paused resource does not claim another owner's active shared transfer as its own running work");
        var legacy = Owner(environment, DownloadTaskStatus.Failed, packageOwner.CreatedAt.AddDays(-1));
        legacy.Models = [new(model, destination), new(package, packageDestination)]; legacy.Error = "legacy parent failure";
        legacy.ResourceOwnerIds[package.Id] = packageOwner.Id;
        snapshot = ModelDownloadProjection.ForModel(environment, package, packageDestination, [legacy, packageOwner], files)!;
        Check(snapshot.OwnerId == packageOwner.Id && snapshot.Error == "paused package" && snapshot.TaskIds.Length == 2,
            "legacy parent relationship resolves to one effective package row without repeating parent failure");
        packageOwner.Status = DownloadTaskStatus.NeedsReview;
        snapshot = ModelDownloadProjection.ForModel(environment, package, packageDestination, [packageOwner], files)!;
        Check(snapshot.CanRetry && !snapshot.CanResume && !snapshot.CanPause, "needs-review model exposes retry without presenting a running pause action");

        var missing = new ModelStatus(model, "缺失", null, destination);
        var row = new ModelRow(missing) { Selected = true };
        var rowDownload = new ModelDownloadSnapshot(active.Id, model.Id, DownloadTaskStatus.Running, "Downloading", 40, 100, 20, "", true, false, false, [file.Id]);
        row.UpdateDownload(rowDownload, false);
        row.Selected = true;
        Check(!row.CanSelect && !row.Selected && row.DownloadActionText == "暂停" && row.CanDownloadAction,
            "running model row clears its checked state and cannot be selected for a duplicate download");
        row.UpdateDownload(rowDownload with { Status = DownloadTaskStatus.Paused, CanPause = false, CanResume = true }, false);
        Check(row.DownloadActionText == "继续" && row.CanDownloadAction && row.CanSelect,
            "paused model row exposes continue and remains selectable for an explicit request");
        row.UpdateDownload(rowDownload with { Status = DownloadTaskStatus.Failed, CanPause = false, CanRetry = true }, false);
        Check(row.DownloadActionText == "重试" && row.CanDownloadAction, "failed model row exposes retry rather than a pause or ambiguous continue action");
        row.UpdateDownload(rowDownload with { Status = DownloadTaskStatus.Failed, CanPause = false, CanRetry = true }, true);
        Check(row.HasDownloadAction && !row.CanDownloadAction, "environment maintenance disables model actions without hiding the recovery state");
        row.UpdateDownload(rowDownload with { Status = DownloadTaskStatus.Completed, Received = 100, CanPause = false }, false);
        row.Selected = true;
        Check(row.CanSelect && row.Selected && row.StateText == "缺失",
            "historical completed download does not hide a missing local file or prevent an explicit replacement");
        row.UpdateStatus(missing with { State = "已校验", FoundPath = destination });
        Check(!row.CanSelect && !row.Selected && row.StateText == "已就绪" && !row.ShowTransfer,
            "inventory refresh after completed publication clears selection and shows the model as ready");
        return count;
    }
}
