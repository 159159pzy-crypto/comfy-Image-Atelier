from __future__ import annotations

import asyncio
from pathlib import Path
import sqlite3
import tempfile
import threading
import unittest
from unittest import mock

from anima_webui.artwork_deletion import ArtworkDeletionService, deletion_image_path
from anima_webui.history import HistoryStore
from anima_webui.image_location import ImageLocationError
from anima_webui.workflow import WorkflowError
from test_artwork_collections import add_image, legacy_management_tables
from test_storage_transactions import FailingConnection


class ArtworkDeletionTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.directory = Path(self.temp.name)
        connect = sqlite3.connect
        with mock.patch("anima_webui.history.sqlite3.connect", side_effect=lambda *a, **kw: connect(*a, factory=FailingConnection, **kw)):
            self.store = HistoryStore(self.directory / "history.sqlite3")
        await self.store.create_batch("test", 20, {"model_profile": "anima"})
        await self.store._call(legacy_management_tables, self.store.connection)
        self.thumbnails = mock.Mock(discard=mock.AsyncMock())
        self.service = ArtworkDeletionService(self.store, self.thumbnails)
        self.patches = [mock.patch("anima_webui.artwork_deletion.image_location.IS_WINDOWS", True),
                        mock.patch("anima_webui.artwork_deletion.image_location.verify_local_comfy"),
                        mock.patch("anima_webui.artwork_deletion.image_location.comfy_image_directory", return_value=self.directory)]
        for patch in self.patches:
            patch.start()

    async def asyncTearDown(self):
        for patch in reversed(self.patches):
            patch.stop()
        await self.store.close()
        self.temp.cleanup()

    async def memberships(self, image_id):
        return await self.store._call(lambda: [row[0] for row in self.store.connection.execute(
            "SELECT folder_id FROM artwork_memberships WHERE image_id=?", (image_id,))])

    async def membership_count(self):
        return await self.store._call(lambda: self.store.connection.execute(
            "SELECT COUNT(*) FROM artwork_memberships").fetchone()[0])

    async def image(self, filename="image.png", create=True):
        if create:
            (self.directory / filename).write_bytes(b"original")
        image_id = (await add_image(self.store, filename))["id"]
        def favorite():
            with self.store.connection:
                self.store.connection.execute("INSERT INTO artwork_memberships VALUES('default',?)", (image_id,))
        await self.store._call(favorite)
        return image_id

    async def test_record_only_keeps_original_cascades_favorites_and_missing_is_idempotent(self):
        image_id = await self.image()
        result = await self.service.delete([image_id, 999])
        self.assertEqual(result["deleted_count"], 1)
        self.assertEqual(result["success_count"], 2)
        self.assertTrue((self.directory / "image.png").is_file())
        self.assertEqual(await self.membership_count(), 0)
        self.assertEqual(result["results"][1]["code"], "already_missing")

    async def test_invalid_mode_has_no_effect(self):
        image_id = await self.image()
        for mode in ([], {}, True, "unknown"):
            with self.assertRaises(WorkflowError):
                await self.service.delete([image_id], mode)
        self.assertEqual(await self.memberships(image_id), ["default"])
        self.assertTrue((self.directory / "image.png").exists())

    async def test_shared_file_requires_all_references_and_unlinks_once(self):
        one, two = await self.image(), await self.image()
        rejected = await self.service.delete([one], "file")
        self.assertEqual(rejected["results"][0]["code"], "file_shared")
        self.assertEqual(rejected["results"][0]["shared_ids"], [two])
        self.assertTrue((self.directory / "image.png").exists())
        deleted = await self.service.delete([one, two], "file")
        self.assertEqual(deleted["deleted_count"], 2)
        self.assertEqual(deleted["file_deleted_count"], 1)
        self.assertFalse((self.directory / "image.png").exists())
        self.assertNotIn("file_key", deleted["results"][0])

    async def test_missing_file_completes_but_unavailable_root_preserves_record(self):
        image_id = await self.image(create=False)
        result = await self.service.delete([image_id], "file")
        self.assertTrue(result["results"][0]["file_missing"])
        self.assertTrue(result["results"][0]["record_deleted"])
        other = await self.image()
        with mock.patch("anima_webui.artwork_deletion.image_location.comfy_image_directory", return_value=self.directory / "unavailable"):
            result = await self.service.delete([other], "file")
        self.assertEqual(result["results"][0]["code"], "image_directory_unavailable")
        self.assertEqual(await self.memberships(other), ["default"])

    async def test_partial_filesystem_failure_preserves_failed_record_and_continues(self):
        one, two = await self.image("denied.png"), await self.image("allowed.png")
        original = Path.unlink
        def unlink(path, *args, **kwargs):
            if path.name == "denied.png":
                raise PermissionError("locked")
            return original(path, *args, **kwargs)
        with mock.patch.object(Path, "unlink", autospec=True, side_effect=unlink):
            result = await self.service.delete([one, two], "file")
        self.assertEqual((result["deleted_count"], result["failed_count"]), (1, 1))
        self.assertEqual(await self.memberships(one), ["default"])
        self.assertTrue((self.directory / "denied.png").exists())
        self.assertFalse((self.directory / "allowed.png").exists())

    async def test_commit_failure_reports_unlinked_original_and_stops_later_groups(self):
        one, two = await self.image("first.png"), await self.image("second.png")
        await self.store._call(setattr, self.store.connection, "fail_commit", True)
        result = await self.service.delete([one, two], "file")
        first, second = result["results"]
        self.assertEqual(first["code"], "database_error")
        self.assertTrue(first["file_deleted"])
        self.assertFalse(first["record_deleted"])
        self.assertFalse(second["file_deleted"])
        self.assertTrue((self.directory / "second.png").exists())
        self.assertEqual(await self.memberships(one), ["default"])
        retry = await self.service.delete([one], "file")
        self.assertTrue(retry["results"][0]["record_deleted"])
        self.assertTrue(retry["results"][0]["file_missing"])

    async def test_record_commit_failure_rolls_back_entire_batch(self):
        one, two = await self.image("one.png"), await self.image("two.png")
        await self.store._call(setattr, self.store.connection, "fail_commit", True)
        result = await self.service.delete([one, two])
        self.assertEqual(result["failed_count"], 2)
        self.assertEqual((await self.store.list_images())["total"], 2)
        self.assertEqual(await self.membership_count(), 2)

    async def test_remote_verification_failure_and_unsafe_record_never_delete(self):
        image_id = await self.image()
        with mock.patch("anima_webui.artwork_deletion.image_location.verify_local_comfy", side_effect=ImageLocationError("remote")):
            result = await self.service.delete([image_id], "file")
        self.assertEqual(result["failed_count"], 1)
        unsafe = (await add_image(self.store, "../outside.png"))["id"]
        result = await self.service.delete([unsafe], "file")
        self.assertEqual(result["results"][0]["code"], "invalid_image_location")
        self.assertTrue((self.directory / "image.png").is_file())

    async def test_repeated_cancellation_finishes_database_and_thumbnail_cleanup(self):
        image_id = await self.image()
        entered, release = threading.Event(), threading.Event()
        original = self.service._delete
        def paused(*args):
            entered.set()
            if not release.wait(5):
                raise TimeoutError("release")
            return original(*args)
        with mock.patch.object(self.service, "_delete", side_effect=paused):
            task = asyncio.create_task(self.service.delete([image_id]))
            try:
                self.assertTrue(await asyncio.to_thread(entered.wait, 2))
                task.cancel()
                await asyncio.sleep(0)
                task.cancel()
            finally:
                release.set()
            with self.assertRaises(asyncio.CancelledError):
                await task
        self.assertEqual((await self.store.list_images())["total"], 0)
        self.thumbnails.discard.assert_awaited_once_with(image_id)


class DeletionPathTests(unittest.TestCase):
    def test_path_escape_missing_root_and_non_file_are_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "directory.png").mkdir()
            for directory, record in ((root, {"filename": "../outside.png"}), (root / "missing", {"filename": "one.png"}), (root, {"filename": "directory.png"})):
                with self.assertRaises(ImageLocationError):
                    deletion_image_path(directory, record)
            outside = root.parent / "outside.png"
            original = Path.resolve
            with mock.patch.object(Path, "resolve", autospec=True, side_effect=lambda path, **kwargs: outside if path.name == "escaped.png" else original(path, **kwargs)):
                with self.assertRaises(ImageLocationError) as caught:
                    deletion_image_path(root, {"filename": "escaped.png"})
            self.assertEqual(caught.exception.code, "invalid_image_location")
