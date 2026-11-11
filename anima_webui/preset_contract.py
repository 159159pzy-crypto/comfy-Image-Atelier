"""Portable style patches, kept separate from complete generation settings."""
from __future__ import annotations

import copy
from typing import Any

from .model_profiles import get_model_profile, normalize_model_profile
from .prompt_rules import PromptRuleStore
from .sampling import DUAL_DEFAULTS
from .workflow import CURRENT_PIPELINE_VERSION, DEFAULT_SETTINGS, WorkflowError, validate_settings


INCLUDE_KEYS = ("size", "repair", "img2img", "affixes")
CORE_KEYS = (
    "model_profile", "model_name", "clip_name", "vae_name", "clip_type", "loras",
    "lora_managed_triggers", "manual_artist", "quality_prompt", "extra_prompt",
    "negative_prompt", "zero_negative", "steps", "cfg", "sampler_name", "scheduler",
    "base_mode", "base_denoise", "krea_variance",
)
DUAL_KEYS = tuple(key for key in DUAL_DEFAULTS if key != "half_precision")
SINGLE_CORE_KEYS = (
    "loras_secondary", "secondary_managed_triggers", "normal_pipeline",
    "zero_negative", "secondary_zero_negative",
)
SINGLE_DUAL_KEYS = (*DUAL_KEYS, "first_pipeline", "second_pipeline", "first_add_noise",
                    "second_add_noise", "first_leftover_noise", "second_leftover_noise")
AFFIX_KEYS = ("prefix", "suffix", "lora_prefix_managed_triggers")
IMAGE_KEYS = ("denoise", "resize_mode")
LATENT_KEYS = ("upscale_method", "model_format", "pipeline")
DEVICE_PATHS = (
    "single.unlock_resources", "single.weight_dtype", "single.vae_encode", "single.vae_decode",
    "single.dual.half_precision", "single.latent.half_precision", "single.dlss.gpu_gen",
    "dual.half_precision", "upscalers.latent.half_precision", "upscalers.seedvr2.weight_dtype",
    "upscalers.seedvr2.encode", "upscalers.seedvr2.decode", "upscalers.face_refine.offload_models_to_cpu",
    "upscalers.nvidia.dlss.gpu_gen", "upscalers.dlss.gpu_gen",
)
REPAIR_DEVICE_KEYS = {
    "latent": {"half_precision"}, "seedvr2": {"weight_dtype", "encode", "decode"},
    "face_refine": {"offload_models_to_cpu"}, "dlss": {"gpu_gen"},
}


def normalize_include(value: Any = None) -> dict[str, bool]:
    if value is None:
        value = {}
    if not isinstance(value, dict) or set(value) - set(INCLUDE_KEYS):
        raise WorkflowError("风格预设 include 必须只包含 size、repair、img2img、affixes")
    if any(not isinstance(flag, bool) for flag in value.values()):
        raise WorkflowError("风格预设携带项必须是布尔值")
    return {key: value.get(key, False) for key in INCLUDE_KEYS}


def normalize_source(value: Any, settings: dict[str, Any] | None = None) -> dict[str, Any]:
    settings = settings or {}
    if value is None:
        value = {}
    if not isinstance(value, dict) or set(value) - {"workbench", "pipeline_version"}:
        raise WorkflowError("风格预设 source 无效")
    workbench = value.get("workbench", settings.get("workbench", "single" if "single" in settings else "random"))
    version = value.get("pipeline_version", settings.get("pipeline_version", 2))
    if not isinstance(workbench, str) or workbench not in {"random", "single"}:
        raise WorkflowError("风格预设来源工作台无效")
    if type(version) is not int or not 2 <= version <= CURRENT_PIPELINE_VERSION:
        raise WorkflowError("风格预设来源 pipeline_version 无效")
    return {"workbench": workbench, "pipeline_version": version}


def deep_merge(base: dict[str, Any], patch: dict[str, Any]) -> dict[str, Any]:
    result = copy.deepcopy(base)
    for key, value in patch.items():
        result[key] = (deep_merge(result.get(key, {}) if isinstance(result.get(key), dict) else {}, value)
                       if isinstance(value, dict) else copy.deepcopy(value))
    return result


def _pick(value: dict[str, Any], keys: Any) -> dict[str, Any]:
    return {key: copy.deepcopy(value[key]) for key in keys if key in value}


def _repair(value: Any) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise WorkflowError("风格预设 upscalers 必须是对象")
    result = copy.deepcopy(value)
    for stage, keys in REPAIR_DEVICE_KEYS.items():
        if isinstance(result.get(stage), dict):
            for key in keys:
                result[stage].pop(key, None)
    if isinstance(result.get("nvidia"), dict) and isinstance(result["nvidia"].get("dlss"), dict):
        result["nvidia"]["dlss"].pop("gpu_gen", None)
    return result


def effective_dual(settings: dict[str, Any]) -> dict[str, Any]:
    if settings.get("workbench") == "single":
        return _pick(settings["single"]["dual"], DUAL_KEYS)
    if isinstance(settings.get("dual"), dict):
        return _pick(settings["dual"], DUAL_KEYS)
    latent = settings["upscalers"]["latent"]
    return {
        "switch_step": 15, "scale": latent["scale_by"], "upscale_method": latent["upscale_method"],
        "model_format": latent["model_format"], "second_cfg": settings["cfg"],
        "second_sampler_name": settings["sampler_name"], "second_scheduler": settings["scheduler"],
    }


def preset_settings(
    value: Any,
    prompt_rules: PromptRuleStore | None = None,
    *,
    force_model_profile: str | None = None,
    include: Any = None,
    source: Any = None,
) -> dict[str, Any]:
    """Validate a capture, then emit only fields the preset may ever apply.

    Excluded controls are removed *before* validation: an unfinished AI service
    form or a device option on another computer cannot prevent saving a style.
    Defaults used for validation never escape into an unchecked optional group.
    """
    if not isinstance(value, dict):
        raise WorkflowError("风格预设 settings 必须是对象")
    unknown = set(value) - set(DEFAULT_SETTINGS)
    if unknown:
        raise WorkflowError(f"风格预设包含未知参数: {', '.join(sorted(unknown))}")
    flags = normalize_include(include)
    origin = normalize_source(source, value)
    candidate = _pick(value, CORE_KEYS)
    candidate.update(origin)
    candidate["generation_mode"] = "txt2img"
    if force_model_profile is not None:
        profile_id = normalize_model_profile(force_model_profile)
        profile = get_model_profile(profile_id)
        candidate.update(model_profile=profile_id, **_pick(profile, ("clip_name", "vae_name", "clip_type")))
        if profile_id != "krea2" and isinstance(candidate.get("krea_variance"), dict):
            candidate["krea_variance"]["enabled"] = False
    if "dual" in value:
        raw_dual = value["dual"]
        if raw_dual is not None and not isinstance(raw_dual, dict):
            raise WorkflowError("风格预设 dual 必须是对象或 null")
        candidate["dual"] = _pick(raw_dual, DUAL_KEYS) if raw_dual is not None else None
    raw_single = value.get("single", {})
    if not isinstance(raw_single, dict):
        raise WorkflowError("风格预设 single 必须是对象")
    single = _pick(raw_single, SINGLE_CORE_KEYS)
    # Read compatibility aliases before projection; only the canonical shared
    # fields survive in the stored style.
    if "krea_variance" in raw_single:
        single["krea_variance"] = copy.deepcopy(raw_single["krea_variance"])
    if "dual" in raw_single:
        if not isinstance(raw_single["dual"], dict):
            raise WorkflowError("风格预设 single.dual 必须是对象")
        single["dual"] = _pick(raw_single["dual"], SINGLE_DUAL_KEYS)
    if flags["size"]:
        candidate.update(_pick(value, ("width", "height")))
    if flags["img2img"] and origin["workbench"] == "single":
        single.update(_pick(raw_single, IMAGE_KEYS))
    if flags["affixes"] and origin["workbench"] == "single":
        single.update(_pick(raw_single, AFFIX_KEYS))
    if flags["repair"]:
        if "upscalers" in value:
            candidate["upscalers"] = _repair(value["upscalers"])
        for alias in ("hires", "second_sampler"):
            if alias in value:
                candidate[alias] = copy.deepcopy(value[alias])
        if origin["workbench"] == "single":
            if "dlss" in raw_single:
                if not isinstance(raw_single["dlss"], dict):
                    raise WorkflowError("风格预设 single.dlss 必须是对象")
                single["dlss"] = {key: copy.deepcopy(item) for key, item in raw_single["dlss"].items() if key != "gpu_gen"}
            if "latent" in raw_single:
                if not isinstance(raw_single["latent"], dict):
                    raise WorkflowError("风格预设 single.latent 必须是对象")
                single["latent"] = _pick(raw_single["latent"], LATENT_KEYS)
            single.update(_pick(raw_single, ("face_pipeline",)))
    elif origin["workbench"] == "random" and value.get("dual") is None:
        # Legacy dual:null borrowed its middle resize from the repair block.
        # Resolve that dependency before the unchecked repair block is dropped.
        latent = value.get("upscalers", {}).get("latent", {}) if isinstance(value.get("upscalers"), dict) else {}
        if not isinstance(latent, dict):
            raise WorkflowError("风格预设旧双采样放大参数无效")
        candidate["upscalers"] = {"latent": _pick(latent, ("scale_by", "upscale_method", "model_format"))}
        if "upscalers" not in value and "hires" in value:
            candidate.pop("upscalers")
            candidate["hires"] = copy.deepcopy(value["hires"])
    candidate["single"] = single
    normalized = validate_settings(candidate)
    if prompt_rules is not None:
        normalized, _ = prompt_rules.normalize_settings(normalized, model_profile=normalized["model_profile"])
        normalized = validate_settings(normalized)
    result = _pick(normalized, CORE_KEYS)
    result["dual"] = effective_dual(normalized)
    if normalized["base_mode"] == "dual_sampler" and result["dual"]["switch_step"] >= normalized["steps"]:
        raise WorkflowError("旧双采样第二轮起始步不小于总步数，请调整采样参数后保存")
    if origin["workbench"] == "single":
        result["single"] = _pick(normalized["single"], SINGLE_CORE_KEYS)
        result["single"]["dual"] = _pick(normalized["single"]["dual"], SINGLE_DUAL_KEYS)
        if flags["img2img"]:
            result["single"].update(_pick(normalized["single"], IMAGE_KEYS))
        if flags["affixes"]:
            result["single"].update(_pick(normalized["single"], AFFIX_KEYS))
        if flags["repair"]:
            result["single"]["latent"] = _pick(normalized["single"]["latent"], LATENT_KEYS)
            result["single"]["face_pipeline"] = normalized["single"]["face_pipeline"]
    if flags["size"]:
        result.update(_pick(normalized, ("width", "height")))
    if flags["repair"]:
        result["upscalers"] = _repair(normalized["upscalers"])
    return result


def resolve_settings(item: dict[str, Any], current_settings: Any) -> dict[str, Any]:
    if not isinstance(current_settings, dict):
        raise WorkflowError("当前 settings 必须是对象")
    current = validate_settings(current_settings)
    if current["model_profile"] != item["model_profile"]:
        raise WorkflowError("不能跨模型档案应用风格预设")
    flags = normalize_include(item["include"])
    origin = normalize_source(item["source"])
    # Re-project at the read boundary as defence against future callers handing
    # us a full snapshot instead of a stored portable patch.
    patch = preset_settings(item["settings"], include=flags, source=origin)
    target_workbench = current["workbench"]
    source_workbench = origin["workbench"]
    if target_workbench == "random":
        if source_workbench == "single":
            source_single = patch["single"]
            pipeline = (source_single["dual"]["second_pipeline"] if patch["base_mode"] == "dual_sampler"
                        else source_single["normal_pipeline"])
            patch["zero_negative"] = source_single["secondary_zero_negative" if pipeline == "secondary" else "zero_negative"]
            if flags["repair"]:
                patch["upscalers"]["latent"].update(_pick(source_single["latent"], ("upscale_method", "model_format")))
        patch.pop("single", None)
        # A portable explicit dual must retain the target device's precision,
        # including when the current legacy null borrowed it from latent repair.
        if current["dual"] is None:
            current["dual"] = {**effective_dual(current), "half_precision": current["upscalers"]["latent"]["half_precision"]}
    else:
        target_single = patch.setdefault("single", {})
        target_single["dual"] = {**target_single.get("dual", {}), **patch["dual"]}
        if source_workbench == "random":
            for key in ("clip_name", "vae_name", "clip_type"):
                patch.pop(key, None)
            if flags["repair"]:
                target_single["latent"] = _pick(patch["upscalers"]["latent"], ("upscale_method", "model_format"))
        patch.pop("dual", None)
    target = deep_merge(current, patch)
    if flags["repair"]:
        target["pipeline_version"] = origin["pipeline_version"]
        # Deep merge must not retain a current stage's fields where the saved
        # contract intentionally omitted them (notably pre-v4 latent.sampler).
        target["upscalers"] = copy.deepcopy(patch["upscalers"])
        for path in DEVICE_PATHS:
            if not path.startswith("upscalers."):
                continue
            parts = path.split(".")
            old = current
            new = target
            for part in parts[:-1]:
                old = old.get(part, {}) if isinstance(old, dict) else {}
                new = new.setdefault(part, {})
            if parts[-1] in old:
                new[parts[-1]] = copy.deepcopy(old[parts[-1]])
    if target_workbench == "single" and source_workbench == "random":
        single = target["single"]
        active = ({single["dual"]["first_pipeline"], single["dual"]["second_pipeline"]}
                  if target["base_mode"] == "dual_sampler" else {single["normal_pipeline"]})
        if target["pipeline_version"] < 3 and target["upscalers"]["latent"]["enabled"] and target["upscalers"]["latent"]["second_sampler"]["enabled"]:
            active.add(single["latent"]["pipeline"])
        if target["upscalers"]["face_refine"]["enabled"]:
            active.add(single["face_pipeline"])
        for pipeline in active:
            single["secondary_zero_negative" if pipeline == "secondary" else "zero_negative"] = patch["zero_negative"]
    target["single"]["prompt_job_id"] = ""
    target["single"]["processed_prompt_override"] = None
    target = validate_settings(target)
    change = ({"from": current["pipeline_version"], "to": target["pipeline_version"]}
              if target["pipeline_version"] != current["pipeline_version"] else None)
    return {"settings": target, "version_change": change}
