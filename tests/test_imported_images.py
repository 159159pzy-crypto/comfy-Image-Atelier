from __future__ import annotations

import asyncio
import io
import os
import tempfile
import threading
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock, patch

from PIL import Image

from anima_webui.history import HistoryStore
from anima_webui.imported_images import ImportedImageService
from anima_webui.input_images import InputImageStore
from anima_webui.persistence import StorageReadOnlyError
from anima_webui.prompt_images import PromptImageStore
from anima_webui.workflow import WorkflowError
from test_artwork_collections import legacy_management_tables


def png():
    stream = io.BytesIO()
    with Image.new("RGB", (24, 16), "blue") as image:
        image.save(stream, format="PNG")
    return stream.getvalue()


class ImportedImageTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.history = HistoryStore(self.root / "history.sqlite3")
        self.inputs = InputImageStore(self.root / "input-images")
        self.prompts = PromptImageStore(self.root / "prompt-images")
        self.comfy = SimpleNamespace(upload_image=AsyncMock(return_value="remote/upload.png"),
                                     status=AsyncMock(side_effect=OSError("remote status unavailable")))
        self.service = ImportedImageService(self.history, self.inputs, self.prompts, self.comfy)

    async def asyncTearDown(self):
        await self.service.close()
        await self.history.close()
        self.temporary.cleanup()

    async def test_read_prepare_and_remote_upload_need_no_cleanup_registry(self):
        for kind in ("input", "prompt"):
            asset = await self.service.add(kind, png(), filename="saved.png")
            self.assertEqual((await self.service.read(kind, asset["id"]))["filename"], "saved.png")
            body = await self.service.read_bytes(kind, asset["id"])
            self.assertTrue(body.startswith(b"\x89PNG"))
            if kind == "input":
                for fit in ("crop", "pad", "stretch"):
                    resized = await self.service.prepare(asset["id"], 16, 20, fit)
                    with Image.open(io.BytesIO(resized)) as image:
                        self.assertEqual(image.size, (16, 20))
        self.assertEqual(await self.service.upload_comfy(png(), "remote.png"), "remote/upload.png")
        self.comfy.upload_image.assert_awaited_once()
        self.comfy.status.assert_not_awaited()
        names = await self.history._call(lambda: {row[0] for row in self.history.connection.execute("SELECT name FROM sqlite_master")})
        self.assertNotIn("managed_image_files", names)

    async def test_old_enabled_cleanup_settings_and_aged_assets_remain_untouched(self):
        await self.history._call(legacy_management_tables, self.history.connection)
        asset = self.inputs.add(png())
        files = [self.inputs.path / f"{asset['id']}{suffix}" for suffix in (".png", ".json")]
        for path in files:
            os.utime(path, (1, 1))
        before = {path: path.read_bytes() for path in files}
        names = ("managed_image_files", "image_draft_leases", "image_cleanup_settings")
        snapshot = await self.history._call(lambda: {name: [tuple(row) for row in self.history.connection.execute(f"SELECT * FROM {name}")]
                                                    for name in names})
        await self.service.read("input", asset["id"])
        await self.service.prepare(asset["id"], 16, 16, "crop")
        await self.service.add("prompt", png())
        after = await self.history._call(lambda: {name: [tuple(row) for row in self.history.connection.execute(f"SELECT * FROM {name}")]
                                                 for name in names})
        self.assertEqual(snapshot, after)
        self.assertEqual(before, {path: path.read_bytes() for path in files})
        self.assertEqual([path.stat().st_mtime for path in files], [1, 1])
        self.assertEqual(self.service._workers, set())

    async def test_read_only_history_rejects_upload_but_not_reads(self):
        asset = await self.service.add("input", png())
        self.history._protect_storage("test")
        with self.assertRaises(StorageReadOnlyError):
            await self.service.add("input", png())
        self.assertEqual((await self.service.read("input", asset["id"]))["id"], asset["id"])
        self.assertTrue(await self.service.read_bytes("input", asset["id"]))

    async def test_symbolic_and_escaping_image_or_metadata_paths_are_rejected(self):
        asset = await self.service.add("input", png())
        original_is_symlink = Path.is_symlink
        for suffix in (".png", ".json"):
            target = self.inputs.path / f"{asset['id']}{suffix}"
            with patch.object(Path, "is_symlink", autospec=True,
                              side_effect=lambda path: path == target or original_is_symlink(path)):
                with self.assertRaises(WorkflowError):
                    await self.service.read("input", asset["id"])
                with self.assertRaises(WorkflowError):
                    await self.service.read_bytes("input", asset["id"])
                with self.assertRaises(WorkflowError):
                    await self.service.prepare(asset["id"], 16, 16, "crop")
        original_resolve = Path.resolve
        image_path = self.inputs.path / f"{asset['id']}.png"
        with patch.object(Path, "resolve", autospec=True, side_effect=lambda path, **kwargs:
                          self.root / "outside.png" if path == image_path else original_resolve(path, **kwargs)):
            with self.assertRaises(WorkflowError):
                await self.service.read_bytes("input", asset["id"])

    async def test_cancelled_upload_finishes_pair_before_same_id_upload_or_read(self):
        entered, release = threading.Event(), threading.Event()
        original_write = self.inputs._write
        writes = 0

        def paused_write(path, body):
            nonlocal writes
            original_write(path, body)
            writes += 1
            if writes == 1:
                entered.set()
                if not release.wait(5):
                    raise TimeoutError("test did not release file worker")

        with patch.object(self.inputs, "_write", side_effect=paused_write):
            first = asyncio.create_task(self.service.add("input", png(), filename="first.png"))
            second = None
            try:
                self.assertTrue(await asyncio.to_thread(entered.wait, 2))
                first.cancel()
                await asyncio.sleep(0)
                first.cancel()
                second = asyncio.create_task(self.service.add("input", png(), filename="second.png"))
                await asyncio.sleep(0.02)
                self.assertFalse(first.done())
                self.assertFalse(second.done())
                self.assertEqual(writes, 1)
            finally:
                release.set()
                with self.assertRaises(asyncio.CancelledError):
                    await first
            saved = await second
        self.assertEqual((await self.service.read("input", saved["id"]))["filename"], "second.png")
        self.assertEqual(writes, 4)
        self.assertTrue(await self.service.read_bytes("input", saved["id"]))

    async def test_shutdown_waits_for_cancelled_file_worker_and_blocks_new_access(self):
        entered, release = threading.Event(), threading.Event()
        original_add = self.prompts.add

        def paused_add(*args, **kwargs):
            entered.set()
            if not release.wait(5):
                raise TimeoutError("test did not release upload")
            return original_add(*args, **kwargs)

        with patch.object(self.prompts, "add", side_effect=paused_add):
            task = asyncio.create_task(self.service.add("prompt", png()))
            closing = None
            try:
                self.assertTrue(await asyncio.to_thread(entered.wait, 2))
                task.cancel()
                closing = asyncio.create_task(self.service.close())
                await asyncio.sleep(0.02)
                self.assertFalse(closing.done())
                self.assertFalse(task.done())
            finally:
                release.set()
                with self.assertRaises(asyncio.CancelledError):
                    await task
                if closing:
                    await closing
        self.assertEqual(len(list(self.prompts.path.glob("*.png"))), 1)
        self.assertEqual(len(list(self.prompts.path.glob("*.json"))), 1)
        self.assertEqual(self.service._workers, set())
        with self.assertRaises(WorkflowError):
            await self.service.add("prompt", png())

    async def test_cancelled_prompt_upload_finishes_cache_registration_before_unlocking(self):
        entered, release = asyncio.Event(), asyncio.Event()
        original_register = self.service.prompt_cache.register_local
        asset_id = None

        async def paused_register(value, *, uploaded=False):
            nonlocal asset_id
            asset_id = value
            entered.set()
            await release.wait()
            await original_register(value, uploaded=uploaded)

        with patch.object(self.service.prompt_cache, "register_local", side_effect=paused_register):
            upload = asyncio.create_task(self.service.add("prompt", png()))
            await asyncio.wait_for(entered.wait(), 2)
            upload.cancel()
            read = asyncio.create_task(self.service.read("prompt", asset_id))
            try:
                await asyncio.sleep(0.02)
                self.assertFalse(upload.done())
                self.assertFalse(read.done())
            finally:
                release.set()
            with self.assertRaises(asyncio.CancelledError):
                await upload
            self.assertEqual((await read)["id"], asset_id)
        self.assertEqual(self.service._workers, set())

    async def test_cache_registration_failure_preserves_successful_prompt_upload(self):
        cache = self.service.prompt_cache
        with patch.object(cache, "register_local", side_effect=OSError("cache index unavailable")), \
                patch.object(cache, "suspend", wraps=cache.suspend) as suspend:
            asset = await self.service.add("prompt", png())
            suspend.assert_called_once_with("cache index unavailable")
        self.assertEqual((await self.service.read("prompt", asset["id"]))["id"], asset["id"])
        self.assertTrue(await self.service.read_bytes("prompt", asset["id"]))
