from __future__ import annotations

import asyncio
import hashlib
import io
import os
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import AsyncMock, patch

from PIL import Image

from anima_webui.history import HistoryStore
from anima_webui.imported_images import ImportedImageService
from anima_webui.input_images import InputImageStore
from anima_webui.prompt_image_cache import PromptImageCache, LEASE_SECONDS
from anima_webui.prompt_images import PromptImageStore
from anima_webui.prompt_jobs import PromptJobStore
from anima_webui.workflow import WorkflowError


def png(color="blue"):
    stream = io.BytesIO()
    with Image.new("RGB", (24, 16), color) as image:
        image.save(stream, format="PNG")
    return stream.getvalue()


class PromptCacheTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.history = HistoryStore(self.root / "history.sqlite3")
        self.inputs = InputImageStore(self.root / "input-images")
        self.prompts = PromptImageStore(self.root / "prompt-images")
        self.remote = self.root / "comfy-input"
        (self.remote / "AnimaStudio").mkdir(parents=True)
        self.comfy = SimpleNamespace(base_url="http://127.0.0.1:8188",
            status=AsyncMock(side_effect=OSError("not running")), upload_image=AsyncMock(),
            _json=AsyncMock(return_value={"queue_running": [], "queue_pending": []}))
        self.service = ImportedImageService(self.history, self.inputs, self.prompts, self.comfy)
        self.cache = self.service.prompt_cache
        self.now = 10000.0
        self.cache.clock = lambda: self.now

    async def asyncTearDown(self):
        await self.cache.close()
        await self.service.close()
        await self.history.close()
        self.temporary.cleanup()

    async def add(self, color="blue"):
        return await self.service.add("prompt", png(color), filename=color + ".png")

    def exists(self, asset):
        return (self.prompts.path / (asset["id"] + ".png")).exists()

    async def rows(self):
        return await self.history._call(self.cache._rows)

    async def upload(self, asset):
        async def save(body, name):
            (self.remote / "AnimaStudio" / name).write_bytes(body)
            return "AnimaStudio/" + name
        self.comfy.upload_image.side_effect = save
        with patch.object(self.cache, "_local_comfy_root", AsyncMock(return_value=self.remote)):
            return await self.service.upload_comfy(
                await self.service.read_bytes("prompt", asset["id"]), "reverse-" + asset["id"] + ".png")

    async def test_constructor_is_lazy_and_owner_persists_without_old_settings(self):
        names = await self.history._call(lambda: {row[0] for row in self.history.connection.execute("SELECT name FROM sqlite_master")})
        self.assertNotIn("prompt_image_cache_files", names)
        await self.add()
        owner = self.cache.owner
        replacement = PromptImageCache(self.service, clock=self.cache.clock)
        async with self.service._lock:
            await replacement.initialize()
        self.assertEqual(replacement.owner, owner)
        names = await self.history._call(lambda: {row[0] for row in self.history.connection.execute("SELECT name FROM sqlite_master")})
        self.assertTrue({"managed_image_files", "image_cleanup_settings", "image_draft_leases"}.isdisjoint(names))

    async def test_capacity_counts_pairs_and_lru_uses_successful_reads(self):
        first = await self.add("blue")
        self.now += 1
        second = await self.add("red")
        self.now += 1
        await self.service.read("prompt", first["id"])
        rows = await self.rows()
        self.cache.max_bytes = next(row["size"] for row in rows if row["asset_id"] == first["id"])
        self.now += 121
        result = await self.cache.run()
        self.assertTrue(self.exists(first))
        self.assertFalse(self.exists(second))
        self.assertFalse((self.prompts.path / (second["id"] + ".json")).exists())
        self.assertEqual(result["deleted_files"], 2)
        self.assertLessEqual(result["total_bytes"], self.cache.max_bytes)
        self.assertEqual(result["freed_bytes"], result["before_bytes"] - result["total_bytes"])

    async def test_upload_protection_and_newest_single_large_asset(self):
        asset = await self.add()
        self.cache.max_bytes = 1
        result = await self.cache.run()
        self.assertEqual(result["deleted_files"], 0)
        self.assertTrue(self.exists(asset))
        self.now += 121
        await self.cache.run()
        self.assertFalse(self.exists(asset))

    async def test_leases_are_ordered_and_include_both_profiles_without_reviving_old_requests(self):
        first, second = await self.add("blue"), await self.add("red")
        result = await self.cache.lease("client_123", {"revision": 1, "image_ids": [first["id"], second["id"], "0" * 64]})
        self.assertEqual(result["missing_image_ids"], ["0" * 64])
        self.assertEqual(set(result["image_ids"]), {first["id"], second["id"]})
        self.cache.max_bytes = 0
        self.now += 121
        await self.cache.run()
        self.assertTrue(self.exists(first) and self.exists(second))
        released = await self.cache.lease("client_123", {"revision": 2, "image_ids": []})
        stale = await self.cache.lease("client_123", {"revision": 1, "image_ids": [first["id"]]})
        self.assertEqual(stale, released)
        await self.cache.run()
        self.assertFalse(self.exists(first) or self.exists(second))

    async def test_independent_client_and_expiry_keep_revision_tombstone(self):
        asset = await self.add()
        for client in ("client_one", "client_two"):
            await self.cache.lease(client, {"revision": 1, "image_ids": [asset["id"]]})
        await self.cache.lease("client_one", {"revision": 2, "image_ids": []})
        self.cache.max_bytes = 0
        self.now += 121
        await self.cache.run()
        self.assertTrue(self.exists(asset))
        self.now += LEASE_SECONDS
        old = await self.cache.lease("client_two", {"revision": 1, "image_ids": [asset["id"]]})
        self.assertLess(old["expires_at"], self.now)
        await self.cache.run()
        self.assertFalse(self.exists(asset))

    async def test_invalid_leases_are_rejected_without_granting_protection(self):
        for client, payload in [("x", {"revision": 1, "image_ids": []}),
                                ("client_123", {"revision": True, "image_ids": []}),
                                ("client_123", {"revision": 1, "image_ids": ["../image"]}),
                                ("client_123", {"revision": -1, "image_ids": []}),
                                ("client_123", {"revision": 1, "image_ids": [], "other": 1})]:
            with self.subTest(payload=payload), self.assertRaises(WorkflowError):
                await self.cache.lease(client, payload)

    async def test_runtime_queue_and_durable_running_jobs_protect_sources(self):
        first, second = await self.add("blue"), await self.add("red")
        self.cache.manager = SimpleNamespace(_start_lock=asyncio.Lock(), active=lambda: False, state=None,
            queue=[{"settings": {"kind": "reverse", "image_id": first["id"]}}])
        jobs = PromptJobStore(self.history)
        job = await jobs.create({"kind": "reverse", "image_id": second["id"]}, "test")
        self.now += 121
        self.cache.max_bytes = 0
        await self.cache.run()
        self.assertTrue(self.exists(first) and self.exists(second))
        self.cache.manager.queue.clear()
        await jobs.update(job["id"], status="completed", result={"text": "preserved result"})
        await self.cache.run()
        self.assertFalse(self.exists(first) or self.exists(second))
        self.assertEqual((await jobs.get(job["id"]))["result"]["text"], "preserved result")

    async def test_input_unknown_files_and_readonly_history_are_untouched(self):
        asset = await self.add()
        original = await self.service.add("input", png())
        unknown = self.prompts.path / "personal.png"
        unknown.write_bytes(b"user file")
        self.cache.max_bytes = 0
        self.now += 121
        await self.cache.run()
        self.assertFalse(self.exists(asset))
        self.assertTrue((self.inputs.path / (original["id"] + ".png")).is_file())
        self.assertEqual(unknown.read_bytes(), b"user file")
        asset = await self.add()
        self.now += 121
        self.history._protect_storage("read only test")
        await self.cache.run()
        self.assertTrue(self.exists(asset))
        self.assertEqual((await self.service.read("prompt", asset["id"]))["id"], asset["id"])

    async def test_replaced_files_are_never_adopted_by_reads_or_restart(self):
        asset = await self.add()
        path = self.prompts.path / (asset["id"] + ".png")
        body = path.read_bytes()
        changed = body[:-1] + bytes([body[-1] ^ 1])
        path.write_bytes(changed)
        await self.service.read("prompt", asset["id"])
        replacement = PromptImageCache(self.service, clock=self.cache.clock, max_bytes=0)
        async with self.service._lock:
            await replacement.initialize()
        self.now += 121
        result = await replacement.run()
        self.assertTrue(result["errors"])
        self.assertEqual(path.read_bytes(), changed)
        self.assertEqual((await self.rows())[0]["sha256"], hashlib.sha256(body).hexdigest())

    async def test_link_or_reparse_point_is_not_deleted(self):
        asset = await self.add()
        path = self.prompts.path / (asset["id"] + ".png")
        original = Path.is_symlink
        self.now += 121
        self.cache.max_bytes = 0
        with patch.object(Path, "is_symlink", autospec=True,
                          side_effect=lambda value: value == path or original(value)):
            result = await self.cache.run()
        self.assertTrue(self.exists(asset))
        self.assertTrue(result["errors"])

    async def test_upload_registers_private_owner_and_combined_capacity(self):
        asset = await self.add()
        relative = await self.upload(asset)
        self.assertEqual(relative, f"AnimaStudio/reverse-{self.cache.owner}-{asset['id']}.png")
        rows = await self.rows()
        self.assertEqual({row["kind"] for row in rows}, {"local", "comfy"})
        self.cache.max_bytes = sum(row["size"] for row in rows) - 1
        self.now += 121
        with patch.object(self.cache, "_local_comfy_root", AsyncMock(return_value=self.remote)):
            result = await self.cache.run()
        self.assertEqual(result["deleted_files"], 3)
        self.assertFalse((self.remote / relative).exists())
        self.assertEqual(result["total_bytes"], 0)

    async def test_busy_remote_pending_deletion_retries_below_capacity(self):
        asset = await self.add()
        relative = await self.upload(asset)
        self.cache.max_bytes = 0
        self.now += 121
        self.comfy._json.return_value = {"queue_running": [[1, "external-job"]], "queue_pending": []}
        with patch.object(self.cache, "_local_comfy_root", AsyncMock(return_value=self.remote)):
            first = await self.cache.run()
            self.assertFalse(self.exists(asset))
            self.assertTrue((self.remote / relative).is_file())
            self.assertEqual(first["pending_files"], 1)
            self.cache.max_bytes = 100 * 1024 * 1024
            self.comfy._json.return_value = {"queue_running": [], "queue_pending": []}
            second = await self.cache.run()
        self.assertEqual(second["deleted_files"], 1)
        self.assertFalse((self.remote / relative).exists())

    async def test_unknown_queue_changed_root_and_replaced_remote_are_preserved(self):
        asset = await self.add()
        relative = await self.upload(asset)
        self.now += 121
        self.cache.max_bytes = 0
        self.comfy._json.return_value = {}
        with patch.object(self.cache, "_local_comfy_root", AsyncMock(return_value=self.remote)):
            await self.cache.run()
        self.assertTrue((self.remote / relative).is_file())
        self.comfy._json.return_value = {"queue_running": [], "queue_pending": []}
        other = self.root / "other-input"
        other.mkdir()
        with patch.object(self.cache, "_local_comfy_root", AsyncMock(return_value=other)):
            result = await self.cache.run()
        self.assertTrue(result["errors"])
        target = self.remote / relative
        target.write_bytes(b"replaced external file")
        with patch.object(self.cache, "_local_comfy_root", AsyncMock(return_value=self.remote)):
            result = await self.cache.run()
        self.assertTrue(result["errors"])
        self.assertEqual(target.read_bytes(), b"replaced external file")

    async def test_successful_upload_survives_unverifiable_locality(self):
        asset = await self.add()
        self.comfy.upload_image.return_value = "AnimaStudio/unexpected.png"
        relative = await self.service.upload_comfy(png(), "reverse-" + asset["id"] + ".png")
        self.assertEqual(relative, "AnimaStudio/unexpected.png")
        self.assertEqual({row["kind"] for row in await self.rows()}, {"local"})

    async def test_legacy_claim_needs_job_and_matching_pixels_unknown_copy_stays(self):
        asset = self.prompts.add(png())
        os.utime(self.prompts.path / (asset["id"] + ".png"), (1, 1))
        os.utime(self.prompts.path / (asset["id"] + ".json"), (1, 1))
        known = self.remote / "AnimaStudio" / ("reverse-" + asset["id"] + ".png")
        known.write_bytes((self.prompts.path / (asset["id"] + ".png")).read_bytes())
        unknown = self.remote / "AnimaStudio" / ("reverse-" + "0" * 64 + ".png")
        unknown.write_bytes(png("red"))
        job = await PromptJobStore(self.history).create({"kind": "reverse", "method": "vision", "image_id": asset["id"]}, "legacy")
        await PromptJobStore(self.history).update(job["id"], status="completed")
        with patch.object(self.cache, "_local_comfy_root", AsyncMock(return_value=self.remote)):
            async with self.service._lock:
                await self.cache.initialize()
            self.assertEqual(len(await self.rows()), 2)
            self.cache.max_bytes = 0
            await self.cache.run()
        self.assertFalse(known.exists())
        self.assertTrue(unknown.exists())

    async def test_scheduler_honors_startup_grace_and_close_drains(self):
        self.cache.startup_delay = .08
        self.cache.interval = .5
        with patch.object(self.cache, "run", AsyncMock()) as run:
            self.cache.start()
            self.cache.wake()
            await asyncio.sleep(.02)
            run.assert_not_awaited()
            await asyncio.sleep(.09)
            self.assertEqual(run.await_count, 1)
            self.cache.wake()
            await asyncio.sleep(.02)
            self.assertEqual(run.await_count, 2)
            await self.cache.close()
            self.assertIsNone(self.cache._task)

    async def test_partial_unlink_accounts_for_freed_bytes_without_evicting_next_asset(self):
        first = await self.add("blue")
        self.now += 1
        second = await self.add("red")
        self.now += 121
        image = self.prompts.path / (first["id"] + ".png")
        metadata = self.prompts.path / (first["id"] + ".json")
        image_bytes, metadata_bytes = image.stat().st_size, metadata.stat().st_size
        self.cache.max_bytes = sum(row["size"] for row in await self.rows()) - image_bytes
        original = Path.unlink

        def unlink(path, *args, **kwargs):
            if path == metadata:
                raise PermissionError("metadata is open elsewhere")
            return original(path, *args, **kwargs)

        with patch.object(Path, "unlink", autospec=True, side_effect=unlink):
            result = await self.cache.run()
        self.assertEqual(result["deleted_files"], 1)
        self.assertEqual(result["freed_bytes"], image_bytes)
        self.assertEqual(result["total_bytes"], self.cache.max_bytes)
        self.assertTrue(self.exists(second))
        row = next(row for row in await self.rows() if row["asset_id"] == first["id"])
        self.assertEqual(row["size"], metadata_bytes)
        self.assertEqual(row["pending_delete"], 1)
        result = await self.cache.run()
        self.assertEqual(result["deleted_files"], 1)
        self.assertEqual(result["freed_bytes"], metadata_bytes)
        self.assertTrue(self.exists(second))

    async def test_external_missing_files_are_reconciled_before_capacity_decision(self):
        first, second = await self.add("blue"), await self.add("red")
        for suffix in (".png", ".json"):
            (self.prompts.path / (first["id"] + suffix)).unlink()
        self.cache.max_bytes = next(row["size"] for row in await self.rows() if row["asset_id"] == second["id"])
        self.now += 121
        result = await self.cache.run()
        self.assertEqual(result["deleted_files"], 0)
        self.assertEqual(result["before_bytes"], self.cache.max_bytes)
        self.assertTrue(self.exists(second))
        self.assertEqual(len(await self.rows()), 1)

    async def test_redirected_canonical_root_does_not_authorize_new_target(self):
        asset = await self.add()
        outside = self.root / "unowned"
        outside.mkdir()
        for suffix in (".png", ".json"):
            (outside / (asset["id"] + suffix)).write_bytes((self.prompts.path / (asset["id"] + suffix)).read_bytes())
        original = Path.resolve
        self.cache.max_bytes = 0
        self.now += 121
        with patch.object(Path, "resolve", autospec=True, side_effect=lambda path, **kwargs:
                          outside if path == self.prompts.path else original(path, **kwargs)):
            result = await self.cache.run()
        self.assertTrue(result["errors"])
        self.assertTrue((outside / (asset["id"] + ".png")).exists())
        self.assertTrue(self.exists(asset))

    async def test_heartbeat_does_not_hash_pixels_or_change_lru(self):
        asset = await self.add()
        before = await self.rows()
        self.now += 200
        with patch.object(self.cache, "_digest", side_effect=AssertionError("heartbeat must not hash pixels")):
            result = await self.cache.lease("client_123", {"revision": 1, "image_ids": [asset["id"]]})
        self.assertEqual(result["image_ids"], [asset["id"]])
        self.assertEqual(before, await self.rows())

    async def test_stopping_sweeper_allows_inflight_upload_registration_to_finish(self):
        asset = await self.add()
        started, release = asyncio.Event(), asyncio.Event()

        async def delayed_upload(body, name):
            started.set()
            await release.wait()
            (self.remote / "AnimaStudio" / name).write_bytes(body)
            return "AnimaStudio/" + name

        self.comfy.upload_image.side_effect = delayed_upload
        self.cache.start()
        with patch.object(self.cache, "_local_comfy_root", AsyncMock(return_value=self.remote)):
            task = asyncio.create_task(self.service.upload_comfy(png(), "reverse-" + asset["id"] + ".png"))
            await asyncio.wait_for(started.wait(), 2)
            await self.cache.stop()
            self.assertFalse(self.cache._closed)
            release.set()
            relative = await task
        self.assertTrue((self.remote / relative).is_file())
        self.assertEqual({row["kind"] for row in await self.rows()}, {"local", "comfy"})

    async def test_restart_does_not_adopt_redirected_root(self):
        asset = await self.add()
        outside = self.root / "outside"
        outside.mkdir()
        for suffix in (".png", ".json"):
            (outside / (asset["id"] + suffix)).write_bytes((self.prompts.path / (asset["id"] + suffix)).read_bytes())
        replacement = PromptImageCache(self.service, max_bytes=0, clock=self.cache.clock)
        original = Path.resolve
        with patch.object(Path, "resolve", autospec=True, side_effect=lambda path, **kwargs:
                          outside if path == self.prompts.path else original(path, **kwargs)):
            async with self.service._lock:
                with self.assertRaises(WorkflowError):
                    await replacement.initialize()
        self.assertTrue(replacement._suspended)
        self.assertEqual(len(await self.rows()), 1)
        self.assertTrue((outside / (asset["id"] + ".png")).is_file())

    async def test_old_registry_is_read_only_and_claims_require_current_root_and_digest(self):
        from test_artwork_collections import legacy_management_tables

        asset = self.prompts.add(png())
        body = (self.prompts.path / (asset["id"] + ".png")).read_bytes()
        relative = f"AnimaStudio/reverse-{asset['id']}.png"
        (self.remote / relative).write_bytes(body)

        def seed_legacy():
            legacy_management_tables(self.history.connection)
            self.history.connection.execute("INSERT INTO managed_image_files VALUES(?,?,?,?,?,?,?,?,?)",
                ("comfy_input", relative, str(self.remote), relative, hashlib.sha256(body).hexdigest(), len(body), 1, 1, "upload"))
            self.history.connection.commit()

        await self.history._call(seed_legacy)

        def snapshot():
            return {name: [tuple(row) for row in self.history.connection.execute("SELECT * FROM " + name)]
                    for name in ("managed_image_files", "image_draft_leases", "image_cleanup_settings")}

        before = await self.history._call(snapshot)
        with patch.object(self.cache, "_local_comfy_root", AsyncMock(return_value=self.remote)):
            async with self.service._lock:
                await self.cache.initialize()
            self.assertEqual({row["kind"] for row in await self.rows()}, {"local", "comfy"})
            self.cache.max_bytes = 0
            await self.cache.run()
        self.assertEqual(before, await self.history._call(snapshot))
        self.assertFalse((self.remote / relative).exists())

    async def test_local_comfy_proof_is_required_before_claim_or_deletion(self):
        asset = await self.add()
        relative = await self.upload(asset)
        self.cache.max_bytes = 0
        self.now += 121
        self.comfy.status.side_effect = None
        stats = {"system": {"os": "win32", "argv": ["F:\\Comfy\\main.py"]}}
        self.comfy.status.return_value = stats
        with patch("anima_webui.prompt_image_cache.image_location.IS_WINDOWS", True), patch(
            "anima_webui.prompt_image_cache.image_location.verify_local_comfy", side_effect=WorkflowError("SSH listener")) as verify:
            result = await self.cache.run()
        verify.assert_called_with(self.comfy.base_url, stats)
        self.assertEqual(result["pending_files"], 1)
        self.assertTrue((self.remote / relative).exists())

    async def test_runtime_root_change_read_cannot_claim_new_root_or_break_successful_read(self):
        asset = await self.add()
        original_root = self.prompts.path
        outside = self.root / "redirected"
        outside.mkdir()
        for suffix in (".png", ".json"):
            (outside / (asset["id"] + suffix)).write_bytes((original_root / (asset["id"] + suffix)).read_bytes())
        before = await self.rows()
        original_resolve = Path.resolve
        with patch.object(Path, "resolve", autospec=True, side_effect=lambda path, **kwargs:
                          outside / path.relative_to(original_root) if path.is_relative_to(original_root)
                          else original_resolve(path, **kwargs)):
            read = await self.service.read("prompt", asset["id"])
        self.assertEqual(read["id"], asset["id"])
        self.assertTrue(self.cache._suspended)
        self.assertEqual(before, await self.rows())
        self.cache.max_bytes = 0
        self.now += 121
        await self.cache.run()
        self.assertTrue((outside / (asset["id"] + ".png")).is_file())
        self.assertTrue((original_root / (asset["id"] + ".png")).is_file())

    async def test_runtime_root_change_upload_is_successful_but_never_adopts_new_files(self):
        first = await self.add()
        original_root = self.prompts.path
        outside = self.root / "redirected"
        outside.mkdir()
        before = await self.rows()
        self.prompts.path = outside
        uploaded = await self.add("red")
        self.assertTrue((outside / (uploaded["id"] + ".png")).is_file())
        self.assertTrue(self.cache._suspended)
        self.assertEqual(before, await self.rows())
        self.cache.max_bytes = 0
        self.now += 121
        await self.cache.run()
        self.assertTrue((original_root / (first["id"] + ".png")).is_file())
        self.assertTrue((outside / (uploaded["id"] + ".png")).is_file())

    async def test_runtime_root_change_prevents_comfy_registration_without_failing_upload(self):
        asset = await self.add()
        outside = self.root / "redirected"
        outside.mkdir()
        self.prompts.path = outside

        async def save(body, name):
            (self.remote / "AnimaStudio" / name).write_bytes(body)
            return "AnimaStudio/" + name

        self.comfy.upload_image.side_effect = save
        with patch.object(self.cache, "_local_comfy_root", AsyncMock(return_value=self.remote)) as proof:
            relative = await self.service.upload_comfy(png(), "reverse-" + asset["id"] + ".png")
        self.assertTrue((self.remote / relative).is_file())
        self.assertTrue(self.cache._suspended)
        self.assertEqual({row["kind"] for row in await self.rows()}, {"local"})
        proof.assert_not_awaited()


if __name__ == "__main__":
    unittest.main()
