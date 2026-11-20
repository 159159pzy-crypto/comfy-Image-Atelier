using System.IO.Compression;
using System.Security.Cryptography;

namespace Anima.Launcher.Core;

public static class BundledResourceRepair
{
    // Callers hold the launcher extraction lease. Only members of its embedded package may be restored.
    public static void EnsureFile(string target, string expectedHash, Func<Stream> open,
        string backupDirectory, Action<string> log)
    {
        LauncherUpdateStore.RequireNoLinks(target);
        if (new FileInfo(target).LinkTarget is not null)
            throw new IOException("内嵌资源路径为文件链接，已保留原位置：" + target);
        var exists = File.Exists(target);
        if (exists)
        {
            using var current = File.OpenRead(target);
            if (Convert.ToHexString(SHA256.HashData(current)) == expectedHash) return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + ".repair-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var source = open())
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                source.CopyTo(output);
            using (var written = File.OpenRead(temporary))
                if (Convert.ToHexString(SHA256.HashData(written)) != expectedHash) throw new IOException("内嵌资源写入校验失败：" + target);
            if (!exists) File.Move(temporary, target);
            else
            {
                LauncherUpdateStore.RequireNoLinks(backupDirectory);
                Directory.CreateDirectory(backupDirectory);
                var backup = Path.Combine(backupDirectory, Guid.NewGuid().ToString("N") + "-" + Path.GetFileName(target));
                File.Replace(temporary, target, backup);
                log("已恢复内嵌资源：" + target + "；原文件备份：" + backup);
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void EnsureWorkbench(string archive, string destination, string backupDirectory, Action<string> log)
    {
        using var zip = ZipFile.OpenRead(archive);
        foreach (var entry in zip.Entries.Where(item => item.Name.Length > 0))
        {
            var target = Safety.Under(destination, entry.FullName);
            using var source = entry.Open();
            EnsureFile(target, Convert.ToHexString(SHA256.HashData(source)), entry.Open, backupDirectory, log);
        }
    }
}
