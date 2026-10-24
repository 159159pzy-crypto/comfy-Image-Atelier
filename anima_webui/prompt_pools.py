"""Profile-isolated, structured natural-language prompt pools for Krea 2."""
from __future__ import annotations

import asyncio
import copy
import csv
import hashlib
import io
import json
import os
import tempfile
import uuid
from pathlib import Path
from typing import Any

from .async_utils import serialized_mutation
from .model_profiles import DEFAULT_MODEL_PROFILE, ModelProfileError, normalize_model_profile
from .persistence import StorageProtection, backup_for_migration
from .pool_entries import normalize_krea_entry, normalize_group_names
from .workflow import WorkflowError

MAX_PROMPTS = 50_000
MAX_PROMPT_LENGTH = 20_000
MAX_GROUPS = 512
MAX_IMPORT_ROWS = 2000
MAX_IMPORT_BYTES = 5 * 1024 * 1024


def _ids(value: Any, label: str = "groupIds") -> list[str]:
    if not isinstance(value, list) or len(value) > MAX_PROMPTS:
        raise WorkflowError(f"{label} 必须是数组且不能超过 {MAX_PROMPTS} 项")
    if any(not isinstance(item, str) or not item.strip() for item in value):
        raise WorkflowError(f"{label} 必须包含非空字符串")
    return list(dict.fromkeys(item.strip() for item in value))


def _entry(value: Any) -> dict[str, Any]:
    raw = dict(value) if isinstance(value, dict) else value
    group_ids = []
    if isinstance(raw, dict):
        group_ids = _ids(raw.pop("groupIds", []))
        raw.pop("id", None)
    try:
        item = normalize_krea_entry(raw)
    except ValueError as error:
        raise WorkflowError(str(error)) from error
    return {"title": item["title"], "subtitle": item["subtitle"], "text": item["prompt"], "groupIds": group_ids}


def _entries(values: Any) -> list[dict[str, Any]]:
    if not isinstance(values, list) or len(values) > MAX_PROMPTS:
        raise WorkflowError(f"提示词池 items 必须是数组且不能超过 {MAX_PROMPTS} 项")
    result, seen = [], set()
    for raw in values:
        if isinstance(raw, str) and not raw.strip():
            continue
        item = _entry(raw)
        key = item["text"].casefold()
        if key not in seen:
            result.append(item)
            seen.add(key)
    return result


def normalize_prompt_items(value: Any) -> list[str]:
    """Legacy text-only helper retained for embedded callers."""
    if not isinstance(value, list) or any(not isinstance(item, str) for item in value):
        raise WorkflowError("提示词池 items 必须是字符串数组")
    return [item["text"] for item in _entries(value)]


class PromptPoolStore(StorageProtection):
    store_name = "prompt_pools"

    def __init__(self, path: str | Path):
        self.path = Path(path)
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.items: dict[str, list[dict[str, Any]]] = {}
        self.groups: dict[str, list[dict[str, Any]]] = {}
        self.load_warnings: list[str] = []
        self._lock = asyncio.Lock()
        self.reload()

    @staticmethod
    def _profile(value: Any) -> str:
        try:
            return normalize_model_profile(value)
        except ModelProfileError as error:
            raise WorkflowError(str(error)) from error

    @staticmethod
    def item_id(text: str) -> str:
        return "prompt_" + hashlib.sha256(text.casefold().encode("utf-8")).hexdigest()[:20]

    @classmethod
    def _public(cls, item: dict[str, Any]) -> dict[str, Any]:
        return {**copy.deepcopy(item), "id": cls.item_id(item["text"]), "prompt": item["text"]}

    @staticmethod
    def _group_name(value: Any) -> str:
        if not isinstance(value, str) or not value.strip() or len(value.strip()) > 100:
            raise WorkflowError("分组名称需要 1-100 个字符")
        return value.strip()

    @classmethod
    def _read_groups(cls, raw: Any) -> list[dict[str, Any]]:
        if not isinstance(raw, list) or len(raw) > MAX_GROUPS:
            raise WorkflowError("提示词池分组数据无效")
        groups, seen = [], set()
        for value in raw:
            if not isinstance(value, dict):
                raise WorkflowError("提示词池分组必须是对象")
            gid, parent, kind = value.get("id"), value.get("parentId") or None, value.get("kind", "group")
            if not isinstance(gid, str) or not gid or gid in seen or not isinstance(kind, str) or kind not in {"group", "import"}:
                raise WorkflowError("提示词池分组标识无效")
            if parent is not None and not isinstance(parent, str):
                raise WorkflowError("提示词池分组父级无效")
            groups.append({"id": gid, "name": cls._group_name(value.get("name")), "parentId": parent, "kind": kind})
            seen.add(gid)
        by_id, siblings = {group["id"]: group for group in groups}, set()
        for group in groups:
            sibling = (group["parentId"], group["name"].casefold())
            if sibling in siblings:
                raise WorkflowError("同级分组名称不能重复")
            siblings.add(sibling)
            visited, current = {group["id"]}, group["parentId"]
            while current:
                if current not in by_id or current in visited:
                    raise WorkflowError("提示词池分组层级无效")
                visited.add(current)
                current = by_id[current]["parentId"]
        return groups

    @staticmethod
    def _validate_groups(ids: list[str], groups: list[dict[str, Any]]) -> None:
        valid = {group["id"] for group in groups if group["kind"] == "group"}
        if any(gid not in valid for gid in ids):
            raise WorkflowError("自定义分组不存在或不是可加入的分组，请刷新后重试")

    def reload(self) -> None:
        self.load_warnings, self.read_only_reason = [], None
        self.items, self.groups = {DEFAULT_MODEL_PROFILE: []}, {DEFAULT_MODEL_PROFILE: []}
        if not self.path.is_file():
            return
        try:
            payload = json.loads(self.path.read_text(encoding="utf-8"))
            if not isinstance(payload, dict) or type(payload.get("version")) is not int or payload["version"] not in {1, 2, 3}:
                raise WorkflowError("自然语言提示词池文件版本无效")
            version = payload["version"]
            raw = payload.get("profiles", payload.get("items", {}))
            if version == 1 and isinstance(raw, list):
                raw = {DEFAULT_MODEL_PROFILE: raw}
            if not isinstance(raw, dict):
                raise WorkflowError("自然语言提示词池内容无效")
            profiles, all_groups = {}, {}
            for name, value in raw.items():
                profile = self._profile(name)
                if version == 3:
                    if not isinstance(value, dict) or set(value) - {"items", "groups"}:
                        raise WorkflowError("自然语言提示词池档案无效")
                    groups, records = self._read_groups(value.get("groups", [])), _entries(value.get("items", []))
                    for item in records:
                        self._validate_groups(item["groupIds"], groups)
                else:
                    records, groups = _entries(normalize_prompt_items(value)), []
                profiles[profile], all_groups[profile] = records, groups
            profiles.setdefault(DEFAULT_MODEL_PROFILE, [])
            all_groups.setdefault(DEFAULT_MODEL_PROFILE, [])
            self.items, self.groups = profiles, all_groups
        except (OSError, UnicodeError, json.JSONDecodeError, WorkflowError) as error:
            self._recover_load_failure(error)
            return
        if version < 3:
            try:
                backup = backup_for_migration(self.path, 3)
                self._write_text(self._serialize(self.items, self.groups))
                if backup:
                    self.load_warnings.append(f"提示词池已迁移，原文件备份为 {backup.name}")
            except OSError as error:
                self._protect_storage(f"提示词池已在内存迁移，但迁移备份或写回失败({error})")

    @staticmethod
    def _serialize(items: dict[str, Any], groups: dict[str, Any]) -> str:
        profiles = {profile: {"items": values, "groups": groups.get(profile, [])} for profile, values in items.items()}
        return json.dumps({"version": 3, "profiles": profiles}, ensure_ascii=False, indent=2) + "\n"

    async def _save(self, profiles: dict[str, Any], groups: dict[str, Any] | None = None) -> None:
        self.ensure_writable()
        source_groups = self.groups if groups is None else groups
        next_groups = {profile: source_groups.get(profile, []) for profile in profiles}
        await asyncio.to_thread(self._write_text, self._serialize(profiles, next_groups))
        self.items, self.groups = profiles, next_groups

    def _write_text(self, text: str) -> None:
        self.ensure_writable()
        fd, temp_name = tempfile.mkstemp(prefix="prompt-pools-", suffix=".json", dir=self.path.parent)
        try:
            with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as handle:
                handle.write(text)
                handle.flush()
                os.fsync(handle.fileno())
            os.replace(temp_name, self.path)
        finally:
            if os.path.exists(temp_name):
                os.unlink(temp_name)

    def all_items(self, model_profile: Any = "krea2") -> list[dict[str, Any]]:
        return [self._public(item) for item in self.items.get(self._profile(model_profile), [])]

    def resolve(self, model_profile: Any, selected_ids: list[str] | None = None) -> list[str]:
        selected = set(selected_ids or [])
        return [item["text"] for item in self.items.get(self._profile(model_profile), [])
                if not selected_ids or self.item_id(item["text"]) in selected]

    def group_filter_ids(self, model_profile: Any, group_id: str) -> set[str]:
        groups = self.groups.get(self._profile(model_profile), [])
        if not any(group["id"] == group_id for group in groups):
            return set()
        descendants = {group_id}
        while True:
            more = {group["id"] for group in groups if group["parentId"] in descendants}
            if more <= descendants:
                return descendants
            descendants.update(more)

    def list_group_items(self, model_profile: Any, group_id: str = "") -> list[dict[str, Any]]:
        ids = self.group_filter_ids(model_profile, group_id) if group_id else None
        return [item for item in self.all_items(model_profile) if ids is None or ids.intersection(item["groupIds"])]

    def list_groups(self, model_profile: Any = "krea2") -> dict[str, Any]:
        profile = self._profile(model_profile)
        values, groups, result = self.items.get(profile, []), self.groups.get(profile, []), []
        for group in groups:
            ids = self.group_filter_ids(profile, group["id"])
            matching = [item for item in values if ids.intersection(item["groupIds"])]
            result.append({**copy.deepcopy(group), "count": len(matching),
                           "directCount": sum(group["id"] in item["groupIds"] for item in values),
                           "exclusiveCount": sum(not (set(item["groupIds"]) - ids) for item in matching),
                           "childCount": sum(child["parentId"] == group["id"] for child in groups)})
        return {"model_profile": profile, "groups": result}

    def list(self, model_profile: Any = DEFAULT_MODEL_PROFILE, *, query: str = "", page: int = 1,
             limit: int = 48, selected_ids: list[str] | None = None, selected_only: bool = False,
             collection: str = "", custom_group: str = "", sort: str = "",
             favorites: dict[str, Any] | None = None) -> dict[str, Any]:
        profile = self._profile(model_profile)
        try:
            page, limit = int(page), int(limit)
        except (TypeError, ValueError) as error:
            raise WorkflowError("提示词池分页参数无效") from error
        if page < 1 or not 1 <= limit <= 500:
            raise WorkflowError("提示词池分页参数无效")
        if not isinstance(sort, str) or sort not in {"", "favorite-first"}:
            raise WorkflowError("提示词池排序方式无效")
        selected = set(_ids(selected_ids or [], "selected_ids"))
        favorites = favorites or {}
        saved = [item for item in favorites.get("items", []) if item.get("groupIds")]
        collection_ids = {collection}
        if collection and collection != "__all__":
            while True:
                more = {group["id"] for group in favorites.get("groups", []) if group.get("parentId") in collection_ids}
                if more <= collection_ids:
                    break
                collection_ids.update(more)
        def favorite_text(item: dict[str, Any]) -> str:
            return str(item.get("text") or item.get("customContent") or "").strip()
        favorite_by_text = {favorite_text(item).casefold(): item for item in saved if favorite_text(item)}
        actual = {item["text"].casefold(): item for item in self.items.get(profile, [])}
        values = []
        if collection:
            candidates = [item for item in saved if collection == "__all__" or collection_ids.intersection(item.get("groupIds", []))]
            seen = set()
            for saved_item in candidates:
                text = favorite_text(saved_item)
                key = text.casefold()
                if not text or key in seen:
                    continue
                seen.add(key)
                original = actual.get(key)
                value = _entry({"text": text, "title": saved_item.get("title") or saved_item.get("nickname") or text.splitlines()[0][:200],
                                "subtitle": saved_item.get("subtitle") or ""})
                value["groupIds"] = list(original["groupIds"]) if original else []
                values.append({**self._public(value), "in_pool": original is not None,
                               "favorite": True, "favorite_id": saved_item["id"]})
        else:
            for item in actual.values():
                favorite = favorite_by_text.get(item["text"].casefold())
                values.append({**self._public(item), "in_pool": True, "favorite": favorite is not None,
                               "favorite_id": favorite.get("id", "") if favorite else ""})
        group_ids = self.group_filter_ids(profile, custom_group) if custom_group else None
        needle = str(query or "").strip().casefold()
        values = [item for item in values if (group_ids is None or group_ids.intersection(item["groupIds"]))
                  and (not needle or any(needle in str(item[field]).casefold() for field in ("title", "subtitle", "text")))]
        selected_count = sum(item["id"] in selected and item["in_pool"] for item in values)
        if selected_only:
            values = [item for item in values if item["id"] in selected and item["in_pool"]]
        if sort == "favorite-first":
            values.sort(key=lambda item: not item["favorite"])
        total = len(values)
        return {"model_profile": profile,
                "items": [{**item, "selected": item["id"] in selected and item["in_pool"]}
                          for item in values[(page - 1) * limit:page * limit]],
                "count": total, "total": total, "pool_count": len(actual), "selected_count": selected_count,
                "page": page, "limit": limit, "pages": (total + limit - 1) // limit if total else 0}

    @staticmethod
    def _normalize_import_mode(mode: Any) -> str:
        if not isinstance(mode, str) or mode not in {"replace", "append"}:
            raise WorkflowError("提示词池导入 mode 必须是 replace 或 append")
        return mode

    def preview_import(self, model_profile: Any, parsed: dict[str, Any], *, mode: str = "replace") -> dict[str, Any]:
        profile, mode = self._profile(model_profile), self._normalize_import_mode(mode)
        items = normalize_prompt_items(parsed.get("items"))
        current = {item["text"].casefold() for item in self.items.get(profile, [])}
        existing_duplicates = sum(text.casefold() in current for text in items)
        unique = len(items)
        importable = unique if mode == "replace" else unique - existing_duplicates
        if mode == "append" and len(current) + importable > MAX_PROMPTS:
            raise WorkflowError(f"追加后提示词池不能超过 {MAX_PROMPTS} 项")
        total = int(parsed.get("total", unique))
        return {"model_profile": profile, "mode": mode, "items": items,
                "summary": {"total": total, "unique": unique, "duplicates": int(parsed.get("duplicates", total - unique)),
                            "existingDuplicates": existing_duplicates, "new": importable}}

    @serialized_mutation
    async def import_items(self, model_profile: Any, values: Any, *, mode: str = "replace") -> dict[str, Any]:
        profile, mode = self._profile(model_profile), self._normalize_import_mode(mode)
        incoming = _entries(values)
        self.ensure_writable()
        existing = self.items.get(profile, []) if mode == "append" else []
        next_groups = copy.deepcopy(self.groups)
        if mode == "replace":
            next_groups[profile] = []
        seen = {item["text"].casefold() for item in existing}
        added = [item for item in incoming if item["text"].casefold() not in seen]
        groups = next_groups.setdefault(profile, [])
        imported_group_names = {}
        for raw in values:
            if isinstance(raw, dict) and "groups" in raw:
                try:
                    imported_group_names.setdefault(_entry(raw)["text"].casefold(), normalize_group_names(raw["groups"]))
                except ValueError as error:
                    raise WorkflowError(str(error)) from error
        for item in added:
            self._validate_groups(item["groupIds"], groups)
            for name in imported_group_names.get(item["text"].casefold(), []):
                group = next((group for group in groups if group["parentId"] is None
                              and group["name"].casefold() == name.casefold()), None)
                if group is not None and group["kind"] != "group":
                    raise WorkflowError("分组名称与已有导入大项冲突")
                if group is None:
                    group = {"id": f"prompt_group_{uuid.uuid4().hex[:12]}", "name": name,
                             "parentId": None, "kind": "group"}
                    groups.append(group)
                if group["id"] not in item["groupIds"]:
                    item["groupIds"].append(group["id"])
        if len(groups) > MAX_GROUPS:
            raise WorkflowError("自定义分组数量已达到上限")
        result_items = [*existing, *added]
        if len(result_items) > MAX_PROMPTS:
            raise WorkflowError(f"提示词池不能超过 {MAX_PROMPTS} 项")
        await self._save({**self.items, profile: result_items}, next_groups)
        return {**self.list(profile), "imported_ids": [self.item_id(item["text"]) for item in added],
                "valid_ids": [self.item_id(item["text"]) for item in result_items]}

    @staticmethod
    def _make_item(text: Any, title: Any, subtitle: Any, group_ids: Any) -> dict[str, Any]:
        value = {"text": text}
        for key, field in (("title", title), ("subtitle", subtitle), ("groupIds", group_ids)):
            if field is not None:
                value[key] = field
        return _entry(value)

    @serialized_mutation
    async def create_item(self, model_profile: Any, text: Any, *, title: Any = None,
                          subtitle: Any = None, group_ids: Any = None) -> dict[str, Any]:
        profile = self._profile(model_profile)
        item = self._make_item(text, title, subtitle, group_ids)
        self._validate_groups(item["groupIds"], self.groups.get(profile, []))
        self.ensure_writable()
        values = self.items.get(profile, [])
        if any(value["text"].casefold() == item["text"].casefold() for value in values):
            raise WorkflowError("提示词池中已存在相同文本")
        if len(values) >= MAX_PROMPTS:
            raise WorkflowError(f"提示词池不能超过 {MAX_PROMPTS} 项")
        await self._save({**self.items, profile: [*values, item]})
        return {"model_profile": profile, **self._public(item), "count": len(values) + 1}

    @serialized_mutation
    async def ensure_item(self, model_profile: Any, text: Any, *, title: Any = None,
                          subtitle: Any = None, group_ids: Any = None) -> dict[str, Any]:
        profile = self._profile(model_profile)
        item = self._make_item(text, title, subtitle, group_ids)
        self._validate_groups(item["groupIds"], self.groups.get(profile, []))
        values = self.items.get(profile, [])
        existing = next((value for value in values if value["text"].casefold() == item["text"].casefold()), None)
        if existing is not None:
            return {"model_profile": profile, **self._public(existing), "created": False, "count": len(values)}
        self.ensure_writable()
        if len(values) >= MAX_PROMPTS:
            raise WorkflowError(f"提示词池不能超过 {MAX_PROMPTS} 项")
        await self._save({**self.items, profile: [*values, item]})
        return {"model_profile": profile, **self._public(item), "created": True, "count": len(values) + 1}

    @serialized_mutation
    async def ensure_items(self, model_profile: Any, values: Any) -> dict[str, Any]:
        profile = self._profile(model_profile)
        incoming = _entries(values)
        existing = self.items.get(profile, [])
        by_text = {item["text"].casefold(): item for item in existing}
        added, result = [], []
        for item in incoming:
            self._validate_groups(item["groupIds"], self.groups.get(profile, []))
            found = by_text.get(item["text"].casefold())
            if found is None:
                added.append(item)
                by_text[item["text"].casefold()] = item
            result.append({**self._public(found or item), "created": found is None})
        if len(by_text) > MAX_PROMPTS:
            raise WorkflowError(f"提示词池不能超过 {MAX_PROMPTS} 项")
        if added:
            await self._save({**self.items, profile: [*existing, *added]})
        return {"model_profile": profile, "items": result,
                "created_ids": [self.item_id(item["text"]) for item in added], "count": len(by_text)}

    @serialized_mutation
    async def update_item(self, model_profile: Any, item_id: Any, text: Any, *, title: Any = None,
                          subtitle: Any = None, group_ids: Any = None) -> dict[str, Any]:
        profile = self._profile(model_profile)
        self.ensure_writable()
        target = str(item_id or "").strip()
        if not target:
            raise WorkflowError("提示词池 item_id 不能为空")
        values = self.items.get(profile, [])
        index = next((i for i, value in enumerate(values) if self.item_id(value["text"]) == target), None)
        if index is None:
            raise KeyError(target)
        old = values[index]
        item = self._make_item(text, old["title"] if title is None else title,
                               old["subtitle"] if subtitle is None else subtitle,
                               old["groupIds"] if group_ids is None else group_ids)
        self._validate_groups(item["groupIds"], self.groups.get(profile, []))
        if any(i != index and value["text"].casefold() == item["text"].casefold() for i, value in enumerate(values)):
            raise WorkflowError("提示词池中已存在相同文本")
        updated = list(values)
        updated[index] = item
        await self._save({**self.items, profile: updated})
        return {"model_profile": profile, **self._public(item), "previous_id": target, "count": len(updated)}

    async def clear(self, model_profile: Any = DEFAULT_MODEL_PROFILE) -> dict[str, Any]:
        return await self.import_items(model_profile, [], mode="replace")

    @serialized_mutation
    async def delete_item(self, model_profile: Any, item_id: Any) -> dict[str, Any]:
        profile = self._profile(model_profile)
        self.ensure_writable()
        target = str(item_id or "").strip()
        if not target:
            raise WorkflowError("提示词池 item_id 不能为空")
        values = self.items.get(profile, [])
        remaining = [item for item in values if self.item_id(item["text"]) != target]
        if len(remaining) == len(values):
            raise KeyError(target)
        await self._save({**self.items, profile: remaining})
        return self.list(profile)

    @serialized_mutation
    async def delete_items(self, model_profile: Any, item_ids: Any) -> dict[str, Any]:
        profile = self._profile(model_profile)
        targets = set(_ids(item_ids, "ids"))
        self.ensure_writable()
        values = self.items.get(profile, [])
        deleted = [self.item_id(item["text"]) for item in values if self.item_id(item["text"]) in targets]
        if deleted:
            await self._save({**self.items, profile: [item for item in values if self.item_id(item["text"]) not in targets]})
        return {"model_profile": profile, "deleted": deleted, "count": len(self.items.get(profile, []))}

    @classmethod
    def _unique_group(cls, groups: list[dict[str, Any]], name: str, parent: Any, exclude: str = "") -> None:
        if any(group["id"] != exclude and group["parentId"] == parent and group["name"].casefold() == name.casefold() for group in groups):
            raise WorkflowError("同级分组名称不能重复")

    @serialized_mutation
    async def create_group(self, model_profile: Any, payload: dict[str, Any]) -> dict[str, Any]:
        profile = self._profile(model_profile)
        name, groups = self._group_name(payload.get("name")), copy.deepcopy(self.groups.get(profile, []))
        parent = payload.get("parentId") or None
        if parent is not None and (not isinstance(parent, str) or not any(group["id"] == parent for group in groups)):
            raise WorkflowError("父分组不存在")
        if len(groups) >= MAX_GROUPS:
            raise WorkflowError("自定义分组数量已达到上限")
        self._unique_group(groups, name, parent)
        group = {"id": f"prompt_group_{uuid.uuid4().hex[:12]}", "name": name, "parentId": parent, "kind": "group"}
        groups.append(group)
        await self._save({**self.items, profile: self.items.get(profile, [])}, {**self.groups, profile: groups})
        return {"group": copy.deepcopy(group), **self.list_groups(profile)}

    @serialized_mutation
    async def update_group(self, model_profile: Any, group_id: str, payload: dict[str, Any]) -> dict[str, Any]:
        profile = self._profile(model_profile)
        self.ensure_writable()
        groups = copy.deepcopy(self.groups.get(profile, []))
        group = next((group for group in groups if group["id"] == group_id), None)
        if group is None:
            raise KeyError(group_id)
        if "parentId" in payload and (payload["parentId"] or None) != group["parentId"]:
            raise WorkflowError("此操作不支持移动分组")
        name = self._group_name(payload.get("name"))
        self._unique_group(groups, name, group["parentId"], group_id)
        group["name"] = name
        await self._save(self.items, {**self.groups, profile: groups})
        return self.list_groups(profile)

    @serialized_mutation
    async def delete_group(self, model_profile: Any, group_id: str, delete_mode: str = "keep") -> dict[str, Any]:
        profile = self._profile(model_profile)
        self.ensure_writable()
        if not isinstance(delete_mode, str) or delete_mode not in {"keep", "exclusive", "all"}:
            raise WorkflowError("deleteMode 必须是 keep、exclusive 或 all")
        ids = self.group_filter_ids(profile, group_id)
        if not ids:
            raise KeyError(group_id)
        remaining, deleted, detached = [], [], 0
        for original in self.items.get(profile, []):
            item, current = copy.deepcopy(original), set(original["groupIds"])
            if current.intersection(ids):
                if delete_mode == "all" or (delete_mode == "exclusive" and not (current - ids)):
                    deleted.append(self.item_id(item["text"]))
                    continue
                item["groupIds"] = [gid for gid in item["groupIds"] if gid not in ids]
                detached += 1
            remaining.append(item)
        groups = [group for group in self.groups.get(profile, []) if group["id"] not in ids]
        await self._save({**self.items, profile: remaining}, {**self.groups, profile: groups})
        return {**self.list_groups(profile), "deletedItemIds": deleted, "deletedItemCount": len(deleted),
                "detachedItemCount": detached, "deletedGroupIds": sorted(ids), "deletedGroupCount": len(ids), "deleteMode": delete_mode}

    @staticmethod
    def template(file_format: str) -> tuple[str, str, bytes]:
        example = {"section": "prompt", "title": "雾中森林", "subtitle": "清晨薄雾与柔和光线",
                   "prompt": "A quiet forest at dawn, with soft light filtering through the mist.", "groups": ["自然风景"]}
        if file_format == "json":
            body = json.dumps({"version": 1, "items": [example]}, ensure_ascii=False, indent=2) + "\n"
            return "krea-prompt-pool-template.json", "application/json", body.encode("utf-8")
        if file_format == "csv":
            stream = io.StringIO()
            writer = csv.DictWriter(stream, fieldnames=list(example))
            writer.writeheader()
            writer.writerow({**example, "groups": "|".join(example["groups"])})
            return "krea-prompt-pool-template.csv", "text/csv", ("\ufeff" + stream.getvalue()).encode("utf-8")
        raise WorkflowError("导入模板只支持 json 或 csv")

    def _import_state(self, profile: str, mode: str, target_group_ids: Any) -> tuple[list, list, list[str]]:
        mode = self._normalize_import_mode(mode)
        targets = _ids([] if target_group_ids is None else target_group_ids, "targetGroupIds")
        if mode == "replace" and targets:
            raise WorkflowError("整池替换不能加入将被删除的已有分组")
        groups = copy.deepcopy(self.groups.get(profile, [])) if mode == "append" else []
        self._validate_groups(targets, groups)
        items = copy.deepcopy(self.items.get(profile, [])) if mode == "append" else []
        return items, groups, targets

    @classmethod
    def _bundle(cls, groups: list[dict[str, Any]], name: str) -> dict[str, Any] | None:
        found = next((group for group in groups if group["parentId"] is None and group["name"].casefold() == name.casefold()), None)
        if found is not None and found["kind"] != "import":
            raise WorkflowError("导入大项名称与已有自定义分组冲突")
        return found

    def preview_structured_import(self, model_profile: Any, content: Any, file_format: str, *,
                                  bundle_name: str, target_group_ids: Any = None, mode: str = "append") -> dict[str, Any]:
        profile, name = self._profile(model_profile), self._group_name(bundle_name)
        current, groups, _ = self._import_state(profile, mode, target_group_ids)
        raw_items = raw_prompt_import(content, file_format)
        if len(raw_items) > MAX_IMPORT_ROWS:
            raise WorkflowError(f"单次最多导入 {MAX_IMPORT_ROWS} 项")
        current_by_text = {item["text"].casefold(): item for item in current}
        rows, seen, names = [], set(), {}
        for index, raw in enumerate(raw_items, 2 if file_format in {"csv", "text/csv"} else 1):
            try:
                item = normalize_krea_entry(raw)
                key = item["prompt"].casefold()
                if key in seen:
                    raise WorkflowError("同一导入文件中存在重复正文")
                seen.add(key)
                conflict = key in current_by_text
                for group_name in item["groups"] or ["未分组"]:
                    names.setdefault(group_name.casefold(), group_name)
                rows.append({"row": index, "status": "conflict" if conflict else "new",
                             "action": "skip" if conflict else "create", "existingId": self.item_id(item["prompt"]) if conflict else "",
                             "item": item, "groups": item["groups"], "error": ""})
            except (ValueError, WorkflowError) as error:
                rows.append({"row": index, "status": "error", "action": "skip", "existingId": "",
                             "item": raw, "groups": [], "error": str(error)})
        bundle = self._bundle(groups, name)
        child_names = {group["name"].casefold() for group in groups if bundle and group["parentId"] == bundle["id"]}
        new_names = sorted(value for key, value in names.items() if key not in child_names)
        if len(groups) + len(new_names) + (0 if bundle else 1) > MAX_GROUPS:
            raise WorkflowError("自定义分组数量已达到上限")
        summary = {status: sum(row["status"] == status for row in rows) for status in ("new", "conflict", "error")}
        if len(current) + summary["new"] > MAX_PROMPTS:
            raise WorkflowError(f"导入后提示词池不能超过 {MAX_PROMPTS} 项")
        return {"model_profile": profile, "mode": mode, "rows": rows, "summary": summary,
                "bundle": {"name": name, "exists": bool(bundle), "newChildCount": len(new_names)},
                "newGroups": {"prompt": new_names} if new_names else {}}

    @serialized_mutation
    async def commit_structured_import(self, model_profile: Any, rows: Any, *, bundle_name: str,
                                       target_group_ids: Any = None, mode: str = "append") -> dict[str, Any]:
        profile, name = self._profile(model_profile), self._group_name(bundle_name)
        current, groups, targets = self._import_state(profile, mode, target_group_ids)
        if not isinstance(rows, list) or len(rows) > MAX_IMPORT_ROWS:
            raise WorkflowError("导入确认数据无效")
        bundle = self._bundle(groups, name)
        by_text = {item["text"].casefold(): index for index, item in enumerate(current)}
        imported, updated, skipped, processed = [], [], 0, set()
        for row in rows:
            if not isinstance(row, dict):
                raise WorkflowError("导入行无效")
            action = row.get("action", "skip")
            if action == "skip":
                skipped += 1
                continue
            if not isinstance(action, str) or action not in {"create", "overwrite"}:
                raise WorkflowError("导入操作无效")
            try:
                normalized = normalize_krea_entry(row.get("item"))
                group_names = normalize_group_names(row.get("groups", normalized["groups"])) or ["未分组"]
            except ValueError as error:
                raise WorkflowError(str(error)) from error
            item = _entry(normalized)
            key = item["text"].casefold()
            if key in processed:
                raise WorkflowError("同一导入确认中存在重复正文")
            processed.add(key)
            index, item_id = by_text.get(key), self.item_id(item["text"])
            if action == "overwrite":
                if index is None or row.get("existingId") != item_id:
                    raise WorkflowError("可覆盖的原条目已变化，请重新预览")
                item["text"] = current[index]["text"]
            elif index is not None:
                raise WorkflowError("导入项已存在，请重新预览")
            if bundle is None:
                bundle = {"id": f"prompt_import_{uuid.uuid4().hex[:12]}", "name": name, "parentId": None, "kind": "import"}
                groups.append(bundle)
            assigned = list(targets)
            for group_name in group_names:
                group = next((group for group in groups if group["parentId"] == bundle["id"] and group["name"].casefold() == group_name.casefold()), None)
                if group is None:
                    group = {"id": f"prompt_group_{uuid.uuid4().hex[:12]}", "name": group_name, "parentId": bundle["id"], "kind": "group"}
                    groups.append(group)
                assigned.append(group["id"])
            item["groupIds"] = list(dict.fromkeys(assigned))
            if index is None:
                by_text[key] = len(current)
                current.append(item)
                imported.append(item_id)
            else:
                current[index] = item
                updated.append(item_id)
        if len(current) > MAX_PROMPTS or len(groups) > MAX_GROUPS:
            raise WorkflowError("导入后提示词或自定义分组数量超过上限")
        await self._save({**self.items, profile: current}, {**self.groups, profile: groups})
        return {"model_profile": profile, "imported": len(imported), "updated": len(updated), "skipped": skipped,
                "imported_ids": imported, "updated_ids": updated, "valid_ids": [self.item_id(item["text"]) for item in current],
                "count": len(current), "groups": self.list_groups(profile)["groups"]}


_CSV_PROMPT_HEADERS = {"text", "prompt", "提示词", "自然语言"}


def raw_prompt_import(value: Any, content_type: str = "") -> list[Any]:
    """Decode transport only; row validation happens separately for useful previews."""
    if isinstance(value, str):
        if len(value.encode("utf-8")) > MAX_IMPORT_BYTES:
            raise WorkflowError("导入文件超过 5 MB")
        text = value.lstrip("\ufeff").strip()
        if not text:
            return []
        hint = str(content_type or "").strip().lower().split(";", 1)[0]
        allowed = {"", "auto", "json", "application/json", "csv", "text/csv", "application/csv", "text", "txt", "text/plain", "plain"}
        if hint not in allowed and not hint.endswith("+json"):
            raise WorkflowError("提示词池导入 format 必须是 text、json 或 csv")
        if hint in {"json", "application/json"} or hint.endswith("+json") or (hint in {"", "auto"} and text[:1] in "[{"):
            try:
                return raw_prompt_import(json.loads(text), "json")
            except json.JSONDecodeError as error:
                raise WorkflowError("提示词池 JSON 无效") from error
        if hint in {"", "auto"}:
            try:
                header = next(csv.reader([text.splitlines()[0]]), [])
            except csv.Error:
                header = []
            hint = "csv" if any(cell.strip().casefold() in _CSV_PROMPT_HEADERS for cell in header) else "text"
        if hint in {"csv", "text/csv", "application/csv"}:
            try:
                rows = list(csv.reader(io.StringIO(text), strict=True))
            except csv.Error as error:
                raise WorkflowError("提示词池 CSV 无效") from error
            if not rows:
                return []
            headers = [cell.strip().casefold() for cell in rows[0]]
            column = next((i for i, name in enumerate(headers) if name in _CSV_PROMPT_HEADERS), None)
            if column is None:
                return [row[0] for row in rows if row]
            result = []
            for row in rows[1:]:
                if not any(cell.strip() for cell in row):
                    continue
                item = {"prompt": row[column] if len(row) > column else ""}
                for field in ("section", "title", "subtitle", "groups"):
                    if field in headers:
                        index = headers.index(field)
                        item[field] = row[index] if len(row) > index else ""
                result.append(item)
            return result
        return text.splitlines()
    if isinstance(value, dict):
        if "text" in value or "prompt" in value:
            return [value]
        unknown = set(value) - {"version", "items", "prompts"}
        if unknown or ("items" in value and "prompts" in value):
            raise WorkflowError("提示词池 JSON 包含未知字段或同时包含 items 和 prompts")
        if "version" in value and (type(value["version"]) is not int or value["version"] != 1):
            raise WorkflowError("提示词池导入格式 version 必须为 1")
        value = value.get("items", value.get("prompts", []))
    if not isinstance(value, list) or len(value) > MAX_PROMPTS:
        raise WorkflowError(f"提示词池 items 必须是数组且不能超过 {MAX_PROMPTS} 项")
    return value


def parse_prompt_records(value: Any, content_type: str = "") -> list[dict[str, Any]]:
    result, seen = [], set()
    for raw in raw_prompt_import(value, content_type):
        if isinstance(raw, str) and not raw.strip():
            continue
        try:
            item = normalize_krea_entry(raw)
        except ValueError as error:
            raise WorkflowError(str(error)) from error
        key = item["prompt"].casefold()
        if key not in seen:
            result.append(item)
            seen.add(key)
    return result


def parse_prompt_import_details(value: Any, content_type: str = "") -> dict[str, Any]:
    raw_values = raw_prompt_import(value, content_type)
    total = 0
    items, seen = [], set()
    for raw in raw_values:
        if isinstance(raw, str) and not raw.strip():
            continue
        try:
            item = normalize_krea_entry(raw)
        except ValueError as error:
            raise WorkflowError(str(error)) from error
        total += 1
        text = item["prompt"]
        if text.casefold() not in seen:
            items.append(text)
            seen.add(text.casefold())
    return {"items": items, "total": total, "unique": len(items), "duplicates": total - len(items)}


def parse_prompt_import(value: Any, content_type: str = "") -> list[str]:
    """Legacy text result; structured consumers use parse_prompt_records."""
    return parse_prompt_import_details(value, content_type)["items"]

