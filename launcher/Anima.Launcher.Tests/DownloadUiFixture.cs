using System.Net;
using Anima.Launcher.Core;

internal static class DownloadUiFixture
{
    public static async Task Seed(string state, string repository, ModelManifest manifest, bool longError = false)
    {
        if (Directory.Exists(state) && Directory.EnumerateFileSystemEntries(state).Any()) throw new IOException("UI fixture requires a new empty state directory.");
        Directory.CreateDirectory(state);
        var environment = Path.Combine(state, "fixture-environment");
        Directory.CreateDirectory(environment);
        await File.WriteAllTextAsync(Path.Combine(environment, "main.py"), "# Isolated UI fixture; not a runnable ComfyUI environment.\n");
        await File.WriteAllTextAsync(Path.Combine(environment, "folder_paths.py"), "# Isolated UI fixture layout marker; not a runnable ComfyUI environment.\n");
        var config = new LauncherConfig { ComfyRoot = environment, AppRoot = repository, DataDir = Path.Combine(state, "data"),
            SetupComplete = true, AutoCheckUpdates = false, StartOnLaunch = false, NetworkMode = "direct", ComfyPort = 19892, WebPort = 19894 };
        JsonFile.Write(Path.Combine(state, "launcher.json"), config);
        using var handler = new WaitingHandler();
        using var downloads = new Downloads(handler: handler);
        await using (var tasks = new DownloadTaskService(downloads, state))
        {
            var model = manifest.Models.First(m => m.CanDownload && !m.IsPackage && m.Size > 1073741824);
            var destination = Path.Combine(environment, "models", model.Category, model.RelativePath);
            var owner = tasks.RegisterOwner("models", "隔离界面验收 · 暂停恢复示例", config, [new(model, destination)]);
            var run = tasks.RunOwnerAsync(owner.Id, ct => downloads.Fetch(model.Name, model.Urls, destination, model.Size, model.Sha256, null, ct));
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await tasks.PauseOwnerAsync(owner.Id);
            try { await run; } catch (OperationCanceledException) { }
        }
        if (longError)
        {
            var store = new DownloadTaskStore(state);
            var snapshot = store.Load();
            const string error = "隔离界面验收注入的长错误：来源在传输过程中改变了文件标识，检查点已保留。请检查目标目录、文件身份与可用空间，选择按原设置继续或使用新来源。此消息仅用于验证窄窗口中的换行、滚动和键盘操作，不代表真实模型下载失败。";
            foreach (var item in snapshot.Tasks) { item.Status = DownloadTaskStatus.NeedsReview; item.Error = error; }
            foreach (var owner in snapshot.Owners) { owner.Status = DownloadTaskStatus.NeedsReview; owner.Error = error; }
            store.Save(snapshot);
        }
        Console.WriteLine("UI FIXTURE READY " + state);
    }
    private sealed class WaitingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new(HttpStatusCode.ServiceUnavailable);
        }
    }
}
