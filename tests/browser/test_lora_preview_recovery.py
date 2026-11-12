"""Real image lifecycle regression checks with temporary data and local fixtures."""
from __future__ import annotations

import asyncio
import importlib
import io
import os
from pathlib import Path
import sys
import unittest
from urllib.parse import parse_qs, urlparse

from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parent))
fixture = importlib.import_module("test_workbench_races")
ARTIFACTS = fixture.APP_DIR / "output" / "playwright" / "lora-preview-recovery"


@unittest.skipUnless(os.environ.get("ANIMA_BROWSER_TESTS") == "1", "opt in to Chromium acceptance")
class LoraPreviewRecoveryTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.preview_gates = {}
        self.inventory_gate = None
        self.inventory_entered = asyncio.Event()
        self.preview_requests = {name: [] for name in ("healthy", "recover", "broken")}
        self.request_changed = asyncio.Condition()
        self.list_status = 200
        self.list_offline = False
        self.items = [{"id": name, "filename": f"{name}.safetensors", "display_name": name,
                       "preview": f"/api/lora-library/items/{name}/preview?original=kept",
                       "available": True, "model_profile": "anima", "base_model": "Anima",
                       "classification_status": "recognized", "favorite": False,
                       "tags": [], "groupIds": [], "bindings": [], "trigger_words": []}
                      for name in self.preview_requests]
        buffer = io.BytesIO()
        Image.new("RGB", (48, 64), "#84a3bd").save(buffer, format="WEBP")
        self.webp = buffer.getvalue()
        ARTIFACTS.mkdir(parents=True, exist_ok=True)
        await fixture.WorkbenchRaceTests.asyncSetUp(self)
        await self.page.route("**/api/lora-library**", self.serve_library)

    async def asyncTearDown(self):
        for gate in self.preview_gates.values():
            gate.set()
        if self.inventory_gate is not None:
            self.inventory_gate.set()
        if hasattr(self, "page") and not self.page.is_closed():
            await self.page.screenshot(path=str(ARTIFACTS / f"{self._testMethodName}.png"))
        if hasattr(self, "context"):
            await self.context.tracing.stop(path=str(ARTIFACTS / f"{self._testMethodName}.zip"))
        self.assertEqual(self.comfy.submissions, [])
        self.assertNotIn(("POST", "/api/batches"), self.requests)
        self.assertEqual(self.page_errors, [])

    async def serve_library(self, route):
        request = route.request
        path = urlparse(request.url).path
        if path == "/api/lora-library":
            gate = self.inventory_gate
            if gate is not None:
                self.inventory_entered.set()
                await gate.wait()
            if self.list_status != 200:
                await route.fulfill(status=self.list_status, json={"error": "fixture offline"})
                return
            await route.fulfill(json={"items": self.items, "total": len(self.items), "page": 1,
                                      "total_pages": 1, "groups": [], "facets": {}, "revision": 1,
                                      "offline": self.list_offline, "cached": self.list_offline,
                                      "writable": not self.list_offline})
            return
        if path.endswith("/preview"):
            name = path.split("/")[-2]
            async with self.request_changed:
                self.preview_requests[name].append(request.url)
                attempt = len(self.preview_requests[name])
                self.request_changed.notify_all()
            if attempt == 1 and name in self.preview_gates:
                await self.preview_gates[name].wait()
            if name == "recover" and attempt == 1:
                await route.fulfill(status=503, json={"error": "preview temporarily unavailable"})
            elif name == "broken":
                # A cacheable response whose image cannot decode must also be retryable.
                await route.fulfill(body=b"broken webp", content_type="image/webp",
                                    headers={"Cache-Control": "private, max-age=3600"})
            else:
                await route.fulfill(body=self.webp, content_type="image/webp",
                                    headers={"Cache-Control": "private, max-age=3600"})
            return
        name = path.rsplit("/", 1)[-1]
        item = next((item for item in self.items if item["id"] == name), None)
        if item is not None and request.method == "PATCH":
            item.update(request.post_data_json)
            await route.fulfill(json=item)
            return
        await route.fallback()

    def card(self, name):
        return self.page.locator(f'#lmCatalog [data-lm-card="{name}"]')

    async def open_library(self):
        await self.page.locator(".lora-section > summary").click()
        await self.page.locator("#addLora").click()
        await self.page.locator("#lmExpandCompact").click()
        await self.expect(self.page.locator("#lmExpandedPane")).to_be_visible()
        await self.expect(self.page.locator("#lmCatalog [data-lm-card]")).to_have_count(3)
        await self.expect(self.page.locator("#lmRefresh")).to_be_enabled()

    async def attempts(self, name, count):
        async with self.request_changed:
            await asyncio.wait_for(self.request_changed.wait_for(
                lambda: len(self.preview_requests[name]) >= count), 10)

    async def decoded(self, name):
        await self.page.wait_for_function("name => { const img = document.querySelector("
                                          "'#lmCatalog [data-lm-card=\"' + name + '\"] img'); "
                                          "return img?.complete && img.naturalWidth > 0; }", arg=name)

    async def settle(self):
        await self.page.evaluate("() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))")

    async def refresh(self):
        async with self.page.expect_response(lambda response: urlparse(response.url).path == "/api/lora-library"):
            await self.page.locator("#lmRefresh").click()
        await self.expect(self.page.locator("#lmRefresh")).to_be_enabled()

    async def test_only_failed_covers_retry_and_successful_nodes_survive(self):
        await self.open_library()
        for name in self.preview_requests:
            await self.attempts(name, 1)
        await self.decoded("healthy")
        await self.expect(self.card("recover").locator("img")).to_have_count(0)
        await self.expect(self.card("broken").locator("img")).to_have_count(0)
        await self.page.evaluate("""() => {
            window.loraPreviewProbe = {
                cards: [...document.querySelectorAll('#lmCatalog [data-lm-card]')],
                healthy: document.querySelector('#lmCatalog [data-lm-card="healthy"] img')
            };
        }""")
        await self.page.locator("#lmSelectToggle").click()
        await self.card("recover").locator(".lm-card-open").click()
        await self.expect(self.page.locator("#lmSelectionCount")).to_contain_text("1")
        await self.card("healthy").locator(".lm-card-star").click()
        await self.expect(self.card("healthy").locator(".lm-card-star")).to_have_attribute("aria-pressed", "true")
        await self.page.locator("#lmListView").click()
        await self.page.locator("#lmGridView").click()
        await self.settle()
        self.assertEqual({name: len(urls) for name, urls in self.preview_requests.items()},
                         {"healthy": 1, "recover": 1, "broken": 1})

        await self.refresh()
        await self.decoded("recover")
        await self.attempts("broken", 2)
        await self.expect(self.card("broken").locator("img")).to_have_count(0)
        await self.settle()
        self.assertEqual({name: len(urls) for name, urls in self.preview_requests.items()},
                         {"healthy": 1, "recover": 2, "broken": 2})
        self.assertTrue(await self.page.evaluate("""() => {
            const current = [...document.querySelectorAll('#lmCatalog [data-lm-card]')];
            return current.every((card, index) => card === loraPreviewProbe.cards[index])
                && document.querySelector('#lmCatalog [data-lm-card="healthy"] img') === loraPreviewProbe.healthy;
        }"""))
        await self.expect(self.page.locator("#lmSelectionCount")).to_contain_text("1")
        await self.refresh()
        await self.attempts("broken", 3)
        await self.expect(self.card("broken").locator("img")).to_have_count(0)
        await self.settle()
        self.assertEqual({name: len(urls) for name, urls in self.preview_requests.items()},
                         {"healthy": 1, "recover": 2, "broken": 3})
        for name in ("recover", "broken"):
            queries = [parse_qs(urlparse(url).query) for url in self.preview_requests[name]]
            self.assertTrue(all(query["original"] == ["kept"] for query in queries))
            self.assertNotIn("_studio_preview_retry", queries[0])
            tokens = [query["_studio_preview_retry"][0] for query in queries[1:]]
            self.assertEqual(len(tokens), len(set(tokens)))

    async def test_late_failure_after_refresh_retries_once_and_old_events_are_ignored(self):
        self.preview_gates["recover"] = asyncio.Event()
        await self.open_library()
        await self.attempts("recover", 1)
        await self.page.evaluate("window.oldLoraPreview = document.querySelector('#lmCatalog [data-lm-card=\"recover\"] img')")
        await self.refresh()
        self.assertEqual(len(self.preview_requests["recover"]), 1)
        self.preview_gates["recover"].set()
        await self.decoded("recover")
        self.assertEqual(len(self.preview_requests["recover"]), 2)
        await self.page.evaluate("""() => {
            oldLoraPreview.dispatchEvent(new Event('error'));
            oldLoraPreview.dispatchEvent(new Event('load'));
        }""")
        await self.settle()
        await self.decoded("recover")
        self.assertEqual(len(self.preview_requests["recover"]), 2)

    async def test_failure_during_refresh_waits_for_current_inventory(self):
        self.preview_gates["recover"] = asyncio.Event()
        await self.open_library()
        await self.attempts("recover", 1)
        self.inventory_gate = asyncio.Event()
        await self.page.locator("#lmRefresh").click()
        await asyncio.wait_for(self.inventory_entered.wait(), 10)
        await self.expect(self.page.locator("#lmRefresh")).to_be_disabled()
        self.preview_gates["recover"].set()
        await self.expect(self.card("recover").locator("img")).to_have_count(0)
        await self.settle()
        self.assertEqual(len(self.preview_requests["recover"]), 1)
        self.inventory_gate.set()
        await self.expect(self.page.locator("#lmRefresh")).to_be_enabled()
        await self.decoded("recover")
        self.assertEqual(len(self.preview_requests["recover"]), 2)

    async def test_failed_and_offline_refresh_do_not_retry_covers(self):
        await self.open_library()
        await self.attempts("recover", 1)
        await self.expect(self.card("recover").locator("img")).to_have_count(0)
        self.list_status = 503
        await self.refresh()
        await self.expect(self.page.locator("#lmNotice")).to_contain_text("离线")
        await self.settle()
        self.assertEqual(len(self.preview_requests["recover"]), 1)
        self.list_status = 200
        self.list_offline = True
        await self.refresh()
        await self.settle()
        self.assertEqual(len(self.preview_requests["recover"]), 1)
        self.list_offline = False
        await self.refresh()
        await self.decoded("recover")
        self.assertEqual(len(self.preview_requests["recover"]), 2)
