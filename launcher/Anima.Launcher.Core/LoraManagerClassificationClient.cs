using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Anima.Launcher.Core;

/// <summary>Uses Manager-owned scanning and storage, including centralized sidecars.</summary>
public sealed class LoraManagerClassificationClient : IDisposable
{
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private readonly string url;
    private readonly TimeSpan timeout;
    private readonly TimeSpan pollInterval;
    public LoraManagerClassificationClient(LauncherConfig config, HttpClient? http = null,
        TimeSpan? timeout = null, TimeSpan? pollInterval = null)
    {
        this.http = http ?? new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromMinutes(5) };
        ownsHttp = http is null;
        url = config.ComfyUrl;
        this.timeout = timeout ?? TimeSpan.FromMinutes(5);
        this.pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(500);
    }

    public async Task InitializeAsync(CancellationToken ct, Action<string>? log = null)
    {
        var timer = Stopwatch.StartNew();
        var started = false;
        while (timer.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            var status = await GetAsync("/api/lm/init-status?page=checkpoints", ct, allowMissing: true);
            if (status is null) return; // Older Manager automatically initializes scanners.
            var state = Text(status.Value, "status");
            if (state is "complete" or "completed") return;
            if (state == "awaiting_first_scan" && !started)
            {
                log?.Invoke("正在完成 LoRA Manager 首次模型扫描…");
                var result = await PostAsync("/api/lm/first-scan/start", new Dictionary<string, object>(), ct);
                RequireSuccess(result, "LoRA Manager 首次扫描未能开始。");
                started = true;
            }
            else if (state == "error") throw new InvalidDataException("LoRA Manager 初始化失败：" + Text(status.Value, "details"));
            else if (state is not ("initializing" or "awaiting_first_scan"))
                throw new InvalidDataException("LoRA Manager 返回未知的初始化状态：" + state);
            await Task.Delay(pollInterval, ct);
        }
        throw new TimeoutException("LoRA Manager 模型初始化超时；已保留分类待办，可以重试。");
    }

    public async Task ClassifyAsync(PendingModelClassification model, CancellationToken ct, Action<string>? log = null)
    {
        var item = await FindAsync(model.ModelPath, ct);
        if (item is null)
        {
            log?.Invoke("正在让 LoRA Manager 扫描新下载的官方 Anima 模型…");
            var scan = await GetAsync("/api/lm/checkpoints/scan", ct);
            RequireSuccess(scan!.Value, "LoRA Manager 模型扫描未完成。");
            await InitializeAsync(ct, log);
            item = await FindAsync(model.ModelPath, ct);
        }
        if (item is null) throw new InvalidDataException("LoRA Manager 尚未收录官方 Anima 模型；请检查有效模型目录及扫描日志，分类待办已保留。");

        // This reads the effective sidecar through Manager, not a guessed adjacent path.
        // A missing/malformed sidecar must never be recreated via save-metadata: older
        // and current Managers strip file_path from updates, producing incomplete JSON.
        var metadata = await GetAsync("/api/lm/checkpoints/metadata?file_path=" + Uri.EscapeDataString(model.ModelPath.Replace('\\', '/')), ct, allowMissing: true);
        if (metadata is null || !True(metadata.Value, "success") || !metadata.Value.TryGetProperty("metadata", out var payload) || payload.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("LoRA Manager 中的模型元数据缺失或无法读取；已保留现有文件，未覆盖分类。");

        // Synchronize the existing valid sidecar into Manager's cache. An empty update
        // preserves all current fields and observes manual edits before classification.
        var path = model.ModelPath.Replace('\\', '/');
        RequireSuccess(await PostAsync("/api/lm/checkpoints/save-metadata", new { file_path = path }, ct), "LoRA Manager 无法同步模型元数据。");
        item = await FindAsync(model.ModelPath, ct) ?? throw new InvalidDataException("LoRA Manager 同步后未返回模型；分类待办已保留。");
        var classification = Text(item.Value, "base_model");
        if (IsAnima(classification)) return;
        if (!IsUnclassified(classification))
            throw new InvalidDataException($"已保留模型已有的手工分类「{classification}」；它与官方 Anima 文件不一致，未自动覆盖。");
        RequireSuccess(await PostAsync("/api/lm/checkpoints/save-metadata", new { file_path = path, base_model = "Anima" }, ct), "LoRA Manager 无法补全 Anima 分类。");
        item = await FindAsync(model.ModelPath, ct);
        if (item is null || !IsAnima(Text(item.Value, "base_model")))
            throw new InvalidDataException("LoRA Manager 尚未确认 Anima 分类；分类待办已保留。");
        log?.Invoke("LoRA Manager 已补全官方 Anima 分类，等待工作台回读确认。");
    }

    private async Task<JsonElement?> FindAsync(string modelPath, CancellationToken ct)
    {
        var found = new List<JsonElement>();
        int? expectedPages = null;
        int? expectedTotal = null;
        var total = 0;
        var pageIdentities = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 1; page <= 10000; page++)
        {
            var response = (await GetAsync($"/api/lm/checkpoints/list?page={page}&page_size=100", ct))!.Value;
            if (!response.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array
                || !response.TryGetProperty("total_pages", out var pagesValue) || pagesValue.ValueKind != JsonValueKind.Number || !pagesValue.TryGetInt32(out var pages) || pages < 0 || pages > 10000
                || items.GetArrayLength() > 100 || pages == 0 && items.GetArrayLength() > 0 || pages > 1 && items.GetArrayLength() == 0
                || expectedPages is not null && expectedPages != pages)
                throw new InvalidDataException("LoRA Manager 返回无效或正在变化的分页清单，请重试。");
            expectedPages = pages;
            if (items.GetArrayLength() > 0 && !pageIdentities.Add(items.GetRawText()))
                throw new InvalidDataException("LoRA Manager 返回重复分页，未修改模型分类。");
            if (response.TryGetProperty("total", out var totalValue))
            {
                if (totalValue.ValueKind != JsonValueKind.Number || !totalValue.TryGetInt32(out var count) || count < 0 || expectedTotal is not null && expectedTotal != count)
                    throw new InvalidDataException("LoRA Manager 模型总数在分页期间发生变化，请重试。");
                expectedTotal = count;
            }
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("LoRA Manager 返回无效模型记录。");
                if (ModelMetadata.SamePath(Text(item, "file_path"), modelPath)) found.Add(item.Clone());
            }
            total += items.GetArrayLength();
            if (page >= Math.Max(1, pages))
            {
                if (expectedTotal is not null && total != expectedTotal) throw new InvalidDataException("LoRA Manager 分页清单不完整。");
                if (found.Count > 1) throw new InvalidDataException("LoRA Manager 同一模型路径存在多条记录，未修改分类。");
                return found.Count == 0 ? null : found[0];
            }
        }
        throw new InvalidDataException("LoRA Manager 分页数量超出安全范围。");
    }

    private async Task<JsonElement?> GetAsync(string path, CancellationToken ct, bool allowMissing = false)
    {
        using var response = await http.GetAsync(url + path, ct);
        if (allowMissing && response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("LoRA Manager 返回非对象响应。");
        return document.RootElement.Clone();
    }
    private async Task<JsonElement> PostAsync(string path, object payload, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(url + path, payload, ct);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return document.RootElement.Clone();
    }
    private static void RequireSuccess(JsonElement payload, string message)
    {
        if (!True(payload, "success") && Text(payload, "status") is not ("success" or "complete" or "completed"))
            throw new InvalidDataException(message + " " + Text(payload, "error"));
    }
    internal static bool IsAnima(string? value) => string.Equals(value?.Trim(), "Anima", StringComparison.OrdinalIgnoreCase);
    internal static bool IsUnclassified(string? value) => string.IsNullOrWhiteSpace(value)
        || value.Trim().Equals("Unknown", StringComparison.OrdinalIgnoreCase);
    internal static bool True(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.True;
    internal static string Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString()! : "";
    public void Dispose() { if (ownsHttp) http.Dispose(); }
}
