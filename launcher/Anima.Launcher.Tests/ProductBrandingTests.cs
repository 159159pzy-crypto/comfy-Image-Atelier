using Anima.Launcher.Core;

internal static class ProductBrandingTests
{
    public static int Run(string work)
    {
        var passed = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL: " + label); passed++; Console.WriteLine("PASS: " + label); }
        var installed = Path.Combine(work, "installed");
        var entry = Path.Combine(installed, "AnimaRandomStudio.exe");
        var other = Path.Combine(work, "unrelated", "AnimaRandomStudio.exe");
        Check(ProductBranding.OwnsInstallation(ProductBranding.LegacyDisplayName, installed, [entry]),
            "legacy install display name retains update registration ownership");
        Check(ProductBranding.OwnsInstallation(ProductBranding.DisplayName, installed, [entry]),
            "renamed install display name retains update registration ownership");
        Check(!ProductBranding.OwnsInstallation("Another program", installed, [entry]),
            "matching executable path does not authorize an unrelated installation name");
        Check(!ProductBranding.OwnsInstallation(ProductBranding.DisplayName, installed, [other]),
            "matching display name does not authorize another installation directory");
        Check(!ProductBranding.MatchesEntry("AnimaRandomStudio.exe", [entry])
            && !ProductBranding.MatchesEntry(other, [entry]),
            "shortcut migration rejects relative or unrelated targets even with the same executable name");
        Check(ProductBranding.MatchesEntry(Path.Combine(installed, ".", "AnimaRandomStudio.exe"), [entry]),
            "shortcut migration recognizes the exact normalized owned executable");
        return passed;
    }
}
