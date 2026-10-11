from __future__ import annotations

import json
import sqlite3
import tempfile
import unittest
from pathlib import Path

from anima_webui.history import HistoryMigrationError, HistoryStore


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


class HistoryMigrationTests(unittest.IsolatedAsyncioTestCase):
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

            with self.assertRaises(HistoryMigrationError):
                HistoryStore(path)

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
