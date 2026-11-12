"""Real-browser reverse recognition with temporary images and network-free models."""
from __future__ import annotations

import importlib
import os
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parent))
fixture = importlib.import_module("test_workbench_races")
from test_prompt_reverse import VisionComfy, source_image  # noqa: E402
from test_prompt_tagger import TaggerComfy, write_model  # noqa: E402
from anima_webui.prompt_processing import OUTPUT_NODE  # noqa: E402
from anima_webui.server import MANAGER_KEY  # noqa: E402

ARTIFACTS = fixture.APP_DIR / "output" / "playwright" / "reverse-metadata"
METADATA_TEXT = "a green garden"
VISION_TEXT = "expanded final prompt"
TAGGER_TEXT = "garden, green, outdoors"


class ReverseBrowserComfy(VisionComfy):
    def __init__(self, root):
        super().__init__()
        write_model(root / "models" / "wd_taggers")
        self.tagger_discovery = TaggerComfy(root)

    async def _json(self, method, path, **kwargs):
        if path in {"/object_info/WDTimmTagger", "/internal/folder_paths", "/system_stats"}:
            return await self.tagger_discovery._json(method, path, **kwargs)
        return await super()._json(method, path, **kwargs)

    async def wait_for_history(self, prompt_id, should_abort=None, missing_timeout=30):
        result = await super().wait_for_history(prompt_id, should_abort, missing_timeout)
        graph = self.submissions[int(prompt_id) - 1]["prompt"]
        if any(node["class_type"] == "WDTimmTagger" for node in graph.values()):
            result["outputs"][OUTPUT_NODE]["text"] = [TAGGER_TEXT]
        return result


@unittest.skipUnless(os.environ.get("ANIMA_BROWSER_TESTS") == "1", "opt in to Chromium acceptance")
class ReverseMetadataBrowserTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        ARTIFACTS.mkdir(parents=True, exist_ok=True)
        with patch.object(fixture, "BrowserComfy", lambda: ReverseBrowserComfy(self.root)):
            await fixture.WorkbenchRaceTests.asyncSetUp(self)
        self.job_requests = []

        def capture(request):
            if request.method == "POST" and request.url == self.origin + "/api/prompt-jobs":
                self.job_requests.append(request.post_data_json)

        self.page.on("request", capture)
        await self.page.locator("#singleWorkbenchTab").click()
        await self.page.locator("#single_positive_prompt").fill("keep my generation prompt")
        await self.page.locator("#singleNegative").fill("keep my negative prompt")
        self.generation_settings = await self.page.evaluate("readSettings()")
        await self.page.locator("#singleCategory_prompt").click()
        await self.page.locator("#promptDeskReverse").click()

    async def asyncTearDown(self):
        for gate in getattr(self, "gates", []):
            gate.release.set()
        ARTIFACTS.mkdir(parents=True, exist_ok=True)
        if hasattr(self, "page") and not self.page.is_closed():
            await self.page.screenshot(path=str(ARTIFACTS / (self._testMethodName + ".png")))
        if hasattr(self, "context"):
            await self.context.tracing.stop(path=str(ARTIFACTS / (self._testMethodName + ".zip")))
        self.assertNotIn(("POST", "/api/batches"), self.requests)
        for submission in self.comfy.submissions:
            classes = {node["class_type"] for node in submission["prompt"].values()}
            self.assertIn(classes, [
                {"LoadImage", "ImageCaptionNode", "PreviewAny"},
                {"LoadImage", "WDTimmTagger", "PreviewAny"},
            ])
        self.assertEqual(getattr(self, "page_errors", []), [])

    async def upload_metadata(self):
        await self.page.locator("#reverseInputUpload").set_input_files({
            "name": "with-metadata.png", "mimeType": "image/png",
            "buffer": source_image(METADATA_TEXT + "\nNegative prompt: blurry\nSteps: 20, Seed: 123, Size: 32x24"),
        })
        await self.expect(self.page.locator("#reverseResult")).to_have_value(METADATA_TEXT)
        await self.expect(self.page.locator("#reverseNegative")).to_have_value("blurry")
        await self.expect(self.page.locator("#reverseSource")).to_contain_text("元数据提取")
        self.assertEqual([job["method"] for job in self.job_requests], ["metadata"])
        self.assertEqual(self.comfy.submissions, [])

    async def recognize(self, method, text, source):
        await self.page.locator(f"#reverseMethod-{method}").click()
        if method == "tagger":
            await self.page.locator("#reverseTaggerModel").select_option("custom-model")
        await self.expect(self.page.locator("#reverseStart")).to_be_enabled()
        async with self.page.expect_response(lambda response: response.request.method == "POST"
                                             and response.url == self.origin + "/api/prompt-jobs") as response:
            await self.page.locator("#reverseStart").click()
        created = await (await response.value).json()
        await self.expect(self.page.locator("#reverseResult")).to_have_value(text)
        await self.expect(self.page.locator(f"#reverseMethod-{method}")).to_have_attribute("aria-selected", "true")
        await self.expect(self.page.locator("#reverseSource")).to_have_text(
            "视觉 AI 反推" if method == "vision" else "本地标签")
        await self.expect(self.page.locator("#reverseNegative")).to_have_value("")
        saved = await self.app[MANAGER_KEY].prompt_jobs.get(created["id"])
        self.assertEqual(saved["result"]["source"], source)
        self.assertEqual(saved["result"]["parameters"], {})
        self.assertEqual(await self.page.evaluate("readSettings()"), self.generation_settings)
        await self.page.locator("#promptReversePanel").screenshot(path=str(ARTIFACTS / f"{method}-result.png"))

    async def test_metadata_image_runs_both_models_and_restores_independent_results(self):
        await self.upload_metadata()
        await self.recognize("vision", VISION_TEXT, "ai")
        await self.recognize("tagger", TAGGER_TEXT, "tagger")
        self.assertEqual([job["method"] for job in self.job_requests], ["metadata", "vision", "tagger"])
        self.assertEqual(len(self.comfy.submissions), 2)
        self.assertEqual(len({job["image_id"] for job in self.job_requests}), 1)

        for reload in (False, True):
            if reload:
                await self.page.reload()
                await self.page.wait_for_function("initialized && singleWorkbenchState.ready")
                await self.page.locator("#singleWorkbenchTab").click()
                await self.page.locator("#singleCategory_prompt").click()
                await self.page.locator("#promptDeskReverse").click()
                await self.expect(self.page.locator("#reverseInputPreview")).to_be_visible()
            for method, text, negative in (("metadata", METADATA_TEXT, "blurry"),
                                           ("vision", VISION_TEXT, ""), ("tagger", TAGGER_TEXT, "")):
                await self.page.locator(f"#reverseMethod-{method}").click()
                await self.expect(self.page.locator("#reverseResult")).to_have_value(text)
                await self.expect(self.page.locator("#reverseNegative")).to_have_value(negative)
                await self.expect(self.page.locator("#reverseReplace")).to_be_enabled()
            self.assertEqual(len(self.job_requests), 3, "switching or restoring must not submit recognition")
            self.assertEqual(await self.page.evaluate("readSettings()"), self.generation_settings)

    async def test_metadata_does_not_bypass_unavailable_models(self):
        await self.upload_metadata()
        self.comfy.vision_available = False
        self.comfy.tagger_discovery.missing = True
        async with self.page.expect_response(lambda response: "/api/prompt-assistant/options?kind=reverse" in response.url):
            await self.page.locator("#reverseRefresh").click()
        for method in ("vision", "tagger"):
            await self.page.locator(f"#reverseMethod-{method}").click()
            await self.expect(self.page.locator("#reverseStart")).to_be_disabled()
        await self.page.locator("#reverseMethod-metadata").click()
        await self.expect(self.page.locator("#reverseStart")).to_be_enabled()
        await self.expect(self.page.locator("#reverseResult")).to_have_value(METADATA_TEXT)
        self.assertEqual([job["method"] for job in self.job_requests], ["metadata"])
        self.assertEqual(self.comfy.submissions, [])
        self.assertEqual(await self.page.evaluate("readSettings()"), self.generation_settings)
