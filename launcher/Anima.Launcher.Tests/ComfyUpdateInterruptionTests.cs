using Anima.Launcher.Core;

internal static class ComfyUpdateInterruptionTests
{
    public static async Task<int> Run(string state, RuntimeManifest runtime, string git, string source,
        LauncherConfig config, string before, string after)
    {
        var passed = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new Exception("FAIL: " + label);
            passed++; Console.WriteLine("PASS: " + label);
        }
        async Task<string> Git(params string[] args) => (await Commands.Run(git, args, config.ComfyRoot, null, default)).Trim();
        ComfyUpdateCoordinator Coordinator(bool allowFetch = true) => new(state, git, null, async (root, commit, token) =>
        {
            if (!allowFetch) throw new Exception("Recovery must not fetch any source.");
            await Commands.Run(git, ["fetch", source, commit], root, null, token);
        }, (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        var release = new ComfyRelease("v1.2.5", after, null, "https://github.com/Comfy-Org/ComfyUI/releases/tag/v1.2.5");
        var snapshot = new ComfyDependencySnapshot(after, "numpy>=1\n", "comfyui_manager==4.2.2\n", ["numpy==2.0.0"])
            { Resolved = true, EnvironmentPackages = ["numpy==1.0.0", "torch==2.11.0+cu128"] };
        var package = Path.Combine(config.ComfyRoot, ".venv", "package.txt");
        var packageBytes = File.ReadAllBytes(package);
        Task NoInstall(LauncherConfig target, ComfyDependencySnapshot plan, CancellationToken token) => Task.CompletedTask;
        Task Valid(LauncherConfig target, CancellationToken token) => Task.CompletedTask;

        using (var sourceCancellation = new CancellationTokenSource())
        {
            var updater = Coordinator();
            var applied = false;
            var recoveryValidated = false;
            updater.Progress = phase =>
            {
                if (phase != "source-checkout") return;
                sourceCancellation.Cancel();
                throw new OperationCanceledException(sourceCancellation.Token);
            };
            try
            {
                await updater.UpdateAsync(config, release, runtime, snapshot,
                    (_, _, _) => { applied = true; return Task.CompletedTask; }, Valid, sourceCancellation.Token,
                    validateRecovery: async (_, token) =>
                    {
                        recoveryValidated = true;
                        Check(token == CancellationToken.None && !token.IsCancellationRequested && await Git("rev-parse", "HEAD") == before,
                            "source-checkout cancellation validates recovery with an independent non-cancelled token");
                    });
                throw new Exception("Source-checkout cancellation expected");
            }
            catch (OperationCanceledException)
            {
                Check(!applied && recoveryValidated && updater.Previous(config) is
                    { Status: "rolled-back", Phase: "recovered", DependenciesMayHaveChanged: false, CoreVerified: true }
                    && await Git("rev-parse", "HEAD") == before && File.ReadAllBytes(package).SequenceEqual(packageBytes),
                    "cancelling at the persisted source-checkout boundary finishes recovery before returning cancellation");
            }
        }

        using (var healthCancellation = new CancellationTokenSource())
        {
            var updater = Coordinator();
            var applied = false;
            var healthStopped = false;
            var recoveryValidated = false;
            try
            {
                await updater.UpdateAsync(config, release, runtime, snapshot,
                    (_, _, _) => { applied = true; return Task.CompletedTask; },
                    (_, token) =>
                    {
                        try
                        {
                            Check(updater.Previous(config) is { Phase: "health-check", DependenciesMayHaveChanged: true },
                                "health-check cancellation occurs after a durable dependency-mutation checkpoint");
                            healthCancellation.Cancel();
                            token.ThrowIfCancellationRequested();
                            return Task.CompletedTask;
                        }
                        finally { healthStopped = true; }
                    }, healthCancellation.Token,
                    validateRecovery: async (_, token) =>
                    {
                        recoveryValidated = true;
                        Check(healthStopped && token == CancellationToken.None && !token.IsCancellationRequested
                            && await Git("rev-parse", "HEAD") == before,
                            "health-check cancellation cleans up its probe before recovery validates with an independent token");
                    });
                throw new Exception("Health-check cancellation expected");
            }
            catch (OperationCanceledException)
            {
                Check(applied && recoveryValidated && updater.Previous(config) is
                    { Status: "rolled-back", Phase: "recovered", DependenciesMayHaveChanged: true, CoreVerified: true }
                    && await Git("rev-parse", "HEAD") == before && File.ReadAllBytes(package).SequenceEqual(packageBytes),
                    "health-check cancellation reports the verified restored source and retains existing Python packages");
            }
        }

        var completed = await Coordinator().UpdateAsync(config, release, runtime, snapshot, NoInstall, Valid, default);
        foreach (var phase in new[] { "restoring-source", "restoring-configuration" })
        {
            var interrupted = false;
            var recovery = Coordinator(allowFetch: false);
            recovery.Progress = current =>
            {
                if (current != phase || interrupted) return;
                interrupted = true;
                throw new IOException("injected interrupted recovery at " + phase);
            };
            try { await recovery.RollbackAsync(config, default, Valid); throw new Exception("Recovery interruption expected"); }
            catch (IOException error) when (error.Message.StartsWith("injected interrupted recovery", StringComparison.Ordinal))
            {
                var saved = recovery.Previous(config)!;
                Check(interrupted && saved.Status == "rollback-failed" && saved.Phase == phase && !saved.CoreVerified
                    && saved.Directory == completed.Directory && saved.RecoveryRef == completed.RecoveryRef,
                    "a recovery interrupted at " + phase + " preserves its durable stage and original recovery authority");
                Check(await Git("rev-parse", "HEAD") == (phase == "restoring-source" ? after : before)
                    && await Git("rev-parse", completed.RecoveryRef) == before && File.ReadAllBytes(package).SequenceEqual(packageBytes),
                    "interruption at " + phase + " preserves the recoverable source state, Git ref and Python bytes");
            }
        }
        var restarted = Coordinator(allowFetch: false);
        await restarted.RollbackAsync(config, default, Valid);
        Check(restarted.Previous(config) is { Status: "rolled-back", Phase: "recovered", CoreVerified: true }
            && await Git("rev-parse", "HEAD") == before && File.ReadAllBytes(package).SequenceEqual(packageBytes),
            "a fresh coordinator completes recovery after two successive interruptions without downloading or reinstalling");
        return passed;
    }
}
