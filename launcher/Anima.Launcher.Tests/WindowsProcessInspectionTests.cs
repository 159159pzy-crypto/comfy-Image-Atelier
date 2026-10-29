using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Anima.Launcher.Core;

internal static class WindowsProcessInspectionTests
{
    public static async Task<int> Run(string work)
    {
        var passed = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new Exception("FAIL: " + label);
            passed++; Console.WriteLine("PASS: " + label);
        }
        async Task Canceled(Func<Task> action, string label)
        {
            try { await action(); }
            catch (OperationCanceledException) { Check(true, label); return; }
            throw new Exception("Expected cancellation: " + label);
        }

        byte[] Table(bool ipv6, params (IPAddress Address, int Port, uint Pid, uint State)[] rows)
        {
            var size = ipv6 ? 56 : 24;
            var bytes = new byte[4 + rows.Length * size];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)rows.Length);
            for (var i = 0; i < rows.Length; i++)
            {
                var row = bytes.AsSpan(4 + i * size, size);
                rows[i].Address.GetAddressBytes().CopyTo(row[(ipv6 ? 0 : 4)..]);
                BinaryPrimitives.WriteUInt16BigEndian(row[(ipv6 ? 20 : 8)..], (ushort)rows[i].Port);
                BinaryPrimitives.WriteUInt32LittleEndian(row[(ipv6 ? 48 : 0)..], rows[i].State);
                BinaryPrimitives.WriteUInt32LittleEndian(row[(ipv6 ? 52 : 20)..], rows[i].Pid);
            }
            return bytes;
        }

        var rows = new List<WindowsProcessInspection.Listener>();
        var ipv4 = Table(false, (IPAddress.Loopback, 18188, 42, 2), (IPAddress.Any, 18188, 42, 2),
            (IPAddress.Loopback, 18188, 99, 5), (IPAddress.Loopback, 18188, 0, 2));
        Check(WindowsProcessInspection.TryReadListeners(ipv4, AddressFamily.InterNetwork, rows)
            && rows.Count == 2 && rows.All(r => r.Port == 18188 && r.ProcessId == 42),
            "native IPv4 table decodes network byte order and excludes connections and unknown PIDs");
        var ipv6 = Table(true, (IPAddress.IPv6Any, 18188, 42, 2), (IPAddress.IPv6Loopback, 18188, 42, 2));
        Check(WindowsProcessInspection.TryReadListeners(ipv6, AddressFamily.InterNetworkV6, rows)
            && rows.Count == 4 && rows[^1].Address.Equals(IPAddress.IPv6Loopback), "native IPv6 table decodes all 16 address bytes and PID offset");
        Check(WindowsProcessInspection.UniqueLoopbackOwner(rows, 18188) == 42,
            "the same listener owner on IPv4 and IPv6 remains unambiguous");
        Check(WindowsProcessInspection.UniqueLoopbackOwner(rows.Append(new(IPAddress.Loopback, 18188, 43)), 18188) == 0,
            "different loopback listener PIDs never authorize reuse");
        Check(WindowsProcessInspection.UniqueLoopbackOwner([new(IPAddress.Parse("192.0.2.10"), 18188, 43), new(IPAddress.Loopback, 18189, 43)], 18188) == 0,
            "unrelated interface addresses and ports are ignored");
        Check(WindowsProcessInspection.UniqueLoopbackOwner([new(IPAddress.Parse("::ffff:127.0.0.1"), 18188, 42)], 18188) == 42,
            "IPv4-mapped loopback addresses resolve to the same local endpoint");
        Check(!WindowsProcessInspection.TryReadListeners(ipv6.AsSpan(0, ipv6.Length - 1), AddressFamily.InterNetworkV6, [])
            && !WindowsProcessInspection.TryReadListeners([255, 255, 255, 255], AddressFamily.InterNetwork, [])
            && !WindowsProcessInspection.TryReadListeners([0, 0], AddressFamily.InterNetwork, []),
            "truncated or overflowing native table counts are rejected without reading beyond the buffer");
        Check(!WindowsProcessInspection.TryReadListeners([0, 0, 0, 0], AddressFamily.Unspecified, []),
            "unsupported address families cannot produce listener ownership");

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Canceled(() => WindowsProcessInspection.FindLoopbackListenerAsync(18188, canceled.Token), "native listener inspection observes cancellation");
        await Canceled(() => WindowsProcessInspection.FindParentProcessIdAsync(Environment.ProcessId, canceled.Token), "native parent inspection observes cancellation");
        await Canceled(() => ServiceManager.InspectComfyReuseAsync(new(), canceled.Token), "Comfy service inspection propagates cancellation before configuration work");
        Check(await WindowsProcessInspection.FindLoopbackListenerAsync(0) == 0
            && await WindowsProcessInspection.FindParentProcessIdAsync(-1) == 0, "invalid endpoint/process identifiers remain unconfirmed");

        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIP: live Windows process snapshots require Windows.");
            return passed;
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        foreach (var address in new[] { IPAddress.Loopback, IPAddress.Any, IPAddress.IPv6Loopback, IPAddress.IPv6Any })
        {
            if (address.AddressFamily == AddressFamily.InterNetworkV6 && !Socket.OSSupportsIPv6)
            { Console.WriteLine("SKIP: live IPv6 listener unavailable on this host."); continue; }
            using var listener = new TcpListener(address, 0);
            if (address.AddressFamily == AddressFamily.InterNetworkV6) listener.Server.DualMode = false;
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Check(await WindowsProcessInspection.FindLoopbackListenerAsync(port, deadline.Token) == Environment.ProcessId,
                "native listener snapshot finds current process at " + address);
            listener.Stop();
            Check(await WindowsProcessInspection.FindLoopbackListenerAsync(port, deadline.Token) == 0,
                "closing listener removes ownership at " + address);
        }

        // Use the existing fixture branch so its native parent is this exact test process.
        var info = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = work,
        };
        Directory.CreateDirectory(work);
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(typeof(WindowsProcessInspectionTests).Assembly.Location);
        info.ArgumentList.Add("--exit-fixture");
        using var child = Process.Start(info) ?? throw new IOException("Cannot start process identity fixture.");
        try
        {
            Check(await child.StandardOutput.ReadLineAsync(deadline.Token) == "ready", "native process fixture is ready");
            Check(await WindowsProcessInspection.FindParentProcessIdAsync(child.Id, deadline.Token) == Environment.ProcessId,
                "Toolhelp reports the direct fixture parent without launching a shell");
            await child.StandardInput.WriteLineAsync();
            await child.WaitForExitAsync(deadline.Token);
            Check(child.ExitCode == 23 && await WindowsProcessInspection.FindParentProcessIdAsync(child.Id, deadline.Token) == 0,
                "an exited process cannot contribute a stale parent identity");
        }
        finally
        {
            if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); }
        }
        return passed;
    }
}
