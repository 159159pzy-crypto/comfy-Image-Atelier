"""Profile-isolated natural-language prompt pools for Krea 2."""
from __future__ import annotations

import asyncio
import copy
import csv
import io
import json
import os
import tempfile
import hashlib
from pathlib import Path
from typing import Any

from .async_utils import serialized_mutation
from .model_profiles import DEFAULT_MODEL_PROFILE, ModelProfileError, normalize_model_profile
from .persistence import StorageProtection, backup_for_migration
from .workflow import WorkflowError

MAX_PROMPTS = 50_000
MAX_PROMPT_LENGTH = 20_000


def normalize_prompt_items(value: Any) -> list[str]:
    if not isinstance(value, list):
        raise WorkflowError("提示词池 items 必须是数组")
    if len(value) > MAX_PROMPTS:
        raise WorkflowError(f"提示词池不能超过 {MAX_PROMPTS} 项")
    result: list[str] = []
    seen: set[str] = set()
    for index, raw in enumerate(value):
        if not isinstance(raw, str):
            raise WorkflowError(f"提示词池 items[{index}] 必须是字符串")
        text = raw.strip()
        if len(text) > MAX_PROMPT_LENGTH:
            raise WorkflowError(f"提示词池 items[{index}] 不能超过 {MAX_PROMPT_LENGTH} 个字符")
        identity = text.casefold()
        if text and identity not in seen:
            result.append(text)
            seen.add(identity)
    return result


class PromptPoolStore(StorageProtection):
    store_name = "prompt_pools"

    def __init__(self, path: str | Path):
        self.path = Path(path)
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.items: dict[str, list[str]] = {}
        self.load_warnings: list[str] = []
        self._lock = asyncio.Lock()
        self.reload()

    @staticmethod
    def _profile(value: Any) -> str:
        try:
            return normalize_model_profile(value or DEFAULT_MODEL_PROFILE)
        except ModelProfileError as error:
            raise WorkflowError(str(error)) from error

    def reload(self) -> None:
        self.load_warnings = []
        self.read_only_reason = None
        if not self.path.is_file():
            self.items = {DEFAULT_MODEL_PROFILE: []}
            return
        try:
            payload = json.loads(self.path.read_text(encoding="utf-8"))
            if not isinstance(payload, dict) or payload.get("version") not in {1, 2}:
                raise WorkflowError("自然语言提示词池文件版本无效")
            raw = payload.get("profiles", payload.get("items", {}))
            migrated_legacy = payload.get("version") == 1 and isinstance(raw, list)
            if migrated_legacy:
                raw = {DEFAULT_MODEL_PROFILE: raw}
            if not isinstance(raw, dict):
                raise WorkflowError("自然语言提示词池内容无效")
            profiles: dict[str, list[str]] = {}
            for profile, values in raw.items():
                profiles[self._profile(profile)] = normalize_prompt_items(values)
            self.items = profiles
            self.items.setdefault(DEFAULT_MODEL_PROFILE, [])
        except (OSError, UnicodeError, json.JSONDecodeError, WorkflowError) as error:
            self._recover_load_failure(error)
            self.items = {DEFAULT_MODEL_PROFILE: []}
            return
        if migrated_legacy:
            # Migration I/O is separate from parsing: a backup/write failure
            # must not quarantine valid source data or empty the usable view.
            try:
                backup = backup_for_migration(self.path, 2)
                if backup:
                    self.load_warnings.append(f"提示词池已迁移,原文件备份为 {backup.name}")
                self._write_text(
                    json.dumps(
                        {"version": 2, "profiles": self.items},
                        ensure_ascii=False,
                        indent=2,
                    )
                    + "\n"
                )
            except OSError as error:
                self._protect_storage(f"提示词池已在内存迁移，但迁移备份或写回失败({error})")

    def list(
        self,
        model_profile: Any = DEFAULT_MODEL_PROFILE,
        *,
        query: str = "",
        page: int = 1,
        limit: int = 48,
        selected_ids: list[str] | None = None,
        selected_only: bool = False,
    ) -> dict[str, Any]:
        profile = self._profile(model_profile)
        try:
            page = int(page)
            limit = int(limit)
        except (TypeError, ValueError) as error:
            raise WorkflowError("提示词池分页参数无效") from error
        if page < 1 or limit < 1 or limit > 500:
            raise WorkflowError("提示词池分页参数无效")
        needle = str(query or "").strip().casefold()
        values = [
            text
            for text in self.items.get(profile, [])
            if not needle or needle in text.casefold()
        ]
        selected = set(selected_ids or [])
        selected_count = sum(1 for text in values if self.item_id(text) in selected)
        pool_count = len(self.items.get(profile, []))
        if selected_only:
            values = [text for text in values if self.item_id(text) in selected]
        total = len(values)
        start = (page - 1) * limit
        visible = values[start : start + limit]
        return {
            "model_profile": profile,
            "items": [
                {
                    "id": self.item_id(text),
                    "text": text,
                    "selected": self.item_id(text) in selected,
                }
                for text in copy.deepcopy(visible)
            ],
            "count": total,
            "total": total,
            "pool_count": pool_count,
            "selected_count": selected_count,
            "page": page,
            "limit": limit,
            "pages": (total + limit - 1) // limit if total else 0,
        }

    @staticmethod
    def item_id(text: str) -> str:
        return "prompt_" + hashlib.sha256(text.casefold().encode("utf-8")).hexdigest()[:20]

    def resolve(self, model_profile: Any, selected_ids: list[str] | None = None) -> list[str]:
        profile = self._profile(model_profile)
        values = self.items.get(profile, [])
        if selected_ids:
            selected = set(selected_ids)
            return [text for text in values if self.item_id(text) in selected]
        return copy.deepcopy(values)

    def preview_import(
        self,
        model_profile: Any,
        parsed: dict[str, Any],
        *,
        mode: str = "replace",
    ) -> dict[str, Any]:
        profile = self._profile(model_profile)
        mode = self._normalize_import_mode(mode)
        items = normalize_prompt_items(parsed.get("items"))
        current = {text.casefold() for text in self.items.get(profile, [])}
        existing_duplicates = sum(1 for text in items if text.casefold() in current)
        unique = len(items)
        importable = unique if mode == "replace" else unique - existing_duplicates
        if mode == "append" and len(current) + importable > MAX_PROMPTS:
            raise WorkflowError(f"追加后提示词池不能超过 {MAX_PROMPTS} 项")
        total = int(parsed.get("total", unique))
        duplicates = int(parsed.get("duplicates", total - unique))
        return {
            "model_profile": profile,
            "mode": mode,
            "items": items,
            "summary": {
                "total": total,
                "unique": unique,
                "duplicates": duplicates,
                "existingDuplicates": existing_duplicates,
                "new": importable,
            },
        }

    @serialized_mutation
    async def import_items(
        self,
        model_profile: Any,
        values: Any,
        *,
        mode: str = "replace",
    ) -> dict[str, Any]:
        profile = self._profile(model_profile)
        incoming = normalize_prompt_items(values)
        mode = self._normalize_import_mode(mode)
        self.ensure_writable()
        if mode == "append":
            existing = self.items.get(profile, [])
            existing_identities = {text.casefold() for text in existing}
            # Only report entries that actually extend the pool.  Existing
            # case-insensitive duplicates are skipped by append and should
            # not be newly selected by the caller.
            imported_ids = []
            for text in incoming:
                identity = text.casefold()
                if identity not in existing_identities:
                    imported_ids.append(self.item_id(text))
                    existing_identities.add(identity)
            incoming = normalize_prompt_items([*existing, *incoming])
        else:
            imported_ids = [self.item_id(text) for text in incoming]
        profiles = {key: list(values) for key, values in self.items.items()}
        profiles[profile] = incoming
        await self._save(profiles)
        result = self.list(profile)
        result["imported_ids"] = imported_ids
        return result

    @staticmethod
    def _normalize_import_mode(mode: Any) -> str:
        if not isinstance(mode, str) or mode not in {"replace", "append"}:
            raise WorkflowError("提示词池导入 mode 必须是 replace 或 append")
        return mode

    @serialized_mutation
    async def create_item(self, model_profile: Any, text: Any) -> dict[str, Any]:
        profile = self._profile(model_profile)
        value = self._normalize_single_text(text)
        self.ensure_writable()
        values = self.items.get(profile, [])
        if len(values) >= MAX_PROMPTS:
            raise WorkflowError(f"提示词池不能超过 {MAX_PROMPTS} 项")
        if any(item.casefold() == value.casefold() for item in values):
            raise WorkflowError("提示词池中已存在相同文本")
        profiles = {key: list(items) for key, items in self.items.items()}
        profiles[profile] = [*values, value]
        await self._save(profiles)
        return {
            "model_profile": profile,
            "id": self.item_id(value),
            "text": value,
            "count": len(profiles[profile]),
        }

    @serialized_mutation
    async def ensure_item(self, model_profile: Any, text: Any) -> dict[str, Any]:
        """Reuse the pool's text identity without a racy list-then-create call."""
        profile = self._profile(model_profile)
        value = self._normalize_single_text(text)
        values = self.items.get(profile, [])
        existing = next((item for item in values if item.casefold() == value.casefold()), None)
        if existing is not None:
            return {
                "model_profile": profile, "id": self.item_id(existing),
                "text": existing, "created": False, "count": len(values),
            }
        self.ensure_writable()
        if len(values) >= MAX_PROMPTS:
            raise WorkflowError(f"提示词池不能超过 {MAX_PROMPTS} 项")
        profiles = {key: list(items) for key, items in self.items.items()}
        profiles[profile] = [*values, value]
        await self._save(profiles)
        return {
            "model_profile": profile, "id": self.item_id(value),
            "text": value, "created": True, "count": len(profiles[profile]),
        }

    @serialized_mutation
    async def update_item(
        self, model_profile: Any, item_id: Any, text: Any
    ) -> dict[str, Any]:
        profile = self._profile(model_profile)
        target = str(item_id or "").strip()
        if not target:
            raise WorkflowError("提示词池 item_id 不能为空")
        value = self._normalize_single_text(text)
        self.ensure_writable()
        values = self.items.get(profile, [])
        index = next(
            (i for i, existing in enumerate(values) if self.item_id(existing) == target),
            None,
        )
        if index is None:
            raise KeyError(target)
        if any(
            i != index and existing.casefold() == value.casefold()
            for i, existing in enumerate(values)
        ):
            raise WorkflowError("提示词池中已存在相同文本")
        updated = list(values)
        updated[index] = value
        profiles = {key: list(items) for key, items in self.items.items()}
        profiles[profile] = updated
        await self._save(profiles)
        return {
            "model_profile": profile,
            "id": self.item_id(value),
            "text": value,
            "count": len(updated),
        }

    @staticmethod
    def _normalize_single_text(value: Any) -> str:
        if not isinstance(value, str):
            raise WorkflowError("提示词池 text 必须是字符串")
        normalized = normalize_prompt_items([value])
        if not normalized:
            raise WorkflowError("提示词池 text 不能为空")
        return normalized[0]

    async def clear(self, model_profile: Any = DEFAULT_MODEL_PROFILE) -> dict[str, Any]:
        return await self.import_items(model_profile, [], mode="replace")

    @serialized_mutation
    async def delete_item(self, model_profile: Any, item_id: Any) -> dict[str, Any]:
        profile = self._profile(model_profile)
        target = str(item_id or "").strip()
        if not target:
            raise WorkflowError("提示词池 item_id 不能为空")
        self.ensure_writable()
        values = self.items.get(profile, [])
        remaining = [text for text in values if self.item_id(text) != target]
        if len(remaining) == len(values):
            raise KeyError(target)
        profiles = {key: list(items) for key, items in self.items.items()}
        profiles[profile] = remaining
        await self._save(profiles)
        return self.list(profile)

    @serialized_mutation
    async def delete_items(self, model_profile: Any, item_ids: Any) -> dict[str, Any]:
        profile = self._profile(model_profile)
        if not isinstance(item_ids, list):
            raise WorkflowError("提示词池 ids 必须是数组")
        if len(item_ids) > MAX_PROMPTS:
            raise WorkflowError(f"提示词池一次最多删除 {MAX_PROMPTS} 项")
        targets: set[str] = set()
        for index, item_id in enumerate(item_ids):
            if not isinstance(item_id, str) or not item_id.strip():
                raise WorkflowError(f"提示词池 ids[{index}] 必须是非空字符串")
            targets.add(item_id.strip())
        self.ensure_writable()
        values = self.items.get(profile, [])
        deleted = [self.item_id(text) for text in values if self.item_id(text) in targets]
        if deleted:
            remaining = [text for text in values if self.item_id(text) not in targets]
            profiles = {key: list(items) for key, items in self.items.items()}
            profiles[profile] = remaining
            await self._save(profiles)
        count = len(self.items.get(profile, []))
        return {"model_profile": profile, "deleted": deleted, "count": count}

    async def _save(self, profiles: dict[str, list[str]]) -> None:
        self.ensure_writable()
        payload = json.dumps(
            {"version": 2, "profiles": profiles}, ensure_ascii=False, indent=2
        ) + "\n"
        await asyncio.to_thread(self._write_text, payload)
        self.items = profiles

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


_PROMPT_OBJECT_FIELDS = {"text", "prompt"}
_PROMPT_IMPORT_FIELDS = {"items", "prompts"}
_CSV_PROMPT_HEADERS = {"text", "prompt", "提示词", "自然语言"}


def _prompt_import_values(value: Any, content_type: str = "") -> list[str]:
    """Extract raw prompt strings from the supported JSON, CSV, or text shapes."""
    if isinstance(value, str):
        text = value.lstrip("\ufeff").strip()
        if not text:
            return []
        hint = str(content_type or "").strip().lower().split(";", 1)[0]
        allowed_hints = {
            "",
            "auto",
            "json",
            "application/json",
            "csv",
            "text/csv",
            "application/csv",
            "text",
            "txt",
            "text/plain",
            "plain",
        }
        if hint not in allowed_hints and not hint.endswith("+json"):
            raise WorkflowError("提示词池导入 format 必须是 text、json 或 csv")
        wants_json = hint in {"json", "application/json"} or hint.endswith("+json")
        wants_csv = hint in {"csv", "text/csv", "application/csv"}
        wants_text = hint in {"text", "txt", "text/plain", "plain"}
        auto_format = hint in {"", "auto"}
        if wants_json or (auto_format and text[:1] in "[{"):
            try:
                return _prompt_import_values(json.loads(text), "application/json")
            except json.JSONDecodeError as error:
                raise WorkflowError("提示词池 JSON 无效") from error
        if wants_csv:
            try:
                rows = list(csv.reader(io.StringIO(text)))
            except csv.Error as error:
                raise WorkflowError("提示词池 CSV 无效") from error
            if not rows:
                return []
            headers = [cell.strip().casefold() for cell in rows[0]]
            prompt_column = next(
                (index for index, name in enumerate(headers) if name in _CSV_PROMPT_HEADERS),
                None,
            )
            start = 1 if prompt_column is not None else 0
            column = prompt_column if prompt_column is not None else 0
            return [row[column] for row in rows[start:] if len(row) > column]
        # Auto detection intentionally matches the browser: JSON is detected
        # from its opening token, CSV from a recognized prompt header, and
        # everything else is one prompt per line.  This preserves commas in
        # natural-language prompts; headerless CSV remains available by
        # explicitly selecting the CSV format.
        if auto_format:
            first_line = text.splitlines()[0] if text.splitlines() else ""
            try:
                first_row = next(csv.reader([first_line]), [])
            except csv.Error:
                first_row = []
            if any(cell.strip().casefold() in _CSV_PROMPT_HEADERS for cell in first_row):
                return _prompt_import_values(text, "text/csv")
            return text.splitlines()
        if wants_text:
            return text.splitlines()
        try:
            rows = list(csv.reader(io.StringIO(text)))
        except csv.Error as error:
            raise WorkflowError("提示词池 CSV 无效") from error
        return [row[0] for row in rows if row]

    if isinstance(value, list):
        if len(value) > MAX_PROMPTS:
            raise WorkflowError(f"提示词池不能超过 {MAX_PROMPTS} 项")
        result: list[str] = []
        for index, item in enumerate(value):
            if isinstance(item, str):
                result.append(item)
            elif isinstance(item, dict):
                unknown = set(item) - _PROMPT_OBJECT_FIELDS
                if unknown:
                    raise WorkflowError(
                        f"提示词池 items[{index}] 包含未知字段: {', '.join(sorted(unknown))}"
                    )
                if "text" in item and "prompt" in item:
                    raise WorkflowError(f"提示词池 items[{index}] 不能同时包含 text 和 prompt")
                field = "text" if "text" in item else "prompt"
                prompt = item.get(field, "")
                if not isinstance(prompt, str):
                    raise WorkflowError(f"提示词池 items[{index}].{field} 必须是字符串")
                result.append(prompt)
            else:
                raise WorkflowError(f"提示词池 items[{index}] 必须是字符串或提示词对象")
        return result

    if isinstance(value, dict):
        if set(value) & _PROMPT_OBJECT_FIELDS:
            unknown = set(value) - _PROMPT_OBJECT_FIELDS
            if unknown:
                raise WorkflowError(
                    f"提示词池 JSON 对象包含未知字段: {', '.join(sorted(unknown))}"
                )
            if "text" in value and "prompt" in value:
                raise WorkflowError("提示词池 JSON 对象不能同时包含 text 和 prompt")
            field = "text" if "text" in value else "prompt"
            prompt = value.get(field, "")
            if not isinstance(prompt, str):
                raise WorkflowError(f"提示词池 JSON 对象 {field} 必须是字符串")
            return [prompt]
        unknown = set(value) - _PROMPT_IMPORT_FIELDS
        if unknown:
            raise WorkflowError(f"提示词池 JSON 包含未知字段: {', '.join(sorted(unknown))}")
        if "items" in value and "prompts" in value:
            raise WorkflowError("提示词池 JSON 不能同时包含 items 和 prompts")
        field = "items" if "items" in value else "prompts"
        values = value.get(field, [])
        if not isinstance(values, list):
            raise WorkflowError(f"提示词池 JSON {field} 必须是数组")
        return _prompt_import_values(values)

    raise WorkflowError("提示词池导入内容无效")


def parse_prompt_import_details(value: Any, content_type: str = "") -> dict[str, Any]:
    """Parse input once and retain duplicate counts for the import preview."""
    raw_values = _prompt_import_values(value, content_type)
    if len(raw_values) > MAX_PROMPTS:
        raise WorkflowError(f"提示词池不能超过 {MAX_PROMPTS} 项")
    items: list[str] = []
    seen: set[str] = set()
    total = 0
    for index, raw in enumerate(raw_values):
        text = raw.strip()
        if len(text) > MAX_PROMPT_LENGTH:
            raise WorkflowError(
                f"提示词池 items[{index}] 不能超过 {MAX_PROMPT_LENGTH} 个字符"
            )
        if not text:
            continue
        total += 1
        identity = text.casefold()
        if identity not in seen:
            items.append(text)
            seen.add(identity)
    return {
        "items": items,
        "total": total,
        "unique": len(items),
        "duplicates": total - len(items),
    }


def parse_prompt_import(value: Any, content_type: str = "") -> list[str]:
    """Parse JSON array/object, CSV, or one-prompt-per-line text."""
    return parse_prompt_import_details(value, content_type)["items"]

