using System.Text.Json;
using System.Text.RegularExpressions;

namespace Anima.Launcher.Core;

public enum WorkbenchActivity { Unknown, Idle, Busy }

/// <summary>Pure decisions shared by the launcher UI and its regression tests.</summary>
public static class RuntimePolicies
{
    public static bool SynchronizeManagedSetupVersion(LauncherConfig draft, LauncherConfig current, string versionsRoot)
    {
        if (string.IsNullOrWhiteSpace(current.AppRoot)) return false;
        try
        {
            var versions = Path.GetFullPath(versionsRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!string.IsNullOrWhiteSpace(draft.AppRoot)
                && !Path.GetFullPath(draft.AppRoot).StartsWith(versions, StringComparison.OrdinalIgnoreCase)) return false;
            var changed = draft.AppRoot != current.AppRoot || draft.PinnedAppRoot != current.PinnedAppRoot;
            draft.AppRoot = current.AppRoot;
            draft.PinnedAppRoot = current.PinnedAppRoot;
            return changed;
        }
        catch { return false; }
    }

    public static bool CanApplyBundledSoftwareUpdate(LauncherConfig previous, string versionsRoot, bool workbenchWasRunning)
    {
        if (string.IsNullOrWhiteSpace(previous.AppRoot)) return !previous.SetupComplete && !workbenchWasRunning;
        try
        {
            var versions = Path.GetFullPath(versionsRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(previous.AppRoot).StartsWith(versions, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static LauncherConfig SnapshotConfig(LauncherConfig config)
    {
        var snapshot = config.Clone();
        snapshot.Validate();
        return snapshot;
    }

    public static WorkbenchActivity WorkbenchActivity(JsonElement? snapshot)
    {
        const global::Anima.Launcher.Core.WorkbenchActivity unknown = global::Anima.Launcher.Core.WorkbenchActivity.Unknown;
        const global::Anima.Launcher.Core.WorkbenchActivity busy = global::Anima.Launcher.Core.WorkbenchActivity.Busy;
        if (snapshot is not { ValueKind: JsonValueKind.Object } value) return unknown;
        if (!value.TryGetProperty("queue", out var queue) || queue.ValueKind != JsonValueKind.Array) return unknown;
        if (queue.GetArrayLength() > 0) return busy;
        if (!value.TryGetProperty("batch", out var batch)) return unknown;
        if (batch.ValueKind == JsonValueKind.Null) return global::Anima.Launcher.Core.WorkbenchActivity.Idle;
        if (batch.ValueKind != JsonValueKind.Object || !batch.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String) return unknown;
        return status.GetString() switch
        {
            "running" or "stopping" => busy,
            "completed" or "stopped" or "error" or "cancelled" => global::Anima.Launcher.Core.WorkbenchActivity.Idle,
            _ => unknown,
        };
    }

    public static bool ShouldSwitchBundledVersion(string currentRoot, string bundledRoot, string versionsRoot, bool setupComplete, bool keepCurrentVersion)
    {
        if (!setupComplete || keepCurrentVersion || string.IsNullOrWhiteSpace(currentRoot) || string.IsNullOrWhiteSpace(bundledRoot)) return false;
        try
        {
            var current = Path.GetFullPath(currentRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var bundled = Path.GetFullPath(bundledRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var versions = Path.GetFullPath(versionsRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return current.StartsWith(versions, StringComparison.OrdinalIgnoreCase)
                && bundled.StartsWith(versions, StringComparison.OrdinalIgnoreCase)
                && !current.Equals(bundled, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static bool CommandLineMatchesSnapshot(string? commandLine, string appDir)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || string.IsNullOrWhiteSpace(appDir)) return false;
        try
        {
            var script = Path.Combine(Path.GetFullPath(appDir), "run.py").Replace('/', '\\');
            // Match the script argument, not a directory mentioned in an unrelated
            // option or a similarly named sibling. Launcher starts use absolute argv.
            return Regex.IsMatch(commandLine.Replace('/', '\\'), "(?:^|[\\s\"])" + Regex.Escape(script) + "(?=$|[\\s\"])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        catch { return false; }
    }
}
