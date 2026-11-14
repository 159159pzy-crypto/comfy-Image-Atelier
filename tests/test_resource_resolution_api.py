"""Resource selection crosses the HTTP, queue, graph and history boundaries."""
import copy
import asyncio
import json
import tempfile
import unittest
from pathlib import Path

from aiohttp.test_utils import TestClient, TestServer

from test_server import APP_DIR, FakeComfy, _write_fake_tools
from anima_webui.server import MANAGER_KEY, create_app
from anima_webui.workflow import DEFAULT_SETTINGS


class InspectedComfy(FakeComfy):
    def __init__(self):
        super().__init__()
        self.lora_files = None
        self.inventory = {
            "models": ["custom-anima.safetensors", "seedvr2_7b_sharp_int8_convrot.safetensors"],
            "clips": ["renamed-encoder.safetensors"], "vaes": ["renamed-main-vae.safetensors", "seed-vae.safetensors"],
            "upscale_models": ["alternate.pth"], "samplers": ["euler", "er_sde"], "schedulers": ["simple", "normal"],
        }
        graph = json.loads((APP_DIR / "templates/workflow_api_v2.json").read_text(encoding="utf-8"))
        self.inventory["node_types"] = sorted({node["class_type"] for node in graph.values()} | {
            "PrimitiveInt", "ComfyMathExpression", "ImageUpscaleWithModel", "UpscaleModelLoader", "KSampler", "LatentUpscaleBy", "SaveImage", "ImageScaleBy", "VRAM_Debug"})
        self.probes = [
            self.item("models", "custom-anima.safetensors", ["anima.model"]),
            self.item("models", "seedvr2_7b_sharp_int8_convrot.safetensors", ["seedvr2.model"]),
            self.item("clips", "renamed-encoder.safetensors", ["anima.clip"]),
            self.item("vaes", "renamed-main-vae.safetensors", ["anima.vae", "krea2.vae"]),
            self.item("vaes", "seed-vae.safetensors", ["seedvr2.vae"]),
        ]

    @staticmethod
    def item(kind, name, roles):
        return {"kind": kind, "name": name, "roles": roles, "source": "header",
                "status": "recognized" if roles else "unknown", "fingerprint": "file-v1:" + name}

    async def resource_inventory(self):
        return copy.deepcopy(self.inventory)

    async def resource_inspection(self, *, refresh=False, modules=None):
        result = {"supported": True, "version": 1, "items": copy.deepcopy(self.probes), "capabilities": {}}
        if self.lora_files is not None:
            result["lora_files"] = copy.deepcopy(self.lora_files)
        return result


class ResourceResolutionApiTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        root = Path(self.temp.name)
        self.comfy = InspectedComfy()
        self.app = create_app(app_dir=APP_DIR, data_dir=root / "data", comfy=self.comfy,
                              anima_tools_dir=_write_fake_tools(root))
        self.client = TestClient(TestServer(self.app))
        await self.client.start_server()
        self.addAsyncCleanup(self.client.close)
        self.manager = self.app[MANAGER_KEY]

    def settings(self, **updates):
        value = copy.deepcopy(DEFAULT_SETTINGS)
        value.update(count=1, pipeline_version=5, base_mode="normal", loras=[])
        for section in ("artist", "character", "clothing", "pose", "background", "expression"):
            if f"random_{section}" in value:
                value[f"random_{section}"] = False
        for stage in value["upscalers"].values():
            stage["enabled"] = False
        value.update(updates)
        return value

    async def resolve(self, settings):
        response = await self.client.post("/api/resources/resolve", json={"settings": settings})
        self.assertEqual(response.status, 200, await response.text())
        return await response.json()

    async def test_disabled_seed_and_missing_recommendations_do_not_block_base(self):
        result = await self.resolve(self.settings())
        self.assertTrue(result["ready"], result["issues"])
        self.assertEqual(result["effective_settings"]["clip_name"], "renamed-encoder.safetensors")
        self.assertFalse(any(item["module"] == "seedvr2" for item in result["issues"] + result["replacements"]))

    async def test_selected_7b_is_frozen_in_graph_metadata_and_history(self):
        settings = self.settings()
        settings["upscalers"]["seedvr2"]["enabled"] = True
        response = await self.client.post("/api/batches", json=settings)
        self.assertEqual(response.status, 201, await response.text())
        state = await response.json()
        self.assertTrue(state["resource_resolution"]["resourceChanged"])
        await self.manager.wait()
        self.assertEqual(self.manager.snapshot()["status"], "completed", self.manager.snapshot())
        submission = self.comfy.submissions[-1]
        loaders = [node["inputs"] for node in submission["prompt"].values() if node["class_type"] == "UNETLoader"]
        self.assertIn("seedvr2_7b_sharp_int8_convrot.safetensors", [node["unet_name"] for node in loaders])
        meta = submission["extra_data"]["extra_pnginfo"]["anima_random_webui"]
        self.assertEqual(meta["settings"]["clip_name"], "renamed-encoder.safetensors")
        self.assertTrue(meta["resource_resolution"]["resourceChanged"])
        history = await (await self.client.get("/api/history")).json()
        record = history["items"][0]
        self.assertEqual(record["bound_resources"]["clip"], "renamed-encoder.safetensors")
        self.assertTrue(record["bound_resources"]["resource_resolution"]["resourceChanged"])

    async def test_empty_inventory_remains_configurable(self):
        for kind in ("models", "clips", "vaes", "upscale_models"):
            self.comfy.inventory[kind] = []
        inventory = await self.client.get("/api/resources")
        self.assertEqual(inventory.status, 200)
        self.assertEqual((await inventory.json())["models"], [])
        result = await self.resolve(self.settings())
        self.assertFalse(result["ready"])
        self.assertTrue(all(item["module"] == "base" for item in result["issues"]))

    async def test_old_single_lock_cannot_override_explicit_compatible_resources(self):
        settings = self.settings(workbench="single", clip_name="renamed-encoder.safetensors", vae_name="renamed-main-vae.safetensors",
                                 single={"unlock_resources": False})
        result = await self.resolve(settings)
        self.assertEqual(result["effective_settings"]["clip_name"], settings["clip_name"])
        self.assertEqual(result["effective_settings"]["vae_name"], settings["vae_name"])
        self.assertFalse(any(item["field"] in {"clip_name", "vae_name"} for item in result["replacements"]))

    async def test_manual_usage_requires_current_fingerprint_and_cannot_override_recognized(self):
        unknown = self.comfy.item("clips", "unknown.safetensors", [])
        self.comfy.inventory["clips"] = [unknown["name"]]
        self.comfy.probes.append(unknown)
        body = {key: unknown[key] for key in ("kind", "name", "fingerprint")}
        body["roles"] = ["anima.clip"]
        stale = await self.client.put("/api/resource-usages", json={**body, "fingerprint": "stale"})
        self.assertEqual(stale.status, 400)
        saved = await self.client.put("/api/resource-usages", json=body)
        self.assertEqual(saved.status, 200, await saved.text())
        self.assertTrue((await self.resolve(self.settings()))["ready"])
        self.comfy.probes[-1]["fingerprint"] = "changed"
        self.assertFalse((await self.resolve(self.settings()))["ready"])
        recognized = self.comfy.probes[0]
        forbidden = await self.client.put("/api/resource-usages", json={
            "kind": "models", "name": recognized["name"], "fingerprint": recognized["fingerprint"], "roles": ["krea2.model"]})
        self.assertEqual(forbidden.status, 400)

    async def test_all_history_modes_resolve_missing_models_without_rewriting_original(self):
        settings = self.settings(random_background=True)
        settings["pools"]["background"] = {"mode": "all", "ids": [], "excluded_ids": []}
        await self.manager.history.create_batch("old", 1, settings)
        record = await self.manager.history.add_image(batch_id="old", sequence=1, prompt_id="old-prompt",
            image={"filename": "original.png", "type": "output"}, positive_prompt="original prompt",
            negative_prompt="original negative", sample_seed=123, prompt_seed=456, settings=settings,
            resolved_selection={}, resolved_prompt="original prompt")
        url = f"/api/history/{record['id']}"
        options = await (await self.client.get(url + "/regeneration-options")).json()
        for mode, option in options["modes"].items():
            with self.subTest(mode=mode):
                self.assertTrue(option["available"], option)
                self.assertTrue(option["resourceChanged"])
                body = {"mode": mode}
                if mode != "replay":
                    body["count"] = 1
                if mode == "content_redraw":
                    body["sections"] = ["background"]
                response = await self.client.post(url + "/regenerate", json=body)
                self.assertEqual(response.status, 201, await response.text())
                self.assertTrue((await response.json())["resource_resolution"]["resourceChanged"])
                await self.manager.wait()
                if mode == "replay":
                    metadata = self.comfy.submissions[-1]["extra_data"]["extra_pnginfo"]["anima_random_webui"]
                    self.assertEqual(metadata["sample_seed"], 123)
                    self.assertEqual(metadata["prompt_seed"], 456)
        original = await self.manager.history.get_image(record["id"])
        self.assertEqual(original["settings"]["model_name"], settings["model_name"])

    async def test_missing_active_lora_blocks_even_when_base_models_can_be_replaced(self):
        result = await self.resolve(self.settings(loras=[{"filename": "deleted.safetensors", "enabled": True, "strength": 1}]))
        self.assertFalse(result["ready"])
        self.assertTrue(any(item["module"] == "loras" for item in result["issues"]))
        self.assertFalse(any(item["module"] == "loras" for item in result["replacements"]))

    async def test_queued_model_identity_change_fails_without_substituting(self):
        entered, release = asyncio.Event(), asyncio.Event()
        original_wait = self.comfy.wait_for_history

        async def gated_wait(*args, **kwargs):
            entered.set()
            await release.wait()
            return await original_wait(*args, **kwargs)

        self.comfy.wait_for_history = gated_wait
        first = await self.client.post("/api/batches", json=self.settings())
        self.assertEqual(first.status, 201, await first.text())
        await asyncio.wait_for(entered.wait(), 5)
        second = await self.client.post("/api/batches", json=self.settings())
        self.assertEqual(second.status, 201, await second.text())
        self.assertEqual((await second.json())["status"], "queued")
        self.comfy.probes[0]["fingerprint"] = "same-path-replaced-file"
        release.set()
        await self.manager.wait()
        # Queue draining schedules the second task at the end of the first.
        if self.manager.task is not None and not self.manager.task.done():
            await self.manager.wait()
        self.assertEqual(len(self.comfy.submissions), 1)
        self.assertEqual(self.manager.snapshot()["status"], "error")
        self.assertIn("资源已变化", self.manager.snapshot()["error"])

    async def test_queued_single_lora_identity_change_blocks_both_profiles(self):
        lora_name = "selected.safetensors"
        self.comfy.preview_items = [{"filename": lora_name}]
        async def lora_filenames():
            return [lora_name]
        self.comfy.lora_filenames = lora_filenames
        original_wait = self.comfy.wait_for_history
        for profile, family in (("anima", "Anima"), ("krea2", "Krea 2 Turbo")):
            with self.subTest(profile=profile):
                self.comfy.probes[0]["roles"] = [profile + ".model"]
                self.comfy.probes[2]["roles"] = [profile + ".clip"]
                self.comfy.base_models[lora_name] = family
                self.comfy.lora_files = [{"name": lora_name, "fingerprint": "lora-before", "status": "available"}]
                settings = self.settings(model_profile=profile, workbench="single",
                    single={"positive_prompt": "a cat"},
                    loras=[{"filename": lora_name, "enabled": True, "strength": 1}])
                entered, release = asyncio.Event(), asyncio.Event()

                async def gated_wait(*args, **kwargs):
                    entered.set()
                    await release.wait()
                    return await original_wait(*args, **kwargs)

                self.comfy.wait_for_history = gated_wait
                before_submissions = len(self.comfy.submissions)
                try:
                    first = await self.client.post("/api/batches", json=settings)
                    self.assertEqual(first.status, 201, await first.text())
                    await asyncio.wait_for(entered.wait(), 5)
                    second = await self.client.post("/api/batches", json=settings)
                    self.assertEqual(second.status, 201, await second.text())
                    queued = await second.json()
                    self.assertEqual(queued["status"], "queued")
                    frozen = [item for item in queued["resource_resolution"]["resources"] if item["kind"] == "loras"]
                    self.assertEqual(frozen, [{"field": "loras.0.filename", "kind": "loras", "name": lora_name,
                                              "fingerprint": "lora-before"}])
                    self.comfy.lora_files[0]["fingerprint"] = "lora-replaced-same-name"
                finally:
                    release.set()
                await self.manager.wait()
                if self.manager.task is not None and not self.manager.task.done():
                    await self.manager.wait()
                self.assertEqual(len(self.comfy.submissions), before_submissions + 1)
                self.assertEqual(self.manager.snapshot()["status"], "error")
                self.assertIn("资源已变化", self.manager.snapshot()["error"])

    async def test_face_uses_its_own_sampler_choices_and_builtin_upscale(self):
        self.comfy.inventory["node_inputs"] = {"ForbiddenVisionFaceProcessorIntegrated": {
            "sampler": [["face-only"]], "scheduler": [["face-schedule"]], "upscaler_model": [["Fast 4x (Lanczos)"]]}}
        settings = self.settings()
        settings["upscalers"]["face_refine"].update(enabled=True, sampler="face-only", scheduler="face-schedule",
            enable_pre_upscale=True, upscaler_model="Fast 4x (Lanczos)", enable_segmentation=False)
        self.comfy.inventory["upscale_models"] = []
        result = await self.resolve(settings)
        self.assertTrue(result["ready"], result["issues"])
        settings["upscalers"]["face_refine"].update(sampler="not-installed", scheduler="not-installed-schedule")
        result = await self.resolve(settings)
        sampler_issues = [issue for issue in result["issues"] if "not-installed" in issue["message"]]
        self.assertEqual(len(sampler_issues), 2, result["issues"])
        self.assertTrue(all(issue["module"] == "face_refine" for issue in sampler_issues))
