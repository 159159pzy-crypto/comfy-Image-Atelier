using System.Security.Cryptography;
using System.Text;

namespace Anima.Launcher.Core;

/// <summary>Short, owned scratch paths avoid legacy Windows build tools' 260-character limit.</summary>
public sealed class InstallationTemporaryDirectory : IDisposable
{
    public const int MaximumPathLength = 64;
    private const string RootMarker = "AnimaLauncherTemporaryRootV1";
    private static readonly AsyncLocal<InstallationTemporaryDirectory?> Current = new();
    private readonly InstallationTemporaryDirectory? previous;
    private readonly bool ownsDirectory;
    private readonly string token;
    private readonly FileStream? lease;
    private bool disposed;
    public string Path { get; }
    public string RuntimeRoot { get; }

    private InstallationTemporaryDirectory(string runtimeRoot, string path, bool owns, string token, FileStream? lease = null)
    {
        RuntimeRoot = runtimeRoot; Path = path; ownsDirectory = owns; this.token = token; this.lease = lease;
        previous = Current.Value; Current.Value = this;
    }

    public static string VolumeRoot(string runtimeRoot) => DownloadDiskSpace.VolumeRoot(runtimeRoot);

    public static string CurrentPath(string runtimeRoot)
    {
        var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(runtimeRoot));
        return Current.Value is { disposed: false } scope && scope.RuntimeRoot.Equals(full, StringComparison.OrdinalIgnoreCase)
            ? scope.Path : throw new InvalidOperationException("安装临时目录尚未进入任务作用域。");
    }

    public static string[] CandidateRoots(string runtimeRoot)
    {
        var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(runtimeRoot));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full.ToUpperInvariant())))[..12].ToLowerInvariant();
        var ancestor = full;
        var candidates = new List<string>();
        while (ancestor is not null)
        {
            if (Directory.Exists(ancestor) && !HasReparseAncestor(ancestor))
            {
                var candidate = System.IO.Path.Combine(ancestor, ".anima-temp", hash);
                // Reserve the final task component before choosing a writable ancestor.
                if (candidate.Length + 11 <= MaximumPathLength) candidates.Add(candidate);
            }
            ancestor = System.IO.Path.GetDirectoryName(ancestor);
        }
        return candidates.ToArray();
    }

    /// <param name="allowRoot">Optional caller policy restricting eligible owned directories.</param>
    public static InstallationTemporaryDirectory Enter(string runtimeRoot, Func<string, bool>? allowRoot = null)
    {
        var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(runtimeRoot));
        if (Current.Value is { disposed: false } existing && existing.RuntimeRoot.Equals(full, StringComparison.OrdinalIgnoreCase) && allowRoot is null)
            return new(full, existing.Path, false, "");
        Exception? last = null;
        foreach (var environmentRoot in CandidateRoots(full))
        {
            try
            {
                if (allowRoot is not null && !allowRoot(environmentRoot)) throw new UnauthorizedAccessException("任务目录策略不允许此位置。");
                if (!VolumeRoot(environmentRoot).Equals(VolumeRoot(full), StringComparison.OrdinalIgnoreCase)) continue;
                var group = System.IO.Path.GetDirectoryName(environmentRoot)!;
                EnsureOwnedDirectory(group, RootMarker);
                EnsureOwnedDirectory(environmentRoot, full.ToUpperInvariant());
                CleanAbandoned(environmentRoot);
                var token = Guid.NewGuid().ToString("N");
                var path = System.IO.Path.Combine(environmentRoot, "t-" + token[..8]);
                EnsureOwnedDirectory(path, token);
                var lease = new FileStream(System.IO.Path.Combine(path, ".anima-active"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                return new(full, path, true, token, lease);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { last = ex; }
        }
        throw new IOException("无法在运行工具所在磁盘创建短临时目录。请选择较短且可写的安装路径；启动器不会更改 Windows 长路径设置。", last);
    }

    private static void CleanAbandoned(string environmentRoot)
    {
        var now = DateTime.UtcNow;
        var cutoff = now.AddHours(-24);
        var boot = now - TimeSpan.FromMilliseconds(Environment.TickCount64);
        var drive = System.IO.Path.GetPathRoot(environmentRoot);
        var fixedLocalVolume = OperatingSystem.IsWindows() && drive is { Length: 3 }
            && new DriveInfo(drive).DriveType == DriveType.Fixed;
        foreach (var path in Directory.EnumerateDirectories(environmentRoot, "t-????????", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (HasReparseAncestor(path)) continue;
                var marker = System.IO.Path.Combine(path, ".anima-owner");
                var lockPath = System.IO.Path.Combine(path, ".anima-active");
                if (!File.Exists(marker) || File.GetLastWriteTimeUtc(marker) >= cutoff
                    || (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0) continue;
                var identity = File.ReadAllText(marker);
                if (!Guid.TryParseExact(identity, "N", out _) || System.IO.Path.GetFileName(path) != "t-" + identity[..8]) continue;
                // Live tasks hold their lease until disposal. Older launchers had no
                // lease; reclaim their local scratch only after a Windows reboot.
                if (File.Exists(lockPath))
                {
                    if ((File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0) continue;
                    using var inactive = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.None);
                }
                else if (!fixedLocalVolume || File.GetLastWriteTimeUtc(marker) >= boot.AddMinutes(-5)) continue;
                DeleteTree(path, path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static bool HasReparseAncestor(string path)
    {
        for (var current = path; current is not null; current = System.IO.Path.GetDirectoryName(current))
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        return false;
    }

    private static void EnsureOwnedDirectory(string path, string identity)
    {
        var marker = System.IO.Path.Combine(path, ".anima-owner");
        if (!Directory.Exists(path))
        {
            var staged = path + ".creating-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(staged);
            try
            {
                File.WriteAllText(System.IO.Path.Combine(staged, ".anima-owner"), identity);
                try { Directory.Move(staged, path); }
                catch (IOException) when (Directory.Exists(path)) { }
            }
            finally { if (Directory.Exists(staged)) DeleteTree(staged, staged); }
        }
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || !File.Exists(marker)
            || (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0 || File.ReadAllText(marker) != identity)
            throw new IOException("临时目录已被非本任务的内容占用，已保留：" + path);
        using var writable = new FileStream(System.IO.Path.Combine(path, ".write-" + Guid.NewGuid().ToString("N")),
            FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        writable.WriteByte(0);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (ReferenceEquals(Current.Value, this)) Current.Value = previous;
        if (!ownsDirectory) return;
        lease?.Dispose();
        try
        {
            if (!HasReparseAncestor(Path) && File.ReadAllText(System.IO.Path.Combine(Path, ".anima-owner")) == token)
                DeleteTree(Path, Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Keep only this task's locked scratch for diagnosis. */ }
    }

    internal static void DeleteTree(string root, string directory)
    {
        var full = System.IO.Path.GetFullPath(directory);
        var owned = System.IO.Path.GetFullPath(root);
        if (!full.Equals(owned, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(owned + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("临时目录清理目标超出本任务范围。");
        foreach (var entry in Directory.EnumerateFileSystemEntries(full))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if ((attributes & FileAttributes.ReparsePoint) != 0) Directory.Delete(entry);
                else DeleteTree(owned, entry);
            }
            else
            {
                if ((attributes & FileAttributes.ReparsePoint) == 0) File.SetAttributes(entry, FileAttributes.Normal);
                File.Delete(entry);
            }
        }
        Directory.Delete(full);
    }
}
