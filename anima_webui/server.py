from __future__ import annotations

import argparse
import asyncio
import hmac
import json
import logging
import os
import threading
import webbrowser
from pathlib import Path
from typing import Any
from urllib.parse import urlencode, urlsplit

from aiohttp import BodyPartReader, MultipartReader, web
from aiohttp.http_exceptions import BadHttpMessage
from aiohttp.web_protocol import RequestPayloadError

from .comfy import ComfyClient, ComfyError
from .catalog import CatalogError, PromptCatalog, SECTIONS, resolve_history_selection
from .persistence import StorageReadOnlyError
from .custom_prompts import CustomPromptStore
from .favorites import FavoriteSavePendingError, FavoritesService, FavoritesExtensionStore, favorite_key
from .input_images import InputImageStore, MAX_IMAGE_BYTES
from .single_workbench import SINGLE_DEFAULTS, active_lora_pipelines
from .history import HistoryStore
from .http_security import (
    SQLITE_MAX_INTEGER,
    LimitedMultipartStream,
    parse_authority,
    parse_origin,
    strict_json_loads,
)
from .lora_triggers import LoraTriggerOverrideStore
from .prompt_rules import PromptRuleStore
from .prompt_assistant import generation_settings
from .prompt_pools import PromptPoolStore, parse_prompt_import, parse_prompt_import_details
from .runner import BatchConflict, BatchManager
from .style_presets import StylePresetStore
from .workflow import WorkflowError, WorkflowTemplates, normalize_lora_path, validate_settings
from .model_profiles import (
    MODEL_PROFILE_IDS,
    model_profile_defaults,
    normalize_model_profile,
    profile_public,
)
from .launcher import configure_resources


APP_DIR = Path(__file__).resolve().parents[1]

logger = logging.getLogger(__name__)

# aiohttp 4 将移除字符串 app key;统一使用 AppKey(消除弃用告警)。
COMFY_KEY: web.AppKey[Any] = web.AppKey("comfy", object)
HISTORY_KEY: web.AppKey[Any] = web.AppKey("history", object)
MANAGER_KEY: web.AppKey[Any] = web.AppKey("manager", object)
CATALOG_KEY: web.AppKey[Any] = web.AppKey("catalog", object)
CUSTOM_PROMPTS_KEY: web.AppKey[Any] = web.AppKey("custom_prompts", object)
FAVORITES_KEY: web.AppKey[Any] = web.AppKey("favorites", object)
STYLE_PRESETS_KEY: web.AppKey[Any] = web.AppKey("style_presets", object)
LORA_TRIGGER_OVERRIDES_KEY: web.AppKey[Any] = web.AppKey("lora_trigger_overrides", object)
PROMPT_RULES_KEY: web.AppKey[Any] = web.AppKey("prompt_rules", object)
PROMPT_POOLS_KEY: web.AppKey[Any] = web.AppKey("prompt_pools", object)
INPUT_IMAGES_KEY: web.AppKey[Any] = web.AppKey("input_images", object)
ALLOWED_HOSTNAMES_KEY: web.AppKey[frozenset[str]] = web.AppKey(
    "allowed_hostnames", frozenset
)

LOCAL_HOSTNAMES = {"127.0.0.1", "localhost", "::1"}
RESOURCE_KINDS = ("models", "loras")


def _hostname(value: str) -> str:
    """从 Host 头(host[:port])或 Origin(scheme://host[:port])提取主机名。"""
    try:
        target = value if "://" in value else f"//{value}"
        return (urlsplit(target).hostname or "").lower()
    except ValueError:
        return ""


def _find_inventory_model_match(requested: Any, candidates: list[str]) -> str | None:
    """Resolve an exact model path or an unambiguous basename in an inventory."""
    normalized = normalize_lora_path(requested)
    identity = normalized.casefold()
    exact = [
        candidate
        for candidate in candidates
        if normalize_lora_path(candidate).casefold() == identity
    ]
    if exact:
        return exact[0] if len(exact) == 1 else None
    # A path-shaped request is an explicit path selection.  Do not silently
    # redirect it to another directory; only legacy basename settings use the
    # basename fallback.
    if "/" in normalized:
        return None
    matches = [
        candidate
        for candidate in candidates
        if normalize_lora_path(candidate).rsplit("/", 1)[-1].casefold()
        == identity
    ]
    return matches[0] if len(matches) == 1 else None


def _resolve_owned_model(
    requested: Any,
    model_names: list[str],
    owners: dict[str, str | None],
    profile: str,
    classifications: dict[str, dict[str, Any]] | None = None,
) -> str:
    """Resolve and enforce the owner of the model selected by a request."""
    normalized = normalize_lora_path(requested)
    identity = normalized.casefold()
    exact = [
        candidate
        for candidate in model_names
        if normalize_lora_path(candidate).casefold() == identity
    ]
    if len(exact) > 1:
        raise WorkflowError(f"主模型路径匹配不唯一: {normalized}")
    if exact:
        actual = exact[0]
    elif "/" in normalized:
        raise WorkflowError(f"主模型不存在于当前 ComfyUI 清单: {normalized}")
    else:
        basename_matches = [
            candidate
            for candidate in model_names
            if normalize_lora_path(candidate).rsplit("/", 1)[-1].casefold()
            == identity
        ]
        if not basename_matches:
            raise WorkflowError(f"主模型不存在于当前 ComfyUI 清单: {normalized}")
        if len(basename_matches) != 1:
            raise WorkflowError(f"主模型 basename 匹配不唯一: {normalized}")
        actual = basename_matches[0]

    assigned = owners.get(actual)
    if assigned != profile:
        item = (classifications or {}).get(actual, {})
        reason = item.get("classification_reason") or f"插件分类为 {item.get('base_model') or assigned or 'Unknown'}"
        raise WorkflowError(
            f"主模型不适用于 {profile} 档案: {actual}；{reason}，请在 LoRA Manager 中检查分类"
        )
    return actual


@web.middleware
async def local_only_middleware(request: web.Request, handler: Any) -> web.StreamResponse:
    # 只信任本机来源:防御恶意网页发起的 CSRF 与 DNS 重绑定。
    allowed_hostnames = request.app[ALLOWED_HOSTNAMES_KEY]
    hosts = request.headers.getall("Host", [])
    authority = parse_authority(hosts[0]) if len(hosts) == 1 else None
    if authority is None or authority[0] not in allowed_hostnames:
        return web.json_response({"error": "仅允许本机访问"}, status=403)
    hostname, host_port = authority
    origins = request.headers.getall("Origin", [])
    if origins:
        origin = parse_origin(origins[0]) if len(origins) == 1 else None
        if origin is None or origin[1] != hostname:
            return web.json_response({"error": "仅允许同源访问"}, status=403)
        scheme, _, origin_port = origin
        # Explicit non-local trusted hosts may terminate TLS at a reverse
        # proxy. Its public Host (including any non-default port) must be
        # preserved. Never trust client-supplied X-Forwarded-* headers.
        if hostname in LOCAL_HOSTNAMES and scheme != request.scheme:
            return web.json_response({"error": "仅允许同源访问"}, status=403)
        if origin_port != (host_port or (443 if scheme == "https" else 80)):
            return web.json_response({"error": "仅允许同源访问"}, status=403)
    if request.path.startswith("/api/") and request.headers.get("Sec-Fetch-Site") in {"cross-site", "same-site"}:
        return web.json_response({"error": "仅允许同源访问"}, status=403)
    return await handler(request)


@web.middleware
async def error_middleware(request: web.Request, handler: Any) -> web.StreamResponse:
    try:
        response = await handler(request)
    except FavoriteSavePendingError as error:
        response = web.json_response(
            {"error": str(error), "code": "favorite_save_pending", "item_id": error.item_id,
             "source_saved": True, "retryable": True},
            status=502,
        )
    except StorageReadOnlyError as error:
        response = web.json_response({"error": str(error), "code": error.code, "store": error.store, "path": str(error.path)}, status=503)
    except WorkflowError as error:
        response = web.json_response({"error": str(error)}, status=400)
    except CatalogError as error:
        response = web.json_response({"error": str(error)}, status=400)
    except BatchConflict as error:
        response = web.json_response({"error": str(error)}, status=409)
    except KeyError:
        response = web.json_response({"error": "记录不存在"}, status=404)
    except ComfyError as error:
        response = web.json_response({"error": str(error)}, status=503)
    except json.JSONDecodeError:
        response = web.json_response({"error": "请求 JSON 无效"}, status=400)
    except RequestPayloadError:
        response = web.json_response({"error": "请求体传输或压缩编码无效"}, status=400)
        # The payload parser cannot recover this HTTP connection reliably.
        response.force_close()
    except web.HTTPException as error:
        if error.status < 400 or not request.path.startswith("/api/"):
            raise
        response = web.json_response(
            {"error": error.reason},
            status=error.status,
            headers={
                name: value for name, value in error.headers.items()
                if name.lower() not in {"content-type", "content-length"}
            },
        )
    except Exception:
        # Preserve cancellation (a BaseException) and never return internal
        # paths, provider responses or tracebacks to API consumers.
        logger.exception("Unhandled request failure: %s %s", request.method, request.path)
        response = web.json_response({"error": "服务内部错误，请检查服务日志", "code": "internal_error"}, status=500)
    response.headers.setdefault("X-Content-Type-Options", "nosniff")
    response.headers.setdefault("X-Frame-Options", "DENY")
    response.headers.setdefault("Content-Security-Policy", "frame-ancestors 'none'")
    response.headers.setdefault("Referrer-Policy", "same-origin")
    if request.path.startswith("/api/"):
        response.headers.setdefault("Cache-Control", "no-store")
    if request.path == "/" or request.path.startswith("/static/"):
        response.headers["Cache-Control"] = "no-cache, max-age=0, must-revalidate"
        response.headers["Pragma"] = "no-cache"
    return response


async def _json_body(request: web.Request) -> dict[str, Any]:
    if request.content_type != "application/json" and not (
        request.content_type.startswith("application/") and request.content_type.endswith("+json")
    ):
        raise web.HTTPUnsupportedMediaType(reason="请求 Content-Type 必须是 application/json")
    try:
        value = await request.json(loads=strict_json_loads)
    except (ValueError, UnicodeError, LookupError, RecursionError) as error:
        raise WorkflowError("请求 JSON 无效") from error
    if not isinstance(value, dict):
        raise WorkflowError("请求内容必须是对象")
    return value


def _image_id(raw: Any) -> int:
    if isinstance(raw, str) and raw.isascii() and raw.isdecimal() and len(raw) <= 19:
        raw = int(raw)
    if type(raw) is not int or not 1 <= raw <= SQLITE_MAX_INTEGER:
        raise WorkflowError("image_id 必须为有效作品 ID")
    return raw


def _query_bool(request: web.Request, name: str, default: bool = False) -> bool:
    raw = request.query.get(name)
    if raw is None:
        return default
    if raw.lower() in {"1", "true", "yes"}:
        return True
    if raw.lower() in {"0", "false", "no"}:
        return False
    raise WorkflowError(f"{name} 必须是布尔值")


def create_app(
    *,
    app_dir: str | Path = APP_DIR,
    comfy: Any | None = None,
    history_path: str | Path | None = None,
    custom_prompts_path: str | Path | None = None,
    style_presets_path: str | Path | None = None,
    lora_trigger_overrides_path: str | Path | None = None,
    resource_profiles_path: str | Path | None = None,
    prompt_replacements_path: str | Path | None = None,
    anima_tools_dir: str | Path | None = None,
    trusted_hostnames: set[str] | frozenset[str] | None = None,
    data_dir: str | Path | None = None,
    resource_paths: dict[str, str] | None = None,
    shutdown_token: str | None = None,
) -> web.Application:
    root = Path(app_dir)
    storage = Path(data_dir) if data_dir is not None else root / "data"
    client = comfy or ComfyClient()
    history = HistoryStore(history_path or storage / "history.sqlite3")
    catalog = PromptCatalog(root, anima_tools_dir)
    custom_prompts = CustomPromptStore(
        custom_prompts_path or storage / "custom_prompts.json", catalog
    )
    favorite_extensions = FavoritesExtensionStore(storage / "favorite_extensions.json")
    favorites = FavoritesService(client, catalog, expression_store=favorite_extensions)
    profile_favorites = {
        "anima": favorites,
        "krea2": FavoritesService(client, catalog, expression_store=favorite_extensions, model_profile="krea2"),
    }
    input_images = InputImageStore(storage / "input-images")
    prompt_rules = PromptRuleStore(
        prompt_replacements_path or storage / "prompt_replacements.json"
    )
    lora_trigger_overrides = LoraTriggerOverrideStore(
        lora_trigger_overrides_path or storage / "lora_trigger_overrides.json"
    )
    style_presets = StylePresetStore(
        style_presets_path or storage / "style_presets.json", prompt_rules
    )
    prompt_pools = PromptPoolStore(storage / "prompt_pools.json")
    # Deprecated argument retained for embedded callers. The legacy ownership
    # file is intentionally never opened, migrated, repaired, or overwritten.
    templates = WorkflowTemplates.load(root / "templates")
    startup_defaults = configure_resources(templates, resource_paths)
    manager = BatchManager(
        templates, history, client, catalog, prompt_rules, prompt_pools, input_images
    )

    startup_warnings = [
        *catalog.load_warnings,
        *history.load_warnings,
        *custom_prompts.load_warnings,
        *style_presets.load_warnings,
        *lora_trigger_overrides.load_warnings,
        *prompt_rules.load_warnings,
        *prompt_pools.load_warnings,
        *favorite_extensions.load_warnings,
    ]

    app = web.Application(middlewares=[error_middleware, local_only_middleware], client_max_size=MAX_IMAGE_BYTES + 1024 * 1024)
    normalized_trusted_hostnames = {
        hostname
        for value in trusted_hostnames or set()
        if (hostname := _hostname(value))
    }
    app[ALLOWED_HOSTNAMES_KEY] = frozenset(LOCAL_HOSTNAMES | normalized_trusted_hostnames)
    app[COMFY_KEY] = client
    app[HISTORY_KEY] = history
    app[MANAGER_KEY] = manager
    app[CATALOG_KEY] = catalog
    app[CUSTOM_PROMPTS_KEY] = custom_prompts
    app[FAVORITES_KEY] = favorites
    app[STYLE_PRESETS_KEY] = style_presets
    app[LORA_TRIGGER_OVERRIDES_KEY] = lora_trigger_overrides
    app[PROMPT_RULES_KEY] = prompt_rules
    app[PROMPT_POOLS_KEY] = prompt_pools
    app[INPUT_IMAGES_KEY] = input_images
    cleanup_task: asyncio.Task[None] | None = None
    shutdown_exit_handle: asyncio.TimerHandle | None = None

    async def favorite_keys(section: str, collection: str = "") -> set[str] | None:
        if not collection:
            return None
        payload = await favorites.get(section)
        collection_ids = {collection}
        if collection != "__all__":
            pending = [collection]
            while pending:
                parent_id = pending.pop()
                children = [
                    group["id"]
                    for group in payload["groups"]
                    if group.get("parentId") == parent_id and group["id"] not in collection_ids
                ]
                collection_ids.update(children)
                pending.extend(children)
        return {
            favorite_key(section, item)
            for item in payload["items"]
            if (collection == "__all__" and item.get("groupIds"))
            or bool(collection_ids.intersection(item.get("groupIds") or []))
        }

    async def config(_: web.Request) -> web.Response:
        return web.json_response(
            {
                "defaults": startup_defaults,
                "model_profiles": {profile_id: profile_public(profile_id) for profile_id in MODEL_PROFILE_IDS},
                "single_defaults": SINGLE_DEFAULTS,
                "comfy_url": getattr(client, "base_url", "local"),
                "catalog": {
                    "available": catalog.available,
                    "path": str(catalog.tools_dir or ""),
                    "counts": {section: catalog.count(section) for section in SECTIONS},
                    "discovery": catalog.discovery,
                },
                "storage_status": {
                    store.store_name: {**store.storage_status(), "path": str(store.path)}
                    for store in (history, custom_prompts, style_presets, lora_trigger_overrides, prompt_rules, prompt_pools, favorite_extensions)
                },
                "warnings": startup_warnings,
            }
        )

    async def _pool_response(
        section: str, params: dict[str, Any], selection: dict[str, Any] | None
    ) -> web.Response:
        """GET /api/pools 与 POST /api/pools/query 共用的参数归一化与搜索。"""
        try:
            model_profile = normalize_model_profile(params.get("model_profile", "anima"))
        except ValueError as error:
            raise WorkflowError(str(error)) from error
        if model_profile == "krea2":
            # Krea 2 deliberately does not consume Anima's five taxonomy
            # pools.  Returning their contents here would leak Anima entries
            # into the profile namespace; callers should use
            # /api/prompt-pools for Krea natural-language entries instead.
            raise WorkflowError("Krea 2 使用独立自然语言提示词池")
        try:
            page = int(params.get("page") or 1)
            limit = int(params.get("limit") or 48)
        except (TypeError, ValueError) as error:
            raise WorkflowError("分页参数无效") from error
        collection = str(params.get("collection") or "")
        sort = str(params.get("sort") or "")
        custom_group = str(params.get("custom_group") or "")
        keys = await favorite_keys(section, collection or ("__all__" if sort == "favorite-first" else ""))
        result = catalog.search(
            section,
            query=str(params.get("q") or ""),
            categories=[str(value) for value in params.get("categories") or []],
            traits=[str(value) for value in params.get("traits") or []],
            gender=str(params.get("gender") or ""),
            hair=str(params.get("hair") or ""),
            eye=str(params.get("eye") or ""),
            series=str(params.get("series") or ""),
            custom_group=custom_prompts.group_filter_ids(section, custom_group)
            if custom_group
            else "",
            favorite_keys=keys,
            favorites_only=bool(collection),
            sort=sort,
            page=page,
            limit=limit,
            selection=selection,
        )
        return web.json_response(result)

    async def pool(request: web.Request) -> web.Response:
        params: dict[str, Any] = {
            key: request.query.get(key, "")
            for key in ("q", "gender", "hair", "eye", "series", "custom_group", "collection", "sort")
        }
        params["page"] = request.query.get("page", "1")
        params["limit"] = request.query.get("limit", "48")
        params["model_profile"] = request.query.get("model_profile", "anima")
        params["categories"] = request.query.getall("category", [])
        params["traits"] = request.query.getall("trait", [])
        return await _pool_response(request.match_info["section"], params, None)

    async def pool_query(request: web.Request) -> web.Response:
        body = await _json_body(request)
        params = dict(body)
        params.setdefault("model_profile", "anima")
        params["categories"] = body.get("categories") if isinstance(body.get("categories"), list) else []
        params["traits"] = body.get("traits") if isinstance(body.get("traits"), list) else []
        selection = body.get("selection") if isinstance(body.get("selection"), dict) else None
        return await _pool_response(request.match_info["section"], params, selection)

    def request_favorites(request: web.Request) -> FavoritesService:
        try:
            profile = normalize_model_profile(request.query.get("model_profile", "anima"))
        except ValueError as error:
            raise WorkflowError(str(error)) from error
        service = profile_favorites[profile]
        service._section(request.match_info["section"])
        return service

    async def get_favorites(request: web.Request) -> web.Response:
        return web.json_response(await request_favorites(request).get(request.match_info["section"]))

    async def update_favorite(request: web.Request) -> web.Response:
        return web.json_response(
            await request_favorites(request).update_item(request.match_info["section"], await _json_body(request))
        )

    async def save_favorite_text(request: web.Request) -> web.Response:
        service = request_favorites(request)
        return web.json_response(
            await service.save_text(request.match_info["section"], await _json_body(request), custom_prompts)
        )

    async def update_favorite_selection(request: web.Request) -> web.Response:
        service = request_favorites(request)
        body = await _json_body(request)
        return web.json_response(
            await service.update_selection(
                request.match_info["section"], body.get("selection"), body.get("groupIds")
            )
        )

    async def create_favorite_group(request: web.Request) -> web.Response:
        return web.json_response(
            await request_favorites(request).create_group(request.match_info["section"], await _json_body(request)),
            status=201,
        )

    async def update_favorite_group(request: web.Request) -> web.Response:
        return web.json_response(
            await request_favorites(request).update_group(
                request.match_info["section"], request.match_info["group_id"], await _json_body(request)
            )
        )

    async def import_favorite_child_group(request: web.Request) -> web.Response:
        service = request_favorites(request)
        section = request.match_info["section"]
        if section == "prompt":
            raise WorkflowError("自然语言收藏不支持导入 Anima 自定义分组")
        body = await _json_body(request)
        custom_group_id = str(body.get("customGroupId") or "").strip()
        if not custom_group_id:
            raise WorkflowError("customGroupId 不能为空")
        source_group = next(
            (
                group
                for group in custom_prompts.list_groups(section)["groups"]
                if group["id"] == custom_group_id
            ),
            None,
        )
        if source_group is None:
            raise KeyError(custom_group_id)
        return web.json_response(
            await service.import_custom_group(
                section,
                request.match_info["parent_id"],
                source_group,
                custom_prompts.list(section, custom_group_id),
            ),
            status=201,
        )

    async def delete_favorite_group(request: web.Request) -> web.Response:
        return web.json_response(
            await request_favorites(request).delete_group(
                request.match_info["section"],
                request.match_info["group_id"],
                _query_bool(request, "deleteItems"),
            )
        )

    async def list_custom_prompts(request: web.Request) -> web.Response:
        section = request.query.get("section") or None
        return web.json_response({"items": custom_prompts.list(section)})

    async def create_custom_prompt(request: web.Request) -> web.Response:
        return web.json_response(await custom_prompts.create(await _json_body(request)), status=201)

    async def update_custom_prompt(request: web.Request) -> web.Response:
        item = await custom_prompts.update(request.match_info["item_id"], await _json_body(request))
        try:
            await favorites.sync_custom(item["section"], item)
        except ComfyError as error:
            logger.warning("同步收藏昵称失败(条目 %s): %s", item.get("id"), error)
        return web.json_response(item)

    async def delete_custom_prompt(request: web.Request) -> web.Response:
        if not await custom_prompts.delete(request.match_info["item_id"]):
            raise KeyError(request.match_info["item_id"])
        return web.json_response({"deleted": True})

    async def list_custom_groups(request: web.Request) -> web.Response:
        return web.json_response(custom_prompts.list_groups(request.match_info["section"]))

    async def create_custom_group(request: web.Request) -> web.Response:
        return web.json_response(
            await custom_prompts.create_group(request.match_info["section"], await _json_body(request)),
            status=201,
        )

    async def update_custom_group(request: web.Request) -> web.Response:
        return web.json_response(
            await custom_prompts.update_group(
                request.match_info["section"], request.match_info["group_id"], await _json_body(request)
            )
        )

    async def delete_custom_group(request: web.Request) -> web.Response:
        delete_mode = request.query.get("deleteMode")
        delete_items = False if delete_mode is not None else _query_bool(request, "deleteItems")
        return web.json_response(
            await custom_prompts.delete_group(
                request.match_info["section"],
                request.match_info["group_id"],
                delete_items,
                delete_mode,
            )
        )

    async def custom_template(request: web.Request) -> web.Response:
        filename, content_type, body = custom_prompts.template(
            request.match_info["section"], request.match_info["format"]
        )
        return web.Response(
            body=body,
            content_type=content_type,
            headers={"Content-Disposition": f'attachment; filename="{filename}"'},
        )

    async def preview_custom_import(request: web.Request) -> web.Response:
        body = await _json_body(request)
        return web.json_response(
            custom_prompts.preview_import(
                str(body.get("format") or ""),
                str(body.get("content") or ""),
                str(body.get("section") or ""),
                str(body.get("bundleName") or ""),
            )
        )

    async def commit_custom_import(request: web.Request) -> web.Response:
        body = await _json_body(request)
        return web.json_response(
            await custom_prompts.commit_import(
                body.get("rows"),
                str(body.get("section") or ""),
                body.get("targetGroupIds"),
                str(body.get("bundleName") or ""),
            )
        )

    async def status(_: web.Request) -> web.Response:
        try:
            stats = await client.status()
            system = stats.get("system") or {}
            devices = stats.get("devices") or []
            return web.json_response(
                {
                    "online": True,
                    "version": system.get("comfyui_version", ""),
                    "device": devices[0].get("name", "") if devices else "",
                }
            )
        except ComfyError as error:
            return web.json_response({"online": False, "error": str(error)})

    async def _lora_inventory_item(filename: Any) -> tuple[dict[str, Any], str]:
        target = str(filename or "")
        if not target:
            raise WorkflowError("filename 不能为空")
        from .workflow import normalize_lora_path

        identity = normalize_lora_path(target).casefold()
        inventory = await client.lora_inventory()
        matches = [
            item
            for item in inventory.get("items") or []
            if normalize_lora_path(item.get("filename")).casefold() == identity
        ]
        if len(matches) != 1:
            raise WorkflowError(f"LoRA 文件不存在: {target}")
        return matches[0], normalize_lora_path(matches[0]["filename"])

    def _public_lora_item(
        item: dict[str, Any], model_profile: str = "anima"
    ) -> dict[str, Any]:
        value = dict(item)
        source_words = value.get("trigger_words") or []
        effective_words, overridden = lora_trigger_overrides.effective(
            value["filename"], source_words, model_profile
        )
        # Trigger overrides remain profile-scoped; they must not rewrite the
        # authoritative classification when editing an unavailable resource.
        value.setdefault("model_profile", model_profile)
        value["source_trigger_words"] = source_words
        value["trigger_words"] = effective_words
        value["trigger_override"] = overridden
        if overridden:
            value["trigger_metadata_available"] = True
        preview = str(value.get("preview") or "")
        value["preview"] = (
            f"/api/loras/preview?{urlencode({'filename': value['filename']})}"
            if preview
            else ""
        )
        return value

    async def loras(request: web.Request) -> web.Response:
        model_profile = request.query.get("model_profile", "anima")
        # Validation happens even when inventory is empty.
        from .model_profiles import normalize_model_profile
        try:
            model_profile = normalize_model_profile(model_profile)
        except ValueError as error:
            raise WorkflowError(str(error)) from error
        inventory = await client.resource_classifications(
            "loras", refresh=_query_bool(request, "refresh")
        )
        inventory_items = inventory.get("items") or []
        public = {
            "items": [],
            "count": 0,
            "source": inventory["source"],
            "manager_url": inventory["manager_url"],
            "classification_items": inventory_items,
        }
        for item in inventory_items:
            if item.get("model_profile") != model_profile:
                continue
            public["items"].append(_public_lora_item(item, model_profile))
        public["count"] = len(public["items"])
        public["model_profile"] = model_profile
        return web.json_response(public)

    async def update_lora_triggers(request: web.Request) -> web.Response:
        body = await _json_body(request)
        from .model_profiles import normalize_model_profile
        try:
            model_profile = normalize_model_profile(body.get("model_profile", "anima"))
        except ValueError as error:
            raise WorkflowError(str(error)) from error
        item, filename = await _lora_inventory_item(body.get("filename"))
        words = await lora_trigger_overrides.set(
            filename, body.get("triggerWords"), model_profile
        )
        value = _public_lora_item({**item, "filename": filename}, model_profile)
        value["trigger_words"] = words
        value["trigger_override"] = True
        return web.json_response(value)

    async def reset_lora_triggers(request: web.Request) -> web.Response:
        from .model_profiles import normalize_model_profile
        try:
            model_profile = normalize_model_profile(
                request.query.get("model_profile", "anima")
            )
        except ValueError as error:
            raise WorkflowError(str(error)) from error
        item, filename = await _lora_inventory_item(request.query.get("filename"))
        await lora_trigger_overrides.delete(filename, model_profile)
        return web.json_response(
            _public_lora_item({**item, "filename": filename}, model_profile)
        )

    async def lora_preview(request: web.Request) -> web.Response:
        filename = request.query.get("filename")
        if not filename:
            raise WorkflowError("filename 不能为空")
        body, content_type, cache_control = await client.lora_preview(filename)
        return web.Response(
            body=body,
            content_type=content_type.split(";", 1)[0],
            headers={"Cache-Control": cache_control},
        )

    async def resources(request: web.Request) -> web.Response:
        try:
            selected_profile = normalize_model_profile(
                request.query.get("model_profile", "anima")
            )
        except ValueError as error:
            raise WorkflowError(str(error)) from error
        classified = await client.resource_classifications(
            "models", refresh=_query_bool(request, "refresh")
        )
        inventory = dict(await client.resource_inventory())
        inventory.setdefault("clips", [])
        inventory.setdefault("vaes", [])
        inventory.setdefault("seedvr2_models", [])
        inventory.setdefault("seedvr2_vaes", [])
        inventory.setdefault("nodes", {})
        inventory.setdefault("plugins", {})
        model_items = classified["items"]
        model_names = [item["name"] for item in model_items]
        full_inventory = dict(inventory)
        full_inventory["models"] = model_names
        model_ownership = {item["name"]: item["model_profile"] for item in model_items}
        inventory["model_items"] = model_items
        inventory["source"] = classified["source"]
        inventory["manager_url"] = classified["manager_url"]
        inventory["model_assignments"] = {
            name: model_ownership.get(name) for name in model_names
        }
        inventory["profile_models"] = {
            profile_id: [
                name for name in model_names if model_ownership.get(name) == profile_id
            ]
            for profile_id in MODEL_PROFILE_IDS
        }
        inventory["unassigned_models"] = [
            name for name in model_names if model_ownership.get(name) is None
        ]
        # Preserve the existing compact inventory keys while making the
        # profile-specific model dropdown safe by construction.
        inventory["models"] = list(inventory["profile_models"].get(selected_profile, []))
        inventory["model_profile"] = selected_profile
        profile_details: dict[str, dict[str, Any]] = {}
        for profile_id in MODEL_PROFILE_IDS:
            profile_models = inventory["profile_models"][profile_id]
            profile_inventory = {**full_inventory, "models": profile_models}
            details = profile_public(profile_id, available=profile_inventory)
            profile_model_name = details["model_name"]
            resolved_model_name = _find_inventory_model_match(
                profile_model_name, model_names
            )
            if resolved_model_name not in profile_models:
                resolved_model_name = None
            if resolved_model_name is not None:
                details["model_name"] = resolved_model_name
            # profile_public's basename-aware resource check intentionally
            # works for generic callers. Here ownership and ambiguity must be
            # reflected too: only an exact or unique match in this profile's
            # full inventory makes the default model available. A known but
            # incompatible exact path must never redirect to a subfolder.
            details["available"]["model"] = resolved_model_name is not None
            if not details["available"]["model"] and profile_model_name not in details["missing"]:
                details["missing"].append(profile_model_name)
            profile_details[profile_id] = details
        inventory["model_profiles"] = profile_details
        return web.json_response(inventory)

    async def resource_ownership(request: web.Request) -> web.Response:
        kind = request.query.get("kind", "models")
        if kind not in RESOURCE_KINDS:
            raise WorkflowError("kind 必须是 models 或 loras")
        classified = await client.resource_classifications(
            kind, refresh=_query_bool(request, "refresh")
        )
        items = classified["items"]
        return web.json_response(
            {
                "kind": kind,
                "source": classified["source"],
                "manager_url": classified["manager_url"],
                "items": items,
                "unassigned": [item for item in items if item["model_profile"] is None],
                "assignments": {
                    item["name"]: item["model_profile"] for item in items
                },
            }
        )

    async def update_resource_ownership(request: web.Request) -> web.Response:
        return web.json_response(
            {"error": "本地归属编辑已停用，请在 ComfyUI-Lora-Manager 中修改基础模型分类，然后刷新分类。",
             "code": "classification_managed_by_lora_manager"},
            status=410,
        )

    async def list_prompt_pool(request: web.Request) -> web.Response:
        profile = request.query.get("model_profile", "krea2")
        raw_selected = request.query.get("selected_ids", "")
        selected_ids = [value for value in raw_selected.split(",") if value.strip()]
        try:
            page = int(request.query.get("page", "1"))
            limit = int(request.query.get("limit", "48"))
        except ValueError as error:
            raise WorkflowError("提示词池分页参数无效") from error
        selected_only = _query_bool(request, "selected_only", False)
        return web.json_response(
            prompt_pools.list(
                profile,
                query=request.query.get("q", ""),
                page=page,
                limit=limit,
                selected_ids=selected_ids,
                selected_only=selected_only,
            )
        )

    async def preview_prompt_pool_import(request: web.Request) -> web.Response:
        body = await _json_body(request)
        unknown = set(body) - {"model_profile", "mode", "format", "content"}
        if unknown:
            raise WorkflowError(
                f"提示词池导入包含未知字段: {', '.join(sorted(unknown))}"
            )
        if "content" not in body:
            raise WorkflowError("提示词池导入 content 不能为空")
        format_hint = body.get("format", "")
        if not isinstance(format_hint, str):
            raise WorkflowError("提示词池导入 format 必须是字符串")
        parsed = parse_prompt_import_details(body["content"], format_hint)
        return web.json_response(
            prompt_pools.preview_import(
                body.get("model_profile", "krea2"),
                parsed,
                mode=body.get("mode", "replace"),
            )
        )

    async def import_prompt_pool(request: web.Request) -> web.Response:
        body = await _json_body(request)
        unknown = set(body) - {"model_profile", "items", "content", "mode", "format"}
        if unknown:
            raise WorkflowError(f"提示词池导入包含未知字段: {', '.join(sorted(unknown))}")
        format_hint = body.get("format", "")
        if not isinstance(format_hint, str):
            raise WorkflowError("提示词池导入 format 必须是字符串")
        profile = body.get("model_profile", "krea2")
        raw = body.get("items", body.get("content", []))
        items = parse_prompt_import(raw, format_hint)
        return web.json_response(
            await prompt_pools.import_items(profile, items, mode=body.get("mode", "replace"))
        )

    async def create_prompt_pool_item(request: web.Request) -> web.Response:
        body = await _json_body(request)
        unknown = set(body) - {"model_profile", "text"}
        if unknown:
            raise WorkflowError(
                f"提示词池条目包含未知字段: {', '.join(sorted(unknown))}"
            )
        if "text" not in body:
            raise WorkflowError("提示词池 text 不能为空")
        item = await prompt_pools.create_item(body.get("model_profile", "krea2"), body["text"])
        return web.json_response(item, status=201)

    async def ensure_prompt_pool_item(request: web.Request) -> web.Response:
        body = await _json_body(request)
        if set(body) - {"model_profile", "text"}:
            raise WorkflowError("提示词池条目包含未知字段")
        if "text" not in body:
            raise WorkflowError("提示词池 text 不能为空")
        return web.json_response(
            await prompt_pools.ensure_item(body.get("model_profile", "krea2"), body["text"])
        )

    async def update_prompt_pool_item(request: web.Request) -> web.Response:
        body = await _json_body(request)
        unknown = set(body) - {"model_profile", "id", "text"}
        if unknown:
            raise WorkflowError(
                f"提示词池条目包含未知字段: {', '.join(sorted(unknown))}"
            )
        if "id" not in body or "text" not in body:
            raise WorkflowError("提示词池更新需要 id 和 text")
        return web.json_response(
            await prompt_pools.update_item(
                body.get("model_profile", "krea2"), body["id"], body["text"]
            )
        )

    async def delete_prompt_pool_items(request: web.Request) -> web.Response:
        body = await _json_body(request)
        unknown = set(body) - {"model_profile", "ids"}
        if unknown:
            raise WorkflowError(
                f"提示词池删除包含未知字段: {', '.join(sorted(unknown))}"
            )
        if "ids" not in body:
            raise WorkflowError("提示词池删除需要 ids")
        return web.json_response(
            await prompt_pools.delete_items(body.get("model_profile", "krea2"), body["ids"])
        )

    async def delete_prompt_pool_item(request: web.Request) -> web.Response:
        profile = request.query.get("model_profile", "krea2")
        item_id = request.query.get("id")
        return web.json_response(await prompt_pools.delete_item(profile, item_id))

    async def list_style_presets(request: web.Request) -> web.Response:
        return web.json_response(style_presets.list(request.query.get("model_profile")))

    async def create_style_preset(request: web.Request) -> web.Response:
        return web.json_response(await style_presets.create(await _json_body(request)), status=201)

    async def update_style_preset(request: web.Request) -> web.Response:
        return web.json_response(
            await style_presets.update(request.match_info["preset_id"], await _json_body(request))
        )

    async def delete_style_preset(request: web.Request) -> web.Response:
        if not await style_presets.delete(request.match_info["preset_id"]):
            raise KeyError(request.match_info["preset_id"])
        return web.json_response({"deleted": True})

    async def list_prompt_rules(request: web.Request) -> web.Response:
        return web.json_response(prompt_rules.list(request.query.get("model_profile", "anima")))

    async def create_prompt_rule(request: web.Request) -> web.Response:
        body = await _json_body(request)
        profile = body.pop("model_profile", "anima")
        return web.json_response(
            await prompt_rules.create(body, model_profile=profile), status=201
        )

    async def update_prompt_rule(request: web.Request) -> web.Response:
        body = await _json_body(request)
        profile = body.pop("model_profile", "anima")
        return web.json_response(
            await prompt_rules.update(
                request.match_info["rule_id"], body, model_profile=profile
            )
        )

    async def delete_prompt_rule(request: web.Request) -> web.Response:
        rule_id = request.match_info["rule_id"]
        if not await prompt_rules.delete(rule_id, model_profile=request.query.get("model_profile", "anima")):
            raise KeyError(rule_id)
        return web.json_response({"deleted": True})

    async def normalize_prompts(request: web.Request) -> web.Response:
        body = await _json_body(request)
        unknown = set(body) - {"fields", "managedTriggers", "model_profile"}
        if unknown:
            raise WorkflowError(f"提示词规范请求包含未知参数: {', '.join(sorted(unknown))}")
        try:
            profile = normalize_model_profile(body.get("model_profile", "anima"))
        except ValueError as error:
            raise WorkflowError(str(error)) from error
        result = prompt_rules.normalize_fields(
            body.get("fields"), body.get("managedTriggers", []), model_profile=profile
        )
        result["model_profile"] = profile
        return web.json_response(result)

    async def validate_resource_ownership(settings: dict[str, Any]) -> None:
        """Reject stale or cross-profile model/LoRA selections at the API edge."""
        try:
            profile = normalize_model_profile(settings.get("model_profile", "anima"))
        except ValueError as error:
            raise WorkflowError(str(error)) from error
        # Submission must observe plugin edits/disconnection even when a list
        # request populated the short-lived display cache moments earlier.
        inventory = await client.resource_classifications("models", refresh=True)
        classifications = {item["name"]: item for item in inventory["items"]}
        model_names = list(classifications)
        model_owners = {name: item["model_profile"] for name, item in classifications.items()}
        model_name = settings.get("model_name") or model_profile_defaults(profile)["model_name"]
        settings["model_name"] = _resolve_owned_model(
            model_name, model_names, model_owners, profile, classifications
        )

        active = active_lora_pipelines(validate_settings(settings))
        stacks = []
        if "primary" in active:
            stacks.extend(settings.get("loras") or [])
        if "secondary" in active:
            stacks.extend((settings.get("single") or {}).get("loras_secondary") or [])
        lora_items = [item for item in stacks if isinstance(item, dict) and item.get("enabled")]
        if not lora_items:
            return
        lora_inventory = await client.resource_classifications("loras", refresh=True)
        available = {item["name"].casefold(): item for item in lora_inventory["items"]}
        for item in lora_items:
            name = normalize_lora_path(item.get("filename"))
            classified = available.get(name.casefold())
            if classified is None:
                raise WorkflowError(f"LoRA 不存在于当前 ComfyUI 清单: {name}")
            if classified.get("model_profile") != profile:
                reason = classified.get("classification_reason") or f"插件分类为 {classified.get('base_model') or 'Unknown'}"
                raise WorkflowError(f"LoRA 不适用于 {profile} 档案: {name}；{reason}，请在 LoRA Manager 中检查分类")

    async def hydrate_single_triggers(settings: dict[str, Any]) -> None:
        if settings.get("workbench") != "single":
            return
        single = settings.setdefault("single", {})
        primary = [item for item in settings.get("loras", []) if isinstance(item, dict) and item.get("enabled")]
        secondary = [item for item in single.get("loras_secondary", []) if isinstance(item, dict) and item.get("enabled")]
        active = active_lora_pipelines(validate_settings(settings))
        if not primary:
            settings["lora_managed_triggers"] = []
        if not secondary:
            single["secondary_managed_triggers"] = []
        if not (("primary" in active and primary) or ("secondary" in active and secondary)):
            return
        inventory = await client.resource_classifications("loras", refresh=True)
        lookup = {normalize_lora_path(item.get("filename") or item.get("name")).casefold(): item for item in inventory["items"]}
        profile = settings.get("model_profile", "anima")
        for name, selected, target, key in (("primary", primary, settings, "lora_managed_triggers"), ("secondary", secondary, single, "secondary_managed_triggers")):
            if name not in active:
                continue
            words = []
            for selection in selected:
                item = lookup.get(normalize_lora_path(selection.get("filename")).casefold())
                if item:
                    public = _public_lora_item(item, profile)
                    for word in public.get("trigger_words") or []:
                        if isinstance(word, str) and word.strip() and word.casefold() not in {value.casefold() for value in words}:
                            words.append(word.strip())
            target[key] = words

    async def start_batch(request: web.Request) -> web.Response:
        history.ensure_writable()
        body = await _json_body(request)
        seeds = body.pop("seeds", None)  # 复现历史图片时携带固定种子,不属于 settings
        if resource_paths is not None:
            body = {**startup_defaults, **body}
        body = generation_settings(body)
        # Validate the public shape before resource helpers traverse nested
        # fields. Keep the original payload so legacy migration semantics at
        # the runner boundary remain unchanged.
        validate_settings(body)
        await client.status()
        await validate_resource_ownership(body)
        await hydrate_single_triggers(body)
        state = await manager.start(body, seeds=seeds)
        return web.json_response(state, status=201)

    async def upload_input_image(request: web.Request) -> web.Response:
        if request.content_type != "multipart/form-data":
            raise WorkflowError("请以 multipart image 字段上传图片")
        if request.content_length is not None and request.content_length > request.client_max_size:
            raise web.HTTPRequestEntityTooLarge(max_size=request.client_max_size, actual_size=request.content_length)
        found = None
        try:
            stream = LimitedMultipartStream(request.content, request.client_max_size)
            reader = MultipartReader(request.headers, stream)
            async for part in reader:
                if not isinstance(part, BodyPartReader) or part.name != "image" or found is not None:
                    raise WorkflowError("上传请求只能包含一个 image 字段")
                data = bytearray()
                while chunk := await part.read_chunk():
                    if len(data) + len(chunk) > MAX_IMAGE_BYTES:
                        raise web.HTTPRequestEntityTooLarge(max_size=MAX_IMAGE_BYTES, actual_size=len(data) + len(chunk))
                    data.extend(chunk)
                found = (bytes(data), part.filename or "image")
            # MultipartReader may stop before the optional MIME epilogue.
            # Account for that tail too, including bodies without a length.
            while await stream.read(64 * 1024):
                pass
        except WorkflowError:
            raise
        except (ValueError, AssertionError, EOFError, RuntimeError, BadHttpMessage) as error:
            raise WorkflowError("上传 multipart 数据无效") from error
        if found is None:
            raise WorkflowError("缺少 image 文件")
        result = await asyncio.to_thread(input_images.add, found[0], filename=found[1])
        return web.json_response(result, status=201)

    async def input_image_from_history(request: web.Request) -> web.Response:
        body = await _json_body(request)
        image_id = body.get("image_id")
        if set(body) != {"image_id"} or type(image_id) is not int:
            raise WorkflowError("image_id 必须为有效作品 ID")
        image_id = _image_id(image_id)
        record = await history.get_image(image_id)
        content, _ = await client.image_bytes(record)
        result = await asyncio.to_thread(input_images.add, content, filename=record["filename"], source_image_id=image_id)
        return web.json_response(result, status=201)

    async def input_image_metadata(request: web.Request) -> web.Response:
        return web.json_response(await asyncio.to_thread(input_images.get, request.match_info["asset_id"]))

    async def input_image_content(request: web.Request) -> web.FileResponse:
        path = await asyncio.to_thread(input_images.image_path, request.match_info["asset_id"])
        return web.FileResponse(path, headers={"Content-Type": "image/png", "X-Content-Type-Options": "nosniff"})

    async def prompt_assistant_options(_: web.Request) -> web.Response:
        return web.json_response(await manager.prompt_assistant.options())

    async def create_prompt_job(request: web.Request) -> web.Response:
        body = await _json_body(request)
        if body.get("kind") == "rewrite":
            return web.json_response(await manager.start_rewrite(body), status=201)
        if set(body) - {"settings", "force"} or not isinstance(body.get("settings"), dict) or not isinstance(body.get("force", False), bool):
            raise WorkflowError("提示词预览需要 settings 对象及可选 force 布尔值")
        validate_settings(body["settings"])
        await hydrate_single_triggers(body["settings"])
        result = await manager.start_prompt_preview(body["settings"], force=body.get("force", False))
        return web.json_response(result, status=201)

    async def get_prompt_job(request: web.Request) -> web.Response:
        return web.json_response(await manager.prompt_jobs.get(request.match_info["job_id"]))

    async def cancel_prompt_job(request: web.Request) -> web.Response:
        return web.json_response(await manager.cancel_prompt_preview(request.match_info["job_id"]))

    async def current_batch(_: web.Request) -> web.Response:
        return web.json_response({"batch": manager.snapshot(), "queue": manager.queue_snapshot()})

    async def stop_batch(request: web.Request) -> web.Response:
        state = await manager.request_stop(
            request.match_info["batch_id"],
            clear_queue=_query_bool(request, "clearQueue", True),
        )
        return web.json_response({"batch": state, "queue": manager.queue_snapshot()})

    async def remove_queued_batch(request: web.Request) -> web.Response:
        entry = next((item for item in manager.queue if item["queue_id"] == request.match_info["queue_id"]), None)
        if entry and entry.get("kind") == "prompt_preview":
            await manager.cancel_prompt_preview(entry["job_id"])
            return web.json_response({"queue": manager.queue_snapshot()})
        if not manager.remove_queued(request.match_info["queue_id"]):
            raise KeyError(request.match_info["queue_id"])
        return web.json_response({"queue": manager.queue_snapshot()})

    async def batch_preview(request: web.Request) -> web.Response:
        preview = manager.preview
        owner = manager.preview_metadata
        headers = {"Cache-Control": "no-store", "X-Content-Type-Options": "nosniff"}
        if preview is None:
            return web.Response(status=204, headers=headers)
        for key in ("batch_id", "prompt_id", "node_id"):
            if key in request.query and (owner is None or request.query[key] != owner[key]):
                return web.Response(status=204, headers=headers)
        if "epoch" in request.query and (owner is None or request.query["epoch"] != str(owner.get("epoch"))):
            return web.Response(status=204, headers=headers)
        if owner is not None:
            state = manager.state or {}
            if (owner["batch_id"] != state.get("id") or owner["prompt_id"] != state.get("prompt_id")
                    or owner["epoch"] != state.get("preview_epoch")
                    or state.get("stage") != "generating" or state.get("status") not in ("running", "stopping")):
                return web.Response(status=204, headers=headers)
            headers.update({"X-Preview-Batch-Id": owner["batch_id"], "X-Preview-Prompt-Id": owner["prompt_id"],
                            "X-Preview-Node-Id": owner["node_id"], "X-Preview-Epoch": str(owner["epoch"])})
        seq, content_type, body = preview
        headers["X-Preview-Id"] = str(seq)
        return web.Response(body=body, content_type=content_type, headers=headers)

    async def list_history(request: web.Request) -> web.Response:
        try:
            page = int(request.query.get("page", "1"))
            limit = int(request.query.get("limit", "24"))
        except ValueError as error:
            raise WorkflowError("分页参数无效") from error
        if max(0, page - 1) * min(60, max(1, limit)) > SQLITE_MAX_INTEGER:
            raise WorkflowError("分页参数超出有效范围")
        before_id = None
        if "before_id" in request.query:
            raw_before_id = request.query["before_id"]
            if not raw_before_id.isascii() or not raw_before_id.isdecimal():
                raise WorkflowError("before_id 必须为正整数图片 ID")
            try:
                before_id = int(raw_before_id)
            except ValueError as error:
                raise WorkflowError("before_id 必须为正整数图片 ID") from error
            if not 1 <= before_id <= 2**63 - 1:
                raise WorkflowError("before_id 必须为正整数图片 ID")
        workbench = request.query.get("workbench") or None
        if workbench not in {None, "random", "single"}:
            raise WorkflowError("workbench 必须为 random 或 single")
        return web.json_response({
            **await history.list_images(page, limit, workbench, before_id=before_id),
            "available": history.available,
        })

    async def _regeneration_context(image_id: int) -> tuple[dict[str, Any], dict[str, Any]]:
        record = await history.get_image(image_id)
        settings = dict(record.get("settings") or {})
        positive = ""
        for candidate in (record.get("positive_prompt"), record.get("resolved_prompt")):
            text = str(candidate or "")
            if text.strip():
                positive = text
                break
        negative = ""
        for candidate in (record.get("negative_prompt"), settings.get("negative_prompt")):
            text = str(candidate or "")
            if text.strip():
                negative = text
                break
        selected = record.get("resolved_selection") or {}
        if not isinstance(selected, dict):
            selected = {}
        restored, preserve_issues = resolve_history_selection(selected, settings, catalog, positive)
        for section, items in restored.items():
            for item in items:
                saved_prompt = str(item.get("prompt") or "")
                normalized, _ = prompt_rules.normalize_text(
                    saved_prompt, "positive", protected_lora_words=settings.get("lora_managed_triggers", []),
                    model_profile=settings.get("model_profile", "anima"),
                )
                if normalized != saved_prompt:
                    preserve_issues[section] = "当前提示词规则会改变历史快照，请同时重抽此维度，或使用同提示词变体"
                    break
        resource_issues = []
        try:
            await validate_resource_ownership(settings)
        except (WorkflowError, ComfyError) as error:
            resource_issues.append({"type": "classification", "name": str(error), "label": "模型分类"})
        try:
            resource_issues.extend(await manager.resource_issues(settings))
        except ComfyError as error:
            if not resource_issues:
                resource_issues.append({"type": "classification", "name": str(error), "label": "模型分类"})
        sections: dict[str, Any] = {}
        for section in SECTIONS:
            random_enabled = bool(settings.get(f"random_{section}"))
            actual = selected.get(section) if isinstance(selected.get(section), list) else []
            sections[section] = {
                "random": random_enabled,
                "hasSnapshot": bool(actual),
                "canRedraw": random_enabled,
                "canPreserve": section not in preserve_issues,
                "preserveReason": preserve_issues.get(section, ""),
            }
        warnings = [
            "资源文件内容已被覆盖时无法识别，原种子重跑不承诺像素级一致。"
        ]
        if not positive:
            warnings.append("历史记录缺少最终正向提示词，无法可靠回放。")
        context = {
            "positive": positive,
            "negative": negative,
            "selected": selected,
            "restored": restored,
            "resourceIssues": resource_issues,
            "sections": sections,
            "warnings": warnings,
        }
        return record, context

    async def regeneration_options(request: web.Request) -> web.Response:
        _record, context = await _regeneration_context(
            _image_id(request.match_info["image_id"])
        )
        resources_ok = not context["resourceIssues"] and not history.read_only_reason
        positive_ok = bool(context["positive"])
        random_sections = [
            section for section, value in context["sections"].items() if value["random"]
        ]
        return web.json_response(
            {
                "modes": {
                    "replay": {
                        "available": resources_ok and positive_ok,
                        "reasons": _regeneration_reasons(context, require_prompt=True),
                    },
                    "prompt_variant": {
                        "available": resources_ok and positive_ok,
                        "reasons": _regeneration_reasons(context, require_prompt=True),
                    },
                    "content_redraw": {
                        "available": resources_ok and bool(random_sections),
                        "reasons": _regeneration_reasons(
                            context, require_random=bool(random_sections)
                        ),
                    },
                    "settings_reroll": {
                        "available": resources_ok,
                        "reasons": _regeneration_reasons(context),
                    },
                },
                "resourceIssues": context["resourceIssues"],
                "warnings": context["warnings"],
                "sections": context["sections"],
            }
        )

    def _regeneration_reasons(
        context: dict[str, Any],
        *,
        require_prompt: bool = False,
        require_random: bool = True,
    ) -> list[str]:
        reasons = [
            f"{item['label']} 不可用: {item['name']}"
            for item in context["resourceIssues"]
        ]
        if history.read_only_reason:
            reasons.append(history.read_only_reason)
        if require_prompt and not context["positive"]:
            reasons.append("历史记录缺少最终正向提示词")
        if require_random is False:
            reasons.append("历史设置没有可重新抽取的随机维度")
        return reasons

    async def regenerate_history(request: web.Request) -> web.Response:
        history.ensure_writable()
        image_id = _image_id(request.match_info["image_id"])
        await client.status()
        record, context = await _regeneration_context(
            image_id
        )
        body = await _json_body(request)
        unknown = set(body) - {"mode", "count", "sections"}
        if unknown:
            raise WorkflowError(
                f"再生成请求包含未知参数: {', '.join(sorted(unknown))}"
            )
        mode = body.get("mode")
        if not isinstance(mode, str) or mode not in {"replay", "prompt_variant", "content_redraw", "settings_reroll"}:
            raise WorkflowError("mode 无效")
        if context["resourceIssues"]:
            missing = "；".join(
                f"{item['label']}: {item['name']}" for item in context["resourceIssues"]
            )
            raise WorkflowError(f"历史资源不可用: {missing}")
        settings = dict(record.get("settings") or {})
        if mode == "replay":
            if set(body) - {"mode"}:
                raise WorkflowError("replay 不接受 count 或 sections")
            if not context["positive"]:
                raise WorkflowError("历史记录缺少可回放的最终正向提示词")
            settings["count"] = 1
            regeneration = {
                "mode": mode,
                "frozen_positive_prompt": context["positive"],
                "frozen_negative_prompt": context["negative"],
                "fixed_selection": context["selected"],
            }
            seeds = {
                "sample_seed": record["sample_seed"],
                "prompt_seed": record["prompt_seed"],
            }
        else:
            count = body.get("count", 4)
            if isinstance(count, bool) or not isinstance(count, int) or not 1 <= count <= 100:
                raise WorkflowError("count 必须是 1-100 之间的整数")
            settings["count"] = count
            seeds = None
            if mode == "prompt_variant":
                if "sections" in body:
                    raise WorkflowError("prompt_variant 不接受 sections")
                if not context["positive"]:
                    raise WorkflowError("历史记录缺少可回放的最终正向提示词")
                regeneration = {
                    "mode": mode,
                    "frozen_positive_prompt": context["positive"],
                    "frozen_negative_prompt": context["negative"],
                    "fixed_selection": context["selected"],
                    "prompt_seed": record["prompt_seed"],
                }
            elif mode == "settings_reroll":
                if "sections" in body:
                    raise WorkflowError("settings_reroll 不接受 sections")
                regeneration = {"mode": mode}
            else:
                sections = body.get("sections")
                if not isinstance(sections, list) or not sections:
                    raise WorkflowError("content_redraw 至少选择一个重抽维度")
                if any(not isinstance(section, str) for section in sections):
                    raise WorkflowError("sections 必须是字符串数组")
                if len(sections) != len(set(sections)):
                    raise WorkflowError("重抽维度不能重复")
                invalid = set(sections) - set(SECTIONS)
                if invalid:
                    raise WorkflowError(f"不支持的重抽维度: {', '.join(sorted(invalid))}")
                not_random = [
                    section for section in sections if not context["sections"][section]["random"]
                ]
                if not_random:
                    raise WorkflowError(
                        f"这些维度在历史设置中不是随机池: {', '.join(not_random)}"
                    )
                missing_fixed = [
                    section
                    for section, value in context["sections"].items()
                    if value["random"] and section not in sections and not value["canPreserve"]
                ]
                if missing_fixed:
                    raise WorkflowError(
                        "无法可靠保留历史内容，请同时重抽: " + "; ".join(
                            f"{section}: {context['sections'][section]['preserveReason']}" for section in missing_fixed
                        )
                    )
                fixed_selection = {
                    section: context["restored"].get(section, [])
                    for section, value in context["sections"].items()
                    if value["random"] and section not in sections
                }
                regeneration = {
                    "mode": mode,
                    "redraw_sections": sections,
                    "fixed_selection": fixed_selection,
                    "original_selection": context["selected"],
                }
        await validate_resource_ownership(settings)
        if settings.get("workbench") == "single" and mode in {"replay", "prompt_variant"}:
            runtime = record.get("single_runtime") or {}
            if not runtime.get("effective_positive"):
                runtime = {"positive": context["positive"], "effective_positive": {"primary": context["positive"], "secondary": context["positive"]}, "negative": context["negative"]}
            regeneration["single_runtime"] = runtime
        state = await manager.start(settings, seeds=seeds, regeneration=regeneration)
        return web.json_response(state, status=201)

    async def delete_history(request: web.Request) -> web.Response:
        image_id = _image_id(request.match_info["image_id"])
        if not await history.delete_image(image_id):
            raise KeyError(image_id)
        return web.json_response({"deleted": True})

    async def image(request: web.Request) -> web.Response:
        record = await history.get_image(_image_id(request.match_info["image_id"]))
        body, content_type = await client.image_bytes(record)
        return web.Response(body=body, content_type=content_type.split(";", 1)[0])

    async def index(_: web.Request) -> web.FileResponse:
        return web.FileResponse(root / "static" / "index.html")

    async def favicon(_: web.Request) -> web.Response:
        return web.Response(status=204)

    async def launcher_health(_: web.Request) -> web.Response:
        return web.json_response({
            "app": "anima-random-studio",
            "app_dir": str(root.resolve()),
            "data_dir": str(storage.resolve()),
            "comfy_url": getattr(client, "base_url", "local"),
        })

    async def launcher_shutdown(request: web.Request) -> web.Response:
        nonlocal shutdown_exit_handle
        # 未配置 token 时端点不存在,旧版本天然返回 404 —— 启动器据此探测能力。
        if shutdown_token is None:
            raise web.HTTPNotFound()
        body = await _json_body(request)
        candidate = body.get("token")
        if not isinstance(candidate, str) or not hmac.compare_digest(
            candidate.encode("utf-8", errors="surrogatepass"),
            shutdown_token.encode("utf-8", errors="surrogatepass"),
        ):
            raise web.HTTPForbidden()
        # Check idleness under the same lock as start; a launcher-side probe
        # alone races with requests that are still validating resources.
        await manager.begin_idle_shutdown()
        try:
            await cleanup(app)
        except Exception:
            if shutdown_exit_handle is None:
                shutdown_exit_handle = asyncio.get_running_loop().call_later(0.5, os._exit, 1)
            raise
        # Flush the response after all owned resources have been released.
        # Repeated authenticated requests share cleanup and schedule one exit.
        if shutdown_exit_handle is None:
            shutdown_exit_handle = asyncio.get_running_loop().call_later(0.5, os._exit, 0)
        return web.json_response({"stopping": True})

    app.router.add_get("/api/launcher-health", launcher_health)
    app.router.add_post("/api/launcher-shutdown", launcher_shutdown)
    app.router.add_get("/api/config", config)
    app.router.add_get("/api/status", status)
    app.router.add_get("/api/loras", loras)
    app.router.add_get("/api/loras/preview", lora_preview)
    app.router.add_put("/api/loras/triggers", update_lora_triggers)
    app.router.add_delete("/api/loras/triggers", reset_lora_triggers)
    app.router.add_get("/api/resources", resources)
    app.router.add_get("/api/resource-ownership", resource_ownership)
    app.router.add_put("/api/resource-ownership", update_resource_ownership)
    app.router.add_get("/api/prompt-pools", list_prompt_pool)
    app.router.add_post("/api/prompt-pools/import/preview", preview_prompt_pool_import)
    app.router.add_post("/api/prompt-pools/import", import_prompt_pool)
    app.router.add_post("/api/prompt-pools/items", create_prompt_pool_item)
    app.router.add_post("/api/prompt-pools/items/ensure", ensure_prompt_pool_item)
    app.router.add_put("/api/prompt-pools/items", update_prompt_pool_item)
    app.router.add_post("/api/prompt-pools/items/delete", delete_prompt_pool_items)
    app.router.add_delete("/api/prompt-pools/items", delete_prompt_pool_item)
    app.router.add_get("/api/style-presets", list_style_presets)
    app.router.add_post("/api/style-presets", create_style_preset)
    app.router.add_put("/api/style-presets/{preset_id}", update_style_preset)
    app.router.add_delete("/api/style-presets/{preset_id}", delete_style_preset)
    app.router.add_get("/api/prompt-rules", list_prompt_rules)
    app.router.add_post("/api/prompt-rules", create_prompt_rule)
    app.router.add_put("/api/prompt-rules/{rule_id}", update_prompt_rule)
    app.router.add_delete("/api/prompt-rules/{rule_id}", delete_prompt_rule)
    app.router.add_post("/api/prompts/normalize", normalize_prompts)
    app.router.add_get("/api/favorites/{section}", get_favorites)
    app.router.add_put("/api/favorites/{section}/item", update_favorite)
    app.router.add_post("/api/favorites/{section}/text", save_favorite_text)
    app.router.add_post("/api/favorites/{section}/selection", update_favorite_selection)
    app.router.add_post("/api/favorites/{section}/groups", create_favorite_group)
    app.router.add_put("/api/favorites/{section}/groups/{group_id}", update_favorite_group)
    app.router.add_post(
        "/api/favorites/{section}/groups/{parent_id}/children/import",
        import_favorite_child_group,
    )
    app.router.add_delete("/api/favorites/{section}/groups/{group_id}", delete_favorite_group)
    app.router.add_get("/api/pools/{section}", pool)
    app.router.add_post("/api/pools/{section}/query", pool_query)
    app.router.add_get("/api/custom-prompts", list_custom_prompts)
    app.router.add_post("/api/custom-prompts", create_custom_prompt)
    app.router.add_put("/api/custom-prompts/{item_id}", update_custom_prompt)
    app.router.add_delete("/api/custom-prompts/{item_id}", delete_custom_prompt)
    app.router.add_get("/api/custom-prompts/templates/{section}/{format}", custom_template)
    app.router.add_post("/api/custom-prompts/import/preview", preview_custom_import)
    app.router.add_post("/api/custom-prompts/import", commit_custom_import)
    app.router.add_get("/api/custom-groups/{section}", list_custom_groups)
    app.router.add_post("/api/custom-groups/{section}", create_custom_group)
    app.router.add_put("/api/custom-groups/{section}/{group_id}", update_custom_group)
    app.router.add_delete("/api/custom-groups/{section}/{group_id}", delete_custom_group)
    app.router.add_post("/api/batches", start_batch)
    app.router.add_post("/api/input-images", upload_input_image)
    app.router.add_post("/api/input-images/from-history", input_image_from_history)
    app.router.add_get("/api/input-images/{asset_id}/image", input_image_content)
    app.router.add_get("/api/input-images/{asset_id}", input_image_metadata)
    app.router.add_get("/api/prompt-assistant/options", prompt_assistant_options)
    app.router.add_post("/api/prompt-jobs", create_prompt_job)
    app.router.add_get("/api/prompt-jobs/{job_id}", get_prompt_job)
    app.router.add_post("/api/prompt-jobs/{job_id}/cancel", cancel_prompt_job)
    app.router.add_get("/api/batches/current", current_batch)
    app.router.add_get("/api/batches/current/preview", batch_preview)
    app.router.add_post("/api/batches/{batch_id}/stop", stop_batch)
    app.router.add_delete("/api/batches/queue/{queue_id}", remove_queued_batch)
    app.router.add_get("/api/history", list_history)
    app.router.add_get(
        r"/api/history/{image_id:\d+}/regeneration-options",
        regeneration_options,
    )
    app.router.add_post(
        r"/api/history/{image_id:\d+}/regenerate", regenerate_history
    )
    app.router.add_delete(r"/api/history/{image_id:\d+}", delete_history)
    app.router.add_get(r"/api/images/{image_id:\d+}", image)
    app.router.add_get("/", index)
    app.router.add_get("/favicon.ico", favicon)
    app.router.add_static("/static/", root / "static", show_index=False)

    async def cleanup_resources() -> None:
        try:
            await manager.shutdown()
        finally:
            try:
                close = getattr(client, "close", None)
                if close:
                    result = close()
                    if asyncio.iscoroutine(result):
                        await result
            finally:
                await history.close()

    async def cleanup(_: web.Application) -> None:
        nonlocal cleanup_task
        if cleanup_task is None:
            cleanup_task = asyncio.create_task(cleanup_resources())
        # A disconnected shutdown caller must not cancel resource cleanup.
        await asyncio.shield(cleanup_task)

    app.on_cleanup.append(cleanup)
    return app


def _setup_logging(root: Path, data_dir: Path | None = None) -> None:
    handlers: list[logging.Handler] = [logging.StreamHandler()]
    try:
        directory = data_dir if data_dir is not None else root / "data"
        directory.mkdir(parents=True, exist_ok=True)
        from logging.handlers import RotatingFileHandler
        handlers.append(RotatingFileHandler(directory / "webui.log", maxBytes=5 * 1024 * 1024, backupCount=3, encoding="utf-8"))
    except OSError:
        pass
    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s %(levelname)s %(name)s %(message)s",
        handlers=handlers,
    )


def main() -> None:
    parser = argparse.ArgumentParser(description="Anima Random WebUI")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8190)
    parser.add_argument("--comfy-url", default="http://127.0.0.1:8188")
    parser.add_argument("--anima-tools-dir", default=None)
    parser.add_argument("--data-dir", default=None, help="用户数据目录；默认仍为项目 data")
    parser.add_argument("--resource-paths", default=None, help="启动器解析的模型相对路径映射 JSON")
    parser.add_argument(
        "--trusted-host",
        action="append",
        default=[],
        help="额外允许的反向代理主机名；可重复指定",
    )
    parser.add_argument("--no-browser", action="store_true")
    parser.add_argument("--shutdown-token", default=None, help="启动器下发的停机令牌;未提供则关闭停机端点")
    args = parser.parse_args()
    if args.host not in {"127.0.0.1", "localhost", "::1"}:
        raise SystemExit("WebUI 只允许监听本机地址")
    _setup_logging(APP_DIR, Path(args.data_dir) if args.data_dir else None)
    app = create_app(
        comfy=ComfyClient(args.comfy_url),
        anima_tools_dir=args.anima_tools_dir,
        trusted_hostnames=set(args.trusted_host),
        data_dir=args.data_dir,
        resource_paths=json.loads(Path(args.resource_paths).read_text(encoding="utf-8")) if args.resource_paths else None,
        shutdown_token=args.shutdown_token,
    )
    if not args.no_browser:
        threading.Timer(0.8, lambda: webbrowser.open(f"http://127.0.0.1:{args.port}")).start()
    web.run_app(app, host=args.host, port=args.port, print=lambda message: print(message, flush=True))
