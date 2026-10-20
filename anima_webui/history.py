from __future__ import annotations

import asyncio
import functools
import json
import logging
import sqlite3
from collections.abc import Callable
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime
from pathlib import Path
from typing import Any

from .persistence import StorageProtection, backup_corrupt_file
from .model_profiles import get_model_profile
from .workflow import migrate_settings, normalize_lora_path, validate_settings


logger = logging.getLogger(__name__)


class HistoryMigrationError(RuntimeError):
    """Raised when legacy history cannot be migrated without risking data."""


def _now() -> str:
    return datetime.now().astimezone().isoformat(timespec="seconds")


class HistoryStore(StorageProtection):
    """sqlite 持久化。

    所有数据库操作都在一个专用单线程 executor 上执行:
    - 单线程天然串行化,连接不会被并发使用;
    - commit/fsync 的耗时不再冻结事件循环(批次中每张图都要落库)。
    构造函数在调用方线程完成建表迁移,此后连接只被 executor 线程使用,
    因此 check_same_thread=False 是安全的。
    """

    store_name = "history"

    def __init__(self, path: str | Path):
        destination = Path(path)
        self.path = destination
        self.read_only_reason = None
        self.available = True
        self.load_warnings: list[str] = []
        try:
            destination.parent.mkdir(parents=True, exist_ok=True)
            self.connection = self._open(destination)
        except HistoryMigrationError as error:
            self._use_unavailable_view(f"历史数据库迁移失败({error})，未替换原数据库")
        except sqlite3.DatabaseError as error:
            # Busy/readonly/unavailable databases are not corrupt. Only
            # quarantine confirmed corruption; never move a locked database.
            code = getattr(error, "sqlite_errorcode", None)
            corrupt = isinstance(code, int) and (code & 0xFF) in {
                sqlite3.SQLITE_CORRUPT, sqlite3.SQLITE_NOTADB
            }
            if not corrupt:
                self._use_unavailable_view(f"历史数据库无法打开({error})，未替换原数据库")
            else:
                try:
                    backup = backup_corrupt_file(destination)
                except OSError as backup_error:
                    self._use_unavailable_view(
                        f"历史数据库无法读取({error})，备份失败({backup_error})，原文件未改动"
                    )
                else:
                    self.load_warnings.append(f"历史数据库无法读取,已备份为 {backup.name} 并重建")
                    try:
                        self.connection = self._open(destination)
                    except (OSError, sqlite3.Error, HistoryMigrationError) as rebuild_error:
                        self._use_unavailable_view(
                            f"历史数据库已备份为 {backup.name}，重建失败({rebuild_error})"
                        )
        except OSError as error:
            self._use_unavailable_view(f"历史数据库无法打开({error})，未替换原数据库")
        self._executor = ThreadPoolExecutor(max_workers=1, thread_name_prefix="anima-history")

    def _use_unavailable_view(self, reason: str) -> None:
        self.available = False
        self._protect_storage(reason)
        # This database only supplies an empty read view. All write methods
        # and generation entry points must pass ensure_writable first.
        self.connection = sqlite3.connect(":memory:", check_same_thread=False)
        self._initialize(self.connection)

    def storage_status(self) -> dict[str, Any]:
        return {**super().storage_status(), "available": self.available}

    @staticmethod
    def _open(destination: Path) -> sqlite3.Connection:
        connection = sqlite3.connect(destination, check_same_thread=False)
        try:
            HistoryStore._backup_before_schema_changes(connection)
            HistoryStore._initialize(connection)
        except Exception:
            connection.close()
            raise
        return connection

    @staticmethod
    def _backup_before_schema_changes(connection: sqlite3.Connection) -> None:
        """Back up legacy data before ALTER/CREATE can modify its source file."""
        tables = {row[0] for row in connection.execute("SELECT name FROM sqlite_master WHERE type = 'table'")}
        if not tables.intersection({"batches", "images"}):
            return
        needs_backup = not {"batches", "images"}.issubset(tables)
        needs_single_backup = "prompt_jobs" not in tables
        for table in ("batches", "images"):
            if table not in tables:
                continue
            columns = {row[1] for row in connection.execute(f"PRAGMA table_info({table})")}
            if table == "images" and not {
                "resolved_selection_json", "resolved_prompt", "bound_resources_json"
            }.issubset(columns):
                needs_backup = True
            if table == "images" and "single_runtime_json" not in columns:
                needs_single_backup = True
            if "settings_json" not in columns:
                continue
            for row in connection.execute(f"SELECT settings_json FROM {table}"):
                try:
                    settings = json.loads(row[0])
                except (TypeError, json.JSONDecodeError):
                    continue
                if isinstance(settings, dict):
                    profile = settings.get("model_profile")
                    if profile is None or isinstance(profile, str) and not profile.strip():
                        needs_backup = True
                        break
        if needs_single_backup:
            HistoryStore._backup_before_single_workbench(connection)
        if needs_backup:
            HistoryStore._backup_before_migration(connection)

    @staticmethod
    def _backup_before_single_workbench(connection: sqlite3.Connection) -> Path | None:
        """Snapshot this upgrade independently of an older profile migration.

        A previously restored database may have accumulated new images since
        its earlier upgrade attempt, so existing single-workbench snapshots
        are preserved and a new numbered snapshot is created when necessary.
        """
        row = connection.execute("PRAGMA database_list").fetchone()
        database_path = str(row[2]) if row is not None else ""
        if not database_path:
            return None
        backup_path = Path(f"{database_path}.pre-single-workbench.bak")
        number = 2
        while backup_path.exists():
            backup_path = Path(f"{database_path}.pre-single-workbench-{number}.bak")
            number += 1
        reserved = False
        try:
            # Exclusive reservation also prevents overwriting a snapshot made
            # by another starting process between the existence check and open.
            with backup_path.open("xb"):
                reserved = True
            backup = sqlite3.connect(backup_path)
            try:
                connection.backup(backup)
            finally:
                backup.close()
        except (OSError, sqlite3.Error) as error:
            if reserved:
                try:
                    backup_path.unlink(missing_ok=True)
                except OSError:
                    pass
            raise HistoryMigrationError(
                f"单图工作台升级前无法创建数据库备份: {backup_path.name}"
            ) from error
        return backup_path

    @staticmethod
    def _initialize(connection: sqlite3.Connection) -> None:
        connection.row_factory = sqlite3.Row
        connection.execute("PRAGMA foreign_keys = ON")
        connection.executescript(
            """
            CREATE TABLE IF NOT EXISTS batches (
                id TEXT PRIMARY KEY,
                total INTEGER NOT NULL,
                completed INTEGER NOT NULL DEFAULT 0,
                status TEXT NOT NULL,
                error TEXT NOT NULL DEFAULT '',
                settings_json TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS images (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                batch_id TEXT NOT NULL REFERENCES batches(id) ON DELETE CASCADE,
                sequence INTEGER NOT NULL,
                prompt_id TEXT NOT NULL,
                filename TEXT NOT NULL,
                subfolder TEXT NOT NULL DEFAULT '',
                file_type TEXT NOT NULL DEFAULT 'output',
                positive_prompt TEXT NOT NULL DEFAULT '',
                negative_prompt TEXT NOT NULL DEFAULT '',
                sample_seed INTEGER NOT NULL,
                prompt_seed INTEGER NOT NULL,
                settings_json TEXT NOT NULL,
                created_at TEXT NOT NULL,
                bound_resources_json TEXT NOT NULL DEFAULT '{}'
            );
            CREATE INDEX IF NOT EXISTS idx_images_created ON images(id DESC);
            CREATE INDEX IF NOT EXISTS idx_images_batch ON images(batch_id, sequence);
            CREATE TABLE IF NOT EXISTS prompt_jobs (
                id TEXT PRIMARY KEY,
                status TEXT NOT NULL,
                input_fingerprint TEXT NOT NULL,
                settings_json TEXT NOT NULL,
                result_json TEXT NOT NULL DEFAULT 'null',
                prompt_id TEXT NOT NULL DEFAULT '',
                error TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            """
        )
        image_columns = {
            row[1] for row in connection.execute("PRAGMA table_info(images)").fetchall()
        }
        if "resolved_selection_json" not in image_columns:
            connection.execute(
                "ALTER TABLE images ADD COLUMN resolved_selection_json TEXT NOT NULL DEFAULT '{}'"
            )
        if "resolved_prompt" not in image_columns:
            connection.execute(
                "ALTER TABLE images ADD COLUMN resolved_prompt TEXT NOT NULL DEFAULT ''"
            )
        if "bound_resources_json" not in image_columns:
            connection.execute(
                "ALTER TABLE images ADD COLUMN bound_resources_json TEXT NOT NULL DEFAULT '{}'"
            )
        if "single_runtime_json" not in image_columns:
            connection.execute("ALTER TABLE images ADD COLUMN single_runtime_json TEXT NOT NULL DEFAULT '{}'")
        # Migrate before the normal restart-status update so the pre-migration
        # backup contains the exact legacy database state.
        HistoryStore._migrate_unprofiled_history(connection)
        # Match the legacy-aware filter exactly so both counts and descending
        # cursor pages avoid parsing every saved settings document. Build before
        # restart-status writes: an index failure must leave existing data intact.
        connection.execute(
            "CREATE INDEX IF NOT EXISTS idx_images_workbench_id ON images "
            "(COALESCE(json_extract(settings_json, '$.workbench'), 'random'), id DESC)"
        )
        connection.execute(
            "UPDATE batches SET status = 'interrupted', error = 'WebUI 重启，未完成批次未自动恢复', updated_at = ? WHERE status IN ('running', 'stopping')",
            (_now(),),
        )
        connection.execute(
            "UPDATE prompt_jobs SET status='interrupted',error='工作台重启，提示词任务未自动恢复',updated_at=? WHERE status IN ('queued','running','stopping')",
            (_now(),),
        )
        connection.commit()

    @staticmethod
    def _unprofiled_rows(
        connection: sqlite3.Connection,
    ) -> tuple[list[tuple[str, Any]], list[tuple[int, Any, str]]]:
        """Return rows whose settings JSON has no usable profile marker."""

        def is_unprofiled(raw_json: Any) -> bool:
            try:
                value = json.loads(raw_json)
            except (TypeError, json.JSONDecodeError):
                return False
            if not isinstance(value, dict):
                return False
            profile = value.get("model_profile")
            return profile is None or isinstance(profile, str) and not profile.strip()

        batches = [
            (str(row[0]), row[1])
            for row in connection.execute("SELECT id, settings_json FROM batches")
            if is_unprofiled(row[1])
        ]
        images = [
            (int(row[0]), row[1], str(row[2] or "{}"))
            for row in connection.execute(
                "SELECT id, settings_json, bound_resources_json FROM images"
            )
            if is_unprofiled(row[1])
        ]
        return batches, images

    @staticmethod
    def _backup_before_migration(connection: sqlite3.Connection) -> Path | None:
        """Create a stable SQLite snapshot before changing legacy records."""

        row = connection.execute("PRAGMA database_list").fetchone()
        database_path = str(row[2]) if row is not None else ""
        if not database_path:
            # In-memory databases are used by a few integrations and cannot
            # have a durable backup. The surrounding transaction still keeps
            # their migration atomic.
            return None
        backup_path = Path(f"{database_path}.pre-anima-v2.bak")
        if backup_path.exists():
            return backup_path
        try:
            backup = sqlite3.connect(backup_path)
            try:
                connection.backup(backup)
            finally:
                backup.close()
        except (OSError, sqlite3.Error) as error:
            try:
                backup_path.unlink(missing_ok=True)
            except OSError:
                pass
            raise HistoryMigrationError(
                f"旧历史记录迁移前无法创建数据库备份: {backup_path.name}"
            ) from error
        return backup_path

    @staticmethod
    def _canonical_settings(raw_json: Any) -> dict[str, Any]:
        source = json.loads(raw_json)
        if not isinstance(source, dict):
            raise ValueError("settings_json 必须是 JSON 对象")
        # Old, untagged settings always belong to Anima. Injecting the profile
        # before migration also prevents migrate_settings from guessing Krea 2
        # from an old model filename.
        source["model_profile"] = "anima"
        migrated = migrate_settings(source)
        return validate_settings(migrated)

    @staticmethod
    def _canonical_bound_resources(
        raw_json: Any, settings: dict[str, Any]
    ) -> dict[str, Any]:
        try:
            bound = json.loads(raw_json or "{}")
        except (TypeError, json.JSONDecodeError):
            bound = {}
        if not isinstance(bound, dict):
            bound = {}

        profile = get_model_profile("anima")
        defaults = {
            "unet": profile["model_name"],
            "clip": profile["clip_name"],
            "vae": profile["vae_name"],
            "seedvr2_model": profile["seedvr2_model"],
            "seedvr2_vae": profile["seedvr2_vae"],
        }
        for key, default in defaults.items():
            value = bound.get(key)
            bound[key] = str(value or default).replace("\\", "/")
        existing_loras = bound.get("loras")
        if isinstance(existing_loras, list):
            normalized_loras = []
            for filename in existing_loras:
                try:
                    normalized_loras.append(normalize_lora_path(filename))
                except (TypeError, ValueError):
                    continue
            bound["loras"] = normalized_loras
        else:
            bound["loras"] = [
                item["filename"]
                for item in settings.get("loras", [])
                if isinstance(item, dict) and item.get("enabled")
            ]
        bound["model_profile"] = "anima"
        return bound

    @staticmethod
    def _migrate_unprofiled_history(connection: sqlite3.Connection) -> None:
        batches, images = HistoryStore._unprofiled_rows(connection)
        if not batches and not images:
            return

        HistoryStore._backup_before_migration(connection)
        try:
            connection.execute("BEGIN IMMEDIATE")
            # Re-read under the write lock in case another process changed the
            # database between the initial check and the backup.
            batches, images = HistoryStore._unprofiled_rows(connection)
            batch_updates: list[tuple[str, str]] = []
            for batch_id, settings_json in batches:
                try:
                    settings = HistoryStore._canonical_settings(settings_json)
                except (TypeError, ValueError, json.JSONDecodeError) as error:
                    logger.warning("跳过无法规范化的历史批次 %s: %s", batch_id, error)
                    continue
                batch_updates.append(
                    (json.dumps(settings, ensure_ascii=False, separators=(",", ":")), batch_id)
                )

            image_updates: list[tuple[str, str, int]] = []
            for image_id, settings_json, bound_json in images:
                try:
                    settings = HistoryStore._canonical_settings(settings_json)
                except (TypeError, ValueError, json.JSONDecodeError) as error:
                    logger.warning("跳过无法规范化的历史图片 %s: %s", image_id, error)
                    continue
                bound_resources = HistoryStore._canonical_bound_resources(bound_json, settings)
                image_updates.append(
                    (
                        json.dumps(settings, ensure_ascii=False, separators=(",", ":")),
                        json.dumps(bound_resources, ensure_ascii=False, separators=(",", ":")),
                        image_id,
                    )
                )

            connection.executemany(
                "UPDATE batches SET settings_json = ? WHERE id = ?", batch_updates
            )
            connection.executemany(
                "UPDATE images SET settings_json = ?, bound_resources_json = ? WHERE id = ?",
                image_updates,
            )
            connection.commit()
        except Exception as error:
            connection.rollback()
            if isinstance(error, HistoryMigrationError):
                raise
            raise HistoryMigrationError("历史数据库档案迁移事务失败，原记录已回滚") from error

    async def _call(self, func: Callable[..., Any], *args: Any, **kwargs: Any) -> Any:
        loop = asyncio.get_running_loop()
        return await loop.run_in_executor(
            self._executor, functools.partial(self._execute, func, *args, **kwargs)
        )

    def _execute(self, func: Callable[..., Any], *args: Any, **kwargs: Any) -> Any:
        try:
            return func(*args, **kwargs)
        except Exception:
            # SQLite can leave a transaction open after execute/commit fails.
            # Roll it back on the same worker before another request can read
            # its uncommitted data or accidentally commit the failed write.
            # This boundary also covers PromptJobStore, which shares _call.
            try:
                if self.connection.in_transaction:
                    self.connection.rollback()
            except sqlite3.Error as rollback_error:
                self._protect_storage(f"历史数据库事务回滚失败({rollback_error})")
            raise

    async def create_batch(self, batch_id: str, total: int, settings: dict[str, Any]) -> dict[str, Any]:
        self.ensure_writable()
        return await self._call(self._create_batch, batch_id, total, settings)

    def _create_batch(self, batch_id: str, total: int, settings: dict[str, Any]) -> dict[str, Any]:
        self.ensure_writable()
        timestamp = _now()
        self.connection.execute(
            "INSERT INTO batches (id, total, completed, status, error, settings_json, created_at, updated_at) VALUES (?, ?, 0, 'running', '', ?, ?, ?)",
            (batch_id, total, json.dumps(settings, ensure_ascii=False), timestamp, timestamp),
        )
        self.connection.commit()
        return self._get_batch(batch_id)

    async def update_batch(
        self,
        batch_id: str,
        *,
        completed: int | None = None,
        status: str | None = None,
        error: str | None = None,
    ) -> dict[str, Any]:
        self.ensure_writable()
        return await self._call(
            self._update_batch, batch_id, completed=completed, status=status, error=error
        )

    def _update_batch(
        self,
        batch_id: str,
        *,
        completed: int | None = None,
        status: str | None = None,
        error: str | None = None,
    ) -> dict[str, Any]:
        self.ensure_writable()
        fields = ["updated_at = ?"]
        values: list[Any] = [_now()]
        if completed is not None:
            fields.append("completed = ?")
            values.append(completed)
        if status is not None:
            fields.append("status = ?")
            values.append(status)
        if error is not None:
            fields.append("error = ?")
            values.append(error)
        values.append(batch_id)
        self.connection.execute(f"UPDATE batches SET {', '.join(fields)} WHERE id = ?", values)
        self.connection.commit()
        return self._get_batch(batch_id)

    async def add_image(self, **kwargs: Any) -> dict[str, Any]:
        self.ensure_writable()
        return await self._call(self._add_image, **kwargs)

    def _add_image(
        self,
        *,
        batch_id: str,
        sequence: int,
        prompt_id: str,
        image: dict[str, Any],
        positive_prompt: str,
        negative_prompt: str,
        sample_seed: int,
        prompt_seed: int,
        settings: dict[str, Any],
        resolved_selection: dict[str, Any] | None = None,
        resolved_prompt: str = "",
        bound_resources: dict[str, Any] | None = None,
        single_runtime: dict[str, Any] | None = None,
    ) -> dict[str, Any]:
        self.ensure_writable()
        cursor = self.connection.execute(
            """
            INSERT INTO images (
                batch_id, sequence, prompt_id, filename, subfolder, file_type,
                positive_prompt, negative_prompt, sample_seed, prompt_seed,
                settings_json, created_at, resolved_selection_json, resolved_prompt,
                bound_resources_json, single_runtime_json
            ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            (
                batch_id,
                sequence,
                prompt_id,
                str(image["filename"]),
                str(image.get("subfolder") or ""),
                str(image.get("type") or "output"),
                positive_prompt,
                negative_prompt,
                sample_seed,
                prompt_seed,
                json.dumps(settings, ensure_ascii=False),
                _now(),
                json.dumps(resolved_selection or {}, ensure_ascii=False),
                str(resolved_prompt or ""),
                json.dumps(bound_resources or {}, ensure_ascii=False),
                json.dumps(single_runtime or {}, ensure_ascii=False),
            ),
        )
        self.connection.commit()
        return self._get_image(cursor.lastrowid)

    async def get_batch(self, batch_id: str) -> dict[str, Any]:
        return await self._call(self._get_batch, batch_id)

    def _get_batch(self, batch_id: str) -> dict[str, Any]:
        row = self.connection.execute("SELECT * FROM batches WHERE id = ?", (batch_id,)).fetchone()
        if row is None:
            raise KeyError(batch_id)
        result = dict(row)
        result["settings"] = json.loads(result.pop("settings_json"))
        return result

    async def get_image(self, image_id: int) -> dict[str, Any]:
        return await self._call(self._get_image, image_id)

    def _get_image(self, image_id: int) -> dict[str, Any]:
        row = self.connection.execute(
            "SELECT images.*, batches.total AS batch_total FROM images JOIN batches ON batches.id = images.batch_id WHERE images.id = ?",
            (image_id,),
        ).fetchone()
        if row is None:
            raise KeyError(image_id)
        return self._image_row(row)

    def _image_row(self, row: sqlite3.Row) -> dict[str, Any]:
        result = dict(row)
        result["settings"] = json.loads(result.pop("settings_json"))
        result["resolved_selection"] = json.loads(result.pop("resolved_selection_json", "{}") or "{}")
        result["bound_resources"] = json.loads(result.pop("bound_resources_json", "{}") or "{}")
        result["single_runtime"] = json.loads(result.pop("single_runtime_json", "{}") or "{}")
        result["workbench"] = result["settings"].get("workbench", "random")
        return result

    async def list_images(
        self, page: int = 1, limit: int = 24, workbench: str | None = None,
        *, before_id: int | None = None,
    ) -> dict[str, Any]:
        return await self._call(self._list_images, page, limit, workbench, before_id=before_id)

    def _list_images(
        self, page: int = 1, limit: int = 24, workbench: str | None = None,
        *, before_id: int | None = None,
    ) -> dict[str, Any]:
        if before_id is not None and (
            type(before_id) is not int or not 1 <= before_id <= 2**63 - 1
        ):
            raise ValueError("before_id 必须为正整数图片 ID")
        page = max(1, page)
        limit = min(60, max(1, limit))
        where = " WHERE COALESCE(json_extract(images.settings_json, '$.workbench'), 'random') = ?" if workbench else ""
        params = (workbench,) if workbench else ()
        total = int(self.connection.execute("SELECT COUNT(*) FROM images" + where, params).fetchone()[0])
        if before_id is not None:
            where += (" AND " if where else " WHERE ") + "images.id < ?"
            params += (before_id,)
        # Fetch one extra matching row, so an exact-size last page reports no
        # further results. Cursor requests never use a shifting row offset.
        pagination = " LIMIT ?" if before_id is not None else " LIMIT ? OFFSET ?"
        pagination_params = (limit + 1,) if before_id is not None else (limit + 1, (page - 1) * limit)
        rows = self.connection.execute(
            """
            SELECT images.*, batches.total AS batch_total
            FROM images JOIN batches ON batches.id = images.batch_id
            """ + where + " ORDER BY images.id DESC" + pagination,
            (*params, *pagination_params),
        ).fetchall()
        has_more = len(rows) > limit
        rows = rows[:limit]
        return {
            "items": [self._image_row(row) for row in rows],
            "page": page,
            "limit": limit,
            "total": total,
            "pages": max(1, (total + limit - 1) // limit),
            "next_before_id": int(rows[-1]["id"]) if rows else None,
            "has_more": has_more,
        }

    async def delete_image(self, image_id: int) -> bool:
        self.ensure_writable()
        return await self._call(self._delete_image, image_id)

    def _delete_image(self, image_id: int) -> bool:
        self.ensure_writable()
        cursor = self.connection.execute("DELETE FROM images WHERE id = ?", (image_id,))
        self.connection.commit()
        return cursor.rowcount > 0

    async def close(self) -> None:
        await self._call(self.connection.close)
        self._executor.shutdown(wait=True)
