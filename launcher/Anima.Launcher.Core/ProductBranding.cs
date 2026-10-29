namespace Anima.Launcher.Core;

/// <summary>Display branding is separate from the persisted installation and update identities.</summary>
public static class ProductBranding
{
    public const string DisplayName = "Comfy Image Atelier";
    public const string LegacyDisplayName = "Anima Random Studio";

    public static bool IsKnownDisplayName(string? value) => value is DisplayName or LegacyDisplayName;

    public static bool MatchesEntry(string? target, IEnumerable<string> knownEntries)
    {
        if (string.IsNullOrWhiteSpace(target) || !Path.IsPathFullyQualified(target)) return false;
        try
        {
            return knownEntries.Any(entry => !string.IsNullOrWhiteSpace(entry) && Path.IsPathFullyQualified(entry) && LauncherUpdateStore.SamePath(target, entry));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public static bool OwnsInstallation(string? displayName, string? location, IEnumerable<string> knownEntries)
        => IsKnownDisplayName(displayName) && !string.IsNullOrWhiteSpace(location) && Path.IsPathFullyQualified(location)
            && MatchesEntry(Path.Combine(location, "AnimaRandomStudio.exe"), knownEntries);
}
