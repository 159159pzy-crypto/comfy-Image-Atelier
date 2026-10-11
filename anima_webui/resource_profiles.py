"""Persistent ownership for model and LoRA resources.

ComfyUI exposes a flat resource inventory, while this application presents
separate Anima and Krea 2 namespaces.  This store records the profile that
owns each relative resource path.  Resources discovered on the first
successful inventory scan are treated as legacy Anima resources (except the
known Krea 2 base UNet); later discoveries stay unassigned until explicitly
claimed.
"""
from __future__ import annotations

import asyncio
import copy
import json
import os
import tempfile
from pathlib import Path
from typing import Any

from .model_profiles import (
    DEFAULT_MODEL_PROFILE,
    ModelProfileError,
    normalize_model_profile,
)
from .persistence import backup_corrupt_file
from .workflow import WorkflowError, normalize_lora_path


RESOURCE_KINDS = ("models", "loras")
STORE_VERSION = 1


class ResourceProfileStore:
    """Store exclusive profile ownership for ComfyUI resource paths."""

    def __init__(self, path: str | Path):
        self.path = Path(path)
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.assignments: dict[str, dict[str, str | None]] = {
            kind: {} for kind in RESOURCE_KINDS
        }
        self.initialized: set[str] = set()
        self.load_warnings: list[str] = []
        self._lock = asyncio.Lock()
        self.reload()

    @staticmethod
    def normalize_kind(value: Any) -> str:
        kind = str(value or "").strip().lower()
        if kind not in RESOURCE_KINDS:
            raise WorkflowError("kind 必须是 models 或 loras")
        return kind

    @staticmethod
    def normalize_name(value: Any) -> str:
        return normalize_lora_path(value)

    @classmethod
    def _profile(cls, value: Any) -> str | None:
        if value is None or value == "":
            return None
        try:
            return normalize_model_profile(value)
        except ModelProfileError as error:
            raise WorkflowError(str(error)) from error

    @classmethod
    def _normalize_assignments(cls, value: Any) -> dict[str, dict[str, str | None]]:
        if not isinstance(value, dict):
            raise WorkflowError("资源归属 assignments 必须是对象")
        result: dict[str, dict[str, str | None]] = {kind: {} for kind in RESOURCE_KINDS}
        for raw_kind, raw_map in value.items():
            kind = cls.normalize_kind(raw_kind)
            if not isinstance(raw_map, dict):
                raise WorkflowError(f"资源归属 {kind} 必须是对象")
            for raw_name, raw_profile in raw_map.items():
                name = cls.normalize_name(raw_name)
                profile = cls._profile(raw_profile)
                # Keep a canonical spelling while making path comparisons
                # case-insensitive on Windows and portable across separators.
                prior = next(
                    (key for key in result[kind] if key.casefold() == name.casefold()),
                    None,
                )
                if prior is not None:
                    del result[kind][prior]
                result[kind][name] = profile
        return result

    def reload(self) -> None:
        self.load_warnings = []
        if not self.path.is_file():
            self.assignments = {kind: {} for kind in RESOURCE_KINDS}
            self.initialized = set()
            return
        try:
            payload = json.loads(self.path.read_text(encoding="utf-8"))
            if not isinstance(payload, dict) or payload.get("version") != STORE_VERSION:
                raise WorkflowError("资源归属文件版本无效")
            assignments = self._normalize_assignments(payload.get("assignments", {}))
            initialized = payload.get("initialized", [])
            if not isinstance(initialized, list) or any(item not in RESOURCE_KINDS for item in initialized):
                raise WorkflowError("资源归属 initialized 内容无效")
        except (OSError, json.JSONDecodeError, WorkflowError) as error:
            backup = backup_corrupt_file(self.path)
            self.load_warnings.append(
                f"资源归属文件无法读取({error}),已备份为 {backup.name} 并以空数据启动"
            )
            self.assignments = {kind: {} for kind in RESOURCE_KINDS}
            self.initialized = set()
            return
        self.assignments = assignments
        self.initialized = set(initialized)

    @staticmethod
    def _legacy_profile(kind: str, name: str) -> str:
        if kind == "models" and name.rsplit("/", 1)[-1].casefold() == "krea2_turbo_int8_convrot.safetensors":
            return "krea2"
        return DEFAULT_MODEL_PROFILE

    def _lookup(self, kind: str, name: str) -> str | None:
        identity = name.casefold()
        for key, profile in self.assignments[kind].items():
            if key.casefold() == identity:
                return profile
        return None

    async def sync_inventory(self, kind: Any, names: Any) -> dict[str, str | None]:
        """Initialize legacy ownership once and return ownership for names.

        New paths do not inherit the legacy default.  Explicit ``None`` entries
        are retained as tombstones so a resource deliberately unassigned by a
        user is not claimed again on a later scan.
        """
        normalized_kind = self.normalize_kind(kind)
        if isinstance(names, (str, bytes)) or not isinstance(names, (list, tuple, set)):
            raise WorkflowError(f"{normalized_kind} inventory 必须是数组")
        normalized_names = list(dict.fromkeys(self.normalize_name(name) for name in names if str(name or "").strip()))
        changed = False
        async with self._lock:
            mapping = self.assignments[normalized_kind]
            if normalized_kind not in self.initialized:
                for name in normalized_names:
                    if self._lookup(normalized_kind, name) is None and not any(
                        key.casefold() == name.casefold() for key in mapping
                    ):
                        mapping[name] = self._legacy_profile(normalized_kind, name)
                self.initialized.add(normalized_kind)
                changed = True
            result = {name: self._lookup(normalized_kind, name) for name in normalized_names}
            if changed:
                await self._save_locked()
        return result

    async def assign(self, kind: Any, name: Any, model_profile: Any) -> dict[str, Any]:
        normalized_kind = self.normalize_kind(kind)
        normalized_name = self.normalize_name(name)
        profile = self._profile(model_profile)
        async with self._lock:
            mapping = self.assignments[normalized_kind]
            existing = next(
                (key for key in mapping if key.casefold() == normalized_name.casefold()),
                None,
            )
            if existing is not None and existing != normalized_name:
                del mapping[existing]
            mapping[normalized_name] = profile
            self.initialized.add(normalized_kind)
            await self._save_locked()
        return {"kind": normalized_kind, "name": normalized_name, "model_profile": profile}

    def ownership(self, kind: Any, names: Any) -> list[dict[str, Any]]:
        normalized_kind = self.normalize_kind(kind)
        result = []
        for raw_name in names:
            name = self.normalize_name(raw_name)
            result.append(
                {
                    "name": name,
                    "folder": name.rsplit("/", 1)[0] if "/" in name else "",
                    "basename": name.rsplit("/", 1)[-1],
                    "model_profile": self._lookup(normalized_kind, name),
                }
            )
        return result

    def snapshot(self) -> dict[str, Any]:
        return {
            "version": STORE_VERSION,
            "initialized": sorted(self.initialized),
            "assignments": copy.deepcopy(self.assignments),
        }

    async def _save_locked(self) -> None:
        text = json.dumps(self.snapshot(), ensure_ascii=False, indent=2) + "\n"
        await asyncio.to_thread(self._write_text, text)

    def _write_text(self, text: str) -> None:
        fd, temp_name = tempfile.mkstemp(
            prefix="resource-profiles-", suffix=".json", dir=self.path.parent
        )
        try:
            with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as handle:
                handle.write(text)
                handle.flush()
                os.fsync(handle.fileno())
            os.replace(temp_name, self.path)
        finally:
            if os.path.exists(temp_name):
                os.unlink(temp_name)
