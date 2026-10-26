from __future__ import annotations

import asyncio
import hashlib
import io
import json
import os
import sqlite3
import tempfile
import threading
import time
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock, patch

from PIL import Image

from anima_webui.asset_lifecycle import AssetLifecycle, DAY
from anima_webui.history import HistoryStore
from anima_webui.input_images import InputImageStore
from anima_webui.persistence import StorageReadOnlyError
from anima_webui.prompt_images import PromptImageStore
from anima_webui.workflow import WorkflowError


def png(color="red"):
    output = io.BytesIO()
    with Image.new("RGB", (16, 16), color) as image:
        image.save(output, "PNG")
    return output.getvalue()


class CleanupTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.history = HistoryStore(self.root / "history.sqlite3")
        self.inputs = InputImageStore(self.root / "input-images")
        self.prompts = PromptImageStore(self.root / "prompt-images")
        self.manager = SimpleNamespace(_start_lock=asyncio.Lock(), state=None, queue=[], active=lambda: False)
        self.comfy = SimpleNamespace(
            status=AsyncMock(side_effect=OSError("offline")),
            _json=AsyncMock(return_value={"queue_running": [], "queue_pending": []}),
        )
        self.service = AssetLifecycle(self.history, self.inputs, self.prompts, self.comfy, self.manager)

    async def asyncTearDown(self):
        await self.service.close()
        await self.history.close()
        self.temporary.cleanup()

    async def age(self, days=8):
        def update():
            with self.history.connection:
                self.history.connection.execute("UPDATE managed_image_files SET last_used_at=?", (time.time() - days * DAY,))
        await self.history._call(update)

    async def image(self, asset_id, *, runtime_path=""):
        settings = {"model_profile": "anima", "workbench": "single", "generation_mode": "img2img",
                    "single": {"input_image_id": asset_id}}
        batch = await self.history.create_batch("test-batch", 1, settings)
        return await self.history.add_image(
            batch_id=batch["id"], sequence=1, prompt_id="prompt", image={"filename": "output.png"},
            positive_prompt="test", negative_prompt="", sample_seed=1, prompt_seed=1, settings=settings,
            single_runtime={"input_image_id": asset_id, "input_image": runtime_path},
        )

    async def test_first_migration_grace_and_unknown_files(self):
        asset = self.inputs.add(png())
        for suffix in (".png", ".json"):
            os.utime(self.inputs.path / (asset["id"] + suffix), (1, 1))
        unknown = self.inputs.path / "unknown.png"
        unknown.write_bytes(png())
        await self.service.initialize()
        self.assertTrue((self.root / "history.sqlite3.pre-image-cleanup.bak").is_file())
        result = await self.service.run()
        self.assertEqual(result["deleted_files"], 0)
        self.assertTrue(unknown.is_file())
        await self.age()
        result = await self.service.run()
        self.assertEqual(result["deleted_files"], 2)
        self.assertGreater(result["freed_bytes"], 0)
        self.assertTrue(unknown.is_file())
        self.assertEqual((await self.service.status())["counts"]["input"], 0)

    async def test_history_source_is_kept_until_last_record_is_removed(self):
        asset = await self.service.add("input", png())
        record = await self.image(asset["id"])
        await self.age()
        result = await self.service.run()
        self.assertEqual(result["deleted_files"], 0)
        await self.history.delete_image(record["id"])
        result = await self.service.run()
        self.assertEqual(result["deleted_files"], 2)

    async def test_runtime_and_queue_protect_both_kinds(self):
        source = await self.service.add("input", png())
        reverse = await self.service.add("prompt", png("blue"))
        self.manager.state = {"status": "running", "settings": {"generation_mode": "img2img", "single": {"input_image_id": source["id"]}}}
        self.manager.queue = [{"settings": {"kind": "reverse", "image_id": reverse["id"]}}]
        await self.age()
        self.assertEqual((await self.service.run())["deleted_files"], 0)
        self.manager.state = None
        self.manager.queue = []
        self.assertEqual((await self.service.run())["deleted_files"], 4)

    async def test_leases_are_per_session_and_touch_extends_idle_time(self):
        asset = await self.service.add("input", png())
        refs = [{"kind": "input", "id": asset["id"]}]
        await self.service.lease("session-one", refs)
        await self.service.lease("session-two", refs)
        await self.service.lease("session-one", [])
        await self.age()
        self.assertEqual((await self.service.run())["deleted_files"], 0)

        def expire():
            with self.history.connection:
                self.history.connection.execute("UPDATE image_draft_leases SET expires_at=0")
        await self.history._call(expire)
        await self.service.read("input", asset["id"])
        self.assertEqual((await self.service.run())["deleted_files"], 0)
        await self.age()
        self.assertEqual((await self.service.run())["deleted_files"], 2)

    async def test_missing_lease_and_strict_settings(self):
        result = await self.service.lease("session-one", [{"kind": "prompt", "id": "a" * 64}])
        self.assertEqual(result["missing"]["prompt_image_ids"], ["a" * 64])
        for payload in ({"retention_days": 0}, {"retention_days": 366}, {"retention_days": True},
                        {"enabled": 1}, {"enabled": "false"}, {"unknown": True}):
            with self.subTest(payload=payload), self.assertRaises(WorkflowError):
                await self.service.settings(payload)
        await self.service.settings({"enabled": False, "retention_days": 30})
        status = await self.service.status()
        self.assertFalse(status["enabled"])
        self.assertEqual(status["retention_days"], 30)

    async def test_read_only_protection_and_initialization_failure(self):
        asset = await self.service.add("input", png())
        await self.age()
        self.history._protect_storage("test protection")
        self.assertTrue((await self.service.status())["read_only"])
        with self.assertRaises(StorageReadOnlyError):
            await self.service.run()
        self.assertEqual((await self.service.read("input", asset["id"]))["id"], asset["id"])
        self.assertTrue((self.inputs.path / (asset["id"] + ".png")).exists())

    async def test_failed_schema_does_not_abort_startup_or_delete(self):
        with patch.object(self.service, "_schema", side_effect=OSError("cannot back up")):
            await self.service.initialize()
        status = await self.service.status()
        self.assertTrue(status["read_only"])
        self.assertIn("cannot back up", status["reason"])
        with self.assertRaises(WorkflowError):
            await self.service.run()

    async def test_legacy_registration_database_failure_disables_only_cleanup(self):
        asset = self.inputs.add(png())
        with patch.object(self.service, "_upsert", side_effect=sqlite3.OperationalError("disk full")):
            await self.service.initialize()
        self.assertTrue((await self.service.status())["read_only"])
        self.assertEqual((await self.service.read("input", asset["id"]))["id"], asset["id"])
        self.assertTrue(self.history.available)

    async def test_settings_recheck_protection_when_queued_write_reaches_worker(self):
        await self.service.initialize()
        entered, release = threading.Event(), threading.Event()

        def protect_before_next_write():
            entered.set()
            if not release.wait(5):
                raise TimeoutError("test release missing")
            self.history._protect_storage("late disk protection")

        blocker = asyncio.create_task(self.history._call(protect_before_next_write))
        self.assertTrue(await asyncio.to_thread(entered.wait, 2))
        update = asyncio.create_task(self.service.settings({"retention_days": 30}))
        await asyncio.sleep(0.03)
        release.set()
        await blocker
        with self.assertRaises(StorageReadOnlyError):
            await update
        self.assertEqual((await self.service.status())["retention_days"], 7)

    async def test_scheduler_waits_five_minutes_before_first_cleanup(self):
        first_sleep = asyncio.Event()
        waits = []

        async def wait(delay):
            waits.append(delay)
            first_sleep.set()
            await asyncio.Future()

        with patch("anima_webui.asset_lifecycle.asyncio.sleep", side_effect=wait), patch.object(self.service, "run", new_callable=AsyncMock) as run:
            self.service.start()
            await first_sleep.wait()
            self.assertEqual(waits, [300])
            run.assert_not_awaited()
            await self.service.close()

    async def test_cancelled_upload_finishes_registration_before_unlock(self):
        entered, release = threading.Event(), threading.Event()
        original = self.inputs.add

        def blocked(*args, **kwargs):
            entered.set()
            if not release.wait(5):
                raise TimeoutError("test release missing")
            return original(*args, **kwargs)

        with patch.object(self.inputs, "add", side_effect=blocked):
            upload = asyncio.create_task(self.service.add("input", png()))
            self.assertTrue(await asyncio.to_thread(entered.wait, 2))
            upload.cancel()
            cleanup = asyncio.create_task(self.service.run())
            await asyncio.sleep(0.03)
            self.assertFalse(upload.done())
            self.assertFalse(cleanup.done())
            release.set()
            with self.assertRaises(asyncio.CancelledError):
                await upload
            self.assertEqual((await cleanup)["deleted_files"], 0)
        self.assertEqual((await self.service.status())["counts"]["input"], 1)

    async def test_replaced_source_and_symlinks_are_not_deleted(self):
        asset = await self.service.add("input", png())
        path = self.inputs.path / (asset["id"] + ".png")
        path.write_bytes(png("blue"))
        await self.age()
        result = await self.service.run()
        self.assertEqual(result["deleted_files"], 0)
        self.assertTrue(result["errors"])
        self.assertTrue(path.is_file())

    async def local_copy(self, *, prefix=""):
        root = self.root / "comfy-input"
        (root / "AnimaStudio").mkdir(parents=True, exist_ok=True)
        body = png()
        digest = hashlib.sha256(body).hexdigest()
        relative = f"AnimaStudio/{prefix}{digest}.png"
        (root / relative).write_bytes(body)
        self.service._local_comfy_root = AsyncMock(return_value=root.resolve())
        await self.service.register_comfy(relative, body)
        return root, relative, body

    async def test_external_comfy_queue_survives_webui_restart(self):
        root, relative, body = await self.local_copy()
        await self.age()
        self.comfy._json.return_value = {"queue_running": [], "queue_pending": [[1, "old-job", {}]]}
        self.assertEqual((await self.service.run())["deleted_files"], 0)
        self.assertTrue((root / relative).exists())
        self.comfy._json.return_value = {"queue_running": [], "queue_pending": []}
        self.assertEqual((await self.service.run())["deleted_files"], 1)
        self.assertFalse((root / relative).exists())

    async def test_pre_submit_local_task_protects_aged_comfy_copy(self):
        root, relative, _ = await self.local_copy()
        await self.age()
        # The file may have been overwritten by upload while registration is
        # waiting for the cleanup lock; Comfy does not know the task yet.
        self.manager.active = lambda: True
        self.manager.state = {"status": "running", "settings": {}}
        self.assertEqual((await self.service.run())["deleted_files"], 0)
        self.assertTrue((root / relative).exists())

    async def test_final_database_check_covers_runtime_to_history_transfer(self):
        asset = await self.service.add("input", png())
        settings = {"model_profile": "anima", "workbench": "single", "single": {"input_image_id": asset["id"]}}
        await self.history.create_batch("finishing", 1, settings)
        await self.age()
        transfer = asyncio.Event()

        async def persist_before_unlink():
            await transfer.wait()
            await self.history.add_image(batch_id="finishing", sequence=1, prompt_id="prompt",
                image={"filename": "result.png"}, positive_prompt="", negative_prompt="",
                sample_seed=1, prompt_seed=1, settings=settings)

        writer = asyncio.create_task(persist_before_unlink())
        calls = 0

        def runtime_snapshot():
            nonlocal calls
            calls += 1
            if calls == 2:
                # Wake the write just before cleanup enqueues its final DB work.
                transfer.set()
            return set()

        with patch.object(self.service, "_runtime_references", side_effect=runtime_snapshot):
            result = await self.service.run()
        await writer
        self.assertEqual(result["deleted_files"], 0)
        self.assertTrue((self.inputs.path / (asset["id"] + ".png")).exists())

    async def test_restarting_or_touching_never_adopts_a_replaced_file(self):
        asset = await self.service.add("input", png())
        path = self.inputs.path / (asset["id"] + ".png")
        old_digest = hashlib.sha256(path.read_bytes()).hexdigest()
        body = bytearray(path.read_bytes())
        body[-1] ^= 1  # Same size: metadata validation alone cannot detect it.
        path.write_bytes(body)
        await self.age()
        restarted = AssetLifecycle(self.history, self.inputs, self.prompts, self.comfy, self.manager)
        try:
            await restarted.initialize()
            self.assertEqual((await restarted.run())["deleted_files"], 0)
            await restarted.touch("input", asset["id"])
            digest = await self.history._call(lambda: self.history.connection.execute(
                "SELECT sha256 FROM managed_image_files WHERE kind='input' AND asset_id=?", (asset["id"],)).fetchone()[0])
            self.assertEqual(digest, old_digest)
            await self.age()
            self.assertEqual((await restarted.run())["deleted_files"], 0)
            self.assertTrue(path.exists())
        finally:
            await restarted.close()

    async def test_unknown_or_replaced_comfy_files_and_changed_root_are_kept(self):
        root, relative, body = await self.local_copy()
        unknown = root / "AnimaStudio" / ("f" * 64 + ".png")
        unknown.write_bytes(body)
        (root / relative).write_bytes(png("green"))
        await self.age()
        result = await self.service.run()
        self.assertEqual(result["deleted_files"], 0)
        self.assertTrue(result["errors"])
        self.assertTrue(unknown.exists())
        other = self.root / "another-input"
        other.mkdir()
        self.service._local_comfy_root.return_value = other.resolve()
        self.assertEqual((await self.service.run())["deleted_files"], 0)
        self.assertTrue((root / relative).exists())

    async def test_comfy_reparse_point_is_not_followed_even_inside_owned_root(self):
        root, relative, _ = await self.local_copy()
        await self.age()
        target = root / relative
        original = os.lstat

        def reparse(path, *args, **kwargs):
            result = original(path, *args, **kwargs)
            if Path(path) == target:
                return SimpleNamespace(st_mode=result.st_mode, st_file_attributes=0x400)
            return result

        with patch("anima_webui.asset_lifecycle.os.lstat", side_effect=reparse):
            result = await self.service.run()
        self.assertEqual(result["deleted_files"], 0)
        self.assertTrue(result["errors"])
        self.assertTrue(target.exists())

    async def test_legacy_claims_need_runtime_or_matching_reverse_source(self):
        root = self.root / "comfy-input"
        (root / "AnimaStudio").mkdir(parents=True)
        self.service._local_comfy_root = AsyncMock(return_value=root.resolve())
        body = png()
        digest = hashlib.sha256(body).hexdigest()
        runtime_path = f"AnimaStudio/{digest}.png"
        (root / runtime_path).write_bytes(body)
        source = await self.service.add("input", png())
        await self.image(source["id"], runtime_path=runtime_path)
        reverse = await self.service.add("prompt", png("blue"))
        reverse_path = root / "AnimaStudio" / ("reverse-" + reverse["id"] + ".png")
        reverse_path.write_bytes((self.prompts.path / (reverse["id"] + ".png")).read_bytes())

        def job():
            with self.history.connection:
                self.history.connection.execute("INSERT INTO prompt_jobs(id,status,input_fingerprint,settings_json,created_at,updated_at) VALUES(?,?,?,?,?,?)",
                    ("job", "completed", "f", json.dumps({"kind": "reverse", "method": "vision", "image_id": reverse["id"]}), "old", "old"))
        await self.history._call(job)
        await self.service.run()
        self.assertEqual((await self.service.status())["counts"]["comfy_input"], 2)
        await self.age()
        result = await self.service.run()
        # WebUI source needed by history remains, derived Comfy copies do not.
        self.assertEqual(result["deleted_files"], 4)
        self.assertTrue((self.inputs.path / (source["id"] + ".png")).exists())
        self.assertFalse((root / runtime_path).exists())

    async def test_malformed_or_unavailable_comfy_queue_fails_closed(self):
        root, relative, _ = await self.local_copy()
        await self.age()
        for response in ({}, {"queue_running": [], "queue_pending": None}):
            self.comfy._json.return_value = response
            self.assertEqual((await self.service.run())["deleted_files"], 0)
        self.comfy._json.side_effect = OSError("offline")
        self.assertEqual((await self.service.run())["deleted_files"], 0)
        self.assertTrue((root / relative).exists())
