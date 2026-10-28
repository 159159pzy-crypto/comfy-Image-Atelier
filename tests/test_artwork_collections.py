"""Compatibility with databases created before artwork management was removed."""
from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

from anima_webui.history import HistoryStore


async def add_image(store, filename="image.png", workbench="random", model="anima"):
    return await store.add_image(batch_id="test", sequence=1, prompt_id="prompt", image={"filename": filename},
                                 positive_prompt="hello", negative_prompt="", sample_seed=1, prompt_seed=2,
                                 settings={"workbench": workbench, "model_profile": model})


def legacy_management_tables(connection):
    connection.executescript("""
        CREATE TABLE artwork_folders(id TEXT PRIMARY KEY, name TEXT NOT NULL, created_at TEXT NOT NULL);
        CREATE TABLE artwork_memberships(
            folder_id TEXT NOT NULL REFERENCES artwork_folders(id) ON DELETE CASCADE,
            image_id INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE,
            PRIMARY KEY(folder_id,image_id));
        INSERT INTO artwork_folders VALUES('default','Saved favorites','before upgrade');
        CREATE TABLE managed_image_files (
            kind TEXT NOT NULL, asset_id TEXT NOT NULL, root TEXT NOT NULL,
            relative_path TEXT NOT NULL, sha256 TEXT NOT NULL, size INTEGER NOT NULL,
            created_at REAL NOT NULL, last_used_at REAL NOT NULL, origin TEXT NOT NULL,
            PRIMARY KEY(kind,asset_id,root));
        INSERT INTO managed_image_files VALUES('input','old-asset','old-root','old.png','digest',10,1,1,'upload');
        CREATE TABLE image_draft_leases (
            session_id TEXT NOT NULL,kind TEXT NOT NULL,asset_id TEXT NOT NULL,expires_at REAL NOT NULL,
            PRIMARY KEY(session_id,kind,asset_id));
        INSERT INTO image_draft_leases VALUES('old-session','input','old-asset',1);
        CREATE TABLE image_cleanup_settings (
            id INTEGER PRIMARY KEY CHECK(id=1),enabled INTEGER NOT NULL,
            retention_days INTEGER NOT NULL,last_run TEXT);
        INSERT INTO image_cleanup_settings VALUES(1,1,7,'{"deleted_files":3}');
    """)


class LegacyArtworkDataTests(unittest.IsolatedAsyncioTestCase):
    async def test_reopen_preserves_retired_tables_without_exposing_favorites(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "history.sqlite3"
            store = HistoryStore(path)
            await store.create_batch("test", 1, {"model_profile": "anima"})
            image = await add_image(store)
            await store._call(legacy_management_tables, store.connection)

            def seed():
                with store.connection:
                    store.connection.execute("INSERT INTO artwork_memberships VALUES('default',?)", (image["id"],))

            await store._call(seed)
            names = ("artwork_folders", "artwork_memberships", "managed_image_files",
                     "image_draft_leases", "image_cleanup_settings")
            before = await store._call(lambda: {name: [tuple(row) for row in store.connection.execute(f"SELECT * FROM {name}")]
                                                for name in names})
            await store.close()
            store = HistoryStore(path)
            try:
                records = await store.list_images()
                self.assertEqual(records["total"], 1)
                self.assertNotIn("folder_ids", records["items"][0])
                after = await store._call(lambda: {name: [tuple(row) for row in store.connection.execute(f"SELECT * FROM {name}")]
                                                   for name in names})
                self.assertEqual(after, before)
                self.assertEqual(list(Path(directory).glob("*.pre-artwork-collections*.bak")), [])
            finally:
                await store.close()

    async def test_fresh_history_does_not_create_retired_management_tables(self):
        with tempfile.TemporaryDirectory() as directory:
            store = HistoryStore(Path(directory) / "history.sqlite3")
            try:
                names = await store._call(lambda: {row[0] for row in store.connection.execute("SELECT name FROM sqlite_master")})
                self.assertTrue(names.isdisjoint({"artwork_folders", "artwork_memberships", "managed_image_files",
                                                 "image_draft_leases", "image_cleanup_settings"}))
            finally:
                await store.close()
