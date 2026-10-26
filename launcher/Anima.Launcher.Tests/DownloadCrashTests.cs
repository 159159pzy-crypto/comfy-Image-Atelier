using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Anima.Launcher.Core;

internal static class DownloadCrashTests
{
    private const long Size = 256L * 1024 * 1024;
    private sealed record Ready(int LauncherPid, int AriaPid, long RetainedBytes, long LogicalLength);
    private sealed record Result(bool LoadedPaused, int RequestsBeforeContinue, long RetainedBeforeContinue,
        long UpstreamBytes, bool RequestedNonzeroRange, bool Verified, int LauncherPid);

    public static async Task<bool> TryHandleFixture(string[] args)
    {
        if (args.FirstOrDefault() != "--download-crash-fixture") return false;
        var root = Path.GetFullPath(args[1]); var mode = args[2];
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, "crash.bin");
        if (mode == "lock")
        {
            using var lease = new FileStream(destination + ".download.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            Console.WriteLine("LOCKED"); Console.Out.Flush();
            await Task.Run(Console.ReadLine); return true;
        }
        var sha = PatternHash(Size);
        var aria = await BundledTools.EnsureAsync("aria2", Path.Combine(root, "runtime"));
        using var source = new PatternHandler(Size, mode == "crash" ? 12 : 1,
            () => File.WriteAllText(Path.Combine(root, mode + "-upstream.txt"), "contacted"));
        using var downloads = new Downloads(handler: source,
            policy: new DownloadPolicy { MinimumBytesPerSecond = 0 },
            engineOptions: new DownloadEngineOptions(aria) { MinimumAria2Bytes = 0, PollInterval = TimeSpan.FromMilliseconds(80) });
        await using var service = new DownloadTaskService(downloads, Path.Combine(root, "state"));
        var loadedPaused = service.Tasks.Count == 1 && service.Tasks[0].Status == DownloadTaskStatus.Paused;
        var beforeRequests = source.Requests;
        var retainedBefore = Size - Downloads.RemainingBytes(destination, Size, sha);
        var announced = false;
        var progress = new Inline(value =>
        {
            if (mode != "crash" || announced || value.Received < 4 * 1024 * 1024) return;
            var retained = Size - Downloads.RemainingBytes(destination, Size, sha);
            if (retained <= 0 || retained >= Size) return;
            var ariaPid = OwnedAriaPid(Environment.ProcessId, aria);
            if (ariaPid == 0) return;
            announced = true;
            JsonFile.Write(Path.Combine(root, "ready.json"), new Ready(Environment.ProcessId, ariaPid, retained,
                new FileInfo(DownloadCheckpoint.AriaData(destination)).Length));
        });
        if (mode == "resume")
        {
            File.WriteAllText(Path.Combine(root, "resume-intent.txt"), "explicit continue");
            await service.ResumeTaskAsync(service.Tasks.Single().Id, progress);
        }
        else await service.FetchAsync(new("crash fixture", ["https://fixture.invalid/crash.bin"], destination, Size, sha)
            { Network = new("direct") }, progress, default);
        var result = new Result(loadedPaused, beforeRequests, retainedBefore, source.BytesRead,
            source.NonzeroRange, await Downloads.Verify(destination, Size, sha, default), Environment.ProcessId);
        JsonFile.Write(Path.Combine(root, "result.json"), result);
        Console.WriteLine("COMPLETE"); Console.Out.Flush();
        return true;
    }

    public static async Task<int> Run(string work)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The kill-on-close acceptance fixture requires Windows.");
        var count = 0;
        void Check(bool condition, string label) { if (!condition) throw new Exception("FAIL: " + label); count++; Console.WriteLine("PASS: " + label); }
        var root = Path.Combine(work, "download-crash"); Directory.CreateDirectory(root);
        var destination = Path.Combine(root, "crash.bin");
        using var first = Start(root, "crash");
        var firstOutput = first.StandardOutput.ReadToEndAsync(); var firstError = first.StandardError.ReadToEndAsync();
        var readyPath = Path.Combine(root, "ready.json");
        Ready? ready = null;
        try
        {
            await WaitFor(() => File.Exists(readyPath), first, TimeSpan.FromSeconds(45));
            ready = JsonFile.Read<Ready>(readyPath);
            Check(ready.LauncherPid == first.Id && ready.AriaPid > 0 && ProcessAlive(ready.AriaPid),
                "isolated launcher fixture starts its own live aria2 child");
            Check(ready.RetainedBytes > 0 && ready.RetainedBytes < Size && ready.LogicalLength > ready.RetainedBytes,
                "crash fixture has persisted completed pieces distinct from sparse logical length");
            first.Kill(entireProcessTree: false); // The tested Job Object, not this test, must reap aria2.
            await first.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await WaitFor(() => !ProcessAlive(ready.AriaPid), null, TimeSpan.FromSeconds(10));
            Check(!ProcessAlive(ready.AriaPid), "hard-killing only the launcher closes its job and terminates owned aria2");
            var sha = PatternHash(Size);
            var retained = Size - Downloads.RemainingBytes(destination, Size, sha);
            Check(File.Exists(DownloadCheckpoint.AriaControl(destination)) && retained > 0 && retained < Size && !File.Exists(destination),
                "hard crash leaves a valid native control bitmap and no unverified published file");

            using var lockHolder = Start(root, "lock");
            var lockError = lockHolder.StandardError.ReadToEndAsync();
            try
            {
                if (await lockHolder.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) != "LOCKED")
                    throw new IOException("Cross-process lease fixture did not start.");
                using var resumed = Start(root, "resume");
                var resumeOutput = resumed.StandardOutput.ReadToEndAsync(); var resumeError = resumed.StandardError.ReadToEndAsync();
                try
                {
                    await WaitFor(() => File.Exists(Path.Combine(root, "resume-intent.txt")), resumed, TimeSpan.FromSeconds(15));
                    await Task.Delay(700);
                    Check(!resumed.HasExited && !File.Exists(Path.Combine(root, "resume-upstream.txt")) && !File.Exists(Path.Combine(root, "result.json")),
                        "independent resumed process waits while another process owns its destination lease");
                    await lockHolder.StandardInput.WriteLineAsync("release"); await lockHolder.StandardInput.FlushAsync();
                    await lockHolder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    await resumed.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(50));
                    if (resumed.ExitCode != 0) throw new IOException("Resume fixture failed: " + await resumeError + await resumeOutput);
                    var result = JsonFile.Read<Result>(Path.Combine(root, "result.json"));
                    Check(result.LauncherPid != first.Id && result.LoadedPaused && result.RequestsBeforeContinue == 0,
                        "new launcher process restores a paused task without starting upstream network requests");
                    Check(result.RetainedBeforeContinue == retained && result.RequestedNonzeroRange && result.UpstreamBytes < Size,
                        "explicit continuation in a new process reuses crash checkpoint bytes rather than downloading the whole file");
                    Check(result.Verified && await Downloads.Verify(destination, Size, sha, default),
                        "crash recovery publishes only after the complete original SHA-256 matches");
                    Check(!Downloads.PartialFiles(destination).Any(File.Exists), "successful crash recovery removes its own obsolete checkpoint sidecars");
                }
                finally { if (!resumed.HasExited) { resumed.Kill(true); await resumed.WaitForExitAsync(); } }
            }
            finally { if (!lockHolder.HasExited) { lockHolder.Kill(true); await lockHolder.WaitForExitAsync(); } }
        }
        catch
        {
            if (first.HasExited) Console.WriteLine("Crash fixture diagnostics: " + await firstError + await firstOutput);
            throw;
        }
        finally
        {
            if (!first.HasExited) { first.Kill(true); await first.WaitForExitAsync(); }
            // A failing test may clean only the exact owned PID established by the fixture.
            if (ready is not null && ProcessAlive(ready.AriaPid))
            { using var child = Process.GetProcessById(ready.AriaPid); child.Kill(true); await child.WaitForExitAsync(); }
        }
        Console.WriteLine("Crash recovery fixtures: " + root);
        return count;
    }

    private static Process Start(string root, string mode)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(DownloadCrashTests).Assembly.Location);
        start.ArgumentList.Add("--download-crash-fixture"); start.ArgumentList.Add(root); start.ArgumentList.Add(mode);
        return Process.Start(start) ?? throw new IOException("Cannot start crash acceptance fixture.");
    }
    private static async Task WaitFor(Func<bool> condition, Process? process, TimeSpan limit)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (process?.HasExited == true) throw new IOException("Fixture exited before reaching the required state.");
            if (timer.Elapsed > limit) throw new TimeoutException("Fixture did not reach the required state.");
            await Task.Delay(50);
        }
    }
    private static bool ProcessAlive(int pid)
    { try { using var process = Process.GetProcessById(pid); return !process.HasExited; } catch (ArgumentException) { return false; } }
    private static int OwnedAriaPid(int parent, string executable)
    {
        foreach (var process in Process.GetProcessesByName("aria2c"))
            using (process)
            {
                try
                {
                    if (NtQueryInformationProcess(process.Handle, 0, out var info, Marshal.SizeOf<BasicProcessInformation>(), out _) == 0
                        && info.ParentPid.ToInt64() == parent
                        && string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)) return process.Id;
                }
                catch (InvalidOperationException) { }
            }
        return 0;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BasicProcessInformation
    { public IntPtr ExitStatus, Peb, Affinity, Priority, Pid, ParentPid; }
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr process, int informationClass,
        out BasicProcessInformation information, int length, out int returnedLength);
    private sealed class Inline(Action<DownloadProgress> report) : IProgress<DownloadProgress> { public void Report(DownloadProgress value) => report(value); }
    private static string PatternHash(long size)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var block = new byte[131072];
        for (long position = 0; position < size; position += block.Length)
        {
            var length = (int)Math.Min(block.Length, size - position);
            Fill(block.AsSpan(0, length), position); hash.AppendData(block, 0, length);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static void Fill(Span<byte> buffer, long position)
    { for (var i = 0; i < buffer.Length; i++) buffer[i] = (byte)((position + i) % 251); }
    private sealed class PatternHandler(long size, int delayMs, Action firstRequest) : HttpMessageHandler
    {
        private int requests; private long bytesRead; private int nonzeroRange;
        public int Requests => Volatile.Read(ref requests);
        public long BytesRead => Interlocked.Read(ref bytesRead);
        public bool NonzeroRange => Volatile.Read(ref nonzeroRange) != 0;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Interlocked.Increment(ref requests) == 1) firstRequest();
            var range = request.Headers.Range?.Ranges.Single(); var from = range?.From ?? 0; var to = range?.To ?? size - 1;
            if (from > 0) Interlocked.Exchange(ref nonzeroRange, 1);
            var response = new HttpResponseMessage(range is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            { Content = new StreamContent(new PatternStream(from, to - from + 1, delayMs, amount => Interlocked.Add(ref bytesRead, amount))) };
            response.Content.Headers.ContentLength = to - from + 1;
            if (range is not null) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, size);
            response.Headers.ETag = new EntityTagHeaderValue("\"crash-fixture-v1\"");
            return Task.FromResult(response);
        }
    }
    private sealed class PatternStream(long start, long length, int delayMs, Action<int> read) : Stream
    {
        private long position;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => length; public override long Position { get => position; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var amount = (int)Math.Min(Math.Min(buffer.Length, 65536), length - position);
            if (amount <= 0) return 0;
            await Task.Delay(delayMs, ct); Fill(buffer.Span[..amount], start + position); position += amount; read(amount); return amount;
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
