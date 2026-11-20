using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Anima.Launcher.Core;

public static class WorkbenchTestGeneration
{
    public static async Task<Uri> RunAsync(LauncherConfig config, Action<string> progress, CancellationToken ct)
    {
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
            { BaseAddress = new Uri(config.WebUrl), Timeout = TimeSpan.FromSeconds(30) };
        var health = await http.GetFromJsonAsync<JsonElement>("/api/launcher-health", ct);
        if (!ServiceManager.IsOurWebUi(health, config))
            throw new InvalidOperationException("当前工作台与选定环境不一致，请重新检查运行状态。");
        using var response = await http.PostAsJsonAsync("/api/launcher-test-generation", new { model_profile = config.SelectedProfile }, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException("此工作台版本尚不支持试生成，请先更新工作台。");
        if (response.StatusCode == HttpStatusCode.Conflict)
            throw new InvalidOperationException("已有任务正在运行或等待。请在队列空闲后试生成。");
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            throw new InvalidOperationException(body.TryGetProperty("error", out var error) ? error.GetString() : "试生成请求失败。");
        }
        var submission = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var id = submission.GetProperty("id").GetString();
        if (string.IsNullOrEmpty(id)) throw new InvalidDataException("工作台没有返回试生成批次。");
        progress("正在试生成一张 512 × 512 图片…");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(20));
        try
        {
            while (true)
            {
                var batch = await http.GetFromJsonAsync<JsonElement>("/api/batches/" + Uri.EscapeDataString(id), timeout.Token);
                if (batch.GetProperty("id").GetString() != id) throw new InvalidDataException("工作台返回了其他批次。");
                var status = batch.GetProperty("status").GetString();
                if (status == "completed")
                {
                    var images = batch.GetProperty("image_ids");
                    if (images.GetArrayLength() == 0) throw new IOException("试生成完成，但工作台没有保存图片。");
                    var uri = new Uri(http.BaseAddress, "/api/images/" + images[0].GetInt64());
                    using var stream = await http.GetStreamAsync(uri, timeout.Token);
                    var header = new byte[24];
                    await stream.ReadExactlyAsync(header, timeout.Token);
                    if (!header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
                        || BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16)) != 512
                        || BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20)) != 512)
                        throw new InvalidDataException("试生成输出不是预期的 512 × 512 PNG 图片。");
                    return uri;
                }
                if (status is not ("running" or "stopping"))
                    throw new IOException($"试生成未完成（{status}）。" + batch.GetProperty("error").GetString());
                await Task.Delay(TimeSpan.FromSeconds(1), timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        { throw new TimeoutException("等待试生成结果超时，可在工作台查看此批次。"); }
    }
}
