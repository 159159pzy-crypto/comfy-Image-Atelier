"""Pure text graphs and durable prompt snapshots for single-image jobs."""
from __future__ import annotations

import copy
import hashlib
import json
from typing import Any

from .workflow import WorkflowError

OUTPUT_NODE = "studio_prompt_result"


def compose_prompt(settings: dict[str, Any]) -> str:
    from .single_workbench import compose_single_prompt
    return compose_single_prompt(settings)


def prompt_fingerprint(settings: dict[str, Any]) -> str:
    single = settings["single"]
    value = {
        "model_profile": settings["model_profile"],
        "text": compose_prompt(settings),
        "processing": single["prompt_processing"],
    }
    return hashlib.sha256(json.dumps(value, sort_keys=True, ensure_ascii=False, separators=(",", ":")).encode()).hexdigest()


def final_prompts(settings: dict[str, Any], text: str) -> dict[str, str]:
    from .single_workbench import effective_single_prompts
    values = effective_single_prompts(settings, text)
    return {"text": text, **values, "negative": settings.get("negative_prompt", "")}


def build_prompt_graph(settings: dict[str, Any], client_id: str, *,
                       resolved_rules: dict[str, str] | None = None) -> dict[str, Any]:
    """No models, images or SaveImage nodes are reachable from this graph."""
    single = settings["single"]
    processing = single["prompt_processing"]
    graph: dict[str, Any] = {}
    source: Any = compose_prompt(settings)
    # OC replacement is performed on the authored text before general expansion.
    for name in ("oc", "expand"):
        config = processing[name]
        if not config["enabled"]:
            continue
        inputs = {key: copy.deepcopy(config[key]) for key in (
            "rule", "custom_rule", "custom_rule_content", "user_prompt",
            "llm_service", "ollama_auto_unload", "seed",
        )}
        if resolved_rules is not None and name in resolved_rules:
            inputs["rule"] = resolved_rules[name]
        if name == "oc":
            hints = [inputs["user_prompt"]]
            if config.get("character"):
                hints.append("OC character: " + config["character"])
            if config.get("features"):
                hints.append("Replace these features: " + config["features"])
            inputs["user_prompt"] = "\n\n".join(item for item in hints if item)
        inputs["source_text"] = source
        node_id = "studio_prompt_" + name
        graph[node_id] = {"class_type": "PromptExpand", "inputs": inputs}
        source = [node_id, 0]
    graph[OUTPUT_NODE] = {"class_type": "PreviewAny", "inputs": {"source": source}}
    return {"prompt": graph, "client_id": client_id}


def extract_prompt_result(entry: dict[str, Any]) -> str:
    status = entry.get("status") or {}
    if status.get("completed") is not True or status.get("status_str") != "success":
        raise WorkflowError("提示词处理任务未成功完成")
    for message in status.get("messages") or []:
        if isinstance(message, (list, tuple)) and message and message[0] in {"execution_error", "execution_interrupted"}:
            raise WorkflowError("提示词处理任务失败或已中断")
    output = (entry.get("outputs") or {}).get(OUTPUT_NODE) or {}
    values = output.get("text")
    if not isinstance(values, list) or len(values) != 1 or not isinstance(values[0], str) or not values[0].strip():
        raise WorkflowError("提示词处理未返回单个非空文本，已停止生图")
    return values[0]


async def validate_prompt_nodes(comfy: Any, settings: dict[str, Any]) -> dict[str, str]:
    """Check live choices and resolve transport-only custom-rule placeholders."""
    processing = settings["single"]["prompt_processing"]
    enabled = {name: item for name, item in processing.items() if item.get("enabled")}
    if not enabled:
        return {}
    info = await comfy.object_info()
    if "PreviewAny" not in info or "PromptExpand" not in info:
        raise WorkflowError("当前 ComfyUI 缺少 PromptExpand 或 PreviewAny 节点")
    inputs = (info["PromptExpand"].get("input") or {}).get("required") or {}
    rules: dict[str, str] = {}
    for name, item in enabled.items():
        for key in ("llm_service", "rule"):
            spec = inputs.get(key) or []
            choices = spec[0] if spec and isinstance(spec[0], list) else (spec[1].get("options") if len(spec) > 1 and isinstance(spec[1], dict) else None)
            if key == "rule" and item.get("custom_rule") and choices is not None:
                # Comfy validates this combo even when the node uses custom
                # content. Keep the authored rule/fingerprint intact and only
                # substitute an available choice in the submitted graph.
                if not choices:
                    raise WorkflowError("提示词处理 rule 不可用，请刷新服务/规则列表")
                default = spec[1].get("default") if len(spec) > 1 and isinstance(spec[1], dict) else None
                rules[name] = item[key] if item[key] in choices else default if default in choices else choices[0]
                continue
            if not item.get(key) or choices is not None and item[key] not in choices:
                raise WorkflowError(f"提示词处理 {key} 不可用，请刷新服务/规则列表")
            if key == "rule":
                rules[name] = item[key]
    return rules
