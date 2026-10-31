using System.Collections.Immutable;

namespace Anima.Launcher.Core;

public sealed record SetupModelChoice(string Id, string Label, string Description,
    ImmutableArray<string> ModelIds, long Size, bool Recommended = false);

/// <summary>Feature choices are optional; each generation profile keeps its complete required set.</summary>
public static class SetupModelSelection
{
    private static readonly string[] SeedModelIds = ["seedvr2-model", "seedvr2-vae"];

    public static ModelItem[] MandatoryModels(ModelManifest models, ModelProfilesManifest profiles, string profile)
    {
        var byId = models.Models.ToDictionary(model => model.Id, StringComparer.Ordinal);
        return profiles.RequiredModels(models, profile).Concat(SeedModelIds.Select(id => byId.TryGetValue(id, out var model) ? model
            : throw new InvalidDataException("安装清单缺少必装模型：" + id))).DistinctBy(model => model.Id).ToArray();
    }

    public static ModelItem[] RequiredForPlan(InstallationPlan? plan, ModelManifest models, ModelProfilesManifest profiles, string profile)
    {
        if (plan is null) return MandatoryModels(models, profiles, profile);
        var generation = profiles.RequiredModels(models, profile);
        // Old receipts froze only their generation profile. New mandatory resources
        // belong to a separate reviewed supplement, never to the old download owner.
        if (plan.MandatoryModelIds.IsEmpty) return generation;
        var ids = plan.MandatoryModelIds.ToHashSet(StringComparer.Ordinal);
        if (ids.Count != plan.MandatoryModelIds.Length || generation.Any(model => !ids.Contains(model.Id)))
            throw new InvalidDataException("固定安装计划的必装模型身份不完整，请重新检查。");
        var byId = models.Models.ToDictionary(model => model.Id, StringComparer.Ordinal);
        return plan.MandatoryModelIds.Select(id => byId.TryGetValue(id, out var model) ? model
            : throw new InvalidDataException("固定安装计划缺少必装模型：" + id)).ToArray();
    }

    public static ModelItem[] SupplementalModels(InstallationPlan plan, ModelManifest models, ModelProfilesManifest profiles)
    {
        var frozenIds = plan.FrozenModels.Select(model => model.Id).ToHashSet(StringComparer.Ordinal);
        models = models with { Models = plan.FrozenModels.Concat(models.Models.Where(model => !frozenIds.Contains(model.Id))).ToArray() };
        if (plan.FrozenProfile is { } profile)
            profiles = profiles with { Profiles = profiles.Profiles.Where(item => item.Id != profile.Id).Append(profile).ToArray() };
        var required = RequiredForPlan(plan, models, profiles, plan.SelectedProfile).Select(model => model.Id).ToHashSet(StringComparer.Ordinal);
        return MandatoryModels(models, profiles, plan.SelectedProfile).Where(model => !required.Contains(model.Id)).ToArray();
    }

    public static ImmutableArray<SetupModelChoice> Choices(ModelManifest models, ModelProfilesManifest profiles, string profile)
    {
        var required = profiles.RequiredModels(models, profile).Select(model => model.Id).Concat(SeedModelIds).ToHashSet(StringComparer.Ordinal);
        var byId = models.Models.ToDictionary(model => model.Id, StringComparer.Ordinal);
        var choices = ImmutableArray.CreateBuilder<SetupModelChoice>();
        void Add(string id, string label, string description, IEnumerable<string> ids, bool recommended = false)
        {
            var selected = ids.Where(modelId => !required.Contains(modelId) && byId.ContainsKey(modelId)).Distinct().ToImmutableArray();
            if (!selected.IsEmpty)
                choices.Add(new(id, label, description, selected, selected.Sum(modelId => byId[modelId].Size), recommended));
        }
        Add("krea2", "Krea 2 图像生成", "Krea 2 Turbo 与文本编码器，共用基础 Qwen VAE。", ["krea2-turbo", "qwen3vl-krea2"]);
        Add("face-refine", "脸部细化默认模型", "Forbidden Vision 人脸检测、分割和颜色校正模型。", ["forbidden-vision-detect", "forbidden-vision-segment", "forbidden-vision-corrector"]);
        foreach (var model in models.Models.Where(model => model.Category == "wd_taggers").OrderByDescending(model => model.Recommended))
            Add(model.Id, "本地反推 · " + model.Name, "从图片提取标签；各反推模型可独立选择。", [model.Id], model.Recommended);
        return choices.ToImmutable();
    }

    public static ModelItem[] Resolve(ModelManifest models, ModelProfilesManifest profiles, string profile,
        IEnumerable<string>? optionalModelIds)
    {
        var byId = models.Models.ToDictionary(model => model.Id, StringComparer.Ordinal);
        var required = MandatoryModels(models, profiles, profile);
        var selected = (optionalModelIds ?? []).Select(id => byId.TryGetValue(id, out var model) ? model
            : throw new InvalidDataException("模型选项已失效，请重新检查：" + id));
        return required.Concat(selected).DistinctBy(model => model.Id).ToArray();
    }

    /// <summary>Skipping a failed feature skips its complete optional model set, never a required model.</summary>
    public static string[] ExpandSkippedOptionalIds(ModelManifest models, ModelProfilesManifest profiles, string profile,
        IEnumerable<string> failedModelIds)
    {
        var required = MandatoryModels(models, profiles, profile).Select(model => model.Id).ToHashSet(StringComparer.Ordinal);
        var skipped = failedModelIds.Where(id => !required.Contains(id)).ToHashSet(StringComparer.Ordinal);
        var choices = Choices(models, profiles, profile);
        int previousCount;
        do
        {
            previousCount = skipped.Count;
            foreach (var choice in choices)
                if (choice.ModelIds.Any(skipped.Contains)) skipped.UnionWith(choice.ModelIds);
        } while (skipped.Count != previousCount);
        return skipped.Order(StringComparer.Ordinal).ToArray();
    }

    public static ImmutableArray<string> ResolveIds(ModelManifest models, ModelProfilesManifest profiles, string profile,
        IEnumerable<string>? optionalModelIds) => Resolve(models, profiles, profile, optionalModelIds)
            .Select(model => model.Id).ToImmutableArray();
}
