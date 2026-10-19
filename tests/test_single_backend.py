import asyncio
import copy
import io
import tempfile
import unittest
import struct
import zlib
from pathlib import Path

from aiohttp import FormData, web
from aiohttp.test_utils import TestClient, TestServer
from PIL import Image

from anima_webui.comfy import ComfyAborted, ComfyClient
from anima_webui.history import HistoryStore
from anima_webui.input_images import InputImageStore, MAX_IMAGE_BYTES
from anima_webui.prompt_jobs import PromptJobStore
from anima_webui.prompt_processing import OUTPUT_NODE, extract_prompt_result, prompt_fingerprint
from anima_webui.server import HISTORY_KEY, MANAGER_KEY, create_app
from anima_webui.workflow import WorkflowError, validate_settings
from test_server import APP_DIR, FakeComfy, _write_fake_tools


def png(width=120, height=80):
    stream = io.BytesIO()
    Image.new("RGB", (width, height), "red").save(stream, format="PNG")
    return stream.getvalue()


class SingleComfy(FakeComfy):
    def __init__(self):
        super().__init__()
        self.uploads = []
        self.cancelled = []
        self.block = None
        self.prompt_failure = False

    async def object_info(self):
        return {"PreviewAny": {}, "PromptExpand": {"input": {"required": {
            "rule": [["扩写-通用"]], "llm_service": [["test/model"]],
        }}}}

    async def lora_filenames(self):
        return [item["filename"] for item in self.preview_items]

    async def submit(self, payload):
        self.submissions.append(copy.deepcopy(payload))
        return str(len(self.submissions))

    async def wait_for_history(self, prompt_id, should_abort=None, missing_timeout=30):
        payload = self.submissions[int(prompt_id) - 1]
        if self.block:
            while not self.block.is_set():
                if should_abort and should_abort():
                    raise ComfyAborted("cancelled")
                await asyncio.sleep(0.001)
        if should_abort and should_abort():
            raise ComfyAborted("cancelled")
        if OUTPUT_NODE in payload["prompt"]:
            return {"outputs": {OUTPUT_NODE: {"text": ["expanded final prompt"]}},
                    "status": {"status_str": "error" if self.prompt_failure else "success", "completed": not self.prompt_failure}}
        return await super().wait_for_history(prompt_id, should_abort, missing_timeout)

    async def upload_image(self, content, filename):
        self.uploads.append((content, filename))
        return "AnimaStudio/" + filename

    async def cancel_prompt(self, prompt_id):
        self.cancelled.append(prompt_id)

    async def image_bytes(self, image):
        return png(), "image/png"


class InputImageTests(unittest.TestCase):
    def test_owned_image_metadata_resize_and_limits(self):
        with tempfile.TemporaryDirectory() as directory:
            store = InputImageStore(directory)
            asset = store.add(png(), filename="../../picture.png")
            self.assertEqual(asset["filename"], "picture.png")
            self.assertEqual(store.get(asset["id"])["width"], 120)
            for mode in ("crop", "stretch", "pad"):
                with Image.open(io.BytesIO(store.prepare(asset["id"], 64, 64, mode))) as result:
                    self.assertEqual(result.size, (64, 64))
                    if mode == "pad":
                        self.assertEqual(result.getpixel((0, 0)), (255, 255, 255))
            with self.assertRaises(KeyError):
                store.get("../escape")
            for body in (b"not an image", b"a" * (MAX_IMAGE_BYTES + 1)):
                with self.assertRaises(WorkflowError):
                    store.add(body)

    def test_pixel_limit_and_exif_orientation(self):
        with tempfile.TemporaryDirectory() as directory:
            store = InputImageStore(directory)
            small = png(1, 1)
            ihdr = struct.pack(">II", 10001, 4000) + small[24:29]
            oversized = small[:16] + ihdr + struct.pack(">I", zlib.crc32(b"IHDR" + ihdr)) + small[33:]
            with self.assertRaisesRegex(WorkflowError, "4000"):
                store.add(oversized)
            stream = io.BytesIO()
            exif = Image.Exif()
            exif[274] = 6
            Image.new("RGB", (120, 80), "red").save(stream, format="JPEG", exif=exif)
            asset = store.add(stream.getvalue())
            self.assertEqual((asset["width"], asset["height"]), (80, 120))

    def test_processing_requires_success_and_exact_text_output(self):
        good = {"outputs": {OUTPUT_NODE: {"text": ["hello"]}}, "status": {"status_str": "success", "completed": True}}
        self.assertEqual(extract_prompt_result(good), "hello")
        for value in ({}, {**good, "status": {"completed": False}}, {**good, "outputs": {OUTPUT_NODE: {"text": [""]}}}):
            with self.assertRaises(WorkflowError):
                extract_prompt_result(value)


class SingleBackendTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.comfy = SingleComfy()
        self.app = create_app(app_dir=APP_DIR, data_dir=self.root / "data", comfy=self.comfy,
                              anima_tools_dir=_write_fake_tools(self.root))
        self.client = TestClient(TestServer(self.app))
        await self.client.start_server()
        self.manager = self.app[MANAGER_KEY]

    async def asyncTearDown(self):
        await self.client.close()
        self.temporary.cleanup()

    def settings(self, processing=False):
        return validate_settings({"workbench": "single", "count": 1, "single": {
            "positive_prompt": "authored prompt", "prompt_processing": {
                "expand": {"enabled": processing, "llm_service": "test/model"},
            },
        }})

    async def preview(self, settings, *, force=False):
        response = await self.client.post("/api/prompt-jobs", json={"settings": settings, "force": force})
        self.assertEqual(response.status, 201, await response.text())
        job = await response.json()
        await self.manager.wait()
        return await self.manager.prompt_jobs.get(job["id"])

    async def test_config_bootstraps_both_model_profiles_without_live_inventory(self):
        payload = await (await self.client.get("/api/config")).json()
        krea = payload["model_profiles"]["krea2"]
        self.assertEqual(krea["clip_name"], "qwen3vl_4b_fp8_scaled.safetensors")
        self.assertEqual(krea["clip_type"], "krea2")
        self.assertIsNone(krea["available"]["clip"])
        self.assertEqual(payload["model_profiles"]["anima"]["clip_type"], "stable_diffusion")

    async def test_preview_persists_without_artwork_and_can_be_edited_and_reused(self):
        settings = self.settings(True)
        job = await self.preview(settings, force=True)
        self.assertEqual(job["status"], "completed")
        self.assertEqual(job["input_fingerprint"], prompt_fingerprint(settings))
        self.assertEqual(job["result"]["text"], "expanded final prompt")
        self.assertEqual((await self.app[HISTORY_KEY].list_images())["total"], 0)
        self.assertEqual(self.app[HISTORY_KEY].connection.execute("SELECT COUNT(*) FROM batches").fetchone()[0], 0)
        settings["single"]["prompt_job_id"] = job["id"]
        settings["single"]["processed_prompt_override"] = "edited final"
        self.comfy.preview_items = [{"filename": "primary.safetensors", "trigger_words": ["my trigger"]}]
        settings["loras"] = [{"filename": "primary.safetensors", "enabled": True, "strength": 1.0}]
        response = await self.client.post("/api/batches", json=settings)
        self.assertEqual(response.status, 201, await response.text())
        await self.manager.wait()
        self.assertEqual(len(self.comfy.submissions), 2)
        saved = (await self.app[HISTORY_KEY].list_images())["items"][0]
        self.assertEqual(saved["single_runtime"]["positive"], "edited final")
        self.assertIn("my trigger", saved["single_runtime"]["effective_positive"]["primary"])
        self.assertEqual(saved["workbench"], "single")
        settings["single"]["positive_prompt"] = "changed"
        response = await self.client.post("/api/batches", json=settings)
        self.assertEqual(response.status, 400)
        self.assertEqual(len(self.comfy.submissions), 2)

    async def test_auto_processing_then_img2img_upload_and_frozen_replay(self):
        form = FormData()
        form.add_field("image", png(), filename="test.png", content_type="image/png")
        response = await self.client.post("/api/input-images", data=form)
        self.assertEqual(response.status, 201, await response.text())
        asset = await response.json()
        self.assertEqual((await self.client.get(asset["image_url"])).status, 200)
        settings = self.settings(True)
        settings["generation_mode"] = "img2img"
        settings["single"]["input_image_id"] = asset["id"]
        response = await self.client.post("/api/batches", json=settings)
        self.assertEqual(response.status, 201, await response.text())
        await self.manager.wait()
        self.assertEqual(self.manager.snapshot()["status"], "completed", self.manager.snapshot())
        self.assertEqual(len(self.comfy.submissions), 2)
        self.assertEqual(len(self.comfy.uploads), 1)
        self.assertEqual({n["class_type"] for n in self.comfy.submissions[0]["prompt"].values()}, {"PromptExpand", "PreviewAny"})
        saved = (await self.app[HISTORY_KEY].list_images())["items"][0]
        response = await self.client.post(f"/api/history/{saved['id']}/regenerate", json={"mode": "replay"})
        self.assertEqual(response.status, 201, await response.text())
        await self.manager.wait()
        self.assertEqual(len(self.comfy.submissions), 3)
        self.assertEqual(len(self.comfy.uploads), 2)
        last = (await self.app[HISTORY_KEY].list_images())["items"][0]
        self.assertEqual(last["sample_seed"], saved["sample_seed"])
        self.assertEqual(last["single_runtime"]["effective_positive"], saved["single_runtime"]["effective_positive"])

    async def test_cancel_processing_never_submits_image_and_other_queue_entry_survives(self):
        self.comfy.block = asyncio.Event()
        response = await self.client.post("/api/batches", json=self.settings(True))
        self.assertEqual(response.status, 201, await response.text())
        for _ in range(100):
            if self.comfy.submissions:
                break
            await asyncio.sleep(0.001)
        current_id = self.manager.snapshot()["id"]
        response = await self.client.post("/api/prompt-jobs", json={"settings": self.settings(False)})
        queued_job = await response.json()
        await self.manager.request_stop(current_id, clear_queue=False)
        self.comfy.block.set()
        await self.manager.wait()
        self.assertEqual(len(self.comfy.submissions), 1)
        self.assertIn("1", self.comfy.cancelled)
        self.assertEqual((await self.manager.prompt_jobs.get(queued_job["id"]))["status"], "completed")
        self.assertEqual((await self.app[HISTORY_KEY].list_images())["total"], 0)

    async def test_history_filters_and_adoption_keeps_source_asset(self):
        response = await self.client.post("/api/batches", json=self.settings())
        self.assertEqual(response.status, 201, await response.text())
        await self.manager.wait()
        history = await (await self.client.get("/api/history?workbench=single")).json()
        self.assertEqual(history["total"], 1)
        self.assertEqual((await (await self.client.get("/api/history?workbench=random")).json())["total"], 0)
        image_id = history["items"][0]["id"]
        response = await self.client.post("/api/input-images/from-history", json={"image_id": image_id})
        self.assertEqual(response.status, 201, await response.text())
        asset = await response.json()
        await self.client.delete(f"/api/history/{image_id}")
        self.assertEqual((await self.client.get(asset["image_url"])).status, 200)

    async def test_fingerprint_ignores_image_seed_negative_and_triggers(self):
        settings = self.settings(True)
        before = prompt_fingerprint(settings)
        settings["negative_prompt"] = "different"
        settings["lora_managed_triggers"] = ["new trigger"]
        settings["width"] = 1024
        self.assertEqual(prompt_fingerprint(settings), before)
        settings["single"]["prefix"] = "changed"
        self.assertNotEqual(prompt_fingerprint(settings), before)

    async def test_processing_failure_does_not_fall_back_or_generate(self):
        self.comfy.prompt_failure = True
        response = await self.client.post("/api/batches", json=self.settings(True))
        self.assertEqual(response.status, 201)
        await self.manager.wait()
        self.assertEqual(self.manager.snapshot()["status"], "error")
        self.assertEqual(len(self.comfy.submissions), 1)
        self.assertEqual((await self.app[HISTORY_KEY].list_images())["total"], 0)
        job = await self.manager.prompt_jobs.get(self.manager.snapshot()["prompt_job_id"])
        self.assertEqual(job["status"], "error")

    async def test_dual_history_uses_second_pipeline_and_keeps_both_snapshots(self):
        settings = self.settings()
        settings["base_mode"] = "dual_sampler"
        settings["single"]["dual"]["second_pipeline"] = "secondary"
        self.comfy.preview_items = [{"filename": "secondary.safetensors", "trigger_words": ["second-stack"]}]
        settings["single"]["loras_secondary"] = [{"filename": "secondary.safetensors", "enabled": True, "strength": 1.0}]
        response = await self.client.post("/api/batches", json=settings)
        self.assertEqual(response.status, 201, await response.text())
        await self.manager.wait()
        saved = (await self.app[HISTORY_KEY].list_images())["items"][0]
        self.assertEqual(saved["positive_prompt"], "authored prompt, second-stack")
        self.assertEqual(saved["single_runtime"]["effective_positive"]["primary"], "authored prompt")

    async def test_invalid_nested_settings_fail_before_resource_or_job_work(self):
        malformed = [
            {"single": "bad"}, {"single": None}, {"single": []},
            {"loras": None}, {"loras": {}}, {"loras": "bad"},
            {"single": {"loras_secondary": None}},
            {"single": {"prompt_processing": "bad"}},
        ]
        for overrides in malformed:
            settings = {"workbench": "single", **overrides}
            for endpoint, payload in (("/api/batches", settings), ("/api/prompt-jobs", {"settings": settings})):
                with self.subTest(endpoint=endpoint, settings=settings):
                    response = await self.client.post(endpoint, json=payload)
                    self.assertEqual(response.status, 400, await response.text())
                    self.assertTrue((await response.json())["error"])
        self.assertEqual(self.comfy.classification_calls, [])
        self.assertEqual(self.comfy.submissions, [])
        self.assertEqual(self.app[HISTORY_KEY].connection.execute("SELECT COUNT(*) FROM prompt_jobs").fetchone()[0], 0)

    async def test_disabling_or_removing_last_loras_clears_both_managed_stacks(self):
        self.comfy.preview_items = [
            {"filename": "primary.safetensors", "trigger_words": ["primary-trigger"]},
            {"filename": "secondary.safetensors", "trigger_words": ["secondary-trigger"]},
        ]
        settings = self.settings()
        # Both stacks participate; dormant stacks retain their saved triggers.
        settings["upscalers"]["face_refine"]["enabled"] = True
        settings["loras"] = [{"filename": "primary.safetensors", "enabled": True, "strength": 1.0}]
        settings["single"]["loras_secondary"] = [{"filename": "secondary.safetensors", "enabled": True, "strength": 1.0}]
        response = await self.client.post("/api/batches", json=settings)
        self.assertEqual(response.status, 201, await response.text())
        await self.manager.wait()
        original = (await self.app[HISTORY_KEY].list_images())["items"][0]
        self.assertEqual(original["settings"]["lora_managed_triggers"], ["primary-trigger"])
        self.assertEqual(original["settings"]["single"]["secondary_managed_triggers"], ["secondary-trigger"])
        for remove in (False, True):
            restored = copy.deepcopy(original["settings"])
            if remove:
                restored["loras"] = []
                restored["single"]["loras_secondary"] = []
            else:
                restored["loras"][0]["enabled"] = False
                restored["single"]["loras_secondary"][0]["enabled"] = False
            preview = await self.preview(restored)
            self.assertEqual(preview["result"]["primary"], "authored prompt")
            self.assertEqual(preview["result"]["secondary"], "authored prompt")
            restored["single"]["prompt_job_id"] = preview["id"]
            response = await self.client.post("/api/batches", json=restored)
            self.assertEqual(response.status, 201, await response.text())
            await self.manager.wait()
            saved = (await self.app[HISTORY_KEY].list_images())["items"][0]
            self.assertEqual(saved["settings"]["lora_managed_triggers"], [])
            self.assertEqual(saved["settings"]["single"]["secondary_managed_triggers"], [])
            self.assertEqual(set(saved["single_runtime"]["effective_positive"].values()), {"authored prompt"})
        response = await self.client.post(f"/api/history/{original['id']}/regenerate", json={"mode": "replay"})
        self.assertEqual(response.status, 201, await response.text())
        await self.manager.wait()
        replay = (await self.app[HISTORY_KEY].list_images())["items"][0]
        self.assertEqual(replay["single_runtime"]["effective_positive"], original["single_runtime"]["effective_positive"])

    async def test_stop_holds_dispatch_lock_until_current_prompt_is_cancelled(self):
        self.comfy.block = asyncio.Event()
        await self.manager.start(self.settings())
        for _ in range(100):
            if self.comfy.submissions:
                break
            await asyncio.sleep(0.001)
        current_id = self.manager.snapshot()["id"]
        queued = await self.manager.start_prompt_preview(self.settings())
        entered, release = asyncio.Event(), asyncio.Event()
        original = self.app[HISTORY_KEY].update_batch

        async def delayed_update(batch_id, **kwargs):
            if kwargs.get("status") == "stopping":
                entered.set()
                await release.wait()
            return await original(batch_id, **kwargs)

        self.app[HISTORY_KEY].update_batch = delayed_update
        stopping = asyncio.create_task(self.manager.request_stop(current_id, clear_queue=False))
        await entered.wait()
        self.comfy.block.set()
        await asyncio.sleep(0.01)
        self.assertEqual(self.manager.snapshot()["id"], current_id)
        self.assertEqual(len(self.manager.queue), 1)
        release.set()
        await stopping
        await self.manager.wait()
        self.assertTrue(all(value == "1" for value in self.comfy.cancelled))
        self.assertEqual((await self.manager.prompt_jobs.get(queued["id"]))["status"], "completed")

    async def test_preview_cancel_during_dequeue_is_not_lost(self):
        self.comfy.block = asyncio.Event()
        await self.manager.start(self.settings())
        for _ in range(100):
            if self.comfy.submissions:
                break
            await asyncio.sleep(0.001)
        queued = await self.manager.start_prompt_preview(self.settings(True))
        entered, release = asyncio.Event(), asyncio.Event()
        original = self.manager.prompt_jobs.get
        first = True

        async def delayed_get(job_id):
            nonlocal first
            if job_id == queued["id"] and first:
                first = False
                entered.set()
                await release.wait()
            return await original(job_id)

        self.manager.prompt_jobs.get = delayed_get
        self.comfy.block.set()
        await entered.wait()
        self.comfy.block = asyncio.Event()
        cancelling = asyncio.create_task(self.manager.cancel_prompt_preview(queued["id"]))
        await asyncio.sleep(0)
        self.assertFalse(cancelling.done())
        release.set()
        await cancelling
        await self.manager.wait()
        self.assertEqual((await original(queued["id"]))["status"], "cancelled")


class PromptPersistenceTests(unittest.IsolatedAsyncioTestCase):
    async def test_restart_preserves_completed_results_and_interrupts_pending(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "history.sqlite3"
            history = HistoryStore(path)
            store = PromptJobStore(history)
            done = await store.create({}, "done")
            pending = await store.create({}, "pending")
            await store.update(done["id"], status="completed", result={"text": "kept"})
            await history.close()
            history = HistoryStore(path)
            store = PromptJobStore(history)
            self.assertEqual((await store.get(done["id"]))["result"], {"text": "kept"})
            self.assertEqual((await store.get(pending["id"]))["status"], "interrupted")
            await history.close()


class SingleComfyClientTests(unittest.IsolatedAsyncioTestCase):
    async def test_multipart_upload_and_targeted_cancel(self):
        uploads = []
        cancellations = []
        app = web.Application()

        async def upload(request):
            parts = {}
            async for part in await request.multipart():
                parts[part.name] = await part.read()
            uploads.append(parts)
            return web.json_response({"name": "owned.png", "subfolder": "AnimaStudio"})

        async def cancel(request):
            cancellations.append(request.match_info["prompt_id"])
            return web.json_response({"cancelled": True})

        app.router.add_post("/upload/image", upload)
        app.router.add_post("/api/jobs/{prompt_id}/cancel", cancel)
        server = TestServer(app)
        await server.start_server()
        client = ComfyClient(f"http://127.0.0.1:{server.port}")
        try:
            self.assertEqual(await client.upload_image(png(), "owned.png"), "AnimaStudio/owned.png")
            self.assertEqual(uploads[0]["type"], b"input")
            self.assertEqual(uploads[0]["image"], png())
            await client.cancel_prompt("owned-job")
            self.assertEqual(cancellations, ["owned-job"])
        finally:
            await client.close()
            await server.close()

    async def test_legacy_cancel_uses_delete_and_targeted_interrupt_only(self):
        calls = []
        app = web.Application()

        async def record(request):
            calls.append((request.path, await request.json()))
            return web.Response()

        app.router.add_post("/queue", record)
        app.router.add_post("/interrupt", record)
        server = TestServer(app)
        await server.start_server()
        client = ComfyClient(f"http://127.0.0.1:{server.port}")
        try:
            await client.cancel_prompt("owned-job")
            self.assertEqual(calls, [("/queue", {"delete": ["owned-job"]}), ("/interrupt", {"prompt_id": "owned-job"})])
        finally:
            await client.close()
            await server.close()
