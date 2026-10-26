"""Delete artwork records and explicitly requested, verified local originals."""
from __future__ import annotations

import asyncio
import os
from pathlib import Path
import sqlite3
import stat
from typing import Any

from .history import HistoryStore
from . import image_location
from .image_location import ImageLocationError
from .persistence import StorageReadOnlyError
from .workflow import WorkflowError


def deletion_image_path(directory: Path, record: dict[str, Any]) -> Path:
    """Permit a missing leaf, but never interpret an unavailable root as absence."""
    parts = image_location._record_parts(record)
    try:
        root = directory.resolve(strict=True)
        if not root.is_dir():
            raise OSError("not a directory")
    except (OSError, RuntimeError, ValueError):
        raise ImageLocationError("无法访问 ComfyUI 图片目录，未删除记录", code="image_directory_unavailable") from None
    try:
        target = root.joinpath(*parts).resolve(strict=False)
        if not target.is_relative_to(root) or target == root:
            raise ImageLocationError("图片路径超出 ComfyUI 文件目录", code="invalid_image_location", status=400)
        try:
            info = target.stat()
        except FileNotFoundError:
            return target
        if not stat.S_ISREG(info.st_mode):
            raise ImageLocationError("原图路径不是普通文件", code="invalid_image_location", status=400)
        return target
    except ImageLocationError:
        raise
    except (OSError, RuntimeError, ValueError):
        raise ImageLocationError("无法访问原图文件，请检查目录与文件权限", code="image_file_unavailable") from None


def _result(image_id: int, **values: Any) -> dict[str, Any]:
    return {"id": image_id, "record_deleted": False, "file_deleted": False,
            "file_missing": False, "code": "", "error": "", **values}


class ArtworkDeletionService:
    def __init__(self, history: HistoryStore, thumbnails: Any | None = None):
        self.history = history
        self.thumbnails = thumbnails
        self._lock = asyncio.Lock()

    async def delete(self, image_ids: list[int], mode: str = "record", *,
                     stats: dict[str, Any] | None = None, comfy_url: str = "") -> dict[str, Any]:
        ids = self.history.artwork_image_ids(image_ids)
        if not isinstance(mode, str) or mode not in {"record", "file"}:
            raise WorkflowError("删除模式必须为 record 或 file")
        self.history.ensure_writable()
        task = asyncio.create_task(self._run(ids, mode, stats or {}, comfy_url))
        try:
            return await asyncio.shield(task)
        except asyncio.CancelledError:
            # A worker may have unlinked or committed already. Finish derived
            # cleanup even if the HTTP caller is cancelled more than once.
            while not task.done():
                try:
                    await asyncio.shield(task)
                except asyncio.CancelledError:
                    continue
                except Exception:
                    break
            if not task.cancelled():
                task.exception()
            raise

    async def _run(self, ids: list[int], mode: str, stats: dict[str, Any], comfy_url: str) -> dict[str, Any]:
        async with self._lock:
            if mode == "file":
                try:
                    if not image_location.IS_WINDOWS:
                        raise ImageLocationError("删除原文件仅支持本机 Windows ComfyUI", code="image_delete_unsupported")
                    await asyncio.to_thread(image_location.verify_local_comfy, comfy_url, stats)
                except ImageLocationError as error:
                    return self._payload(mode, [_result(i, code=error.code, error=str(error)) for i in ids])
            results = await self.history._call(self._delete, ids, mode, stats)
            if self.thumbnails is not None:
                for result in results:
                    if result["record_deleted"] or result["code"] == "already_missing":
                        try:
                            await self.thumbnails.discard(result["id"])
                        except Exception:
                            result["warning"] = "记录已删除，缩略图缓存清理失败，将在后续维护时重试"
            return self._payload(mode, results)

    @staticmethod
    def _payload(mode: str, results: list[dict[str, Any]]) -> dict[str, Any]:
        success = sum(result["record_deleted"] or result["code"] == "already_missing" for result in results)
        file_count = len({result.get("file_key") for result in results if result["file_deleted"]})
        for result in results:
            result.pop("file_key", None)
        return {"mode": mode, "results": results, "deleted_count": sum(result["record_deleted"] for result in results),
                "success_count": success, "failed_count": len(results) - success,
                "file_deleted_count": file_count}

    def _rollback(self) -> None:
        try:
            if self.history.connection.in_transaction:
                self.history.connection.rollback()
        except sqlite3.Error as error:
            self.history._protect_storage(f"历史数据库事务回滚失败({error})")

    @staticmethod
    def _path(record: dict[str, Any], stats: dict[str, Any]) -> Path:
        directory = image_location.comfy_image_directory(stats, record.get("file_type") or "output")
        return deletion_image_path(Path(directory), record)

    def _delete(self, ids: list[int], mode: str, stats: dict[str, Any]) -> list[dict[str, Any]]:
        self.history.ensure_writable()
        connection = self.history.connection
        placeholders = ",".join("?" for _ in ids)
        records = {row["id"]: dict(row) for row in connection.execute(
            f"SELECT id, filename, subfolder, file_type FROM images WHERE id IN ({placeholders})", ids)}
        results = {image_id: _result(image_id, code="already_missing") for image_id in ids if image_id not in records}
        if mode == "record":
            try:
                connection.execute("BEGIN IMMEDIATE")
                # Read under the write lock: another process may have deleted
                # a record after the first snapshot.
                present = {row[0] for row in connection.execute(f"SELECT id FROM images WHERE id IN ({placeholders})", ids)}
                connection.execute(f"DELETE FROM images WHERE id IN ({placeholders})", ids)
                connection.commit()
                results.update({i: _result(i, record_deleted=i in present, code="deleted" if i in present else "already_missing") for i in ids})
            except sqlite3.Error:
                self._rollback()
                results.update({i: _result(i, code="database_error", error="历史记录写入失败，记录及收藏未删除") for i in records})
            return [results[i] for i in ids]

        groups: dict[str, tuple[Path, list[int]]] = {}
        for image_id, record in records.items():
            try:
                path = self._path(record, stats)
                key = os.path.normcase(str(path))
                groups.setdefault(key, (path, []))[1].append(image_id)
            except ImageLocationError as error:
                results[image_id] = _result(image_id, code=error.code, error=str(error))
        selected = set(ids)
        database_failed = False
        for key, (path, group_ids) in groups.items():
            if database_failed:
                results.update({i: _result(i, code="database_error", error="数据库写入失败，本项尚未处理") for i in group_ids})
                continue
            deleted_file = missing_file = False
            try:
                self.history.ensure_writable()
                connection.execute("BEGIN IMMEDIATE")
                # Re-resolve all references while holding the write lock. A
                # filename may have aliases through directory junctions.
                references, current = [], set()
                for row in connection.execute("SELECT id, filename, subfolder, file_type FROM images"):
                    record = dict(row)
                    try:
                        candidate = self._path(record, stats)
                    except ImageLocationError:
                        if record["id"] in group_ids:
                            raise
                        continue
                    if os.path.normcase(str(candidate)) == key:
                        references.append(record["id"])
                        if record["id"] in group_ids:
                            current.add(record["id"])
                shared = sorted(set(references) - selected)
                if shared:
                    self._rollback()
                    results.update({i: _result(i, code="file_shared", error="原文件仍被其他历史记录引用，请一并选择后删除", shared_ids=shared) for i in group_ids})
                    continue
                if not current:
                    self._rollback()
                    results.update({i: _result(i, code="already_missing") for i in group_ids})
                    continue
                if current != set(group_ids):
                    # A record changed its file reference after the snapshot;
                    # never delete a file using stale selection information.
                    self._rollback()
                    results.update({i: _result(i, code="record_changed", error="作品记录已变化，请刷新后重试") for i in group_ids})
                    continue
                connection.executemany("DELETE FROM images WHERE id = ?", [(i,) for i in group_ids])
                # Resolve once more immediately before unlinking to reject a
                # junction changed since the initial snapshot.
                confirmed = self._path(records[group_ids[0]], stats)
                if os.path.normcase(str(confirmed)) != key:
                    raise ImageLocationError("原图路径已变化，请刷新后重试", code="image_location_changed")
                try:
                    confirmed.unlink()
                    deleted_file = True
                except FileNotFoundError:
                    missing_file = True
                connection.commit()
                results.update({i: _result(i, record_deleted=True, file_deleted=deleted_file, file_missing=missing_file,
                                           code="deleted", file_key=key) for i in group_ids})
            except ImageLocationError as error:
                self._rollback()
                results.update({i: _result(i, code=error.code, error=str(error)) for i in group_ids})
            except (sqlite3.Error, StorageReadOnlyError):
                self._rollback()
                database_failed = True
                message = "原文件已删除，但记录写入失败；记录及收藏仍保留，请重试删除记录" if deleted_file else "历史记录写入失败，记录及收藏未删除"
                results.update({i: _result(i, file_deleted=deleted_file, file_missing=missing_file, code="database_error", error=message, file_key=key) for i in group_ids})
            except OSError:
                self._rollback()
                results.update({i: _result(i, code="image_delete_failed", error="无法删除原文件，请检查文件占用或目录权限") for i in group_ids})
        return [results[i] for i in ids]
