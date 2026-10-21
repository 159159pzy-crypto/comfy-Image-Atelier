"""Bounded, branch-local ComfyUI parser adapted from SD Prompt Reader 1.3.5.

Copyright (c) 2023 Rhys Yang. MIT; see LICENSE and UPSTREAM.txt.
"""
from __future__ import annotations

import json
from typing import Any

from .formats import MAX_GRAPH_DEPTH, MAX_GRAPH_NODES, MAX_METADATA_BYTES, MetadataError, candidate, join_text, object_json, parameters, text

SAMPLERS = {"KSampler", "KSamplerAdvanced", "KSampler (Efficient)", "SamplerCustom", "SamplerCustomAdvanced"}
OUTPUTS = {"SaveImage", "Image Save", "SDPromptSaver", "SaveImageWithMetadata"}
IMAGE_INPUTS = ("images", "image", "samples", "samples_from", "latent_image", "pixels")


def parse_comfyui(graph: dict[str, Any]) -> dict[str, Any]:
    if not graph or len(graph) > MAX_GRAPH_NODES:
        raise MetadataError("ComfyUI 节点数超过限制或工作流为空")
    graph = {str(k): v for k, v in graph.items() if isinstance(v, dict)}
    warnings: set[str] = set()
    # Bound total work as well as recursion: repeated links in a DAG must not
    # turn a small metadata file into exponential traversal.
    budget = MAX_GRAPH_NODES * 8

    def node(identity: str, seen: frozenset[str]) -> tuple[dict[str, Any], str, frozenset[str]]:
        nonlocal budget
        budget -= 1
        if budget < 0 or len(seen) >= MAX_GRAPH_DEPTH or identity in seen:
            raise MetadataError("ComfyUI 工作流包含循环或超过遍历限制")
        item = graph.get(identity)
        if not isinstance(item, dict) or not isinstance(item.get("inputs"), dict):
            raise MetadataError("ComfyUI 工作流包含无效连接")
        return item["inputs"], str(item.get("class_type", "")), seen | {identity}

    def reference(value: Any) -> str | None:
        if isinstance(value, list) and len(value) == 2 and isinstance(value[0], (str, int)) and type(value[1]) is int:
            return str(value[0])
        return None

    def scalar(value: Any, seen: frozenset[str] = frozenset(), *, target: str = "") -> Any:
        linked = reference(value)
        if linked is None:
            return value if isinstance(value, (str, int, float, bool)) else None
        values, kind, visited = node(linked, seen)
        if kind == "SDParameterGenerator":
            field = target if target in values else "seed" if target == "noise_seed" else target
            return scalar(values.get(field), visited, target=field)
        if kind in {"CR Seed", "PrimitiveNode", "PrimitiveInt", "PrimitiveFloat", "String", "Text Multiline", "SDParameterGenerator"}:
            for key in ("seed", "value", "text"):
                if key in values:
                    return scalar(values[key], visited, target=target)
        return None

    def conditioning(value: Any, role: str, seen: frozenset[str] = frozenset()) -> dict[str, str]:
        if isinstance(value, str):
            return {"": text(value)} if text(value) else {}
        linked = reference(value)
        if linked is None:
            return {}
        values, kind, visited = node(linked, seen)
        if kind == "CLIPTextEncodeSDXL":
            output = {}
            for label, key in (("CLIP G", "text_g"), ("CLIP L", "text_l")):
                parts = conditioning(values.get(key), role, visited)
                joined = join_text(parts.values())
                if joined:
                    output[label] = joined
            return output
        if kind in {"CLIPTextEncode", "CLIPTextEncodeSDXLRefiner"}:
            output = conditioning(values.get("text"), role, visited)
            if kind.endswith("Refiner") and output:
                return {"Refiner": join_text(output.values())}
            return output
        if kind == "SDXLPromptStyler":
            return conditioning(values.get("text_positive" if role == "positive" else "text_negative"), role, visited)
        if kind == "SDPromptReader":
            cached = graph[linked].get("is_changed", [])
            if isinstance(cached, list) and cached:
                values = object_json(cached[0])
            sdxl = values.get(f"{role}_sdxl")
            if isinstance(sdxl, dict) and sdxl:
                return {str(k): text(v) for k, v in sdxl.items() if text(v)}
            return conditioning(values.get(role), role, visited)
        if kind in {"String", "Text Multiline", "PrimitiveNode", "PrimitiveString", "SDParameterGenerator"}:
            return conditioning(values.get("text", values.get("value", values.get(role))), role, visited)
        if kind == "ConditioningZeroOut":
            return {}
        if kind in {"ControlNetApplyAdvanced", "CFGGuider"}:
            return conditioning(values.get(role), role, visited)
        if kind in {"ConditioningCombine", "ConditioningConcat", "ConditioningAverage"}:
            output: dict[str, str] = {}
            for key in ("conditioning_1", "conditioning_2", "conditioning_to", "conditioning_from"):
                for label, content in conditioning(values.get(key), role, visited).items():
                    if content and content != output.get(label):
                        output[label] = join_text((output.get(label), content))
            return output
        if "conditioning" in values:
            return conditioning(values["conditioning"], role, visited)
        warnings.add(f"未解析提示词节点：{kind or linked}")
        return {}

    def model_values(value: Any, seen: frozenset[str] = frozenset()) -> dict[str, Any]:
        linked = reference(value)
        if linked is None:
            return {}
        values, kind, visited = node(linked, seen)
        output: dict[str, Any] = {}
        if "model" in values:
            output.update(model_values(values["model"], visited))
        for key in ("ckpt_name", "unet_name", "model_name"):
            if isinstance(values.get(key), str):
                output["model"] = values[key]
        if "lora_name" in values:
            output.setdefault("loras", []).append({key: scalar(values[key], target=key) for key in ("lora_name", "strength_model", "strength_clip") if key in values})
        if isinstance(values.get("loras"), (dict, list)):
            output["loras"] = values["loras"]
        return output

    def sampling_values(values: dict[str, Any]) -> dict[str, Any]:
        output = {key: scalar(values[key], target=key) for key in (
            "seed", "noise_seed", "steps", "cfg", "sampler_name", "scheduler", "denoise", "add_noise",
            "start_at_step", "end_at_step", "return_with_leftover_noise") if key in values}
        output = {key: value for key, value in output.items() if value is not None}
        output.update(model_values(values.get("model")))
        for source in ("noise", "sampler", "sigmas"):
            linked = reference(values.get(source))
            if linked:
                child, _, _ = node(linked, frozenset())
                for key in ("noise_seed", "seed", "sampler_name", "scheduler", "steps", "denoise"):
                    if key in child and scalar(child[key], target=key) is not None:
                        output[key] = scalar(child[key], target=key)
        return parameters(output)

    roots = [key for key, value in graph.items() if value.get("class_type") in OUTPUTS]
    if not roots:
        roots = [key for key, value in graph.items() if value.get("class_type") in SAMPLERS]
    candidates: list[dict[str, Any]] = []
    partial: dict[str, Any] = {}
    seen_candidates: set[tuple[str, str, str]] = set()
    candidate_bytes = 0
    oversized = False

    def visit(identity: str, root: str, seen: frozenset[str]) -> None:
        nonlocal candidate_bytes, oversized
        values, kind, visited = node(identity, seen)
        if kind in SAMPLERS:
            sampling = dict(values)
            guider = reference(values.get("guider"))
            if guider:
                guide, _, _ = node(guider, visited)
                sampling = {**guide, **sampling}
                if "conditioning" in guide and "positive" not in sampling:
                    sampling["positive"] = guide["conditioning"]
            params = sampling_values(sampling)
            if not partial:
                partial.update(params)
            positives = conditioning(sampling.get("positive"), "positive")
            negatives = conditioning(sampling.get("negative"), "negative")
            for label, positive in positives.items():
                negative = negatives.get(label, negatives.get("", ""))
                marker = (identity, label, positive)
                if marker in seen_candidates:
                    continue
                seen_candidates.add(marker)
                item = candidate(f"comfy:{root}:{identity}:{len(candidates)}",
                                 f"输出 {root} · 采样器 {identity}" + (f" · {label}" if label else ""),
                                 positive, negative, params)
                if item:
                    candidate_bytes += len(json.dumps(item, ensure_ascii=False).encode("utf-8"))
                    if candidate_bytes > MAX_METADATA_BYTES:
                        oversized = True
                        raise MetadataError("ComfyUI 候选提示词总量超过限制")
                    candidates.append(item)
        for key in IMAGE_INPUTS:
            linked = reference(values.get(key))
            if linked:
                visit(linked, root, visited)

    for root in roots:
        try:
            visit(root, root, frozenset())
        except (ValueError, TypeError, KeyError, RecursionError):
            warnings.add(f"输出 {root} 的部分工作流无法可靠解析")
        if oversized:
            candidates.clear()
            warnings.add("ComfyUI 候选结果超过 4 MiB，已跳过提取")
            break
    if not roots:
        warnings.add("ComfyUI 工作流没有可识别的输出或采样器")
    return {"format": "ComfyUI", "candidates": candidates, "parameters": partial,
            "warnings": sorted(warnings)}
