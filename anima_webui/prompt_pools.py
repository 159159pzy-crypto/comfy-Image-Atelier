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
from typing import Any, Iterable

from .model_profiles import DEFAULT_MODEL_PROFILE, ModelProfileError, normalize_model_profile
from .persistence import backup_corrupt_file, backup_for_migration
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


class PromptPoolStore:
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
                backup = backup_for_migration(self.path, 2)
                if backup:
                    self.load_warnings.append(f"提示词池已迁移,原文件备份为 {backup.name}")
            if not isinstance(raw, dict):
                raise WorkflowError("自然语言提示词池内容无效")
            profiles: dict[str, list[str]] = {}
            for profile, values in raw.items():
                profiles[self._profile(profile)] = normalize_prompt_items(values)
            self.items = profiles
            self.items.setdefault(DEFAULT_MODEL_PROFILE, [])
            if migrated_legacy:
                # Persist the profile namespace immediately.  Otherwise a
                # restart would repeat the migration and leave the user's
                # file in the v1 shape indefinitely.
                self._write_text(
                    json.dumps(
                        {"version": 2, "profiles": self.items},
                        ensure_ascii=False,
                        indent=2,
                    )
                    + "\n"
                )
        except (OSError, json.JSONDecodeError, WorkflowError) as error:
            backup = backup_corrupt_file(self.path)
            self.load_warnings.append(
                f"自然语言提示词池文件无法读取({error}),已备份为 {backup.name} 并以空数据启动"
            )
            self.items = {DEFAULT_MODEL_PROFILE: []}

    def list(
        self,
        model_profile: Any = DEFAULT_MODEL_PROFILE,
        *,
        query: str = "",
        page: int = 1,
        limit: int = 48,
        selected_ids: list[str] | None = None,
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
        total = len(values)
        start = (page - 1) * limit
        visible = values[start : start + limit]
        selected = set(selected_ids or [])
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

    async def import_items(
        self,
        model_profile: Any,
        values: Any,
        *,
        mode: str = "replace",
    ) -> dict[str, Any]:
        profile = self._profile(model_profile)
        incoming = normalize_prompt_items(values)
        if mode not in {"replace", "append"}:
            raise WorkflowError("提示词池导入 mode 必须是 replace 或 append")
        async with self._lock:
            if mode == "append":
                incoming = normalize_prompt_items([*self.items.get(profile, []), *incoming])
            self.items[profile] = incoming
            await self._save()
        return self.list(profile)

    async def clear(self, model_profile: Any = DEFAULT_MODEL_PROFILE) -> dict[str, Any]:
        return await self.import_items(model_profile, [], mode="replace")

    async def delete_item(self, model_profile: Any, item_id: Any) -> dict[str, Any]:
        profile = self._profile(model_profile)
        target = str(item_id or "").strip()
        if not target:
            raise WorkflowError("提示词池 item_id 不能为空")
        async with self._lock:
            values = self.items.get(profile, [])
            remaining = [text for text in values if self.item_id(text) != target]
            if len(remaining) == len(values):
                raise KeyError(target)
            self.items[profile] = remaining
            await self._save()
        return self.list(profile)

    async def _save(self) -> None:
        payload = json.dumps({"version": 2, "profiles": self.items}, ensure_ascii=False, indent=2) + "\n"
        await asyncio.to_thread(self._write_text, payload)

    def _write_text(self, text: str) -> None:
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


def parse_prompt_import(value: Any, content_type: str = "") -> list[str]:
    """Parse JSON array/object or CSV text used by the import endpoint."""
    if isinstance(value, list):
        return normalize_prompt_items(value)
    if isinstance(value, dict):
        return normalize_prompt_items(value.get("items", []))
    if not isinstance(value, str):
        raise WorkflowError("提示词池导入内容无效")
    text = value.strip()
    if not text:
        return []
    if "json" in content_type.lower() or text[:1] in "[{":
        try:
            return parse_prompt_import(json.loads(text), "application/json")
        except json.JSONDecodeError as error:
            raise WorkflowError("提示词池 JSON 无效") from error
    rows = csv.reader(io.StringIO(text))
    values: list[str] = []
    for row in rows:
        values.extend(row[:1])
    return normalize_prompt_items(values)

