"""Capacity-limited reverse-image cache; ownership is required before unlinking."""
from __future__ import annotations

import asyncio
from contextlib import asynccontextmanager
import hashlib
import json
import logging
from pathlib import Path
import re
import time
from typing import Any
import uuid

from . import image_location
from .workflow import WorkflowError

logger = logging.getLogger(__name__)
MAX_BYTES = 100 * 1024 * 1024
LEASE_SECONDS = 600
UPLOAD_PROTECTION_SECONDS = 120
ASSET_ID = re.compile(r"^[a-f0-9]{64}$")
CLIENT_ID = re.compile(r"^[A-Za-z0-9_-]{8,128}$")


class PromptImageCache:
    def __init__(self, service: Any, manager: Any = None, *, max_bytes: int = MAX_BYTES,
                 clock: Any = time.time, interval: float = 60, startup_delay: float = 60):
        self.service, self.manager, self.history = service, manager, service.history
        self.max_bytes, self.clock = max_bytes, clock
        self.interval, self.startup_delay = interval, startup_delay
        self.owner = ""
        self._local_root: Path | None = None
        self._initialized = False
        self._closed = False
        self._suspended = ""
        self._task: asyncio.Task | None = None
        self._wake = asyncio.Event()

    def suspend(self, reason: str) -> None:
        if not self._suspended:
            logger.warning("反推图片自动清理已暂停: %s", reason)
        self._suspended = str(reason)

    def _writable(self) -> bool:
        return not self._suspended and not getattr(self.history, "read_only_reason", None) and getattr(self.history, "available", True)

    def _schema(self) -> tuple[str, str]:
        self.history.ensure_writable()
        connection = self.history.connection
        with connection:
            connection.execute("""CREATE TABLE IF NOT EXISTS prompt_image_cache_state (
                id INTEGER PRIMARY KEY CHECK(id=1), owner TEXT NOT NULL, local_root TEXT NOT NULL)""")
            root = str(self.service.stores["prompt"].path.resolve(strict=True))
            connection.execute("INSERT OR IGNORE INTO prompt_image_cache_state VALUES(1,?,?)", (uuid.uuid4().hex, root))
            connection.execute("""CREATE TABLE IF NOT EXISTS prompt_image_cache_files (
                kind TEXT NOT NULL, asset_id TEXT NOT NULL, root TEXT NOT NULL,
                relative_path TEXT NOT NULL, sha256 TEXT NOT NULL, metadata_sha256 TEXT NOT NULL DEFAULT '',
                size INTEGER NOT NULL, last_used_at REAL NOT NULL, protect_until REAL NOT NULL DEFAULT 0,
                pending_delete INTEGER NOT NULL DEFAULT 0, origin TEXT NOT NULL,
                PRIMARY KEY(kind,root,relative_path))""")
            connection.execute("""CREATE TABLE IF NOT EXISTS prompt_image_cache_leases (
                client_id TEXT PRIMARY KEY, revision INTEGER NOT NULL,
                image_ids TEXT NOT NULL, expires_at REAL NOT NULL)""")
        state = connection.execute("SELECT owner,local_root FROM prompt_image_cache_state WHERE id=1").fetchone()
        owner = state[0]
        if not isinstance(owner, str) or not re.fullmatch(r"[a-f0-9]{32}", owner):
            raise WorkflowError("反推图片缓存标识无效")
        if Path(state[1]) != Path(root):
            raise WorkflowError("反推缓存根目录与首次登记不符，已停止自动清理")
        return owner, root

    @staticmethod
    def _digest(path: Path) -> str:
        with path.open("rb") as stream:
            return hashlib.file_digest(stream, "sha256").hexdigest()

    @staticmethod
    def _safe_path(root: Path, relative: str) -> Path:
        parts = relative.replace("\\", "/").split("/")
        if any(not value or value in {".", ".."} or any(c in value for c in ':<>"|?*') for value in parts):
            raise WorkflowError("反推缓存路径无效")
        candidate = root
        for value in parts:
            candidate = candidate / value
            try:
                info = candidate.lstat()
            except FileNotFoundError:
                continue
            if candidate.is_symlink() or getattr(info, "st_file_attributes", 0) & 0x400:
                raise WorkflowError("反推缓存路径包含链接或重解析点")
        if not candidate.resolve().is_relative_to(root):
            raise WorkflowError("反推缓存路径超出受管目录")
        return candidate

    def _check_local_root(self) -> Path:
        root = self.service.stores["prompt"].path.resolve(strict=True)
        if self._local_root is not None and root != self._local_root:
            reason = "反推缓存根目录在运行期间发生变化，已停止自动清理"
            self.suspend(reason)
            raise WorkflowError(reason)
        return root

    def _local_info(self, asset_id: str) -> dict[str, Any]:
        if not isinstance(asset_id, str) or not ASSET_ID.fullmatch(asset_id):
            raise WorkflowError("反推图片 ID 无效")
        store = self.service.stores["prompt"]
        root = self._check_local_root()
        image = self._safe_path(root, asset_id + ".png")
        metadata = self._safe_path(root, asset_id + ".json")
        store.get(asset_id)
        return {"kind": "local", "asset_id": asset_id, "root": str(root), "relative_path": image.name,
                "sha256": self._digest(image), "metadata_sha256": self._digest(metadata),
                "size": image.stat().st_size + metadata.stat().st_size,
                "last_used_at": max(image.stat().st_mtime, metadata.stat().st_mtime), "origin": "legacy"}

    def _upsert(self, row: dict[str, Any], *, replace: bool = False, touch: bool = False,
                uploaded: bool = False) -> None:
        self.history.ensure_writable()
        if self._suspended:
            raise WorkflowError("反推缓存已暂停，不能登记新文件")
        now = self.clock()
        connection = self.history.connection
        with connection:
            connection.execute("""INSERT INTO prompt_image_cache_files
                (kind,asset_id,root,relative_path,sha256,metadata_sha256,size,last_used_at,protect_until,pending_delete,origin)
                VALUES(?,?,?,?,?,?,?,?,?,0,?) ON CONFLICT(kind,root,relative_path) DO NOTHING""",
                (row["kind"], row["asset_id"], row["root"], row["relative_path"], row["sha256"],
                 row.get("metadata_sha256", ""), row["size"], now if touch else row["last_used_at"],
                 now + UPLOAD_PROTECTION_SECONDS if uploaded else 0, row["origin"]))
            if replace:
                connection.execute("""UPDATE prompt_image_cache_files SET sha256=?,metadata_sha256=?,size=?,origin=?
                    WHERE kind=? AND root=? AND relative_path=?""",
                    (row["sha256"], row.get("metadata_sha256", ""), row["size"], row["origin"],
                     row["kind"], row["root"], row["relative_path"]))
            if touch:
                connection.execute("""UPDATE prompt_image_cache_files SET last_used_at=?,pending_delete=0,
                    protect_until=MAX(protect_until,?) WHERE asset_id=?""",
                    (now, now + UPLOAD_PROTECTION_SECONDS if uploaded else 0, row["asset_id"]))

    async def initialize(self) -> None:
        """Caller holds the image-service lock. Initialization never deletes files."""
        if self._initialized or not self._writable() or self._closed:
            return
        try:
            self.owner, root = await self.history._call(self._schema)
            self._local_root = Path(root)
            paths = await self.service._thread(lambda: list(self.service.stores["prompt"].path.glob("*.json")))
            for path in paths:
                if not ASSET_ID.fullmatch(path.stem):
                    continue
                try:
                    row = await self.service._thread(self._local_info, path.stem)
                except (KeyError, OSError, ValueError, WorkflowError):
                    continue
                await self.history._call(self._upsert, row)
            self._initialized = True
            await self._claim_legacy()
        except Exception as error:
            self.suspend(str(error))
            raise

    async def register_local(self, asset_id: str, uploaded: bool = False) -> None:
        """Called under the service lock after a successful write or read."""
        if not self._writable() or self._closed:
            return
        await self.initialize()
        row = await self.service._thread(self._local_info, asset_id)
        row["origin"] = "upload" if uploaded else "access"
        await self.history._call(self._upsert, row, replace=uploaded, touch=True, uploaded=uploaded)
        if uploaded:
            self.wake()

    def _legacy_candidates(self) -> list[dict[str, Any]]:
        connection = self.history.connection
        candidates = []
        tables = {row[0] for row in connection.execute("SELECT name FROM sqlite_master WHERE type='table'")}
        if "managed_image_files" in tables:
            for row in connection.execute("SELECT * FROM managed_image_files WHERE kind='comfy_input'"):
                record = dict(row)
                match = re.fullmatch(r"AnimaStudio/reverse-([a-f0-9]{64})\.png", record.get("relative_path", ""))
                if match:
                    candidates.append({**record, "asset_id": match[1]})
        for row in connection.execute("SELECT settings_json FROM prompt_jobs"):
            settings = json.loads(row[0])
            asset_id = settings.get("image_id")
            if (settings.get("kind") == "reverse" and settings.get("method", "vision") != "metadata"
                    and isinstance(asset_id, str) and ASSET_ID.fullmatch(asset_id)):
                candidates.append({"asset_id": asset_id, "relative_path": f"AnimaStudio/reverse-{asset_id}.png"})
                # Recover an upload that reached ComfyUI immediately before a
                # process crash prevented registration of its private filename.
                candidates.append({"asset_id": asset_id, "relative_path": f"AnimaStudio/reverse-{self.owner}-{asset_id}.png"})
        return candidates

    async def _local_comfy_root(self) -> Path:
        stats = await asyncio.wait_for(self.service.comfy.status(), timeout=3)
        url = getattr(self.service.comfy, "base_url", "")

        def verify() -> Path:
            if not image_location.IS_WINDOWS:
                raise WorkflowError("仅支持清理已核实的本机 Windows ComfyUI 副本")
            image_location.verify_local_comfy(url, stats)
            return Path(image_location.comfy_image_directory(stats, "input")).resolve(strict=True)

        return await self.service._thread(verify)

    async def _claim_legacy(self) -> None:
        candidates = await self.history._call(self._legacy_candidates)
        if not candidates:
            return
        try:
            root = await self._local_comfy_root()
        except Exception:
            return
        for candidate in candidates:
            try:
                if "root" in candidate and Path(candidate["root"]).resolve() != root:
                    continue
                if "sha256" not in candidate:
                    source = await self.service._thread(self._local_info, candidate["asset_id"])
                    candidate["sha256"] = source["sha256"]
                path = self._safe_path(root, candidate["relative_path"])
                row = await self.service._thread(self._comfy_info, path, root, candidate["asset_id"], candidate["sha256"])
                if "size" in candidate and row["size"] != candidate["size"]:
                    continue
                row["last_used_at"] = candidate.get("last_used_at", row["last_used_at"])
                row["origin"] = "legacy"
                await self.history._call(self._upsert, row)
            except (KeyError, OSError, ValueError, WorkflowError, image_location.ImageLocationError):
                continue

    def _comfy_info(self, path: Path, root: Path, asset_id: str, digest: str) -> dict[str, Any]:
        if not path.is_file() or self._digest(path) != digest:
            raise WorkflowError("ComfyUI 反推上传副本内容不匹配")
        return {"kind": "comfy", "asset_id": asset_id, "root": str(root),
                "relative_path": path.relative_to(root).as_posix(), "sha256": digest,
                "size": path.stat().st_size, "last_used_at": path.stat().st_mtime, "origin": "upload"}

    async def upload_comfy(self, body: bytes, filename: str, asset_id: str) -> str:
        """Caller holds service lock and drains this whole operation on cancellation."""
        try:
            await self.initialize()
            if self._writable():
                await self.service._thread(self._check_local_root)
        except Exception:
            pass
        name = f"reverse-{self.owner}-{asset_id}.png" if self.owner and self._writable() else filename
        relative = await self.service.comfy.upload_image(body, name)
        if not self._writable() or self._closed:
            return relative
        try:
            if not ASSET_ID.fullmatch(asset_id) or relative not in {name, f"AnimaStudio/{name}"}:
                raise WorkflowError("ComfyUI 返回的反推副本路径与上传不符")
            root = await self._local_comfy_root()
            path = self._safe_path(root, relative)
            row = await self.service._thread(self._comfy_info, path, root, asset_id, hashlib.sha256(body).hexdigest())
            await self.history._call(self._upsert, row, replace=True, touch=True)
            self.wake()
        except Exception as error:
            logger.info("反推 ComfyUI 副本未登记，本次识别继续: %s", error)
        return relative

    def _saved_lease(self, client_id: str) -> dict[str, Any] | None:
        row = self.history.connection.execute("SELECT * FROM prompt_image_cache_leases WHERE client_id=?", (client_id,)).fetchone()
        return dict(row) if row else None

    @staticmethod
    def _lease_result(row: dict[str, Any], missing: list[str] | None = None) -> dict[str, Any]:
        return {"revision": row["revision"], "image_ids": json.loads(row["image_ids"]),
                "missing_image_ids": missing or [], "expires_at": row["expires_at"]}

    def _save_lease(self, client_id: str, revision: int, image_ids: list[str]) -> dict[str, Any]:
        self.history.ensure_writable()
        connection = self.history.connection
        with connection:
            connection.execute("""INSERT INTO prompt_image_cache_leases VALUES(?,?,?,?)
                ON CONFLICT(client_id) DO UPDATE SET revision=excluded.revision,image_ids=excluded.image_ids,
                expires_at=excluded.expires_at WHERE excluded.revision>prompt_image_cache_leases.revision""",
                (client_id, revision, json.dumps(image_ids), self.clock() + LEASE_SECONDS))
        return self._saved_lease(client_id)

    async def lease(self, client_id: str, payload: Any) -> dict[str, Any]:
        if (not isinstance(client_id, str) or not CLIENT_ID.fullmatch(client_id)
                or not isinstance(payload, dict) or set(payload) != {"revision", "image_ids"}
                or type(payload.get("revision")) is not int or not 0 <= payload["revision"] <= 2**53 - 1
                or not isinstance(payload.get("image_ids"), list) or len(payload["image_ids"]) > 128
                or any(not isinstance(value, str) or not ASSET_ID.fullmatch(value) for value in payload["image_ids"])):
            raise WorkflowError("反推图片草稿租约无效")
        async with self.service._lock:
            self.service._store("prompt")
            self.history.ensure_writable()
            await self.initialize()
            if self._suspended:
                raise WorkflowError("反推图片缓存登记不可用")
            saved = await self.history._call(self._saved_lease, client_id)
            if saved and saved["revision"] >= payload["revision"]:
                return self._lease_result(saved)
            accepted, missing = [], []
            for asset_id in dict.fromkeys(payload["image_ids"]):
                try:
                    # A background heartbeat is protection, not actual use.
                    # Do not hash large images or reorder the LRU every minute.
                    await self.service._thread(self._lease_image, asset_id)
                except (KeyError, OSError, WorkflowError):
                    missing.append(asset_id)
                else:
                    accepted.append(asset_id)
            saved = await self.history._call(self._save_lease, client_id, payload["revision"], accepted)
            self.wake()
            return self._lease_result(saved, missing)

    def _lease_image(self, asset_id: str) -> None:
        store = self.service.stores["prompt"]
        self._check_local_root()
        self.service._checked_path(store, asset_id)
        store.get(asset_id)

    def _references(self) -> set[str]:
        now = self.clock()
        connection = self.history.connection
        references = set()
        for row in connection.execute("SELECT image_ids FROM prompt_image_cache_leases WHERE expires_at>?", (now,)):
            references.update(json.loads(row[0]))
        for row in connection.execute("SELECT asset_id FROM prompt_image_cache_files WHERE protect_until>?", (now,)):
            references.add(row[0])
        for row in connection.execute("SELECT settings_json FROM prompt_jobs WHERE status IN ('queued','running','stopping')"):
            settings = json.loads(row[0])
            if settings.get("kind") == "reverse":
                references.add(settings.get("image_id"))
        return references

    def _runtime_references(self) -> set[str]:
        references = set()
        manager = self.manager
        if manager is None:
            return references
        entries = list(getattr(manager, "queue", []))
        state = getattr(manager, "state", None)
        if state and (manager.active() or state.get("status") in {"running", "stopping"}):
            entries.append(state)
        for entry in entries:
            settings = entry.get("settings") or {}
            if settings.get("kind") == "reverse":
                references.add(settings.get("image_id"))
        return references

    def _rows(self) -> list[dict[str, Any]]:
        return [dict(row) for row in self.history.connection.execute("SELECT * FROM prompt_image_cache_files")]

    @asynccontextmanager
    async def _manager_guard(self):
        lock = getattr(self.manager, "_start_lock", None)
        if lock is None:
            yield
        else:
            async with lock:
                yield

    async def _comfy_idle(self) -> bool:
        response = await asyncio.wait_for(self.service.comfy._json("GET", "/queue"), timeout=3)
        return (isinstance(response, dict)
                and all(isinstance(response.get(key), list) and not response[key] for key in ("queue_running", "queue_pending")))

    def _mark_pending(self, asset_id: str) -> None:
        self.history.ensure_writable()
        with self.history.connection:
            self.history.connection.execute("UPDATE prompt_image_cache_files SET pending_delete=1 WHERE asset_id=?", (asset_id,))

    def _row_paths(self, row: dict[str, Any], remote_root: Path | None) -> list[tuple[Path, str]]:
        recorded_root = Path(row["root"])
        root = recorded_root.resolve(strict=True)
        if root != recorded_root:
            raise WorkflowError("登记的反推缓存根目录被重定向，保留文件")
        if row["kind"] == "local":
            if root != self.service.stores["prompt"].path.resolve(strict=True):
                raise WorkflowError("反推图片目录已变化")
            if row["relative_path"] != row["asset_id"] + ".png" or not ASSET_ID.fullmatch(row["asset_id"]):
                raise WorkflowError("反推图片登记路径无效")
            return [(self._safe_path(root, row["relative_path"]), row["sha256"]),
                    (self._safe_path(root, row["asset_id"] + ".json"), row["metadata_sha256"])]
        names = {f"reverse-{row['asset_id']}.png", f"reverse-{self.owner}-{row['asset_id']}.png"}
        if remote_root != root or row["relative_path"] not in names | {"AnimaStudio/" + value for value in names}:
            raise WorkflowError("ComfyUI 反推副本目录或归属已变化")
        return [(self._safe_path(root, row["relative_path"]), row["sha256"])]

    def _refresh_sizes(self, remote_root: Path | None) -> list[str]:
        """Reconcile missing/partly removed files without adopting new contents."""
        self.history.ensure_writable()
        connection = self.history.connection
        errors = []
        with connection:
            for row in self._rows():
                if row["kind"] == "comfy" and remote_root is None:
                    continue  # Keep the last known size when locality is unknown.
                try:
                    paths = self._row_paths(row, remote_root)
                    size = sum(path.stat().st_size for path, _ in paths if path.exists())
                except (OSError, ValueError, WorkflowError) as error:
                    errors.append(str(error))
                    continue
                key = row["kind"], row["root"], row["relative_path"]
                if size:
                    connection.execute("UPDATE prompt_image_cache_files SET size=? WHERE kind=? AND root=? AND relative_path=?",
                                       (size, *key))
                else:
                    connection.execute("DELETE FROM prompt_image_cache_files WHERE kind=? AND root=? AND relative_path=?", key)
        return errors

    def _delete(self, row: dict[str, Any], remote_root: Path | None) -> tuple[int, int, str] | None:
        """Final persistent reference check and unlink share the DB transaction."""
        self.history.ensure_writable()
        connection = self.history.connection
        connection.execute("BEGIN IMMEDIATE")
        try:
            current = connection.execute("""SELECT * FROM prompt_image_cache_files
                WHERE kind=? AND root=? AND relative_path=?""", (row["kind"], row["root"], row["relative_path"])).fetchone()
            if (not current or not current["pending_delete"] or current["last_used_at"] != row["last_used_at"]
                    or row["asset_id"] in self._references()):
                connection.commit()
                return None
            paths = self._row_paths(row, remote_root)
            for path, digest in paths:
                if path.exists() and (not path.is_file() or self._digest(path) != digest):
                    raise WorkflowError("反推缓存文件内容已变化，保留文件")
            count, freed, error = 0, 0, ""
            try:
                for path, _ in paths:
                    if path.exists():
                        size = path.stat().st_size
                        path.unlink()
                        count += 1
                        freed += size
            except OSError as failure:
                error = str(failure)
            remaining = sum(path.stat().st_size for path, _ in paths if path.exists())
            key = row["kind"], row["root"], row["relative_path"]
            if remaining:
                connection.execute("UPDATE prompt_image_cache_files SET size=?,pending_delete=1 WHERE kind=? AND root=? AND relative_path=?",
                                   (remaining, *key))
            else:
                connection.execute("DELETE FROM prompt_image_cache_files WHERE kind=? AND root=? AND relative_path=?", key)
            connection.commit()
            return count, freed, error
        except BaseException:
            connection.rollback()
            raise

    async def run(self) -> dict[str, Any]:
        result = {"before_bytes": 0, "total_bytes": 0, "deleted_files": 0, "freed_bytes": 0,
                  "skipped_files": 0, "pending_files": 0, "errors": []}
        async with self._manager_guard(), self.service._lock:
            if self._closed or not self._writable():
                return result
            try:
                await self.initialize()
                await self.service._thread(self._check_local_root)
                await self._claim_legacy()
                if not self._writable():
                    return result
                rows = await self.history._call(self._rows)
                remote_root = None
                if any(row["kind"] == "comfy" for row in rows):
                    try:
                        remote_root = await self._local_comfy_root()
                    except Exception:
                        pass
                result["errors"].extend(await self.history._call(self._refresh_sizes, remote_root))
                rows = await self.history._call(self._rows)
                total = sum(row["size"] for row in rows)
                result["before_bytes"] = total
                protected = await self.history._call(self._references) | self._runtime_references()
                grouped: dict[str, list[dict[str, Any]]] = {}
                for row in rows:
                    grouped.setdefault(row["asset_id"], []).append(row)
                ordered = sorted(grouped.items(), key=lambda pair: (max(row["last_used_at"] for row in pair[1]), pair[0]))
                for asset_id, group in ordered:
                    if asset_id in protected or asset_id in self._runtime_references():
                        result["skipped_files"] += len(group)
                        continue
                    if total <= self.max_bytes and not any(row["pending_delete"] for row in group):
                        continue
                    await self.history._call(self._mark_pending, asset_id)
                    for row in group:
                        try:
                            if row["kind"] == "comfy" and (remote_root is None or not await self._comfy_idle()):
                                result["skipped_files"] += 1
                                continue
                            deleted = await self.service._finish(self.history._call(self._delete, row, remote_root))
                            if deleted is None:
                                result["skipped_files"] += 1
                            else:
                                result["deleted_files"] += deleted[0]
                                result["freed_bytes"] += deleted[1]
                                total -= deleted[1]
                                if deleted[2]:
                                    result["errors"].append(deleted[2])
                        except Exception as error:
                            result["skipped_files"] += 1
                            result["errors"].append(str(error))
                remaining = await self.history._call(self._rows)
                result["total_bytes"] = sum(row["size"] for row in remaining)
                result["pending_files"] = sum(bool(row["pending_delete"]) for row in remaining)
            except Exception as error:
                self.suspend(str(error))
                result["errors"].append(str(error))
        if result["errors"]:
            logger.warning("反推图片清理跳过部分文件: %s", "; ".join(result["errors"][:3]))
        if result["deleted_files"]:
            logger.info("反推缓存清理: 删除 %s 个文件，释放 %s 字节，剩余 %s 字节",
                        result["deleted_files"], result["freed_bytes"], result["total_bytes"])
        return result

    def wake(self) -> None:
        self._wake.set()

    def start(self) -> None:
        if not self._closed and self._task is None:
            self._task = asyncio.create_task(self._periodic())

    async def _periodic(self) -> None:
        try:
            await asyncio.sleep(self.startup_delay)
            while not self._closed:
                self._wake.clear()
                await self.run()
                try:
                    await asyncio.wait_for(self._wake.wait(), self.interval)
                except asyncio.TimeoutError:
                    pass
        except asyncio.CancelledError:
            pass

    async def stop(self) -> None:
        """Stop sweeping while already accepted uploads may still register."""
        if self._task is not None:
            self._task.cancel()
            await asyncio.gather(self._task, return_exceptions=True)
            self._task = None

    async def close(self) -> None:
        self._closed = True
        await self.stop()
