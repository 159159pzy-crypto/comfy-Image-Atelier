"""Ownership and idle cleanup for imported images and verified ComfyUI copies.

The history database is the authority for timestamps, leases and file ownership.
An unavailable history view must never be interpreted as an empty reference set.
"""
from __future__ import annotations

import asyncio
import hashlib
import json
import logging
import os
import re
import sqlite3
import time
from contextlib import asynccontextmanager, closing
from pathlib import Path
from typing import Any

from . import image_location
from .persistence import StorageReadOnlyError
from .workflow import WorkflowError


logger = logging.getLogger(__name__)
ASSET_ID = re.compile(r"^[a-f0-9]{64}$")
SESSION_ID = re.compile(r"^[A-Za-z0-9_-]{8,128}$")
COMFY_NAME = re.compile(r"^(?:reverse-)?[a-f0-9]{64}\.png$")
DAY = 86400
STARTUP_GRACE = 300
INTERVAL = 3600
LEASE_SECONDS = 600


class AssetLifecycle:
    """Serialize cleanup against file access and manager preflight/dispatch."""

    def __init__(self, history: Any, input_images: Any, prompt_images: Any,
                 comfy: Any, manager: Any):
        self.history = history
        self.stores = {"input": input_images, "prompt": prompt_images}
        self.comfy = comfy
        self.manager = manager
        self._lock = asyncio.Lock()
        self._task: asyncio.Task | None = None
        self._workers: set[asyncio.Task] = set()
        self._initialized = False
        self._initialization_error = ""
        self._closed = False

    def _writable(self) -> None:
        self.history.ensure_writable()
        if self._initialization_error:
            raise WorkflowError(self._initialization_error)
        if not getattr(self.history, "available", True):
            raise WorkflowError("历史存储不可用，已停止图片清理")

    async def _thread(self, function: Any, *args: Any, **kwargs: Any) -> Any:
        """A cancelled HTTP/task caller cannot release a running file worker."""
        worker = asyncio.create_task(asyncio.to_thread(function, *args, **kwargs))
        self._workers.add(worker)
        try:
            return await asyncio.shield(worker)
        except asyncio.CancelledError:
            # Repeated cancellation must not unlock the file while Pillow or
            # atomic writes are still running in an uncancellable OS thread.
            while not worker.done():
                try:
                    await asyncio.shield(worker)
                except asyncio.CancelledError:
                    continue
                except Exception:
                    break
            if worker.done() and not worker.cancelled():
                worker.exception()
            raise
        finally:
            self._workers.discard(worker)

    def _schema(self) -> None:
        self._writable()
        connection = self.history.connection
        present = connection.execute(
            "SELECT 1 FROM sqlite_master WHERE type='table' AND name='managed_image_files'"
        ).fetchone()
        if not present:
            row = connection.execute("PRAGMA database_list").fetchone()
            if row and row[2]:
                destination = Path(str(row[2]) + ".pre-image-cleanup.bak")
                number = 2
                while destination.exists():
                    destination = Path(str(row[2]) + f".pre-image-cleanup-{number}.bak")
                    number += 1
                # Reserve without overwriting another process's backup.
                with destination.open("xb"):
                    pass
                with closing(sqlite3.connect(destination)) as backup:
                    connection.backup(backup)
        connection.executescript("""
            CREATE TABLE IF NOT EXISTS managed_image_files (
                kind TEXT NOT NULL, asset_id TEXT NOT NULL, root TEXT NOT NULL,
                relative_path TEXT NOT NULL, sha256 TEXT NOT NULL,
                size INTEGER NOT NULL, created_at REAL NOT NULL,
                last_used_at REAL NOT NULL, origin TEXT NOT NULL,
                PRIMARY KEY(kind, asset_id, root)
            );
            CREATE TABLE IF NOT EXISTS image_draft_leases (
                session_id TEXT NOT NULL, kind TEXT NOT NULL, asset_id TEXT NOT NULL,
                expires_at REAL NOT NULL, PRIMARY KEY(session_id,kind,asset_id)
            );
            CREATE TABLE IF NOT EXISTS image_cleanup_settings (
                id INTEGER PRIMARY KEY CHECK(id=1), enabled INTEGER NOT NULL,
                retention_days INTEGER NOT NULL, last_run TEXT
            );
            INSERT OR IGNORE INTO image_cleanup_settings(id,enabled,retention_days)
                VALUES(1,1,7);
            CREATE INDEX IF NOT EXISTS idx_managed_images_idle
                ON managed_image_files(last_used_at);
        """)
        connection.commit()

    async def initialize(self) -> None:
        async with self._lock:
            if self._initialized or self._initialization_error:
                return
            if getattr(self.history, "read_only_reason", None) or not getattr(self.history, "available", True):
                return
            try:
                await self.history._call(self._schema)
            except (OSError, sqlite3.Error, WorkflowError, StorageReadOnlyError) as error:
                self._initialization_error = f"图片清理存储无法初始化，已停止清理：{error}"
                logger.warning(self._initialization_error)
                return
            # Old data gets a full first-upgrade grace. New discoveries also
            # receive grace instead of being removed based on an old mtime.
            for kind, store in self.stores.items():
                for metadata in await self._thread(lambda s=store: list(s.path.glob("*.json"))):
                    asset_id = metadata.stem
                    if not ASSET_ID.fullmatch(asset_id):
                        continue
                    try:
                        await self._register_local(kind, asset_id, touch=False, origin="legacy")
                    except (sqlite3.Error, StorageReadOnlyError) as error:
                        self._initialization_error = f"图片清理登记不可用，已停止清理：{error}"
                        logger.warning(self._initialization_error)
                        return
                    except (KeyError, OSError, ValueError, WorkflowError):
                        # Damaged/unknown files are not evidence of ownership.
                        continue
            self._initialized = True

    def start(self) -> None:
        if not self._closed and self._task is None:
            self._task = asyncio.create_task(self._periodic())

    async def _periodic(self) -> None:
        await asyncio.sleep(STARTUP_GRACE)
        while not self._closed:
            try:
                settings = await self.settings()
                if settings["enabled"] and not settings["read_only"]:
                    await self.run()
            except asyncio.CancelledError:
                raise
            except Exception:
                logger.exception("导入图片自动清理失败")
            await asyncio.sleep(INTERVAL)

    async def close(self) -> None:
        self._closed = True
        if self._task is not None:
            self._task.cancel()
            await asyncio.gather(self._task, return_exceptions=True)
            self._task = None
        # Caller must close lifecycle before shutting down manager/database.
        if self._workers:
            await asyncio.gather(*self._workers, return_exceptions=True)

    def _store(self, kind: str, asset_id: str | None = None) -> Any:
        if kind not in self.stores or asset_id is not None and (
            not isinstance(asset_id, str) or not ASSET_ID.fullmatch(asset_id)
        ):
            raise WorkflowError("导入图片类型或 ID 无效")
        return self.stores[kind]

    @staticmethod
    def _digest(path: Path) -> str:
        with path.open("rb") as stream:
            return hashlib.file_digest(stream, "sha256").hexdigest()

    def _upsert(self, kind: str, asset_id: str, root: str, relative_path: str,
                sha256: str, size: int, created_at: float, now: float,
                origin: str, touch: bool = True) -> None:
        self._writable()
        self.history.connection.execute("""
            INSERT INTO managed_image_files
                (kind,asset_id,root,relative_path,sha256,size,created_at,last_used_at,origin)
            VALUES(?,?,?,?,?,?,?,?,?)
            ON CONFLICT(kind,asset_id,root) DO UPDATE SET
                relative_path=CASE WHEN ? THEN excluded.relative_path ELSE managed_image_files.relative_path END,
                sha256=CASE WHEN ? THEN excluded.sha256 ELSE managed_image_files.sha256 END,
                size=CASE WHEN ? THEN excluded.size ELSE managed_image_files.size END,
                last_used_at=CASE WHEN ? THEN excluded.last_used_at ELSE managed_image_files.last_used_at END
        """, (kind, asset_id, root, relative_path, sha256, size, created_at, now, origin,
              int(origin == "upload"), int(origin == "upload"), int(origin == "upload"), int(touch)))
        self.history.connection.commit()

    async def _register_local(self, kind: str, asset_id: str, *, touch: bool = True,
                              origin: str = "access") -> dict[str, Any]:
        store = self._store(kind, asset_id)

        def inspect() -> tuple[dict[str, Any], str, int, float]:
            asset = store.get(asset_id)
            path = store.path / f"{asset_id}.png"
            root = store.path.resolve(strict=True)
            if path.is_symlink() or not path.resolve(strict=True).is_relative_to(root):
                raise WorkflowError("导入图片路径超出受管目录")
            metadata = store.path / f"{asset_id}.json"
            if metadata.is_symlink() or not metadata.resolve(strict=True).is_relative_to(root):
                raise WorkflowError("导入图片记录路径超出受管目录")
            return asset, self._digest(path), path.stat().st_size + metadata.stat().st_size, path.stat().st_mtime

        asset, digest, size, created_at = await self._thread(inspect)
        await self.history._call(self._upsert, kind, asset_id, str(store.path.resolve()),
                                 f"{asset_id}.png", digest, size, created_at, time.time(), origin, touch)
        return asset

    async def add(self, kind: str, body: bytes, *, filename: str = "image",
                  source_image_id: int | None = None) -> dict[str, Any]:
        await self.initialize()
        # Finish registration even if the HTTP request disconnects after the
        # decoder has saved its files. Otherwise that upload becomes an orphan.
        operation = asyncio.create_task(self._add(kind, body, filename, source_image_id))
        try:
            return await asyncio.shield(operation)
        except asyncio.CancelledError:
            while not operation.done():
                try:
                    await asyncio.shield(operation)
                except asyncio.CancelledError:
                    continue
                except Exception:
                    break
            if operation.done() and not operation.cancelled():
                operation.exception()
            raise

    async def _add(self, kind: str, body: bytes, filename: str, source_image_id: int | None) -> dict[str, Any]:
        async with self._lock:
            self._writable()
            store = self._store(kind)
            kwargs = {"filename": filename}
            if kind == "input":
                kwargs["source_image_id"] = source_image_id
            asset = await self._thread(store.add, body, **kwargs)
            await self._register_local(kind, asset["id"], origin="upload")
            return asset

    async def touch(self, kind: str, asset_id: str) -> dict[str, Any]:
        await self.initialize()
        async with self._lock:
            self._writable()
            return await self._register_local(kind, asset_id)

    async def read(self, kind: str, asset_id: str, *, content: bool = False) -> Any:
        await self.initialize()
        async with self._lock:
            store = self._store(kind, asset_id)
            # Preserve read access during storage protection; cleanup is off.
            if self._initialization_error or getattr(self.history, "read_only_reason", None) or not getattr(self.history, "available", True):
                asset = await self._thread(store.get, asset_id)
            else:
                asset = await self._register_local(kind, asset_id)
            return store.path / f"{asset_id}.png" if content else asset

    async def read_bytes(self, kind: str, asset_id: str) -> bytes:
        await self.initialize()
        async with self._lock:
            store = self._store(kind, asset_id)
            if self._initialization_error or getattr(self.history, "read_only_reason", None) or not getattr(self.history, "available", True):
                await self._thread(store.get, asset_id)
            else:
                await self._register_local(kind, asset_id)
            return await self._thread((store.path / f"{asset_id}.png").read_bytes)

    async def prepare(self, asset_id: str, width: int, height: int, fit: str) -> bytes:
        await self.initialize()
        async with self._lock:
            await self._register_local("input", asset_id)
            return await self._thread(self.stores["input"].prepare, asset_id, width, height, fit)

    async def lease(self, session_id: str, assets: list[dict[str, str]]) -> dict[str, Any]:
        if not isinstance(session_id, str) or not SESSION_ID.fullmatch(session_id):
            raise WorkflowError("图片草稿会话 ID 无效")
        if not isinstance(assets, list) or len(assets) > 128:
            raise WorkflowError("图片草稿引用最多 128 项")
        references = set()
        for value in assets:
            if not isinstance(value, dict) or set(value) != {"kind", "id"}:
                raise WorkflowError("图片草稿引用必须包含 kind 和 id")
            self._store(value["kind"], value["id"])
            references.add((value["kind"], value["id"]))
        await self.initialize()
        async with self._lock:
            self._writable()
            accepted = {"input_image_ids": [], "prompt_image_ids": []}
            missing = {"input_image_ids": [], "prompt_image_ids": []}
            valid = []
            for kind, asset_id in sorted(references):
                key = f"{kind}_image_ids"
                try:
                    await self._register_local(kind, asset_id)
                except KeyError:
                    missing[key].append(asset_id)
                else:
                    accepted[key].append(asset_id)
                    valid.append((kind, asset_id))
            expires_at = time.time() + LEASE_SECONDS

            def replace() -> None:
                self._writable()
                connection = self.history.connection
                with connection:
                    connection.execute("DELETE FROM image_draft_leases WHERE session_id=?", (session_id,))
                    connection.executemany("INSERT INTO image_draft_leases VALUES(?,?,?,?)",
                                           [(session_id, kind, asset_id, expires_at) for kind, asset_id in valid])

            await self.history._call(replace)
            return {"expires_at": expires_at, "accepted": accepted, "missing": missing}

    def _status(self) -> dict[str, Any]:
        row = self.history.connection.execute("SELECT * FROM image_cleanup_settings WHERE id=1").fetchone()
        counts = {"input": 0, "prompt": 0, "comfy_input": 0}
        sizes = dict(counts)
        for record in self.history.connection.execute("SELECT kind,COUNT(*),SUM(size) FROM managed_image_files GROUP BY kind"):
            if record[0] in counts:
                counts[record[0]], sizes[record[0]] = record[1], record[2] or 0
        sizes["total"] = sum(sizes.values())
        return {"enabled": bool(row["enabled"]), "retention_days": row["retention_days"],
                "counts": counts, "bytes": sizes,
                "last_run": json.loads(row["last_run"]) if row["last_run"] else None}

    async def status(self) -> dict[str, Any]:
        await self.initialize()
        reason = self._initialization_error or getattr(self.history, "read_only_reason", None) or (
            "历史存储不可用" if not getattr(self.history, "available", True) else "")
        if not self._initialized:
            value = {"enabled": True, "retention_days": 7, "counts": {"input": 0, "prompt": 0, "comfy_input": 0},
                     "bytes": {"input": 0, "prompt": 0, "comfy_input": 0, "total": 0}, "last_run": None}
        else:
            value = await self.history._call(self._status)
        return {**value, "startup_grace_seconds": STARTUP_GRACE, "interval_seconds": INTERVAL,
                "lease_seconds": LEASE_SECONDS, "read_only": bool(reason), "reason": reason}

    async def settings(self, payload: dict[str, Any] | None = None) -> dict[str, Any]:
        if payload is not None:
            if not isinstance(payload, dict) or set(payload) - {"enabled", "retention_days"}:
                raise WorkflowError("图片清理设置仅支持 enabled 和 retention_days")
            if "enabled" in payload and type(payload["enabled"]) is not bool:
                raise WorkflowError("enabled 必须为布尔值")
            if "retention_days" in payload and (type(payload["retention_days"]) is not int or not 1 <= payload["retention_days"] <= 365):
                raise WorkflowError("闲置保留天数必须为 1 至 365 的整数")
            await self.initialize()
            async with self._lock:
                self._writable()

                def update() -> None:
                    self._writable()
                    with self.history.connection:
                        for key, value in payload.items():
                            self.history.connection.execute(f"UPDATE image_cleanup_settings SET {key}=? WHERE id=1", (value,))

                await self.history._call(update)
        return await self.status()

    async def _local_comfy_root(self) -> Path:
        stats = await self.comfy.status()
        url = getattr(self.comfy, "base_url", "")

        def resolve() -> Path:
            if not image_location.IS_WINDOWS:
                raise WorkflowError("仅支持核实本机 Windows ComfyUI 输入副本")
            image_location.verify_local_comfy(url, stats)
            return Path(image_location.comfy_image_directory(stats, "input")).resolve(strict=True)

        return await self._thread(resolve)

    @staticmethod
    def _owned_comfy_path(root: Path, relative: str) -> Path:
        # Empty subfolder is accepted by old upload adapters, but only an
        # explicitly registered response may claim it. Never scan the input dir.
        pieces = relative.replace("\\", "/").split("/")
        if len(pieces) not in (1, 2) or len(pieces) == 2 and pieces[0] != "AnimaStudio" or not COMFY_NAME.fullmatch(pieces[-1]):
            raise WorkflowError("ComfyUI 输入副本路径不属于本应用")
        candidate = root
        for piece in pieces:
            candidate = candidate / piece
            try:
                attributes = os.lstat(candidate)
            except FileNotFoundError:
                break  # recorded_image_path translates a missing file below.
            if candidate.is_symlink() or getattr(attributes, "st_file_attributes", 0) & 0x400:
                raise WorkflowError("ComfyUI 输入副本路径包含链接或重解析点，已跳过")
        return image_location.recorded_image_path(root, {"filename": pieces[-1], "subfolder": "/".join(pieces[:-1])})

    async def register_comfy(self, relative_path: str, body: bytes) -> None:
        """Record only a verified local upload; remote use must still succeed."""
        await self.initialize()
        async with self._lock:
            self._writable()
            try:
                root = await self._local_comfy_root()
                digest = hashlib.sha256(body).hexdigest()
                path = await self._thread(self._owned_comfy_path, root, relative_path)
                if await self._thread(self._digest, path) != digest:
                    raise WorkflowError("上传副本内容已变化")
                await self.history._call(self._upsert, "comfy_input", relative_path, str(root), relative_path,
                                         digest, len(body), time.time(), time.time(), "upload")
            except (OSError, ValueError, WorkflowError, image_location.ImageLocationError) as error:
                logger.info("ComfyUI 输入副本未登记为本机可清理文件: %s", error)
            except Exception as error:
                # Recording an optional local cache must not fail generation
                # after a successful remote upload or transient status failure.
                logger.warning("无法登记 ComfyUI 输入副本: %s", error)

    async def upload_comfy(self, body: bytes, filename: str) -> str:
        """Drain upload plus registration even when its generation is cancelled."""
        async def upload() -> str:
            relative = await self.comfy.upload_image(body, filename)
            await self.register_comfy(relative, body)
            return relative

        return await self._drain(asyncio.create_task(upload()))

    @staticmethod
    async def _drain(operation: asyncio.Task) -> Any:
        try:
            return await asyncio.shield(operation)
        except asyncio.CancelledError:
            while not operation.done():
                try:
                    await asyncio.shield(operation)
                except asyncio.CancelledError:
                    continue
                except Exception:
                    break
            if operation.done() and not operation.cancelled():
                operation.exception()
            raise

    def _legacy_evidence(self) -> tuple[list[str], list[str]]:
        paths = set()
        reverse = set()
        for row in self.history.connection.execute("SELECT single_runtime_json FROM images"):
            value = json.loads(row[0] or "{}")
            if isinstance(value, dict) and isinstance(value.get("input_image"), str):
                paths.add(value["input_image"])
        for row in self.history.connection.execute("SELECT settings_json FROM prompt_jobs"):
            value = json.loads(row[0])
            if isinstance(value, dict) and value.get("kind") == "reverse" and value.get("method", "vision") != "metadata":
                asset_id = value.get("image_id")
                if isinstance(asset_id, str) and ASSET_ID.fullmatch(asset_id):
                    reverse.add(asset_id)
        return sorted(paths), sorted(reverse)

    async def _claim_legacy(self, root: Path) -> None:
        paths, reverse = await self.history._call(self._legacy_evidence)
        candidates = [(path, Path(path).stem) for path in paths if path.startswith("AnimaStudio/") and ASSET_ID.fullmatch(Path(path).stem)]
        for asset_id in reverse:
            try:
                path = self.stores["prompt"].image_path(asset_id)
                digest = await self._thread(self._digest, path)
                candidates.append((f"AnimaStudio/reverse-{asset_id}.png", digest))
            except (KeyError, OSError, WorkflowError):
                continue
        for relative, digest in candidates:
            try:
                path = await self._thread(self._owned_comfy_path, root, relative)
                if await self._thread(self._digest, path) != digest:
                    continue
                stat = await self._thread(path.stat)
                await self.history._call(self._upsert, "comfy_input", relative, str(root), relative, digest,
                                         stat.st_size, stat.st_mtime, time.time(), "legacy", False)
            except (OSError, WorkflowError, image_location.ImageLocationError):
                continue

    def _references(self, now: float) -> set[tuple[str, str]]:
        self._writable()
        references = set()
        for row in self.history.connection.execute("SELECT settings_json,single_runtime_json FROM images"):
            settings, runtime = json.loads(row[0]), json.loads(row[1] or "{}")
            for value in ((settings.get("single") or {}).get("input_image_id"), runtime.get("input_image_id")):
                if isinstance(value, str) and ASSET_ID.fullmatch(value):
                    references.add(("input", value))
        for row in self.history.connection.execute("SELECT kind,asset_id FROM image_draft_leases WHERE expires_at>?", (now,)):
            references.add((row[0], row[1]))
        return references

    def _runtime_references(self) -> set[tuple[str, str]]:
        references = set()
        state = getattr(self.manager, "state", None)
        entries = list(getattr(self.manager, "queue", []))
        if state and (self.manager.active() or state.get("status") in {"running", "stopping"}):
            entries.append(state)
        for entry in entries:
            settings = entry.get("settings") or {}
            value = (settings.get("single") or {}).get("input_image_id")
            if isinstance(value, str) and ASSET_ID.fullmatch(value):
                references.add(("input", value))
            value = settings.get("image_id")
            if settings.get("kind") == "reverse" and isinstance(value, str) and ASSET_ID.fullmatch(value):
                references.add(("prompt", value))
        return references

    @asynccontextmanager
    async def _manager_guard(self):
        lock = getattr(self.manager, "_start_lock", None)
        if lock is None:
            yield
        else:
            async with lock:
                yield

    async def _comfy_idle(self) -> bool:
        # Inspect Comfy's real queue, including jobs surviving a WebUI restart.
        # Fail closed on malformed/unsupported responses and transport errors.
        response = await self.comfy._json("GET", "/queue")
        if not isinstance(response, dict) or any(not isinstance(response.get(key), list) for key in ("queue_running", "queue_pending")):
            raise WorkflowError("无法确认 ComfyUI 运行与等待队列")
        return not response["queue_running"] and not response["queue_pending"]

    async def run(self) -> dict[str, Any]:
        await self.initialize()
        self._writable()
        result = {"finished_at": None, "deleted_files": 0, "freed_bytes": 0,
                  "skipped_files": 0, "errors": [], "comfy_status": "尚未检查本机 ComfyUI"}
        async with self._manager_guard(), self._lock:
            self._writable()
            root = None
            try:
                root = await self._local_comfy_root()
                await self._claim_legacy(root)
                if self.manager.active() or getattr(self.manager, "queue", []) or not await self._comfy_idle():
                    result["comfy_status"] = "ComfyUI 有运行或等待任务，已跳过输入副本"
                    root = None
                else:
                    result["comfy_status"] = "已核实本机 ComfyUI，按登记清单清理"
            except Exception as error:
                result["comfy_status"] = f"已跳过 ComfyUI 输入副本：{error}"
            now = time.time()
            configuration = await self.history._call(self._status)
            cutoff = now - configuration["retention_days"] * DAY
            references = await self.history._call(self._references, now)
            references.update(self._runtime_references())
            rows = await self.history._call(lambda: [dict(row) for row in self.history.connection.execute(
                "SELECT * FROM managed_image_files WHERE last_used_at<?", (cutoff,))])
            for row in rows:
                kind, asset_id = row["kind"], row["asset_id"]
                if (kind, asset_id) in references or kind == "comfy_input" and (root is None or str(root) != row["root"]):
                    result["skipped_files"] += 1
                    continue
                try:
                    # Recheck the external queue immediately before unlinking;
                    # an interrupted job may remain alive after the UI restarts.
                    if kind == "comfy_input" and not await self._comfy_idle():
                        result["skipped_files"] += 1
                        result["comfy_status"] = "ComfyUI 有运行或等待任务，已跳过输入副本"
                        continue
                    # Runtime is checked before the final DB reference check.
                    # A finishing runner writes history before leaving runtime,
                    # so the two checks cover that transfer without a gap.
                    if (kind, asset_id) in self._runtime_references():
                        result["skipped_files"] += 1
                        continue
                    deleted = await self._drain(asyncio.create_task(self.history._call(self._delete_if_unreferenced, row, cutoff)))
                    if deleted is None:
                        result["skipped_files"] += 1
                        continue
                    count, freed = deleted
                    result["deleted_files"] += count
                    result["freed_bytes"] += freed
                except (OSError, ValueError, WorkflowError, image_location.ImageLocationError) as error:
                    result["skipped_files"] += 1
                    result["errors"].append(f"{kind}/{asset_id}: {error}")
                except Exception as error:
                    result["skipped_files"] += 1
                    result["errors"].append(f"{kind}/{asset_id}: {error}")
            result["finished_at"] = time.time()

            def finish() -> None:
                self._writable()
                with self.history.connection:
                    self.history.connection.execute("DELETE FROM image_draft_leases WHERE expires_at<=?", (now,))
                    self.history.connection.execute("UPDATE image_cleanup_settings SET last_run=? WHERE id=1",
                                                    (json.dumps(result, ensure_ascii=False),))

            await self.history._call(finish)
        return result

    def _delete_if_unreferenced(self, row: dict[str, Any], cutoff: float) -> tuple[int, int] | None:
        """Final reference check and unlink share the serial DB worker/transaction."""
        self._writable()
        connection = self.history.connection
        connection.execute("BEGIN IMMEDIATE")
        try:
            current = connection.execute(
                "SELECT last_used_at FROM managed_image_files WHERE kind=? AND asset_id=? AND root=?",
                (row["kind"], row["asset_id"], row["root"]),
            ).fetchone()
            if current is None or current[0] >= cutoff or (row["kind"], row["asset_id"]) in self._references(time.time()):
                connection.commit()
                return None
            deleted = self._delete_file(row)
            connection.execute("DELETE FROM managed_image_files WHERE kind=? AND asset_id=? AND root=?",
                               (row["kind"], row["asset_id"], row["root"]))
            connection.commit()
            return deleted
        except BaseException:
            connection.rollback()
            raise

    def _delete_file(self, row: dict[str, Any]) -> tuple[int, int]:
        self._writable()
        kind, asset_id = row["kind"], row["asset_id"]
        root = Path(row["root"]).resolve(strict=True)
        if kind == "comfy_input":
            try:
                image = self._owned_comfy_path(root, row["relative_path"])
            except image_location.ImageLocationError as error:
                if error.code == "image_file_missing":
                    return 0, 0
                raise
            files = [image]
        else:
            store = self._store(kind, asset_id)
            if root != store.path.resolve(strict=True):
                raise WorkflowError("导入图片目录已变化，跳过旧登记")
            image = root / f"{asset_id}.png"
            files = [image, root / f"{asset_id}.json"]
        for path in files:
            if path.is_symlink() or not path.resolve().is_relative_to(root):
                raise WorkflowError("图片路径超出受管目录，已跳过")
        if image.exists() and self._digest(image) != row["sha256"]:
            raise WorkflowError("图片内容已被替换，已跳过")
        count, size = 0, 0
        for path in files:
            if path.exists():
                size += path.stat().st_size
                path.unlink()
                count += 1
        return count, size
