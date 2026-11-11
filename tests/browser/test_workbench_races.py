"""Full-page Chromium checks against a temporary app and a network-free Comfy fake.

Opt in with ANIMA_BROWSER_TESTS=1; the ordinary quality matrix needs no browser.
Response gates control ordering without timing sleeps or replacing app functions.
"""
import asyncio
import importlib
import io
import json
import os
import sys
import tempfile
import unittest
from dataclasses import dataclass, field
from pathlib import Path

from aiohttp import web
from aiohttp.test_utils import TestServer
from PIL import Image

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
        self.browser = await self.playwright.chromium.launch(channel=os.environ.get("ANIMA_BROWSER_CHANNEL") or None)
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
        await self.expect(self.page.locator("#manageStylePresets")).to_be_visible()

    async def new_preset(self, name):
        await self.open_presets()
        await self.page.locator("#savePreset").click()
        await self.expect(self.page.locator("#stylePresetEditor")).to_be_visible()
        await self.page.locator("#stylePresetName").fill(name)

    async def close_preset_editor(self):
        await self.page.locator("#stylePresetEditor .spm-heading button").click()
        await self.expect(self.page.locator("#stylePresetEditor")).to_be_hidden()

    async def switch_profile(self, profile):
        await self.page.locator("#model_profile").select_option(profile)
        await self.page.wait_for_function("profile => currentProfile() === profile && stylePresetProfile === profile", arg=profile)

    async def resource_fixture(self, seed_models):
        """Exercise real editor events against an explicit resolver contract, never GPU work."""
        response = await self.context.request.get(self.origin + "/api/resources?model_profile=anima")
        payload = await response.json()
        catalog = []
        for profile in ("anima", "krea2"):
            definition = payload["model_profiles"][profile]
            for kind, resource_field, role in (("models", "model_name", "model"), ("clips", "clip_name", "clip"), ("vaes", "vae_name", "vae")):
                existing = next((item for item in catalog if item["kind"] == kind and item["name"] == definition[resource_field]), None)
                if existing:
                    existing["roles"].append(profile + "." + role)
                else:
                    catalog.append({"kind": kind, "name": definition[resource_field], "roles": [profile + "." + role], "status": "recognized", "source": "fixture", "fingerprint": "fixture"})
        for name in ("custom/clip-a.safetensors", "custom/clip-b.safetensors"):
            catalog.append({"kind": "clips", "name": name, "roles": ["anima.clip"], "status": "recognized", "source": "manual", "fingerprint": "fixture"})
        catalog.extend({"kind": "models", "name": name, "roles": ["seedvr2.model"], "status": "recognized", "source": "fixture", "fingerprint": "fixture"} for name in seed_models)
        catalog.append({"kind": "vaes", "name": "custom/seed-vae.safetensors", "roles": ["seedvr2.vae"], "status": "recognized", "source": "fixture", "fingerprint": "fixture"})
        payload["resource_catalog"] = catalog
        payload["model_profiles"]["anima"]["missing"] = ["old-default-seed.safetensors"]

        async def inventory(route):
            await route.fulfill(json=payload)

        async def resolve(route):
            settings = route.request.post_data_json["settings"]
            replacements, issues = [], []
            seed = settings["upscalers"]["seedvr2"]
            if seed["enabled"]:
                if not seed_models:
                    issues.append({"module": "seedvr2", "field": "upscalers.seedvr2.model_name", "code": "missing_model", "message": "缺少可用 SeedVR2 模型"})
                elif seed.get("model_name") not in seed_models:
                    replacements.append({"module": "seedvr2", "field": "upscalers.seedvr2.model_name", "from": seed.get("model_name", ""), "to": seed_models[0]})
                    seed["model_name"] = seed_models[0]
                if seed.get("vae_name") != "custom/seed-vae.safetensors":
                    replacements.append({"module": "seedvr2", "field": "upscalers.seedvr2.vae_name", "from": seed.get("vae_name", ""), "to": "custom/seed-vae.safetensors"})
                    seed["vae_name"] = "custom/seed-vae.safetensors"
            await route.fulfill(json={"version": 1, "ready": not issues, "effective_settings": settings, "replacements": replacements, "issues": issues})

        await self.page.route("**/api/resources?*", inventory)
        await self.page.route("**/api/resources/resolve", resolve)
        await self.page.evaluate("() => loadResources({refresh: true})")
        await self.page.wait_for_function("resourceResolutionState.result !== null")
        return catalog

    async def test_resource_modules_warn_only_when_enabled_and_clear_on_disable(self):
        await self.resource_fixture([])
        await self.expect(self.page.locator("#resourceWarning")).to_be_hidden()
        await self.page.locator('[data-workbench="single"]').click()
        await self.page.locator('[data-single-category="repair"]').click()
        await self.page.locator("#seedvr2_enabled").check()
        await self.expect(self.page.locator("#resourceIssue_seedvr2")).to_contain_text("缺少可用 SeedVR2 模型")
        await self.expect(self.page.locator("#resourceIssue_seedvr2")).to_be_visible()
        await self.expect(self.page.locator("#resourceWarning")).to_be_hidden()
        await self.expect(self.page.locator("#startButton")).to_be_disabled()
        await self.page.screenshot(path=str(APP_DIR / "output" / "playwright" / "resource-seed-missing-enabled.png"))
        await self.page.locator("#seedvr2_enabled").uncheck()
        await self.expect(self.page.locator("#resourceIssue_seedvr2")).to_be_hidden()
        await self.expect(self.page.locator("#startButton")).to_be_enabled()

    async def test_resource_auto_replacement_persists_actual_choice_and_origin(self):
        await self.resource_fixture(["custom/seed-7b.safetensors"])
        await self.page.locator('[data-workbench="single"]').click()
        await self.page.locator('[data-single-category="repair"]').click()
        await self.page.locator("#seedvr2_enabled").check()
        await self.expect(self.page.locator("#seedvr2_model_name")).to_have_value("custom/seed-7b.safetensors")
        await self.expect(self.page.locator("#seedvr2_vae_name")).to_have_value("custom/seed-vae.safetensors")
        await self.expect(self.page.locator("#resourceIssue_seedvr2")).to_be_hidden()
        await self.page.wait_for_function("JSON.parse(localStorage.getItem(WORKBENCH_DRAFT_KEY)).drafts.single.anima.settings.upscalers.seedvr2.model_name === 'custom/seed-7b.safetensors'")
        saved = await self.page.evaluate("JSON.parse(localStorage.getItem(WORKBENCH_DRAFT_KEY)).drafts.single.anima.settings")
        self.assertTrue(any(item["to"] == "custom/seed-7b.safetensors" for item in saved["resource_replacements"]))
        await self.page.locator("#seedvr2_enabled").uncheck()
        await self.expect(self.page.locator("#seedvr2_model_name")).to_have_value("custom/seed-7b.safetensors")
        await self.page.wait_for_function("JSON.parse(localStorage.getItem(WORKBENCH_DRAFT_KEY)).drafts.single.anima.settings.upscalers.seedvr2.enabled === false")
        await self.page.reload()
        await self.page.wait_for_function("initialized && singleWorkbenchState.ready")
        await self.expect(self.page.locator("#seedvr2_model_name")).to_have_value("custom/seed-7b.safetensors")
        await self.expect(self.page.locator("#seedvr2_enabled")).not_to_be_checked()
        restored = await self.page.evaluate("readSettings().resource_replacements")
        self.assertTrue(any(item["to"] == "custom/seed-7b.safetensors" for item in restored))

    async def test_resource_codecs_keep_independent_single_and_random_drafts(self):
        await self.resource_fixture([])
        await self.page.locator("#clip_name").select_option("custom/clip-a.safetensors")
        await self.page.locator('[data-workbench="single"]').click()
        await self.page.locator("#clip_name").select_option("custom/clip-b.safetensors")
        await self.page.locator('[data-workbench="random"]').click()
        await self.expect(self.page.locator("#clip_name")).to_have_value("custom/clip-a.safetensors")
        await self.page.locator('[data-workbench="single"]').click()
        await self.expect(self.page.locator("#clip_name")).to_have_value("custom/clip-b.safetensors")
        values = await self.page.evaluate("({form: ui.clip_name.value, submitted: readSettings().clip_name})")
        self.assertEqual(values, {"form": "custom/clip-b.safetensors", "submitted": "custom/clip-b.safetensors"})

    async def test_resource_unknown_usage_can_be_assigned_and_removed_in_narrow_dialog(self):
        catalog = await self.resource_fixture([])
        unknown = {"kind": "clips", "name": "my-models/unknown-encoder.safetensors", "roles": [], "status": "unknown", "source": "inventory", "fingerprint": "test-fingerprint"}
        catalog.append(unknown)
        writes = []

        async def usage(route):
            body = route.request.post_data_json
            writes.append((route.request.method, body))
            if route.request.method == "PUT":
                unknown.update(roles=body["roles"], status="recognized", source="manual")
            else:
                unknown.update(roles=[], status="unknown", source="inventory")
            await route.fulfill(json=unknown)

        await self.page.route("**/api/resource-usages", usage)
        await self.page.locator("#manageResourceOwnership").click()
        await self.page.locator("#ownershipKind").select_option("clips")
        row = self.page.locator(".ownership-row").filter(has_text="unknown-encoder.safetensors")
        await row.locator("select").select_option("anima.clip")
        await row.get_by_role("button", name="保存用途").click()
        await self.expect(row).to_contain_text("用途：Anima 文本编码器")
        self.assertEqual(writes[0], ("PUT", {"kind": "clips", "name": unknown["name"], "roles": ["anima.clip"], "fingerprint": "test-fingerprint"}))
        await self.page.set_viewport_size({"width": 390, "height": 844})
        await self.expect(row.get_by_role("button", name="撤销指定")).to_be_visible()
        width = await self.page.locator("#resourceOwnershipDialog").evaluate("el => ({scroll: el.scrollWidth, client: el.clientWidth})")
        self.assertLessEqual(width["scroll"], width["client"])
        await row.scroll_into_view_if_needed()
        await self.page.screenshot(path=str(APP_DIR / "output" / "playwright" / "resource-usage-narrow.png"))
        await row.get_by_role("button", name="撤销指定").click()
        await self.expect(row).to_contain_text("尚未识别用途")
        self.assertEqual(writes[-1], ("DELETE", {"kind": "clips", "name": unknown["name"]}))

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
        await self.new_preset("Anima style")
        old = self.hold("POST", "/api/style-presets")
        await self.page.locator("#stylePresetSave").click()
        await self.entered(old)
        before = self.app[STYLE_PRESETS_KEY].list("anima")["items"][0]
        await self.close_preset_editor()
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
        await self.new_preset("Original")
        old = self.hold("POST", "/api/style-presets")
        await self.page.locator("#stylePresetSave").click()
        await self.entered(old)
        await self.close_preset_editor()
        await self.switch_profile("krea2")
        await self.switch_profile("anima")
        await self.new_preset("New draft name")
        await self.release(old)
        await self.expect(self.page.locator("#stylePresetName")).to_have_value("New draft name")
        await self.expect(self.page.locator("#presetList")).to_contain_text("Original")

    async def test_old_preset_list_cannot_hide_successful_save(self):
        await self.open_presets()
        await self.switch_profile("krea2")
        old = self.hold("GET", "/api/style-presets", profile="anima")
        await self.switch_profile("anima")
        await self.entered(old)
        await self.new_preset("New style")
        await self.page.locator("#stylePresetSave").click()
        await self.expect(self.page.locator("#presetList")).to_contain_text("New style")
        await self.release(old)
        await self.expect(self.page.locator("#presetList")).to_contain_text("New style")

    async def test_cross_profile_update_is_atomic_and_same_profile_edits_work(self):
        await self.open_presets()
        await self.new_preset("Protected")
        await self.page.locator("#stylePresetSave").click()
        await self.expect(self.page.locator("#presetList")).to_contain_text("Protected")
        item = self.app[STYLE_PRESETS_KEY].list("anima")["items"][0]
        path = self.root / "data" / "style_presets.json"
        before = path.read_bytes()
        response = await self.context.request.put(self.origin + "/api/style-presets/" + item["id"],
                                                  data={"favorite": True, "model_profile": "krea2"})
        self.assertEqual(response.status, 400)
        self.assertEqual(path.read_bytes(), before)
        await self.page.locator("[data-preset-rename]").click()
        await self.page.locator("#stylePresetName").fill("Renamed")
        await self.page.locator("#stylePresetSave").click()
        await self.expect(self.page.locator("#presetList")).to_contain_text("Renamed")
        await self.page.locator("[data-preset-update]").click()
        async with self.page.expect_response(lambda response: response.request.method == "PUT" and "/api/style-presets/" in response.url):
            await self.page.locator("#stylePresetSave").click()
        await self.expect(self.page.locator("#presetList")).to_contain_text("Renamed")
        after = self.app[STYLE_PRESETS_KEY].list("anima")["items"][0]
        self.assertEqual(after["settings"]["model_name"], item["settings"]["model_name"])
        self.assertEqual(after["settings"]["clip_name"], item["settings"]["clip_name"])

    async def test_preset_home_six_rows_and_scroll_edge_fades(self):
        await self.open_presets()
        await self.expect(self.page.locator("#presetEmpty")).to_be_visible()
        store = self.app[STYLE_PRESETS_KEY]
        for count in range(1, 8):
            await store.create({"name": f"风格 {count}", "settings": {"pipeline_version": 5}})
            if count not in {1, 6, 7}:
                continue
            await self.page.evaluate("loadStylePresets()")
            await self.expect(self.page.locator(".preset-row")).to_have_count(count)
            metrics = await self.page.locator("#presetList").evaluate("el => ({height: el.clientHeight, total: el.scrollHeight, rows: [...el.children].slice(0, 6).reduce((n,row) => n + row.getBoundingClientRect().height, 0), gap: parseFloat(getComputedStyle(el).rowGap)})")
            preview = metrics["gap"] + 24 if count > 6 else 0
            self.assertAlmostEqual(metrics["height"], metrics["rows"] + (min(count, 6) - 1) * metrics["gap"] + preview, delta=1)
            if count <= 6:
                await self.expect(self.page.locator("#presetScroll")).not_to_have_class(__import__("re").compile("can-scroll"))
        await self.expect(self.page.locator("#presetScroll")).to_have_class(__import__("re").compile("can-scroll-down"))
        await self.page.locator("#presetList").evaluate("el => { el.scrollTop = el.scrollHeight; }")
        await self.expect(self.page.locator("#presetScroll")).to_have_class(__import__("re").compile("can-scroll-up"))
        await self.expect(self.page.locator("#presetScroll")).not_to_have_class(__import__("re").compile("can-scroll-down"))

    async def test_preset_library_cover_groups_tags_and_batch(self):
        await self.new_preset("晨雾")
        for key in ("size", "repair", "img2img", "affixes"):
            await self.expect(self.page.locator("#stylePresetInclude_" + key)).not_to_be_checked()
        image = Image.new("RGB", (960, 720), "#778e9e")
        buffer = io.BytesIO()
        image.save(buffer, "PNG")
        await self.page.locator("#stylePresetCoverFile").set_input_files({"name": "mist.png", "mimeType": "image/png", "buffer": buffer.getvalue()})
        await self.expect(self.page.locator("#stylePresetCoverZoom")).to_be_visible()
        await self.page.locator("#stylePresetCoverZoom").evaluate("el => { el.value = '1.5'; el.dispatchEvent(new Event('input', {bubbles:true})); }")
        crop = await self.page.locator("#stylePresetCropStage").bounding_box()
        await self.page.mouse.move(crop["x"] + crop["width"] / 2, crop["y"] + crop["height"] / 2)
        await self.page.mouse.down()
        await self.page.mouse.move(crop["x"] + crop["width"] / 2 + 30, crop["y"] + crop["height"] / 2 + 15, steps=5)
        await self.page.mouse.up()
        await self.page.locator("#stylePresetSave").click()
        await self.expect(self.page.locator("#presetList")).to_contain_text("晨雾")
        store = self.app[STYLE_PRESETS_KEY]
        first = store.list("anima")["items"][0]
        self.assertTrue(first["cover_id"])
        saved_settings = json.loads(json.dumps(first["settings"]))
        await self.page.locator("#manageStylePresets").click()
        await self.page.get_by_role("button", name="新建收藏分组", exact=True).click()
        await self.page.locator("#stylePresetGroupName").fill("自然光")
        await self.page.locator("#stylePresetOrganizeSave").click()
        await self.expect(self.page.get_by_role("button", name="自然光", exact=True)).to_be_visible()
        await self.page.get_by_role("button", name="查看 晨雾", exact=True).click()
        await self.page.locator("#stylePresetGroups").get_by_label("自然光", exact=True).check()
        await self.page.locator("#stylePresetTags").fill("柔光, 水彩")
        await self.page.locator("#stylePresetSave").click()
        await self.expect(self.page.locator("#stylePresetEditor")).to_be_hidden()
        updated = store.list("anima")["items"][0]
        self.assertEqual(updated["settings"], saved_settings)
        self.assertTrue(updated["favorite"])
        self.assertEqual(updated["tags"], ["柔光", "水彩"])
        await store.create({"name": "工笔", "settings": {"pipeline_version": 5}, "tags": ["水彩"]})
        await self.page.evaluate("loadStylePresets()")
        await self.expect(self.page.locator(".spm-card")).to_have_count(2)
        await self.page.locator("#stylePresetSearch").fill("柔光")
        await self.expect(self.page.locator(".spm-card")).to_have_count(1)
        await self.page.locator("#stylePresetSearch").fill("")
        await self.page.locator("#stylePresetSelect").click()
        await self.page.get_by_role("button", name="选择当前结果", exact=True).click()
        await self.page.locator('[data-batch-action="tags"]').click()
        await self.page.locator("#stylePresetBatchTags").fill("常用")
        await self.page.locator("#stylePresetOrganizeSave").click()
        await self.expect(self.page.locator("#stylePresetOrganize")).to_be_hidden()
        self.assertTrue(all("常用" in item["tags"] for item in store.list("anima")["items"]))
        await self.page.locator("#stylePresetSelect").click()
        await self.page.screenshot(path=str(APP_DIR / "output/playwright/preset-library-desktop.png"))
        await self.page.set_viewport_size({"width": 390, "height": 844})
        await self.page.evaluate("document.documentElement.dataset.theme = 'dark'")
        await self.page.screenshot(path=str(APP_DIR / "output/playwright/preset-library-mobile-dark.png"))
        bounds = await self.page.locator("#stylePresetManager").bounding_box()
        self.assertLessEqual(bounds["width"], 390)
        self.assertGreaterEqual(bounds["x"], 0)
        await self.page.keyboard.press("Escape")
        await self.expect(self.page.locator("#stylePresetManager")).to_be_hidden()

    async def test_preset_apply_retains_current_fields_in_both_profiles_and_workbenches(self):
        for model in ("anima", "krea2"):
            for workbench in ("random", "single"):
                await self.page.locator(f'#{workbench}WorkbenchTab').click()
                await self.switch_profile(model)
                if workbench == "single":
                    await self.page.locator("#single_positive_prompt").fill("current scene stays")
                pool_ids = await self.page.evaluate("() => { draft.fixed.pose = 'inactive pool draft'; return [...kreaPromptPoolSelection]; }")
                current = await self.page.evaluate("readSettings()")
                captured = json.loads(json.dumps(current))
                captured["cfg"] = 7
                captured["width"] = 1024
                captured["single"]["positive_prompt"] = "must never replace current scene"
                captured["single"]["weight_dtype"] = "fp8_e4m3fn"
                captured["single"]["vae_decode"]["tile_size"] = 1024
                item = await self.app[STYLE_PRESETS_KEY].create({"name": f"Portable {model} {workbench}", "model_profile": model, "settings": captured})
                if workbench == "single":
                    await self.page.locator("#singleCategory_more").click()
                await self.open_presets()
                await self.page.evaluate("loadStylePresets()")
                await self.page.locator(f'[data-preset-id="{item["id"]}"] .preset-apply').click()
                await self.page.wait_for_function("readSettings().cfg === 7")
                after = await self.page.evaluate("readSettings()")
                self.assertEqual(await self.page.evaluate("draft.fixed.pose"), "inactive pool draft")
                self.assertEqual(await self.page.evaluate("[...kreaPromptPoolSelection]"), pool_ids)
                for key in ("width", "height", "pipeline_version", "workbench", "generation_mode", "krea_prompt_pool", "upscalers"):
                    self.assertEqual(after[key], current[key], (model, workbench, key))
                for key in ("positive_prompt", "prefix", "suffix", "prompt_processing", "weight_dtype", "vae_encode", "vae_decode", "input_image_id"):
                    self.assertEqual(after["single"][key], current["single"][key], (model, workbench, key))
                if workbench == "single":
                    await self.page.locator("#singleCategory_common").click()

    async def test_preset_polish_motion_dirty_mobile_and_read_only(self):
        await self.page.emulate_media(reduced_motion="no-preference")
        await self.new_preset("尚未保存的柔光")
        await self.page.wait_for_function("document.querySelector('#stylePresetEditor').dataset.motionState === 'open'")
        artifacts = APP_DIR / "output" / "playwright"
        artifacts.mkdir(parents=True, exist_ok=True)
        await self.page.evaluate("document.documentElement.dataset.theme = 'light'")
        await self.page.wait_for_function("() => !document.querySelector('#stylePresetEditor').getAnimations({subtree: true}).some(a => a.playState === 'running' && Number.isFinite(a.effect?.getComputedTiming().iterations))", timeout=5000)
        await self.page.screenshot(path=str(artifacts / "preset-editor-light.png"))
        await self.page.evaluate("document.documentElement.dataset.theme = 'dark'")
        await self.page.wait_for_function("() => !document.querySelector('#stylePresetEditor').getAnimations({subtree: true}).some(a => a.playState === 'running' && Number.isFinite(a.effect?.getComputedTiming().iterations))", timeout=5000)
        await self.page.screenshot(path=str(artifacts / "preset-editor-dark.png"))
        await self.page.keyboard.press("Escape")
        await self.expect(self.page.locator("#stylePresetConfirm")).to_be_visible()
        await self.page.locator("#stylePresetConfirm").get_by_role("button", name="取消", exact=True).click()
        await self.expect(self.page.locator("#stylePresetConfirm")).to_be_hidden()
        await self.expect(self.page.locator("#stylePresetName")).to_have_value("尚未保存的柔光")
        await self.page.keyboard.press("Escape")
        await self.page.locator("#stylePresetConfirmAccept").click()
        await self.expect(self.page.locator("#stylePresetEditor")).to_be_hidden()
        await self.expect(self.page.locator("#savePreset")).to_be_focused()
        self.assertEqual(self.app[STYLE_PRESETS_KEY].list("anima")["count"], 0)

        await self.page.locator("#manageStylePresets").click()
        await self.page.wait_for_function("document.querySelector('#stylePresetManager').dataset.motionState === 'open'")
        reversal = await self.page.evaluate("""async () => {
          const dialog = document.querySelector('#stylePresetManager');
          dialog.querySelector('.spm-heading .spm-icon-button').click();
          await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
          const before = StudioMotion.snapshot(dialog, 'presence');
          void StylePresetManager.open();
          const after = StudioMotion.snapshot(dialog, 'presence');
          return {before: before.value.progress, after: after.value.progress,
                  moving: before.active, target: StudioMotion.target(dialog)};
        }""")
        self.assertTrue(reversal["moving"])
        self.assertLess(reversal["before"], 1)
        self.assertEqual(reversal["before"], reversal["after"], "reopening must start from the presented frame")
        self.assertTrue(reversal["target"])
        await self.page.wait_for_function("document.querySelector('#stylePresetManager').dataset.motionState === 'open'")
        await self.page.keyboard.press("Escape")
        await self.expect(self.page.locator("#stylePresetManager")).to_be_hidden()
        await self.expect(self.page.locator("#manageStylePresets")).to_be_focused()

        await self.page.set_viewport_size({"width": 390, "height": 844})
        await self.page.locator("#manageStylePresets").click()
        await self.page.locator("#stylePresetFiltersToggle").click()
        await self.expect(self.page.locator("#stylePresetFiltersToggle")).to_have_attribute("aria-expanded", "true")
        await self.expect(self.page.locator("#stylePresetFilters")).to_be_visible()
        await self.page.locator('#stylePresetFilters [data-collection="favorite"]').click()
        await self.expect(self.page.locator('#stylePresetFilters [data-collection="favorite"]')).to_be_focused()
        await self.page.locator("#stylePresetFiltersToggle").click()
        await self.expect(self.page.locator("#stylePresetFilters")).to_be_hidden()
        await self.expect(self.page.locator("#stylePresetFiltersToggle")).to_have_attribute("aria-expanded", "false")
        await self.page.keyboard.press("Escape")
        await self.expect(self.page.locator("#stylePresetManager")).to_be_hidden()

        await self.app[STYLE_PRESETS_KEY].create({"name": "只读收藏", "favorite": True, "settings": {"pipeline_version": 5}})
        await self.page.evaluate("""() => {
          config.storage_status.style_presets = {read_only: true, reason: '验收用只读状态'};
          renderStorageStatus();
        }""")
        await self.page.locator("#manageStylePresets").click()
        await self.expect(self.page.locator(".spm-card")).to_have_count(1)
        await self.expect(self.page.locator("#stylePresetNew")).to_be_disabled()
        await self.expect(self.page.locator(".spm-star")).to_be_disabled()
        await self.expect(self.page.locator(".spm-apply")).to_be_enabled()
        await self.expect(self.page.locator("#stylePresetSearch")).to_be_enabled()
        await self.page.locator("#stylePresetSearch").fill("只读")
        await self.expect(self.page.locator(".spm-card")).to_have_count(1)
        await self.expect(self.page.locator(".spm-content > .spm-error")).to_contain_text("只读")
        await self.page.get_by_role("button", name="查看 只读收藏", exact=True).click()
        await self.expect(self.page.locator("#stylePresetSave")).to_be_disabled()
        await self.expect(self.page.locator("#stylePresetCoverFile")).to_be_disabled()

    async def test_preset_polish_repair_version_confirmation_preserves_current_content(self):
        await self.page.locator("#singleWorkbenchTab").click()
        await self.page.locator("#single_positive_prompt").fill("current portrait remains")
        current = await self.page.evaluate("readSettings()")
        self.assertEqual(current["pipeline_version"], 5)
        captured = json.loads(json.dumps(current))
        captured["pipeline_version"] = 2
        captured["cfg"] = 7
        captured["width"] = 1024
        captured["single"]["positive_prompt"] = "old scene must not return"
        captured["single"]["prefix"] = "old prefix"
        item = await self.app[STYLE_PRESETS_KEY].create({"name": "旧版放大修复", "settings": captured, "include": {"repair": True}})
        await self.page.locator("#singleCategory_more").click()
        await self.open_presets()
        await self.page.evaluate("loadStylePresets()")
        apply = self.page.locator(f'[data-preset-id="{item["id"]}"] .preset-apply')
        await apply.click()
        await self.expect(self.page.locator("#stylePresetConfirm")).to_contain_text("v5 切换到 v2")
        await self.page.locator("#stylePresetConfirm").get_by_role("button", name="取消", exact=True).click()
        await self.expect(self.page.locator("#stylePresetConfirm")).to_be_hidden()
        self.assertEqual(await self.page.evaluate("readSettings()"), current)
        await apply.click()
        await self.page.locator("#stylePresetConfirmAccept").click()
        await self.page.wait_for_function("readSettings().pipeline_version === 2")
        after = await self.page.evaluate("readSettings()")
        self.assertEqual(after["cfg"], 7)
        for key in ("width", "height", "workbench", "generation_mode", "krea_prompt_pool"):
            self.assertEqual(after[key], current[key], key)
        for key in ("positive_prompt", "prefix", "suffix", "prompt_processing", "weight_dtype", "vae_encode", "vae_decode", "input_image_id"):
            self.assertEqual(after["single"][key], current["single"][key], key)

    async def test_preset_polish_closed_library_ignores_pending_resolve(self):
        current = await self.page.evaluate("readSettings()")
        item = await self.app[STYLE_PRESETS_KEY].create({"name": "迟到的风格", "settings": {**current, "cfg": 7}})
        await self.open_presets()
        await self.page.locator("#manageStylePresets").click()
        await self.expect(self.page.locator(".spm-card")).to_have_count(1)
        gate = self.hold("POST", f'/api/style-presets/{item["id"]}/resolve')
        await self.page.locator(".spm-apply").click()
        await self.entered(gate)
        await self.page.locator("#stylePresetManager .spm-heading .spm-icon-button").click()
        await self.expect(self.page.locator("#stylePresetManager")).to_be_hidden()
        await self.page.locator("#manageStylePresets").click()
        await self.expect(self.page.locator("#stylePresetManager")).to_be_visible()
        async with self.page.expect_response(lambda response: response.url.endswith(f'/api/style-presets/{item["id"]}/resolve')):
            await self.release(gate)
        await self.page.evaluate("() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))")
        self.assertEqual(await self.page.evaluate("readSettings()"), current)
        await self.expect(self.page.locator("#stylePresetManager")).to_be_visible()

    async def test_preset_polish_old_batch_failure_does_not_unlock_new_profile(self):
        for model in ("anima", "krea2"):
            await self.app[STYLE_PRESETS_KEY].create({"name": "Batch " + model, "model_profile": model, "settings": {"model_profile": model, "pipeline_version": 5}})
        await self.open_presets()
        await self.page.locator("#manageStylePresets").click()
        await self.expect(self.page.locator(".spm-card")).to_have_count(1)
        await self.page.locator("#stylePresetSelect").click()
        await self.page.get_by_role("button", name="选择当前结果", exact=True).click()
        old = self.hold("POST", "/api/style-presets/batch", error=True)
        await self.page.locator('[data-batch-action="favorite"]').click()
        await self.entered(old)
        await self.page.locator("#stylePresetManager .spm-heading .spm-icon-button").click()
        await self.expect(self.page.locator("#stylePresetManager")).to_be_hidden()
        await self.switch_profile("krea2")
        await self.page.locator("#manageStylePresets").click()
        await self.expect(self.page.locator(".spm-card")).to_have_count(1)
        await self.page.locator("#stylePresetSelect").click()
        await self.page.get_by_role("button", name="选择当前结果", exact=True).click()
        newer = self.hold("POST", "/api/style-presets/batch")
        await self.page.locator('[data-batch-action="favorite"]').click()
        await self.entered(newer)
        await self.release(old)
        await self.expect(self.page.locator('[data-batch-action="favorite"]')).to_be_disabled()
        await self.expect(self.page.locator(".spm-content > .spm-error")).to_be_hidden()
        await self.release(newer)
        await self.expect(self.page.locator('[data-batch-action="favorite"]')).to_be_enabled()
        self.assertTrue(self.app[STYLE_PRESETS_KEY].list("krea2")["items"][0]["favorite"])
