from __future__ import annotations

import copy
import json
import math
import re
from pathlib import Path
from typing import Any

from .single_workbench import SINGLE_DEFAULTS, validate_single_settings
from .sampling import (
    latent_upscale_node, validate_random_dual, validate_resize_options, validate_resize_scale,
    DUAL_DEFAULTS, KREA_VARIANCE_DEFAULTS, DLSS_DEFAULTS, validate_krea_variance, validate_dlss,
)

from .model_profiles import (
    DEFAULT_MODEL_PROFILE,
    ModelProfileError,
    MODEL_PROFILES,
    get_model_profile,
    normalize_model_profile,
)


DEFAULT_QUALITY = "masterpiece, best quality, score_9, score_8, highres, year 2025, newest, safe"
DEFAULT_NEGATIVE = (
    "worst quality, low quality, lowres, score_1, score_2, score_3, blurry, "
    "jpeg artifacts, bad anatomy, watermark, artist name,"
)
DEFAULT_LORAS: list[dict[str, Any]] = []
DEFAULT_MODEL = MODEL_PROFILES[DEFAULT_MODEL_PROFILE]["model_name"]
DEFAULT_SAMPLER = "euler"
DEFAULT_SCHEDULER = "simple"
DEFAULT_SECOND_SAMPLER = {
    "enabled": False,
    "steps": 16,
    "cfg": 4.0,
    "sampler_name": DEFAULT_SAMPLER,
    "scheduler": DEFAULT_SCHEDULER,
    "denoise": 0.2,
}
DEFAULT_HIRES = {
    "enabled": True,
    "model_name": "4x_foolhardy_Remacri.pth",
    "percent": 45,
}
DEFAULT_DETAILERS = {
    "hand": False,
    "nsfw": False,
    "face": False,
    "eyes": False,
}
DETAILER_ORDER = tuple(DEFAULT_DETAILERS)
DETAILER_NODES = {
    "hand": {"detector": 8, "editor": 13, "detailer": 27, "detector_input": "bbox_detector"},
    "nsfw": {"detector": 9, "editor": 14, "detailer": 28, "detector_input": "segm_detector"},
    "face": {"detector": 10, "editor": 15, "detailer": 29, "detector_input": "bbox_detector"},
    "eyes": {"detector": 11, "editor": 16, "detailer": 30, "detector_input": "bbox_detector"},
}
MAX_LORAS = 64
MAX_LORA_MANAGED_TRIGGERS = 256
MIN_LORA_STRENGTH = -100.0
MAX_LORA_STRENGTH = 100.0
PROMPT_SECTIONS = ("character", "clothing", "pose", "background", "expression")
DEFAULT_SETTINGS = {
    "workbench": "random",
    "generation_mode": "txt2img",
    "single": copy.deepcopy(SINGLE_DEFAULTS),
    "dual": copy.deepcopy(DUAL_DEFAULTS),
    "base_denoise": 1.0,
    "krea_variance": copy.deepcopy(KREA_VARIANCE_DEFAULTS),
    "count": 10,
    "random_character": False,
    "random_clothing": False,
    "random_pose": False,
    "random_background": False,
    "random_expression": False,
    "random_character_count": 1,
    "random_clothing_count": 1,
    "random_pose_count": 1,
    "random_background_count": 1,
    "random_expression_count": 1,
    "fixed_character": "",
    "fixed_clothing": "",
    "fixed_pose": "",
    "fixed_background": "",
    "fixed_expression": "",
    "female_count": 1,
    "male_count": 0,
    "model_name": DEFAULT_MODEL,
    # v2 profile/resource contract.  Legacy fields below remain accepted at
    # the validation boundary so saved v1 settings can be migrated safely.
    "model_profile": DEFAULT_MODEL_PROFILE,
    "clip_name": MODEL_PROFILES[DEFAULT_MODEL_PROFILE]["clip_name"],
    "vae_name": MODEL_PROFILES[DEFAULT_MODEL_PROFILE]["vae_name"],
    "clip_type": MODEL_PROFILES[DEFAULT_MODEL_PROFILE]["clip_type"],
    "loras": DEFAULT_LORAS,
    "lora_managed_triggers": [],
    "pools": {
        "character": {"mode": "include", "ids": [], "excluded_ids": []},
        "clothing": {"mode": "include", "ids": [], "excluded_ids": []},
        "pose": {"mode": "include", "ids": [], "excluded_ids": []},
        "background": {"mode": "include", "ids": [], "excluded_ids": []},
        "expression": {"mode": "include", "ids": [], "excluded_ids": []},
    },
    # Krea 2 uses natural-language entries instead of the five Anima
    # taxonomy pools.  Entries are persisted by profile and selected with the
    # prompt seed by the batch runner.
    "krea_prompt_pool": {"enabled": False, "ids": []},
    "character_detail": "trigger_tags",
    "manual_artist": "",
    "quality_prompt": DEFAULT_QUALITY,
    "extra_prompt": "",
    "negative_prompt": DEFAULT_NEGATIVE,
    "width": 832,
    "height": 1216,
    "steps": MODEL_PROFILES[DEFAULT_MODEL_PROFILE]["defaults"]["steps"],
    "cfg": MODEL_PROFILES[DEFAULT_MODEL_PROFILE]["defaults"]["cfg"],
    "sampler_name": MODEL_PROFILES[DEFAULT_MODEL_PROFILE]["defaults"]["sampler_name"],
    "scheduler": MODEL_PROFILES[DEFAULT_MODEL_PROFILE]["defaults"]["scheduler"],
    # Compatibility-only aliases remain available to v1 embedded callers and
    # old test/integration adapters.  ``migrate_settings`` strips them before
    # a v2 batch/history payload is persisted, and the v2 renderer never
    # appends the corresponding nodes.
    "hires": DEFAULT_HIRES,
    "detailers": DEFAULT_DETAILERS,
    "cfg_zero_star": False,
    "second_sampler": DEFAULT_SECOND_SAMPLER,
    "base_mode": MODEL_PROFILES[DEFAULT_MODEL_PROFILE]["defaults"]["base_mode"],
    "upscalers": {
        "latent": {
            "enabled": False,
            "scale_by": 1.5,
            "upscale_method": "sesqui",
            "model_format": "Wan 2.1",
            "half_precision": True,
            # The attached v2 workflow has always sampled after the latent
            # resize. Keep that behavior when users enable the latent stage;
            # the sampler now has an independent switch and full parameters.
            "second_sampler": {
                "enabled": True,
                "steps": 20,
                "cfg": 8.0,
                "sampler_name": DEFAULT_SAMPLER,
                "scheduler": DEFAULT_SCHEDULER,
                "denoise": 1.0,
            },
        },
        "seedvr2": {
            "enabled": False,
            "model_name": MODEL_PROFILES[DEFAULT_MODEL_PROFILE]["seedvr2_model"],
            "vae_name": MODEL_PROFILES[DEFAULT_MODEL_PROFILE]["seedvr2_vae"],
            "scale_by": 2.0,
            "weight_dtype": "default",
            "color_correction_method": "lab",
            "sampler": {
                "steps": 1,
                "cfg": 1.0,
                "sampler_name": "euler",
                "scheduler": "simple",
                "denoise": 1.0,
            },
            "encode": {
                "tile_size": 512,
                "overlap": 64,
                "temporal_size": 64,
                "temporal_overlap": 8,
            },
            "decode": {
                "tile_size": 512,
                "overlap": 128,
                "temporal_size": 4096,
                "temporal_overlap": 8,
            },
        },
        "nvidia": {
            "enabled": False,
            "resize_type": "scale by multiplier",
            "scale": 2.0,
            "quality": "ULTRA",
            "dlss": copy.deepcopy(DLSS_DEFAULTS),
        },
        "face_refine": {
            "enabled": False,
            "steps": 8,
            "cfg_scale": 1.0,
            "sampler": "res_2m_ode",
            "scheduler": "beta57",
            "denoise_strength": 0.3,
            "face_selection": 0,
            "detection_confidence": 0.75,
            "manual_rotation": "None",
            "processing_resolution": 1024,
            "enable_pre_upscale": True,
            "upscaler_model": "Fast 4x (Lanczos)",
            "crop_padding": 1.6,
            "face_positive_prompt": "",
            "replace_positive_prompt": False,
            "face_negative_prompt": "",
            "replace_negative_prompt": False,
            "exclusions": "",
            "blend_softness": 8,
            "mask_expansion": 2,
            "sampling_mask_blur_size": 21,
            "sampling_mask_blur_strength": 1,
            "enable_color_correction": True,
            "enable_segmentation": True,
            "enable_differential_diffusion": True,
            "enable_lightness_rescue": True,
            "enable_final_refinement": True,
            "offload_models_to_cpu": True,
        },
    },
}

MAX_SAMPLE_SEED = 1125899906842624

COMPOSER_ID = 60
POSITIVE_ID = 42
NEGATIVE_ID = 45
HIRES_SCALE_ID = 51
HIRES_MODEL_ID = 61
HIRES_UPSCALE_ID = 62
CFG_ZERO_STAR_ID = 49
SECOND_SAMPLER_ID = 25
SAM_LOADER_ID = 7
DETAILER_PIPE_ID = 18
REMOVED_NODE_IDS = {3, 4}

# 模板必需节点总表:构造 WorkflowTemplates 时一次性校验。
# 工作流重新导出会导致节点重编号,缺失节点应在启动时给出明确错误,
# 而不是运行期抛出晦涩的 KeyError/IndexError。
REQUIRED_API_NODES: dict[int, str] = {
    1: "UNETLoader 主模型",
    2: "Power LoRA Loader",
    5: "KSampler 主采样器",
    12: "SaveImage 保存图像",
    22: "VAE 解码",
    23: "宽度",
    31: "高度",
    35: "批量数量",
    37: "种子",
    39: "步数",
    41: "CFG",
    POSITIVE_ID: "正向提示词",
    NEGATIVE_ID: "负向提示词",
    HIRES_SCALE_ID: "高清修复缩放",
    HIRES_MODEL_ID: "高清修复模型加载",
    HIRES_UPSCALE_ID: "高清修复放大",
}
_DETAILER_LABELS = {"hand": "手部", "nsfw": "NSFW", "face": "面部", "eyes": "眼睛"}
REQUIRED_UI_NODES: dict[int, str] = dict(REQUIRED_API_NODES)
for _detailer_name, _detailer_nodes in DETAILER_NODES.items():
    _label = _DETAILER_LABELS[_detailer_name]
    REQUIRED_UI_NODES[_detailer_nodes["detector"]] = f"{_label} Detailer 检测器"
    REQUIRED_UI_NODES[_detailer_nodes["editor"]] = f"{_label} Detailer 编辑管线"
    REQUIRED_UI_NODES[_detailer_nodes["detailer"]] = f"{_label} Detailer"


def validate_template_nodes(api: dict[str, Any], ui: dict[str, Any]) -> None:
    """校验两份模板包含全部必需节点,缺失时给出可行动的错误信息。"""

    # ComfyUI's API Format export uses namespaced ids (``group:node``) and
    # contains the optional v2 branches.  It has a different contract from
    # the hand-numbered v1 template, so do not apply the legacy integer table
    # to it.
    if any(":" in str(node_id) for node_id in api):
        problems: list[str] = []
        for node_id, node in api.items():
            if not isinstance(node, dict) or not isinstance(node.get("class_type"), str) or not node["class_type"]:
                problems.append(f"{node_id}(class_type)")
                continue
            if node.get("class_type") in V2_REMOVED_CLASSES:
                problems.append(f"{node_id}({node['class_type']})")
            for input_name, value in (node.get("inputs") or {}).items():
                if (
                    isinstance(value, list)
                    and len(value) == 2
                    and isinstance(value[0], str)
                    and isinstance(value[1], int)
                    and value[0] not in api
                ):
                    problems.append(f"{node_id}.{input_name}->{value[0]}")
        required_v2 = {
            "167:56": "UNETLoader 主模型",
            "167:57": "CLIPLoader",
            "167:58": "VAELoader",
            "72": "LoRA 堆 1",
            "243": "LoRA 堆 2",
            "691:1413": "普通底图",
            "701:1414": "双采样底图",
            "704:1415": "潜空间放大",
            "712:1417": "SeedVR2",
            "1432:1447": "N 卡放大",
            "1253:1416": "脸部细化",
        }
        problems.extend(
            f"{node_id}({label})"
            for node_id, label in required_v2.items()
            if node_id not in api
        )
        if problems:
            raise WorkflowError("v2 工作流 API 模板契约不完整: " + "; ".join(problems))
        ui_ids = {str(node.get("id")) for node in ui.get("nodes") or []}
        missing_ui = [str(node_id) for node_id in (167, 691, 701, 704, 712, 1253, 1432) if str(node_id) not in ui_ids]
        if missing_ui:
            raise WorkflowError("v2 可视化模板缺少组节点: " + ", ".join(missing_ui))
        return
    missing_api = [
        f"{node_id}({label})"
        for node_id, label in sorted(REQUIRED_API_NODES.items())
        if str(node_id) not in api
    ]
    ui_ids = {node.get("id") for node in ui.get("nodes") or []}
    missing_ui = [
        f"{node_id}({label})"
        for node_id, label in sorted(REQUIRED_UI_NODES.items())
        if node_id not in ui_ids
    ]
    problems = []
    if missing_api:
        problems.append(f"API 模板缺少节点 {', '.join(missing_api)}")
    if missing_ui:
        problems.append(f"可视化模板缺少节点 {', '.join(missing_ui)}")
    if problems:
        raise WorkflowError(
            "工作流模板与预期结构不符(可能因重新导出导致节点重编号): " + "；".join(problems)
        )


class WorkflowError(ValueError):
    pass


def read_json(path: str | Path) -> dict[str, Any]:
    with Path(path).open("r", encoding="utf-8") as handle:
        return json.load(handle)


def write_json(path: str | Path, value: dict[str, Any]) -> None:
    destination = Path(path)
    destination.parent.mkdir(parents=True, exist_ok=True)
    with destination.open("w", encoding="utf-8", newline="\n") as handle:
        json.dump(value, handle, ensure_ascii=False, indent=2)
        handle.write("\n")


def _integer(name: str, value: Any, minimum: int, maximum: int) -> int:
    if isinstance(value, bool) or not isinstance(value, int):
        raise WorkflowError(f"{name} 必须是整数")
    if not minimum <= value <= maximum:
        raise WorkflowError(f"{name} 必须在 {minimum}-{maximum} 之间")
    return value


def _boolean(name: str, value: Any) -> bool:
    if not isinstance(value, bool):
        raise WorkflowError(f"{name} 必须是布尔值")
    return value


def _text(name: str, value: Any) -> str:
    if not isinstance(value, str):
        raise WorkflowError(f"{name} 必须是字符串")
    if len(value) > 20000:
        raise WorkflowError(f"{name} 不能超过 20000 个字符")
    return value.strip()


def normalize_lora_path(value: Any) -> str:
    filename = _text("LoRA 文件名", value).replace("\\", "/")
    parts = filename.split("/")
    if (
        not filename
        or filename.startswith("/")
        or len(filename) >= 2 and filename[1] == ":"
        or any(part in {"", ".", ".."} for part in parts)
    ):
        raise WorkflowError("LoRA 文件名必须是安全的相对路径")
    return "/".join(parts)


def normalize_artist_tags(value: Any) -> str:
    raw = _text("manual_artist", value).replace("\r", ",").replace("\n", ",")
    names: list[str] = []
    seen: set[str] = set()
    for part in re.split(r",|(?=@)", raw):
        name = part.strip()
        if name.startswith("@"):
            name = name[1:].strip()
        elif name.lower().startswith("by "):
            name = name[3:].strip()
        if not name:
            continue
        identity = name.casefold()
        if identity not in seen:
            names.append(name)
            seen.add(identity)
    return ", ".join(f"@{name}" for name in names)


def _normalize_loras(value: Any) -> list[dict[str, Any]]:
    if not isinstance(value, list):
        raise WorkflowError("loras 必须是数组")
    if len(value) > MAX_LORAS:
        raise WorkflowError(f"LoRA 配置不能超过 {MAX_LORAS} 项")
    result: list[dict[str, Any]] = []
    seen: set[str] = set()
    for index, item in enumerate(value, 1):
        if not isinstance(item, dict):
            raise WorkflowError(f"loras[{index}] 必须是对象")
        filename = normalize_lora_path(item.get("filename"))
        identity = filename.casefold()
        if identity in seen:
            raise WorkflowError(f"LoRA 不能重复配置: {filename}")
        seen.add(identity)
        enabled = _boolean(f"loras[{index}].enabled", item.get("enabled"))
        strength = item.get("strength")
        if isinstance(strength, bool) or not isinstance(strength, (int, float)):
            raise WorkflowError(f"loras[{index}].strength 必须是数字")
        if not math.isfinite(float(strength)):
            raise WorkflowError(f"loras[{index}].strength 必须是有限数字")
        strength = float(strength)
        if not MIN_LORA_STRENGTH <= strength <= MAX_LORA_STRENGTH:
            raise WorkflowError(
                f"loras[{index}].strength 必须在 {MIN_LORA_STRENGTH:g}-{MAX_LORA_STRENGTH:g} 之间"
            )
        result.append({"filename": filename, "enabled": enabled, "strength": strength})
    return result


def validate_loras(settings: dict[str, Any], available_filenames: Any | None = None) -> list[dict[str, Any]]:
    """Validate the persisted LoRA shape and, when available, the ComfyUI inventory."""
    loras = _normalize_loras(settings.get("loras", DEFAULT_LORAS))
    if available_filenames is not None:
        available: dict[str, str] = {}
        by_basename: dict[str, list[str]] = {}
        for raw in available_filenames:
            exact = str(raw)
            normalized = normalize_lora_path(exact)
            available.setdefault(normalized.casefold(), exact)
            by_basename.setdefault(normalized.rsplit("/", 1)[-1].casefold(), []).append(exact)
        for item in loras:
            if not item["enabled"]:
                continue
            normalized = normalize_lora_path(item["filename"])
            exact = available.get(normalized.casefold())
            if exact is None and "/" not in normalized:
                matches = list(dict.fromkeys(by_basename.get(normalized.casefold(), [])))
                if len(matches) == 1:
                    exact = matches[0]
                elif len(matches) > 1:
                    raise WorkflowError(
                        f"LoRA 文件名存在多个子目录匹配，请重新选择: {item['filename']}"
                    )
            if exact is None:
                raise WorkflowError(f"LoRA 文件不存在: {item['filename']}")
            item["filename"] = exact
    return loras


def _normalize_managed_triggers(value: Any) -> list[str]:
    if not isinstance(value, list):
        raise WorkflowError("lora_managed_triggers 必须是数组")
    if len(value) > MAX_LORA_MANAGED_TRIGGERS:
        raise WorkflowError(
            f"lora_managed_triggers 不能超过 {MAX_LORA_MANAGED_TRIGGERS} 项"
        )
    result: list[str] = []
    seen: set[str] = set()
    for index, raw in enumerate(value):
        word = _text(f"lora_managed_triggers[{index}]", raw)
        if len(word) > 500:
            raise WorkflowError(f"lora_managed_triggers[{index}] 过长")
        identity = word.casefold()
        if word and identity not in seen:
            result.append(word)
            seen.add(identity)
    return result


def _validate_hires(value: Any) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise WorkflowError("hires 必须是对象")
    unknown = set(value) - set(DEFAULT_HIRES)
    if unknown:
        raise WorkflowError(f"未知高清修复参数: {', '.join(sorted(unknown))}")
    merged = {**DEFAULT_HIRES, **value}
    return {
        "enabled": _boolean("hires.enabled", merged["enabled"]),
        "model_name": _text("hires.model_name", merged["model_name"]),
        "percent": _integer("hires.percent", merged["percent"], 1, 1000),
    }


def _validate_detailers(value: Any) -> dict[str, bool]:
    if not isinstance(value, dict):
        raise WorkflowError("detailers 必须是对象")
    unknown = set(value) - set(DEFAULT_DETAILERS)
    if unknown:
        raise WorkflowError(f"未知细节修复模块: {', '.join(sorted(unknown))}")
    merged = {**DEFAULT_DETAILERS, **value}
    return {name: _boolean(f"detailers.{name}", merged[name]) for name in DETAILER_ORDER}


def _validate_second_sampler(value: Any) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise WorkflowError("second_sampler 必须是对象")
    unknown = set(value) - set(DEFAULT_SECOND_SAMPLER)
    if unknown:
        raise WorkflowError(f"未知二次采样参数: {', '.join(sorted(unknown))}")
    merged = {**DEFAULT_SECOND_SAMPLER, **value}
    enabled = _boolean("second_sampler.enabled", merged["enabled"])
    steps = _integer("second_sampler.steps", merged["steps"], 1, 150)
    cfg = merged["cfg"]
    if isinstance(cfg, bool) or not isinstance(cfg, (int, float)):
        raise WorkflowError("second_sampler.cfg 必须是数字")
    if not math.isfinite(float(cfg)):
        raise WorkflowError("second_sampler.cfg 必须是有限数字")
    cfg = float(cfg)
    if not 0.1 <= cfg <= 30:
        raise WorkflowError("second_sampler.cfg 必须在 0.1-30 之间")
    sampler_name = _text("second_sampler.sampler_name", merged["sampler_name"])
    scheduler = _text("second_sampler.scheduler", merged["scheduler"])
    if not sampler_name:
        raise WorkflowError("second_sampler.sampler_name 不能为空")
    if not scheduler:
        raise WorkflowError("second_sampler.scheduler 不能为空")
    denoise = merged["denoise"]
    if isinstance(denoise, bool) or not isinstance(denoise, (int, float)):
        raise WorkflowError("second_sampler.denoise 必须是数字")
    if not math.isfinite(float(denoise)):
        raise WorkflowError("second_sampler.denoise 必须是有限数字")
    denoise = float(denoise)
    if not 0 <= denoise <= 1:
        raise WorkflowError("second_sampler.denoise 必须在 0-1 之间")
    return {
        "enabled": enabled,
        "steps": steps,
        "cfg": cfg,
        "sampler_name": sampler_name,
        "scheduler": scheduler,
        "denoise": denoise,
    }


def _validate_post_sampler(value: Any, field_prefix: str) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise WorkflowError(f"{field_prefix} 必须是对象")
    allowed = {"enabled", "steps", "cfg", "sampler_name", "scheduler", "denoise"}
    unknown = set(value) - allowed
    if unknown:
        raise WorkflowError(f"未知 {field_prefix} 参数: {', '.join(sorted(unknown))}")
    defaults = {
        "enabled": True,
        "steps": 20,
        "cfg": 8.0,
        "sampler_name": DEFAULT_SAMPLER,
        "scheduler": DEFAULT_SCHEDULER,
        "denoise": 1.0,
    }
    merged = {**defaults, **value}
    enabled = _boolean(f"{field_prefix}.enabled", merged["enabled"])
    steps = _integer(f"{field_prefix}.steps", merged["steps"], 1, 150)
    cfg = merged["cfg"]
    if isinstance(cfg, bool) or not isinstance(cfg, (int, float)):
        raise WorkflowError(f"{field_prefix}.cfg 必须是数字")
    cfg = float(cfg)
    if not math.isfinite(cfg) or not 0.1 <= cfg <= 30:
        raise WorkflowError(f"{field_prefix}.cfg 必须在 0.1-30 之间")
    sampler_name = _text(f"{field_prefix}.sampler_name", merged["sampler_name"])
    scheduler = _text(f"{field_prefix}.scheduler", merged["scheduler"])
    if not sampler_name or not scheduler:
        raise WorkflowError(f"{field_prefix} 的 sampler_name 和 scheduler 不能为空")
    denoise = merged["denoise"]
    if isinstance(denoise, bool) or not isinstance(denoise, (int, float)):
        raise WorkflowError(f"{field_prefix}.denoise 必须是数字")
    denoise = float(denoise)
    if not math.isfinite(denoise) or not 0 <= denoise <= 1:
        raise WorkflowError(f"{field_prefix}.denoise 必须在 0-1 之间")
    return {
        "enabled": enabled,
        "steps": steps,
        "cfg": cfg,
        "sampler_name": sampler_name,
        "scheduler": scheduler,
        "denoise": denoise,
    }


def _validate_upscalers(value: Any, profile_id: str = DEFAULT_MODEL_PROFILE) -> dict[str, Any]:
    """Validate the v2 post-processing chain.

    Each stage is independent; the renderer decides whether an installed
    optional node can be connected.  Validation deliberately only checks the
    persisted shape and numeric bounds so a missing optional ComfyUI plugin is
    reported by the resource preflight instead of corrupting settings.
    """

    if not isinstance(value, dict):
        raise WorkflowError("upscalers 必须是对象")
    allowed = {"latent", "seedvr2", "nvidia", "face_refine"}
    unknown = set(value) - allowed
    if unknown:
        raise WorkflowError(f"未知放大修复模块: {', '.join(sorted(unknown))}")
    merged = copy.deepcopy(DEFAULT_SETTINGS["upscalers"])
    for key, item in value.items():
        if not isinstance(item, dict):
            raise WorkflowError(f"upscalers.{key} 必须是对象")
        merged[key].update(copy.deepcopy(item))
    latent = merged["latent"]
    if set(latent) - {"enabled", "scale_by", "upscale_method", "model_format", "half_precision", "second_sampler"}:
        raise WorkflowError("未知潜空间放大参数")
    enabled = _boolean("upscalers.latent.enabled", latent["enabled"])
    scale_by = latent["scale_by"]
    if isinstance(scale_by, bool) or not isinstance(scale_by, (int, float)):
        raise WorkflowError("upscalers.latent.scale_by 必须是数字")
    if not math.isfinite(float(scale_by)) or not 0.01 <= float(scale_by) <= 64:
        raise WorkflowError("upscalers.latent.scale_by 必须在 0.01-64 之间")
    resize_options = validate_resize_options(latent, "upscalers.latent")
    second_sampler = _validate_post_sampler(
        latent["second_sampler"], "upscalers.latent.second_sampler"
    )
    result: dict[str, Any] = {
        "latent": {
            "enabled": enabled,
            "scale_by": float(scale_by),
            **resize_options,
            "second_sampler": second_sampler,
        }
    }

    seedvr2 = merged["seedvr2"]
    seedvr2_allowed = {
        "enabled", "model_name", "vae_name", "scale_by", "weight_dtype",
        "color_correction_method", "sampler", "encode", "decode",
    }
    if set(seedvr2) - seedvr2_allowed:
        raise WorkflowError("未知 upscalers.seedvr2 参数")
    profile = get_model_profile(profile_id)
    seedvr2_model = _text("upscalers.seedvr2.model_name", seedvr2.get("model_name") or profile["seedvr2_model"])
    seedvr2_vae = _text("upscalers.seedvr2.vae_name", seedvr2.get("vae_name") or profile["seedvr2_vae"])
    seedvr2_scale = seedvr2.get("scale_by")
    if isinstance(seedvr2_scale, bool) or not isinstance(seedvr2_scale, (int, float)):
        raise WorkflowError("upscalers.seedvr2.scale_by 必须是数字")
    seedvr2_scale = float(seedvr2_scale)
    if not math.isfinite(seedvr2_scale) or not 0.01 <= seedvr2_scale <= 64:
        raise WorkflowError("upscalers.seedvr2.scale_by 必须在 0.01-64 之间")
    weight_dtype = _text("upscalers.seedvr2.weight_dtype", seedvr2["weight_dtype"])
    color_correction = _text(
        "upscalers.seedvr2.color_correction_method", seedvr2["color_correction_method"]
    )
    seed_sampler = seedvr2["sampler"]
    if not isinstance(seed_sampler, dict):
        raise WorkflowError("upscalers.seedvr2.sampler 必须是对象")
    seed_sampler = {
        **DEFAULT_SETTINGS["upscalers"]["seedvr2"]["sampler"],
        **seed_sampler,
    }
    seed_sampler = _validate_post_sampler(
        {"enabled": True, **seed_sampler}, "upscalers.seedvr2.sampler"
    )
    seed_sampler.pop("enabled", None)

    def validate_tiles(name: str, item: Any) -> dict[str, int]:
        defaults = DEFAULT_SETTINGS["upscalers"]["seedvr2"][name]
        if not isinstance(item, dict):
            raise WorkflowError(f"upscalers.seedvr2.{name} 必须是对象")
        unknown = set(item) - set(defaults)
        if unknown:
            raise WorkflowError(f"未知 upscalers.seedvr2.{name} 参数: {', '.join(sorted(unknown))}")
        values = {**defaults, **item}
        return {
            "tile_size": _integer(f"upscalers.seedvr2.{name}.tile_size", values["tile_size"], 64, 16384),
            "overlap": _integer(f"upscalers.seedvr2.{name}.overlap", values["overlap"], 0, 4096),
            "temporal_size": _integer(f"upscalers.seedvr2.{name}.temporal_size", values["temporal_size"], 1, 16384),
            "temporal_overlap": _integer(f"upscalers.seedvr2.{name}.temporal_overlap", values["temporal_overlap"], 0, 4096),
        }

    result["seedvr2"] = {
        "enabled": _boolean("upscalers.seedvr2.enabled", seedvr2["enabled"]),
        "model_name": seedvr2_model,
        "vae_name": seedvr2_vae,
        "scale_by": seedvr2_scale,
        "weight_dtype": weight_dtype,
        "color_correction_method": color_correction,
        "sampler": seed_sampler,
        "encode": validate_tiles("encode", seedvr2["encode"]),
        "decode": validate_tiles("decode", seedvr2["decode"]),
    }

    nvidia = merged["nvidia"]
    if set(nvidia) - {"enabled", "resize_type", "scale", "quality", "model_name", "dlss"}:
        raise WorkflowError("未知 upscalers.nvidia 参数")
    scale = nvidia["scale"]
    if isinstance(scale, bool) or not isinstance(scale, (int, float)):
        raise WorkflowError("upscalers.nvidia.scale 必须是数字")
    scale = float(scale)
    if not math.isfinite(scale) or not 0.01 <= scale <= 64:
        raise WorkflowError("upscalers.nvidia.scale 必须在 0.01-64 之间")
    resize_type = _text("upscalers.nvidia.resize_type", nvidia["resize_type"])
    quality = _text("upscalers.nvidia.quality", nvidia["quality"])
    if not resize_type or not quality:
        raise WorkflowError("upscalers.nvidia.resize_type 和 quality 不能为空")
    result["nvidia"] = {
        "enabled": _boolean("upscalers.nvidia.enabled", nvidia["enabled"]),
        "resize_type": resize_type,
        "scale": scale,
        "quality": quality,
        "dlss": validate_dlss(nvidia["dlss"]),
    }

    face = merged["face_refine"]
    face_defaults = DEFAULT_SETTINGS["upscalers"]["face_refine"]
    if set(face) - set(face_defaults):
        raise WorkflowError("未知 upscalers.face_refine 参数")
    face_values = {**face_defaults, **face}
    face_float_ranges = {
        "cfg_scale": (0.1, 30.0),
        "denoise_strength": (0.0, 1.0),
        "detection_confidence": (0.0, 1.0),
        "crop_padding": (0.0, 16.0),
        "sampling_mask_blur_strength": (0.0, 6.0),
    }
    face_int_ranges = {
        "steps": (1, 150),
        "face_selection": (0, 1000000),
        "processing_resolution": (64, 8192),
        "blend_softness": (0, 4096),
        "mask_expansion": (0, 4096),
        "sampling_mask_blur_size": (0, 4096),
    }
    validated_face: dict[str, Any] = {
        "enabled": _boolean("upscalers.face_refine.enabled", face_values["enabled"])
    }
    for name, bounds in face_float_ranges.items():
        number = face_values[name]
        if isinstance(number, bool) or not isinstance(number, (int, float)):
            raise WorkflowError(f"upscalers.face_refine.{name} 必须是数字")
        number = float(number)
        if not math.isfinite(number) or not bounds[0] <= number <= bounds[1]:
            raise WorkflowError(f"upscalers.face_refine.{name} 超出允许范围")
        validated_face[name] = number
    for name, bounds in face_int_ranges.items():
        validated_face[name] = _integer(
            f"upscalers.face_refine.{name}", face_values[name], bounds[0], bounds[1]
        )
    for name in (
        "sampler", "scheduler", "manual_rotation", "upscaler_model",
        "face_positive_prompt", "face_negative_prompt", "exclusions",
    ):
        validated_face[name] = _text(f"upscalers.face_refine.{name}", face_values[name])
    if not validated_face["sampler"] or not validated_face["scheduler"]:
        raise WorkflowError("脸部细化 sampler 和 scheduler 不能为空")
    for name in (
        "enable_pre_upscale", "replace_positive_prompt", "replace_negative_prompt",
        "enable_color_correction", "enable_segmentation", "enable_differential_diffusion",
        "enable_lightness_rescue", "enable_final_refinement", "offload_models_to_cpu",
    ):
        validated_face[name] = _boolean(f"upscalers.face_refine.{name}", face_values[name])
    result["face_refine"] = validated_face
    return result


def _validate_krea_prompt_pool(value: Any) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise WorkflowError("krea_prompt_pool 必须是对象")
    unknown = set(value) - {"enabled", "ids"}
    if unknown:
        raise WorkflowError(f"未知 krea_prompt_pool 参数: {', '.join(sorted(unknown))}")
    enabled = _boolean("krea_prompt_pool.enabled", value.get("enabled", False))
    ids = value.get("ids", [])
    if not isinstance(ids, list):
        raise WorkflowError("krea_prompt_pool.ids 必须是数组")
    if len(ids) > 50000:
        raise WorkflowError("krea_prompt_pool 条目过多")
    normalized: list[str] = []
    seen: set[str] = set()
    for index, item in enumerate(ids):
        text = _text(f"krea_prompt_pool.ids[{index}]", item)
        identity = text.casefold()
        if text and identity not in seen:
            normalized.append(text)
            seen.add(identity)
    return {"enabled": enabled, "ids": normalized}


_LEGACY_SETTING_KEYS = {"hires", "detailers", "cfg_zero_star", "second_sampler"}


def migrate_settings(overrides: dict[str, Any] | None = None) -> dict[str, Any]:
    """Migrate v1 persisted settings to the v2 model/profile contract.

    The input is never modified. The returned object omits the old standalone
    detailer, CFGZeroStar and second-sampler keys; legacy second-sampler values
    move into ``upscalers.latent.second_sampler``. A v1 ``hires`` block becomes
    the latent stage (percent is converted to a scale factor). Explicit main
    model selections are preserved for the resource-ownership preflight.
    """

    if overrides is None:
        source: dict[str, Any] = {}
    elif isinstance(overrides, dict):
        source = copy.deepcopy(overrides)
    else:
        raise WorkflowError("设置必须是对象")
    allowed = set(DEFAULT_SETTINGS) | _LEGACY_SETTING_KEYS
    unknown = set(source) - allowed
    if unknown:
        raise WorkflowError(f"未知参数: {', '.join(sorted(unknown))}")

    raw_profile = source.get("model_profile")
    if raw_profile is None:
        old_model = str(source.get("model_name") or "").casefold()
        krea_name = MODEL_PROFILES["krea2"]["model_name"].casefold()
        raw_profile = "krea2" if old_model == krea_name else DEFAULT_MODEL_PROFILE
    try:
        profile_id = normalize_model_profile(raw_profile)
    except ModelProfileError as error:
        raise WorkflowError(str(error)) from error
    profile = get_model_profile(profile_id)
    result = copy.deepcopy(source)
    result["model_profile"] = profile_id
    workbench = source.get("workbench", "random")
    if not isinstance(workbench, str) or workbench not in {"random", "single"}:
        raise WorkflowError("workbench 必须是 random 或 single")
    generation_mode = source.get("generation_mode", "txt2img")
    if not isinstance(generation_mode, str) or generation_mode not in {"txt2img", "img2img"}:
        raise WorkflowError("generation_mode 必须是 txt2img 或 img2img")
    if workbench == "random" and generation_mode != "txt2img":
        raise WorkflowError("随机工作台仅支持文生图")
    result["workbench"] = workbench
    result["generation_mode"] = generation_mode
    raw_single = source.get("single", {})
    if not isinstance(raw_single, dict):
        raise WorkflowError("single 必须是对象")
    unlocked = workbench == "single" and raw_single.get("unlock_resources", True) is True
    # Unlocked single settings may select custom resources, but a canonical
    # resource from another profile is a stale default, not a custom override.
    # Repair old browser drafts/history as well as newly switched settings.
    for resource_key in ("clip_name", "vae_name", "clip_type"):
        value = source.get(resource_key, profile[resource_key]) if unlocked else profile[resource_key]
        if unlocked and isinstance(value, str):
            normalized = value.replace("\\", "/").rsplit("/", 1)[-1].casefold()
            foreign_defaults = {
                other[resource_key].casefold()
                for other_id, other in MODEL_PROFILES.items()
                if other_id != profile_id and other[resource_key] != profile[resource_key]
            }
            if normalized in foreign_defaults:
                value = profile[resource_key]
        result[resource_key] = value
    # Switching profiles should reset a previous profile's canonical UNet,
    # while a custom explicitly assigned UNet remains selected. Ownership is
    # enforced at the request/resource boundary where the live inventory is
    # available; locked CLIP/VAE and encoder type stay profile-bound.
    raw_model_name = str(source.get("model_name") or "")
    other_profile_defaults = {
        str(value["model_name"]).casefold()
        for key, value in MODEL_PROFILES.items()
        if key != profile_id
    }
    if raw_model_name and raw_model_name.casefold() not in other_profile_defaults:
        # The profile owns its CLIP/VAE and prompt namespace, while users may
        # choose any UNet explicitly assigned to that profile. Ownership is
        # enforced at the request/resource boundary where the live inventory
        # is available; this normalization layer only validates the shape.
        result["model_name"] = raw_model_name
    else:
        result["model_name"] = profile["model_name"]
    for key in ("steps", "cfg", "sampler_name", "scheduler"):
        if key not in result:
            result[key] = profile["defaults"][key]

    raw_base = result.get("base_mode")
    if raw_base is None:
        legacy_second = source.get("second_sampler")
        if workbench == "single":
            raw_base = "normal"
        elif isinstance(legacy_second, dict) and legacy_second.get("enabled"):
            raw_base = "dual_sampler"
        else:
            raw_base = profile["defaults"]["base_mode"]
    if not isinstance(raw_base, str) or raw_base not in {"normal", "dual_sampler"}:
        raise WorkflowError("base_mode 必须是 normal 或 dual_sampler")
    result["base_mode"] = raw_base
    if "steps" not in source:
        result["steps"] = 30 if raw_base == "dual_sampler" else 20
    # New clients send explicit shared defaults. Older snapshots keep their
    # effective variance behavior and all hidden controls during migration.
    if "krea_variance" not in source:
        result["krea_variance"] = copy.deepcopy(raw_single.get("krea_variance", {})) if workbench == "single" else {"enabled": profile_id == "krea2"}
    elif workbench == "single" and isinstance(raw_single.get("krea_variance"), dict) and isinstance(source["krea_variance"], dict):
        result["krea_variance"] = {**copy.deepcopy(raw_single["krea_variance"]), **source["krea_variance"]}
    if "base_denoise" not in source:
        result["base_denoise"] = 1.0
    if "dual" not in source:
        result["dual"] = None
    if workbench == "single":
        result["count"] = 1

    upscalers = copy.deepcopy(DEFAULT_SETTINGS["upscalers"])
    if isinstance(result.get("upscalers"), dict):
        for key, item in result["upscalers"].items():
            if isinstance(item, dict) and key in upscalers:
                upscalers[key].update(item)
    # An explicit v2 ``upscalers`` block wins over a legacy ``hires`` key.
    # DEFAULT_SETTINGS still carries the legacy fields for v1 compatibility,
    # so applying both unconditionally would silently turn every disabled
    # latent stage back on during validation.
    legacy_hires = source.get("hires") if "upscalers" not in source else None
    if isinstance(legacy_hires, dict):
        # Reuse v1 validation before translating it; invalid persisted values
        # should fail with the same actionable error as a v1 request.
        legacy_hires = _validate_hires(legacy_hires)
        if "enabled" in legacy_hires:
            upscalers["latent"]["enabled"] = legacy_hires["enabled"]
        if "percent" in legacy_hires:
            # v1 applied the percentage to a 4x pixel upscaler.  Preserve the
            # resulting geometry when moving to the direct latent scale.
            upscalers["latent"]["scale_by"] = float(legacy_hires["percent"]) / 100 * 4
            upscalers["latent"]["upscale_method"] = "bilinear"
    # Keep old v1 second-sampler settings executable by moving them into the
    # new latent-stage contract. A unit latent scale avoids adding a resize to
    # old settings which had no latent-upscale stage.
    legacy_second = source.get("second_sampler")
    # A new v2 payload may carry DEFAULT_SETTINGS' disabled compatibility
    # alias alongside a partial ``upscalers`` block. In that case its nested
    # sampler default is authoritative; only copy a disabled legacy sampler
    # when migrating a v1 payload that has no v2 upscalers block.
    if isinstance(legacy_second, dict) and (
        legacy_second.get("enabled") or "upscalers" not in source
    ):
        latent_settings = upscalers["latent"]
        raw_upscalers = source.get("upscalers")
        explicit_latent = (
            raw_upscalers.get("latent", {}) if isinstance(raw_upscalers, dict) else {}
        )
        if not isinstance(explicit_latent, dict):
            explicit_latent = {}
        if "second_sampler" not in explicit_latent:
            if legacy_second.get("enabled"):
                latent_settings["enabled"] = True
                if (
                    "hires" not in source
                    and latent_settings.get("scale_by")
                    == DEFAULT_SETTINGS["upscalers"]["latent"]["scale_by"]
                ):
                    latent_settings["scale_by"] = 1.0
            latent_settings["second_sampler"] = {
                **copy.deepcopy(DEFAULT_SETTINGS["upscalers"]["latent"]["second_sampler"]),
                **{
                    key: legacy_second[key]
                    for key in ("enabled", "steps", "cfg", "sampler_name", "scheduler", "denoise")
                    if key in legacy_second
                },
            }
    # Loader names use profile defaults only when no explicit selection was
    # persisted. The selected resources are checked against live inventories
    # by BatchManager and can be restored on profile switches by the UI.
    seed_defaults = copy.deepcopy(DEFAULT_SETTINGS["upscalers"]["seedvr2"])
    seed_defaults.update(
        {
            "model_name": profile["seedvr2_model"],
            "vae_name": profile["seedvr2_vae"],
        }
    )
    seed_defaults.update(upscalers.get("seedvr2", {}))
    upscalers["seedvr2"] = seed_defaults
    raw_nvidia = source.get("upscalers", {}).get("nvidia", {}) if isinstance(source.get("upscalers"), dict) else {}
    if isinstance(raw_nvidia, dict) and "dlss" in raw_single:
        if "dlss" not in raw_nvidia:
            upscalers["nvidia"]["dlss"] = copy.deepcopy(raw_single["dlss"])
        elif isinstance(raw_single["dlss"], dict) and isinstance(raw_nvidia["dlss"], dict):
            upscalers["nvidia"]["dlss"] = {**copy.deepcopy(raw_single["dlss"]), **raw_nvidia["dlss"]}
    result["upscalers"] = upscalers
    for key in _LEGACY_SETTING_KEYS:
        result.pop(key, None)
    return result


def validate_settings(overrides: dict[str, Any] | None = None) -> dict[str, Any]:
    raw_overrides = copy.deepcopy(overrides or {})
    overrides = migrate_settings(raw_overrides)
    unknown = set(overrides) - set(DEFAULT_SETTINGS)
    if unknown:
        raise WorkflowError(f"未知参数: {', '.join(sorted(unknown))}")

    legacy_pools = "pools" not in overrides and any(overrides.get(f"random_{section}") for section in PROMPT_SECTIONS)
    settings = copy.deepcopy(DEFAULT_SETTINGS)
    for legacy_key in _LEGACY_SETTING_KEYS:
        settings.pop(legacy_key, None)
    settings.update(overrides)

    # Preserve the validated aliases for callers that explicitly target the
    # legacy hand-numbered template.  A v2 caller is migrated at the runner
    # boundary (and build_submission removes these keys from v2 metadata), so
    # they never become part of a v2 batch/history snapshot.
    if set(raw_overrides) & _LEGACY_SETTING_KEYS:
        settings["hires"] = _validate_hires(
            raw_overrides.get("hires", DEFAULT_HIRES)
        )
        settings["detailers"] = _validate_detailers(
            raw_overrides.get("detailers", DEFAULT_DETAILERS)
        )
        settings["cfg_zero_star"] = _boolean(
            "cfg_zero_star", raw_overrides.get("cfg_zero_star", False)
        )
        settings["second_sampler"] = _validate_second_sampler(
            raw_overrides.get("second_sampler", DEFAULT_SECOND_SAMPLER)
        )

    # Resolve the model profile before validating fields controlled by it.
    try:
        # ``migrate_settings`` adds a canonical profile to every payload.  A
        # profile is explicit only when the caller actually selected it.  An
        # untagged v1 payload can retain its custom Anima UNet, while an
        # explicit profile switch is authoritative for every bound loader.
        explicit_profile = "model_profile" in raw_overrides
        profile_id = normalize_model_profile(
            overrides.get("model_profile", settings.get("model_profile", DEFAULT_MODEL_PROFILE))
        )
    except ModelProfileError as error:
        raise WorkflowError(str(error)) from error
    profile = get_model_profile(profile_id)
    if explicit_profile:
        settings.update(
            {
                "model_profile": profile_id,
                "model_name": overrides.get("model_name", profile["model_name"]),
                "clip_name": overrides["clip_name"],
                "vae_name": overrides["vae_name"],
                "clip_type": overrides["clip_type"],
            }
        )
        # Sampling defaults are profile controlled only when the caller did
        # not provide an explicit value for that field.
        for key in ("steps", "cfg", "sampler_name", "scheduler"):
            if key not in overrides:
                settings[key] = profile["defaults"][key]
        if "base_mode" not in overrides:
            settings["base_mode"] = profile["defaults"]["base_mode"]
    else:
        settings["model_profile"] = profile_id
        settings.setdefault("clip_name", profile["clip_name"])
        settings.setdefault("vae_name", profile["vae_name"])
        settings.setdefault("clip_type", profile["clip_type"])
    settings["count"] = _integer("生成数量", settings["count"], 1, 1000)
    for name in tuple(f"random_{section}" for section in PROMPT_SECTIONS):
        settings[name] = _boolean(name, settings[name])
    for name in tuple(f"random_{section}_count" for section in PROMPT_SECTIONS):
        settings[name] = _integer(name, settings[name], 1, 5)
    if settings["random_expression_count"] != 1:
        raise WorkflowError("表情随机池每张只能抽取 1 项")
    settings["female_count"] = _integer("女性人数", settings["female_count"], 0, 5)
    settings["male_count"] = _integer("男性人数", settings["male_count"], 0, 5)
    total_people = settings["female_count"] + settings["male_count"]
    if total_people > 5:
        raise WorkflowError("画面总人数不能超过 5")
    if settings["random_character"] and total_people and settings["random_character_count"] > total_people:
        raise WorkflowError("指定角色数不能超过画面总人数")

    pools = settings.get("pools")
    if legacy_pools:
        pools = {
            section: {"mode": "all", "ids": [], "excluded_ids": []}
            for section in PROMPT_SECTIONS
        }
    if not isinstance(pools, dict):
        raise WorkflowError("pools 必须是对象")
    normalized_pools = {}
    for section in PROMPT_SECTIONS:
        selection = pools.get(section, {})
        if not isinstance(selection, dict):
            raise WorkflowError(f"{section} 随机池设置无效")
        mode = selection.get("mode", "include")
        if not isinstance(mode, str) or mode not in {"include", "all"}:
            raise WorkflowError(f"{section} 随机池模式无效")
        ids = selection.get("ids", [])
        excluded_ids = selection.get("excluded_ids", [])
        if not isinstance(ids, list) or not isinstance(excluded_ids, list):
            raise WorkflowError(f"{section} 随机池条目必须是数组")
        if len(ids) > 50000 or len(excluded_ids) > 50000:
            raise WorkflowError(f"{section} 随机池条目过多")
        normalized_pools[section] = {
            "mode": mode,
            "ids": list(dict.fromkeys(_text(f"{section} 条目", value) for value in ids)),
            "excluded_ids": list(dict.fromkeys(_text(f"{section} 排除条目", value) for value in excluded_ids)),
        }
    settings["pools"] = normalized_pools
    settings["krea_prompt_pool"] = _validate_krea_prompt_pool(
        settings.get("krea_prompt_pool", DEFAULT_SETTINGS["krea_prompt_pool"])
    )
    settings["model_name"] = _text("model_name", settings["model_name"])
    if not settings["model_name"]:
        raise WorkflowError("model_name 不能为空")
    for name in ("clip_name", "vae_name", "clip_type"):
        settings[name] = _text(name, settings[name])
        if not settings[name]:
            raise WorkflowError(f"{name} 不能为空")
    base_mode = settings.get("base_mode")
    if not isinstance(base_mode, str) or base_mode not in {"normal", "dual_sampler"}:
        raise WorkflowError("base_mode 必须是 normal 或 dual_sampler")
    settings["upscalers"] = _validate_upscalers(settings.get("upscalers"), profile_id)
    settings["loras"] = validate_loras(settings)
    settings["lora_managed_triggers"] = _normalize_managed_triggers(
        settings["lora_managed_triggers"]
    )

    if not isinstance(settings["character_detail"], str) or settings["character_detail"] not in {"trigger", "trigger_tags"}:
        raise WorkflowError("character_detail 必须是 trigger 或 trigger_tags")

    settings["manual_artist"] = (
        normalize_artist_tags(settings["manual_artist"]) if profile_id == "anima"
        else _text("manual_artist", settings["manual_artist"])
    )
    for name in (
        "fixed_character",
        "fixed_clothing",
        "fixed_pose",
        "fixed_background",
        "fixed_expression",
        "quality_prompt",
        "extra_prompt",
        "negative_prompt",
    ):
        settings[name] = _text(name, settings[name])

    for name in ("width", "height"):
        settings[name] = _integer(name, settings[name], 64, 4096)
        if settings[name] % 8:
            raise WorkflowError(f"{name} 必须能被 8 整除")

    settings["steps"] = _integer("steps", settings["steps"], 1, 150)
    if isinstance(settings["cfg"], bool) or not isinstance(settings["cfg"], (int, float)):
        raise WorkflowError("cfg 必须是数字")
    settings["cfg"] = float(settings["cfg"])
    if not 0.1 <= settings["cfg"] <= 30:
        raise WorkflowError("cfg 必须在 0.1-30 之间")
    settings["sampler_name"] = _text("sampler_name", settings["sampler_name"])
    settings["scheduler"] = _text("scheduler", settings["scheduler"])
    if not settings["sampler_name"]:
        raise WorkflowError("sampler_name 不能为空")
    if not settings["scheduler"]:
        raise WorkflowError("scheduler 不能为空")
    denoise = settings["base_denoise"]
    if isinstance(denoise, bool) or not isinstance(denoise, (int, float)) or not math.isfinite(denoise) or not 0 <= denoise <= 1:
        raise WorkflowError("base_denoise 必须在 0-1 之间")
    settings["base_denoise"] = float(denoise)
    settings["krea_variance"] = validate_krea_variance(settings["krea_variance"], profile_id)
    settings["dual"] = validate_random_dual(settings.get("dual"), settings)
    latent = settings["upscalers"]["latent"]
    # Single keeps its existing independent single.latent namespace. Random
    # legacy dual graphs also use this resize even when postprocessing is off.
    if settings["workbench"] == "random":
        resize_active = latent["enabled"] or settings["base_mode"] == "dual_sampler" and settings["dual"] is None
        validate_resize_scale(latent["scale_by"], latent["upscale_method"],
                              "upscalers.latent.scale_by", active=resize_active)
    settings["single"] = validate_single_settings(settings.get("single", {}), settings)
    return settings


def _remove_visual_nodes(workflow: dict[str, Any]) -> set[int]:
    workflow["nodes"] = [node for node in workflow["nodes"] if node.get("id") not in REMOVED_NODE_IDS]
    removed_links = {
        int(link[0])
        for link in workflow.get("links", [])
        if int(link[1]) in REMOVED_NODE_IDS or int(link[3]) in REMOVED_NODE_IDS
    }
    workflow["links"] = [link for link in workflow.get("links", []) if int(link[0]) not in removed_links]
    for node in workflow["nodes"]:
        for input_value in node.get("inputs", []):
            if input_value.get("link") in removed_links:
                input_value["link"] = None
        for output in node.get("outputs", []):
            links = output.get("links")
            if isinstance(links, list):
                output["links"] = [link_id for link_id in links if link_id not in removed_links] or None
    return removed_links


def _visual_node(workflow: dict[str, Any], node_id: int) -> dict[str, Any]:
    for node in workflow["nodes"]:
        if int(node.get("id")) == node_id:
            return node
    raise WorkflowError(f"工作流缺少节点 {node_id}")


def _prepare_hires_nodes(api: dict[str, Any], ui: dict[str, Any]) -> None:
    existing = api.get(str(HIRES_SCALE_ID), {})
    existing_inputs = existing.get("inputs", {})
    if existing.get("class_type") == "ImageScaleBy":
        model_name = api.get(str(HIRES_MODEL_ID), {}).get("inputs", {}).get(
            "model_name", DEFAULT_HIRES["model_name"]
        )
        percent = round(float(existing_inputs.get("scale_by", DEFAULT_HIRES["percent"] / 100)) * 100)
    else:
        model_name = existing_inputs.get("model_name", DEFAULT_HIRES["model_name"])
        percent = existing_inputs.get("percent", DEFAULT_HIRES["percent"])
    api[str(HIRES_MODEL_ID)] = {
        "inputs": {"model_name": model_name},
        "class_type": "UpscaleModelLoader",
        "_meta": {"title": "高清修复模型"},
    }
    api[str(HIRES_UPSCALE_ID)] = {
        "inputs": {
            "upscale_model": [str(HIRES_MODEL_ID), 0],
            "image": ["48", 0],
        },
        "class_type": "ImageUpscaleWithModel",
        "_meta": {"title": "高清模型放大"},
    }
    api[str(HIRES_SCALE_ID)] = {
        "inputs": {
            "image": [str(HIRES_UPSCALE_ID), 0],
            "upscale_method": "lanczos",
            "scale_by": percent / 100,
        },
        "class_type": "ImageScaleBy",
        "_meta": {"title": "高清输出比例"},
    }
    api["12"]["inputs"]["images"] = [str(HIRES_SCALE_ID), 0]

    scale_node = _visual_node(ui, HIRES_SCALE_ID)
    if scale_node.get("type") == "ImageScaleBy":
        _visual_node(ui, HIRES_MODEL_ID)
        _visual_node(ui, HIRES_UPSCALE_ID)
        ui["last_node_id"] = max(int(ui.get("last_node_id", 0)), HIRES_UPSCALE_ID)
        return
    image_link = next(item["link"] for item in scale_node["inputs"] if item["name"] == "image")
    model_link = next(item["link"] for item in scale_node["inputs"] if item["name"] == "vae")
    output_links = next(item["links"] for item in scale_node["outputs"] if item["name"] == "image")
    scale_link = max((int(link[0]) for link in ui.get("links", [])), default=0) + 1

    for link in ui["links"]:
        if int(link[0]) == image_link:
            link[3:6] = [HIRES_UPSCALE_ID, 1, "IMAGE"]
        elif int(link[0]) == model_link:
            link[1:6] = [HIRES_MODEL_ID, 0, HIRES_UPSCALE_ID, 0, "UPSCALE_MODEL"]
        elif int(link[0]) in output_links:
            link[1] = HIRES_SCALE_ID
            link[2] = 0
    ui["links"].append([scale_link, HIRES_UPSCALE_ID, 0, HIRES_SCALE_ID, 0, "IMAGE"])

    vae_node = _visual_node(ui, 22)
    for output in vae_node.get("outputs", []):
        links = output.get("links")
        if isinstance(links, list) and model_link in links:
            output["links"] = [link for link in links if link != model_link] or None

    x, y = scale_node["pos"]
    common_properties = {"cnr_id": "comfy-core", "ver": "0.11.0"}
    ui["nodes"].extend(
        [
            {
                "id": HIRES_MODEL_ID,
                "type": "UpscaleModelLoader",
                "pos": [x - 650, y],
                "size": [300, 82],
                "flags": {},
                "order": scale_node.get("order", 0),
                "mode": scale_node.get("mode", 0),
                "inputs": [],
                "outputs": [{"name": "UPSCALE_MODEL", "type": "UPSCALE_MODEL", "links": [model_link]}],
                "properties": {**common_properties, "Node name for S&R": "UpscaleModelLoader"},
                "widgets_values": [model_name],
                "title": "高清修复模型",
            },
            {
                "id": HIRES_UPSCALE_ID,
                "type": "ImageUpscaleWithModel",
                "pos": [x - 330, y],
                "size": [300, 82],
                "flags": {},
                "order": scale_node.get("order", 0) + 1,
                "mode": scale_node.get("mode", 0),
                "inputs": [
                    {"name": "upscale_model", "type": "UPSCALE_MODEL", "link": model_link},
                    {"name": "image", "type": "IMAGE", "link": image_link},
                ],
                "outputs": [{"name": "IMAGE", "type": "IMAGE", "links": [scale_link]}],
                "properties": {**common_properties, "Node name for S&R": "ImageUpscaleWithModel"},
                "widgets_values": [],
                "title": "高清模型放大",
            },
        ]
    )
    scale_node.update(
        {
            "type": "ImageScaleBy",
            "size": [300, 110],
            "inputs": [{"name": "image", "type": "IMAGE", "link": scale_link}],
            "outputs": [{"name": "IMAGE", "type": "IMAGE", "links": output_links}],
            "properties": {**common_properties, "Node name for S&R": "ImageScaleBy"},
            "widgets_values": ["lanczos", percent / 100],
            "title": "高清输出比例",
        }
    )
    ui["last_node_id"] = max(int(ui.get("last_node_id", 0)), HIRES_UPSCALE_ID)
    ui["last_link_id"] = max(int(ui.get("last_link_id", 0)), scale_link)


def _set_native_positive(api: dict[str, Any], ui: dict[str, Any], text: str) -> None:
    """Remove the plugin's queue-time random resolver from both graph forms."""
    positive = api[str(POSITIVE_ID)]
    positive["class_type"] = "CLIPTextEncode"
    positive["inputs"] = {"clip": positive["inputs"]["clip"], "text": text}
    api.pop(str(COMPOSER_ID), None)
    node = _visual_node(ui, POSITIVE_ID)
    clip_link = next(item.get("link") for item in node["inputs"] if item["name"] == "clip")
    removed = {
        link[0] for link in ui.get("links", [])
        if COMPOSER_ID in (link[1], link[3])
        or (link[3] == POSITIVE_ID and link[0] != clip_link)
    }
    ui["links"] = [link for link in ui.get("links", []) if link[0] not in removed]
    ui["nodes"] = [item for item in ui["nodes"] if item["id"] != COMPOSER_ID]
    for item in ui["nodes"]:
        for value in item.get("inputs", []):
            if value.get("link") in removed:
                value["link"] = None
        for output in item.get("outputs", []):
            if isinstance(output.get("links"), list):
                output["links"] = [link for link in output["links"] if link not in removed] or None
    node.update({
        "type": "CLIPTextEncode",
        "title": "正向提示词",
        "inputs": [
            {"name": "clip", "type": "CLIP", "link": clip_link},
            {"name": "text", "type": "STRING", "widget": {"name": "text"}, "link": None},
        ],
        "outputs": [{"name": "CONDITIONING", "type": "CONDITIONING", "slot_index": 0,
                     "links": node["outputs"][0].get("links")}],
        "widgets_values": [text],
        "properties": {"Node name for S&R": "CLIPTextEncode", "cnr_id": "comfy-core"},
    })


def _legacy_positive_prompt(settings: dict[str, Any], resolved: str, full: str) -> str:
    if full.strip():
        return full
    if not resolved.strip() and any(settings.get(f"random_{section}") for section in PROMPT_SECTIONS):
        raise WorkflowError("随机提示词尚未解析，无法提交生成")
    values = [settings["quality_prompt"], settings["manual_artist"]]
    for section in PROMPT_SECTIONS:
        if not settings.get(f"random_{section}"):
            values.append(settings.get(f"fixed_{section}", ""))
    if resolved.strip():
        values.append(resolved)
    else:
        for count, singular, plural in (
            (settings.get("female_count", 0), "1girl", "girls"),
            (settings.get("male_count", 0), "1boy", "boys"),
        ):
            if count:
                values.append(singular if count == 1 else f"{count}{plural}")
    values.append(settings.get("extra_prompt", ""))
    parts: list[str] = []
    seen: set[str] = set()
    for value in values:
        for part in str(value).replace("\n", ",").replace("\r", ",").split(","):
            part = part.replace("_raw_:", "", 1).strip()
            if part and part.casefold() not in seen:
                seen.add(part.casefold())
                parts.append(part)
    return ", ".join(parts)


def prepare_templates(
    api_source: dict[str, Any], ui_source: dict[str, Any]
) -> tuple[dict[str, Any], dict[str, Any]]:
    api = copy.deepcopy(api_source)
    ui = copy.deepcopy(ui_source)

    for node_id in ("3", "4"):
        api.pop(node_id, None)
    _prepare_hires_nodes(api, ui)
    _remove_visual_nodes(ui)
    _set_native_positive(api, ui, DEFAULT_QUALITY)
    negative = api[str(NEGATIVE_ID)]
    negative["inputs"] = {"text": DEFAULT_NEGATIVE, "clip": negative["inputs"]["clip"]}

    negative_ui = _visual_node(ui, NEGATIVE_ID)
    negative_clip_link = next(item["link"] for item in negative_ui["inputs"] if item["name"] == "clip")
    negative_ui["pos"] = [1485, 555]
    negative_ui["size"] = [430, 175]
    negative_ui["inputs"] = [
        {"name": "clip", "type": "CLIP", "link": negative_clip_link},
        {"name": "text", "type": "STRING", "widget": {"name": "text"}, "link": None},
    ]
    negative_ui["widgets_values"] = [DEFAULT_NEGATIVE]

    return api, ui


def _set_ui_widget(ui: dict[str, Any], node_id: int, value: Any, index: int | None = None) -> None:
    node = _visual_node(ui, node_id)
    if index is None:
        node["widgets_values"] = value
        return
    values = list(node.get("widgets_values") or [])
    while len(values) <= index:
        values.append("")
    values[index] = value
    node["widgets_values"] = values


def _set_ui_mode(ui: dict[str, Any], node_id: int, enabled: bool) -> None:
    _visual_node(ui, node_id)["mode"] = 0 if enabled else 4


def _disconnect_ui_input(ui: dict[str, Any], node_id: int, input_name: str) -> None:
    node = _visual_node(ui, node_id)
    input_spec = next((item for item in node.get("inputs", []) if item.get("name") == input_name), None)
    if not isinstance(input_spec, dict):
        return
    link_id = input_spec.get("link")
    input_spec["link"] = None
    if link_id is None:
        return
    ui["links"] = [link for link in ui.get("links", []) if int(link[0]) != int(link_id)]
    for candidate in ui.get("nodes", []):
        for output in candidate.get("outputs", []):
            links = output.get("links")
            if isinstance(links, list):
                output["links"] = [value for value in links if int(value) != int(link_id)] or None


def _ui_widgets(ui: dict[str, Any], node_id: int) -> list[Any]:
    return list(_visual_node(ui, node_id).get("widgets_values") or [])


def _detailer_nodes(
    ui: dict[str, Any], name: str, image: list[Any]
) -> tuple[dict[str, dict[str, Any]], list[Any]]:
    config = DETAILER_NODES[name]
    detector_id = config["detector"]
    editor_id = config["editor"]
    detailer_id = config["detailer"]
    detector_ui = _visual_node(ui, detector_id)
    detector_widgets = _ui_widgets(ui, detector_id)
    editor_widgets = _ui_widgets(ui, editor_id)
    detailer_widgets = _ui_widgets(ui, detailer_id)
    detector_type = detector_ui["type"]
    detector = {
        "inputs": {"model_name": detector_widgets[0]},
        "class_type": detector_type,
        "_meta": {"title": detector_ui.get("title") or detector_type},
    }
    editor_inputs = {
        "wildcard": editor_widgets[0],
        "Select to add LoRA": "Select the LoRA to add to the text",
        "Select to add Wildcard": "Select the Wildcard to add to the text",
        "detailer_pipe": ["18", 0],
        config["detector_input"]: [str(detector_id), 0],
    }
    editor = {
        "inputs": editor_inputs,
        "class_type": "EditDetailerPipe",
        "_meta": {"title": _visual_node(ui, editor_id).get("title") or f"{name.title()} Detailer Pipe"},
    }
    detailer_inputs = {
        "image": image,
        "detailer_pipe": [str(editor_id), 0],
        "guide_size": detailer_widgets[0],
        "guide_size_for": detailer_widgets[1],
        "max_size": detailer_widgets[2],
        "seed": ["37", 0],
        "steps": detailer_widgets[5],
        "cfg": detailer_widgets[6],
        "sampler_name": detailer_widgets[7],
        "scheduler": detailer_widgets[8],
        "denoise": detailer_widgets[9],
        "feather": detailer_widgets[10],
        "noise_mask": detailer_widgets[11],
        "force_inpaint": detailer_widgets[12],
        "bbox_threshold": detailer_widgets[13],
        "bbox_dilation": detailer_widgets[14],
        "bbox_crop_factor": detailer_widgets[15],
        "sam_detection_hint": detailer_widgets[16],
        "sam_dilation": detailer_widgets[17],
        "sam_threshold": detailer_widgets[18],
        "sam_bbox_expansion": detailer_widgets[19],
        "sam_mask_hint_threshold": detailer_widgets[20],
        "sam_mask_hint_use_negative": detailer_widgets[21],
        "drop_size": detailer_widgets[22],
        "refiner_ratio": detailer_widgets[23],
        "cycle": detailer_widgets[24],
        "noise_mask_feather": detailer_widgets[26],
        "tiled_encode": ["50", 0],
        "tiled_decode": ["50", 0],
    }
    detailer = {
        "inputs": detailer_inputs,
        "class_type": "FaceDetailerPipe",
        "_meta": {"title": _visual_node(ui, detailer_id).get("title") or f"{name.title()} Detailer"},
    }
    return {
        str(detector_id): detector,
        str(editor_id): editor,
        str(detailer_id): detailer,
    }, [str(detailer_id), 0]


# The v2 export uses the ids allocated by ComfyUI's graphToPrompt exporter
# (for example ``167:56``) instead of the hand-maintained ids from the v1
# Anima template.  Keep the adapter here, next to the renderer, so callers can
# still submit v1 templates while new installs use the attached graph without
# having to rewrite the runner or history code.
V2_UNET_ID = "167:56"
V2_CLIP_ID = "167:57"
V2_VAE_ID = "167:58"
V2_LORA_IDS = ("72", "243")
V2_BASE_GROUPS = {"normal": "691:", "dual_sampler": "701:"}
V2_BASE_ENDPOINTS = {
    "normal": ("691:1413", 1),
    "dual_sampler": ("701:1414", 1),
}
V2_STAGE_GROUPS = {
    "latent": "704:",
    "face_refine": "1253:",
    "seedvr2": "712:",
    "nvidia": "1432:",
}
V2_STAGE_ENDPOINTS = {
    "latent": ("704:1415", 1),
    "seedvr2": ("712:1417", 1),
    "nvidia": ("1432:1447", 1),
    "face_refine": ("1253:1416", 1),
}
# The attachment's ConditionalSaveImage nodes are pass-through nodes rather
# than ComfyUI output nodes.  A rendered API prompt therefore needs one final
# core SaveImage sink; otherwise ComfyUI can report execution_success while
# never scheduling the image branch and the runner has no image metadata to
# persist.  This synthetic id is namespaced so it cannot collide with an
# attachment node or a legacy integer id.
V2_OUTPUT_ID = "v2:output"
V2_REMOVED_CLASSES = {
    "CFGZeroStar",
    "UpscaleModelLoader",
    "ImageUpscaleWithModel",
    "FaceDetailerPipe",
}


def _is_v2_api_template(api: dict[str, Any]) -> bool:
    """Return whether *api* is the graphToPrompt v2 export.

    A colon in a node id is the stable signal; checking the loader as well
    makes this work with small contract fixtures which only retain the shared
    loader nodes.
    """

    return V2_UNET_ID in api or any(":" in str(key) for key in api)


def _v2_node(api: dict[str, Any], node_id: str) -> dict[str, Any] | None:
    value = api.get(str(node_id))
    return value if isinstance(value, dict) else None


def _v2_inputs(api: dict[str, Any], node_id: str) -> dict[str, Any] | None:
    node = _v2_node(api, node_id)
    inputs = node.get("inputs") if node else None
    return inputs if isinstance(inputs, dict) else None


def _v2_set_input(api: dict[str, Any], node_id: str, name: str, value: Any) -> bool:
    inputs = _v2_inputs(api, node_id)
    if inputs is None:
        return False
    inputs[name] = value
    return True


def _mapped_resource_name(
    value: str, resource_paths: dict[str, str] | None = None
) -> str:
    """Apply launcher-resolved relative paths to a profile resource name."""

    if not resource_paths:
        return value
    identity = str(value).replace("\\", "/").casefold()
    for source, target in resource_paths.items():
        if str(source).replace("\\", "/").casefold() == identity:
            return str(target).replace("\\", "/")
    return value


def _v2_replace_refs(
    api: dict[str, Any], old_prefix: str, replacement: list[Any]
) -> None:
    """Replace links into a removed optional subgraph with a bypass link."""

    for node in api.values():
        if not isinstance(node, dict):
            continue
        inputs = node.get("inputs")
        if not isinstance(inputs, dict):
            continue
        for name, value in list(inputs.items()):
            if (
                isinstance(value, list)
                and len(value) == 2
                and isinstance(value[0], str)
                and value[0].startswith(old_prefix)
            ):
                inputs[name] = list(replacement)


def _v2_remove_group(api: dict[str, Any], prefix: str, bypass: list[Any]) -> None:
    """Drop a disabled v2 subgraph and repair all links pointing into it."""

    _v2_replace_refs(api, prefix, bypass)
    for node_id in list(api):
        if str(node_id).startswith(prefix):
            api.pop(node_id, None)


def _v2_repair_missing_refs(api: dict[str, Any], fallback: list[Any]) -> None:
    """Ensure an optional branch cannot leave dangling API links behind."""

    for node in api.values():
        if not isinstance(node, dict):
            continue
        inputs = node.get("inputs")
        if not isinstance(inputs, dict):
            continue
        for name, value in list(inputs.items()):
            if not (
                isinstance(value, list)
                and len(value) == 2
                and isinstance(value[0], str)
                and value[0] not in api
            ):
                continue
            # Scalar links are optional on most switch nodes.  For fixtures
            # with a single surviving branch, routing them to the known image
            # endpoint is more useful than submitting a broken graph.
            if fallback and fallback[0] in api:
                inputs[name] = list(fallback)
            else:
                inputs.pop(name, None)


def _v2_set_ui_mode(ui: dict[str, Any], node_id: int, enabled: bool) -> None:
    """Set a v2 group mode when a visual workflow accompanies the API graph."""

    for node in ui.get("nodes") or []:
        try:
            if int(node.get("id")) == int(node_id):
                node["mode"] = 0 if enabled else 4
                return
        except (TypeError, ValueError):
            continue


def _lora_manager_values(loras: list[dict[str, Any]]) -> list[dict[str, Any]]:
    # Lora Manager consumes active/name/clipStrength, unlike rgthree's
    # on/lora/strengthTwo widgets used by the legacy workflow.
    return [
        {"active": bool(item["enabled"]), "name": item["filename"],
         "strength": item["strength"], "clipStrength": item["strength"]}
        for item in loras
    ]


def _v2_set_ui_loras(ui: dict[str, Any], node_id: str, loras: list[dict[str, Any]]) -> None:
    for node in ui.get("nodes") or []:
        if str(node.get("id")) != str(node_id):
            continue
        values = list(node.get("widgets_values") or [])
        # Lora Manager's first widget is metadata, second is free-form text,
        # and the third stores its structured stack.
        while len(values) < 3:
            values.append({} if len(values) == 0 else "" if len(values) == 1 else [])
        values[2] = copy.deepcopy(loras)
        node["widgets_values"] = values
        if "widgets_values_named" in node:
            node["widgets_values_named"]["loras"] = copy.deepcopy(loras)
        return


def _v2_prompt(settings: dict[str, Any], resolved_prompt: str, resolved_prompt_full: str) -> str:
    """Build a deterministic prompt for the graph's plain CLIPTextEncode."""

    if resolved_prompt_full.strip():
        return resolved_prompt_full
    if resolved_prompt.strip():
        return resolved_prompt
    if any(settings.get(f"random_{section}") for section in PROMPT_SECTIONS):
        raise WorkflowError("随机提示词尚未解析，无法提交生成")
    parts: list[str] = []
    for key in ("quality_prompt", "manual_artist"):
        value = str(settings.get(key) or "").strip()
        if value:
            parts.append(value)
    for section in PROMPT_SECTIONS:
        if not settings.get(f"random_{section}"):
            value = str(settings.get(f"fixed_{section}") or "").strip()
            if value:
                parts.append(value)
    extra = str(settings.get("extra_prompt") or "").strip()
    if extra:
        parts.append(extra)
    return ", ".join(parts)


def _v2_set_sampler(api: dict[str, Any], node_id: str, settings: dict[str, Any], seed: int) -> None:
    inputs = _v2_inputs(api, node_id)
    if inputs is None:
        return
    # The two base samplers and latent sampler use the same public sampling
    # controls.  Only overwrite fields that exist in a particular export so a
    # reduced contract fixture remains valid.
    for name, value in (
        ("seed", seed),
        ("noise_seed", seed),
        ("steps", settings["steps"]),
        ("cfg", settings["cfg"]),
        ("sampler_name", settings["sampler_name"]),
        ("scheduler", settings["scheduler"]),
    ):
        if name in inputs:
            inputs[name] = value
    if "denoise" in inputs:
        inputs["denoise"] = settings["base_denoise"]


def _render_v2_workflows(
    api_template: dict[str, Any],
    ui_template: dict[str, Any],
    settings: dict[str, Any],
    sample_seed: int,
    prompt_seed: int,
    filename_prefix: str,
    resolved_prompt: str,
    resolved_prompt_full: str,
    frozen_positive_prompt: str,
    frozen_negative_prompt: str,
    resource_paths: dict[str, str] | None = None,
) -> tuple[dict[str, Any], dict[str, Any]]:
    """Render the attached graphToPrompt workflow.

    The attachment contains several optional subgraphs.  Their flattened ids
    are intentionally treated as an API contract: changing a profile only
    changes loader/widget inputs, while disabling a stage removes its complete
    subgraph and rewires selectors to the previous stage endpoint.
    """

    api = copy.deepcopy(api_template)
    ui = copy.deepcopy(ui_template)
    profile = get_model_profile(settings["model_profile"])

    # Profile-bound resources are authoritative.  This prevents a stale Anima
    # CLIP/VAE surviving a Krea switch in browser drafts or history replay.
    _v2_set_input(
        api,
        V2_UNET_ID,
        "unet_name",
        _mapped_resource_name(settings["model_name"], resource_paths),
    )
    _v2_set_input(
        api,
        V2_CLIP_ID,
        "clip_name",
        _mapped_resource_name(profile["clip_name"], resource_paths),
    )
    _v2_set_input(api, V2_CLIP_ID, "type", profile["clip_type"])
    _v2_set_input(
        api,
        V2_VAE_ID,
        "vae_name",
        _mapped_resource_name(profile["vae_name"], resource_paths),
    )
    # SeedVR2 resources are selectable from their live node inventories;
    # migration/validation use the current profile's canonical files only as
    # defaults. Main-model ownership is checked before this renderer runs.
    seedvr2 = settings["upscalers"]["seedvr2"]
    _v2_set_input(
        api,
        "712:716",
        "unet_name",
        _mapped_resource_name(seedvr2["model_name"], resource_paths),
    )
    _v2_set_input(
        api,
        "712:715",
        "vae_name",
        _mapped_resource_name(seedvr2["vae_name"], resource_paths),
    )
    _v2_set_input(api, "712:716", "weight_dtype", seedvr2["weight_dtype"])
    _v2_set_input(api, "712:721", "color_correction_method", seedvr2["color_correction_method"])

    # Prompt and negative prompt.  Keep the graph's Lora trigger concatenation
    # nodes intact for visual editing, but put the resolved strings directly on
    # the encode nodes so API submissions are deterministic even when a custom
    # prompt plugin is unavailable.
    positive_text = frozen_positive_prompt or _v2_prompt(settings, resolved_prompt, resolved_prompt_full)
    negative_text = frozen_negative_prompt or settings["negative_prompt"]
    _v2_set_input(api, "168", "value", positive_text)
    _v2_set_input(api, "250", "value", negative_text)
    for node_id in ("1010:995", "1026:1011"):
        inputs = _v2_inputs(api, node_id)
        if inputs is not None and "text" in inputs:
            inputs["text"] = positive_text
    for node_id in ("1010:1004", "1026:1020"):
        inputs = _v2_inputs(api, node_id)
        if inputs is not None and "text" in inputs:
            inputs["text"] = negative_text
    for node_id in ("1010:1083", "1026:1088"):
        inputs = _v2_inputs(api, node_id)
        if inputs is not None:
            if "string_a" in inputs:
                inputs["string_a"] = positive_text
            if "string_b" in inputs:
                inputs["string_b"] = ""
            if "delimiter" in inputs:
                inputs["delimiter"] = ""

    lora_values = _lora_manager_values(settings["loras"])
    trigger_text = ", ".join(settings.get("lora_managed_triggers") or [])
    for node_id in V2_LORA_IDS:
        inputs = _v2_inputs(api, node_id)
        if inputs is None:
            continue
        if "loras" in inputs:
            inputs["loras"] = {"__value__": copy.deepcopy(lora_values)}
        if "text" in inputs:
            inputs["text"] = trigger_text
    _v2_set_ui_loras(ui, "72", lora_values)
    _v2_set_ui_loras(ui, "243", lora_values)

    # Shared dimensions, batch and deterministic seeds.
    resolution = json.dumps(
        {"version": 1, "width": settings["width"], "height": settings["height"]},
        ensure_ascii=False,
        separators=(",", ":"),
    )
    _v2_set_input(api, "76", "resolution_json", resolution)
    _v2_set_input(api, "207", "value", 1)
    # Each mutually exclusive base graph owns a prompt-seed expression node;
    # write both before the inactive graph is pruned so either mode preserves
    # deterministic pool selection.
    _v2_set_input(api, "691:1241", "values.a", prompt_seed)
    _v2_set_input(api, "701:1242", "values.a", prompt_seed)
    for sampler_id in ("691:1240", "701:1245", "701:1246", "704:1248"):
        _v2_set_sampler(api, sampler_id, settings, sample_seed)
    dual = settings.get("dual") if settings.get("workbench") == "random" else None
    if dual is not None and settings["base_mode"] == "dual_sampler":
        for node_id, start in (("701:1245", 0), ("701:1246", dual["switch_step"])):
            _v2_set_input(api, node_id, "start_at_step", start)
            _v2_set_input(api, node_id, "end_at_step", settings["steps"])
        for name, value in (("cfg", dual["second_cfg"]), ("sampler_name", dual["second_sampler_name"]),
                            ("scheduler", dual["second_scheduler"])):
            _v2_set_input(api, "701:1246", name, value)
    # SeedVR2 and face processing have their own seeds but inherit the public
    # batch seed so replay remains reproducible.
    _v2_set_input(api, "712:1208", "seed", sample_seed)
    _v2_set_input(api, "1253:1215", "seed", sample_seed)

    # The ComfyUI API export flattens the attachment's subgraphs but drops
    # several edges that cross a subgraph boundary. Restore those links from
    # the attached graph contract so the selected base path and repair stages
    # remain valid executable graphs. The normal base's model/conditioning
    # routes are the same profile-aware LoRA and CLIP routes used by the dual
    # sampler group. Its VAE boundary was also omitted by graphToPrompt.
    _v2_set_input(api, "691:1240", "model", ["1010:1005", 0])
    _v2_set_input(api, "691:1240", "negative", ["1010:1002", 0])
    _v2_set_input(api, "691:158", "conditioning", ["1010:995", 0])
    _v2_set_input(api, "691:213", "on_false", ["1010:995", 0])
    _v2_set_input(api, "691:239", "vae", [V2_VAE_ID, 0])

    # The latent-upscale subgraph has the same missing boundary connections:
    # image -> VAE encode -> latent scale -> sampler -> VAE decode. Bind the
    # profile-selected LoRA/conditioning route and shared VAE explicitly.
    _v2_set_input(api, "704:1182", "vae", [V2_VAE_ID, 0])
    _v2_set_input(api, "704:1248", "model", ["1026:1021", 0])
    _v2_set_input(api, "704:1248", "positive", ["1026:1011", 0])
    _v2_set_input(api, "704:1248", "negative", ["1026:1018", 0])
    _v2_set_input(api, "704:359", "vae", [V2_VAE_ID, 0])

    latent = settings["upscalers"]["latent"]
    latent_sampler = latent["second_sampler"]
    if latent_sampler["enabled"]:
        _v2_set_input(api, "704:1248", "seed", sample_seed)
        for name in ("steps", "cfg", "sampler_name", "scheduler"):
            _v2_set_input(api, "704:1248", name, latent_sampler[name])
        _v2_set_input(api, "704:1248", "denoise", latent_sampler["denoise"])
    else:
        # Latent resizing remains useful on its own. Remove the sampler node
        # and connect the scaled latent directly to the VAE decoder.
        _v2_replace_refs(api, "704:1248", ["704:1064", 0])
        api.pop("704:1248", None)

    seed_sampler = seedvr2["sampler"]
    for name in ("steps", "cfg", "sampler_name", "scheduler", "denoise"):
        _v2_set_input(api, "712:1208", name, seed_sampler[name])
    _v2_set_input(api, "712:907", "value", seedvr2["scale_by"])
    for group_name, node_id in (("encode", "712:1177"), ("decode", "712:718")):
        for name, value in seedvr2[group_name].items():
            _v2_set_input(api, node_id, name, value)

    # Face refinement is another flattened subgraph whose model, VAE,
    # conditioning, and CLIP boundary edges are absent from the API export.
    # Keep it on the same profile-bound resources as the selected base path.
    _v2_set_input(api, "1253:1215", "model", ["1026:1021", 0])
    _v2_set_input(api, "1253:1215", "vae", [V2_VAE_ID, 0])
    _v2_set_input(api, "1253:1215", "positive", ["1026:1011", 0])
    _v2_set_input(api, "1253:1215", "negative", ["1026:1018", 0])
    _v2_set_input(api, "1253:1215", "clip", ["1026:1025", 0])
    face = settings["upscalers"]["face_refine"]
    for name, value in face.items():
        if name != "enabled":
            _v2_set_input(api, "1253:1215", name, value)

    variance = settings["krea_variance"]
    for switch_id, enhancer_id in (("701:538", "701:537"), ("691:213", "691:158")):
        _v2_set_input(api, switch_id, "switch", variance["enabled"])
        for name, value in variance.items():
            if name != "enabled":
                _v2_set_input(api, enhancer_id, name, value)
    # The attached workflow includes an interactive image-picker branch for
    # manual reference selection.  Random Studio submissions must remain
    # unattended: route both base modes through their generated-image branch
    # and leave the picker as an opt-in visual-workflow feature.
    _v2_set_input(api, "691:688", "switch", False)
    _v2_set_input(api, "701:697", "switch", False)

    for node in api.values():
        if isinstance(node, dict) and node.get("class_type") in V2_REMOVED_CLASSES:
            # This protects callers that mixed a v1 node into the v2 export.
            node["_v2_removed"] = True
    for node_id in [node_id for node_id, node in api.items() if isinstance(node, dict) and node.get("_v2_removed")]:
        api.pop(node_id, None)

    base_mode = settings["base_mode"]
    base_endpoint = list(V2_BASE_ENDPOINTS[base_mode])
    inactive_base = V2_BASE_GROUPS["dual_sampler" if base_mode == "normal" else "normal"]
    _v2_remove_group(api, inactive_base, base_endpoint)
    active_base = V2_BASE_GROUPS[base_mode]
    raw_base_image = {
        "normal": ["691:239", 0],
        "dual_sampler": ["701:539", 0],
    }[base_mode]

    # The visual workflow exposes an optional image picker and desktop
    # notification after the base sampler.  They are useful when a person is
    # driving ComfyUI directly, but the Random Studio API path is unattended.
    # Bypass those nodes in both switch arms before removing them; ComfyUI
    # validates lazy switch inputs even when the other arm is selected.
    interactive_ids = {
        node_id
        for node_id, node in api.items()
        if node_id.startswith(active_base)
        and isinstance(node, dict)
        and node.get("class_type") in {"SimpleNotify", "AaaliceImagePicker"}
    }
    for node_id, node in api.items():
        if not node_id.startswith(active_base) or not isinstance(node, dict):
            continue
        inputs = node.get("inputs")
        if not isinstance(inputs, dict):
            continue
        for name in ("on_false", "on_true"):
            value = inputs.get(name)
            if (
                isinstance(value, list)
                and len(value) == 2
                and value[0] in interactive_ids
            ):
                inputs[name] = list(raw_base_image)
    for node_id in interactive_ids:
        _v2_replace_refs(api, node_id, raw_base_image)
        api.pop(node_id, None)

    # The normal base subgraph carries an unused image-to-latent branch for
    # manual img2img.  Its VAEEncode widget has no image in an unattended
    # text-to-image submission, and ComfyUI validates both lazy switch arms.
    # Route that arm to the already-rendered empty latent and keep the visual
    # UI template untouched for direct interactive use.
    for node_id, node in list(api.items()):
        if (
            node_id.startswith(active_base)
            and isinstance(node, dict)
            and node.get("class_type") == "VAEEncode"
        ):
            _v2_replace_refs(api, node_id, ["691:53", 0])
            api.pop(node_id, None)

    _v2_set_ui_mode(ui, 691, base_mode == "normal")
    _v2_set_ui_mode(ui, 701, base_mode == "dual_sampler")

    # The selectors in the export are AnySwitch nodes.  Set every candidate
    # to the current endpoint before enabling a stage; this makes the chain
    # deterministic and also bypasses stale links left by a disabled group.
    def set_selector(node_id: str, endpoint: list[Any]) -> None:
        inputs = _v2_inputs(api, node_id)
        if inputs is None:
            return
        for name in list(inputs):
            if name.startswith("any_") or name in {"on_false", "on_true"}:
                inputs[name] = list(endpoint)

    set_selector("702:353", base_endpoint)
    current = base_endpoint
    stage_enabled = {key: bool(settings["upscalers"][key]["enabled"]) for key in V2_STAGE_GROUPS}
    # Configure each optional group in the requested order.  A group's input
    # selector is pointed at the previous endpoint; disabling it removes the
    # flattened nodes and all references to them.
    stage_selector = {
        "latent": "702:353",
        "seedvr2": "710:388",
        "nvidia": "1425:1419",
        "face_refine": "1214:1209",
    }
    for stage, prefix in V2_STAGE_GROUPS.items():
        selector_id = stage_selector[stage]
        set_selector(selector_id, current)
        if not stage_enabled[stage]:
            _v2_remove_group(api, prefix, current)
            _v2_set_ui_mode(ui, {"latent": 704, "seedvr2": 712, "nvidia": 1432, "face_refine": 1253}[stage], False)
            continue
        _v2_set_ui_mode(ui, {"latent": 704, "seedvr2": 712, "nvidia": 1432, "face_refine": 1253}[stage], True)
        current = list(V2_STAGE_ENDPOINTS[stage])

    # Route the visual preview to the final surviving image and discard its
    # optional example-image fallback.  The frontend workflow retains the
    # original picker graph; the submitted API graph stays deterministic.
    for node_id, node in list(api.items()):
        if isinstance(node, dict) and node.get("class_type") == "PreviewImage":
            api.pop(node_id, None)
    _v2_remove_group(api, "863:", current)

    # Desktop notifications and interactive save helpers belong to the visual
    # workflow, not the unattended API graph. The notification plugin marks
    # these helpers as output nodes, and the save plugin's widget signature
    # changes across releases. Bypass both node types to their image input and
    # let the core SaveImage sink below own persistence.
    for node_id, node in list(api.items()):
        if not isinstance(node, dict):
            continue
        inputs = node.get("inputs") or {}
        if node.get("class_type") == "SimpleNotify":
            endpoint = inputs.get("value")
        elif node.get("class_type") == "ConditionalSaveImage":
            endpoint = inputs.get("images")
        else:
            continue
        if isinstance(endpoint, list) and len(endpoint) == 2:
            _v2_replace_refs(api, node_id, list(endpoint))
        api.pop(node_id, None)

    # Stage-specific model/settings controls not represented by a common
    # sampler node.
    latent = settings["upscalers"]["latent"]
    # The latent upscale node lives inside the 704: group.  Earlier drafts
    # targeted the group id (``701``), which is not a node in the API export,
    # so the requested scale was silently ignored by ComfyUI.  Write the
    # actual resize node and keep reduced fixtures tolerant when it is absent.
    # Explicit random dual settings own their resize; null keeps old coupling.
    for node_id, source, resize, scale in (
        ("704:1064", ["704:1182", 0], latent, latent["scale_by"]),
        ("701:1450", ["701:1245", 0], dual or latent, dual["scale"] if dual else latent["scale_by"]),
    ):
        if node_id in api:
            # Keep existing metadata and reduced fixtures while replacing the
            # algorithm-specific inputs; Sesqui takes latent/scale, not samples/scale_by.
            api[node_id] = {**api[node_id], **latent_upscale_node(source, resize, float(scale))}
    nvidia = settings["upscalers"]["nvidia"]
    _v2_set_input(api, "1432:1418", "resize_type", nvidia["resize_type"])
    _v2_set_input(api, "1432:1418", "resize_type.scale", nvidia["scale"])
    _v2_set_input(api, "1432:1418", "quality", nvidia["quality"])
    if "1432:1492" in api:
        api["1432:1492"] = {"class_type": "DLSS5NeuralRender",
                            "inputs": {"images": ["1432:1418", 0], **copy.deepcopy(nvidia["dlss"])},
                            "_meta": {"title": "N卡 DLSS 色彩处理"}}

    # Every surviving save node receives the same prefix.  Intermediate saves
    # are part of the attached graph and are harmless when a stage is absent.
    for node in api.values():
        if not isinstance(node, dict):
            continue
        inputs = node.get("inputs")
        if isinstance(inputs, dict) and "filename_prefix" in inputs:
            inputs["filename_prefix"] = filename_prefix
    _v2_repair_missing_refs(api, current)
    # v2 submissions stay on the attached namespaced graph.  Do not append
    # the old integer-id readback nodes here: they reintroduce the removed
    # CFGZeroStar/second-sampler/detailer surface to otherwise clean v2
    # payloads.  Legacy settings are migrated at the request/history boundary
    # and are rendered through the v1 template only when that template is
    # explicitly selected.
    # ``ConditionalSaveImage`` in the attachment is intentionally a
    # pass-through helper (``output_node: false`` in /object_info).  Connect a
    # single standard SaveImage sink to the last surviving image endpoint so
    # every unattended Random Studio submission produces an image that the
    # runner can discover through ComfyUI history.  The branch-local save
    # nodes remain in the graph for visual/manual use; this sink is the only
    # node whose filename prefix is authoritative for the submitted batch.
    api[V2_OUTPUT_ID] = {
        "inputs": {
            "filename_prefix": filename_prefix,
            "images": list(current),
        },
        "class_type": "SaveImage",
        "_meta": {"title": "Comfy Image Atelier 输出"},
    }
    return api, ui


def render_workflows(
    api_template: dict[str, Any],
    ui_template: dict[str, Any],
    settings: dict[str, Any],
    sample_seed: int,
    prompt_seed: int,
    filename_prefix: str,
    resolved_prompt: str = "",
    resolved_selection: dict[str, Any] | None = None,
    resolved_prompt_full: str = "",
    frozen_positive_prompt: str = "",
    frozen_negative_prompt: str = "",
    resource_paths: dict[str, str] | None = None,
    *,
    single_runtime: dict[str, Any] | None = None,
) -> tuple[dict[str, Any], dict[str, Any]]:
    raw_settings = copy.deepcopy(settings)
    settings = validate_settings(settings)
    if not 0 <= sample_seed <= MAX_SAMPLE_SEED:
        raise WorkflowError("sample_seed 超出范围")
    if not 0 <= prompt_seed <= 2**31 - 1:
        raise WorkflowError("prompt_seed 超出范围")

    # graphToPrompt exports from the v2 attachment use namespaced ids and a
    # different node contract.  Keep the mature v1 renderer below intact for
    # old embedded workflows and route only the new export through its adapter.
    if _is_v2_api_template(api_template):
        if settings["workbench"] == "single":
            from .single_workbench import render_single_workflows
            return render_single_workflows(
                api_template, ui_template, settings, sample_seed, prompt_seed, filename_prefix,
                single_runtime=single_runtime, frozen_positive_prompt=frozen_positive_prompt,
                frozen_negative_prompt=frozen_negative_prompt, resource_paths=resource_paths,
            )
        return _render_v2_workflows(
            api_template,
            ui_template,
            settings,
            sample_seed,
            prompt_seed,
            filename_prefix,
            resolved_prompt,
            resolved_prompt_full,
            frozen_positive_prompt,
            frozen_negative_prompt,
            resource_paths,
        )

    # Embedded v1 workflows still need their compatibility-only controls while
    # they are being replayed.  Keep those aliases local to this legacy render
    # call; v2 settings/history remain free of the removed controls.
    settings["hires"] = _validate_hires(raw_settings.get("hires", DEFAULT_HIRES))
    settings["detailers"] = _validate_detailers(
        raw_settings.get("detailers", DEFAULT_DETAILERS)
    )
    settings["cfg_zero_star"] = _boolean(
        "cfg_zero_star", raw_settings.get("cfg_zero_star", False)
    )
    settings["second_sampler"] = _validate_second_sampler(
        raw_settings.get("second_sampler", DEFAULT_SECOND_SAMPLER)
    )
    # Legacy embedded workflows may send a full v1 settings snapshot with the
    # newly added default ``model_profile=anima`` field.  Their custom Anima
    # UNet remains valid for the v1 renderer; explicit v2 profile requests are
    # handled above and stay authoritative in ``validate_settings``.
    if (
        "model_profile" in raw_settings
        and str(raw_settings.get("model_profile")) == DEFAULT_MODEL_PROFILE
        and isinstance(raw_settings.get("model_name"), str)
        and raw_settings["model_name"] != DEFAULT_SETTINGS.get("model_name")
    ):
        settings["model_name"] = raw_settings["model_name"]

    api = copy.deepcopy(api_template)
    ui = copy.deepcopy(ui_template)
    api["1"]["inputs"]["unet_name"] = settings["model_name"]
    cfg_zero_star = settings["cfg_zero_star"]
    model_ref: list[Any] = [str(CFG_ZERO_STAR_ID), 0] if cfg_zero_star else ["2", 0]
    if cfg_zero_star:
        api[str(CFG_ZERO_STAR_ID)] = {
            "inputs": {"model": ["2", 0]},
            "class_type": "CFGZeroStar",
            "_meta": {"title": "CFGZeroStar"},
        }
    else:
        api.pop(str(CFG_ZERO_STAR_ID), None)
    api["5"]["inputs"]["model"] = model_ref
    if "26" in api:
        api["26"]["inputs"]["model"] = model_ref

    second_sampler = settings["second_sampler"]
    if second_sampler["enabled"]:
        api[str(SECOND_SAMPLER_ID)] = {
            "inputs": {
                "seed": ["37", 0],
                "steps": second_sampler["steps"],
                "cfg": second_sampler["cfg"],
                "sampler_name": second_sampler["sampler_name"],
                "scheduler": second_sampler["scheduler"],
                "denoise": second_sampler["denoise"],
                "model": model_ref,
                "positive": [str(POSITIVE_ID), 0],
                "negative": [str(NEGATIVE_ID), 0],
                "latent_image": ["5", 0],
            },
            "class_type": "KSampler",
            "_meta": {"title": "2ndSampler"},
        }
        api["48"]["inputs"]["samples"] = [str(SECOND_SAMPLER_ID), 0]
    else:
        api.pop(str(SECOND_SAMPLER_ID), None)
        api["48"]["inputs"]["samples"] = ["5", 0]

    # The SAM loader is only needed when at least one detailer is active.
    sam_enabled = any(settings["detailers"].values())
    if sam_enabled:
        if str(SAM_LOADER_ID) not in api:
            raise WorkflowError("工作流缺少 SAMLoader 节点 7")
        detailer_pipe = api.get(str(DETAILER_PIPE_ID))
        if not isinstance(detailer_pipe, dict) or not isinstance(detailer_pipe.get("inputs"), dict):
            raise WorkflowError("工作流缺少 ToDetailerPipe 节点 18")
        detailer_pipe["inputs"]["sam_model_opt"] = [str(SAM_LOADER_ID), 0]
    else:
        api.pop(str(SAM_LOADER_ID), None)
        detailer_pipe = api.get(str(DETAILER_PIPE_ID))
        if isinstance(detailer_pipe, dict) and isinstance(detailer_pipe.get("inputs"), dict):
            detailer_pipe["inputs"].pop("sam_model_opt", None)

    _set_ui_mode(ui, CFG_ZERO_STAR_ID, cfg_zero_star)
    _set_ui_mode(ui, SECOND_SAMPLER_ID, second_sampler["enabled"])
    _set_ui_mode(ui, SAM_LOADER_ID, sam_enabled)
    if not sam_enabled:
        _disconnect_ui_input(ui, DETAILER_PIPE_ID, "sam_model_opt")
    if second_sampler["enabled"]:
        _disconnect_ui_input(ui, SECOND_SAMPLER_ID, "cfg")
    _set_ui_widget(ui, 1, settings["model_name"], 0)
    lora_node = api.get("2")
    if not isinstance(lora_node, dict) or not isinstance(lora_node.get("inputs"), dict):
        raise WorkflowError("工作流缺少 LoRA 节点 2")
    lora_inputs = lora_node["inputs"]
    for key in list(lora_inputs):
        if key.startswith("lora_"):
            del lora_inputs[key]
    for index, item in enumerate(settings["loras"], 1):
        lora_inputs[f"lora_{index}"] = {
            "on": item["enabled"],
            "lora": item["filename"],
            "strength": item["strength"],
        }
    lora_ui = _visual_node(ui, 2)
    lora_ui["widgets_values"] = [
        {},
        {"type": "PowerLoraLoaderHeaderWidget"},
        *[
            {
                "on": item["enabled"],
                "lora": item["filename"],
                "strength": item["strength"],
                "strengthTwo": None,
            }
            for item in settings["loras"]
        ],
        {},
        "",
    ]
    frozen_positive_prompt = str(frozen_positive_prompt or "")
    frozen_negative_prompt = str(frozen_negative_prompt or "")
    positive_text = frozen_positive_prompt or _legacy_positive_prompt(
        settings, resolved_prompt, resolved_prompt_full
    )
    _set_native_positive(api, ui, positive_text)
    api[str(NEGATIVE_ID)]["inputs"]["text"] = frozen_negative_prompt or settings["negative_prompt"]
    api["23"]["inputs"]["value"] = settings["width"]
    api["31"]["inputs"]["value"] = settings["height"]
    api["35"]["inputs"]["value"] = 1
    api["37"]["inputs"]["seed"] = sample_seed
    api["39"]["inputs"]["value"] = settings["steps"]
    api["41"]["inputs"]["value"] = settings["cfg"]
    api["5"]["inputs"]["sampler_name"] = settings["sampler_name"]
    api["5"]["inputs"]["scheduler"] = settings["scheduler"]
    api["12"]["inputs"]["filename_prefix"] = filename_prefix

    current_image: list[Any] = ["48", 0]
    hires = settings["hires"]
    if hires["enabled"]:
        api[str(HIRES_MODEL_ID)]["inputs"]["model_name"] = hires["model_name"]
        api[str(HIRES_UPSCALE_ID)]["inputs"]["image"] = current_image
        api[str(HIRES_SCALE_ID)]["inputs"]["scale_by"] = hires["percent"] / 100
        current_image = [str(HIRES_SCALE_ID), 0]
    else:
        for node_id in (HIRES_MODEL_ID, HIRES_UPSCALE_ID, HIRES_SCALE_ID):
            api.pop(str(node_id), None)
    for node_id in (HIRES_MODEL_ID, HIRES_UPSCALE_ID, HIRES_SCALE_ID):
        _set_ui_mode(ui, node_id, hires["enabled"])
    _set_ui_widget(ui, HIRES_MODEL_ID, hires["model_name"], 0)
    _set_ui_widget(ui, HIRES_SCALE_ID, hires["percent"] / 100, 1)

    for name in DETAILER_ORDER:
        config = DETAILER_NODES[name]
        enabled = settings["detailers"][name]
        for node_id in (config["detector"], config["editor"], config["detailer"]):
            _set_ui_mode(ui, node_id, enabled)
        if enabled:
            nodes, current_image = _detailer_nodes(ui, name, current_image)
            api.update(nodes)
        else:
            for node_id in (config["detector"], config["editor"], config["detailer"]):
                api.pop(str(node_id), None)
    api["12"]["inputs"]["images"] = current_image
    _set_ui_mode(ui, 52, False)

    _set_ui_widget(ui, NEGATIVE_ID, api[str(NEGATIVE_ID)]["inputs"]["text"], 0)
    for node_id, value in ((23, settings["width"]), (31, settings["height"]), (35, 1), (39, settings["steps"]), (41, settings["cfg"]), (12, filename_prefix)):
        _set_ui_widget(ui, node_id, value, 0)
    _set_ui_widget(ui, 37, sample_seed, 0)
    _set_ui_widget(ui, 5, settings["sampler_name"], 4)
    _set_ui_widget(ui, 5, settings["scheduler"], 5)
    _set_ui_widget(ui, SECOND_SAMPLER_ID, sample_seed, 0)
    _set_ui_widget(ui, SECOND_SAMPLER_ID, second_sampler["steps"], 2)
    _set_ui_widget(ui, SECOND_SAMPLER_ID, second_sampler["cfg"], 3)
    _set_ui_widget(ui, SECOND_SAMPLER_ID, second_sampler["sampler_name"], 4)
    _set_ui_widget(ui, SECOND_SAMPLER_ID, second_sampler["scheduler"], 5)
    _set_ui_widget(ui, SECOND_SAMPLER_ID, second_sampler["denoise"], 6)
    return api, ui


def build_submission(
    api_template: dict[str, Any],
    ui_template: dict[str, Any],
    settings: dict[str, Any],
    sample_seed: int,
    prompt_seed: int,
    filename_prefix: str,
    client_id: str,
    sequence: int,
    resolved_prompt: str = "",
    resolved_selection: dict[str, Any] | None = None,
    resolved_prompt_full: str = "",
    frozen_positive_prompt: str = "",
    frozen_negative_prompt: str = "",
    resource_paths: dict[str, str] | None = None,
    *,
    single_runtime: dict[str, Any] | None = None,
) -> dict[str, Any]:
    api, ui = render_workflows(
        api_template,
        ui_template,
        settings,
        sample_seed,
        prompt_seed,
        filename_prefix,
        resolved_prompt,
        resolved_selection,
        resolved_prompt_full,
        frozen_positive_prompt,
        frozen_negative_prompt,
        resource_paths,
        single_runtime=single_runtime,
    )
    metadata_settings = validate_settings(settings)
    if _is_v2_api_template(api_template):
        for legacy_key in _LEGACY_SETTING_KEYS:
            metadata_settings.pop(legacy_key, None)
    final_positive = (
        frozen_positive_prompt or _v2_prompt(metadata_settings, resolved_prompt, resolved_prompt_full)
        if _is_v2_api_template(api_template)
        else api[str(POSITIVE_ID)]["inputs"]["text"]
    )
    metadata = {
        "settings": metadata_settings,
        "sample_seed": sample_seed,
        "prompt_seed": prompt_seed,
        "sequence": sequence,
        "resolved_selection": resolved_selection or {},
        "resolved_prompt": resolved_prompt,
        "resolved_prompt_full": final_positive,
        "frozen_positive_prompt": frozen_positive_prompt,
    }
    if metadata_settings["workbench"] == "single":
        single_meta = (ui.get("extra") or {}).get("single_workbench") or {}
        metadata["resolved_prompt_full"] = single_meta.get("positive", "")
        metadata["single_runtime"] = {
            "positive": single_meta.get("positive", ""),
            "negative": single_meta.get("negative", ""),
            "effective_positive": copy.deepcopy(single_meta.get("effective_positive", {})),
            **({key: single_runtime[key] for key in ("input_image", "input_image_id", "prompt_job_id") if key in single_runtime} if single_runtime else {}),
        }
    return {
        "prompt": api,
        "client_id": client_id,
        "extra_data": {
            "extra_pnginfo": {"workflow": ui, "anima_random_webui": metadata},
            **({"preview_method": "latent2rgb"} if metadata_settings["workbench"] == "single" else {}),
        },
    }


class WorkflowTemplates:
    def __init__(self, api_template: dict[str, Any], ui_template: dict[str, Any]):
        validate_template_nodes(api_template, ui_template)
        self.api = api_template
        self.ui = ui_template
        # Launcher mappings are attached after construction by
        # ``configure_resources`` so dynamic v2 profile switches can resolve
        # every loader (including SeedVR2) to its actual ComfyUI-relative path.
        self.resource_paths: dict[str, str] = {}
        # Expose the contract version to queue/history owners without making
        # them inspect private renderer details.  The v2 graph uses namespaced
        # graphToPrompt ids; v1 keeps the hand-numbered template contract.
        self.is_v2 = _is_v2_api_template(api_template)

    @classmethod
    def load(cls, template_dir: str | Path) -> "WorkflowTemplates":
        directory = Path(template_dir)
        v2_api = directory / "workflow_api_v2.json"
        v2_ui = directory / "workflow_ui_v2.json"
        if v2_api.is_file() and v2_ui.is_file():
            return cls(read_json(v2_api), read_json(v2_ui))
        return cls(read_json(directory / "workflow_api.json"), read_json(directory / "workflow_ui.json"))

    def submission(
        self,
        settings: dict[str, Any],
        sample_seed: int,
        prompt_seed: int,
        filename_prefix: str,
        client_id: str,
        sequence: int,
        resolved_prompt: str = "",
        resolved_selection: dict[str, Any] | None = None,
        resolved_prompt_full: str = "",
        frozen_positive_prompt: str = "",
        frozen_negative_prompt: str = "",
        *,
        single_runtime: dict[str, Any] | None = None,
    ) -> dict[str, Any]:
        return build_submission(
            self.api,
            self.ui,
            settings,
            sample_seed,
            prompt_seed,
            filename_prefix,
            client_id,
            sequence,
            resolved_prompt,
            resolved_selection,
            resolved_prompt_full,
            frozen_positive_prompt,
            frozen_negative_prompt,
            self.resource_paths,
            single_runtime=single_runtime,
        )
