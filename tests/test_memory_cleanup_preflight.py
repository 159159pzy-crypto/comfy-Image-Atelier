"""Stage cleanup dependencies fail before queuing work, with recovery guidance."""
import copy
import tempfile
import unittest
from pathlib import Path

from aiohttp.test_utils import TestClient, TestServer

from anima_webui.server import MANAGER_KEY, create_app
from anima_webui.workflow import DEFAULT_SETTINGS
from test_resource_resolution_api import InspectedComfy
from test_server import APP_DIR, _write_fake_tools


class MemoryCleanupPreflightTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name)
        self.comfy = InspectedComfy()
        app = create_app(app_dir=APP_DIR, data_dir=root / "data", comfy=self.comfy,
                         anima_tools_dir=_write_fake_tools(root))
        self.manager = app[MANAGER_KEY]
        self.client = TestClient(TestServer(app))
        await self.client.start_server()
        self.addAsyncCleanup(self.client.close)

    def settings(self, version, workbench):
        value = copy.deepcopy(DEFAULT_SETTINGS)
        value.update(count=1, pipeline_version=version, workbench=workbench,
                     base_mode="normal", loras=[])
        for key in value:
            if key.startswith("random_") and isinstance(value[key], bool):
                value[key] = False
        for stage in value["upscalers"].values():
            stage["enabled"] = False
        return value

    async def resolve(self, settings):
        response = await self.client.post("/api/resources/resolve", json={"settings": settings})
        self.assertEqual(response.status, 200, await response.text())
        return await response.json()

    async def test_missing_cleanup_node_blocks_newer_workbenches_before_submission(self):
        self.comfy.inventory["node_types"].remove("VRAM_Debug")
        for version in (3, 4, 5):
            for workbench in ("random", "single"):
                with self.subTest(version=version, workbench=workbench):
                    settings = self.settings(version, workbench)
                    result = await self.resolve(settings)
                    self.assertFalse(result["ready"])
                    cleanup = [item for item in result["issues"] if "VRAM_Debug" in item["message"]]
                    self.assertEqual(len(cleanup), 1, result["issues"])
                    self.assertEqual(cleanup[0]["module"], "base")
                    for text in ("清缓存", "垃圾回收", "ComfyUI-KJNodes", "重启 ComfyUI"):
                        self.assertIn(text, cleanup[0]["message"])
                    response = await self.client.post("/api/batches", json=settings)
                    self.assertEqual(response.status, 400, await response.text())
                    self.assertIn("ComfyUI-KJNodes", await response.text())
                    self.assertFalse(self.manager.active())
                    self.assertEqual(self.manager.queue, [])
        self.assertEqual(self.comfy.submissions, [])

    async def test_available_cleanup_node_passes_newer_workbench_preflight(self):
        for version in (3, 4, 5):
            for workbench in ("random", "single"):
                with self.subTest(version=version, workbench=workbench):
                    result = await self.resolve(self.settings(version, workbench))
                    self.assertTrue(result["ready"], result["issues"])

    async def test_v2_history_does_not_require_new_cleanup_dependency(self):
        self.comfy.inventory["node_types"].remove("VRAM_Debug")
        for workbench in ("random", "single"):
            with self.subTest(workbench=workbench):
                result = await self.resolve(self.settings(2, workbench))
                self.assertTrue(result["ready"], result["issues"])
