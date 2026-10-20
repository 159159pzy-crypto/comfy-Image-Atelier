"""A narrow, secret-free bridge to the installed Prompt Assistant plugin."""
from __future__ import annotations

import asyncio
import copy
import hashlib
import json
import re
from typing import Any
from urllib.parse import quote

from .comfy import ComfyError
from .model_profiles import normalize_model_profile
from .workflow import WorkflowError


def choices(spec: Any) -> list[str]:
    if not isinstance(spec, list) or not spec:
        return []
    values = spec[0] if isinstance(spec[0], list) else (
        spec[1].get("options", []) if len(spec) > 1 and isinstance(spec[1], dict) else []
    )
    return [item for item in values if isinstance(item, str)] if isinstance(values, list) else []


def generation_settings(settings: dict[str, Any]) -> dict[str, Any]:
    """Retire automatic legacy AI without mutating drafts or frozen history."""
    result = copy.deepcopy(settings)
    if result.get("workbench") != "single" or not isinstance(result.get("single", {}), dict):
        return result
    single = result.setdefault("single", {})
    processing = single.get("prompt_processing")
    if isinstance(processing, dict):
        for item in processing.values():
            if isinstance(item, dict) and isinstance(item.get("enabled", False), bool):
                item["enabled"] = False
    if isinstance(single.get("prompt_job_id", ""), str):
        single["prompt_job_id"] = ""
    if single.get("processed_prompt_override") is None or isinstance(single.get("processed_prompt_override"), str):
        single["processed_prompt_override"] = None
    return result


def rewrite_fingerprint(snapshot: dict[str, Any]) -> str:
    return hashlib.sha256(json.dumps(snapshot, sort_keys=True, ensure_ascii=False).encode()).hexdigest()


class PromptAssistant:
    def __init__(self, comfy: Any):
        self.comfy = comfy

    async def _node_info(self) -> dict[str, Any]:
        try:
            # This small endpoint is deliberately fresh after launcher edits.
            if hasattr(self.comfy, "_json"):
                info = await self.comfy._json("GET", "/object_info/PromptExpand")
                preview = await self.comfy._json("GET", "/object_info/PreviewAny")
                if not isinstance(info, dict) or not isinstance(preview, dict):
                    raise WorkflowError("ComfyUI 节点信息格式不兼容")
                info = {**info, **preview}
            else:
                info = await self.comfy.object_info()
        except (ComfyError, OSError, asyncio.TimeoutError) as error:
            raise WorkflowError("无法连接 ComfyUI，请先启动服务") from error
        if not isinstance(info, dict) or not isinstance(info.get("PromptExpand"), dict) or "PreviewAny" not in info:
            raise WorkflowError("当前 ComfyUI 缺少 PromptExpand 或 PreviewAny 节点")
        return info["PromptExpand"]

    async def _catalog(self) -> tuple[dict[str, Any], dict[str, Any], list[dict[str, Any]]]:
        node = await self._node_info()
        module = node.get("python_module", "")
        if not isinstance(module, str) or not module.startswith("custom_nodes."):
            raise WorkflowError("无法识别 Prompt Assistant 插件，请通过启动器检查兼容性")
        directory = module.removeprefix("custom_nodes.")
        if not re.fullmatch(r"[\w.-]+", directory) or directory in {".", ".."}:
            raise WorkflowError("Prompt Assistant 插件路径无效")
        prefix = "/" + quote(directory, safe="") + "/api"
        try:
            services_data, templates_data = await asyncio.gather(
                self.comfy._json("GET", prefix + "/services"),
                self.comfy._json("GET", prefix + "/config/system_prompts"),
            )
        except (ComfyError, OSError, asyncio.TimeoutError, AttributeError) as error:
            # Plugin error bodies can contain credentials. Never forward them.
            raise WorkflowError("无法读取 Prompt Assistant 配置，请在启动器中检查服务") from error
        if not isinstance(services_data, dict) or not isinstance(templates_data, dict):
            raise WorkflowError("Prompt Assistant 配置格式不兼容，请通过启动器检查")
        if services_data.get("success") is False or not isinstance(services_data.get("services"), list) or not isinstance(templates_data.get("expand_prompts"), dict):
            raise WorkflowError("Prompt Assistant 配置格式不兼容，请通过启动器检查")
        services: list[dict[str, Any]] = []
        for item in services_data["services"]:
            if not isinstance(item, dict) or not all(isinstance(item.get(key), str) and item[key] for key in ("id", "name")):
                continue
            models = [
                {"name": model["name"]}
                for model in item.get("llm_models", []) if isinstance(model, dict)
                and isinstance(model.get("name"), str) and model["name"]
            ] if isinstance(item.get("llm_models"), list) else []
            # Explicit allowlist: upstream /services can expose plaintext keys.
            services.append({"id": item["id"], "name": item["name"],
                             "type": item.get("type") if isinstance(item.get("type"), str) else "",
                             "models": models})
        templates: list[dict[str, Any]] = []
        for template_id, item in templates_data["expand_prompts"].items():
            if not isinstance(template_id, str) or not isinstance(item, dict) or not isinstance(item.get("content"), str) or not item["content"].strip():
                continue
            templates.append({
                "id": template_id,
                "name": item.get("name") if isinstance(item.get("name"), str) else template_id,
                "category": item.get("category") if isinstance(item.get("category"), str) else "",
                "content": item["content"],
            })
        inputs = node.get("input") or {}
        if not isinstance(inputs, dict) or not all(isinstance(inputs.get(key, {}), dict) for key in ("required", "optional")):
            raise WorkflowError("PromptExpand 节点输入格式不兼容")
        all_inputs = {**(inputs.get("required") or {}), **(inputs.get("optional") or {})}
        spec = all_inputs.get("template_only")
        capability = isinstance(spec, list) and bool(spec) and spec[0] == "BOOLEAN"
        reason = "" if capability else "Prompt Assistant 尚未启用纯模板支持，请在启动器中修复兼容性后重启 ComfyUI"
        if capability and not services:
            reason = "请先在启动器中配置 LLM 服务和模型"
        elif capability and not any(item["models"] for item in services):
            reason = "请先在启动器中为 LLM 服务添加模型"
        elif capability and not templates:
            reason = "请先在启动器中创建系统提示词模板"
        public = {
            "available": not reason, "reason": reason, "template_only": capability,
            "services": services,
            "templates": [{key: item[key] for key in ("id", "name", "category")} for item in templates],
        }
        return public, all_inputs, templates

    async def options(self) -> dict[str, Any]:
        try:
            return (await self._catalog())[0]
        except WorkflowError as error:
            return {"available": False, "reason": str(error), "template_only": False,
                    "services": [], "templates": []}

    async def prepare(self, payload: dict[str, Any]) -> dict[str, Any]:
        allowed = {"kind", "model_profile", "source_text", "service_id", "model_name", "template_id"}
        if set(payload) - allowed or payload.get("kind") != "rewrite":
            raise WorkflowError("重写任务参数无效")
        try:
            profile = normalize_model_profile(payload.get("model_profile", "anima"))
        except ValueError as error:
            raise WorkflowError(str(error)) from error
        for field in ("source_text", "service_id", "model_name", "template_id"):
            value = payload.get(field)
            if not isinstance(value, str) or not value.strip():
                raise WorkflowError(f"{field} 必须是非空字符串")
            if len(value) > (20000 if field == "source_text" else 512):
                raise WorkflowError(f"{field} 过长")
        public, inputs, templates = await self._catalog()
        if not public["available"]:
            raise WorkflowError(public["reason"])
        service, transport_name = self._service(public, payload["service_id"], payload["model_name"], inputs)
        template = next((item for item in templates if item["id"] == payload["template_id"]), None)
        if template is None:
            raise WorkflowError("所选系统提示词模板已删除，请刷新后重新选择")
        if len(template["content"]) > 100000:
            raise WorkflowError("系统提示词模板不能超过 100000 个字符")
        rules = choices(inputs.get("rule"))
        if not rules:
            raise WorkflowError("PromptExpand 规则选项不可用，请刷新服务")
        return {
            "kind": "rewrite", "workbench": "single", "model_profile": profile,
            "source_text": payload["source_text"], "service_id": service["id"],
            "service_name": service["name"], "model_name": payload["model_name"],
            "template_id": template["id"], "template_name": template["name"],
            "template_content": template["content"], "llm_service": transport_name,
            "rule": rules[0],
        }

    @staticmethod
    def _service(public: dict[str, Any], service_id: str, model: str, inputs: dict[str, Any]) -> tuple[dict[str, Any], str]:
        matches = [item for item in public["services"] if item["id"] == service_id]
        if len(matches) != 1 or model not in [entry["name"] for entry in matches[0]["models"]]:
            raise WorkflowError("所选服务或模型已不可用，请刷新后重新选择")
        service = matches[0]
        if "/" in service["name"] or sum(item["name"] == service["name"] for item in public["services"]) != 1:
            raise WorkflowError("服务名称重复或包含斜杠，请在启动器中修改名称")
        transport_name = service["name"] + "/" + model
        if transport_name not in choices(inputs.get("llm_service")):
            raise WorkflowError("所选服务或模型尚未出现在节点选项中，请刷新后重试")
        return service, transport_name

    async def transport(self, snapshot: dict[str, Any]) -> dict[str, Any]:
        """Refresh transport choices, preserving the submitted template snapshot."""
        public, inputs, templates = await self._catalog()
        if not public["template_only"]:
            raise WorkflowError(public["reason"])
        _, name = self._service(public, snapshot["service_id"], snapshot["model_name"], inputs)
        if not any(item["id"] == snapshot["template_id"] for item in templates):
            raise WorkflowError("所选系统提示词模板已删除或内容为空，请刷新后重新选择")
        rules = choices(inputs.get("rule"))
        if not rules:
            raise WorkflowError("PromptExpand 规则选项不可用，请刷新服务")
        return {**snapshot, "llm_service": name, "rule": rules[0]}
