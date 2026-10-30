namespace Anima.Launcher.Core;

/// <summary>Immutable package identity from the reviewed resolver result; mirrors only change transport.</summary>
public sealed record DependencyPackage
{
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string Kind { get; init; } = "wheel";
    public string FileName { get; init; } = "";
    public string Url { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public long Size { get; init; }
    public string[] Sources { get; init; } = [];
    public string Commit { get; init; } = "";
    public string SourceDirectory { get; init; } = "";
    public bool Direct { get; init; }
    public string Subdirectory { get; init; } = "";
}
