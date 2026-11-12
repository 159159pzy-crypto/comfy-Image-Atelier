"""Real Chromium thumbnail scheduling and recovery against isolated image routes."""
from __future__ import annotations

import asyncio
import importlib
import io
import json
import os
from pathlib import Path
import re
import sys
import unittest
from urllib.parse import parse_qs, urlparse

from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parent))
fixture = importlib.import_module("test_workbench_races")
ARTIFACTS = fixture.APP_DIR / "output" / "playwright" / "thumbnail-upgrade-20261113"


@unittest.skipUnless(os.environ.get("ANIMA_BROWSER_TESTS") == "1", "opt in to Chromium acceptance")
class HistoryThumbnailBrowserTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.image_requests = []
        self.original_requests = []
        self.image_active = 0
        self.maximum_active = 0
        self.image_changed = asyncio.Condition()
        self.release_images = asyncio.Event()
        self.release_images.set()
        self.fail_first = set()
        self.corrupt_first = set()
        self.records = [self.record(index) for index in range(1, 73)]
        output = io.BytesIO()
        with Image.new("RGB", (160, 240), "#7a97aa") as image:
            image.save(output, format="WEBP")
        self.webp = output.getvalue()
        ARTIFACTS.mkdir(parents=True, exist_ok=True)
        await fixture.WorkbenchRaceTests.asyncSetUp(self)
        await self.page.route(re.compile(r"/api/history\?"), self.serve_history)
        await self.page.route(re.compile(r"/api/images/\d+(?:/thumbnail)?(?:\?|$)"), self.serve_image)
        await self.page.route("**/api/artwork-folders", lambda route: route.fulfill(json={
            "folders": [{"id": "browser-favorites", "name": "验收收藏", "count": 2}], "total": 2}))

    async def asyncTearDown(self):
        self.release_images.set()
        if hasattr(self, "page") and not self.page.is_closed():
            await self.settle()
            await self.page.screenshot(path=str(ARTIFACTS / f"{self._testMethodName}.png"))
        if hasattr(self, "context"):
            await self.context.tracing.stop(path=str(ARTIFACTS / f"{self._testMethodName}.zip"))
        (ARTIFACTS / f"{self._testMethodName}.json").write_text(json.dumps({
            "image_requests": self.image_requests, "maximum_active": self.maximum_active,
            "original_requests": self.original_requests, "page_errors": getattr(self, "page_errors", []),
            "image_source": "controlled local WebP responses; browser routing disables HTTP cache",
        }, ensure_ascii=False, indent=2), encoding="utf-8")
        self.assertEqual(self.comfy.submissions, [])
        self.assertNotIn(("POST", "/api/batches"), self.requests)
        self.assertEqual(self.original_requests, [], "thumbnail failures must not fall back to original images")
        self.assertEqual(self.page_errors, [])

    @staticmethod
    def record(image_id):
        workbench = "random" if image_id % 2 else "single"
        return {"id": image_id, "batch_id": "thumbnail-browser", "sequence": image_id,
                "filename": f"作品-{image_id:03}.png", "subfolder": "", "file_type": "output",
                "prompt_id": f"browser-{image_id}", "created_at": "2026-11-13T12:00:00+08:00",
                "workbench": workbench, "sample_seed": image_id, "prompt_seed": image_id,
                "positive_prompt": "browser fixture", "negative_prompt": "", "selection": {},
                "settings": {"workbench": workbench},
                "folder_ids": ["browser-favorites"] if image_id in (1, 3) else []}

    async def serve_history(self, route):
        query = parse_qs(urlparse(route.request.url).query)
        page = int(query.get("page", [1])[0])
        limit = int(query.get("limit", [24])[0])
        records = self.records
        if query.get("workbench"):
            records = [record for record in records if record["workbench"] == query["workbench"][0]]
        if query.get("folder_id"):
            records = [record for record in records if query["folder_id"][0] in record["folder_ids"]]
        items = records[(page - 1) * limit:page * limit]
        await route.fulfill(json={"page": page, "pages": max(1, (len(records) + limit - 1) // limit),
                                  "total": len(records), "items": items})

    async def serve_image(self, route):
        request = route.request
        path = urlparse(request.url).path
        if not path.endswith("/thumbnail"):
            self.original_requests.append(path)
            await route.fulfill(status=404, json={"error": "originals are not part of this fixture"})
            return
        image_id = int(path.split("/")[-2])
        async with self.image_changed:
            attempt = 1 + sum(item["id"] == image_id for item in self.image_requests)
            self.image_active += 1
            self.maximum_active = max(self.maximum_active, self.image_active)
            self.image_requests.append({"id": image_id, "attempt": attempt, "url": request.url,
                                        "resource_type": request.resource_type,
                                        "cache_control": request.headers.get("cache-control", "")})
            self.image_changed.notify_all()
        try:
            await self.release_images.wait()
            if attempt == 1 and image_id in self.fail_first:
                await route.fulfill(status=503, json={"error": "temporary thumbnail failure"})
            else:
                body = b"broken WebP" if attempt == 1 and image_id in self.corrupt_first else self.webp
                await route.fulfill(body=body, content_type="image/webp",
                                    headers={"Cache-Control": "private, max-age=3600"})
        finally:
            async with self.image_changed:
                self.image_active -= 1
                self.image_changed.notify_all()

    async def wait_images(self, predicate):
        async with self.image_changed:
            await asyncio.wait_for(self.image_changed.wait_for(predicate), 10)

    async def settle(self):
        await self.page.evaluate("async () => { for(let i=0;i<4;i++) await new Promise(requestAnimationFrame); }")

    def card(self, image_id):
        return self.page.locator(f'#gallery .image-card[data-record-id="{image_id}"]')

    async def show_history(self):
        await self.page.evaluate("() => artworkLoadFolders()")
        await self.page.evaluate("() => loadHistory(1)")
        await self.expect(self.page.locator("#gallery .image-card")).to_have_count(min(24, len(self.records)))
        await self.settle()

    async def decoded(self, image_id):
        await self.expect(self.card(image_id)).to_have_attribute("data-thumbnail-state", "loaded")
        await self.page.wait_for_function("id => {const image=document.querySelector("
                                          "'#gallery .image-card[data-record-id=\"'+id+'\"] img');"
                                          "return image?.complete && image.naturalWidth > 0;}", arg=image_id)

    async def test_visible_cards_lead_nearby_prefetch_and_scroll_loads_distant_cards(self):
        await self.page.set_viewport_size({"width": 1440, "height": 720})
        self.release_images.clear()
        await self.show_history()
        await self.wait_images(lambda: len(self.image_requests) >= 4)
        geometry = await self.page.locator("#gallery .image-card").evaluate_all("""cards => cards.map(card => {
            const box=card.getBoundingClientRect(); return {id:Number(card.dataset.recordId),
              visible:box.bottom>0 && box.top<innerHeight, near:box.bottom>-400 && box.top<innerHeight+400,
              priority:card.querySelector('img').fetchPriority, src:card.querySelector('img').getAttribute('src')};
        })""")
        visible = {card["id"] for card in geometry if card["visible"]}
        first = [item["id"] for item in self.image_requests]
        self.assertTrue(set(first[:min(4, len(visible))]).issubset(visible), (first, visible))
        self.assertTrue(all(card["priority"] == ("high" if card["visible"] else "low")
                            for card in geometry if card["id"] in first))
        distant = [card["id"] for card in geometry if not card["near"]]
        self.assertTrue(distant, "the fixture must include cards beyond the prefetch boundary")
        self.assertTrue(all(not card["src"] for card in geometry if card["id"] in distant))
        self.release_images.set()
        await self.decoded(first[0])
        await self.page.wait_for_function("() => [...document.querySelectorAll('#gallery .image-card')].filter("
                                          "card => {const r=card.getBoundingClientRect(); return r.top<innerHeight+400;})"
                                          ".every(card => card.dataset.thumbnailState==='loaded')")
        self.assertTrue(set(distant).isdisjoint(item["id"] for item in self.image_requests))
        await self.card(24).scroll_into_view_if_needed()
        await self.decoded(24)
        self.assertLessEqual(self.maximum_active, 4)

    async def test_rapid_pages_keep_four_slots_and_hidden_archive_pauses_new_work(self):
        self.release_images.clear()
        await self.show_history()
        await self.wait_images(lambda: len(self.image_requests) >= 4)
        first = [item["id"] for item in self.image_requests]
        await self.page.locator("#nextPage").click()
        await self.expect(self.page.locator("#pageLabel")).to_have_text("2 / 3")
        await self.page.locator("#nextPage").click()
        await self.expect(self.page.locator("#pageLabel")).to_have_text("3 / 3")
        await self.settle()
        self.assertEqual(len(self.image_requests), 4)
        await self.page.locator('[data-workbench="single"]').click()
        await self.expect(self.page.locator("#randomArchive")).to_be_hidden()
        self.release_images.set()
        await self.wait_images(lambda: self.image_active == 0)
        await self.settle()
        self.assertEqual([item["id"] for item in self.image_requests], first)
        self.release_images.clear()
        await self.page.locator('[data-workbench="random"]').click()
        await self.expect(self.page.locator("#randomArchive")).to_be_visible()
        await self.wait_images(lambda: len(self.image_requests) >= 8)
        self.assertTrue(all(item["id"] >= 49 for item in self.image_requests[4:]))
        self.assertEqual(self.maximum_active, 4)
        self.release_images.set()
        await self.decoded(self.image_requests[4]["id"])

    async def test_manual_retry_recovers_errors_and_refresh_preserves_images(self):
        self.records = self.records[:8]
        self.fail_first.add(1)
        self.corrupt_first.add(2)
        await self.show_history()
        for image_id in (1, 2):
            await self.expect(self.card(image_id).locator(".image-retry")).to_be_visible()
            await self.expect(self.card(image_id)).to_have_attribute("data-thumbnail-state", "error")
            await self.expect(self.card(image_id).locator("img")).to_have_css("visibility", "hidden")
        await self.decoded(3)
        await self.page.evaluate("window.thumbnailNodes=[...document.querySelectorAll('#gallery .image-card')].map(card=>({card,image:card.querySelector('img')}))")
        await self.page.screenshot(path=str(ARTIFACTS / "retry-desktop.png"))
        await self.page.set_viewport_size({"width": 390, "height": 844})
        await self.page.locator('[data-mobile-view="gallery"]').click()
        await self.card(1).scroll_into_view_if_needed()
        await self.page.screenshot(path=str(ARTIFACTS / "retry-mobile.png"))
        await self.page.locator("#artworkSelect").click()
        await self.page.locator("#artworkSelect").click()
        await self.page.evaluate("() => loadHistory(1)")
        await self.settle()
        self.assertTrue(await self.page.evaluate("thumbnailNodes.every(({card,image})=>card.isConnected && card.querySelector('img')===image)"))
        self.assertEqual(sum(item["id"] == 1 for item in self.image_requests), 1)
        self.assertEqual(sum(item["id"] == 2 for item in self.image_requests), 1)
        for image_id in (1, 2):
            retry = self.card(image_id).locator(".image-retry")
            await retry.focus()
            await retry.press("Enter")
            await self.expect(self.card(image_id).locator(".image-card-open")).to_be_focused()
            await self.decoded(image_id)
            await self.expect(self.card(image_id).locator(".image-card-open")).to_be_focused()
            await self.expect(self.card(image_id).locator(".image-retry")).to_be_hidden()
            self.assertTrue((await self.card(image_id).locator("img").get_attribute("src")).startswith("blob:"))
            attempts = [item for item in self.image_requests if item["id"] == image_id]
            self.assertEqual(len(attempts), 2)
            self.assertEqual(attempts[0]["url"], attempts[1]["url"])
            self.assertEqual(attempts[1]["resource_type"], "fetch")
        self.assertLessEqual(self.maximum_active, 4)
        await self.expect(self.page.locator("#detailDialog")).to_be_hidden()
        await self.page.locator('[data-history-source="random"]').click()
        await self.expect(self.page.locator("#gallery .image-card")).to_have_count(4)
        self.assertTrue(await self.page.evaluate("thumbnailNodes.filter(({card})=>Number(card.dataset.recordId)%2).every("
                                                  "({card,image})=>card.isConnected && card.querySelector('img')===image)"))
        if await self.page.locator("#artworkFoldersToggle").get_attribute("aria-expanded") != "true":
            await self.page.locator("#artworkFoldersToggle").click()
        await self.page.locator('#artworkFolderStrip [data-folder-id="browser-favorites"]').click()
        await self.expect(self.page.locator("#gallery .image-card")).to_have_count(2)
        await self.expect(self.page.locator("#artworkHistoryTitle")).to_have_text("验收收藏")
        self.assertTrue(await self.page.evaluate("thumbnailNodes.filter(({card})=>['1','3'].includes(card.dataset.recordId)).every("
                                                  "({card,image})=>card.isConnected && card.querySelector('img')===image)"))
        self.assertEqual(sum(item["id"] == 1 for item in self.image_requests), 2)
        self.assertEqual(sum(item["id"] == 3 for item in self.image_requests), 1)


if __name__ == "__main__":
    unittest.main()
