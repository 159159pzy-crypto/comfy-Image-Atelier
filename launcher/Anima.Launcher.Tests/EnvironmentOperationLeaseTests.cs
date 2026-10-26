using Anima.Launcher.Core;

internal static class EnvironmentOperationLeaseTests
{
    public static async Task<int> Run(string work)
    {
        var root = Path.Combine(work, "environment-lock", "ComfyUI");
        var count = 0;
        async Task Blocked(bool exclusive)
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            try { using var unexpected = await EnvironmentOperationLease.AcquireAsync(root, exclusive, cancel.Token); }
            catch (OperationCanceledException) { count++; return; }
            throw new Exception("Environment lease did not protect an active transaction.");
        }
        using (var modelOne = await EnvironmentOperationLease.AcquireAsync(root, false, default))
        using (var modelTwo = await EnvironmentOperationLease.AcquireAsync(root, false, default))
        {
            count++;
            await Blocked(true);
        }
        using (var install = await EnvironmentOperationLease.AcquireAsync(root, true, default))
        {
            count++;
            await Blocked(false);
            await Blocked(true);
        }
        using (var recovered = await EnvironmentOperationLease.AcquireAsync(root, false, default)) count++;
        Console.WriteLine($"PASS: {count} environment transaction and model concurrency lease checks");
        return count;
    }
}
