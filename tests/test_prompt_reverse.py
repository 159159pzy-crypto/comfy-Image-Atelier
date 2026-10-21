import asyncio
import copy
import io
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from aiohttp import FormData
from aiohttp.test_utils import TestClient, TestServer
from PIL import Image, PngImagePlugin

from anima_webui.comfy import ComfyError
from anima_webui.prompt_processing import OUTPUT_NODE
from anima_webui.workflow import WorkflowError
from anima_webui.server import HISTORY_KEY, MANAGER_KEY, create_app
from test_prompt_assistant import AssistantComfy
from test_server import APP_DIR, _write_fake_tools


def source_image(parameters=None):
    output = io.BytesIO()
    info = PngImagePlugin.PngInfo()
    if parameters is not None:
        info.add_text("parameters", parameters)
    Image.new("RGB", (32, 24), "green").save(output, format="PNG", pnginfo=info)
    return output.getvalue()


class VisionComfy(AssistantComfy):
    def __init__(self):
        super().__init__()
        self.services[0]["vlm_models"] = [{"name": "vision/model", "api_key": "nested-secret"}]
        self.vision_templates = {"vision_custom": {"name": "Visual template", "content": "Describe the picture."}}
        self.vision_available = True

    async def _json(self, method, path, **kwargs):
        if path == "/object_info/WDTimmTagger":
            self.calls.append((method, path))
            return {}
        if path == "/object_info/ImageCaptionNode":
            self.calls.append((method, path))
            if not self.vision_available:
                return {}
            return {"ImageCaptionNode": {"python_module": "custom_nodes.renamed-prompt-assistant",
                    "input": {"required": {
                        "image": ["IMAGE"], "custom_rule": ["BOOLEAN"], "custom_rule_content": ["STRING"],
                        "rule": [["Visual template"]], "vlm_service": [["My provider/vision/model"]],
                    }}}}
        result = await super()._json(method, path, **kwargs)
        if path.endswith("/config/system_prompts"):
            result["vision_prompts"] = copy.deepcopy(self.vision_templates)
        return result


class ReverseTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temp = tempfile.TemporaryDirectory()
        root = Path(self.temp.name)
        self.comfy = VisionComfy()
        self.app = create_app(app_dir=APP_DIR, data_dir=root / "data", comfy=self.comfy,
                              anima_tools_dir=_write_fake_tools(root))
        self.client = TestClient(TestServer(self.app))
        await self.client.start_server()
        self.manager = self.app[MANAGER_KEY]

    async def asyncTearDown(self):
        if self.comfy.block:
            self.comfy.block.set()
        await self.client.close()
        self.temp.cleanup()

    async def upload(self, parameters=None):
        form = FormData()
        form.add_field("image", source_image(parameters), filename="original.png", content_type="image/png")
        response = await self.client.post("/api/prompt-images", data=form)
        self.assertEqual(response.status, 201, await response.text())
        return await response.json()

    def request(self, asset_id, **overrides):
        return {"kind": "reverse", "method": "vision", "model_profile": "anima", "image_id": asset_id,
                "service_id": "service_1", "model_name": "vision/model", "template_id": "vision_custom", **overrides}

    async def create(self, asset, **overrides):
        response = await self.client.post("/api/prompt-jobs", json=self.request(asset["id"], **overrides))
        self.assertEqual(response.status, 201, await response.text())
        return await response.json()

    async def submitted(self):
        for _ in range(200):
            if self.comfy.submissions:
                return
            await asyncio.sleep(.001)
        self.fail("visual job not submitted")

    async def test_metadata_works_offline_without_options_queue_or_ai(self):
        asset = await self.upload("a green garden\nNegative prompt: blurry\nSteps: 20, Sampler: Euler, CFG scale: 0, Seed: 18446744073709551615, Size: 32x24")
        self.comfy.calls.clear()
        self.comfy.plugin_error = True
        self.comfy.vision_available = False
        with patch.object(self.comfy, "_json", side_effect=AssertionError("metadata must stay local")):
            response = await self.client.post("/api/prompt-jobs", json={
                "kind": "reverse", "model_profile": "krea2", "image_id": asset["id"],
            })
            self.assertEqual(response.status, 201, await response.text())
            job = await response.json()
        self.assertEqual(job["status"], "completed")
        self.assertEqual(job["result"]["source"], "metadata")
        self.assertEqual(job["result"]["text"], "a green garden")
        self.assertEqual(job["result"]["negative"], "blurry")
        self.assertIn("18446744073709551615", json.dumps(job["result"]["parameters"]))
        self.assertFalse(self.manager.active())
        self.assertEqual(self.comfy.submissions, [])
        self.assertEqual(self.comfy.uploads, [])
        restored = await (await self.client.get(f"/api/prompt-jobs/{job['id']}")).json()
        self.assertEqual(restored["result"], job["result"])
        saved = await (await self.client.get(f"/api/prompt-images/{asset['id']}")).json()
        self.assertEqual(saved["metadata"], asset["metadata"])
        preview = await self.client.get(asset["image_url"])
        self.assertEqual(preview.status, 200)
        with Image.open(io.BytesIO(await preview.read())) as image:
            self.assertNotIn("parameters", image.info)

    async def test_visual_options_are_independent_allowlisted_and_have_defaults(self):
        self.comfy.template_only = False
        options = await (await self.client.get("/api/prompt-assistant/options?kind=reverse")).json()
        self.assertTrue(options["available"])
        self.assertTrue(options["methods"]["metadata"]["available"])
        self.assertTrue(options["methods"]["vision"]["available"])
        self.assertFalse(options["methods"]["tagger"]["available"])
        self.assertEqual(options["services"][0]["models"], [{"name": "vision/model"}])
        self.assertEqual(set(options["defaults"]), {"anima", "krea2"})
        self.assertIn("vision_custom", [item["id"] for item in options["templates"]])
        for forbidden in ("secret", "api_key", "base_url", "Describe the picture", "expand_custom"):
            self.assertNotIn(forbidden, json.dumps(options))
        rewrite = await (await self.client.get("/api/prompt-assistant/options")).json()
        self.assertFalse(rewrite["available"])
        self.assertEqual((await self.client.get("/api/prompt-assistant/options?kind=other")).status, 400)

    async def test_no_metadata_submits_one_image_graph_and_no_generated_artwork(self):
        asset = await self.upload()
        job = await self.create(asset)
        await self.manager.wait()
        saved = await self.manager.prompt_jobs.get(job["id"])
        self.assertEqual(saved["status"], "completed", saved["error"])
        self.assertEqual(saved["result"]["source"], "ai")
        self.assertEqual(saved["result"]["parameters"], {})
        self.assertEqual(saved["result"]["negative"], "")
        self.assertEqual(len(self.comfy.submissions), 1)
        self.assertEqual(len(self.comfy.uploads), 1)
        graph = self.comfy.submissions[0]["prompt"]
        self.assertEqual({node["class_type"] for node in graph.values()}, {"LoadImage", "ImageCaptionNode", "PreviewAny"})
        inputs = graph["studio_prompt_reverse"]["inputs"]
        self.assertEqual(inputs["vlm_service"], "My provider/vision/model")
        self.assertEqual(inputs["custom_rule_content"], "Describe the picture.")
        self.assertTrue(inputs["custom_rule"])
        self.assertEqual(inputs["user_prompt"], "")
        self.assertEqual((await self.app[HISTORY_KEY].list_images())["total"], 0)

    async def test_queue_preserves_template_and_local_metadata_bypasses_busy_ai(self):
        asset = await self.upload()
        self.comfy.block = asyncio.Event()
        first = await self.create(asset)
        await self.submitted()
        second = await self.create(asset, model_profile="krea2")
        self.assertEqual(second["status"], "queued")
        self.comfy.vision_templates["vision_custom"]["content"] = "Changed template"
        local = await self.create(await self.upload("local prompt\nSteps: 1, Seed: 0"))
        self.assertEqual(local["status"], "completed")
        self.assertEqual(self.manager.state["id"], first["id"])
        self.comfy.block.set()
        await self.manager.wait()
        self.assertEqual(self.comfy.submissions[1]["prompt"]["studio_prompt_reverse"]["inputs"]["custom_rule_content"], "Describe the picture.")

    async def test_missing_configuration_preserves_image_and_builtin_template_works(self):
        asset = await self.upload()
        self.comfy.services[0]["vlm_models"] = []
        response = await self.client.post("/api/prompt-jobs", json=self.request(asset["id"]))
        self.assertEqual(response.status, 400)
        self.assertEqual((await self.client.get(asset["image_url"])).status, 200)
        self.assertEqual(self.comfy.submissions, [])
        self.comfy.services[0]["vlm_models"] = [{"name": "vision/model"}]
        self.comfy.vision_templates = {}
        job = await self.create(asset, template_id="builtin:reverse:anima")
        await self.manager.wait()
        self.assertEqual((await self.manager.prompt_jobs.get(job["id"]))["status"], "completed")

    async def test_cancel_and_timeout_only_target_owned_visual_job(self):
        asset = await self.upload()
        self.comfy.block = asyncio.Event()
        first = await self.create(asset)
        await self.submitted()
        second = await self.create(asset, model_profile="krea2")
        await self.client.post(f"/api/prompt-jobs/{first['id']}/cancel")
        self.comfy.block.set()
        await self.manager.wait()
        self.assertEqual((await self.manager.prompt_jobs.get(first["id"]))["status"], "cancelled")
        self.assertEqual((await self.manager.prompt_jobs.get(second["id"]))["status"], "completed")
        self.assertEqual(set(self.comfy.cancelled), {"1"})
        self.comfy.block = asyncio.Event()
        with patch("anima_webui.runner.PROMPT_JOB_TIMEOUT", .2):
            third = await self.create(asset)
            await self.manager.wait()
        self.assertEqual((await self.manager.prompt_jobs.get(third["id"]))["status"], "error")
        self.assertEqual(set(self.comfy.cancelled), {"1", "3"})

    async def test_provider_error_and_empty_output_do_not_leak_or_return_success(self):
        asset = await self.upload()
        with patch.object(self.comfy, "submit", side_effect=ComfyError("secret-provider-key")):
            job = await self.create(asset)
            await self.manager.wait()
        saved = await self.manager.prompt_jobs.get(job["id"])
        self.assertEqual(saved["status"], "error")
        self.assertNotIn("secret", json.dumps(saved))
        self.assertNotIn("secret", json.dumps(self.manager.snapshot()))
        empty = {"outputs": {OUTPUT_NODE: {"text": [" "]}}, "status": {"status_str": "success", "completed": True}}
        with patch.object(self.comfy, "wait_for_history", return_value=empty):
            job = await self.create(asset)
            await self.manager.wait()
        saved = await self.manager.prompt_jobs.get(job["id"])
        self.assertEqual(saved["status"], "error")
        self.assertFalse(saved["result"])

    async def test_cancel_failure_hides_provider_details_in_state_job_and_logs(self):
        asset = await self.upload()
        self.comfy.block = asyncio.Event()
        job = await self.create(asset)
        await self.submitted()
        sensitive = "synthetic-private-provider-token"
        with self.assertLogs("anima_webui.runner", level="WARNING") as logs:
            with patch.object(self.comfy, "cancel_prompt", side_effect=ComfyError(sensitive)):
                response = await self.client.post(f"/api/prompt-jobs/{job['id']}/cancel")
                self.assertEqual(response.status, 200)
                self.assertNotIn(sensitive, await response.text())
                self.comfy.block.set()
                await self.manager.wait()
        saved = await self.manager.prompt_jobs.get(job["id"])
        self.assertEqual(saved["status"], "cancelled")
        self.assertNotIn(sensitive, json.dumps(saved))
        self.assertNotIn(sensitive, json.dumps(self.manager.snapshot()))
        self.assertNotIn(sensitive, "\n".join(logs.output))
        self.assertTrue(self.manager.snapshot()["cancel_warning"])

    async def test_timeout_cancel_failure_hides_provider_details(self):
        asset = await self.upload()
        self.comfy.block = asyncio.Event()
        sensitive = "synthetic-private-provider-token"
        with self.assertLogs("anima_webui.runner", level="WARNING") as logs:
            with patch("anima_webui.runner.PROMPT_JOB_TIMEOUT", .1), patch.object(
                self.comfy, "cancel_prompt", side_effect=ComfyError(sensitive)
            ):
                job = await self.create(asset)
                await self.manager.wait()
        saved = await self.manager.prompt_jobs.get(job["id"])
        self.assertEqual(saved["status"], "error")
        self.assertIn("超过", saved["error"])
        self.assertNotIn(sensitive, json.dumps(saved))
        self.assertNotIn(sensitive, json.dumps(self.manager.snapshot()))
        self.assertNotIn(sensitive, "\n".join(logs.output))

    async def test_cancel_during_upload_does_not_submit_a_visual_job(self):
        asset = await self.upload()
        upload_started, release_upload = asyncio.Event(), asyncio.Event()

        async def delayed_upload(body, filename):
            upload_started.set()
            await release_upload.wait()
            return "AnimaStudio/" + filename

        with patch.object(self.comfy, "upload_image", side_effect=delayed_upload):
            job = await self.create(asset)
            await asyncio.wait_for(upload_started.wait(), 2)
            response = await self.client.post(f"/api/prompt-jobs/{job['id']}/cancel")
            self.assertEqual(response.status, 200)
            release_upload.set()
            await self.manager.wait()
        self.assertEqual((await self.manager.prompt_jobs.get(job["id"]))["status"], "cancelled")
        self.assertEqual(self.comfy.submissions, [])
        self.assertEqual(self.comfy.cancelled, [])

    async def test_invalid_inputs_never_reach_ai(self):
        asset = await self.upload()
        for overrides in ({"image_id": "../../x"}, {"image_id": "0" * 64}, {"model_profile": "other"},
                          {"service_id": "missing"}, {"template_id": "missing"}, {"source_text": "wrong type"}):
            response = await self.client.post("/api/prompt-jobs", json=self.request(asset["id"], **overrides))
            self.assertIn(response.status, {400, 404}, await response.text())
        self.assertEqual(self.comfy.submissions, [])
        bad = FormData()
        bad.add_field("image", b"broken", filename="bad.png", content_type="image/png")
        self.assertEqual((await self.client.post("/api/prompt-images", data=bad)).status, 400)

    async def test_upload_and_default_method_only_read_metadata_even_without_configuration(self):
        self.comfy.plugin_error = True
        self.comfy.vision_available = False
        with patch.object(self.comfy, "_json", side_effect=AssertionError("initial inspection must stay local")):
            for parameters in (None, "Steps: 10, Seed: 42", "\nNegative prompt: blur\nSteps: 20"):
                asset = await self.upload(parameters)
                response = await self.client.post("/api/prompt-jobs", json={"kind": "reverse", "image_id": asset["id"]})
                self.assertEqual(response.status, 201)
                job = await response.json()
                self.assertEqual(job["status"], "completed")
                self.assertEqual(job["result"]["outcome"], "no_metadata")
                self.assertEqual(job["result"]["source"], "none")
                self.assertEqual(job["settings"]["method"], "metadata")
                for _ in range(2):
                    self.assertEqual((await self.client.get(f"/api/prompt-jobs/{job['id']}")).status, 200)
        self.assertEqual(self.comfy.submissions, [])
        self.assertEqual(self.comfy.uploads, [])
        self.assertFalse(self.manager.active())

    async def test_metadata_precedes_each_method_and_invalid_engine_settings(self):
        asset = await self.upload("original flower\nSteps: 4, Seed: 0")
        with patch.object(self.comfy, "_json", side_effect=AssertionError("metadata takes priority")):
            for method in ("metadata", "vision", "tagger"):
                job = await self.create(asset, method=method, seed="not a valid seed", tagger={"model": "missing"})
                self.assertEqual(job["result"]["source"], "metadata")
                self.assertEqual(job["result"]["method"], "metadata")
                self.assertEqual(job["result"]["outcome"], "found")
        self.assertEqual(self.comfy.submissions, [])

    async def test_fixed_and_random_visual_seeds_are_frozen_strings(self):
        asset = await self.upload()
        for seed in ("0", "18446744073709551615", "random"):
            with patch("anima_webui.prompt_assistant.secrets.randbits", return_value=123456789123456789):
                job = await self.create(asset, seed=seed)
            expected = "123456789123456789" if seed == "random" else seed
            self.assertEqual(job["settings"]["seed"], expected)
            await self.manager.wait()
            saved = await self.manager.prompt_jobs.get(job["id"])
            self.assertEqual(saved["status"], "completed")
            self.assertEqual(saved["result"]["recognition"]["seed"], expected)
            self.assertEqual(saved["result"]["parameters"], {})
            self.assertEqual(self.comfy.submissions[-1]["prompt"]["studio_prompt_reverse"]["inputs"]["seed"], int(expected))
        for seed in (0, -1, True, None, "-1", "1.5", "18446744073709551616", [], {}):
            response = await self.client.post("/api/prompt-jobs", json=self.request(asset["id"], seed=seed))
            self.assertEqual(response.status, 400, await response.text())

    async def test_each_profile_allows_one_active_recognition_but_metadata_is_immediate(self):
        asset = await self.upload()
        self.comfy.block = asyncio.Event()
        await self.create(asset)
        await self.submitted()
        for method in ("vision", "tagger"):
            response = await self.client.post("/api/prompt-jobs", json=self.request(asset["id"], method=method))
            self.assertEqual(response.status, 409)
        inspected = await self.create(asset, method="metadata")
        self.assertEqual(inspected["status"], "completed")
        self.assertEqual(inspected["result"]["outcome"], "no_metadata")
        other = await self.create(asset, model_profile="krea2")
        self.assertEqual(other["status"], "queued")
        self.comfy.block.set()
        await self.manager.wait()

    def install_fake_tagger(self):
        class LocalTagger:
            ready = True

            async def prepare(inner, payload):
                return {"kind": "reverse", "method": "tagger", "workbench": "single",
                        "image_id": payload["image_id"], "model_profile": payload.get("model_profile", "anima"),
                        "tagger": copy.deepcopy(payload["tagger"])}

            async def transport(inner, snapshot):
                if not inner.ready:
                    raise WorkflowError("本地模型文件不完整，请在启动器中修复")
                return copy.deepcopy(snapshot)

            async def options(inner):
                return {"available": True, "reason": "", "models": [{"name": "local", "state": "ready", "reason": ""}], "defaults": {}}

        self.manager.prompt_tagger = LocalTagger()
        return {"method": "tagger", "tagger": {"model": "local", "general_threshold": .35,
                                               "character_threshold": .75, "exclude_tags": "watermark, text"}}

    async def test_local_graph_is_independent_of_visual_service_and_exposes_only_recognition_settings(self):
        asset = await self.upload()
        config = self.install_fake_tagger()
        self.comfy.plugin_error = True
        self.comfy.vision_available = False
        options = await (await self.client.get("/api/prompt-assistant/options?kind=reverse")).json()
        self.assertFalse(options["methods"]["vision"]["available"])
        self.assertTrue(options["methods"]["tagger"]["available"])
        with patch.object(self.comfy, "_json", side_effect=AssertionError("local execution must not need a provider")):
            job = await self.create(asset, **config)
            await self.manager.wait()
        saved = await self.manager.prompt_jobs.get(job["id"])
        self.assertEqual(saved["status"], "completed", saved["error"])
        result = saved["result"]
        self.assertEqual(result["source"], "tagger")
        self.assertEqual(result["recognition"], {"method": "tagger", **config["tagger"]})
        self.assertEqual(result["parameters"], {})
        self.assertEqual(result["negative"], "")
        graph = self.comfy.submissions[0]["prompt"]
        self.assertEqual({node["class_type"] for node in graph.values()}, {"LoadImage", "WDTimmTagger", "PreviewAny"})
        self.assertEqual(graph[OUTPUT_NODE]["inputs"]["source"], ["studio_prompt_tagger", 0])
        settings = graph["studio_prompt_tagger"]["inputs"]
        self.assertEqual(settings["model_name"], "local")
        self.assertEqual(settings["batch_size"], 1)
        self.assertEqual(settings["dtype"], "auto")
        self.assertFalse(settings["add_rating"])
        self.assertEqual(settings["exclude_tags"], "watermark, text")
        self.assertEqual((await self.app[HISTORY_KEY].list_images())["total"], 0)

    async def test_queued_local_model_is_revalidated_and_frozen(self):
        asset = await self.upload()
        config = self.install_fake_tagger()
        self.comfy.block = asyncio.Event()
        await self.create(asset)
        await self.submitted()
        queued = await self.create(asset, model_profile="krea2", **config)
        config["tagger"]["exclude_tags"] = "changed"
        self.assertEqual(queued["settings"]["tagger"]["exclude_tags"], "watermark, text")
        self.manager.prompt_tagger.ready = False
        self.comfy.block.set()
        await self.manager.wait()
        saved = await self.manager.prompt_jobs.get(queued["id"])
        self.assertEqual(saved["status"], "error")
        self.assertIn("不完整", saved["error"])
        self.assertEqual(len(self.comfy.submissions), 1)
        self.assertEqual(len(self.comfy.uploads), 1)

    async def test_local_cancel_timeout_empty_and_multiple_output(self):
        asset = await self.upload()
        config = self.install_fake_tagger()
        self.comfy.block = asyncio.Event()
        job = await self.create(asset, **config)
        await self.submitted()
        await self.client.post(f"/api/prompt-jobs/{job['id']}/cancel")
        self.comfy.block.set()
        await self.manager.wait()
        self.assertEqual((await self.manager.prompt_jobs.get(job["id"]))["status"], "cancelled")
        self.comfy.block = asyncio.Event()
        with patch("anima_webui.runner.PROMPT_JOB_TIMEOUT", .1):
            job = await self.create(asset, **config)
            await self.manager.wait()
        self.assertEqual((await self.manager.prompt_jobs.get(job["id"]))["status"], "error")
        self.comfy.block.set()
        for output in ([], [""], ["one", "two"]):
            entry = {"outputs": {OUTPUT_NODE: {"text": output}}, "status": {"status_str": "success", "completed": True}}
            with patch.object(self.comfy, "wait_for_history", return_value=entry):
                job = await self.create(asset, **config)
                await self.manager.wait()
            self.assertEqual((await self.manager.prompt_jobs.get(job["id"]))["status"], "error")
