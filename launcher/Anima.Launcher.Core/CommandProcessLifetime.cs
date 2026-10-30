using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Anima.Launcher.Core;

/// <summary>
/// A command and all of its children are assigned to this job at process creation.
/// Waiting for a parent PID alone cannot cover a backend which already exited,
/// nor a descendant which inherited a stdout handle and keeps the pipe open.
/// </summary>
internal static class CommandProcessLifetime
{
    public static async Task<string> RunAsync(ProcessStartInfo info, Action<string>? log, CancellationToken ct,
        TimeSpan timeout, bool allowBackgroundChildren)
    {
        using var job = CreateJobObject(IntPtr.Zero, null);
        Ensure(!job.IsInvalid);
        SetKillOnClose(job, true);
        using var stdout = new RedirectedPipe();
        using var stderr = new RedirectedPipe();
        var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
        using var stdin = CreateFile("NUL", 0x80000000, 3, ref security, 3, 0, IntPtr.Zero);
        Ensure(!stdin.IsInvalid);
        using var attributes = new ProcessAttributes(job, stdout.WriteHandle, stderr.WriteHandle, stdin);
        var startup = new StartupInfoEx
        {
            StartupInfo = new()
            {
                Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100,
                StdInput = stdin.DangerousGetHandle(), StdOutput = stdout.WriteHandle.DangerousGetHandle(),
                StdError = stderr.WriteHandle.DangerousGetHandle(),
            },
            AttributeList = attributes.Pointer,
        };
        var commandLine = new StringBuilder(Quote(info.FileName));
        foreach (var argument in info.ArgumentList) commandLine.Append(' ').Append(Quote(argument));
        // ProcessStartInfo has already applied the unified proxy/environment policy.
        // Preserve empty values and hidden drive-current-directory variables too.
        var environment = string.Join('\0', info.Environment.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Where(x => x.Value is not null).Select(x => x.Key + "=" + x.Value)) + "\0\0";
        var environmentPointer = Marshal.StringToHGlobalUni(environment);
        ProcessInformation started;
        try
        {
            ct.ThrowIfCancellationRequested();
            // JOB_LIST is applied before the initial thread runs, closing the
            // Process.Start -> AssignProcessToJobObject race for short parents.
            Ensure(CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, true,
                0x00080000 | 0x08000000 | 0x00000400, environmentPointer,
                info.WorkingDirectory.Length == 0 ? null : info.WorkingDirectory, ref startup, out started));
        }
        finally { Marshal.FreeHGlobal(environmentPointer); }
        using var process = new SafeFileHandle(started.Process, true);
        using var thread = new SafeFileHandle(started.Thread, true);
        stdout.WriteHandle.Dispose(); stderr.WriteHandle.Dispose();
        using var outputReader = stdout.Reader(info.StandardOutputEncoding ?? Encoding.UTF8);
        using var errorReader = stderr.Reader(info.StandardErrorEncoding ?? Encoding.UTF8);
        var output = new StringBuilder();
        var errors = new StringBuilder();
        var outputTask = ReadLinesAsync(outputReader, output, false, log);
        var errorTask = ReadLinesAsync(errorReader, errors, true, log);
        var readers = Task.WhenAll(outputTask, errorTask);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var rootExited = WaitForRootAsync(process, deadline.Token);
        var released = false;
        try
        {
            // A reader fault must also stop the command instead of leaving its
            // writer blocked on a full pipe until the command timeout elapses.
            var observed = new List<Task> { rootExited, outputTask, errorTask };
            while (true)
            {
                var completed = await Task.WhenAny(observed);
                await completed;
                if (completed == rootExited) break;
                observed.Remove(completed);
            }
            Ensure(GetExitCodeProcess(process, out var exitCode));
            if (exitCode != 0 || !allowBackgroundChildren) await TerminateAndJoinAsync(job);
            await readers.WaitAsync(deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            if (exitCode != 0) throw new CommandFailureException(info.FileName, unchecked((int)exitCode), output.ToString(), errors.ToString());
            if (allowBackgroundChildren)
            {
                // ServiceManager gives its service real log handles. Only that
                // explicit successful bootstrap releases its background child.
                SetKillOnClose(job, false);
                released = true;
            }
            return output.ToString();
        }
        catch
        {
            await TerminateAndJoinAsync(job);
            // Job termination closes every inherited writer, so these reads can
            // finish before cancellation is reported to the caller.
            try { await readers; } catch { /* Preserve the initiating failure. */ }
            if (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
                throw new TimeoutException($"{Path.GetFileName(info.FileName)} 执行超时，已终止；请查看日志确认是否长时间无输出。");
            throw;
        }
        finally
        {
            deadline.Cancel();
            if (!released) await TerminateAndJoinAsync(job);
            try { await rootExited; } catch (OperationCanceledException) { }
        }
    }

    private static async Task ReadLinesAsync(StreamReader reader, StringBuilder buffer, bool bounded, Action<string>? log)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            buffer.AppendLine(line);
            if (bounded && buffer.Length > 65536) buffer.Remove(0, buffer.Length - 65536);
            log?.Invoke(DiagnosticRedaction.Text(line));
        }
    }

    private static async Task WaitForRootAsync(SafeFileHandle process, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var result = WaitForSingleObject(process, 0);
            if (result == 0) return;
            if (result != 258) throw new Win32Exception(Marshal.GetLastWin32Error());
            await Task.Delay(20, ct);
        }
    }

    private static async Task TerminateAndJoinAsync(SafeFileHandle job)
    {
        if (ActiveProcesses(job) == 0) return;
        Ensure(TerminateJobObject(job, 1));
        // Do not reuse the caller's cancelled token, or merely close the parent
        // handle. The installation can resume only after every writer is gone.
        while (ActiveProcesses(job) != 0) await Task.Delay(10);
    }

    private static uint ActiveProcesses(SafeFileHandle job)
    {
        Ensure(QueryInformationJobObject(job, 1, out var accounting, (uint)Marshal.SizeOf<BasicAccountingInformation>(), IntPtr.Zero));
        return accounting.ActiveProcesses;
    }

    private static void SetKillOnClose(SafeFileHandle job, bool enabled)
    {
        var limits = new ExtendedLimitInformation { BasicLimitInformation = new() { LimitFlags = enabled ? 0x2000u : 0u } };
        Ensure(SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>()));
    }

    // Windows CommandLineToArgv/CRT quoting, including empty arguments and
    // backslashes immediately before a quote or the final closing quote.
    private static string Quote(string value)
    {
        var quoted = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { quoted.Append('\\', slashes * 2 + 1).Append(c); slashes = 0; continue; }
            quoted.Append('\\', slashes).Append(c); slashes = 0;
        }
        return quoted.Append('\\', slashes * 2).Append('"').ToString();
    }

    private static void Ensure(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }

    private sealed class RedirectedPipe : IDisposable
    {
        private readonly SafeFileHandle readHandle;
        public SafeFileHandle WriteHandle { get; }
        public RedirectedPipe()
        {
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
            Ensure(CreatePipe(out readHandle, out var write, ref security, 0));
            WriteHandle = write;
            try { Ensure(SetHandleInformation(readHandle, 1, 0)); }
            catch { Dispose(); throw; }
        }
        public StreamReader Reader(Encoding encoding) => new(new FileStream(readHandle, FileAccess.Read, 4096, false), encoding, true);
        public void Dispose() { WriteHandle.Dispose(); readHandle.Dispose(); }
    }

    private sealed class ProcessAttributes : IDisposable
    {
        public IntPtr Pointer { get; private set; }
        private IntPtr jobHandles, inheritedHandles;
        private bool initialized;
        public ProcessAttributes(SafeFileHandle job, params SafeFileHandle[] handles)
        {
            nuint size = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
            Pointer = Marshal.AllocHGlobal(checked((nint)size));
            try
            {
                Ensure(InitializeProcThreadAttributeList(Pointer, 2, 0, ref size));
                initialized = true;
                jobHandles = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(jobHandles, job.DangerousGetHandle());
                Ensure(UpdateProcThreadAttribute(Pointer, 0, 0x0002000D, jobHandles, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero));
                inheritedHandles = Marshal.AllocHGlobal(IntPtr.Size * handles.Length);
                for (var i = 0; i < handles.Length; i++) Marshal.WriteIntPtr(inheritedHandles, i * IntPtr.Size, handles[i].DangerousGetHandle());
                Ensure(UpdateProcThreadAttribute(Pointer, 0, 0x00020002, inheritedHandles, (nuint)(IntPtr.Size * handles.Length), IntPtr.Zero, IntPtr.Zero));
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            if (Pointer != IntPtr.Zero) { if (initialized) DeleteProcThreadAttributeList(Pointer); Marshal.FreeHGlobal(Pointer); Pointer = IntPtr.Zero; }
            if (jobHandles != IntPtr.Zero) { Marshal.FreeHGlobal(jobHandles); jobHandles = IntPtr.Zero; }
            if (inheritedHandles != IntPtr.Zero) { Marshal.FreeHGlobal(inheritedHandles); inheritedHandles = IntPtr.Zero; }
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int Size; public string? Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, Reserved2Length;
        public IntPtr Reserved2, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr AttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicAccountingInformation
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit;
        public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters
    { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation; public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimitInformation information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryInformationJobObject(SafeFileHandle job, int infoClass, out BasicAccountingInformation information, uint length, IntPtr returnLength);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe, ref SecurityAttributes attributes, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, ref SecurityAttributes attributes, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previousValue, IntPtr returnSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string? applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment, string? directory, ref StartupInfoEx startup, out ProcessInformation information);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetExitCodeProcess(SafeFileHandle process, out uint exitCode);
}
