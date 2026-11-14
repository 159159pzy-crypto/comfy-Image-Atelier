namespace Anima.Launcher.Core;

internal sealed record DependencySpaceBudget(string Volume, long CacheCopyBytes, long CacheExpandedBytes, long PythonExpandedBytes)
{
    public long RequiredBytes => checked(CacheCopyBytes + CacheExpandedBytes + PythonExpandedBytes);
    public long MinimumFreeBytes => checked(RequiredBytes + DownloadDiskSpace.SafetyMargin);
}

/// <summary>Only the prepared package batch is budgeted; existing Python trees are never scanned or copied.</summary>
internal static class DependencyInstallationSpace
{
    internal static DependencySpaceBudget[] Calculate(string runtimeRoot, string python, long cacheCopyBytes, long expandedBytes,
        Func<string, string>? volumeFor = null)
    {
        if (cacheCopyBytes < 0 || expandedBytes < 0) throw new ArgumentOutOfRangeException(nameof(cacheCopyBytes));
        volumeFor ??= DownloadDiskSpace.VolumeRoot;
        var cacheVolume = volumeFor(Path.GetFullPath(runtimeRoot));
        var pythonVolume = volumeFor(Path.GetDirectoryName(Path.GetFullPath(python))!);
        // uv's copy mode and pip's scratch extraction may coexist with the installed
        // files, so keep a full expanded batch on both runtime and Python volumes.
        var budgets = cacheVolume.Equals(pythonVolume, StringComparison.OrdinalIgnoreCase)
            ? new[] { new DependencySpaceBudget(cacheVolume, cacheCopyBytes, expandedBytes, expandedBytes) }
            : new[] { new DependencySpaceBudget(cacheVolume, cacheCopyBytes, expandedBytes, 0), new DependencySpaceBudget(pythonVolume, 0, 0, expandedBytes) };
        foreach (var budget in budgets) _ = budget.MinimumFreeBytes; // Fail closed on overflow before any write.
        return budgets;
    }

    internal static void Validate(IEnumerable<DependencySpaceBudget> budgets, Func<string, long>? availableBytes = null,
        Action<string>? log = null)
    {
        availableBytes ??= DownloadDiskSpace.AvailableBytes;
        foreach (var budget in budgets)
        {
            log?.Invoke($"依赖空间预检 · {budget.Volume}：缓存复制 {budget.CacheCopyBytes} 字节，解包缓存 {budget.CacheExpandedBytes} 字节，Python 安装 {budget.PythonExpandedBytes} 字节，另预留 {DownloadDiskSpace.SafetyMargin} 字节。");
            if (availableBytes(budget.Volume) < budget.MinimumFreeBytes)
                throw new InsufficientSpaceException(budget.Volume, budget.RequiredBytes);
        }
    }
}
