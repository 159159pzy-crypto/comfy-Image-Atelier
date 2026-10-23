using System.Collections.Immutable;

namespace Anima.Launcher.Core;

public sealed record SetupModelChoice(string Id, string Label, string Description,
    ImmutableArray<string> ModelIds, long Size, bool Recommended = false);

/// <summary>Feature choices are optional; each generation profile keeps its complete required set.</summary>
public static class SetupModelSelection
{
    public static ImmutableArray<SetupModelChoice> Choices(ModelManifest models, ModelProfilesManifest profiles, string profile)
    {
        var required = profiles.RequiredModels(models, profile).Select(model => model.Id).ToHashSet(StringComparer.Ordinal);
        var byId = models.Models.ToDictionary(model => model.Id, StringComparer.Ordinal);
        var choices = ImmutableArray.CreateBuilder<SetupModelChoice>();
        void Add(string id, string label, string description, IEnumerable<string> ids, bool recommended = false)
        {
            var selected = ids.Where(modelId => !required.Contains(modelId) && byId.ContainsKey(modelId)).Distinct().ToImmutableArray();
            if (!selected.IsEmpty)
                choices.Add(new(id, label, description, selected, selected.Sum(modelId => byId[modelId].Size), recommended));
        }
        Add("krea2", "Krea 2 图像生成", "Krea 2 Turbo 与文本编码器，共用基础 Qwen VAE。", ["krea2-turbo", "qwen3vl-krea2"]);
        Add("seedvr2", "SeedVR2 修复放大", "修复放大模型与专用 VAE。", ["seedvr2-model", "seedvr2-vae"]);
        foreach (var model in models.Models.Where(model => model.Category == "wd_taggers").OrderByDescending(model => model.Recommended))
            Add(model.Id, "本地反推 · " + model.Name, "从图片提取标签；各反推模型可独立选择。", [model.Id], model.Recommended);
        return choices.ToImmutable();
    }

    public static ModelItem[] Resolve(ModelManifest models, ModelProfilesManifest profiles, string profile,
        IEnumerable<string>? optionalModelIds)
    {
        var byId = models.Models.ToDictionary(model => model.Id, StringComparer.Ordinal);
        var required = profiles.RequiredModels(models, profile);
        var selected = (optionalModelIds ?? []).Select(id => byId.TryGetValue(id, out var model) ? model
            : throw new InvalidDataException("模型选项已失效，请重新检查：" + id));
        return required.Concat(selected).DistinctBy(model => model.Id).ToArray();
    }

    public static ImmutableArray<string> ResolveIds(ModelManifest models, ModelProfilesManifest profiles, string profile,
        IEnumerable<string>? optionalModelIds) => Resolve(models, profiles, profile, optionalModelIds)
            .Select(model => model.Id).ToImmutableArray();
}
