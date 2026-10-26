using System.Reflection;
using System.Text.Json;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Anima.Launcher.Core;

public sealed record BundledToolManifest(int SchemaVersion, Artifact[] Tools);

/// <summary>Verified tool archives embedded in the release; never launches or downloads an unverified executable.</summary>
public static class BundledTools
{
    private static readonly Lazy<BundledToolManifest> Manifest = new(() =>
    {
        using var stream = typeof(BundledTools).Assembly.GetManifestResourceStream("Anima.Launcher.Core.download-tools.json")
            ?? throw new InvalidDataException("缺少内置下载工具清单。");
        var manifest = JsonSerializer.Deserialize<BundledToolManifest>(stream, JsonFile.Options)
            ?? throw new InvalidDataException("内置工具清单无效。");
        if (manifest.SchemaVersion != 1) throw new InvalidDataException("不支持的内置工具清单。");
        foreach (var tool in manifest.Tools) Safety.ValidateArtifact(tool);
        return manifest;
    });
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);

    public static Artifact GetArtifact(string toolId) => Manifest.Value.Tools.Single(tool => tool.Id == toolId);
    public static void VerifyEmbedded()
    {
        foreach (var tool in Manifest.Value.Tools)
        {
            if (tool.ExecutableSha256.Length != 64 || tool.License.Length == 0 || tool.SourceArchive is null)
                throw new InvalidDataException("内置工具缺少程序校验或许可来源信息。");
            using var input = typeof(BundledTools).Assembly.GetManifestResourceStream(ResourceName(tool.Id))
                ?? throw new InvalidDataException("发布包缺少内嵌工具：" + tool.Id);
            if (input.Length != tool.Size || !Convert.ToHexString(SHA256.HashData(input)).Equals(tool.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("内嵌工具归档校验失败：" + tool.Id);
            input.Position = 0;
            using var zip = new ZipArchive(input, ZipArchiveMode.Read);
            using var executable = (zip.GetEntry(tool.Executable) ?? throw new InvalidDataException("工具归档缺少程序：" + tool.Id)).Open();
            if (!Convert.ToHexString(SHA256.HashData(executable)).Equals(tool.ExecutableSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("内嵌工具程序校验失败：" + tool.Id);
        }
    }
    public static string ArchiveName(Artifact tool) => tool.Id + "-" + tool.Sha256.ToLowerInvariant() + ".zip";
    public static bool Contains(Artifact artifact) => Manifest.Value.Tools.Any(tool => tool.Id == artifact.Id
        && tool.Size == artifact.Size && tool.Sha256.Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase));

    public static bool IsAvailable(Artifact artifact, string? bundledRoot = null)
    {
        if (!Contains(artifact)) return false;
        return typeof(BundledTools).Assembly.GetManifestResourceNames().Contains(ResourceName(artifact.Id))
            || File.Exists(Path.Combine(bundledRoot ?? DevelopmentRoot(), ArchiveName(artifact)));
    }

    public static async Task<string> EnsureAsync(string toolId, string runtimeRoot, CancellationToken ct = default, string? bundledRoot = null)
    {
        var tool = GetArtifact(toolId);
        var gate = Gates.GetOrAdd(Path.GetFullPath(runtimeRoot) + "/" + tool.Id, _ => new(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (await Task.Run(() => ToolInstallation.IsReady(runtimeRoot, tool, ct), ct))
                return Safety.Under(ToolInstallation.DirectoryFor(runtimeRoot, tool), tool.Executable);
            var archive = await MaterializeArchiveAsync(tool, runtimeRoot, ct, bundledRoot);
            return await ToolInstallation.InstallVerifiedArchive(runtimeRoot, tool, archive, ct);
        }
        finally { gate.Release(); }
    }

    public static async Task<string> MaterializeArchiveAsync(Artifact tool, string runtimeRoot, CancellationToken ct = default, string? bundledRoot = null)
    {
        if (!Contains(tool)) throw new InvalidDataException("请求的工具版本与内置归档不同。");
        var archive = await ArtifactCache.PrepareAsync(runtimeRoot, tool, ct);
        using var targetLease = await DownloadTargetLease.AcquireAsync(archive, ct);
        if (await Downloads.Verify(archive, tool.Size, tool.Sha256, ct)) return archive;
        Safety.FreeSpace(archive, tool.Size);
        var staging = archive + ".bundled-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var source = typeof(BundledTools).Assembly.GetManifestResourceStream(ResourceName(tool.Id))
                ?? OpenDevelopmentArchive(tool, bundledRoot))
            await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
                await source.CopyToAsync(output, ct);
            if (!await Downloads.Verify(staging, tool.Size, tool.Sha256, ct)) throw new InvalidDataException("内置工具归档完整性检查失败，请重新安装启动器。");
            ct.ThrowIfCancellationRequested();
            File.Move(staging, archive, true);
            return archive;
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    private static string ResourceName(string id) => "Anima.Launcher.Core.Tools." + id + ".zip";
    private static string DevelopmentRoot() => Path.Combine(Environment.CurrentDirectory, "tools", "download-tools");
    private static Stream OpenDevelopmentArchive(Artifact tool, string? bundledRoot)
    {
        var path = Path.Combine(bundledRoot ?? DevelopmentRoot(), ArchiveName(tool));
        if (!File.Exists(path)) throw new FileNotFoundException("缺少内置下载工具；源码运行请先执行 launcher/scripts/Prepare-DownloadTools.ps1。", path);
        return File.OpenRead(path);
    }
}
