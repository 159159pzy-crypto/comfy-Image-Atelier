namespace Anima.Launcher.Core;

/// <summary>Model transfers may share an environment; install/repair/move operations take an exclusive handle.</summary>
public sealed class EnvironmentOperationLease : IDisposable
{
    private readonly FileStream? stream;
    private EnvironmentOperationLease(FileStream? stream) => this.stream = stream;

    public static async Task<EnvironmentOperationLease> AcquireAsync(string root, bool exclusive, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(root)) return new(null);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var path = full + ".anima-operation.lock";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("环境锁文件不能是链接。");
            try { return new(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, exclusive ? FileShare.None : FileShare.ReadWrite)); }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { await Task.Delay(100, ct); }
        }
    }
    public void Dispose() => stream?.Dispose();
}
