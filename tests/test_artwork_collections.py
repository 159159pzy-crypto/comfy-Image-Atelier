from __future__ import annotations

import sqlite3
import tempfile
import unittest
from contextlib import closing
from pathlib import Path
from unittest import mock

from anima_webui.history import HistoryStore
from anima_webui.workflow import WorkflowError
from anima_webui.persistence import StorageReadOnlyError
from test_storage_transactions import FailingConnection


async def add_image(store, filename="image.png", workbench="random", model="anima"):
    return await store.add_image(batch_id="test", sequence=1, prompt_id="prompt", image={"filename": filename},
                                 positive_prompt="hello", negative_prompt="", sample_seed=1, prompt_seed=2,
                                 settings={"workbench": workbench, "model_profile": model})


class ArtworkCollectionTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.path = Path(self.temp.name) / "history.sqlite3"
        connect = sqlite3.connect
        with mock.patch("anima_webui.history.sqlite3.connect", side_effect=lambda *a, **kw: connect(*a, factory=FailingConnection, **kw)):
            self.store = HistoryStore(self.path)
        await self.store.create_batch("test", 20, {"model_profile": "anima"})

    async def asyncTearDown(self):
        await self.store.close()
        self.temp.cleanup()

    async def test_multiple_memberships_counts_filter_and_cursor(self):
        folder = (await self.store.create_artwork_folder("精选"))["folder"]["id"]
        images = [await add_image(self.store, f"{n}.png", "single" if n % 2 else "random", "krea2" if n % 2 else "anima") for n in range(6)]
        ids = [image["id"] for image in images]
        await self.store.update_artwork_memberships(ids, [folder, "default"])
        await self.store.update_artwork_memberships(ids, [folder])
        listing = await self.store.list_artwork_folders()
        self.assertEqual(listing["total"], 6)
        self.assertEqual([row["count"] for row in listing["folders"]], [6, 6])
        first = await self.store.list_images(limit=2, workbench="single", folder_id=folder)
        self.assertEqual(first["total"], 3)
        self.assertEqual([row["id"] for row in first["items"]], [ids[5], ids[3]])
        self.assertTrue(first["has_more"])
        last = await self.store.list_images(limit=2, workbench="single", folder_id=folder, before_id=first["next_before_id"])
        self.assertEqual([row["id"] for row in last["items"]], [ids[1]])
        self.assertFalse(last["has_more"])
        self.assertEqual(set(first["items"][0]["folder_ids"]), {folder, "default"})

    async def test_delete_folder_moves_only_orphans_and_delete_record_cascades(self):
        one = (await add_image(self.store))["id"]
        two = (await add_image(self.store, "two.png"))["id"]
        first = (await self.store.create_artwork_folder("第一"))["folder"]["id"]
        second = (await self.store.create_artwork_folder("第二"))["folder"]["id"]
        await self.store.update_artwork_memberships([one, two], [first])
        await self.store.update_artwork_memberships([two], [second])
        deleted = await self.store.delete_artwork_folder(first)
        self.assertEqual(deleted["moved_count"], 1)
        self.assertEqual((await self.store.get_image(one))["folder_ids"], ["default"])
        self.assertEqual((await self.store.get_image(two))["folder_ids"], [second])
        await self.store.delete_image(two)
        self.assertEqual((await self.store.list_artwork_folders())["total"], 1)
        self.assertEqual((await self.store.list_images(favorites_only=True))["total"], 1)

    async def test_remove_and_clear_are_explicit_unfavorite_and_survive_restart(self):
        image_id = (await add_image(self.store))["id"]
        folder = (await self.store.create_artwork_folder("持久化"))["folder"]["id"]
        await self.store.update_artwork_memberships([image_id], [folder, "default"])
        await self.store.close()
        self.store = HistoryStore(self.path)
        self.assertEqual(len((await self.store.get_image(image_id))["folder_ids"]), 2)
        await self.store.update_artwork_memberships([image_id], [folder], "remove")
        self.assertEqual((await self.store.get_image(image_id))["folder_ids"], ["default"])
        await self.store.update_artwork_memberships([image_id], [], "clear")
        self.assertEqual((await self.store.list_images(favorites_only=True))["total"], 0)

    async def test_atomic_validation_system_folder_and_duplicate_names(self):
        image_id = (await add_image(self.store))["id"]
        for action in (lambda: self.store.rename_artwork_folder("default", "rename"), lambda: self.store.delete_artwork_folder("default")):
            with self.assertRaises(WorkflowError):
                await action()
        await self.store.create_artwork_folder(" Shared ")
        with self.assertRaises(WorkflowError):
            await self.store.create_artwork_folder("shared")
        with self.assertRaises(KeyError):
            await self.store.update_artwork_memberships([image_id, 999], ["default"])
        self.assertEqual((await self.store.get_image(image_id))["folder_ids"], [])
        with self.assertRaises(KeyError):
            await self.store.update_artwork_memberships([image_id], ["default", "missing"])
        self.assertEqual((await self.store.list_artwork_folders())["total"], 0)
        for ids in ([], [True], [0], [1] * 201):
            with self.assertRaises(WorkflowError):
                await self.store.update_artwork_memberships(ids, ["default"])
        for action in ([], {}, True, "unknown"):
            with self.assertRaises(WorkflowError):
                await self.store.update_artwork_memberships([image_id], ["default"], action)

    async def test_failed_commit_rolls_back_memberships_and_failed_rollback_protects(self):
        image_id = (await add_image(self.store))["id"]
        await self.store._call(setattr, self.store.connection, "fail_commit", True)
        with self.assertRaises(sqlite3.Error):
            await self.store.update_artwork_memberships([image_id], ["default"])
        self.assertEqual((await self.store.get_image(image_id))["folder_ids"], [])
        await self.store._call(setattr, self.store.connection, "fail_commit", True)
        await self.store._call(setattr, self.store.connection, "fail_rollback", True)
        with self.assertRaises(sqlite3.Error):
            await self.store.create_artwork_folder("failed")
        with self.assertRaises(StorageReadOnlyError):
            await self.store.create_artwork_folder("blocked")

    async def test_upgrade_snapshot_precedes_schema_creation(self):
        image_id = (await add_image(self.store))["id"]
        await self.store.close()
        with closing(sqlite3.connect(self.path)) as connection:
            connection.executescript("DROP TABLE artwork_memberships; DROP TABLE artwork_folders;")
        self.store = HistoryStore(self.path)
        backup = Path(f"{self.path}.pre-artwork-collections.bak")
        self.assertTrue(backup.is_file())
        with closing(sqlite3.connect(backup)) as connection:
            self.assertEqual(connection.execute("SELECT id FROM images").fetchone()[0], image_id)
            self.assertIsNone(connection.execute("SELECT name FROM sqlite_master WHERE name='artwork_folders'").fetchone())
        self.assertEqual((await self.store.get_image(image_id))["folder_ids"], [])

    async def test_failed_upgrade_backup_preserves_database(self):
        await self.store.close()
        with closing(sqlite3.connect(self.path)) as connection:
            connection.executescript("DROP TABLE artwork_memberships; DROP TABLE artwork_folders;")
        before = self.path.read_bytes()
        with mock.patch.object(Path, "open", side_effect=PermissionError("no backup")):
            self.store = HistoryStore(self.path)
        self.assertFalse(self.store.available)
        self.assertEqual(self.path.read_bytes(), before)
