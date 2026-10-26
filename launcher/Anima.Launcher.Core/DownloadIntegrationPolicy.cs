using System.Text.Json;

namespace Anima.Launcher.Core;

/// <summary>Separates current UI preferences from an operation's frozen transport and release identity.</summary>
public static class DownloadIntegrationPolicy
{
    public static LauncherConfig WithDownloadSettings(LauncherConfig runtime, LauncherConfig settings)
    {
        var result = runtime.Clone();
        result.SourceId = settings.SourceId;
        result.GitMirror = settings.GitMirror;
        result.ModelMirror = settings.ModelMirror;
        result.PipIndex = settings.PipIndex;
        result.NetworkMode = settings.NetworkMode;
        result.ProxyAddress = settings.ProxyAddress;
        return result;
    }

    public static RuntimeManifest ModelRuntime(InstallationPlan? plan, RuntimeManifest current) => plan?.ResolvedRuntime ?? current;

    public static bool IsNewerSoftwareVersion(string candidate, string current) =>
        Version.TryParse(candidate.TrimStart('v'), out var next) && Version.TryParse(current.TrimStart('v'), out var installed) && next > installed;

    // Continue-all resumes at most one software candidate. Older/invalid records remain available for explicit review.
    public static DownloadOwnerRecord? LatestSoftwareOwner(IEnumerable<DownloadOwnerRecord> owners, string currentVersion)
    {
        return owners.Where(o => o.Kind == "software" && !o.CancellationRequested
                && o.Status is DownloadTaskStatus.Paused or DownloadTaskStatus.Failed or DownloadTaskStatus.NeedsReview)
            .Select(o => (Owner: o, Release: ReadRelease(o)))
            .Where(item => item.Release is not null && IsNewerSoftwareVersion(item.Release.Version, currentVersion))
            .OrderByDescending(item => Version.Parse(item.Release!.Version.TrimStart('v')))
            .ThenByDescending(item => item.Release!.PublishedAt)
            .ThenByDescending(item => item.Owner.UpdatedAt)
            .Select(item => item.Owner).FirstOrDefault();
    }

    private static LauncherRelease? ReadRelease(DownloadOwnerRecord owner)
    {
        try
        {
            var release = owner.Payload?.Deserialize<LauncherRelease>(JsonFile.Options);
            if (release is null) return null;
            LauncherUpdates.ValidateRelease(release);
            return release;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException or InvalidOperationException or NullReferenceException or KeyNotFoundException)
        { return null; }
    }
}

/// <summary>One reservation covers owner startup, transport, validation, and shared ready-state publication.</summary>
public sealed class SoftwareDownloadGate
{
    private int occupied;
    public bool IsOccupied => Volatile.Read(ref occupied) != 0;
    public IDisposable? TryEnter() => Interlocked.CompareExchange(ref occupied, 1, 0) == 0 ? new Reservation(this) : null;

    private sealed class Reservation(SoftwareDownloadGate gate) : IDisposable
    {
        private SoftwareDownloadGate? owner = gate;
        public void Dispose()
        {
            var released = Interlocked.Exchange(ref owner, null);
            if (released is not null) Volatile.Write(ref released.occupied, 0);
        }
    }
}
