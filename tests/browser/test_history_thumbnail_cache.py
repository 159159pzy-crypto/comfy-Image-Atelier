"""Real Chromium HTTP cache acceptance; deliberately no Playwright routes.

Responses come from an isolated aiohttp app, so memory/disk cache and 304
revalidation remain enabled. The production gallery and loader are unchanged.
"""
from __future__ import annotations

import hashlib
import importlib
import io
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest

from aiohttp import web
from aiohttp.test_utils import TestServer
from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parent))
fixture = importlib.import_module("test_workbench_races")
gallery_fixture = importlib.import_module("test_history_thumbnails")
ARTIFACTS = fixture.APP_DIR / "output" / "playwright" / "thumbnail-recovery-20261114"


def startup_post(path):
    return (path in {"/api/settings/normalize", "/api/prompts/normalize", "/api/resources/resolve"}
            or path.startswith("/api/pools/") and path.endswith("/query")
            or path.startswith("/api/favorites/") and path.endswith("/reconcile"))


@unittest.skipUnless(os.environ.get("ANIMA_BROWSER_TESTS") == "1", "opt in to real HTTP cache acceptance")
class HistoryThumbnailHttpCacheTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="atelier-thumbnail-cache-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.records = [gallery_fixture.HistoryThumbnailBrowserTests.record(index) for index in range(1, 25)]
        self.requests = []
        self.image_requests = []
        self.cache_events = []
        self.page_errors = []
        self.external_requests = []
        self.phase = "cold"
        self.context = None
        image = io.BytesIO()
        with Image.new("RGB", (160, 240), "#8da99c") as source:
            source.save(image, format="WEBP")
        self.webp = image.getvalue()
        self.etag = '"' + hashlib.sha256(self.webp).hexdigest() + '"'
        self.comfy = fixture.BrowserComfy()
        self.app = fixture.create_app(app_dir=fixture.APP_DIR, comfy=self.comfy,
                                      data_dir=self.root / "data",
                                      anima_tools_dir=fixture._write_fake_tools(self.root))

        @web.middleware
        async def serve_fixture(request, handler):
            self.requests.append((request.method, request.path))
            # Startup POSTs normalize drafts, query pools and reconcile favorites
            # in the temporary data directory. Generation endpoints stay blocked.
            initialization = request.method == "POST" and startup_post(request.path)
            if request.method not in {"GET", "HEAD"} and not initialization:
                return web.json_response({"error": "Unsupported browser fixture action"}, status=405)
            if request.path == "/api/history":
                return web.json_response({"page": 1, "pages": 1, "total": len(self.records),
                                          "items": self.records}, headers={"Cache-Control": "no-store"})
            if request.path == "/api/artwork-folders":
                return web.json_response({"folders": [], "total": 0}, headers={"Cache-Control": "no-store"})
            if request.path.startswith("/api/images/"):
                return self.serve_thumbnail(request)
            return await handler(request)

        self.app.middlewares.insert(0, serve_fixture)
        self.server = TestServer(self.app)
        await self.server.start_server()
        self.addAsyncCleanup(self.server.close)
        self.origin = str(self.server.make_url(""))
        api = importlib.import_module("playwright.async_api")
        self.expect = api.expect
        self.playwright = await api.async_playwright().start()
        self.addAsyncCleanup(self.playwright.stop)
        self.addAsyncCleanup(self.close_context)
        ARTIFACTS.mkdir(parents=True, exist_ok=True)
        await self.open_context()

    def serve_thumbnail(self, request):
        if not request.path.endswith("/thumbnail"):
            return web.json_response({"error": "Original-image fallback is forbidden"}, status=404)
        image_id = int(request.path.split("/")[-2])
        attempt = 1 + sum(item["id"] == image_id for item in self.image_requests)
        broken = image_id == 3 and attempt == 1
        headers = {
            "Cache-Control": "private, max-age=0, must-revalidate" if image_id == 2 else "private, max-age=3600",
            "ETag": '"broken-fixture"' if broken else self.etag,
            "Last-Modified": "Fri, 13 Nov 2026 00:00:00 GMT",
        }
        status = 304 if request.headers.get("If-None-Match") == headers["ETag"] else 200
        self.image_requests.append({
            "phase": self.phase, "id": image_id, "attempt": attempt, "status": status,
            "path": request.path_qs, "cache_control": request.headers.get("Cache-Control", ""),
            "if_none_match": request.headers.get("If-None-Match", ""), "broken": broken,
        })
        if status == 304:
            return web.Response(status=304, headers=headers)
        return web.Response(body=b"invalid but cacheable WebP" if broken else self.webp,
                            content_type="image/webp", headers=headers)

    async def open_context(self):
        # Reopening this same profile in a new browser process below exercises
        # disk cache, not merely another tab sharing an in-memory response.
        self.context = await self.playwright.chromium.launch_persistent_context(
            user_data_dir=str(self.root / "browser-profile"),
            channel=os.environ.get("ANIMA_BROWSER_CHANNEL") or None,
            viewport={"width": 1440, "height": 720}, reduced_motion="reduce",
        )
        await self.context.tracing.start(screenshots=True, snapshots=True)
        self.page = self.context.pages[0] if self.context.pages else await self.context.new_page()
        self.page.on("pageerror", lambda error: self.page_errors.append(str(error)))
        self.page.on("request", lambda request: self.external_requests.append(request.url)
                     if not request.url.startswith((self.origin + "/", "blob:", "data:")) else None)
        session = await self.context.new_cdp_session(self.page)
        await session.send("Network.enable")

        def response_received(event):
            response = event.get("response", {})
            if "/api/images/" in response.get("url", ""):
                self.cache_events.append({"phase": self.phase, "url": response["url"],
                                          "status": response["status"],
                                          "from_disk_cache": response.get("fromDiskCache", False)})

        session.on("Network.responseReceived", response_received)
        await self.page.goto(self.origin)
        await self.ready()

    async def ready(self):
        await self.page.wait_for_function("typeof initialized !== 'undefined' && (initialized || !document.getElementById('appError').hidden)")
        await self.expect(self.page.locator("#appError")).to_be_hidden()
        await self.expect(self.page.locator("#gallery .image-card")).to_have_count(24)

    async def scroll_entire_page(self):
        for image_id in range(1, 25):
            card = self.page.locator(f'#gallery .image-card[data-record-id="{image_id}"]')
            await card.scroll_into_view_if_needed()
            await self.expect(card).to_have_attribute("data-thumbnail-state", "loaded")
            await self.expect(card.locator(".image-retry")).to_be_hidden()
        for image_id in (1, 24, 1, 24):
            await self.page.locator(f'#gallery .image-card[data-record-id="{image_id}"]').scroll_into_view_if_needed()
        self.assertTrue(await self.page.locator("#gallery .image-card").evaluate_all("""cards => cards.every(card => {
            const image = card.querySelector('img');
            return card.dataset.thumbnailState === 'loaded' && image.complete
                && image.naturalWidth > 0 && image.naturalHeight > 0;
        })"""))

    async def close_context(self):
        if self.context is not None:
            context, self.context = self.context, None
            await context.close()

    async def asyncTearDown(self):
        if self.context is not None:
            await self.page.screenshot(path=str(ARTIFACTS / "real-http-cache-final.png"))
            await self.context.tracing.stop(path=str(ARTIFACTS / f"real-http-cache-{self.phase}.zip"))
        (ARTIFACTS / "real-http-cache.json").write_text(json.dumps({
            "requests": self.requests, "image_requests": self.image_requests, "cache_events": self.cache_events,
            "page_errors": self.page_errors, "external_requests": self.external_requests,
            "image_source": "real aiohttp responses; no browser route interception; persistent Chromium profile",
        }, ensure_ascii=False, indent=2), encoding="utf-8")
        self.assertEqual(self.comfy.submissions, [])
        self.assertNotIn(("POST", "/api/batches"), self.requests)
        self.assertFalse(any(method == "POST" and not startup_post(path) for method, path in self.requests))
        self.assertFalse(any(path.startswith("/api/images/") and not path.endswith("/thumbnail")
                             for _, path in self.requests))
        self.assertEqual(self.page_errors, [])
        self.assertEqual(self.external_requests, [])

    async def test_memory_disk_revalidation_and_bad_cached_image_recovery(self):
        await self.scroll_entire_page()
        bad_attempts = [item for item in self.image_requests if item["id"] == 3]
        self.assertEqual(len(bad_attempts), 2)
        self.assertTrue(bad_attempts[0]["broken"])
        self.assertFalse(bad_attempts[1]["broken"])
        self.assertEqual(bad_attempts[1]["status"], 200)
        self.assertTrue("no-cache" in bad_attempts[1]["cache_control"]
                        or "max-age=0" in bad_attempts[1]["cache_control"], bad_attempts)
        self.assertEqual(len({item["path"] for item in bad_attempts}), 1)
        first_counts = {image_id: sum(item["id"] == image_id for item in self.image_requests)
                        for image_id in range(1, 25)}

        self.phase = "memory-cache"
        await self.page.reload()
        await self.ready()
        await self.scroll_entire_page()
        for image_id in range(1, 25):
            if image_id != 2:
                self.assertEqual(sum(item["id"] == image_id for item in self.image_requests), first_counts[image_id])
        self.assertTrue(any(item["id"] == 2 and item["status"] == 304
                            and item["phase"] == self.phase for item in self.image_requests))
        await self.context.tracing.stop(path=str(ARTIFACTS / "real-http-cache-memory.zip"))
        await self.close_context()

        self.phase = "disk-cache"
        await self.open_context()
        await self.scroll_entire_page()
        for image_id in range(1, 25):
            if image_id != 2:
                self.assertEqual(sum(item["id"] == image_id for item in self.image_requests), first_counts[image_id])
        self.assertTrue(any(item["id"] == 2 and item["status"] == 304
                            and item["phase"] == self.phase for item in self.image_requests))
        self.assertTrue(any(item["phase"] == "disk-cache" and item["from_disk_cache"]
                            for item in self.cache_events), self.cache_events)


if __name__ == "__main__":
    unittest.main()
