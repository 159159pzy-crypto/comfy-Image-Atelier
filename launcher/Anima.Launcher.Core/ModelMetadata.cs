using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace Anima.Launcher.Core;

public sealed record ModelMetadataResult(bool Created, string Status, string MetadataPath)
{
    public bool Queued { get; init; }
}

public sealed record PendingModelClassification(string ModelPath, long Size, string Sha256);
internal sealed record ModelClassificationJournal(int SchemaVersion, string ComfyRoot, PendingModelClassification[] Pending);

/// <summary>Classify verified official models through the running Manager's public API.</summary>
public static class ModelMetadata
{
    public const string AnimaSha256 = "bd43b7cffe1ed1153d9c41e7beb2f18cb1273eafbaa3af3edd6a173dc90a006e";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    public static string PendingPath(LauncherConfig config) => Path.Combine(Path.GetFullPath(config.ComfyRoot), ".anima-classification-pending.json");

    // Retain the call shape used by installation, individual downloads and durable-owner recovery.
    // No plugin code is imported and no sidecar path is assumed to be authoritative here.
    public static Task<ModelMetadataResult> EnsureDefaultAsync(LauncherConfig config, RuntimeManifest runtime,
        ModelManifest models, ModelProfilesManifest profiles, string modelPath, CancellationToken ct, Action<string>? log = null)
        => EnsureDefaultCoreAsync(config, models, profiles, modelPath, VerifyFileAsync, ct, log);

    internal static async Task<ModelMetadataResult> EnsureDefaultCoreAsync(LauncherConfig config, ModelManifest models,
        ModelProfilesManifest profiles, string modelPath, Func<PendingModelClassification, CancellationToken, Task<FileStream>> verify,
        CancellationToken ct, Action<string>? log = null)
    {
        ct.ThrowIfCancellationRequested();
        var path = Path.GetFullPath(modelPath);
        var sidecar = Path.ChangeExtension(path, ".metadata.json");
        ModelMetadataResult Result(string status, bool queued = false)
        {
            log?.Invoke(status);
            return new(false, status, sidecar) { Queued = queued };
        }
        var profile = profiles.Get("anima");
        var model = models.Models.SingleOrDefault(m => m.Category == "diffusion_models" && m.RelativePath == profile.ModelName);
        if (model is null || !model.CanDownload || !model.Sha256.Equals(AnimaSha256, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(path).Equals(Path.GetFileName(model.RelativePath), StringComparison.OrdinalIgnoreCase))
            return Result("模型未匹配已锁定的官方 Anima 底模，保留原有分类。");

        // An adjacent sidecar may be stale when Manager uses centralized storage.
        // It neither authorizes nor suppresses classification: the running Manager
        // alone identifies its effective, valid metadata and preserves explicit edits.
        await using var input = await verify(new(path, model.Size, model.Sha256), ct);
        await RegisterPendingAsync(config, new(path, model.Size, model.Sha256.ToLowerInvariant()), ct);
        return Result("官方 Anima 底模已校验；已保存分类待办，启动后由 LoRA Manager 完成。", true);
    }

    internal static async Task<FileStream> VerifyFileAsync(PendingModelClassification item, CancellationToken ct)
    {
        var input = new FileStream(item.ModelPath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        try
        {
            if (input.Length != item.Size || !Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("默认 Anima 底模的大小或 SHA-256 与已验证清单不符，未修改模型分类。");
            return input;
        }
        catch { await input.DisposeAsync(); throw; }
    }

    internal static async Task RegisterPendingAsync(LauncherConfig config, PendingModelClassification item, CancellationToken ct)
    {
        var file = PendingPath(config);
        var gate = Gates.GetOrAdd(file, _ => new(1));
        await gate.WaitAsync(ct);
        try
        {
            var pending = ReadPending(config).Where(value => !SamePath(value.ModelPath, item.ModelPath)).Append(item).ToArray();
            JsonFile.Write(file, new ModelClassificationJournal(1, Path.GetFullPath(config.ComfyRoot), pending));
        }
        finally { gate.Release(); }
    }

    public static PendingModelClassification[] ReadPending(LauncherConfig config)
    {
        var file = PendingPath(config);
        if (!File.Exists(file)) return [];
        var journal = JsonFile.Read<ModelClassificationJournal>(file);
        if (journal.SchemaVersion != 1 || !SamePath(journal.ComfyRoot, config.ComfyRoot) || journal.Pending is null
            || journal.Pending.Any(item => item is null || !Path.IsPathFullyQualified(item.ModelPath) || item.Size <= 0
                || !string.Equals(item.Sha256, AnimaSha256, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(item.ModelPath).Equals("anima-base-v1.0.safetensors", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("模型分类待办身份无效；原文件已保留，请重新检查安装任务。");
        return journal.Pending;
    }

    /// <summary>Call after ComfyUI starts, before WebUI starts. Pending work survives this call.</summary>
    public static async Task SynchronizePendingAsync(LauncherConfig config, CancellationToken ct, Action<string>? log = null)
    {
        using var client = new LoraManagerClassificationClient(config);
        await SynchronizePendingCoreAsync(config, client, VerifyFileAsync, ct, log);
    }

    internal static async Task SynchronizePendingCoreAsync(LauncherConfig config, LoraManagerClassificationClient client,
        Func<PendingModelClassification, CancellationToken, Task<FileStream>> verify, CancellationToken ct, Action<string>? log = null)
    {
        var pending = ReadPending(config);
        if (pending.Length == 0) return;
        await client.InitializeAsync(ct, log);
        foreach (var item in pending)
        {
            await using var input = await verify(item, ct);
            await client.ClassifyAsync(item, ct, log);
        }
    }

    /// <summary>Call after WebUI starts. Only its authoritative classification clears a durable pending item.</summary>
    public static async Task ConfirmPendingAsync(LauncherConfig config, CancellationToken ct, Action<string>? log = null)
    {
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(45) };
        await ConfirmPendingCoreAsync(config, http, ct, log);
    }

    internal static async Task ConfirmPendingCoreAsync(LauncherConfig config, HttpClient http, CancellationToken ct, Action<string>? log = null)
    {
        var pending = ReadPending(config);
        if (pending.Length == 0) return;
        using var response = await http.GetAsync(config.WebUrl + "/api/resources?model_profile=anima&refresh=true", ct);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        foreach (var item in pending) ValidateClassification(document.RootElement, item);
        var file = PendingPath(config);
        var gate = Gates.GetOrAdd(file, _ => new(1));
        await gate.WaitAsync(ct);
        try
        {
            var remaining = ReadPending(config).Where(item => !pending.Any(done => done == item)).ToArray();
            JsonFile.Write(file, new ModelClassificationJournal(1, Path.GetFullPath(config.ComfyRoot), remaining));
        }
        finally { gate.Release(); }
        log?.Invoke("工作台已确认官方 Anima 模型分类；分类待办已完成。");
    }

    internal static void ValidateClassification(JsonElement resources, PendingModelClassification pending)
    {
        if (!resources.TryGetProperty("model_items", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("工作台未返回模型分类清单；分类待办已保留。");
        var matches = items.EnumerateArray().Where(item => item.TryGetProperty("file_path", out var path)
            && path.ValueKind == JsonValueKind.String && SamePath(path.GetString()!, pending.ModelPath)).ToArray();
        if (matches.Length != 1 || !matches[0].TryGetProperty("model_profile", out var profile) || profile.ValueKind != JsonValueKind.String || profile.GetString() != "anima"
            || !matches[0].TryGetProperty("classification_status", out var status) || status.ValueKind != JsonValueKind.String || status.GetString() != "recognized")
            throw new InvalidDataException("工作台尚未识别已校验的 Anima 底模；分类待办已保留，请重试运行检查。");
    }

    internal static bool SamePath(string first, string second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)) return false;
        try { return Path.GetFullPath(first.Replace('/', Path.DirectorySeparatorChar)).TrimEnd(Path.DirectorySeparatorChar)
            .Equals(Path.GetFullPath(second.Replace('/', Path.DirectorySeparatorChar)).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return false; }
    }
}
