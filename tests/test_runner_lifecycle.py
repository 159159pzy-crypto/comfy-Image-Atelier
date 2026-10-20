import asyncio
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from anima_webui.history import HistoryStore
from anima_webui.runner import BatchConflict, BatchManager
from anima_webui.workflow import DEFAULT_SETTINGS
from test_runner import FakeComfy, FakeTemplates


class RunnerLifecycleTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.history = HistoryStore(Path(self.temp.name) / "history.sqlite3")
        self.comfy = FakeComfy()
        self.manager = BatchManager(FakeTemplates(), self.history, self.comfy)

    async def asyncTearDown(self):
        if self.comfy.block:
            self.comfy.block.set()
        await self.manager.shutdown()
        await self.history.close()
        self.temp.cleanup()

    async def submitted(self):
        async with asyncio.timeout(2):
            while not self.comfy.submissions:
                await asyncio.sleep(0)

    async def test_all_start_endpoints_reject_shutdown_without_side_effects(self):
        await self.manager.shutdown()
        for start, payload in (
            (self.manager.start, DEFAULT_SETTINGS),
            (self.manager.start_rewrite, {}),
            (self.manager.start_prompt_preview, {}),
        ):
            with self.subTest(start=start.__name__):
                with self.assertRaisesRegex(BatchConflict, "服务正在关闭"):
                    await start(payload)
        self.assertFalse(self.manager.active())
        self.assertEqual(self.comfy.submissions, [])
        self.assertEqual(self.manager.queue, [])

    async def test_shutdown_during_preflight_rejects_unsubmitted_batch(self):
        preflight = asyncio.Event()
        release = asyncio.Event()
        original = self.comfy.lora_filenames

        async def blocked_inventory():
            preflight.set()
            await release.wait()
            return await original()

        with patch.object(self.comfy, "lora_filenames", blocked_inventory):
            starting = asyncio.create_task(self.manager.start(DEFAULT_SETTINGS))
            await preflight.wait()
            closing = asyncio.create_task(self.manager.shutdown())
            await asyncio.sleep(0)
            release.set()
            with self.assertRaises(BatchConflict):
                await starting
            await closing
        self.assertEqual(self.comfy.submissions, [])
        self.assertIsNone(self.manager.state)

    async def test_idle_shutdown_reserves_dispatch_and_is_idempotent(self):
        await self.manager.begin_idle_shutdown()
        await self.manager.begin_idle_shutdown()
        with self.assertRaises(BatchConflict):
            await self.manager.start(DEFAULT_SETTINGS)

    async def test_idle_shutdown_rechecks_inflight_start_before_reserving(self):
        preflight = asyncio.Event()
        release = asyncio.Event()
        original = self.comfy.lora_filenames
        self.comfy.block = asyncio.Event()

        async def blocked_inventory():
            preflight.set()
            await release.wait()
            return await original()

        with patch.object(self.comfy, "lora_filenames", blocked_inventory):
            starting = asyncio.create_task(self.manager.start(DEFAULT_SETTINGS))
            await preflight.wait()
            closing = asyncio.create_task(self.manager.begin_idle_shutdown())
            await asyncio.sleep(0)
            self.assertFalse(closing.done())
            release.set()
            await starting
            with self.assertRaisesRegex(BatchConflict, "任务正在运行"):
                await closing
        self.assertFalse(self.manager.shutting_down)
        self.assertTrue(self.manager.active())

    async def test_idle_shutdown_rejects_waiting_queue_even_without_active_task(self):
        self.manager.queue.append({"kind": "generation", "queue_id": "waiting"})
        with self.assertRaises(BatchConflict):
            await self.manager.begin_idle_shutdown()
        self.assertFalse(self.manager.shutting_down)

    async def test_shutdown_stops_current_and_cancels_durable_queued_text_jobs(self):
        self.comfy.block = asyncio.Event()
        self.comfy.honor_abort = True
        state = await self.manager.start({**DEFAULT_SETTINGS, "count": 2})
        await self.submitted()
        await self.manager.start({**DEFAULT_SETTINGS, "count": 1})
        job = await self.manager.prompt_jobs.create({"kind": "rewrite"}, "queued")
        self.manager.queue.append({
            "queue_id": job["id"], "kind": "prompt_preview", "job_id": job["id"],
            "settings": job["settings"],
        })

        await self.manager.shutdown()

        self.assertTrue(self.manager.task.done())
        self.assertEqual(self.manager.queue, [])
        self.assertEqual(len(self.comfy.submissions), 1)
        self.assertEqual((await self.history.get_batch(state["id"]))["status"], "stopped")
        self.assertEqual((await self.manager.prompt_jobs.get(job["id"]))["status"], "cancelled")

    async def test_shutdown_waits_for_cancelled_worker_database_finalization(self):
        self.comfy.block = asyncio.Event()  # This fake ignores should_abort.
        state = await self.manager.start({**DEFAULT_SETTINGS, "count": 1})
        await self.submitted()
        finalizing = asyncio.Event()
        release = asyncio.Event()
        original = self.history.update_batch

        async def update(batch_id, **kwargs):
            if kwargs.get("status") == "stopped":
                finalizing.set()
                await release.wait()
            return await original(batch_id, **kwargs)

        with patch.object(self.history, "update_batch", update), \
                patch("anima_webui.runner.SHUTDOWN_GRACE", 0.01):
            closing = asyncio.create_task(self.manager.shutdown())
            await asyncio.wait_for(finalizing.wait(), 2)
            self.assertFalse(closing.done())
            release.set()
            await closing
        self.assertTrue(self.manager.task.done())
        self.assertEqual((await self.history.get_batch(state["id"]))["status"], "stopped")

    async def test_shutdown_waits_for_websocket_cleanup(self):
        connected = asyncio.Event()
        disconnecting = asyncio.Event()
        release = asyncio.Event()

        async def stream(client_id):
            connected.set()
            try:
                await asyncio.Event().wait()
                yield {}
            finally:
                disconnecting.set()
                await release.wait()

        self.comfy.block = asyncio.Event()
        self.comfy.honor_abort = True
        with patch.object(self.comfy, "progress_stream", stream, create=True):
            await self.manager.start({**DEFAULT_SETTINGS, "count": 1})
            await connected.wait()
            closing = asyncio.create_task(self.manager.shutdown())
            await asyncio.wait_for(disconnecting.wait(), 2)
            self.assertFalse(closing.done())
            release.set()
            await closing
        self.assertEqual(self.manager._monitor_tasks, set())

    async def test_shutdown_cancels_text_job_during_initial_status_update(self):
        job = await self.manager.prompt_jobs.create({"kind": "rewrite"}, "initial")
        running = asyncio.Event()
        original = self.manager.prompt_jobs.update

        async def update(job_id, **kwargs):
            result = await original(job_id, **kwargs)
            if kwargs.get("status") == "running":
                running.set()
                await asyncio.Event().wait()
            return result

        with patch.object(self.manager.prompt_jobs, "update", update), \
                patch("anima_webui.runner.SHUTDOWN_GRACE", 0.01):
            await self.manager._begin_prompt_preview(job)
            await running.wait()
            await self.manager.shutdown()
        self.assertEqual((await self.manager.prompt_jobs.get(job["id"]))["status"], "cancelled")
        self.assertFalse(self.manager.active())
        self.assertEqual(self.comfy.submissions, [])

    async def test_slow_remote_cancel_has_one_total_time_budget(self):
        cancel_started = asyncio.Event()

        async def cancel(prompt_id):
            cancel_started.set()
            await asyncio.Event().wait()

        self.manager.state = {"kind": "generation", "prompt_id": "owned"}
        with patch.object(self.comfy, "cancel_prompt", cancel, create=True), \
                patch("anima_webui.runner.CANCEL_PROMPT_TIMEOUT", 0.01):
            await asyncio.wait_for(self.manager._interrupt_current(), 1)
        self.assertTrue(cancel_started.is_set())
        self.assertIn("远端任务可能继续", self.manager.state["cancel_warning"])
