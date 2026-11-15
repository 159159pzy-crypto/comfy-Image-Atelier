using System.Text.RegularExpressions;

namespace Anima.Launcher.Core;

public sealed record KnownDirectRequirement(string Pattern, string Replacement);

/// <summary>Only explicitly managed upstream declarations may be normalized to our fixed source.</summary>
public static class KnownDependencyRequirements
{
    private const string Sam2Pattern = @"(?:sam[-_.]?2(?:\[[^\]]*\])?\s*@\s*)?git\+https://github\.com/facebookresearch/sam2(?:\.git)?(?:@[\w./\-]+)?\s*";

    public static KnownDirectRequirement Sam2(string commit)
    {
        if (!Regex.IsMatch(commit, "^[a-fA-F0-9]{40}$"))
            throw new InvalidDataException("SAM2 固定提交无效，无法检查依赖来源。");
        return new(Sam2Pattern, "SAM-2 @ git+https://github.com/facebookresearch/sam2@" + commit);
    }

    public static string? PinSam2(string content, string commit)
    {
        var rule = Sam2(commit);
        var found = false;
        var lines = content.Replace("\r\n", "\n").Split('\n').Select(line =>
        {
            // Match the probe's requirement parsing: whitespace followed by # starts
            // a comment, while URL fragments stay part of the source identity.
            var comment = Regex.Match(line, @"\s+#.*$");
            var requirement = comment.Success ? line[..comment.Index] : line;
            if (!Regex.IsMatch(requirement.Trim(), "^" + rule.Pattern + "$", RegexOptions.IgnoreCase)) return line;
            found = true;
            return rule.Replacement + (comment.Success ? comment.Value : "");
        }).ToArray();
        return found ? string.Join("\n", lines) : null;
    }
}
