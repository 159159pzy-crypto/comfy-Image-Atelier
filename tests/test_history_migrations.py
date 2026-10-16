from __future__ import annotations

import json
import sqlite3
import tempfile
import unittest
from contextlib import closing
from pathlib import Path
from unittest.mock import patch

from anima_webui.history import HistoryStore


def _create_legacy_history(path: Path) -> tuple[str, str]:
    """Create a pre-profile database without touching the user's data store."""

    legacy = {
        "model_name": "krea2_turbo_int8_convrot.safetensors",
        "steps": 20,
        "hires": {"enabled": True, "model_name": "4x_foolhardy_Remacri.pth", "percent": 50},
        "second_sampler": {
            "enabled": True,
            "steps": 12,
            "cfg": 3.5,
            "sampler_name": "er_sde",
            "scheduler": "simple",
            "denoise": 0.25,
        },
        "loras": [
            {"filename": "styles\\artist\\one.safetensors", "enabled": True, "strength": 0.8}
        ],
    }
    tagged = {
        "model_profile": "krea2",
        "model_name": "krea2_turbo_int8_convrot.safetensors",
    }
    legacy_json = json.dumps(legacy, ensure_ascii=False)
    tagged_json = json.dumps(tagged, ensure_ascii=False)
    connection = sqlite3.connect(path)
    try:
        connection.executescript(
            """
            CREATE TABLE batches (
                id TEXT PRIMARY KEY,
                total INTEGER NOT NULL,
                completed INTEGER NOT NULL DEFAULT 0,
                status TEXT NOT NULL,
                error TEXT NOT NULL DEFAULT '',
                settings_json TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE images (
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
                created_at TEXT NOT NULL
            );
            """
        )
        connection.execute(
            "INSERT INTO batches VALUES (?, 1, 0, 'completed', '', ?, 'created', 'updated')",
            ("legacy", legacy_json),
        )
        connection.execute(
            "INSERT INTO batches VALUES (?, 1, 0, 'completed', '', ?, 'created', 'updated')",
            ("tagged", tagged_json),
        )
        connection.execute(
            """
            INSERT INTO images (
                batch_id, sequence, prompt_id, filename, sample_seed, prompt_seed,
                settings_json, created_at
            ) VALUES (?, 1, 'prompt-legacy', 'legacy.png', 7, 8, ?, 'created')
            """,
            ("legacy", legacy_json),
        )
        connection.execute(
            """
            INSERT INTO images (
                batch_id, sequence, prompt_id, filename, sample_seed, prompt_seed,
                settings_json, created_at
            ) VALUES (?, 1, 'prompt-tagged', 'tagged.png', 9, 10, ?, 'created')
            """,
            ("tagged", tagged_json),
        )
        connection.commit()
    finally:
        connection.close()
    return legacy_json, tagged_json


def _create_pre_single_history_with_old_backup(path: Path) -> Path:
    """Model a current v2 database that has outgrown its original backup."""
    _, tagged_json = _create_legacy_history(path)
    old_backup_path = Path(f"{path}.pre-anima-v2.bak")
    with closing(sqlite3.connect(path)) as connection:
        for name in ("resolved_selection_json", "bound_resources_json"):
            connection.execute(f"ALTER TABLE images ADD COLUMN {name} TEXT NOT NULL DEFAULT '{{}}'")
        connection.execute("ALTER TABLE images ADD COLUMN resolved_prompt TEXT NOT NULL DEFAULT ''")
        connection.execute("UPDATE batches SET settings_json = ?", (tagged_json,))
        connection.execute("UPDATE images SET settings_json = ?", (tagged_json,))
        connection.commit()
        with closing(sqlite3.connect(old_backup_path)) as backup:
            connection.backup(backup)
        for sequence in (2, 3):
            connection.execute(
                """
                INSERT INTO images (
                    batch_id, sequence, prompt_id, filename, sample_seed, prompt_seed,
                    settings_json, created_at
                ) VALUES ('tagged', ?, ?, ?, 11, 12, ?, 'newer')
                """,
                (sequence, f"prompt-new-{sequence}", f"new-{sequence}.png", tagged_json),
            )
        connection.commit()
    return old_backup_path


class HistoryMigrationTests(unittest.IsolatedAsyncioTestCase):
    async def test_single_upgrade_snapshots_latest_history_before_schema_changes(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "history.sqlite3"
            old_backup_path = _create_pre_single_history_with_old_backup(path)
            old_backup_bytes = old_backup_path.read_bytes()
            store = HistoryStore(path)
            try:
                self.assertTrue(store.available)
                self.assertEqual((await store.list_images())["total"], 4)
            finally:
                await store.close()

            backup_path = Path(f"{path}.pre-single-workbench.bak")
            with closing(sqlite3.connect(backup_path)) as backup:
                self.assertEqual(
                    [row[0] for row in backup.execute("SELECT filename FROM images ORDER BY id")],
                    ["legacy.png", "tagged.png", "new-2.png", "new-3.png"],
                )
                columns = {row[1] for row in backup.execute("PRAGMA table_info(images)")}
                self.assertNotIn("single_runtime_json", columns)
                self.assertIsNone(backup.execute(
                    "SELECT name FROM sqlite_master WHERE name = 'prompt_jobs'"
                ).fetchone())
            self.assertEqual(old_backup_path.read_bytes(), old_backup_bytes)
            with closing(sqlite3.connect(path)) as upgraded:
                self.assertIn("single_runtime_json", {
                    row[1] for row in upgraded.execute("PRAGMA table_info(images)")
                })
                self.assertIsNotNone(upgraded.execute(
                    "SELECT name FROM sqlite_master WHERE name = 'prompt_jobs'"
                ).fetchone())

            reopened = HistoryStore(path)
            await reopened.close()
            self.assertEqual(
                [item.name for item in path.parent.glob("*.pre-single-workbench*.bak")],
                [backup_path.name],
            )

    async def test_single_backup_failure_preserves_original_database(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "history.sqlite3"
            old_backup_path = _create_pre_single_history_with_old_backup(path)
            original_bytes = path.read_bytes()
            old_backup_bytes = old_backup_path.read_bytes()
            backup_path = Path(f"{path}.pre-single-workbench.bak")
            real_connect = sqlite3.connect

            def fail_backup_open(destination, *args, **kwargs):
                if Path(destination) == backup_path:
                    raise sqlite3.OperationalError("test backup disk failure")
                return real_connect(destination, *args, **kwargs)

            with patch("anima_webui.history.sqlite3.connect", side_effect=fail_backup_open):
                store = HistoryStore(path)
            try:
                self.assertFalse(store.available)
                self.assertIn("单图工作台升级前无法创建数据库备份", store.read_only_reason)
            finally:
                await store.close()

            self.assertEqual(path.read_bytes(), original_bytes)
            self.assertEqual(old_backup_path.read_bytes(), old_backup_bytes)
            self.assertFalse(backup_path.exists())
            with closing(sqlite3.connect(path)) as original:
                self.assertEqual(original.execute("SELECT COUNT(*) FROM images").fetchone()[0], 4)
                self.assertNotIn("single_runtime_json", {
                    row[1] for row in original.execute("PRAGMA table_info(images)")
                })
                self.assertIsNone(original.execute(
                    "SELECT name FROM sqlite_master WHERE name = 'prompt_jobs'"
                ).fetchone())

    async def test_single_upgrade_preserves_existing_snapshot_of_restored_database(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "history.sqlite3"
            old_backup_path = _create_pre_single_history_with_old_backup(path)
            existing_snapshot = Path(f"{path}.pre-single-workbench.bak")
            existing_snapshot.write_bytes(old_backup_path.read_bytes())
            original_snapshot_bytes = existing_snapshot.read_bytes()
            store = HistoryStore(path)
            try:
                self.assertTrue(store.available)
            finally:
                await store.close()

            self.assertEqual(existing_snapshot.read_bytes(), original_snapshot_bytes)
            with closing(sqlite3.connect(Path(f"{path}.pre-single-workbench-2.bak"))) as backup:
                self.assertEqual(backup.execute("SELECT COUNT(*) FROM images").fetchone()[0], 4)
                self.assertNotIn("single_runtime_json", {
                    row[1] for row in backup.execute("PRAGMA table_info(images)")
                })

    async def test_legacy_records_are_backed_up_and_migrated_to_anima_once(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "history.sqlite3"
            legacy_json, tagged_json = _create_legacy_history(path)
            store = HistoryStore(path)
            store_closed = False
            try:
                batch = await store.get_batch("legacy")
                self.assertEqual(batch["settings"]["model_profile"], "anima")
                self.assertNotIn("hires", batch["settings"])
                self.assertNotIn("second_sampler", batch["settings"])
                self.assertTrue(batch["settings"]["upscalers"]["latent"]["enabled"])
                self.assertEqual(batch["settings"]["upscalers"]["latent"]["scale_by"], 2.0)
                self.assertEqual(
                    batch["settings"]["loras"][0]["filename"],
                    "styles/artist/one.safetensors",
                )
                self.assertEqual((await store.get_batch("tagged"))["settings"], json.loads(tagged_json))

                page = await store.list_images(limit=10)
                legacy_image = next(item for item in page["items"] if item["batch_id"] == "legacy")
                tagged_image = next(item for item in page["items"] if item["batch_id"] == "tagged")
                self.assertEqual(legacy_image["settings"]["model_profile"], "anima")
                self.assertEqual(
                    legacy_image["settings"]["loras"][0]["filename"],
                    "styles/artist/one.safetensors",
                )
                self.assertEqual(legacy_image["bound_resources"]["model_profile"], "anima")
                self.assertEqual(
                    legacy_image["bound_resources"]["loras"],
                    ["styles/artist/one.safetensors"],
                )
                self.assertEqual(tagged_image["settings"], json.loads(tagged_json))
                self.assertEqual(tagged_image["bound_resources"], {})

                migrated_batch_json = json.dumps(batch["settings"], ensure_ascii=False, separators=(",", ":"))
                migrated_image_json = json.dumps(
                    legacy_image["settings"], ensure_ascii=False, separators=(",", ":")
                )
                await store.close()
                store_closed = True

                backup_path = Path(f"{path}.pre-anima-v2.bak")
                self.assertTrue(backup_path.is_file())
                backup = sqlite3.connect(backup_path)
                try:
                    self.assertEqual(
                        backup.execute(
                            "SELECT settings_json FROM batches WHERE id = 'legacy'"
                        ).fetchone()[0],
                        legacy_json,
                    )
                finally:
                    backup.close()

                reopened = HistoryStore(path)
                try:
                    current_batch = await reopened.get_batch("legacy")
                    current_images = await reopened.list_images(limit=10)
                    current_image = next(
                        item for item in current_images["items"] if item["batch_id"] == "legacy"
                    )
                    self.assertEqual(
                        json.dumps(current_batch["settings"], ensure_ascii=False, separators=(",", ":")),
                        migrated_batch_json,
                    )
                    self.assertEqual(
                        json.dumps(current_image["settings"], ensure_ascii=False, separators=(",", ":")),
                        migrated_image_json,
                    )
                finally:
                    await reopened.close()
            finally:
                if not store_closed:
                    await store.close()

    async def test_migration_transaction_rolls_back_every_record_on_write_failure(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "history.sqlite3"
            legacy_json, _ = _create_legacy_history(path)
            connection = sqlite3.connect(path)
            try:
                connection.execute(
                    """
                    CREATE TRIGGER reject_image_migration
                    BEFORE UPDATE OF settings_json ON images
                    BEGIN SELECT RAISE(ABORT, 'test migration failure'); END
                    """
                )
                connection.commit()
            finally:
                connection.close()

            store = HistoryStore(path)
            try:
                self.assertFalse(store.available)
                self.assertIn("迁移失败", store.read_only_reason)
                self.assertEqual((await store.list_images())["items"], [])
            finally:
                await store.close()

            connection = sqlite3.connect(path)
            try:
                self.assertEqual(
                    connection.execute(
                        "SELECT settings_json FROM batches WHERE id = 'legacy'"
                    ).fetchone()[0],
                    legacy_json,
                )
                self.assertEqual(
                    connection.execute(
                        "SELECT settings_json FROM images WHERE batch_id = 'legacy'"
                    ).fetchone()[0],
                    legacy_json,
                )
            finally:
                connection.close()
