from __future__ import annotations

import asyncio
import copy
import json
import os
import tempfile
import uuid
from pathlib import Path
from typing import Any

from .async_utils import serialized_mutation
from .catalog import SECTIONS, CatalogError, PromptCatalog, normalize_text
from .comfy import ComfyError
from .model_profiles import normalize_model_profile
from .persistence import StorageProtection, StorageReadOnlyError, backup_for_migration


MAX_GROUPS = 128
MAX_ITEMS = 50000
FAVORITE_SECTIONS = (*SECTIONS, "artist", "prompt")


class FavoriteSavePendingError(RuntimeError):
    """The source word is durable, but its favorite write needs a retry."""

    def __init__(self, item_id: str, error: Exception):
        self.item_id = item_id
        super().__init__(f"提示词已保存到自定义池，但收藏尚未确认，请重试：{error}")


def _section(value: str) -> str:
    if value not in FAVORITE_SECTIONS:
        raise CatalogError(f"不支持的收藏分类: {value}")
    return value


def _repair_group_hierarchy(groups: list[dict[str, Any]]) -> None:
    by_id = {group["id"]: group for group in groups}
    for group in groups:
        parent_id = group.get("parentId")
        if group["id"] == "default" or parent_id not in by_id or parent_id == group["id"]:
            group["parentId"] = None

    for group in groups:
        seen: set[str] = set()
        current = group
        while current.get("parentId"):
            if current["id"] in seen:
                group["parentId"] = None
                break
            seen.add(current["id"])
            parent = by_id.get(current["parentId"])
            if parent is None:
                group["parentId"] = None
                break
            current = parent


def normalize_section(value: Any) -> dict[str, list[dict[str, Any]]]:
    value = value if isinstance(value, dict) else {}
    groups = value.get("groups") if isinstance(value.get("groups"), list) else []
    items = value.get("items") if isinstance(value.get("items"), list) else []
    normalized_groups: list[dict[str, Any]] = []
    seen_groups: set[str] = set()
    for raw in groups[:MAX_GROUPS]:
        if not isinstance(raw, dict):
            continue
        group_id = str(raw.get("id") or "").strip()
        name = str(raw.get("name") or "").strip()
        if not group_id or not name or group_id in seen_groups:
            continue
        seen_groups.add(group_id)
        parent_id = str(raw.get("parentId") or "").strip() or None
        source_id = str(raw.get("sourceCustomGroupId") or "").strip() or None
        normalized_groups.append(
            {
                "id": group_id,
                "name": name[:100],
                "isSystem": group_id == "default",
                "parentId": parent_id,
                "sourceCustomGroupId": source_id,
            }
        )
    if "default" not in seen_groups:
        normalized_groups.insert(
            0,
            {
                "id": "default",
                "name": "Default Favorites",
                "isSystem": True,
                "parentId": None,
                "sourceCustomGroupId": None,
            },
        )
    _repair_group_hierarchy(normalized_groups)
    valid_group_ids = {group["id"] for group in normalized_groups}
    normalized_items: list[dict[str, Any]] = []
    for raw in items[:MAX_ITEMS]:
        if not isinstance(raw, dict):
            continue
        item = copy.deepcopy(raw)
        group_ids = item.get("groupIds") if isinstance(item.get("groupIds"), list) else []
        item["groupIds"] = list(
            dict.fromkeys(str(group_id) for group_id in group_ids if str(group_id) in valid_group_ids)
        )
        normalized_items.append(item)
    return {"groups": normalized_groups, "items": normalized_items}


def favorite_key(section: str, item: dict[str, Any]) -> str:
    if section in {"character", "artist"}:
        return str(item.get("name") or item.get("id") or "")
    return str(item.get("id") or item.get("name") or "")


def _children_by_parent(groups: list[dict[str, Any]]) -> dict[str | None, list[dict[str, Any]]]:
    result: dict[str | None, list[dict[str, Any]]] = {}
    for group in groups:
        result.setdefault(group.get("parentId"), []).append(group)
    return result


def _descendant_ids(groups: list[dict[str, Any]], group_id: str) -> set[str]:
    children = _children_by_parent(groups)
    result: set[str] = set()
    pending = [group_id]
    while pending:
        current = pending.pop()
        if current in result:
            continue
        result.add(current)
        pending.extend(child["id"] for child in children.get(current, []))
    return result


def _group_stats(current: dict[str, list[dict[str, Any]]]) -> list[dict[str, Any]]:
    groups = current["groups"]
    items = current["items"]
    children = _children_by_parent(groups)
    valid_ids = {group["id"] for group in groups}
    result: list[dict[str, Any]] = []
    for group in groups:
        group_id = group["id"]
        descendants = _descendant_ids(groups, group_id)
        direct_items = [item for item in items if group_id in (item.get("groupIds") or [])]
        result.append(
            {
                **group,
                "directCount": len(direct_items),
                "totalCount": sum(
                    bool(descendants.intersection(item.get("groupIds") or [])) for item in items
                ),
                "childCount": len(children.get(group_id, [])),
                "exclusiveCount": sum(
                    not ({str(value) for value in item.get("groupIds") or []} & valid_ids - {group_id})
                    for item in direct_items
                ),
            }
        )
    return result


_locked = serialized_mutation


class FavoritesExtensionStore(StorageProtection):
    """Persist categories which the upstream Anima Tools whitelist drops.

    Existing plugin categories remain in their original store. This supplement
    uses the same section shape, so both workbenches use one FavoritesService.
    """

    store_name = "favorite_extensions"

    def __init__(self, path: str | Path):
        self.path = Path(path)
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.load_warnings: list[str] = []
        self.read_only_reason = None
        self._lock = asyncio.Lock()
        self.profiles: dict[str, dict[str, Any]] = {}
        if self.path.is_file():
            try:
                payload = json.loads(self.path.read_text(encoding="utf-8"))
                if not isinstance(payload, dict) or payload.get("version") not in (1, 2):
                    raise ValueError("收藏补充文件版本无效")
                legacy = payload["version"] == 1
                profiles = {"anima": {"expression": payload.get("expression")}} if legacy else payload.get("profiles")
                if not isinstance(profiles, dict):
                    raise ValueError("收藏档案格式无效")
                normalized: dict[str, dict[str, Any]] = {}
                for profile, sections in profiles.items():
                    profile = normalize_model_profile(profile)
                    if not isinstance(sections, dict):
                        raise ValueError("收藏分类格式无效")
                    normalized[profile] = {}
                    for section, value in sections.items():
                        if section not in ({"expression"} if profile == "anima" else {"prompt"}):
                            raise ValueError("收藏档案包含不支持的分类")
                        if not isinstance(value, dict) or not isinstance(value.get("items"), list) or not isinstance(value.get("groups"), list):
                            raise ValueError("收藏分类格式无效")
                        normalized[profile][section] = normalize_section(value)
                self.profiles = normalized
            except (OSError, UnicodeError, ValueError) as error:
                self._recover_load_failure(error)
                return
            if legacy:
                try:
                    backup = backup_for_migration(self.path, 2)
                    self._write_text(self._serialize(self.profiles))
                    if backup:
                        self.load_warnings.append(f"收藏补充文件已迁移，原文件备份为 {backup.name}")
                except OSError as error:
                    self._protect_storage(f"收藏已在内存迁移，但备份或写回失败({error})")

    @property
    def value(self) -> dict[str, Any]:
        """Compatibility view for callers of the former expression-only store."""
        return self.get("anima", "expression")

    def get(self, model_profile: str, section: str) -> dict[str, Any]:
        return copy.deepcopy(self.profiles.get(model_profile, {}).get(section, normalize_section({})))

    @staticmethod
    def _serialize(profiles: dict[str, Any]) -> str:
        return json.dumps({"version": 2, "profiles": profiles}, ensure_ascii=False, indent=2) + "\n"

    @serialized_mutation
    async def save(self, value: dict[str, Any], model_profile: str = "anima", section: str = "expression") -> None:
        self.ensure_writable()
        profiles = copy.deepcopy(self.profiles)
        profiles.setdefault(model_profile, {})[section] = normalize_section(value)
        try:
            await asyncio.to_thread(self._write_text, self._serialize(profiles))
        except OSError as error:
            self._protect_storage(f"收藏写入失败({error})，已保留原内容")
            self.ensure_writable()
        self.profiles = profiles

    def _write_text(self, text: str) -> None:
        fd, name = tempfile.mkstemp(prefix="favorites-", suffix=".json", dir=self.path.parent)
        try:
            with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as handle:
                handle.write(text)
                handle.flush()
                os.fsync(handle.fileno())
            os.replace(name, self.path)
        finally:
            if os.path.exists(name):
                os.unlink(name)


class FavoritesService:
    def __init__(self, comfy: Any, catalog: PromptCatalog, expression_store: FavoritesExtensionStore | None = None, model_profile: str = "anima"):
        self.comfy = comfy
        self.catalog = catalog
        self.expression_store = expression_store
        self.model_profile = normalize_model_profile(model_profile)
        # 所有变更都是「读 ComfyUI 全量 → 内存改 → 整体写回」,
        # 两次操作并发时后写者会覆盖前写者;用锁把读-改-写串行化。
        self._lock = asyncio.Lock()

    def _section(self, section: str) -> str:
        section = _section(section)
        if (self.model_profile == "krea2") != (section == "prompt"):
            raise CatalogError(f"{self.model_profile} 不支持收藏分类 {section}")
        return section

    async def _read_payload(self, section: str) -> dict[str, Any]:
        if section in {"expression", "prompt"} and self.expression_store is not None:
            return {section: self.expression_store.get(self.model_profile, section)}
        if section == "prompt":
            raise CatalogError("自然语言收藏存储不可用")
        return await self.comfy.favorites()

    async def _save_section(self, section: str, value: dict[str, Any]) -> None:
        if section in {"expression", "prompt"} and self.expression_store is not None:
            await self.expression_store.save(value, self.model_profile, section)
        else:
            await self.comfy.save_favorites({section: value})

    async def get(self, section: str) -> dict[str, Any]:
        section = self._section(section)
        payload = await self._read_payload(section)
        result = normalize_section(payload.get(section))
        return {
            **result,
            "model_profile": self.model_profile,
            "groups": _group_stats(result),
            "favorite_keys": sorted(
                favorite_key(section, item) for item in result["items"] if item.get("groupIds")
            ),
        }

    async def collection_group_ids(self, section: str, group_id: str) -> set[str]:
        section = self._section(section)
        payload = await self._read_payload(section)
        current = normalize_section(payload.get(section))
        if not any(group["id"] == group_id for group in current["groups"]):
            return set()
        return _descendant_ids(current["groups"], group_id)

    @_locked
    async def update_item(self, section: str, payload: dict[str, Any]) -> dict[str, Any]:
        section = self._section(section)
        if section == "artist":
            return await self._update_artist(payload)
        if section == "prompt":
            return await self._update_prompt_item(payload)
        item_id = str(payload.get("id") or "").strip()
        catalog_item = self.catalog.get(item_id)
        if catalog_item is None or catalog_item.get("section") != section:
            raise CatalogError("收藏条目不存在")
        data = await self._read_payload(section)
        current = normalize_section(data.get(section))
        key = str(catalog_item.get("favorite_key") or "")
        position = next(
            (index for index, item in enumerate(current["items"]) if favorite_key(section, item) == key),
            None,
        )
        existing = current["items"][position] if position is not None else None
        old_group_ids = list(existing.get("groupIds") or []) if existing else []
        enabled = payload.get("favorite", True)
        if not isinstance(enabled, bool):
            raise CatalogError("favorite 必须是布尔值")
        if "groupIds" not in payload and existing is not None:
            # 与 nickname 同理:载荷未携带 groupIds 的重复收藏(如前端 toggleFavorite)
            # 保留原分组,重新校验以过滤期间被删除的组。
            groups = self._validated_group_ids(current, old_group_ids, enabled)
        else:
            groups = self._validated_group_ids(current, payload.get("groupIds"), enabled)
        nickname = str(payload.get("nickname") or "").strip()[:300]
        if not enabled:
            if position is not None:
                current["items"].pop(position)
        else:
            saved = self._favorite_record(section, catalog_item, groups, nickname)
            if position is None:
                current["items"].append(saved)
            else:
                if "nickname" not in payload:
                    saved["nickname"] = str(existing.get("nickname") or "")
                current["items"][position] = saved
        self._prune_empty_affected_children(current, old_group_ids)
        await self._save_section(section, current)
        return await self.get(section)

    @_locked
    async def save_text(self, section: str, payload: dict[str, Any], custom_prompts: Any = None, prompt_pools: Any = None) -> dict[str, Any]:
        """Save one text selection; repeat requests never create duplicate words."""
        section = self._section(section)
        if section == "artist":
            raise CatalogError("请将文本收藏到角色、服装、姿势、背景或表情分类")
        if set(payload) - {"title", "subtitle", "text", "groupIds"}:
            raise CatalogError("文本收藏包含未知字段")
        title = payload.get("title")
        subtitle = payload.get("subtitle", "")
        text = payload.get("text")
        if not isinstance(title, str) or not title.strip() or len(title.strip()) > 200:
            raise CatalogError("收藏名称需要 1-200 个字符")
        if not isinstance(text, str) or not text.strip() or len(text.strip()) > 20000:
            raise CatalogError("收藏提示词需要 1-20000 个字符")
        if not isinstance(subtitle, str) or len(subtitle.strip()) > 300:
            raise CatalogError("收藏副标题不能超过 300 个字符")
        title, subtitle, text = title.strip(), subtitle.strip(), text.strip()
        data = await self._read_payload(section)
        current = normalize_section(data.get(section))
        raw_groups = payload.get("groupIds", ["default"])
        if not isinstance(raw_groups, list) or any(not isinstance(value, str) for value in raw_groups):
            raise CatalogError("groupIds 必须是字符串数组")
        groups = list(dict.fromkeys(raw_groups)) or ["default"]
        valid_groups = {group["id"] for group in current["groups"]}
        if any(group not in valid_groups for group in groups):
            raise CatalogError("收藏分组不存在，请刷新后重试")
        if section in {"expression", "prompt"} and self.expression_store is not None:
            self.expression_store.ensure_writable()

        created = False
        if section == "prompt":
            if prompt_pools is None:
                raise CatalogError("自然语言提示词池不可用")
            existing = next(
                (item for item in current["items"] if str(item.get("text", item.get("customContent", ""))).strip().casefold() == text.casefold()),
                None,
            )
            if existing is None:
                if len(current["items"]) >= MAX_ITEMS:
                    raise CatalogError("收藏条目数量已达到上限")
            source = await prompt_pools.ensure_item(self.model_profile, text, title=title, subtitle=subtitle)
            if existing is None:
                item_id = f"favorite_{uuid.uuid4().hex}"
                existing = {"id": item_id, "name": item_id, "isCustom": True, "groupIds": []}
                existing.update(title=title, nickname=title, subtitle=subtitle, text=text, customContent=text)
                current["items"].append(existing)
                created = True
            item_id = existing["id"]
            existing["groupIds"] = list(dict.fromkeys([*existing["groupIds"], *groups]))
            try:
                await self._save_section(section, current)
                result = await self.get(section)
            except (ComfyError, StorageReadOnlyError, OSError, TimeoutError) as error:
                raise FavoriteSavePendingError(source["id"], error) from error
            return {**result, "item_id": item_id, "source_item_id": source["id"], "created": created}
        else:
            if custom_prompts is None:
                raise CatalogError("自定义提示词存储不可用")
            if len(current["items"]) >= MAX_ITEMS:
                source = next(
                    (item for item in custom_prompts.list(section) if item["prompt"].strip().casefold() == text.casefold()),
                    None,
                )
                if source is None or not any(favorite_key(section, entry) == source["id"] for entry in current["items"]):
                    raise CatalogError("收藏条目数量已达到上限")
            # The source item must be durable before its favorite references it.
            item, created = await custom_prompts.create_or_reuse(
                {"section": section, "title": title, "subtitle": subtitle, "prompt": text}
            )
            item_id = item["id"]
            existing = next((entry for entry in current["items"] if favorite_key(section, entry) == item_id), None)
            merged_groups = list(dict.fromkeys([*(existing.get("groupIds", []) if existing else []), *groups]))
            saved = self._favorite_record(section, item, merged_groups, title)
            if existing is None:
                current["items"].append(saved)
            else:
                existing["groupIds"] = merged_groups
            try:
                await self._save_section(section, current)
                result = await self.get(section)
            except (ComfyError, StorageReadOnlyError, OSError, TimeoutError) as error:
                # Do not compensate by deleting the source: an upstream timeout
                # may mean the favorite was committed but its reply was lost.
                raise FavoriteSavePendingError(item_id, error) from error
            return {**result, "item_id": item_id, "created": created}

    async def _update_prompt_item(self, payload: dict[str, Any]) -> dict[str, Any]:
        data = await self._read_payload("prompt")
        current = normalize_section(data.get("prompt"))
        item_id = str(payload.get("id") or "").strip()
        item = next((entry for entry in current["items"] if entry.get("id") == item_id), None)
        if item is None:
            raise KeyError(item_id)
        enabled = payload.get("favorite", True)
        if not isinstance(enabled, bool):
            raise CatalogError("favorite 必须是布尔值")
        old_groups = list(item.get("groupIds") or [])
        if enabled:
            item["groupIds"] = self._validated_group_ids(current, payload.get("groupIds", old_groups), True)
            if "nickname" in payload or "title" in payload:
                title = payload.get("title", payload.get("nickname"))
                if not isinstance(title, str) or not title.strip() or len(title.strip()) > 200:
                    raise CatalogError("收藏名称需要 1-200 个字符")
                item["title"] = item["nickname"] = title.strip()
        else:
            current["items"].remove(item)
        self._prune_empty_affected_children(current, old_groups)
        await self._save_section("prompt", current)
        return await self.get("prompt")

    @_locked
    async def update_selection(
        self, section: str, selection: Any, group_ids: Any, prompt_pools: Any = None
    ) -> dict[str, Any]:
        section = self._section(section)
        if not isinstance(selection, dict):
            raise CatalogError("selection 必须是对象")
        mode = str(selection.get("mode") or "include")
        if mode not in {"include", "all"}:
            raise CatalogError("selection.mode 必须是 include 或 all")
        if not isinstance(group_ids, list):
            raise CatalogError("groupIds 必须是数组")
        data = await self._read_payload(section)
        current = normalize_section(data.get(section))
        valid_group_ids = {group["id"] for group in current["groups"]}
        selected_groups = list(
            dict.fromkeys(str(value).strip() for value in group_ids if str(value).strip())
        )
        if not selected_groups:
            raise CatalogError("至少选择一个收藏分组")
        invalid_groups = [value for value in selected_groups if value not in valid_group_ids]
        if invalid_groups:
            raise CatalogError(f"收藏分组不存在: {', '.join(invalid_groups)}")

        if section == "prompt" and prompt_pools is None:
            raise CatalogError("自然语言提示词池不可用")
        catalog_items = prompt_pools.all_items(self.model_profile) if section == "prompt" else self.catalog.all_items(section)
        by_id = {str(item.get("id")): item for item in catalog_items}
        if mode == "include":
            raw_ids = selection.get("ids")
            if not isinstance(raw_ids, list):
                raise CatalogError("selection.ids 必须是数组")
            selected_ids = list(dict.fromkeys(str(value).strip() for value in raw_ids if str(value).strip()))
            excluded_ids: set[str] = set()
        else:
            selected_ids = list(by_id)
            raw_excluded = selection.get("excluded_ids")
            if raw_excluded is None:
                raw_excluded = []
            if not isinstance(raw_excluded, list):
                raise CatalogError("selection.excluded_ids 必须是数组")
            excluded_ids = {str(value).strip() for value in raw_excluded if str(value).strip()}
            invalid_excluded = [item_id for item_id in excluded_ids if item_id not in by_id]
            if invalid_excluded:
                raise CatalogError(
                    f"排除项包含当前词池不存在的词条: {', '.join(invalid_excluded[:5])}"
                )
            selected_ids = [item_id for item_id in selected_ids if item_id not in excluded_ids]
        invalid_ids = [item_id for item_id in selected_ids if item_id not in by_id]
        if invalid_ids:
            raise CatalogError(f"选择中包含当前词池不存在的词条: {', '.join(invalid_ids[:5])}")
        if not selected_ids:
            raise CatalogError("本次选择没有可收藏的词条")

        if section == "prompt":
            created_count, updated_count = self._merge_prompt_snapshots(
                current, [by_id[item_id] for item_id in selected_ids], selected_groups
            )
            await self._save_section(section, current)
            return {**(await self.get(section)), "selectedCount": len(selected_ids),
                    "createdCount": created_count, "updatedCount": updated_count, "groupIds": selected_groups}

        existing_by_key = {
            favorite_key(section, item): item for item in current["items"]
        }
        created_count = 0
        updated_count = 0
        for item_id in selected_ids:
            catalog_item = by_id[item_id]
            key = str(catalog_item.get("favorite_key") or favorite_key(section, catalog_item))
            existing = existing_by_key.get(key)
            if existing is None:
                current["items"].append(self._favorite_record(section, catalog_item, selected_groups, ""))
                created_count += 1
                continue
            old_groups = list(existing.get("groupIds") or [])
            merged_groups = list(dict.fromkeys([*old_groups, *selected_groups]))
            if merged_groups != old_groups:
                existing["groupIds"] = merged_groups
                updated_count += 1

        await self._save_section(section, current)
        return {
            **(await self.get(section)),
            "selectedCount": len(selected_ids),
            "createdCount": created_count,
            "updatedCount": updated_count,
            "groupIds": selected_groups,
        }

    @_locked
    async def create_group(self, section: str, payload: dict[str, Any]) -> dict[str, Any]:
        section = self._section(section)
        name = self._group_name(payload)
        data = await self._read_payload(section)
        current = normalize_section(data.get(section))
        if len(current["groups"]) >= MAX_GROUPS:
            raise CatalogError("收藏分组数量已达到上限")
        self._ensure_unique_sibling(current, None, name)
        group = {
            "id": f"group_{uuid.uuid4().hex[:12]}",
            "name": name,
            "isSystem": False,
            "parentId": None,
            "sourceCustomGroupId": None,
        }
        current["groups"].append(group)
        await self._save_section(section, current)
        return {"group": group, **(await self.get(section))}

    @_locked
    async def import_custom_group(
        self,
        section: str,
        parent_id: str,
        source_group: dict[str, Any],
        source_items: list[dict[str, Any]],
    ) -> dict[str, Any]:
        section = self._section(section)
        data = await self._read_payload(section)
        current = normalize_section(data.get(section))
        if not any(group["id"] == parent_id for group in current["groups"]):
            raise KeyError(parent_id)
        if not source_items:
            raise CatalogError("空自定义分组不能导入收藏子分组")
        if len(current["groups"]) >= MAX_GROUPS:
            raise CatalogError("收藏分组数量已达到上限")
        source_id = str(source_group.get("id") or "").strip()
        name = self._group_name(source_group)
        self._ensure_unique_sibling(current, parent_id, name, source_id)
        group = {
            "id": f"group_{uuid.uuid4().hex[:12]}",
            "name": name,
            "isSystem": False,
            "parentId": parent_id,
            "sourceCustomGroupId": source_id,
        }
        current["groups"].append(group)
        if section == "prompt":
            self._merge_prompt_snapshots(current, source_items, [group["id"]])
            await self._save_section(section, current)
            return {"group": group, **(await self.get(section))}
        for source_item in source_items:
            catalog_item = self.catalog.get(str(source_item.get("id") or ""))
            if catalog_item is None or catalog_item.get("section") != section:
                continue
            key = favorite_key(section, catalog_item)
            existing = next(
                (item for item in current["items"] if favorite_key(section, item) == key), None
            )
            if existing is not None:
                existing["groupIds"] = list(
                    dict.fromkeys([*(existing.get("groupIds") or []), group["id"]])
                )
            else:
                current["items"].append(
                    self._favorite_record(section, catalog_item, [group["id"]], "")
                )
        await self._save_section(section, current)
        return {"group": group, **(await self.get(section))}

    @staticmethod
    def _merge_prompt_snapshots(current: dict[str, Any], sources: list[dict[str, Any]], groups: list[str]) -> tuple[int, int]:
        """Copy pool contents into independent favorites; future pool edits do not mutate them."""
        by_text = {str(item.get("text", item.get("customContent", ""))).strip().casefold(): item for item in current["items"]}
        created = updated = 0
        for source in sources:
            text = str(source.get("prompt", source.get("text", ""))).strip()
            if not text:
                raise CatalogError("自然语言收藏正文不能为空")
            existing = by_text.get(text.casefold())
            if existing is None:
                if len(current["items"]) >= MAX_ITEMS:
                    raise CatalogError("收藏条目数量已达到上限")
                item_id = f"favorite_{uuid.uuid4().hex}"
                title = str(source.get("title") or text[:80])
                existing = {"id": item_id, "name": item_id, "isCustom": True, "title": title,
                            "nickname": title, "subtitle": str(source.get("subtitle") or ""),
                            "text": text, "customContent": text, "groupIds": list(groups)}
                current["items"].append(existing)
                by_text[text.casefold()] = existing
                created += 1
            else:
                merged = list(dict.fromkeys([*(existing.get("groupIds") or []), *groups]))
                if merged != existing.get("groupIds"):
                    existing["groupIds"] = merged
                    updated += 1
        return created, updated

    @_locked
    async def update_group(self, section: str, group_id: str, payload: dict[str, Any]) -> dict[str, Any]:
        section = self._section(section)
        if group_id == "default":
            raise CatalogError("默认收藏分组不能重命名")
        name = self._group_name(payload)
        data = await self._read_payload(section)
        current = normalize_section(data.get(section))
        group = next((item for item in current["groups"] if item["id"] == group_id), None)
        if group is None:
            raise KeyError(group_id)
        self._ensure_unique_sibling(current, group.get("parentId"), name, except_id=group_id)
        group["name"] = name
        await self._save_section(section, current)
        return await self.get(section)

    @_locked
    async def delete_group(
        self, section: str, group_id: str, delete_items: bool = False
    ) -> dict[str, Any]:
        section = self._section(section)
        if group_id == "default":
            raise CatalogError("默认收藏分组不能删除")
        data = await self._read_payload(section)
        current = normalize_section(data.get(section))
        group = next((item for item in current["groups"] if item["id"] == group_id), None)
        if group is None:
            raise KeyError(group_id)
        subtree_ids = _descendant_ids(current["groups"], group_id)
        direct_items = [item for item in current["items"] if group_id in (item.get("groupIds") or [])]
        child_count = sum(item.get("parentId") == group_id for item in current["groups"])
        if delete_items and (group.get("parentId") is None or child_count or not direct_items):
            raise CatalogError("只有含有条目且没有子分组的收藏叶子子组可以同时删除条目")

        deleted_favorite_count = 0
        detached_item_count = 0
        moved_to_default_count = 0
        next_items: list[dict[str, Any]] = []
        for item in current["items"]:
            group_ids = list(item.get("groupIds") or [])
            affected = bool(subtree_ids.intersection(group_ids))
            if not affected:
                next_items.append(item)
                continue
            remaining = [value for value in group_ids if value not in subtree_ids]
            if delete_items and group_id in group_ids and not remaining:
                deleted_favorite_count += 1
                continue
            if delete_items:
                detached_item_count += 1
            if not remaining:
                remaining = ["default"]
                moved_to_default_count += 1
            item["groupIds"] = remaining
            next_items.append(item)
        current["items"] = next_items
        current["groups"] = [
            item for item in current["groups"] if item["id"] not in subtree_ids
        ]
        await self._save_section(section, current)
        return {
            **(await self.get(section)),
            "deletedGroupIds": sorted(subtree_ids),
            "deletedGroupCount": len(subtree_ids),
            "deletedFavoriteCount": deleted_favorite_count,
            "detachedItemCount": detached_item_count,
            "movedToDefaultCount": moved_to_default_count,
        }

    @_locked
    async def sync_custom(self, section: str, item: dict[str, Any]) -> None:
        section = self._section(section)
        data = await self._read_payload(section)
        current = normalize_section(data.get(section))
        key = str(item.get("id") or "")
        saved = next(
            (
                entry
                for entry in current["items"]
                if entry.get("isCustom") and favorite_key(section, entry) == key
            ),
            None,
        )
        if saved is None:
            return
        saved["nickname"] = str(item.get("title") or "")
        saved["subtitle"] = str(item.get("subtitle") or "")
        saved["customContent"] = str(item.get("prompt") or "")
        await self._save_section(section, current)

    async def _update_artist(self, payload: dict[str, Any]) -> dict[str, Any]:
        raw_name = str(payload.get("name") or "").strip()
        name = self._artist_name(raw_name)
        data = await self._read_payload("artist")
        current = normalize_section(data.get("artist"))
        position = next(
            (
                index
                for index, item in enumerate(current["items"])
                if normalize_text(self._artist_name(item.get("name"))) == normalize_text(name)
            ),
            None,
        )
        existing = current["items"][position] if position is not None else None
        old_group_ids = list(existing.get("groupIds") or []) if existing else []
        enabled = payload.get("favorite", True)
        if not isinstance(enabled, bool):
            raise CatalogError("favorite 必须是布尔值")
        if "groupIds" not in payload and existing is not None:
            # 与 update_item 同理:未携带 groupIds 时保留原分组(前端"保存当前画师"
            # 对每个画师无条件重复收藏,不能因此清掉分组整理)。
            groups = self._validated_group_ids(current, old_group_ids, enabled)
        else:
            groups = self._validated_group_ids(current, payload.get("groupIds"), enabled)
        if not enabled:
            if position is not None:
                current["items"].pop(position)
        else:
            nickname = str(payload.get("nickname") or "").strip()[:300]
            saved = {"name": name, "nickname": nickname, "groupIds": groups, "isCustom": False}
            if position is None:
                current["items"].append(saved)
            else:
                if "nickname" not in payload:
                    saved["nickname"] = str(existing.get("nickname") or "")
                current["items"][position] = saved
        self._prune_empty_affected_children(current, old_group_ids)
        await self._save_section("artist", current)
        return await self.get("artist")

    @staticmethod
    def _validated_group_ids(
        current: dict[str, list[dict[str, Any]]], groups: Any, enabled: bool
    ) -> list[str]:
        valid_group_ids = {group["id"] for group in current["groups"]}
        if groups is None:
            return ["default"] if enabled else []
        if not isinstance(groups, list):
            raise CatalogError("groupIds 必须是数组")
        result = list(
            dict.fromkeys(str(value) for value in groups if str(value) in valid_group_ids)
        )
        return result or (["default"] if enabled else [])

    @staticmethod
    def _prune_empty_affected_children(
        current: dict[str, list[dict[str, Any]]], affected_group_ids: list[str]
    ) -> None:
        affected = set(affected_group_ids)
        if not affected:
            return
        child_parent_ids = {group.get("parentId") for group in current["groups"]}
        non_empty = {
            group_id for item in current["items"] for group_id in item.get("groupIds") or []
        }
        removed = {
            group["id"]
            for group in current["groups"]
            if group["id"] in affected
            and not group.get("isSystem")
            and group.get("parentId") is not None
            and group["id"] not in child_parent_ids
            and group["id"] not in non_empty
        }
        if removed:
            current["groups"] = [group for group in current["groups"] if group["id"] not in removed]

    @staticmethod
    def _ensure_unique_sibling(
        current: dict[str, list[dict[str, Any]]],
        parent_id: str | None,
        name: str,
        source_id: str | None = None,
        except_id: str = "",
    ) -> None:
        name_key = normalize_text(name)
        for group in current["groups"]:
            if group["id"] == except_id or group.get("parentId") != parent_id:
                continue
            if normalize_text(group["name"]) == name_key:
                raise CatalogError("同一收藏父分组下不能有重名分组")
            if source_id and group.get("sourceCustomGroupId") == source_id:
                raise CatalogError("这个自定义分组已经导入到当前收藏父分组")

    @staticmethod
    def _group_name(payload: dict[str, Any]) -> str:
        name = str(payload.get("name") or "").strip()
        if not name or len(name) > 100:
            raise CatalogError("收藏分组名称需要 1-100 个字符")
        return name

    @staticmethod
    def _artist_name(value: Any) -> str:
        name = str(value or "").strip()
        if name.startswith("@"):
            name = name[1:].strip()
        elif name.lower().startswith("by "):
            name = name[3:].strip()
        if not name or len(name) > 200 or "," in name:
            raise CatalogError("画师名称需要 1-200 个字符且不能包含逗号")
        return name

    @staticmethod
    def _favorite_record(
        section: str, item: dict[str, Any], groups: list[str], nickname: str
    ) -> dict[str, Any]:
        if item.get("builtin"):
            if section == "character":
                return {
                    "name": item["favorite_key"],
                    "nickname": nickname,
                    "groupIds": groups,
                    "isCustom": False,
                }
            return {
                "id": item["favorite_key"],
                "name": item.get("subtitle") or item.get("title"),
                "nickname": nickname,
                "groupIds": groups,
                "isCustom": False,
            }
        custom_name = str(item.get("id") or "custom")
        return {
            "id": custom_name,
            "name": custom_name,
            "nickname": nickname or item.get("title") or "",
            "subtitle": item.get("subtitle") or "",
            "customContent": item.get("prompt") or "",
            "groupIds": groups,
            "isCustom": True,
        }
