import asyncio
import tempfile
import unittest
from pathlib import Path
from unittest.mock import AsyncMock, patch

from anima_webui.history import HistoryStore
from anima_webui.runner import BatchConflict, BatchManager
from anima_webui.comfy import ComfyError
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
        self.assertEqual(self.manager.state["cancel_state"], "unconfirmed")

    async def test_concurrent_and_repeated_cancel_share_one_confirmed_operation(self):
        entered, release = asyncio.Event(), asyncio.Event()

        async def cancel(prompt_id):
            self.assertEqual(prompt_id, "owned")
            entered.set()
            await release.wait()

        self.manager.state = {"id": "batch", "kind": "generation", "prompt_id": "owned",
                              "cancel_warning": "previous unconfirmed warning"}
        with patch.object(self.comfy, "cancel_prompt", AsyncMock(side_effect=cancel), create=True) as cancel_mock, \
                patch.object(self.comfy, "prompt_queued", AsyncMock(return_value=False), create=True) as queued:
            first = asyncio.create_task(self.manager._interrupt_current())
            await entered.wait()
            second = asyncio.create_task(self.manager._interrupt_current())
            await asyncio.sleep(0)
            self.assertEqual(self.manager.state["cancel_state"], "pending")
            release.set()
            await asyncio.gather(first, second)
            await self.manager._interrupt_current()
            cancel_mock.assert_awaited_once_with("owned")
            queued.assert_awaited_once_with("owned")
        self.assertEqual(self.manager.state["cancel_state"], "confirmed")
        self.assertNotIn("cancel_warning", self.manager.state)

    async def test_cancel_acknowledgement_without_queue_check_is_unconfirmed(self):
        self.manager.state = {"id": "batch", "kind": "generation", "prompt_id": "owned"}
        with patch.object(self.comfy, "cancel_prompt", AsyncMock(), create=True):
            await self.manager._interrupt_current()
        self.assertEqual(self.manager.state["cancel_state"], "unconfirmed")
        self.assertIn("远端任务可能继续", self.manager.state["cancel_warning"])
        self.assertFalse(self.comfy.interrupted)

    async def test_failed_queue_read_does_not_confirm_remote_stop(self):
        for result in (ComfyError("queue unavailable"), None):
            self.manager.state = {"id": str(result), "kind": "generation", "prompt_id": "owned"}
            query = AsyncMock(side_effect=result) if isinstance(result, Exception) else AsyncMock(return_value=result)
            with patch.object(self.comfy, "cancel_prompt", AsyncMock(), create=True), \
                    patch.object(self.comfy, "prompt_queued", query, create=True):
                await self.manager._interrupt_current()
            self.assertEqual(self.manager.state["cancel_state"], "unconfirmed")

    async def test_cancelled_job_remaining_in_remote_queue_is_unconfirmed(self):
        self.manager.state = {"id": "batch", "kind": "generation", "prompt_id": "owned"}
        with patch.object(self.comfy, "cancel_prompt", AsyncMock(), create=True), \
                patch.object(self.comfy, "prompt_queued", AsyncMock(return_value=True), create=True), \
                patch("anima_webui.runner.CANCEL_PROMPT_TIMEOUT", 0.01):
            await self.manager._interrupt_current()
        self.assertEqual(self.manager.state["cancel_state"], "unconfirmed")

    async def test_old_text_job_keeps_cancel_result_after_current_job_changes(self):
        for confirmed in (False, True):
            entered, release = asyncio.Event(), asyncio.Event()
            job_id = f"old-{confirmed}"

            async def cancel(_):
                entered.set()
                await release.wait()
                if not confirmed:
                    raise ComfyError("unavailable")

            self.manager.state = {"id": job_id, "kind": "prompt_preview", "prompt_id": "old",
                                  "cancel_warning": "previous warning"}
            with patch.object(self.comfy, "cancel_prompt", cancel, create=True), \
                    patch.object(self.comfy, "prompt_queued", AsyncMock(return_value=False), create=True):
                stopping = asyncio.create_task(self.manager._interrupt_current())
                await entered.wait()
                self.assertEqual(self.manager.prompt_job_snapshot({"id": job_id})["cancel_state"], "pending")
                self.manager.state = {"id": "new", "kind": "prompt_preview", "prompt_id": "new",
                                      "cancel_state": "not_requested"}
                release.set()
                await stopping
            snapshot = self.manager.prompt_job_snapshot({"id": job_id, "status": "cancelled"})
            self.assertEqual(snapshot["cancel_state"], "confirmed" if confirmed else "unconfirmed")
            self.assertEqual("cancel_warning" in snapshot, not confirmed)
            self.assertNotIn("prompt_id", snapshot)  # Cache metadata is not an API field.
            self.assertEqual(self.manager.prompt_job_snapshot({"id": "new"})["cancel_state"], "not_requested")
            self.assertNotIn("cancel_warning", self.manager.state)

    async def test_late_cancel_result_cannot_replace_newer_text_prompt_outcome(self):
        entered, release = asyncio.Event(), asyncio.Event()

        async def cancel(prompt_id):
            if prompt_id == "old":
                entered.set()
                await release.wait()
            else:
                raise ComfyError("unavailable")

        self.manager.state = {"id": "job", "kind": "prompt_preview", "prompt_id": "old"}
        with patch.object(self.comfy, "cancel_prompt", cancel, create=True), \
                patch.object(self.comfy, "prompt_queued", AsyncMock(return_value=False), create=True):
            stopping = asyncio.create_task(self.manager._interrupt_current())
            await entered.wait()
            self.manager.state.update(prompt_id="new", cancel_state="not_requested")
            await self.manager._interrupt_current()
            release.set()
            await stopping
        self.manager.state = None
        snapshot = self.manager.prompt_job_snapshot({"id": "job"})
        self.assertEqual(snapshot["cancel_state"], "unconfirmed")
        self.assertIn("远端任务可能继续", snapshot["cancel_warning"])

    async def test_late_cancel_result_does_not_modify_another_batch_or_prompt(self):
        for replace_state in (False, True):
            entered, release = asyncio.Event(), asyncio.Event()

            async def cancel(_):
                entered.set()
                await release.wait()

            self.manager.state = {"id": str(replace_state), "kind": "generation", "prompt_id": "old"}
            with patch.object(self.comfy, "cancel_prompt", cancel, create=True), \
                    patch.object(self.comfy, "prompt_queued", AsyncMock(return_value=False), create=True):
                stopping = asyncio.create_task(self.manager._interrupt_current())
                await entered.wait()
                if replace_state:
                    self.manager.state = {"id": "new", "prompt_id": "new", "cancel_state": "not_requested"}
                else:
                    self.manager.state.update(prompt_id="new", cancel_state="not_requested")
                release.set()
                await stopping
            self.assertEqual(self.manager.state["cancel_state"], "not_requested")
            self.assertNotIn("cancel_warning", self.manager.state)

    async def test_stop_request_and_aborted_wait_cancel_owned_prompt_only_once(self):
        self.comfy.block = asyncio.Event()
        self.comfy.honor_abort = True
        entered, release = asyncio.Event(), asyncio.Event()

        async def cancel(_):
            entered.set()
            await release.wait()

        with patch.object(self.comfy, "cancel_prompt", AsyncMock(side_effect=cancel), create=True) as cancel_mock, \
                patch.object(self.comfy, "prompt_queued", AsyncMock(return_value=False), create=True):
            state = await self.manager.start({**DEFAULT_SETTINGS, "count": 2})
            await self.submitted()
            await self.manager.start({**DEFAULT_SETTINGS, "count": 1})
            stopping = asyncio.create_task(self.manager.request_stop(state["id"]))
            await entered.wait()
            await asyncio.sleep(0)
            self.assertEqual(self.manager.queue, [])
            release.set()
            await stopping
            result = await self.manager.wait()
            cancel_mock.assert_awaited_once_with("prompt-1")
        self.assertEqual(result["status"], "stopped")
        self.assertEqual(result["cancel_state"], "confirmed")
        self.assertEqual(len(self.comfy.submissions), 1)

    async def test_no_accepted_prompt_needs_no_remote_cancellation(self):
        self.manager.state = {"id": "batch", "kind": "generation", "prompt_id": ""}
        with patch.object(self.comfy, "cancel_prompt", AsyncMock(), create=True) as cancel:
            await self.manager._interrupt_current()
            cancel.assert_not_awaited()
        self.assertEqual(self.manager.state["cancel_state"], "not_requested")
        self.assertFalse(self.comfy.interrupted)
