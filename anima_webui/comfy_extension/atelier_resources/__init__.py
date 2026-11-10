"""ComfyUI extension providing bounded, read-only Atelier resource discovery."""
from __future__ import annotations

import asyncio

from .inspector import inspect_inventory, inspect_lora_files

NODE_CLASS_MAPPINGS = {}
NODE_DISPLAY_NAME_MAPPINGS = {}


def register_routes() -> None:
    import folder_paths
    import nodes
    from aiohttp import web
    from server import PromptServer
    from .capabilities import inspect_capabilities, probe_native

    @PromptServer.instance.routes.get("/anima/resources/inspect")
    async def inspect(request):
        requested = request.query.get("probe", "")
        modules = list(dict.fromkeys(value for value in requested.split(",") if value))
        if set(modules) - {"nvidia", "dlss"}:
            return web.json_response({"error": "未知能力检查"}, status=400)
        items = await asyncio.to_thread(inspect_inventory, folder_paths)
        lora_files = await asyncio.to_thread(inspect_lora_files, folder_paths)
        capabilities, context = await asyncio.to_thread(inspect_capabilities, folder_paths, nodes)
        if modules:
            running, _ = PromptServer.instance.prompt_queue.get_current_queue()
            if not running:
                try:
                    await asyncio.wait_for(probe_native(modules, capabilities, context), 18)
                except asyncio.TimeoutError:
                    for module in modules:
                        key = "nvidia.vsr" if module == "nvidia" else "nvidia.dlss"
                        if capabilities.get(key, {}).get("status") == "unknown":
                            capabilities[key]["message"] = "原生运行库检查超时，请稍后重试"
            else:
                for module in modules:
                    key = "nvidia.vsr" if module == "nvidia" else "nvidia.dlss"
                    if capabilities.get(key, {}).get("status") == "unknown":
                        capabilities[key]["message"] = "正在生成，原生能力验证将在空闲时进行"
        return web.json_response({"version": 1, "supported": True, "items": items,
                                  "lora_files": lora_files, "capabilities": capabilities})


# Importing the pure inspector in tests does not require a running ComfyUI.
try:
    import folder_paths as _folder_paths  # noqa: F401
except ImportError:
    pass
else:
    register_routes()
