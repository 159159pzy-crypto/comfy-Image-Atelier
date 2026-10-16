using System.Security.Cryptography;

namespace Anima.Launcher.Core;

public sealed record InstalledToolFile(string Path, long Size, string Sha256);
public sealed record InstalledTool(string Id, string ArchiveSha256, long ArchiveSize, string Executable, InstalledToolFile[] Files);

/// <summary>A tool is usable only after its entire verified archive has been published.</summary>
public static class ToolInstallation
{
    private const string ReceiptName = ".anima-tool-complete.json";

    public static string DirectoryFor(string runtime, Artifact tool) =>
        Safety.Under(Path.Combine(runtime, "tools"), tool.Id + "/" + tool.Sha256[..12]);

    // Also used by the installation planner. An EXE alone is not completion evidence.
    public static bool IsReady(string runtime, Artifact tool, CancellationToken ct = default)
    {
        try
        {
            var target = DirectoryFor(runtime, tool);
            if (!Directory.Exists(target)) return false;
            RequireRegularTree(target);
            var receipt = JsonFile.Read<InstalledTool>(Path.Combine(target, ReceiptName));
            if (receipt.Id != tool.Id || receipt.ArchiveSha256 != tool.Sha256 || receipt.ArchiveSize != tool.Size
                || receipt.Executable != tool.Executable || receipt.Files.Length == 0) return false;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in receipt.Files)
            {
                ct.ThrowIfCancellationRequested();
                if (!names.Add(file.Path) || file.Path == ReceiptName) return false;
                var path = Safety.Under(target, file.Path);
                if (!File.Exists(path) || new FileInfo(path).Length != file.Size) return false;
                using var input = File.OpenRead(path);
                if (!Convert.ToHexString(SHA256.HashData(input)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return names.Contains(tool.Executable.Replace('\\', '/'))
                && Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Count() == receipt.Files.Length + 1;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    public static async Task<string> InstallVerifiedArchive(string runtime, Artifact tool, string archive, CancellationToken ct)
    {
        if (!await Downloads.Verify(archive, tool.Size, tool.Sha256, ct)) throw new InvalidDataException("工具归档与锁定的大小或 SHA-256 不符。");
        return await Task.Run(() =>
        {
            var target = DirectoryFor(runtime, tool);
            var staging = target + ".extracting";
            var parent = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(parent);
            if (Directory.Exists(staging))
            {
                // This fixed child of the hash-specific tool folder is solely our
                // extraction scratch area. Never follow links while resetting it.
                if (!Path.GetDirectoryName(Path.GetFullPath(staging))!.Equals(Path.GetFullPath(parent), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("工具解压暂存目录超出预期位置。");
                RequireRegularTree(staging);
                Directory.Delete(staging, true);
            }
            ct.ThrowIfCancellationRequested();
            Installer.ExtractZip(archive, staging, ct);
            if (!File.Exists(Safety.Under(staging, tool.Executable))) throw new IOException($"{tool.Id} 压缩包不包含预期程序。");
            RequireRegularTree(staging);
            var files = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).Select(path =>
            {
                ct.ThrowIfCancellationRequested();
                using var input = File.OpenRead(path);
                return new InstalledToolFile(Path.GetRelativePath(staging, path).Replace('\\', '/'), input.Length, Convert.ToHexString(SHA256.HashData(input)));
            }).ToArray();
            JsonFile.Write(Path.Combine(staging, ReceiptName), new InstalledTool(tool.Id, tool.Sha256, tool.Size, tool.Executable, files));
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(target))
            {
                // Preserve old/corrupt installations for diagnosis; a failed or
                // interrupted extraction must never overwrite them in place.
                RequireRegularTree(target);
                MoveDirectory(target, target + ".incomplete-" + Guid.NewGuid().ToString("N"), ct);
            }
            MoveDirectory(staging, target, ct);
            return Safety.Under(target, tool.Executable);
        }, ct);
    }

    private static void MoveDirectory(string source, string destination, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { Directory.Move(source, destination); return; }
            catch (IOException) when (attempt < 5 && Directory.Exists(source) && !Directory.Exists(destination))
            {
                // Antivirus/indexing can briefly retain a newly extracted DLL or
                // directory handle on Windows. Keep the completed staging intact.
                if (ct.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(150 * (attempt + 1)))) ct.ThrowIfCancellationRequested();
            }
        }
    }

    private static void RequireRegularTree(string root)
    {
        var directories = new Stack<string>();
        directories.Push(root);
        while (directories.Count > 0)
        {
            var directory = directories.Pop();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("工具目录包含链接，不能自动修复。");
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("工具文件包含链接，不能自动修复。");
                if ((attributes & FileAttributes.Directory) != 0) directories.Push(path);
            }
        }
    }
}
