"""Single-image workbench settings and the executable v2 graph adapter.

Input asset paths and processed prompts are supplied by the queue owner through
``single_runtime``; they are deliberately not public settings fields.
"""
from __future__ import annotations

import copy
import math
import re
from typing import Any

from .sampling import (
    enabled_postprocess_parameter_issues, latent_upscale_node, node_input_issues,
    validate_resize_options, validate_resize_scale,
)


_TILING = {"tiled": True, "tile_size": 512, "overlap": 64, "temporal_size": 64, "temporal_overlap": 8}
_PROCESSOR = {
    "enabled": False, "rule": "扩写-通用", "custom_rule": False,
    "custom_rule_content": "", "user_prompt": "", "llm_service": "",
    "ollama_auto_unload": True, "seed": 0,
}
OC_DEFAULT_RULE = (
    "Replace only the character features specified by the user with the supplied original character description. "
    "Preserve composition, pose, expression, background, lighting, style and quality tags. "
    "If no feature list is supplied, replace character appearance and clothing. "
    "Return only the complete final English image prompt, without commentary or code fences."
)
SINGLE_DEFAULTS: dict[str, Any] = {
    "positive_prompt": "", "prefix": "", "suffix": "",
    "input_image_id": "", "denoise": 0.65, "resize_mode": "crop",
    "unlock_resources": True, "weight_dtype": "default",
    "vae_decode": copy.deepcopy(_TILING), "vae_encode": copy.deepcopy(_TILING),
    "loras_secondary": [], "secondary_managed_triggers": [],
    "normal_pipeline": "primary", "zero_negative": False, "secondary_zero_negative": False,
    "dual": {
        "switch_step": 15, "scale": 1.0, "upscale_method": "sesqui",
        "model_format": "Wan 2.1", "half_precision": True,
        "first_pipeline": "primary", "second_pipeline": "primary",
        "second_cfg": 4.0, "second_sampler_name": "er_sde", "second_scheduler": "simple",
        "first_add_noise": True, "second_add_noise": True,
        "first_leftover_noise": True, "second_leftover_noise": False,
    },
    "krea_variance": {
        "enabled": False, "randomize_percent": 50.0, "auto_strength_factor": 1.0,
        "strength": 20.0, "noise_insert": "noise on beginning steps",
        "steps_switchover_percent": 25.0, "mask_starts_at": "beginning", "mask_percent": 0.0,
        "noise_distribution": "uniform", "granularity": "values",
    },
    "prompt_processing": {
        "expand": copy.deepcopy(_PROCESSOR),
        "oc": {**copy.deepcopy(_PROCESSOR), "custom_rule": True,
               "custom_rule_content": OC_DEFAULT_RULE, "character": "", "features": ""},
    },
    "prompt_job_id": "", "processed_prompt_override": None,
    "latent": {"upscale_method": "sesqui", "model_format": "Wan 2.1", "half_precision": True, "pipeline": "secondary"},
    "face_pipeline": "secondary",
    "dlss": {
        "enabled": False, "style": "电影", "intensity": 1.0, "local_tone": 1.0,
        "local_struct": 1.0, "output_view": "处理", "output_mix": 1.0, "gpu_gen": "Auto",
    },
}


def _merge(value: Any, defaults: dict[str, Any], path: str) -> dict[str, Any]:
    from .workflow import WorkflowError
    if not isinstance(value, dict):
        raise WorkflowError(f"{path} 必须是对象")
    unknown = set(value) - set(defaults)
    if unknown:
        raise WorkflowError(f"未知 {path} 参数: {', '.join(sorted(unknown))}")
    result = copy.deepcopy(defaults)
    for key, item in value.items():
        result[key] = _merge(item, defaults[key], f"{path}.{key}") if isinstance(defaults[key], dict) else copy.deepcopy(item)
    return result


def validate_single_settings(value: Any, settings: dict[str, Any]) -> dict[str, Any]:
    """Validate only the single namespace; callers validate shared settings."""
    from .workflow import WorkflowError, _boolean, _integer, _text, _normalize_loras, _normalize_managed_triggers
    s = _merge(value, SINGLE_DEFAULTS, "single")

    def enum(obj: dict[str, Any], key: str, choices: set[str], path: str = "single") -> None:
        if not isinstance(obj[key], str) or obj[key] not in choices:
            raise WorkflowError(f"{path}.{key} 无效，可选: {', '.join(sorted(choices))}")

    def num(obj: dict[str, Any], key: str, low: float, high: float, path: str = "single") -> None:
        val = obj[key]
        if isinstance(val, bool) or not isinstance(val, (int, float)) or not math.isfinite(val) or not low <= val <= high:
            raise WorkflowError(f"{path}.{key} 必须在 {low:g}-{high:g} 之间")
        obj[key] = float(val)

    for key in ("positive_prompt", "prefix", "suffix", "input_image_id", "prompt_job_id", "weight_dtype"):
        s[key] = _text(f"single.{key}", s[key])
    for key in ("input_image_id", "prompt_job_id"):
        if s[key] and not re.fullmatch(r"[A-Za-z0-9_-]{1,128}", s[key]):
            raise WorkflowError(f"single.{key} 无效")
    if s["processed_prompt_override"] is not None:
        s["processed_prompt_override"] = _text("single.processed_prompt_override", s["processed_prompt_override"])
    for key in ("unlock_resources", "zero_negative", "secondary_zero_negative"):
        s[key] = _boolean(f"single.{key}", s[key])
    enum(s, "resize_mode", {"pad", "crop", "stretch"})
    enum(s, "weight_dtype", {"default", "fp8_e4m3fn", "fp8_e4m3fn_fast", "fp8_e5m2"})
    num(s, "denoise", 0.0, 1.0)
    for key in ("normal_pipeline", "face_pipeline"):
        enum(s, key, {"primary", "secondary"})
    s["loras_secondary"] = _normalize_loras(s["loras_secondary"])
    s["secondary_managed_triggers"] = _normalize_managed_triggers(s["secondary_managed_triggers"])
    for group in ("vae_decode", "vae_encode"):
        tile = s[group]
        tile["tiled"] = _boolean(f"single.{group}.tiled", tile["tiled"])
        for key, low, high in (("tile_size", 64, 4096), ("overlap", 0, 2048), ("temporal_size", 8, 4096), ("temporal_overlap", 4, 4096)):
            tile[key] = _integer(f"single.{group}.{key}", tile[key], low, high)
        if tile["overlap"] >= tile["tile_size"] or tile["temporal_overlap"] >= tile["temporal_size"]:
            raise WorkflowError(f"single.{group} 重叠必须小于分块大小")
    for group in ("dual", "latent"):
        obj = s[group]
        obj.update(validate_resize_options(obj, f"single.{group}"))
    enum(s["latent"], "pipeline", {"primary", "secondary"}, "single.latent")
    dual = s["dual"]
    dual["switch_step"] = _integer("single.dual.switch_step", dual["switch_step"], 0, 149)
    if settings.get("workbench") == "single" and settings.get("base_mode") == "dual_sampler" and dual["switch_step"] >= settings["steps"]:
        raise WorkflowError("第二采样起始步必须小于总步数")
    dual["scale"] = validate_resize_scale(dual["scale"], dual["upscale_method"],
                                          "single.dual.scale", minimum=1, maximum=8)
    num(dual, "second_cfg", 0.0, 30.0, "single.dual")
    for key in ("first_pipeline", "second_pipeline"):
        enum(dual, key, {"primary", "secondary"}, "single.dual")
    for key in ("first_add_noise", "second_add_noise", "first_leftover_noise", "second_leftover_noise"):
        dual[key] = _boolean(f"single.dual.{key}", dual[key])
    for key in ("second_sampler_name", "second_scheduler"):
        dual[key] = _text(f"single.dual.{key}", dual[key])
        if not dual[key]:
            raise WorkflowError(f"single.dual.{key} 不能为空")
    if (settings.get("workbench") == "single" and settings.get("upscalers", {}).get("latent", {}).get("enabled")
            and s["latent"]["upscale_method"] == "sesqui"
            and not 1 <= settings["upscalers"]["latent"]["scale_by"] <= 2):
        raise WorkflowError("SesquiLSR 潜空间缩放必须在 1-2 之间")
    var = s["krea_variance"]
    var["enabled"] = _boolean("single.krea_variance.enabled", var["enabled"])
    for key, low, high in (("randomize_percent", 1, 100), ("auto_strength_factor", 0, 100), ("strength", -4294967295, 4294967295), ("steps_switchover_percent", 1, 99), ("mask_percent", 0, 99)):
        num(var, key, low, high, "single.krea_variance")
    enum(var, "noise_insert", {"noise on beginning steps", "noise on ending steps", "noise on all steps", "disabled"}, "single.krea_variance")
    enum(var, "mask_starts_at", {"beginning", "end"}, "single.krea_variance")
    enum(var, "noise_distribution", {"uniform", "gaussian"}, "single.krea_variance")
    enum(var, "granularity", {"values", "tokens"}, "single.krea_variance")
    if var["enabled"] and settings.get("model_profile") != "krea2":
        raise WorkflowError("多样性增强仅适用于 Krea 2")
    for name, proc in s["prompt_processing"].items():
        for key in ("enabled", "custom_rule", "ollama_auto_unload"):
            proc[key] = _boolean(f"single.prompt_processing.{name}.{key}", proc[key])
        for key in ("rule", "custom_rule_content", "user_prompt", "llm_service", *(["character", "features"] if name == "oc" else [])):
            proc[key] = _text(f"single.prompt_processing.{name}.{key}", proc[key])
        proc["seed"] = _integer(f"single.prompt_processing.{name}.seed", proc["seed"], 0, 2**53 - 1)
        if proc["enabled"]:
            if not proc["llm_service"]:
                raise WorkflowError(f"请为 {name} 选择提示词服务")
            if proc["custom_rule"] and not proc["custom_rule_content"]:
                raise WorkflowError(f"{name} 自定义规则不能为空")
            if name == "oc" and not proc["character"]:
                raise WorkflowError("OC 角色设定不能为空")
    dlss = s["dlss"]
    dlss["enabled"] = _boolean("single.dlss.enabled", dlss["enabled"])
    enum(dlss, "style", {"默认", "自然", "电影", "风格3"}, "single.dlss")
    enum(dlss, "output_view", {"处理", "差异×10", "左右对比"}, "single.dlss")
    enum(dlss, "gpu_gen", {"Auto", "RTX 30", "RTX 40", "RTX 50"}, "single.dlss")
    for key in ("intensity", "local_tone", "local_struct", "output_mix"):
        num(dlss, key, 0, 1, "single.dlss")
    if settings.get("generation_mode") == "img2img" and settings.get("base_mode") != "normal":
        raise WorkflowError("图生图仅支持普通采样模式")
    return s


def compose_single_prompt(settings: dict[str, Any]) -> str:
    s = settings["single"]
    return ", ".join(str(s.get(k) or "").strip() for k in ("prefix", "positive_prompt", "suffix") if str(s.get(k) or "").strip())


def effective_single_prompts(settings: dict[str, Any], positive: str) -> dict[str, str]:
    """Append each stack's frozen trigger words once, preserving user prose."""
    from .prompt_rules import _split_top_level
    result = {}
    for name, triggers in (("primary", settings.get("lora_managed_triggers", [])),
                           ("secondary", settings["single"].get("secondary_managed_triggers", []))):
        seen = {part.strip().casefold() for part in _split_top_level(positive)}
        extra = []
        for word in triggers:
            identity = word.strip().casefold()
            if identity and identity not in seen:
                extra.append(word.strip())
                seen.add(identity)
        result[name] = ", ".join([*([positive] if positive else []), *extra])
    return result


def active_lora_pipelines(settings: dict[str, Any]) -> frozenset[str]:
    """Return stacks reachable by the validated generation settings.

    Dormant stack settings remain available for editing and historical replay,
    but their resources must not become requirements of an unrelated pipeline.
    """
    if settings.get("workbench") != "single":
        return frozenset({"primary"})
    single = settings["single"]
    if settings["base_mode"] == "dual_sampler":
        pipelines = {single["dual"]["first_pipeline"], single["dual"]["second_pipeline"]}
    else:
        pipelines = {single["normal_pipeline"]}
    stages = settings["upscalers"]
    if stages["latent"]["enabled"] and stages["latent"]["second_sampler"]["enabled"]:
        pipelines.add(single["latent"]["pipeline"])
    if stages["face_refine"]["enabled"]:
        pipelines.add(single["face_pipeline"])
    return frozenset(pipelines)


def single_resource_issues(settings: dict[str, Any], resources: dict[str, Any]) -> list[dict[str, str]]:
    """Report enabled single-only nodes/options missing from live inventories."""
    if settings.get("workbench") != "single":
        return []
    s = settings["single"]
    issues: list[dict[str, str]] = []
    advertised = resources.get("node_types")
    node_types = set(advertised) if isinstance(advertised, list) else None
    required = {"UNETLoader", "CLIPLoader", "VAELoader", "CLIPTextEncode", "Lora Loader (LoraManager)", "SaveImage"}
    required.add("VAEDecodeTiled" if s["vae_decode"]["tiled"] else "VAEDecode")
    if settings["base_mode"] == "dual_sampler":
        required.add("KSamplerAdvanced")
        required.add("SesquiLatentUpscale" if s["dual"]["upscale_method"] == "sesqui" else "LatentUpscaleBy")
    else:
        required.add("KSampler")
    if settings["generation_mode"] == "img2img":
        required.update({"LoadImage", "VAEEncodeTiled" if s["vae_encode"]["tiled"] else "VAEEncode"})
    active = active_lora_pipelines(settings)
    if ("primary" in active and s["zero_negative"]) or ("secondary" in active and s["secondary_zero_negative"]):
        required.add("ConditioningZeroOut")
    if settings["upscalers"]["latent"]["enabled"]:
        required.update({"SesquiLatentUpscale" if s["latent"]["upscale_method"] == "sesqui" else "LatentUpscaleBy",
                         "VAEEncodeTiled" if s["vae_encode"]["tiled"] else "VAEEncode"})
    if s["dlss"]["enabled"]:
        required.add("DLSS5NeuralRender")
    if s["krea_variance"]["enabled"]:
        required.add("KreaSeedVarianceEnhancer")
    if node_types is not None:
        for name in sorted(required - node_types):
            issues.append({"type": "single_node", "name": name, "label": f"单图节点 {name}"})

    def choice(name: str, value: str, values: Any, label: str) -> None:
        if values is not None and value not in values:
            issues.append({"type": name, "name": value, "label": label})

    choice("clip_type", settings["clip_type"], resources.get("clip_types"), "CLIP 类型")
    if settings["base_mode"] == "dual_sampler":
        choice("dual_sampler", s["dual"]["second_sampler_name"], resources.get("samplers"), "第二阶段采样器")
        choice("dual_scheduler", s["dual"]["second_scheduler"], resources.get("schedulers"), "第二阶段调度器")
    definition = (resources.get("node_inputs", {}).get("UNETLoader", {}).get("weight_dtype") or [])
    if definition:
        values = definition[0] if isinstance(definition[0], list) else (definition[1].get("options") if len(definition) > 1 and isinstance(definition[1], dict) else None)
        choice("weight_dtype", s["weight_dtype"], values, "主模型精度")

    # Validate the values that the renderer will actually send, using the
    # installed node's contract. Disabled stages may retain stale drafts.
    # Old clients without node_inputs keep their existing compatibility path.
    def check_inputs(node_type: str, values: dict[str, Any], label: str) -> None:
        issues.extend(node_input_issues(resources, node_type, values, label, issue_type="single_parameter"))

    def tiling(node_type: str, values: dict[str, Any], label: str) -> None:
        check_inputs(node_type, {key: value for key, value in values.items() if key != "tiled"}, label)

    def resize(values: dict[str, Any], scale: float, label: str) -> None:
        if values["upscale_method"] == "sesqui":
            check_inputs("SesquiLatentUpscale", {"scale": scale, "model_format": values["model_format"],
                                               "half_precision": values["half_precision"]}, label)
        else:
            check_inputs("LatentUpscaleBy", {"scale_by": scale, "upscale_method": values["upscale_method"]}, label)

    sampler = {key: settings[key] for key in ("steps", "cfg", "sampler_name", "scheduler")}
    if settings["base_mode"] == "normal":
        check_inputs("KSampler", {**sampler, "denoise": s["denoise"] if settings["generation_mode"] == "img2img" else 1.0}, "普通采样")
    else:
        check_inputs("KSamplerAdvanced", sampler, "第一阶段采样")
        check_inputs("KSamplerAdvanced", {**sampler, "cfg": s["dual"]["second_cfg"],
                     "sampler_name": s["dual"]["second_sampler_name"], "scheduler": s["dual"]["second_scheduler"]}, "第二阶段采样")
        resize(s["dual"], s["dual"]["scale"], "双采样中间放大")
    if s["vae_decode"]["tiled"]:
        tiling("VAEDecodeTiled", s["vae_decode"], "主 VAE 解码")
    stages = settings["upscalers"]
    if s["vae_encode"]["tiled"] and (settings["generation_mode"] == "img2img" or stages["latent"]["enabled"]):
        tiling("VAEEncodeTiled", s["vae_encode"], "主 VAE 编码")
    if stages["latent"]["enabled"]:
        resize(s["latent"], stages["latent"]["scale_by"], "潜空间放大")
    issues.extend(enabled_postprocess_parameter_issues(settings, resources))
    if s["dlss"]["enabled"]:
        check_inputs("DLSS5NeuralRender", s["dlss"], "DLSS")
    if s["krea_variance"]["enabled"]:
        check_inputs("KreaSeedVarianceEnhancer", s["krea_variance"], "Krea 变化控制")
    return issues


def _prune_graph(api: dict[str, Any], sink: str) -> dict[str, Any]:
    from .workflow import WorkflowError
    required: set[str] = set()
    pending = [sink]
    while pending:
        node_id = pending.pop()
        if node_id in required:
            continue
        if node_id not in api:
            raise WorkflowError(f"单图工作流缺少节点: {node_id}")
        required.add(node_id)
        for value in api[node_id].get("inputs", {}).values():
            if isinstance(value, list) and len(value) == 2 and isinstance(value[0], str) and isinstance(value[1], int):
                pending.append(value[0])
    return {key: value for key, value in api.items() if key in required}


def render_single_workflows(api_template: dict[str, Any], ui_template: dict[str, Any], settings: dict[str, Any],
                            sample_seed: int, prompt_seed: int, filename_prefix: str,
                            *, single_runtime: dict[str, Any] | None = None,
                            frozen_positive_prompt: str = "", frozen_negative_prompt: str = "",
                            resource_paths: dict[str, str] | None = None) -> tuple[dict[str, Any], dict[str, Any]]:
    from .workflow import (
        WorkflowError, _render_v2_workflows, _mapped_resource_name, normalize_lora_path, V2_OUTPUT_ID,
        _lora_manager_values, _v2_set_ui_loras,
    )
    runtime = copy.deepcopy(single_runtime or {})
    if not isinstance(runtime, dict) or set(runtime) - {"input_image", "positive", "effective_positive", "negative", "prompt_job_id", "input_image_id"}:
        raise WorkflowError("single_runtime 无效")
    for key in ("prompt_job_id", "input_image_id"):
        if key in runtime and (not isinstance(runtime[key], str) or runtime[key] and not re.fullmatch(r"[A-Za-z0-9_-]{1,128}", runtime[key])):
            raise WorkflowError(f"single_runtime.{key} 无效")
    if "input_image" in runtime:
        runtime["input_image"] = normalize_lora_path(runtime["input_image"])
    s = settings["single"]
    positive = runtime.get("positive", frozen_positive_prompt or s.get("processed_prompt_override"))
    if positive is None:
        positive = compose_single_prompt(settings)
    if not isinstance(positive, str):
        raise WorkflowError("运行期正向提示词必须是字符串")
    effective = runtime.get("effective_positive", effective_single_prompts(settings, positive))
    if not isinstance(effective, dict) or set(effective) != {"primary", "secondary"} or not all(isinstance(v, str) for v in effective.values()):
        raise WorkflowError("运行期管线提示词无效")
    negative = runtime.get("negative", frozen_negative_prompt or settings["negative_prompt"])
    if not isinstance(negative, str):
        raise WorkflowError("运行期负向提示词必须是字符串")
    api, ui = _render_v2_workflows(api_template, ui_template, settings, sample_seed, prompt_seed,
                                  filename_prefix, positive, positive, positive, negative, resource_paths)

    def node(node_id: str, class_type: str, **inputs: Any) -> list[Any]:
        api[node_id] = {"class_type": class_type, "inputs": inputs}
        return [node_id, 0]

    api["167:56"]["inputs"]["weight_dtype"] = s["weight_dtype"]
    api["167:57"]["inputs"].update(clip_name=_mapped_resource_name(settings["clip_name"], resource_paths), type=settings["clip_type"])
    api["167:58"]["inputs"]["vae_name"] = _mapped_resource_name(settings["vae_name"], resource_paths)
    pipelines = {}
    active = active_lora_pipelines(settings)
    for name, loader, loras, zero in (("primary", "72", settings["loras"], s["zero_negative"]),
                                     ("secondary", "243", s["loras_secondary"], s["secondary_zero_negative"])):
        lora_values = _lora_manager_values(loras)
        _v2_set_ui_loras(ui, loader, lora_values)
        if name not in active:
            continue
        api[loader]["inputs"].update(text="", loras={"__value__": lora_values})
        pos = node(f"single:{name}:positive", "CLIPTextEncode", clip=[loader, 1], text=effective[name])
        neg = node(f"single:{name}:negative", "CLIPTextEncode", clip=[loader, 1], text=negative)
        if zero:
            neg = node(f"single:{name}:zero", "ConditioningZeroOut", conditioning=neg)
        pipelines[name] = {"model": [loader, 0], "positive": pos, "negative": neg}

    def decode(node_id: str) -> None:
        if node_id in api:
            inputs = api[node_id]["inputs"]
            samples = inputs["samples"]
            tiling = s["vae_decode"]
            api[node_id] = {"class_type": "VAEDecodeTiled" if tiling["tiled"] else "VAEDecode", "inputs": {
                "samples": samples, "vae": ["167:58", 0],
                **({k: v for k, v in tiling.items() if k != "tiled"} if tiling["tiled"] else {}),
            }}

    def upscale(node_id: str, source: list[Any], config: dict[str, Any], scale: float) -> None:
        api[node_id] = latent_upscale_node(source, config, scale)

    def with_variance(pipe: dict[str, Any], node_id: str) -> dict[str, Any]:
        pipe = copy.deepcopy(pipe)
        variance = s["krea_variance"]
        if variance["enabled"]:
            pipe["positive"] = node(node_id, "KreaSeedVarianceEnhancer", conditioning=pipe["positive"],
                                    seed=prompt_seed, log_to_console=False, **{k: v for k, v in variance.items() if k != "enabled"})
        return pipe

    if settings["base_mode"] == "normal":
        sampler = api["691:1240"]["inputs"]
        sampler.update(with_variance(pipelines[s["normal_pipeline"]], "single:variance"))
        sampler["denoise"] = s["denoise"] if settings["generation_mode"] == "img2img" else 1.0
        sampler["latent_image"] = ["691:53", 0]
        if settings["generation_mode"] == "img2img":
            image = runtime.get("input_image")
            if not isinstance(image, str) or not image:
                raise WorkflowError("图生图缺少已上传的输入图像")
            image = normalize_lora_path(image)
            image_ref = node("single:input", "LoadImage", image=image)
            tiling = s["vae_encode"]
            sampler["latent_image"] = node("single:encode", "VAEEncodeTiled" if tiling["tiled"] else "VAEEncode",
                                           pixels=image_ref, vae=["167:58", 0],
                                           **({k: v for k, v in tiling.items() if k != "tiled"} if tiling["tiled"] else {}))
        decode("691:239")
    else:
        dual = s["dual"]
        # This is the attached workflow's full first pass followed by a
        # partial second pass after upscale, NOT one pass split at switch_step.
        # E.g. 30 total / 15 second start executes 30 + 15 denoising steps.
        for stage, node_id in (("first", "701:1245"), ("second", "701:1246")):
            sampler = api[node_id]["inputs"]
            pipe = pipelines[dual[f"{stage}_pipeline"]]
            sampler.update(with_variance(pipe, "single:variance") if stage == "first" else copy.deepcopy(pipe))
            sampler.update(steps=settings["steps"], end_at_step=settings["steps"],
                           start_at_step=0 if stage == "first" else dual["switch_step"],
                           add_noise="enable" if dual[f"{stage}_add_noise"] else "disable",
                           return_with_leftover_noise="enable" if dual[f"{stage}_leftover_noise"] else "disable")
            if stage == "second":
                sampler.update(cfg=dual["second_cfg"], sampler_name=dual["second_sampler_name"], scheduler=dual["second_scheduler"])
        upscale("701:1450", ["701:1245", 0], dual, dual["scale"])
        decode("701:539")
    if settings["upscalers"]["latent"]["enabled"]:
        upscale("704:1064", ["704:1182", 0], s["latent"], settings["upscalers"]["latent"]["scale_by"])
        if "704:1248" in api:
            api["704:1248"]["inputs"].update(pipelines[s["latent"]["pipeline"]])
        decode("704:359")
        tiling = s["vae_encode"]
        old = api["704:1182"]["inputs"]
        node("704:1182", "VAEEncodeTiled" if tiling["tiled"] else "VAEEncode", pixels=old["pixels"], vae=["167:58", 0],
             **({k: v for k, v in tiling.items() if k != "tiled"} if tiling["tiled"] else {}))
    if settings["upscalers"]["face_refine"]["enabled"]:
        name = s["face_pipeline"]
        api["1253:1215"]["inputs"].update(pipelines[name], clip=["72" if name == "primary" else "243", 1])
    if s["dlss"]["enabled"]:
        config = {k: v for k, v in s["dlss"].items() if k != "enabled"}
        if settings["upscalers"]["nvidia"]["enabled"]:
            node("1432:1492", "DLSS5NeuralRender", images=["1432:1418", 0], **config)
        else:
            # DLSS is also useful without VSR. Insert it before face refinement.
            if settings["upscalers"]["face_refine"]["enabled"]:
                face_input = api["1253:1215"]["inputs"]["image"]
                api["1253:1215"]["inputs"]["image"] = node("single:dlss", "DLSS5NeuralRender", images=face_input, **config)
            else:
                source = api[V2_OUTPUT_ID]["inputs"]["images"]
                api[V2_OUTPUT_ID]["inputs"]["images"] = node("single:dlss", "DLSS5NeuralRender", images=source, **config)
    api[V2_OUTPUT_ID]["_meta"] = {"title": "进阶单图工作台 输出"}
    api = _prune_graph(api, V2_OUTPUT_ID)
    # The API graph is authoritative; keep it with the attached UI workflow so
    # advanced settings and newly inserted input nodes remain auditable.
    ui.setdefault("extra", {})["single_workbench"] = {
        "settings": copy.deepcopy(s), "effective_positive": copy.deepcopy(effective),
        "positive": positive, "negative": negative, "api_prompt": copy.deepcopy(api),
    }
    return api, ui
