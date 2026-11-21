"""Anima-first metadata extraction, independent of reference-image storage."""
from __future__ import annotations

import json
from typing import Any

from PIL import Image

from ._vendor.sd_prompt_reader.comfyui import parse_comfyui
from ._vendor.sd_prompt_reader.formats import (
    MAX_GRAPH_NODES, MAX_METADATA_BYTES, candidate, object_json, parameters, parse_image, result,
    safe_parameters, text,
)
from .single_workbench import active_lora_pipelines

WARDROBE_KEYS = ("anima_v4_prompt", "anima_v3_prompt")


def empty_metadata(*, warning: str = "") -> dict[str, Any]:
    return {"source": "none", "format": "", "text": "", "negative": "", "parameters": {},
            "candidates": [], "warnings": [warning] if warning else []}


def _native(info: dict[str, Any]) -> dict[str, Any] | None:
    if "anima_random_webui" in info:
        native = object_json(info["anima_random_webui"])
        runtime = native.get("single_runtime")
        runtime = runtime if isinstance(runtime, dict) else {}
        settings = native.get("settings")
        settings = settings if isinstance(settings, dict) else {}
        positive = native.get("resolved_prompt_full") or runtime.get("positive") or native.get("frozen_positive_prompt")
        negative = runtime.get("negative", native.get("frozen_negative_prompt", settings.get("negative_prompt", "")))
        values = parameters({**settings, **{k: native[k] for k in ("sample_seed", "prompt_seed", "sequence") if k in native}},
                            {"seed": ("sample_seed", "seed"), "model": ("unet_name", "model", "checkpoint")},
                            exclude=("negative_prompt", "positive_prompt", "extra_prompt", "quality_prompt"))
        if runtime.get("effective_positive"):
            values["pipeline_prompts"] = safe_parameters(runtime["effective_positive"])
        parsed = result("Anima Random Studio", positive, negative, values)
        if parsed["candidates"]:
            parsed["candidates"][0]["id"] = "anima:base"
            parsed["candidates"][0]["label"] = "Anima 基础提示词"
            effective = runtime.get("effective_positive")
            # The saved settings are authoritative about which LoRA stacks
            # participated. Dormant secondary prompts must never be offered as
            # an actual image prompt. Old incomplete records keep their base.
            try:
                active = active_lora_pipelines(settings) if settings.get("workbench") == "single" else frozenset()
            except (KeyError, TypeError, AttributeError):
                active = frozenset()
            seen = {parsed["candidates"][0]["text"]}
            if isinstance(effective, dict):
                for pipeline in ("primary", "secondary"):
                    if pipeline not in active:
                        continue
                    item = candidate(f"anima:{pipeline}", f"Anima {'主' if pipeline == 'primary' else '副'}管线实际提示词",
                                     effective.get(pipeline), negative, {**values, "pipeline": pipeline})
                    if item and item["text"] not in seen:
                        seen.add(item["text"])
                        parsed["candidates"].append(item)
            return parsed
    for key, field in (("anima_prompt", "positive"), ("anima_prompt_composer", "resolved_prompt")):
        if key not in info:
            continue
        entries = object_json(info[key])
        candidates = []
        for identity, entry in entries.items():
            if not isinstance(entry, dict):
                continue
            prompt = entry.get(field)
            # Old custom nodes sometimes wrote a stringified graph connection.
            if isinstance(prompt, str) and prompt.strip().startswith(("['", '["')):
                continue
            item = candidate(f"anima:{identity}", f"Anima 节点 {identity}", prompt,
                             entry.get("negative", ""), parameters(entry, exclude=(field, "negative")))
            if item:
                candidates.append(item)
        if candidates:
            return {"format": "Anima Tools", "candidates": candidates, "parameters": {}, "warnings": []}
    return None


def _wardrobe_candidate(raw: Any, identity: str, image_size: str) -> dict[str, Any] | None:
    try:
        record = object_json(raw)
        full = text(record.get("positive_full"))
        positive = full or record.get("positive")
        label = "Wardrobe · 重建正向" if full else "Wardrobe · 生成正文"
        values = {"actual_image_size": image_size}
        if isinstance(record.get("version"), str) and record["version"].strip():
            values["wardrobe_version"] = record["version"].strip()[:100]
        return candidate(identity, label, positive, record.get("negative"), values)
    except (ValueError, TypeError, RecursionError):
        return None


def _wardrobe_graph(info: dict[str, Any], *, dedicated_record: bool) -> dict[str, Any]:
    raw = info.get("prompt")
    if raw is None or (not dedicated_record and isinstance(raw, str) and not any(key in raw for key in WARDROBE_KEYS)):
        return {}
    try:
        graph = object_json(raw)
        return graph if len(graph) <= MAX_GRAPH_NODES else {}
    except (ValueError, TypeError, RecursionError):
        return {}


def _wardrobe(image: Image.Image) -> dict[str, Any] | None:
    image_size = f"{image.width}x{image.height}"
    candidates = []
    for key in WARDROBE_KEYS:
        if key in image.info:
            item = _wardrobe_candidate(image.info[key], "wardrobe:0", image_size)
            if item:
                candidates.append(item)
                break

    graph = _wardrobe_graph(image.info, dedicated_record=bool(candidates))
    if not candidates:
        # Wardrobe also stores its report inside multiple API graph nodes. Copies
        # commonly share the same payload even across different encoders.
        seen = set()
        for node in graph.values():
            inputs = node.get("inputs") if isinstance(node, dict) else None
            if not isinstance(inputs, dict):
                continue
            for key in WARDROBE_KEYS:
                if key not in inputs:
                    continue
                item = _wardrobe_candidate(inputs[key], f"wardrobe:{len(candidates)}", image_size)
                if item:
                    signature = (item["text"], item["negative"])
                    if signature not in seen:
                        seen.add(signature)
                        candidates.append(item)
                    break
    if not candidates:
        return None

    # Attach sampler parameters only when the graph independently resolves
    # a unique positive/negative pair matching the Wardrobe record.
    if graph:
        try:
            graph_candidates = parse_comfyui(graph)["candidates"]
        except (ValueError, TypeError, KeyError, AttributeError, RecursionError):
            graph_candidates = []
        by_prompt: dict[tuple[str, str], list[dict[str, Any]]] = {}
        for item in graph_candidates:
            by_prompt.setdefault((item["text"], item["negative"]), []).append(item["parameters"])
        for item in candidates:
            matches = by_prompt.get((item["text"], item["negative"]), [])
            if len(matches) == 1:
                item["parameters"] = {**matches[0], **item["parameters"]}

    return {
        "format": "Anima Wardrobe",
        "candidates": candidates,
        "parameters": {},
        "warnings": ["Wardrobe 记录了生成正文和可用的重建文本，可能缺少后续拼接内容或其他工作流分支。"],
    }


def extract_metadata(image: Image.Image) -> dict[str, Any]:
    warnings = []
    total = sum(len(value.encode("utf-8")) if isinstance(value, str) else len(value) if isinstance(value, bytes) else 0
                for value in image.info.values())
    if total > MAX_METADATA_BYTES:
        return empty_metadata(warning="图片元数据超过 4 MiB，已跳过提取")
    try:
        parsed = _native(image.info)
    except (ValueError, TypeError, KeyError, AttributeError, RecursionError):
        parsed = None
        warnings.append("Anima 元数据无法完整解析")
    if not parsed:
        parsed = _wardrobe(image)
    if not parsed:
        try:
            parsed = parse_image(image)
        except (ValueError, TypeError, KeyError, AttributeError, IndexError, OSError, RecursionError):
            return empty_metadata(warning="图片元数据无法解析，可使用 AI 反推")
    candidates = parsed.get("candidates", [])
    selected = candidates[0] if len(candidates) == 1 else {}
    values = selected.get("parameters", parsed.get("parameters", {}))
    if len(candidates) > 1:
        # Only common values belong in the unselected summary. Branch-specific
        # seed/model/steps remain attached to the candidate chosen by the user.
        values = {key: value for key, value in candidates[0]["parameters"].items()
                  if all(item["parameters"].get(key) == value for item in candidates[1:])}
    metadata = {"source": "metadata" if candidates else "none", "format": parsed.get("format", ""),
                "text": selected.get("text", ""), "negative": selected.get("negative", ""),
                "parameters": safe_parameters(values), "candidates": candidates,
                "warnings": warnings + parsed.get("warnings", [])}
    if len(json.dumps(metadata, ensure_ascii=False).encode("utf-8")) > MAX_METADATA_BYTES:
        return empty_metadata(warning="提取结果超过 4 MiB，已跳过提取")
    return metadata
