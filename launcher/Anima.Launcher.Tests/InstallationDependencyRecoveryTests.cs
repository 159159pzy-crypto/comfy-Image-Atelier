using System.Diagnostics;
using System.Text.Json;
using Anima.Launcher.Core;

internal static class InstallationDependencyRecoveryTests
{
    public static async Task<bool> TryHandleFixture(string[] args)
    {
        if (args is not ["--interrupt-node-fixture", var directory]) return false;
        var config = JsonFile.Read<LauncherConfig>(Path.Combine(directory, "fixture-config.json"));
        var path = InstallationDependencyRecovery.CheckpointPath(config.ComfyRoot, new string('a', 40), directory);
        var saved = JsonFile.Read<ComfyDependencySnapshot>(path);
        await InstallationDependencyRecovery.CheckpointAsync(config, saved, path, async () =>
        {
            await Commands.Run(config.Python, ["-B", Path.Combine(directory, "install.py")], directory, null, default);
            Environment.Exit(89); // Terminate the installer without executing its finally block.
        }, _ => { });
        return true;
    }

    public static async Task<int> Run(string work)
    {
        var count = 0;
        void Check(bool result, string message)
        {
            if (!result) throw new Exception(message);
            count++;
            Console.WriteLine("PASS: " + message);
        }
        var directory = Directory.CreateDirectory(Path.Combine(work, "node interruption")).FullName;
        var root = Directory.CreateDirectory(Path.Combine(directory, "comfy")).FullName;
        var venv = Path.Combine(root, ".venv");
        var host = Commands.Find("python.exe") ?? throw new InvalidOperationException("Python required for the installer crash fixture.");
        await Commands.Run(host, ["-m", "venv", "--without-pip", venv], directory, null, default);
        var config = new LauncherConfig { ComfyRoot = root, Python = Path.Combine(venv, "Scripts", "python.exe") };
        JsonFile.Write(Path.Combine(directory, "fixture-config.json"), config);
        File.WriteAllText(Path.Combine(directory, "install.py"), """
            import sysconfig
            from pathlib import Path
            metadata = Path(sysconfig.get_path('purelib')) / 'httpcore-1.0.dist-info'
            metadata.mkdir()
            (metadata / 'METADATA').write_text('Metadata-Version: 2.1\nName: httpcore\nVersion: 1.0\n')
            """);
        var (identity, baseline, _) = await DependencyPlans.ReadEnvironmentAsync(config, default);
        var initial = new ComfyDependencySnapshot(new string('a', 40), "", "", []);
        var saved = initial with { PlanVersion = 1, Resolved = true, PythonIdentity = identity, EnvironmentPackages = baseline };
        var path = InstallationDependencyRecovery.CheckpointPath(root, initial.ComfyCommit, directory);
        JsonFile.Write(path, saved);
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(typeof(InstallationDependencyRecoveryTests).Assembly.Location);
        start.ArgumentList.Add("--interrupt-node-fixture");
        start.ArgumentList.Add(directory);
        using var child = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await child.WaitForExitAsync(timeout.Token);
        Check(child.ExitCode == 89 && File.Exists(path + ".active.json"), "a terminated node installer leaves a durable active checkpoint");
        var plan = new InstallationPlan(JsonSerializer.Serialize(config, JsonFile.Options), root, directory, "anima", true, 0, 0, 0, []) { Dependencies = initial };
        var approved = await InstallationDependencyRecovery.ReviewAsync(plan, directory, default);
        var approval = approved.ReviewedDependencyAdditions!;
        Check(approval.Packages.SequenceEqual(["httpcore==1.0"]) && approved.DependencyReviewIssues.Single().Contains("httpcore==1.0")
            && JsonFile.Read<ComfyDependencySnapshot>(path).EnvironmentPackages.SequenceEqual(baseline),
            "fresh review displays exact newly installed packages without changing the old receipt");
        var current = await DependencyPlans.InventoryAsync(config, default);
        var accepted = InstallationDependencyRecovery.Apply(saved, approval, current);
        Check(accepted.EnvironmentPackages.Contains("httpcore==1.0"), "continuing the reviewed plan adopts only the acknowledged addition");
        try
        {
            InstallationDependencyRecovery.Apply(saved, approval, [.. current, "unreviewed==2"]);
            throw new Exception("unexpected package after review was accepted");
        }
        catch (InvalidOperationException) { Check(true, "environment changes between review and execution require another review"); }
        await InstallationDependencyRecovery.CheckpointAsync(config, accepted, path, () => Task.CompletedTask, _ => { });
        Check(!File.Exists(path + ".active.json") && InstallationDependencyRecovery.Review(saved, baseline) is null
            && InstallationDependencyRecovery.Apply(JsonFile.Read<ComfyDependencySnapshot>(path), approval, current).EnvironmentPackages.SequenceEqual(current),
            "the resumed checkpoint closes cleanly and replay is idempotent");
        File.WriteAllText(Path.Combine(directory, "failing-node.py"), File.ReadAllText(Path.Combine(directory, "install.py"))
            .Replace("httpcore", "node_extra", StringComparison.Ordinal) + "\nraise RuntimeError('fixture node failed')\n");
        try
        {
            await InstallationDependencyRecovery.CheckpointAsync(config, accepted, path, async () =>
                await Commands.Run(config.Python, ["-B", Path.Combine(directory, "failing-node.py")], directory, null, default), _ => { });
            throw new Exception("expected node install failure");
        }
        catch (CommandFailureException error) when (error.Message.Contains("fixture node failed")) { }
        var afterFailure = JsonFile.Read<ComfyDependencySnapshot>(path);
        await InstallationDependencyRecovery.CheckpointAsync(config, afterFailure, path, () => Task.CompletedTask, _ => { });
        Check(afterFailure.EnvironmentPackages.Contains("node_extra==1.0") && !File.Exists(path + ".active.json"),
            "a failing node that installs a dependency records its additions and retries without a new solve");

        var external = config.Clone();
        external.ExternalEnvironmentMode = "repair";
        external.RuntimeRoot = Path.Combine(directory, "external-runtime");
        var repair = plan with { Fresh = false, ExternalRepairBackupId = Guid.NewGuid().ToString("N"), RuntimeRoot = external.RuntimeRoot,
            ConfigurationJson = JsonSerializer.Serialize(external, JsonFile.Options) };
        var node = new Repository("fixture", "https://github.com/test/fixture.git", new string('b', 40), []);
        var existingNode = Path.Combine(root, "custom_nodes", node.Id);
        var missingNode = Path.Combine(root, "custom_nodes", "missing");
        Directory.CreateDirectory(existingNode);
        File.WriteAllText(Path.Combine(existingNode, "settings.txt"), "original");
        File.WriteAllText(Path.Combine(root, ".anima-constraints.txt"), "fixture==1");
        NodeInstallPlan[] nodes = [new(node, existingNode, true), new(node with { Id = "missing" }, missingNode, false)];
        var volume = DownloadDiskSpace.VolumeRoot(directory);
        try
        {
            await ExternalRepairBackup.EnsureAsync(repair, nodes, [new(volume, long.MaxValue / 2, 0)], _ => { }, default);
            throw new Exception("insufficient backup disk should block repair");
        }
        catch (IOException error) when (error.Message.Contains("修复备份需")) { Check(true, "backup space admission includes room for the reviewed install"); }
        var backup = await ExternalRepairBackup.EnsureAsync(repair, nodes, [], _ => { }, default);
        Check(File.ReadAllText(Path.Combine(backup, "configuration", ".anima-constraints.txt")) == "fixture==1"
            && File.Exists(Path.Combine(backup, "python", "Scripts", "python.exe"))
            && File.ReadAllText(Path.Combine(backup, "恢复说明.txt")).Contains("原先不存在：" + missingNode),
            "external repair saves the complete private Python, configuration and original absence of selected nodes");
        File.WriteAllText(Path.Combine(existingNode, "settings.txt"), "changed by repair");
        Directory.CreateDirectory(missingNode);
        var reused = await ExternalRepairBackup.EnsureAsync(repair, nodes, [], _ => { }, default);
        Check(reused == backup && File.ReadAllText(Path.Combine(backup, "nodes", "fixture", "settings.txt")) == "original"
            && !Directory.Exists(Path.Combine(backup, "nodes", "missing")), "retry retains the first verified pre-repair backup");
        external.ExternalEnvironmentMode = "readOnly";
        var readOnly = repair with { ConfigurationJson = JsonSerializer.Serialize(external, JsonFile.Options), ExternalRepairBackupId = "" };
        Check(ExternalRepairBackup.Preview(readOnly) == "" && ExternalRepairBackup.Preview(repair).Contains(backup),
            "only explicit repair advertises an environment backup and its exact location");
        return count;
    }
}
