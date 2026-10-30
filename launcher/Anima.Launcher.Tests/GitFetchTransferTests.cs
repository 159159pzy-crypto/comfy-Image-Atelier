using System.Diagnostics;
using Anima.Launcher.Core;

internal static class GitFetchTransferTests
{
    public static async Task<int> Run(string work)
    {
        var checks = 0;
        void Check(bool condition, string detail)
        {
            if (!condition) throw new Exception(detail);
            checks++; Console.WriteLine("PASS: " + detail);
        }
        string Root(string name)
        {
            var path = Path.Combine(work, "git-transfer-" + name);
            Directory.CreateDirectory(Path.Combine(path, ".git", "objects", "pack"));
            return path;
        }
        var idle = TimeSpan.FromMilliseconds(400);
        var poll = TimeSpan.FromMilliseconds(25);
        Task<string> Run(string root, Func<CancellationToken, Task<string>> fetch, CancellationToken ct = default) =>
            GitFetchRecovery.ExecuteTransferAsync(root, fetch, ct, idleTimeout: idle, pollInterval: poll, gitIsActive: () => false);

        var growing = Root("growing");
        var watch = Stopwatch.StartNew();
        var result = await Run(growing, async ct =>
        {
            await using var pack = new FileStream(Path.Combine(growing, ".git", "objects", "pack", "tmp_pack_growth"),
                FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
            for (var i = 0; i < 16; i++)
            {
                await pack.WriteAsync(new byte[4096], ct);
                await pack.FlushAsync(ct);
                await Task.Delay(75, ct); // No console/log heartbeat: only pack bytes prove activity.
            }
            return "fetched";
        });
        Check(result == "fetched" && watch.Elapsed > idle * 2,
            "a silent fetch with growing pack bytes succeeds even when total duration exceeds the idle limit");

        var loose = Root("loose-objects");
        var looseDirectory = Path.Combine(loose, ".git", "objects", "ab");
        Directory.CreateDirectory(looseDirectory);
        result = await Run(loose, async ct =>
        {
            for (var i = 0; i < 12; i++)
            {
                await File.WriteAllBytesAsync(Path.Combine(looseDirectory, i.ToString("x38")), new byte[4096], ct);
                await Task.Delay(75, ct);
            }
            return "unpacked";
        });
        Check(result == "unpacked", "new loose objects also count as real progress for Git unpack-objects transfers");

        var stalled = Root("stalled");
        var childEnded = false;
        var shallow = Path.Combine(stalled, ".git", "shallow.lock");
        try
        {
            await Run(stalled, async ct =>
            {
                File.WriteAllBytes(Path.Combine(stalled, ".git", "objects", "pack", "tmp_pack_stalled"), []);
                await Task.Delay(40, ct);
                File.WriteAllText(shallow, "this interrupted fetch");
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                finally { await Task.Delay(100); childEnded = true; }
                return "unexpected";
            });
            throw new Exception("A zero-byte stalled fetch should time out.");
        }
        catch (TimeoutException)
        {
            Check(childEnded, "idle timeout awaits the fetch's asynchronous cleanup before returning or switching sources");
            Check(!File.Exists(shallow) && Directory.GetFiles(Path.GetDirectoryName(shallow)!, "shallow.lock.anima-interrupted-*").Length == 1,
                "a transfer idle timeout retains the existing newly-created shallow-lock recovery contract");
        }

        var canceled = Root("user-cancel");
        childEnded = false;
        using (var user = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
        {
            try
            {
                await Run(canceled, async ct =>
                {
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                    finally { await Task.Delay(75); childEnded = true; }
                    return "unexpected";
                }, user.Token);
                throw new Exception("A user canceled fetch should remain canceled.");
            }
            catch (OperationCanceledException error)
            { Check(childEnded && error.CancellationToken == user.Token, "user cancellation stays OperationCanceledException and joins child cleanup"); }
        }

        var existing = Root("existing-pack");
        var oldPack = Path.Combine(existing, ".git", "objects", "pack", "pack-existing.pack");
        File.WriteAllBytes(oldPack, new byte[8192]);
        watch.Restart();
        try
        {
            await Run(existing, async ct =>
            {
                while (true)
                {
                    File.SetLastWriteTimeUtc(oldPack, DateTime.UtcNow);
                    await Task.Delay(50, ct); // Neither an existing file nor changing mtime is new bytes.
                }
            });
            throw new Exception("Static existing packs must not keep a stalled transfer alive.");
        }
        catch (TimeoutException)
        { Check(watch.Elapsed < idle * 5, "an unchanged existing pack and mtime-only activity do not postpone transfer timeout"); }

        var connecting = Root("connecting");
        Directory.Delete(Path.Combine(connecting, ".git", "objects", "pack"));
        try
        {
            await Run(connecting, async ct => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return "unexpected"; });
            throw new Exception("A connection that never creates a pack should time out.");
        }
        catch (TimeoutException) { Check(true, "initial connection time is bounded before a pack directory or first byte exists"); }

        var locked = Root("preexisting-lock");
        var existingLock = Path.Combine(locked, ".git", "shallow.lock");
        File.WriteAllText(existingLock, "owned elsewhere");
        var called = false;
        try
        {
            await Run(locked, _ => { called = true; return Task.FromResult("unexpected"); });
            throw new Exception("An existing shallow lock should block transfer startup.");
        }
        catch (IOException)
        { Check(!called && File.ReadAllText(existingLock) == "owned elsewhere", "transfer monitoring never starts a fetch or changes a preexisting shallow lock"); }
        return checks;
    }
}
