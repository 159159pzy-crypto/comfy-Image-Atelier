"""Full-page Chromium checks against a temporary app and a network-free Comfy fake.

Opt in with ANIMA_BROWSER_TESTS=1; the ordinary quality matrix needs no browser.
Response gates control ordering without timing sleeps or replacing app functions.
"""
import asyncio
import importlib
import json
import os
import sys
import tempfile
import unittest
from dataclasses import dataclass, field
from pathlib import Path

from aiohttp import web
from aiohttp.test_utils import TestServer

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from test_server import APP_DIR, FakeComfy, _write_fake_tools  # noqa: E402
from anima_webui.server import CUSTOM_PROMPTS_KEY, STYLE_PRESETS_KEY, create_app  # noqa: E402


@dataclass
class ResponseGate:
    method: str
    path: str
    profile: str = ""
    error: bool = False
    claimed: bool = False
    cancelled: bool = False
    entered: asyncio.Event = field(default_factory=asyncio.Event)
    release: asyncio.Event = field(default_factory=asyncio.Event)
    finished: asyncio.Event = field(default_factory=asyncio.Event)


class BrowserComfy(FakeComfy):
    async def object_info(self):
        # This fixture intentionally has no optional prompt-assistant/tagger nodes.
        return {}


@unittest.skipUnless(os.environ.get("ANIMA_BROWSER_TESTS") == "1", "set ANIMA_BROWSER_TESTS=1 for Chromium integration")
class WorkbenchRaceTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="atelier-browser-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.gates = []
        self.requests = []
        self.comfy = BrowserComfy()
        self.app = create_app(app_dir=APP_DIR, comfy=self.comfy, data_dir=self.root / "data",
                              anima_tools_dir=_write_fake_tools(self.root))

        @web.middleware
        async def gate_responses(request, handler):
            self.requests.append((request.method, request.path))
            gate = next((gate for gate in self.gates if not gate.claimed and gate.method == request.method
                         and gate.path == request.path and (not gate.profile or request.query.get("model_profile") == gate.profile)), None)
            if gate is not None:
                gate.claimed = True
            response = await handler(request)
            if gate is not None:
                gate.entered.set()
                try:
                    await gate.release.wait()
                except asyncio.CancelledError:
                    # Read gates deliberately abort their HTTP request when the
                    # user changes context. The server handler may stop too.
                    gate.cancelled = True
                    raise
                finally:
                    gate.finished.set()
                if gate.error:
                    return web.json_response({"error": "delayed old preview failure"}, status=400)
            return response

        self.app.middlewares.insert(0, gate_responses)
        self.server = TestServer(self.app)
        await self.server.start_server()
        self.addAsyncCleanup(self.server.close)
        self.origin = str(self.server.make_url(""))
        api = importlib.import_module("playwright.async_api")
        self.expect = api.expect
        self.playwright = await api.async_playwright().start()
        self.addAsyncCleanup(self.playwright.stop)
        self.browser = await self.playwright.chromium.launch()
        self.addAsyncCleanup(self.browser.close)
        self.context = await self.browser.new_context(viewport={"width": 1440, "height": 1000}, reduced_motion="reduce")
        self.addAsyncCleanup(self.context.close)

        async def local_only(route):
            if route.request.url.startswith(self.origin + "/"):
                await route.continue_()
            else:
                await route.abort()
        await self.context.route("**/*", local_only)
        await self.context.tracing.start(screenshots=True, snapshots=True)
        self.page = await self.context.new_page()
        self.page_errors = []
        self.page.on("pageerror", lambda error: self.page_errors.append(str(error)))
        await self.page.goto(self.origin)
        await self.page.wait_for_function("typeof initialized !== 'undefined' && initialized")
        await self.expect(self.page.locator("#appError")).to_be_hidden()
        await self.page.wait_for_function("typeof stylePresetProfile !== 'undefined' && stylePresetProfile === 'anima'")

    async def asyncTearDown(self):
        for gate in self.gates:
            gate.release.set()
        if hasattr(self, "context"):
            artifacts = APP_DIR / "output" / "playwright"
            artifacts.mkdir(parents=True, exist_ok=True)
            if hasattr(self, "page") and not self.page.is_closed():
                await self.page.screenshot(path=str(artifacts / f"{self._testMethodName}.png"))
            await self.context.tracing.stop(path=str(artifacts / f"{self._testMethodName}.zip"))
        self.assertEqual(self.comfy.submissions, [], "browser race tests must never submit GPU work")
        self.assertNotIn(("POST", "/api/batches"), self.requests)
        self.assertEqual(getattr(self, "page_errors", []), [])

    def hold(self, method, path, **kwargs):
        gate = ResponseGate(method, path, **kwargs)
        self.gates.append(gate)
        return gate

    async def entered(self, gate):
        await asyncio.wait_for(gate.entered.wait(), 10)

    async def release(self, gate):
        gate.release.set()
        await asyncio.wait_for(gate.finished.wait(), 10)
        # A roundtrip through the real event loop, not an arbitrary delay.
        await self.page.evaluate("() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))")

    async def open_import(self):
        if not await self.page.locator("#poolDrawer.open").count():
            await self.page.locator('[data-manage="pose"]').click()
        await self.page.locator("#importCustom").click()
        await self.expect(self.page.locator("#importDialog")).to_be_visible()

    async def choose_file(self, name):
        content = {"items": [{"section": "pose", "title": name, "prompt": f"pose {name}", "groups": ["Examples"]}]}
        await self.page.locator("#importFile").set_input_files({"name": name + ".json", "mimeType": "application/json",
                                                               "buffer": json.dumps(content).encode()})

    async def preview_ready(self, name):
        await self.expect(self.page.locator("#importHint")).to_contain_text(name + ".json · 已完成校验")
        await self.expect(self.page.locator("#commitImport")).to_be_enabled()

    async def open_presets(self):
        if not await self.page.locator(".preset-section").get_attribute("open") == "":
            await self.page.locator(".preset-section > summary").click()
        await self.expect(self.page.locator("#presetName")).to_be_visible()

    async def switch_profile(self, profile):
        await self.page.locator("#model_profile").select_option(profile)
        await self.page.wait_for_function("profile => currentProfile() === profile && stylePresetProfile === profile", arg=profile)

    async def test_import_change_disables_previous_rows_and_keeps_editable_destination(self):
        await self.open_import()
        await self.choose_file("A")
        await self.preview_ready("A")
        delayed = self.hold("POST", "/api/custom-prompts/import/preview")
        await self.choose_file("B")
        await self.entered(delayed)
        await self.expect(self.page.locator("#commitImport")).to_be_disabled()
        # The native button suppresses .click() while disabled as well.
        await self.page.locator("#commitImport").evaluate("button => button.click()")
        self.assertNotIn(("POST", "/api/custom-prompts/import"), self.requests)
        await self.release(delayed)
        await self.preview_ready("B")
        await self.page.locator("#importBundleName").fill("Renamed B")
        async with self.page.expect_response(lambda response: response.url.endswith("/api/custom-prompts/import")):
            await self.page.locator("#commitImport").click()
        self.assertEqual([item["title"] for item in self.app[CUSTOM_PROMPTS_KEY].list("pose")], ["B"])
        saved = json.loads((self.root / "data" / "custom_prompts.json").read_text(encoding="utf-8"))
        self.assertIn("Renamed B", json.dumps(saved))
        await self.expect(self.page.locator("#poolGrid").get_by_text("B", exact=True)).to_be_visible()

    async def test_late_preview_success_or_failure_cannot_replace_newer_file(self):
        await self.open_import()
        for error in (False, True):
            old = self.hold("POST", "/api/custom-prompts/import/preview", error=error)
            await self.choose_file("Old" + str(error))
            await self.entered(old)
            await self.choose_file("New" + str(error))
            await self.preview_ready("New" + str(error))
            await self.release(old)
            await self.preview_ready("New" + str(error))
        await self.page.locator("#importFile").set_input_files({"name": "bad.txt", "mimeType": "text/plain", "buffer": b"bad"})
        await self.expect(self.page.locator("#commitImport")).to_be_disabled()

    async def test_old_import_commit_does_not_close_reopened_dialog(self):
        await self.open_import()
        await self.choose_file("A")
        await self.preview_ready("A")
        old = self.hold("POST", "/api/custom-prompts/import")
        await self.page.locator("#commitImport").click()
        await self.entered(old)
        await self.page.locator("#closeImport").click()
        await self.open_import()
        await self.choose_file("B")
        await self.preview_ready("B")
        await self.release(old)
        self.assertFalse(old.cancelled, "changing the view must not cancel a committed write")
        await self.expect(self.page.locator("#importDialog")).to_be_visible()
        await self.expect(self.page.locator("#importBundleName")).to_have_value("B")
        await self.preview_ready("B")
        self.assertEqual([item["title"] for item in self.app[CUSTOM_PROMPTS_KEY].list("pose")], ["A"])

    async def test_preset_switch_does_not_mix_profiles_or_change_saved_models(self):
        await self.open_presets()
        await self.page.locator("#presetName").fill("Anima style")
        old = self.hold("POST", "/api/style-presets")
        await self.page.locator("#savePreset").click()
        await self.entered(old)
        before = self.app[STYLE_PRESETS_KEY].list("anima")["items"][0]
        await self.switch_profile("krea2")
        await self.release(old)
        self.assertFalse(old.cancelled, "changing profiles must not cancel a committed write")
        await self.expect(self.page.locator("#presetList")).not_to_contain_text("Anima style")
        await self.switch_profile("anima")
        await self.expect(self.page.locator("#presetList")).to_contain_text("Anima style")
        row = self.page.locator(".preset-row").filter(has_text="Anima style")
        async with self.page.expect_response(lambda response: response.request.method == "PUT" and "/api/style-presets/" in response.url):
            await row.locator("[data-preset-favorite]").click()
        after = self.app[STYLE_PRESETS_KEY].list("anima")["items"][0]
        self.assertTrue(after["favorite"])
        self.assertEqual(after["settings"], before["settings"])
        self.assertEqual(self.app[STYLE_PRESETS_KEY].list("krea2")["count"], 0)

    async def test_preset_round_trip_keeps_new_input_and_shows_committed_save(self):
        await self.open_presets()
        await self.page.locator("#presetName").fill("Original")
        old = self.hold("POST", "/api/style-presets")
        await self.page.locator("#savePreset").click()
        await self.entered(old)
        await self.switch_profile("krea2")
        await self.switch_profile("anima")
        await self.page.locator("#presetName").fill("New draft name")
        await self.release(old)
        await self.expect(self.page.locator("#presetName")).to_have_value("New draft name")
        await self.expect(self.page.locator("#presetList")).to_contain_text("Original")

    async def test_old_preset_list_cannot_hide_successful_save(self):
        await self.open_presets()
        await self.switch_profile("krea2")
        old = self.hold("GET", "/api/style-presets", profile="anima")
        await self.switch_profile("anima")
        await self.entered(old)
        await self.page.locator("#presetName").fill("New style")
        await self.page.locator("#savePreset").click()
        await self.expect(self.page.locator("#presetList")).to_contain_text("New style")
        await self.release(old)
        await self.expect(self.page.locator("#presetList")).to_contain_text("New style")

    async def test_cross_profile_update_is_atomic_and_same_profile_edits_work(self):
        await self.open_presets()
        await self.page.locator("#presetName").fill("Protected")
        await self.page.locator("#savePreset").click()
        await self.expect(self.page.locator("#presetList")).to_contain_text("Protected")
        item = self.app[STYLE_PRESETS_KEY].list("anima")["items"][0]
        path = self.root / "data" / "style_presets.json"
        before = path.read_bytes()
        response = await self.context.request.put(self.origin + "/api/style-presets/" + item["id"],
                                                  data={"favorite": True, "model_profile": "krea2"})
        self.assertEqual(response.status, 400)
        self.assertEqual(path.read_bytes(), before)
        self.page.once("dialog", lambda dialog: dialog.accept("Renamed"))
        await self.page.locator("[data-preset-rename]").click()
        await self.expect(self.page.locator("#presetList")).to_contain_text("Renamed")
        self.page.once("dialog", lambda dialog: dialog.accept())
        async with self.page.expect_response(lambda response: response.request.method == "PUT" and "/api/style-presets/" in response.url):
            await self.page.locator("[data-preset-update]").click()
        await self.expect(self.page.locator("#presetList")).to_contain_text("Renamed")
        after = self.app[STYLE_PRESETS_KEY].list("anima")["items"][0]
        self.assertEqual(after["settings"]["model_name"], item["settings"]["model_name"])
        self.assertEqual(after["settings"]["clip_name"], item["settings"]["clip_name"])
