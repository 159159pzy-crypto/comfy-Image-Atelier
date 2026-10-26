namespace Anima.Launcher.Core;

/// <summary>A sidecar handle protects one destination across instances and processes. A crashed process releases it.</summary>
internal sealed class DownloadTargetLease(FileStream stream) : IDisposable
{
    public static async Task<DownloadTargetLease> AcquireAsync(string destination, CancellationToken ct)
    {
        destination = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var path = destination + ".download.lock";
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("下载锁文件不能是链接。");
            try { return new(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)); }
            catch (IOException error) when (File.Exists(path) && IsSharingViolation(error)) { await Task.Delay(100, ct); }
        }
    }
    public void Dispose() => stream.Dispose();
    private static bool IsSharingViolation(IOException error)
    {
        var code = error.HResult & 0xffff;
        return code is 32 or 33 || !OperatingSystem.IsWindows() && code == 11;
    }
}
