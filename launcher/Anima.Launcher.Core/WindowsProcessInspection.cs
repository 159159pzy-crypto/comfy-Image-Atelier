using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

[assembly: InternalsVisibleTo("Anima.Launcher.Tests")]

namespace Anima.Launcher.Core;

/// <summary>Read-only Windows snapshots. Failure or ambiguous ownership never grants process ownership.</summary>
internal static class WindowsProcessInspection
{
    private const uint ErrorInsufficientBuffer = 122;
    private const int MaximumTableBytes = 16 * 1024 * 1024;
    private const uint OwnerPidListeners = 3;
    internal sealed record Listener(IPAddress Address, int Port, int ProcessId);

    internal static Task<int> FindLoopbackListenerAsync(int port, CancellationToken ct = default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() || port is < 1 or > 65535) return 0;
        try
        {
            var listeners = new List<Listener>();
            if (!ReadListeners(AddressFamily.InterNetwork, listeners, ct)
                || !ReadListeners(AddressFamily.InterNetworkV6, listeners, ct)) return 0;
            return UniqueLoopbackOwner(listeners, port);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { return 0; }
    }, ct);

    internal static Task<int> FindParentProcessIdAsync(int processId, CancellationToken ct = default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() || processId <= 0) return 0;
        try
        {
            using var snapshot = CreateToolhelp32Snapshot(0x00000002, 0); // TH32CS_SNAPPROCESS
            if (snapshot.IsInvalid) return 0;
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), ExeFile = "" };
            if (!Process32FirstW(snapshot, ref entry)) return 0;
            do
            {
                ct.ThrowIfCancellationRequested();
                if (entry.ProcessId == processId)
                    return entry.ParentProcessId is > 0 and <= int.MaxValue ? (int)entry.ParentProcessId : 0;
            } while (Process32NextW(snapshot, ref entry));
            return 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { return 0; }
    }, ct);

    private static bool ReadListeners(AddressFamily family, ICollection<Listener> listeners, CancellationToken ct)
    {
        uint required = 0;
        var error = GetExtendedTcpTable(null, ref required, false, (uint)family, OwnerPidListeners, 0);
        // Endpoint churn can grow the table between the size query and the copy.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (error != ErrorInsufficientBuffer || required < sizeof(uint) || required > MaximumTableBytes) return false;
            var buffer = new byte[(int)required];
            error = GetExtendedTcpTable(buffer, ref required, false, (uint)family, OwnerPidListeners, 0);
            ct.ThrowIfCancellationRequested();
            if (error == 0)
                return required <= buffer.Length && TryReadListeners(buffer.AsSpan(0, (int)required), family, listeners, ct);
        }
        return false;
    }

    // MIB_TCP[6]TABLE_OWNER_PID starts with a DWORD count; rows are DWORD aligned.
    // Local addresses/ports are network byte order; state/count/PID are Windows DWORDs.
    internal static bool TryReadListeners(ReadOnlySpan<byte> table, AddressFamily family, ICollection<Listener> listeners, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var ipv6 = family == AddressFamily.InterNetworkV6;
        if ((!ipv6 && family != AddressFamily.InterNetwork) || table.Length < sizeof(uint)) return false;
        var rowSize = ipv6 ? 56 : 24;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(table);
        if (count > (table.Length - sizeof(uint)) / rowSize) return false;
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var row = table.Slice(sizeof(uint) + i * rowSize, rowSize);
            var state = BinaryPrimitives.ReadUInt32LittleEndian(row[(ipv6 ? 48 : 0)..]);
            var pid = BinaryPrimitives.ReadUInt32LittleEndian(row[(ipv6 ? 52 : 20)..]);
            if (state != 2 || pid is 0 or > int.MaxValue) continue;
            var address = new IPAddress(row.Slice(ipv6 ? 0 : 4, ipv6 ? 16 : 4));
            var port = BinaryPrimitives.ReadUInt16BigEndian(row[(ipv6 ? 20 : 8)..]);
            listeners.Add(new(address, port, (int)pid));
        }
        return true;
    }

    internal static int UniqueLoopbackOwner(IEnumerable<Listener> listeners, int port)
    {
        var owner = 0;
        foreach (var listener in listeners)
        {
            if (listener.Port != port || listener.ProcessId <= 0) continue;
            var address = listener.Address.IsIPv4MappedToIPv6 ? listener.Address.MapToIPv4() : listener.Address;
            if (!address.Equals(IPAddress.Loopback) && !address.Equals(IPAddress.Any)
                && !address.Equals(IPAddress.IPv6Loopback) && !address.Equals(IPAddress.IPv6Any)) continue;
            if (owner != 0 && owner != listener.ProcessId) return 0;
            owner = listener.ProcessId;
        }
        return owner;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetExtendedTcpTable([Out] byte[]? table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order,
        uint family, uint tableClass, uint reserved);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(SafeFileHandle snapshot, ref ProcessEntry entry);
}
