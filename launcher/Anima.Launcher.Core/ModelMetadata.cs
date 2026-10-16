using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Anima.Launcher.Core;

public sealed record ModelMetadataResult(bool Created, string Status, string MetadataPath);

/// <summary>Supply the classification absent from the official Anima safetensors header.</summary>
public static class ModelMetadata
{
    public const string ManagerCommit = "186ef4da786a039ee2a429508c93f5bffaedf1e3";
    public const string AnimaSha256 = "bd43b7cffe1ed1153d9c41e7beb2f18cb1273eafbaa3af3edd6a173dc90a006e";

    /// <summary>
    /// Only the manifest's verified default Anima model is eligible. Existing metadata,
    /// including malformed user files and Civitai sidecars, is never overwritten.
    /// Call after downloads and before the first ComfyUI scan.
    /// </summary>
    public static async Task<ModelMetadataResult> EnsureDefaultAsync(LauncherConfig config, RuntimeManifest runtime,
        ModelManifest models, ModelProfilesManifest profiles, string modelPath, CancellationToken ct, Action<string>? log = null)
    {
        ct.ThrowIfCancellationRequested();
        var path = Path.GetFullPath(modelPath);
        var sidecar = Path.ChangeExtension(path, ".metadata.json");
        ModelMetadataResult Result(bool created, string status)
        {
            log?.Invoke(status);
            return new(created, status, sidecar);
        }

        var profile = profiles.Get(config.SelectedProfile);
        if (profile.Id != "anima") return Result(false, "此模型档案不需要自动补全 Anima 分类。");
        var model = models.Models.SingleOrDefault(m => m.Category == "diffusion_models" && m.RelativePath == profile.ModelName);
        if (model is null || !model.CanDownload || !model.Sha256.Equals(AnimaSha256, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(path).Equals(Path.GetFileName(model.RelativePath), StringComparison.OrdinalIgnoreCase))
            return Result(false, "模型未匹配已锁定的官方 Anima 底模，保留原有分类。");

        bool HasExisting() => File.Exists(sidecar) || Directory.Exists(sidecar)
            || File.Exists(Path.ChangeExtension(path, ".civitai.info")) || File.Exists(path + ".civitai.info");
        if (HasExisting()) return Result(false, "保留已有 Anima 模型元数据。");

        // Retain the read handle through publication, preventing Windows writers from
        // replacing a model between its SHA check and classification.
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        if (input.Length != model.Size || !Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).Equals(model.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("默认 Anima 底模的大小或 SHA-256 与清单不符，未写入模型分类。");

        var manager = runtime.Nodes.SingleOrDefault(n => n.Id == "ComfyUI-Lora-Manager");
        if (manager?.Commit != ManagerCommit) throw new InvalidDataException("LoRA Manager 清单版本不支持自动补全模型分类。");
        var managerRoot = Safety.Under(Path.Combine(config.ComfyRoot, "custom_nodes"), manager.Id);
        var json = await Commands.Run(config.Python, ["-I", "-B", "-X", "utf8", "-c", BuildMetadataScript, managerRoot, path, model.Sha256],
            config.ComfyRoot, null, ct, timeout: TimeSpan.FromSeconds(30));
        using var document = JsonDocument.Parse(json);
        var metadata = document.RootElement;
        if (metadata.GetProperty("base_model").GetString() != "Anima"
            || metadata.GetProperty("sha256").GetString() != model.Sha256.ToLowerInvariant()
            || metadata.GetProperty("size").GetInt64() != model.Size
            || !Path.GetFullPath(metadata.GetProperty("file_path").GetString()!).Equals(path, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("LoRA Manager 返回的模型元数据与已验证文件不符。");

        ct.ThrowIfCancellationRequested();
        if (HasExisting()) return Result(false, "保留已有 Anima 模型元数据。");
        var temporary = sidecar + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, json, new UTF8Encoding(false), ct);
            ct.ThrowIfCancellationRequested();
            if (HasExisting()) return Result(false, "保留已有 Anima 模型元数据。");
            try { File.Move(temporary, sidecar, false); }
            catch (IOException) when (File.Exists(sidecar)) { return Result(false, "保留已有 Anima 模型元数据。"); }
            return Result(true, "官方 Anima 底模已校验，并补全 LoRA Manager 的 Anima 分类。");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    // These are the canonical LF source hashes from the pinned official commit.
    // Execute only these two stdlib-only modules; loading the plugin entrypoint
    // would start ComfyUI services and mutate its settings during installation.
    public const string BuildMetadataScript = """
        import hashlib
        import json
        import pathlib
        import sys
        import types

        plugin_root, model_path, verified_sha = sys.argv[1:]
        sources = {
            'model_utils': '5522d56844b56443405b32a6dd6170e708127ba1f8c80f6987b54e0da42ed3a5',
            'models': '3abc6a2deb70490a04d0352cccae421fc6eb47fb87a4f98f2a36f52c4a7018d3',
        }
        package_name = '_anima_launcher_metadata'
        package = types.ModuleType(package_name)
        package.__path__ = []
        sys.modules[package_name] = package
        for module_name, expected in sources.items():
            source_path = pathlib.Path(plugin_root) / 'py' / 'utils' / (module_name + '.py')
            source = source_path.read_bytes().replace(b'\r\n', b'\n')
            if hashlib.sha256(source).hexdigest() != expected:
                raise RuntimeError('LoRA Manager source differs from locked revision: ' + str(source_path))
            name = package_name + '.' + module_name
            module = types.ModuleType(name)
            module.__file__ = str(source_path)
            module.__package__ = package_name
            sys.modules[name] = module
            exec(compile(source, str(source_path), 'exec'), module.__dict__)

        from _anima_launcher_metadata.models import CheckpointMetadata
        path = pathlib.Path(model_path).absolute()
        stat = path.stat()
        metadata = CheckpointMetadata(
            file_name=path.stem, model_name='Anima', file_path=path.as_posix(),
            size=stat.st_size, modified=stat.st_mtime, sha256=verified_sha.lower(),
            base_model='Anima', preview_url='', tags=[], modelDescription='',
            sub_type='diffusion_model', from_civitai=False, hash_status='completed',
        )
        payload = metadata.to_dict()
        assert CheckpointMetadata.from_dict(payload).base_model == 'Anima'
        print(json.dumps(payload, ensure_ascii=False, indent=2))
        """;
}
