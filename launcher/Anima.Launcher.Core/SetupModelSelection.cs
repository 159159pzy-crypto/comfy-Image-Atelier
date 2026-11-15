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
        return profiles.RequiredModels(models, profile);
    }

    public static ModelItem[] RequiredForPlan(InstallationPlan? plan, ModelManifest models, ModelProfilesManifest profiles, string profile)
    {
        if (plan is null) return MandatoryModels(models, profiles, profile);
        if (plan.ResourcePolicyVersion > 0)
        {
            if (plan.ResourcePolicyVersion != SetupFeatures.PolicyVersion
                || plan.MandatoryModelIds.Distinct(StringComparer.Ordinal).Count() != plan.MandatoryModelIds.Length
                || plan.MandatoryModelIds.Any(id => !plan.ModelIds.Contains(id)))
                throw new InvalidDataException("固定安装计划的资源策略或下载身份无效。");
            var frozen = plan.FrozenModels.ToDictionary(model => model.Id, StringComparer.Ordinal);
            return plan.MandatoryModelIds.Select(id => frozen.TryGetValue(id, out var model) ? model
                : throw new InvalidDataException("固定安装计划缺少所选下载：" + id)).ToArray();
        }
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
        // New functionality is opt-in; an upgrade never expands an old download owner.
        if (plan.ResourcePolicyVersion > 0 || !plan.Fresh) return [];
        var frozenIds = plan.FrozenModels.Select(model => model.Id).ToHashSet(StringComparer.Ordinal);
        models = models with { Models = plan.FrozenModels.Concat(models.Models.Where(model => !frozenIds.Contains(model.Id))).ToArray() };
        if (plan.FrozenProfile is { } profile)
            profiles = profiles with { Profiles = profiles.Profiles.Where(item => item.Id != profile.Id).Append(profile).ToArray() };
        var required = RequiredForPlan(plan, models, profiles, plan.SelectedProfile).Select(model => model.Id).ToHashSet(StringComparer.Ordinal);
        return MandatoryModels(models, profiles, plan.SelectedProfile).Where(model => !required.Contains(model.Id)).ToArray();
    }

    public static ImmutableArray<SetupModelChoice> Choices(ModelManifest models, ModelProfilesManifest profiles, string profile, bool includeBase = false)
    {
        var required = (includeBase ? [] : profiles.RequiredModels(models, profile).Select(model => model.Id)).ToHashSet(StringComparer.Ordinal);
        var byId = models.Models.ToDictionary(model => model.Id, StringComparer.Ordinal);
        var choices = ImmutableArray.CreateBuilder<SetupModelChoice>();
        void Add(string id, string label, string description, IEnumerable<string> ids, bool recommended = false)
        {
            var selected = ids.Where(modelId => !required.Contains(modelId) && byId.ContainsKey(modelId)).Distinct().ToImmutableArray();
            if (!selected.IsEmpty)
                choices.Add(new(id, label, description, selected, selected.Sum(modelId => byId[modelId].Size), recommended));
        }
        if (includeBase) Add("base", "推荐基础模型", "可使用已有兼容模型；仅勾选时下载本档案的推荐三件套。", profiles.RequiredModels(models, profile).Select(model => model.Id));
        Add("seedvr2", "SeedVR2 3B INT8 修复放大", "保留当前默认模型；已有兼容模型可在工作台选择。", SeedModelIds);
        if (byId.ContainsKey("seedvr2-7b-sharp"))
            Add("seedvr2-7b", "SeedVR2 7B Sharp INT8", "可选的 7B Sharp 模型，与 3B 共用 VAE；安装后可在工作台选择。", ["seedvr2-7b-sharp", "seedvr2-vae"]);
        Add("pixel_hires", "4x AnimeSharp 通用放大模型", "准备可选的像素放大模型下载；工作台默认模型和生成参数保持不变。", ["pixel-animesharp"]);
        if (profile != "krea2") Add("krea2", "Krea 2 图像生成", "Krea 2 Turbo 与文本编码器，共用基础 Qwen VAE。", ["krea2-turbo", "qwen3vl-krea2", "qwen-vae"]);
        Add("face-refine", "脸部细化默认模型", "集成脸部细化使用的人脸检测与分割模型。", ["forbidden-vision-detect", "forbidden-vision-segment"]);
        foreach (var model in models.Models.Where(model => model.Category == "wd_taggers").OrderByDescending(model => model.Recommended))
            Add(model.Id, "本地反推 · " + model.Name, "从图片提取标签；各反推模型可独立选择。", [model.Id], model.Recommended);
        if (includeBase || profile == "krea2") choices.Add(new("sesqui", "Sesqui 潜变量处理", "用于双采样中间处理或 Sesqui 潜空间放大。", [SetupFeatures.Prefix + "sesqui"], SesquiRuntimePreparation.Weight.Size));
        choices.Add(new("nvidia", "N 卡 VSR", "独立准备 VSR 节点与 VFX 运行库，支持情况在检查时确认。", [SetupFeatures.Prefix + "nvidia"], 0));
        choices.Add(new("dlss", "DLSS", "独立准备 DLSS 节点与当前显卡对应运行库。", [SetupFeatures.Prefix + "dlss"], 0));
        choices.Add(new("prompt-assistant", "提示词助手", "需要重写或视觉反推时安装对应节点。", [SetupFeatures.Prefix + "prompt-assistant"], 0));
        return choices.ToImmutable();
    }

    public static string[] SetChoiceSelected(IEnumerable<SetupModelChoice> choices, IEnumerable<string> selectedModelIds,
        string choiceId, bool selected)
    {
        var available = choices.ToArray();
        var choice = available.Single(item => item.Id == choiceId);
        var ids = selectedModelIds.ToHashSet(StringComparer.Ordinal);
        if (selected) ids.UnionWith(choice.ModelIds);
        else
        {
            var retained = available.Where(item => item.Id != choiceId && item.ModelIds.All(ids.Contains))
                .SelectMany(item => item.ModelIds).ToArray();
            ids.ExceptWith(choice.ModelIds);
            ids.UnionWith(retained);
        }
        return ids.Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Expand a new selection, never a frozen download plan.</summary>
    public static string[] IncludeDependencies(IEnumerable<string> modelIds)
    {
        var ids = modelIds.ToHashSet(StringComparer.Ordinal);
        if (ids.Overlaps(["seedvr2-model", "seedvr2-7b-sharp"])) ids.Add("seedvr2-vae");
        return ids.Order(StringComparer.Ordinal).ToArray();
    }

    public static ModelItem[] Resolve(ModelManifest models, ModelProfilesManifest profiles, string profile,
        IEnumerable<string>? optionalModelIds)
    {
        var byId = models.Models.ToDictionary(model => model.Id, StringComparer.Ordinal);
        var required = MandatoryModels(models, profiles, profile);
        var selected = (optionalModelIds ?? []).Where(id => !SetupFeatures.IsCapability(id)).Select(id => byId.TryGetValue(id, out var model) ? model
            : throw new InvalidDataException("模型选项已失效，请重新检查：" + id));
        return required.Concat(selected).DistinctBy(model => model.Id).ToArray();
    }

    /// <summary>Skip failed choices while retaining resources needed by other selected choices.</summary>
    public static string[] ExpandSkippedOptionalIds(ModelManifest models, ModelProfilesManifest profiles, string profile,
        IEnumerable<string> failedModelIds, bool allowBase = false, IEnumerable<string>? selectedModelIds = null)
    {
        var required = (allowBase ? [] : MandatoryModels(models, profiles, profile).Select(model => model.Id)).ToHashSet(StringComparer.Ordinal);
        var failed = failedModelIds.Where(id => !required.Contains(id)).ToHashSet(StringComparer.Ordinal);
        var choices = Choices(models, profiles, profile, includeBase: allowBase);
        foreach (var id in failed.Where(SetupFeatures.IsCapability).ToArray())
        {
            var feature = id[SetupFeatures.Prefix.Length..];
            foreach (var choice in choices.Where(choice => choice.Id == feature
                || SetupFeatures.Resolve(false, choice.ModelIds).Contains(feature)))
                failed.UnionWith(choice.ModelIds);
        }
        var selected = (selectedModelIds ?? choices.Where(choice => choice.ModelIds.Any(failed.Contains))
            .SelectMany(choice => choice.ModelIds)).ToHashSet(StringComparer.Ordinal);
        var skipped = failed.Concat(choices.Where(choice => choice.ModelIds.All(selected.Contains) && choice.ModelIds.Any(failed.Contains))
            .SelectMany(choice => choice.ModelIds)).ToHashSet(StringComparer.Ordinal);
        var retained = choices.Where(choice => choice.ModelIds.All(selected.Contains) && !choice.ModelIds.Any(failed.Contains))
            .SelectMany(choice => choice.ModelIds).Concat(required);
        skipped.ExceptWith(retained);
        return skipped.Order(StringComparer.Ordinal).ToArray();
    }

    public static ImmutableArray<string> ResolveIds(ModelManifest models, ModelProfilesManifest profiles, string profile,
        IEnumerable<string>? optionalModelIds) => Resolve(models, profiles, profile, optionalModelIds)
            .Select(model => model.Id).ToImmutableArray();
}
