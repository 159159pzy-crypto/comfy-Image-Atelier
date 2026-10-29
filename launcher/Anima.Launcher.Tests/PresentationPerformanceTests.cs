using Anima.Launcher.Core;
using System.Text.Json;

internal static class PresentationPerformanceTests
{
    public static int Run(string work)
    {
        var checks = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks++; }
        var logs = new BoundedLogBuffer();
        for (var i = 0; i < 2100; i++) logs.Add("line " + i);
        Check(logs.Count == 2000 && logs.Snapshot()[0] == "line 100", "log line bound preserves newest tail");
        var clear = logs.Sequence;
        logs.Add("after clear");
        Check(logs.Snapshot(clear).SequenceEqual(["after clear"]), "display clear cursor excludes older log lines");
        logs.Clear();
        var longLine = new string('x', 19997) + "😀" + new string('y', 20000);
        logs.Add(longLine);
        Check(logs.Snapshot()[0].Length <= 20000 && !logs.Snapshot()[0].Contains('\ud83d'), "line truncation avoids splitting a surrogate");
        Parallel.For(0, 100000, i => logs.Add(i % 2 == 0 ? longLine : "short"));
        var tail = logs.Snapshot();
        Check(logs.Count <= 2000 && logs.Characters <= 240000 && string.Join("\r\n", tail).Length <= 240000,
            "100000 concurrent log appends stay within both bounds before joining");
        Check(logs.Drain().Length == tail.Length && logs.Count == 0 && logs.Characters == 0, "drain releases buffered lines");

        var multilineLogs = new BoundedLogBuffer();
        multilineLogs.Add(string.Join('\n', Enumerable.Range(0, 2005).Select(i => "line " + i)));
        Check(multilineLogs.Count == 2000 && multilineLogs.Sequence == 2005
            && multilineLogs.Snapshot()[0] == "line 5" && multilineLogs.Snapshot()[^1] == "line 2004",
            "one multiline entry obeys the 2000 logical-line bound while preserving every line sequence");
        Check(multilineLogs.Capture().Offset == 40,
            "multiline eviction advances absolute offsets using normalized CRLF separators");
        var mixedNewlines = new BoundedLogBuffer();
        mixedNewlines.Add("a\r\nb\rc\nd\r\n");
        Check(mixedNewlines.Sequence == 5 && mixedNewlines.Count == 5
            && mixedNewlines.Snapshot().SequenceEqual(["a", "b", "c", "d", ""])
            && mixedNewlines.Capture().Text == "a\r\nb\r\nc\r\nd\r\n",
            "CRLF is one logical separator and mixed CR/LF preserve empty trailing lines");
        Check(mixedNewlines.Characters == 14, "character budget includes normalized separators for every logical line");
        var mixedCursor = mixedNewlines.Sequence;
        mixedNewlines.Clear();
        mixedNewlines.Add("after");
        Check(mixedNewlines.Capture(mixedCursor) == new BoundedLogSnapshot("after", 14) && mixedNewlines.Sequence == 6,
            "clear keeps normalized absolute offsets and sequence monotonic after multiline input");
        var longMultiline = new BoundedLogBuffer();
        longMultiline.Add(longLine + "\r\n" + longLine + "\nlast");
        var longParts = longMultiline.Snapshot();
        Check(longParts.Length == 3 && longParts[0].Length <= 20000 && longParts[1].Length <= 20000
            && !longParts[0].Contains('\ud83d') && longParts[^1] == "last",
            "each long logical line is truncated without losing later lines or splitting surrogates");
        Check(longMultiline.Capture(after: 2) == new BoundedLogSnapshot("last", longParts[0].Length + longParts[1].Length + 4),
            "absolute offsets use retained truncated text rather than original oversized lines");
        longMultiline.Add(string.Join('\r', Enumerable.Repeat(longLine, 30)));
        Check(longMultiline.Count <= 2000 && longMultiline.Characters <= 240000 && longMultiline.Capture().Text.Length <= 240000,
            "a single oversized multiline append obeys the total character budget before joining");

        var selectionLogs = new BoundedLogBuffer();
        for (var i = 0; i < 2000; i++) selectionLogs.Add("repeat");
        var beforeTrim = selectionLogs.Capture();
        selectionLogs.Add("next");
        var afterTrim = selectionLogs.Capture();
        Check(afterTrim.RebaseSelection(beforeTrim, 16, 6) == (8, 6), "log selection follows retained characters even with duplicate lines");
        Check(afterTrim.RebaseSelection(beforeTrim, 0, 12) == (0, 4), "partly trimmed log selection retains only surviving characters");
        Check(afterTrim.RebaseSelection(beforeTrim, 0, 3) == (0, 0), "fully trimmed log selection collapses safely");
        selectionLogs.Add("tail");
        var appended = selectionLogs.Capture(after: selectionLogs.Sequence - 1);
        Check(appended.Text == "tail" && appended.Offset > afterTrim.Offset, "clear-view snapshots preserve absolute character positions");

        var multilineSelection = new BoundedLogBuffer();
        multilineSelection.Add(string.Join("\r\n", Enumerable.Repeat("repeat", 2000)));
        var multilineBefore = multilineSelection.Capture();
        multilineSelection.Add("first\rsecond");
        var multilineAfter = multilineSelection.Capture();
        Check(multilineAfter.Offset == 16 && multilineAfter.RebaseSelection(multilineBefore, 32, 6) == (16, 6)
            && multilineAfter.RebaseSelection(multilineBefore, 8, 12) == (0, 4)
            && multilineAfter.RebaseSelection(multilineBefore, 0, 7) == (0, 0),
            "multiline eviction preserves surviving duplicate-text selections and safely clips removed selections");
        Check(multilineSelection.Capture(after: 2000) == new BoundedLogSnapshot("first\r\nsecond", 16000),
            "multiline clear-view cursors select complete logical lines with stable absolute positions");

        var database = new DownloadTaskDatabase();
        for (var i = 0; i < 1000; i++) database.Tasks.Add(new() { Id = "history-" + i, Status = DownloadTaskStatus.Completed, Size = 1000, Received = 1000 });
        database.Tasks.Add(new() { Id = "active-a", Status = DownloadTaskStatus.Running, Size = 10000 });
        database.Tasks.Add(new() { Id = "active-b", Status = DownloadTaskStatus.Running, Size = 10000 });
        var gate = new PresentationRefreshGate();
        var times = new List<int>();
        DownloadTaskDatabase? displayed = null;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        // 100 progress notifications per second; all presenters reuse one snapshot per batch.
        for (var ms = 0; ms < 10000; ms += 10)
        {
            database.Tasks[^1].Received = ms;
            database.Tasks[^2].Received = ms / 2;
            gate.Request(1);
            if (gate.Take(TimeSpan.FromMilliseconds(ms)) == 0) continue;
            displayed = JsonSerializer.Deserialize<DownloadTaskDatabase>(JsonSerializer.Serialize(database, JsonFile.Options), JsonFile.Options)!;
            times.Add(ms);
        }
        Check(times.GroupBy(ms => ms / 1000).All(group => group.Count() <= 4), "1000 history entries and burst progress use at most 4 snapshots per second");
        Check(times.Zip(times.Skip(1)).All(pair => pair.Second - pair.First >= 250), "refresh interval is maintained across second boundaries");
        Check(displayed!.Tasks.Count == 1002 && database.Tasks[^1].Received == 9990, "display sampling never changes authoritative progress");
        database.Tasks[^1].Status = DownloadTaskStatus.Completed; gate.Request(1);
        Check(gate.Take(TimeSpan.FromSeconds(10)) == 1, "terminal state is delivered at next due refresh");
        gate.SetVisible(false); gate.Request(1);
        Check(gate.Take(TimeSpan.FromSeconds(20), force: true) == 0 && gate.Delay(TimeSpan.FromSeconds(20)) is null,
            "hidden windows neither refresh nor schedule display snapshots");
        gate.SetVisible(true);
        Check(gate.Take(TimeSpan.FromSeconds(20), force: true) == 1, "restore delivers the latest retained invalidation immediately");
        gate.Request(1 | 2);
        Check(gate.Take(TimeSpan.FromSeconds(20), force: true, mask: ~1) == 2, "derived display events can be consumed separately");
        Check(gate.Take(TimeSpan.FromMilliseconds(20250)) == 1, "a concurrent final download event is not swallowed by derived events");
        var report = new { checks, historyTasks = 1000, concurrentTasks = 2, notificationsPerSecond = 100,
            simulatedSeconds = 10, snapshots = times.Count, snapshotAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore,
            maxLogCharacters = BoundedLogBuffer.MaxCharacters, maxLogLines = BoundedLogBuffer.MaxLines };
        JsonFile.Write(Path.Combine(work, "presentation-performance.json"), report);
        return checks;
    }
}
