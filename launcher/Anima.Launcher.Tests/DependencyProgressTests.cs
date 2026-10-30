using Anima.Launcher.Core;

internal static class DependencyProgressTests
{
    public static int Run()
    {
        var checks = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
        var clock = new ManualTime();
        var events = new List<DependencyProgress>();
        var logs = new List<string>();
        var reporter = new DependencyProgressReporter(events.Add, logs.Add, clock);
        reporter.Phase("resolving", "正在解析环境依赖", "https://user:secret@example.invalid/simple?token=secret");
        var first = events[^1];
        Check(first.Status == DependencyProgressStatus.Running && first.Source == "example.invalid"
            && first.StartedAt == clock.GetUtcNow() && first.LastOutputAt == clock.GetUtcNow(),
            "dependency phase begins with explicit activity timestamps and credential-free source label");
        clock.Advance(5);
        reporter.Log("Collecting opencv-python>=4.0");
        Check(events[^1].CurrentPackage == "opencv-python" && logs[^1] == "Collecting opencv-python>=4.0",
            "dependency activity extracts the package while retaining original pip log output");
        Check(events[^1].StartedAt == first.StartedAt && events[^1].LastOutputAt == clock.GetUtcNow()
            && events[^1].CompletedPackages is null && events[^1].TotalPackages is null,
            "resolver candidates advance heartbeat without pretending to be installed packages");
        reporter.Log("\u001b[32mDownloading numpy-2.0.0-cp311-cp311-win_amd64.whl (15.6 MB)\u001b[0m\rUsing cached numpy-1.26.4-cp311-cp311-win_amd64.whl.metadata");
        Check(events[^1].CurrentPackage == "numpy" && events[^1].Received is null && events[^1].Total is null,
            "wheel and metadata output does not invent byte progress or count resolver backtracking twice");
        reporter.Log("Building wheel for SAM-2 (pyproject.toml): started");
        Check(events[^1].CurrentPackage == "SAM-2", "source builds identify their current package");
        reporter.Log(" + pydantic==2.10.0");
        Check(events[^1].CurrentPackage == "pydantic" && events[^1].CompletedPackages is null,
            "uv package output is readable without treating individual text lines as authoritative completion");
        clock.Advance(20);
        reporter.Log("unknown future installer output");
        Check(events[^1].LastOutputAt == clock.GetUtcNow() && events[^1].CurrentPackage == "pydantic",
            "unrecognized process output is still activity and preserves the known package");
        var running = events[^1];
        Check(DependencyProgressPresentation.Create(running, clock.GetUtcNow().AddSeconds(29)).QuietNotice.Length == 0,
            "healthy recent output has no quiet-process notice");
        var quiet30 = DependencyProgressPresentation.Create(running, clock.GetUtcNow().AddSeconds(30));
        Check(quiet30.QuietNotice.Contains("等待") && quiet30.Indeterminate && quiet30.Percent == 0,
            "30 seconds without output adds informational feedback without fake progress");
        var quiet90 = DependencyProgressPresentation.Create(running, clock.GetUtcNow().AddSeconds(90));
        Check(quiet90.QuietNotice.Contains("1 分 30 秒") && quiet90.QuietNotice.Contains("仍在运行")
            && running.Status == DependencyProgressStatus.Running,
            "90 seconds without output describes the wait without failing or canceling the operation");
        var downloadWait = DependencyProgressPresentation.Create(running with { Phase = "downloading" }, clock.GetUtcNow().AddSeconds(90));
        Check(downloadWait.QuietNotice.Contains("依赖任务") && !downloadWait.QuietNotice.Contains("安装进程")
            && !quiet30.QuietNotice.Contains("安装进程"),
            "quiet network-only phases do not claim a nonexistent installation subprocess is running");
        reporter.Log("new output after a quiet interval");
        Check(DependencyProgressPresentation.Create(events[^1], clock.GetUtcNow()).QuietNotice.Length == 0,
            "new output clears a previously quiet-process notice");
        reporter.Phase("downloading", "正在下载依赖包");
        Check(events[^1].CurrentPackage.Length == 0 && events[^1].StartedAt == first.StartedAt,
            "subphase transition clears the previous package and retains total dependency elapsed time");
        reporter.Report(new("downloading", "正在下载依赖包") { Received = 1024 * 1024, Total = 2 * 1024 * 1024 });
        var bytes = DependencyProgressPresentation.Create(events[^1], clock.GetUtcNow());
        Check(!bytes.Indeterminate && bytes.Percent == 50 && bytes.Detail.Contains("MiB"),
            "authoritative byte counts provide determinate dependency progress");
        Check(!bytes.Detail.Contains("MiB/s"), "cached byte counts alone never invent a network transfer speed");
        reporter.Report(new("downloading", "正在下载依赖包")
            { Received = 1024 * 1024, Total = 2 * 1024 * 1024, BytesPerSecond = 2.5 * 1048576 });
        Check(DependencyProgressPresentation.Create(events[^1], clock.GetUtcNow()).Detail.Contains(2.5.ToString("N1") + " MiB/s"),
            "fresh authoritative aggregate download speed is shown in MiB per second");
        Check(!DependencyProgressPresentation.Create(events[^1], clock.GetUtcNow().AddSeconds(3)).Detail.Contains("MiB/s"),
            "stale transfer speed is hidden while the UI waits for fresh download activity");
        foreach (var invalid in new[] { -1d, 0d, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            reporter.Report(new("downloading", "正在下载依赖包") { BytesPerSecond = invalid });
            Check(events[^1].BytesPerSecond is null
                && !DependencyProgressPresentation.Create(events[^1], clock.GetUtcNow()).Detail.Contains("MiB/s"),
                "invalid or inactive download speed does not enter the displayed progress state");
        }
        reporter.Phase("installing", "正在安装依赖包");
        reporter.Report(new("installing", "正在安装依赖包") { CompletedPackages = 3, TotalPackages = 4, BytesPerSecond = 1048576 });
        var packages = DependencyProgressPresentation.Create(events[^1], clock.GetUtcNow());
        Check(packages.Percent == 75 && packages.Detail.Contains("3/4") && !packages.Detail.Contains("MiB"),
            "package progress stays distinct from byte progress");
        Check(events[^1].BytesPerSecond is null, "non-download stages cannot retain a previous network transfer speed");
        reporter.Report(new("verifying", "正在校验依赖") { CompletedPackages = 4, TotalPackages = 4 });
        Check(DependencyProgressPresentation.Create(events[^1], clock.GetUtcNow()).Percent == 99,
            "all packages prepared cannot claim 100 percent before dependency validation completes");
        clock.Advance(10);
        reporter.Complete("依赖已校验");
        var completed = events[^1];
        var finish = DependencyProgressPresentation.Create(completed, clock.GetUtcNow());
        Check(finish.Percent == 100 && !finish.Indeterminate && finish.QuietNotice.Length == 0,
            "confirmed completion publishes a terminal dependency progress state");
        Check(!DependencyProgressPresentation.Create(completed with { Phase = "downloading", BytesPerSecond = 1048576 }, clock.GetUtcNow()).Detail.Contains("MiB/s"),
            "terminal states never present a running download speed");
        Check(DependencyProgressPresentation.Create(completed, clock.GetUtcNow().AddHours(1)).Detail == finish.Detail,
            "completed elapsed time is frozen and cannot accumulate quiet time");
        var beforeLate = events.Count;
        reporter.Log("late process drain");
        reporter.Report(new("installing", "late running callback"));
        Check(events.Count == beforeLate && logs[^1] == "late process drain",
            "terminal progress ignores late activity while continuing to retain original logs");
        reporter.Phase("verifying", "password=hidden-value");
        Check(!events[^1].Detail.Contains("hidden-value"), "displayed dependency text follows diagnostic secret redaction");
        reporter.Pause("已暂停");
        var paused = events[^1];
        Check(DependencyProgressPresentation.Create(paused, clock.GetUtcNow().AddMinutes(4)).Heading.Contains("暂停")
            && DependencyProgressPresentation.Create(paused, clock.GetUtcNow().AddMinutes(4)).QuietNotice.Length == 0,
            "pausing has a distinct frozen terminal presentation");

        var session = new DependencyProgressSession();
        var generation = session.Begin();
        Check(session.TryReport(generation, first), "active dependency session accepts its own operation");
        session.End(DependencyProgressStatus.Paused, clock.GetUtcNow());
        Check(!session.TryReport(generation, running) && session.Current?.Status == DependencyProgressStatus.Paused,
            "callbacks cannot overwrite a task after cancellation has completed");
        var next = session.Begin();
        Check(session.Current is null && !session.TryReport(generation, completed) && session.TryReport(next, running),
            "a new task clears old activity and rejects callbacks from the previous task generation");
        session.Clear();
        Check(session.Current is null && session.TryReport(next, running),
            "leaving a dependency stage clears its display without invalidating the same install coordinator");
        session.End(DependencyProgressStatus.Failed, clock.GetUtcNow());
        Check(session.Current?.Status == DependencyProgressStatus.Failed && !session.TryReport(next, completed),
            "a failed operation cannot be changed to success by a late callback");

        var gate = new PresentationRefreshGate();
        var displayCount = 0;
        var busyGeneration = session.Begin();
        for (var ms = 0; ms < 1000; ms++)
        {
            session.TryReport(busyGeneration, running with { CurrentPackage = "package-" + ms });
            gate.Request(256);
            if (gate.Take(TimeSpan.FromMilliseconds(ms)) != 0) displayCount++;
        }
        Check(displayCount == 4 && session.Current!.CurrentPackage == "package-999",
            "burst process output retains the latest activity with at most four display refreshes per second");
        gate.SetVisible(false);
        gate.Request(256);
        Check(gate.Delay(TimeSpan.FromSeconds(2)) is null && gate.Take(TimeSpan.FromSeconds(2), force: true) == 0,
            "hidden dependency presentation does not request timer-driven refreshes");
        gate.SetVisible(true);
        Check(gate.Take(TimeSpan.FromSeconds(3), force: true) == 256 && session.Current!.CurrentPackage == "package-999",
            "returning to the window immediately presents the latest retained dependency activity");
        return checks;
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 30, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(int seconds) => now = now.AddSeconds(seconds);
    }
}
