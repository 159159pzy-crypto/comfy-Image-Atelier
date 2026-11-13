"""LoRA feedback/cache races with temporary stores and gated local responses."""
from __future__ import annotations

import asyncio
import copy
from datetime import datetime, timedelta, timezone
import importlib
import io
import json
import os
from pathlib import Path
import sys
import unittest
from urllib.parse import parse_qs, urlparse

from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parent))
fixture = importlib.import_module("test_workbench_races")
ARTIFACTS = fixture.APP_DIR / "output" / "playwright" / "lora-responsiveness"


@unittest.skipUnless(os.environ.get("ANIMA_BROWSER_TESTS") == "1", "opt in to Chromium acceptance")
class LoraResponsivenessTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        ARTIFACTS.mkdir(parents=True, exist_ok=True)
        self.library_gates = []
        self.network = []
        self.metrics = {}
        self.items = [{"id": name, "filename": f"{name}.safetensors", "display_name": name,
                       "preview": f"/api/lora-library/items/{name}/preview", "has_preview": True,
                       "available": True, "writable": True, "model_profile": profile,
                       "base_model": "Anima" if profile == "anima" else "Krea 2",
                       "classification_status": "recognized", "favorite": False,
                       "tags": [], "groupIds": [], "bindings": [], "trigger_words": [],
                       "default_strength": 1, "notes": ""}
                      for name, profile in [("alpha", "anima"), ("beta", "anima"), ("krea", "krea2")]]
        buffer = io.BytesIO()
        Image.new("RGB", (48, 64), "#84a3bd").save(buffer, format="WEBP")
        self.webp = buffer.getvalue()
        await fixture.WorkbenchRaceTests.asyncSetUp(self)
        self.page.on("request", lambda request: self.network.append((request.method, request.url)))
        await self.page.route("**/api/lora-library**", self.serve_library)
        self.clock_time = datetime.now(timezone.utc)
        await self.page.clock.set_fixed_time(self.clock_time)
        # The controller captures Date.now when initialized. Install the browser
        # clock before that capture rather than replacing application functions.
        await self.page.reload()
        await self.page.wait_for_function("initialized && stylePresetProfile === 'anima'")

    async def asyncTearDown(self):
        for gate in self.library_gates:
            gate.release.set()
        if hasattr(self, "page") and not self.page.is_closed():
            await self.page.screenshot(path=str(ARTIFACTS / f"{self._testMethodName}.png"))
        if hasattr(self, "context"):
            await self.context.tracing.stop(path=str(ARTIFACTS / f"{self._testMethodName}.zip"))
        if self.metrics:
            (ARTIFACTS / f"{self._testMethodName}.json").write_text(
                json.dumps(self.metrics, indent=2), encoding="utf-8")
        self.assertEqual(self.comfy.submissions, [])
        self.assertNotIn(("POST", "/api/batches"), self.requests)
        self.assertEqual(self.page_errors, [])

    def hold_library(self, method, path, *, error=False, partial=False):
        gate = fixture.ResponseGate(method, path, error=error)
        gate.partial = partial
        self.library_gates.append(gate)
        return gate

    async def enter(self, gate):
        await asyncio.wait_for(gate.entered.wait(), 10)

    async def release_library(self, gate):
        gate.release.set()
        await asyncio.wait_for(gate.finished.wait(), 10)
        await self.settle()

    async def serve_library(self, route):
        request = route.request
        parsed = urlparse(request.url)
        path, query = parsed.path, parse_qs(parsed.query)
        if path.endswith("/preview"):
            await route.fulfill(body=self.webp, content_type="image/webp",
                                headers={"Cache-Control": "private, max-age=3600"})
            return
        gate = next((gate for gate in self.library_gates
                     if not gate.claimed and gate.method == request.method and gate.path == path), None)
        if gate:
            gate.claimed = True
        item = next((item for item in self.items if path == f"/api/lora-library/items/{item['id']}"), None)
        if path == "/api/lora-library":
            profile = query.get("model_profile", ["anima"])[0]
            items = [item for item in self.items if profile == "all" or item["model_profile"] == profile]
            if query.get("collection"):
                items = [item for item in items if item["favorite"]]
            page = int(query.get("page", ["1"])[0])
            limit = int(query.get("limit", ["60"])[0])
            payload = {"items": copy.deepcopy(items[(page - 1) * limit:page * limit]),
                       "total": len(items), "page": page, "limit": limit,
                       "total_pages": (len(items) + limit - 1) // limit, "groups": [], "facets": {},
                       "revision": 1, "offline": False, "cached": False, "writable": True}
        elif item is not None:
            payload = copy.deepcopy(item)
        else:
            await route.fallback()
            return
        if gate:
            gate.entered.set()
            await gate.release.wait()
        try:
            if gate and gate.partial:
                item.update(request.post_data_json)
                item["pending_local"] = True
                await route.fulfill(status=503, json={"error": "插件元数据已保存，回读尚待确认",
                    "code": "lora_metadata_saved_local_pending", "id": item["id"],
                    "metadata_saved": True, "local_saved": False, "readback_pending": True,
                    "retryable": True, "can_discard": True})
            elif gate and gate.error:
                await route.fulfill(status=503, json={"error": "fixture save failed"})
            else:
                if item is not None and request.method == "PATCH":
                    item.update(request.post_data_json)
                    payload = copy.deepcopy(item)
                await route.fulfill(json=payload)
        finally:
            if gate:
                gate.finished.set()

    def card(self, name):
        return self.page.locator(f'#lmCatalog [data-lm-card="{name}"]')

    def star(self, name):
        return self.card(name).locator(".lm-card-star")

    def request_count(self, path):
        return sum(urlparse(url).path == path for _, url in self.network)

    async def settle(self):
        await self.page.evaluate("() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))")

    async def open_library(self, *, first=True, count=2):
        if first:
            await self.page.locator(".lora-section > summary").click()
        await self.page.locator("#addLora").click()
        await self.page.locator("#lmExpandCompact").click()
        await self.expect(self.page.locator("#lmExpandedPane")).to_be_visible()
        await self.expect(self.page.locator("#lmCatalog [data-lm-card]")).to_have_count(count)
        await self.expect(self.page.locator("#lmRefresh")).to_be_enabled()

    async def close_library(self):
        await self.page.locator("#lmClose").click()
        await self.expect(self.page.locator("#loraDialog")).not_to_be_visible()

    async def expire_cache(self):
        self.clock_time += timedelta(seconds=31)
        await self.page.clock.set_fixed_time(self.clock_time)

    async def test_pending_favorite_is_immediate_and_does_not_reload_host_or_images(self):
        await self.open_library()
        await self.page.wait_for_function("[...document.querySelectorAll('#lmCatalog img')].every(img => img.complete && img.naturalWidth)")
        host_requests = self.request_count("/api/loras")
        image_requests = self.request_count("/api/lora-library/items/alpha/preview")
        gate = self.hold_library("PATCH", "/api/lora-library/items/alpha")
        await self.star("alpha").evaluate("""star => {
            window.loraFeedbackProbe = {star, image: star.closest('[data-lm-card]').querySelector('img')};
            star.addEventListener('click', () => { loraFeedbackProbe.started = performance.now(); }, {capture:true, once:true});
            const observer = new MutationObserver(() => {
                if (star.getAttribute('aria-pressed') === 'true' && loraFeedbackProbe.started != null) {
                    loraFeedbackProbe.elapsed = performance.now() - loraFeedbackProbe.started;
                    observer.disconnect();
                }
            });
            observer.observe(star, {attributes:true, attributeFilter:['aria-pressed']});
        }""")
        await self.star("alpha").click()
        await self.enter(gate)
        await self.expect(self.star("alpha")).to_have_attribute("aria-pressed", "true")
        await self.expect(self.star("alpha")).to_have_attribute("aria-busy", "true")
        await self.expect(self.star("alpha")).to_be_disabled()
        await self.expect(self.star("beta")).to_be_enabled()
        self.assertFalse(await self.page.locator("#lmCatalog").evaluate("el => !!el.closest('[inert]')"))
        elapsed = await self.page.evaluate("loraFeedbackProbe.elapsed")
        self.metrics["click_to_star_ms"] = elapsed
        self.assertLess(elapsed, 100)
        await self.release_library(gate)
        await self.expect(self.star("alpha")).to_be_enabled()
        self.assertTrue(self.items[0]["favorite"])
        self.assertEqual(self.request_count("/api/loras"), host_requests)
        self.assertEqual(self.request_count("/api/lora-library/items/alpha/preview"), image_requests)
        self.assertTrue(await self.star("alpha").evaluate(
            "star => star === loraFeedbackProbe.star && star.closest('[data-lm-card]').querySelector('img') === loraFeedbackProbe.image"))

    async def test_failed_favorite_rolls_back_and_other_cards_remain_available(self):
        await self.open_library()
        gate = self.hold_library("PATCH", "/api/lora-library/items/alpha", error=True)
        await self.star("alpha").click()
        await self.enter(gate)
        await self.expect(self.star("alpha")).to_have_attribute("aria-pressed", "true")
        await self.expect(self.star("beta")).to_be_enabled()
        await self.release_library(gate)
        await self.expect(self.star("alpha")).to_have_attribute("aria-pressed", "false")
        await self.expect(self.star("alpha")).to_be_enabled()
        await self.expect(self.page.locator("#lmNotice")).to_contain_text("fixture save failed")
        self.assertFalse(self.items[0]["favorite"])

    async def test_cached_reopen_and_expired_background_refresh_keep_cards_usable(self):
        await self.open_library()
        count = self.request_count("/api/lora-library")
        await self.close_library()
        await self.open_library(first=False)
        await self.page.evaluate("window.dispatchEvent(new Event('focus'))")
        await self.settle()
        self.assertEqual(self.request_count("/api/lora-library"), count)
        await self.expire_cache()
        gate = self.hold_library("GET", "/api/lora-library")
        await self.page.evaluate("window.dispatchEvent(new Event('focus'))")
        await self.enter(gate)
        await self.expect(self.star("alpha")).to_be_enabled()
        self.assertFalse(await self.page.locator("#lmCatalog").evaluate("el => !!el.closest('[inert]')"))
        await self.release_library(gate)
        async with self.page.expect_request(lambda request: urlparse(request.url).path == "/api/lora-library"
                                            and parse_qs(urlparse(request.url).query).get("refresh") == ["true"]):
            await self.page.locator("#lmRefresh").click()
        await self.expect(self.page.locator("#lmRefresh")).to_be_enabled()

    async def test_confirmed_save_with_pending_readback_keeps_star_and_blocks_repeat_write(self):
        await self.open_library()
        gate = self.hold_library("PATCH", "/api/lora-library/items/alpha", partial=True)
        await self.star("alpha").click()
        await self.enter(gate)
        await self.release_library(gate)
        await self.expect(self.star("alpha")).to_have_attribute("aria-pressed", "true")
        await self.expect(self.star("alpha")).to_be_disabled()
        self.assertTrue(self.items[0]["favorite"])
        self.assertEqual(sum(method == "PATCH" and urlparse(url).path == "/api/lora-library/items/alpha"
                             for method, url in self.network), 1)

    async def test_old_list_and_detail_do_not_revert_a_saved_favorite(self):
        await self.open_library()
        await self.expire_cache()
        listing = self.hold_library("GET", "/api/lora-library")
        await self.page.evaluate("window.dispatchEvent(new Event('focus'))")
        await self.enter(listing)
        detail = self.hold_library("GET", "/api/lora-library/items/alpha")
        await self.card("alpha").locator(".lm-card-open").click()
        await self.enter(detail)
        detail_star = self.page.locator('#lmDetail [data-lm-star="alpha"]')
        await detail_star.click()
        await self.expect(detail_star).to_have_attribute("aria-pressed", "true")
        await self.expect(detail_star).to_be_enabled()
        self.assertTrue(self.items[0]["favorite"])
        await self.release_library(detail)
        await self.release_library(listing)
        await self.expect(detail_star).to_have_attribute("aria-pressed", "true")
        await self.expect(self.star("alpha")).to_have_attribute("aria-pressed", "true")

    async def test_collection_removal_waits_for_save_and_failure_keeps_the_card(self):
        self.items[0]["favorite"] = True
        await self.open_library()
        await self.page.locator('[data-lm-collection="__all__"]').click()
        await self.expect(self.page.locator("#lmCatalog [data-lm-card]")).to_have_count(1)
        failure = self.hold_library("PATCH", "/api/lora-library/items/alpha", error=True)
        await self.star("alpha").click()
        await self.enter(failure)
        await self.expect(self.card("alpha")).to_be_visible()
        await self.expect(self.star("alpha")).to_have_attribute("aria-pressed", "false")
        await self.release_library(failure)
        await self.expect(self.star("alpha")).to_have_attribute("aria-pressed", "true")
        success = self.hold_library("PATCH", "/api/lora-library/items/alpha")
        await self.star("alpha").click()
        await self.enter(success)
        await self.expect(self.card("alpha")).to_be_visible()
        await self.release_library(success)
        await self.expect(self.page.locator("#lmCatalog [data-lm-card]")).to_have_count(0)
        await self.expect(self.page.locator("#lmPage")).to_have_text("1 / 1")
        await self.expect(self.page.locator("#lmEmptyMessage")).to_be_visible()

    async def test_closed_profile_does_not_receive_late_save_ui(self):
        await self.open_library()
        gate = self.hold_library("PATCH", "/api/lora-library/items/alpha")
        await self.star("alpha").click()
        await self.enter(gate)
        await self.close_library()
        await self.page.locator("#model_profile").select_option("krea2")
        await self.page.wait_for_function("currentProfile() === 'krea2' && stylePresetProfile === 'krea2'")
        await self.open_library(first=False, count=1)
        await self.expect(self.card("krea")).to_be_visible()
        await self.release_library(gate)
        await self.expect(self.card("alpha")).to_have_count(0)
        await self.expect(self.star("krea")).to_have_attribute("aria-pressed", "false")
        await self.expect(self.page.locator("#lmDetail")).not_to_be_visible()
        self.assertTrue(self.items[0]["favorite"])
        await self.close_library()
        await self.page.locator("#model_profile").select_option("anima")
        await self.page.wait_for_function("currentProfile() === 'anima' && stylePresetProfile === 'anima'")
        await self.open_library(first=False)
        await self.expect(self.star("alpha")).to_have_attribute("aria-pressed", "true")

    async def test_removing_last_favorite_on_second_page_returns_to_filled_first_page(self):
        template = self.items[0]
        self.items = [{**copy.deepcopy(template), "id": f"item{index:02d}",
                       "filename": f"item{index:02d}.safetensors", "display_name": f"item{index:02d}",
                       "preview": f"/api/lora-library/items/item{index:02d}/preview", "favorite": True}
                      for index in range(61)]
        await self.open_library(count=60)
        await self.page.locator('[data-lm-collection="__all__"]').click()
        await self.expect(self.page.locator("#lmPage")).to_have_text("1 / 2")
        await self.page.locator("#lmNext").click()
        await self.expect(self.page.locator("#lmPage")).to_have_text("2 / 2")
        await self.expect(self.page.locator("#lmCatalog [data-lm-card]")).to_have_count(1)
        gate = self.hold_library("PATCH", "/api/lora-library/items/item60")
        await self.star("item60").click()
        await self.enter(gate)
        await self.expect(self.card("item60")).to_be_visible()
        await self.release_library(gate)
        await self.expect(self.page.locator("#lmPage")).to_have_text("1 / 1")
        await self.expect(self.page.locator("#lmCatalog [data-lm-card]")).to_have_count(60)
        await self.expect(self.card("item60")).to_have_count(0)

    async def test_all_profiles_view_rolls_back_same_item_after_profile_switch(self):
        await self.open_library()
        await self.page.locator("#lmFiltersToggle").click()
        await self.page.locator("#lmProfile").select_option("all")
        await self.expect(self.page.locator("#lmCatalog [data-lm-card]")).to_have_count(3)
        await self.page.locator("#lmFiltersToggle").click()
        gate = self.hold_library("PATCH", "/api/lora-library/items/alpha", error=True)
        await self.star("alpha").click()
        await self.enter(gate)
        await self.close_library()
        await self.page.locator("#model_profile").select_option("krea2")
        await self.page.wait_for_function("currentProfile() === 'krea2' && stylePresetProfile === 'krea2'")
        await self.open_library(first=False, count=3)
        await self.expect(self.star("alpha")).to_have_attribute("aria-pressed", "true")
        await self.release_library(gate)
        await self.expect(self.star("alpha")).to_have_attribute("aria-pressed", "false")
        await self.expect(self.star("alpha")).to_be_enabled()
        await self.expect(self.card("alpha").locator(".lm-card-add")).to_be_disabled()
        await self.expect(self.card("krea").locator(".lm-card-add")).to_be_enabled()
        self.assertEqual(await self.page.evaluate("currentProfile()"), "krea2")

    async def test_pending_readback_preserves_detail_draft_and_exposes_recovery(self):
        await self.open_library()
        await self.card("alpha").locator(".lm-card-open").click()
        await self.expect(self.page.locator("#lmDetailTitle")).to_have_text("alpha")
        await self.page.locator("#lmEditDetail").click()
        notes = self.page.locator('#lmMetadataForm [name="notes"]')
        await notes.fill("Unsaved local note")
        gate = self.hold_library("PATCH", "/api/lora-library/items/alpha", partial=True)
        await self.star("alpha").click()
        await self.enter(gate)
        await self.release_library(gate)
        await self.expect(notes).to_have_value("Unsaved local note")
        await self.expect(self.page.locator('#lmMetadataForm [type="submit"]')).to_be_disabled()
        await self.expect(self.page.locator('[data-lm-retry-local="alpha"]')).to_be_visible()
        await self.expect(self.star("alpha")).to_have_attribute("aria-pressed", "true")
        self.assertEqual(sum(method == "PATCH" and urlparse(url).path == "/api/lora-library/items/alpha"
                             for method, url in self.network), 1)
