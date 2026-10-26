using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Anima.Launcher.Core;

public sealed record DownloadDiskSpaceOptions
{
    public string? LedgerRoot { get; init; }
    public Func<string, long>? AvailableBytes { get; init; }
    public Func<string, long>? AllocatedBytes { get; init; }
}

internal sealed record DiskAllocationPath(string Path, long ExcludedAtStart);
internal sealed record DiskSpaceClaim(string Id, int ProcessId, long ProcessStartedUtcTicks, long Size,
    DiskAllocationPath[] Paths, bool Frozen = false);

/// <summary>Physical allocation accounting is independent of downloaded/verified network bytes.</summary>
public static class DownloadDiskSpace
{
    public const long SafetyMargin = 256L * 1024 * 1024;
    private static readonly long ProcessStarted = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetCompressedFileSizeW(string file, out uint high);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumePathNameW(string file, StringBuilder volume, uint length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mount, StringBuilder name, uint length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceExW(string directory, out ulong available, out ulong total, out ulong free);

    // GetCompressedFileSize includes compression and sparse allocation. Unknown is zero
    // credit, never logical Length, so an unsupported probe cannot over-promise space.
    public static long AllocatedBytes(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows() || !File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return 0;
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(@"\\?\", StringComparison.Ordinal))
                full = full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
            Marshal.SetLastPInvokeError(0);
            var low = GetCompressedFileSizeW(full, out var high);
            if (low == uint.MaxValue && Marshal.GetLastPInvokeError() != 0) return 0;
            var value = ((ulong)high << 32) | low;
            return value > long.MaxValue ? 0 : (long)value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return 0; }
    }

    public static string VolumeRoot(string path)
    {
        var full = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows()) return Path.GetPathRoot(full)!;
        var volume = new StringBuilder(32768);
        if (!GetVolumePathNameW(full, volume, (uint)volume.Capacity))
            throw new IOException("无法识别下载目标所在磁盘。", new Win32Exception(Marshal.GetLastPInvokeError()));
        return volume.ToString();
    }

    public static long AvailableBytes(string path)
    {
        var volume = VolumeRoot(path);
        if (!OperatingSystem.IsWindows()) return new DriveInfo(volume).AvailableFreeSpace;
        if (!GetDiskFreeSpaceExW(volume, out var available, out _, out _))
            throw new IOException("无法读取下载目标磁盘的可用空间。", new Win32Exception(Marshal.GetLastPInvokeError()));
        return available > long.MaxValue ? long.MaxValue : (long)available;
    }

    // The caller must separately verify a final file; only identity-bound resumable
    // partials earn allocation credit here.
    public static long ReusableAllocatedBytes(string destination, long size, string sha)
    {
        try
        {
            var aria = DownloadCheckpoint.AriaData(destination);
            if (File.Exists(aria))
            {
                var state = DownloadCheckpoint.Read(destination, size, sha);
                if (state is null || new FileInfo(aria).Length > size) return 0;
                if (File.Exists(DownloadCheckpoint.AriaControl(destination)))
                    _ = DownloadCheckpoint.ReadCompletedPieces(DownloadCheckpoint.AriaControl(destination), size, new FileInfo(aria).Length);
                else if (state.EngineStarted || new FileInfo(aria).Length != state.ImportedPrefixLength) return 0;
                return Math.Min(size, AllocatedBytes(aria));
            }
            var part = destination + ".part";
            if (!File.Exists(part) || !File.Exists(part + ".json")) return 0;
            var resume = JsonFile.Read<ResumeInfo>(part + ".json");
            return resume.Size == size && resume.Sha256.Equals(sha, StringComparison.OrdinalIgnoreCase) && new FileInfo(part).Length <= size
                ? Math.Min(size, AllocatedBytes(part)) : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException) { return 0; }
    }

    public static long RemainingAllocationBytes(string destination, long size, string sha) =>
        Math.Max(0, size - ReusableAllocatedBytes(destination, size, sha));

    public static async Task<DownloadDiskSpaceReservation> AcquireAsync(string destination, long size, string? reusablePart = null,
        CancellationToken ct = default, DownloadDiskSpaceOptions? options = null)
    {
        if (size < 0) throw new ArgumentOutOfRangeException(nameof(size));
        destination = Path.GetFullPath(destination);
        options ??= new();
        var volume = VolumeRoot(destination);
        var key = VolumeIdentity(volume);
        var ledger = LedgerPath(volume, options);
        long Allocated(string path) => Math.Max(0, (options.AllocatedBytes ?? AllocatedBytes)(path));
        var paths = new[] { destination + ".part", DownloadCheckpoint.AriaData(destination), destination }
            .Append(reusablePart is null ? destination + ".part" : Path.GetFullPath(reusablePart)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new DiskAllocationPath(path, reusablePart is not null && path.Equals(Path.GetFullPath(reusablePart), StringComparison.OrdinalIgnoreCase) ? 0 : Allocated(path))).ToArray();
        if (paths.Any(path => !VolumeIdentity(VolumeRoot(path.Path)).Equals(key, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("下载临时文件必须与目标位于同一磁盘。");
        var claim = new DiskSpaceClaim(Guid.NewGuid().ToString("N"), Environment.ProcessId, ProcessStarted, size, paths);
        await MutateAsync(ledger, volume, options, claims => claims.Add(claim), ct);
        return new(ledger, volume, options, claim.Id, destination);
    }

    private static string VolumeIdentity(string mount)
    {
        if (!OperatingSystem.IsWindows()) return mount;
        var name = new StringBuilder(128);
        return GetVolumeNameForVolumeMountPointW(mount, name, (uint)name.Capacity) ? name.ToString().ToUpperInvariant() : mount.ToUpperInvariant();
    }

    private static string LedgerPath(string volume, DownloadDiskSpaceOptions options)
    {
        var root = options.LedgerRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnimaRandomStudio", "download-space");
        return Path.Combine(Path.GetFullPath(root), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(VolumeIdentity(volume)))).ToLowerInvariant() + ".json");
    }

    public static async Task<long> AvailableForPlanningAsync(string path, CancellationToken ct = default, DownloadDiskSpaceOptions? options = null)
    {
        options ??= new();
        var volume = VolumeRoot(path);
        var ledger = LedgerPath(volume, options);
        var available = (options.AvailableBytes ?? AvailableBytes)(volume);
        if (!File.Exists(ledger)) return available;
        using var gate = await DownloadTargetLease.AcquireAsync(ledger, ct);
        var reserved = JsonFile.Read<DiskSpaceClaim[]>(ledger).Where(OwnerAlive).Sum(claim => Outstanding(claim, options));
        return Math.Max(0, available - reserved);
    }

    internal static async Task MutateAsync(string ledger, string volume, DownloadDiskSpaceOptions options,
        Action<List<DiskSpaceClaim>> mutate, CancellationToken ct, bool enforce = true)
    {
        using var gate = await DownloadTargetLease.AcquireAsync(ledger, ct);
        var claims = File.Exists(ledger) ? JsonFile.Read<DiskSpaceClaim[]>(ledger).ToList() : [];
        claims.RemoveAll(claim => !OwnerAlive(claim));
        mutate(claims);
        if (enforce)
        {
            var required = claims.Sum(claim => Outstanding(claim, options));
            var available = (options.AvailableBytes ?? AvailableBytes)(volume);
            if (available < SafetyMargin || required > available - SafetyMargin)
                throw new InsufficientSpaceException(volume, required);
        }
        JsonFile.Write(ledger, claims.ToArray());
    }

    private static long Outstanding(DiskSpaceClaim claim, DownloadDiskSpaceOptions options)
    {
        if (claim.Frozen) return 0;
        var allocated = claim.Paths.Select(path => Math.Max(0, (options.AllocatedBytes ?? AllocatedBytes)(path.Path) - path.ExcludedAtStart)).DefaultIfEmpty(0).Max();
        return Math.Max(0, claim.Size - allocated);
    }

    private static bool OwnerAlive(DiskSpaceClaim claim)
    {
        try
        {
            using var process = Process.GetProcessById(claim.ProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == claim.ProcessStartedUtcTicks;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (Win32Exception) { return true; } // Cannot inspect another owner: reserve conservatively.
    }
}

public sealed class DownloadDiskSpaceReservation : IAsyncDisposable
{
    private readonly string ledger, volume, id, destination;
    private readonly DownloadDiskSpaceOptions options;
    private int disposed;
    internal DownloadDiskSpaceReservation(string ledger, string volume, DownloadDiskSpaceOptions options, string id, string destination)
    { this.ledger = ledger; this.volume = volume; this.options = options; this.id = id; this.destination = destination; }

    public Task CheckAsync(CancellationToken ct = default) =>
        DownloadDiskSpace.MutateAsync(ledger, volume, options, _ => { }, ct);

    public Task SetPrimaryPathAsync(string path, CancellationToken ct = default)
    {
        path = Path.GetFullPath(path);
        if (!DownloadDiskSpace.VolumeRoot(path).Equals(volume, StringComparison.OrdinalIgnoreCase))
            throw new IOException("下载暂存重命名不能跨磁盘。");
        return DownloadDiskSpace.MutateAsync(ledger, volume, options, claims =>
        {
            var index = claims.FindIndex(claim => claim.Id == id);
            if (index < 0) throw new ObjectDisposedException(nameof(DownloadDiskSpaceReservation));
            var claim = claims[index];
            // Call after deleting/truncating an old untrusted file. Its old physical
            // bytes affected admission, but must not be subtracted from the new generation.
            var reset = !File.Exists(path) || new FileInfo(path).Length == 0;
            claims[index] = claim with { Frozen = false, Paths = claim.Paths.Any(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
                ? claim.Paths.Select(item => reset && item.Path.Equals(path, StringComparison.OrdinalIgnoreCase) ? item with { ExcludedAtStart = 0 } : item).ToArray()
                : [.. claim.Paths, new(path, 0)] };
        }, ct);
    }

    // The previous transport is stopped: its existing file is already subtracted
    // from physical free space, so only the fresh file's future growth is reserved.
    public async Task<DownloadDiskSpaceReservation> ReserveFreshFileAsync(string freshPath, long size, CancellationToken ct = default)
    {
        freshPath = Path.GetFullPath(freshPath);
        if (size < 0 || !DownloadDiskSpace.VolumeRoot(freshPath).Equals(volume, StringComparison.OrdinalIgnoreCase))
            throw new IOException("兼容重下必须使用同磁盘的有效大小。");
        var freshId = Guid.NewGuid().ToString("N");
        using var process = Process.GetCurrentProcess();
        var baseline = Math.Max(0, (options.AllocatedBytes ?? DownloadDiskSpace.AllocatedBytes)(freshPath));
        var publishedBaseline = Math.Max(0, (options.AllocatedBytes ?? DownloadDiskSpace.AllocatedBytes)(destination));
        await DownloadDiskSpace.MutateAsync(ledger, volume, options, claims =>
        {
            var index = claims.FindIndex(claim => claim.Id == id);
            if (index < 0) throw new ObjectDisposedException(nameof(DownloadDiskSpaceReservation));
            claims[index] = claims[index] with { Frozen = true };
            claims.Add(new(freshId, Environment.ProcessId, process.StartTime.ToUniversalTime().Ticks, size,
                [new(freshPath, baseline), new(destination, publishedBaseline)]));
        }, ct);
        return new(ledger, volume, options, freshId, destination);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await DownloadDiskSpace.MutateAsync(ledger, volume, options, claims => claims.RemoveAll(claim => claim.Id == id), default, enforce: false);
    }
}
