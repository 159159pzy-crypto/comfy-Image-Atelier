"""Batch deletion acceptance using temporary history, originals and a network-free Comfy fake."""
from __future__ import annotations

import importlib
import io
import os
from pathlib import Path
import re
import sys
import unittest
from unittest.mock import patch

from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parent))
fixture = importlib.import_module("test_workbench_races")
from anima_webui.server import HISTORY_KEY  # noqa: E402

ARTIFACTS = fixture.APP_DIR / "output" / "playwright" / "artwork-batch-delete"


@unittest.skipUnless(os.environ.get("ANIMA_BROWSER_TESTS") == "1", "opt in to Chromium acceptance")
class ArtworkBatchDeleteBrowserTests(unittest.IsolatedAsyncioTestCase):
    hold = fixture.WorkbenchRaceTests.hold
    entered = fixture.WorkbenchRaceTests.entered
    release = fixture.WorkbenchRaceTests.release

    async def asyncSetUp(self):
        ARTIFACTS.mkdir(parents=True, exist_ok=True)
        await fixture.WorkbenchRaceTests.asyncSetUp(self)
        self.history = self.app[HISTORY_KEY]
        self.originals = self.root / "originals"
        self.originals.mkdir()
        # Only ownership discovery is stubbed. The real deletion service deletes
        # actual fixture files and commits the temporary SQLite store.
        for current in (
            patch("anima_webui.artwork_deletion.image_location.IS_WINDOWS", True),
            patch("anima_webui.artwork_deletion.image_location.verify_local_comfy"),
            patch("anima_webui.artwork_deletion.image_location.comfy_image_directory", return_value=self.originals),
        ):
            current.start()
            self.addCleanup(current.stop)
        output = io.BytesIO()
        with Image.new("RGB", (160, 240), "#91a5b8") as image:
            image.save(output, format="PNG")
        self.png = output.getvalue()
        await self.page.route(re.compile(r"/api/images/\d+(?:/thumbnail)?(?:\?|$)"),
                              lambda route: route.fulfill(body=self.png, content_type="image/png"))
        self.delete_requests = []
        self.page.on("request", lambda request: self.delete_requests.append(request.post_data_json)
                     if request.method == "POST" and request.url.endswith("/api/history/delete") else None)

    async def asyncTearDown(self):
        for gate in self.gates:
            gate.release.set()
        if hasattr(self, "page") and not self.page.is_closed():
            await self.page.screenshot(path=str(ARTIFACTS / f"{self._testMethodName}.png"))
        if hasattr(self, "context"):
            await self.context.tracing.stop(path=str(ARTIFACTS / f"{self._testMethodName}.zip"))
        self.assertEqual(self.comfy.submissions, [])
        self.assertNotIn(("POST", "/api/batches"), self.requests)
        self.assertEqual(self.page_errors, [])

    async def seed(self, count, *, shared=False):
        await self.history.create_batch("batch-delete", count, {"model_profile": "anima"})
        self.records = []
        for index in range(1, count + 1):
            filename = "shared.png" if shared and index <= 2 else f"作品-{index:03d}.png"
            (self.originals / filename).write_bytes(self.png)
            record = await self.history.add_image(
                batch_id="batch-delete", sequence=index, prompt_id=f"browser-{index}",
                image={"filename": filename}, positive_prompt="browser fixture", negative_prompt="",
                sample_seed=index, prompt_seed=index,
                settings={"workbench": "single" if index % 2 == 0 else "random", "model_profile": "anima"},
            )
            self.records.append(record)
        self.folder = (await self.history.create_artwork_folder("验收收藏"))["folder"]["id"]
        await self.history.update_artwork_memberships([item["id"] for item in self.records], [self.folder])
        await self.page.evaluate("async () => { await artworkLoadFolders(); await loadHistory(1); }")
        await self.expect(self.page.locator("#gallery .image-card")).to_have_count(min(24, count))

    def card(self, image_id):
        return self.page.locator(f'#gallery .image-card[data-record-id="{image_id}"] .image-card-open')

    async def selected(self):
        return await self.page.evaluate("[...artworkFavoritesState.selected.keys()].map(Number)")

    async def check_count(self, count):
        await self.expect(self.page.locator(f'#artworkFolderStrip [data-folder-id="{self.folder}"]')).to_have_text(f"验收收藏 {count}")
        self.assertEqual((await self.history.list_artwork_folders())["folders"][0]["count"], count)

    async def test_record_delete_across_pages_and_sources_cancel_busy_and_stale_read(self):
        await self.seed(26)
        await self.page.locator("#artworkSelect").click()
        await self.expect(self.page.locator("#artworkBatchDelete")).to_be_disabled()
        await self.card(26).click()
        await self.page.locator("#nextPage").click()
        await self.card(1).click()
        await self.page.locator('[data-history-source="single"]').click()
        await self.card(24).click()
        self.assertEqual(await self.selected(), [26, 1, 24])
        await self.page.locator("#artworkBatchDelete").click()
        await self.expect(self.page.locator("#historyDeleteSummary")).to_contain_text("3")
        await self.expect(self.page.locator("#historyDeleteSummary")).to_contain_text("1")
        await self.expect(self.page.locator("#cancelHistoryDelete")).to_be_focused()
        await self.page.keyboard.press("Escape")
        await self.expect(self.page.locator("#historyDeleteDialog")).to_be_hidden()
        self.assertEqual(await self.selected(), [26, 1, 24])
        self.assertEqual(self.delete_requests, [])
        await self.expect(self.page.locator("#artworkBatchDelete")).to_be_focused()

        stale = self.hold("GET", "/api/history")
        await self.page.evaluate("void loadHistory(1)")
        await self.entered(stale)
        pending = self.hold("POST", "/api/history/delete")
        await self.page.locator("#artworkBatchDelete").click()
        await self.page.locator("#historyDeleteRecord").click()
        await self.entered(pending)
        await self.expect(self.page.locator("#historyDeleteRecord")).to_be_disabled()
        await self.expect(self.page.locator("#historyDeleteFile")).to_be_disabled()
        await self.page.keyboard.press("Escape")
        await self.expect(self.page.locator("#historyDeleteDialog")).to_be_visible()
        await self.release(pending)
        await self.expect(self.page.locator("#historyDeleteDialog")).to_be_hidden()
        await self.release(stale)
        await self.expect(self.page.locator("#gallery .image-card")).to_have_count(11)
        self.assertEqual(self.delete_requests, [{"image_ids": [26, 1, 24], "mode": "record"}])
        self.assertEqual(await self.selected(), [])
        await self.expect(self.page.locator("#artworkSelect")).to_have_text("选择")
        await self.check_count(23)
        self.assertEqual((await self.history.list_images())["total"], 23)
        self.assertTrue(all((self.originals / self.records[index - 1]["filename"]).exists() for index in (26, 1, 24)))
        self.assertFalse(await self.page.evaluate("[...artworkFavoritesState.records.keys()].some(id=>['26','1','24'].includes(id))"))

    async def test_narrow_light_and_dark_file_delete_and_keyboard_cancel(self):
        await self.seed(2)
        await self.page.set_viewport_size({"width": 390, "height": 844})
        await self.page.locator('button[data-mobile-view="gallery"]').click()
        await self.page.locator("#artworkSelect").click()
        await self.card(2).click()
        await self.card(1).click()
        for theme in ("light", "dark"):
            await self.page.evaluate("theme => applyTheme(theme)", theme)
            await self.page.locator("#artworkBatchDelete").click()
            await self.expect(self.page.locator("#cancelHistoryDelete")).to_be_focused()
            await self.page.keyboard.press("Tab")
            await self.expect(self.page.locator("#closeHistoryDelete")).to_be_focused()
            await self.page.keyboard.press("Shift+Tab")
            await self.expect(self.page.locator("#cancelHistoryDelete")).to_be_focused()
            await self.page.screenshot(path=str(ARTIFACTS / f"batch-delete-{theme}-390.png"))
            bounds = await self.page.locator("#historyDeleteDialog").evaluate("el=>({scroll:el.scrollWidth,width:el.clientWidth})")
            self.assertLessEqual(bounds["scroll"], bounds["width"] + 1)
            await self.page.locator("#cancelHistoryDelete").click()
            await self.expect(self.page.locator("#historyDeleteDialog")).to_be_hidden()
            toolbar = await self.page.locator("#artworkToolbar").evaluate("el=>({scroll:el.scrollWidth,width:el.clientWidth})")
            self.assertLessEqual(toolbar["scroll"], toolbar["width"] + 1)
        await self.page.locator("#artworkBatchDelete").click()
        await self.page.locator("#historyDeleteFile").click()
        await self.expect(self.page.locator("#historyDeleteDialog")).to_be_hidden()
        await self.expect(self.page.locator("#gallery .image-card")).to_have_count(0)
        await self.check_count(0)
        self.assertEqual(list(self.originals.iterdir()), [])
        self.assertEqual(self.delete_requests, [{"image_ids": [2, 1], "mode": "file"}])

    async def test_partial_shared_file_failure_retries_only_failed_then_single_delete(self):
        await self.seed(3, shared=True)
        await self.page.locator("#artworkSelect").click()
        await self.card(3).click()
        await self.card(1).click()
        await self.page.locator("#artworkBatchDelete").click()
        await self.page.locator("#historyDeleteFile").click()
        await self.expect(self.page.locator("#historyDeleteResults")).to_contain_text("shared.png")
        await self.expect(self.page.locator("#historyDeleteResults")).to_contain_text("引用")
        await self.expect(self.page.locator("#historyDeleteRecord")).to_be_enabled()
        self.assertEqual(await self.selected(), [1])
        self.assertFalse((self.originals / "作品-003.png").exists())
        self.assertTrue((self.originals / "shared.png").exists())
        await self.check_count(2)
        await self.page.screenshot(path=str(ARTIFACTS / "partial-file-failure.png"))
        await self.page.locator("#historyDeleteRecord").click()
        await self.expect(self.page.locator("#historyDeleteDialog")).to_be_hidden()
        await self.expect(self.page.locator("#gallery .image-card")).to_have_count(1)
        await self.check_count(1)
        self.assertEqual(self.delete_requests, [{"image_ids": [3, 1], "mode": "file"}, {"image_ids": [1], "mode": "record"}])
        self.assertTrue((self.originals / "shared.png").exists())
        await self.card(2).click()
        await self.page.locator('[data-detail-action="more"]').click()
        await self.page.locator("#deleteRecord").click()
        await self.expect(self.page.locator("#historyDeleteTitle")).to_have_text("删除作品")
        await self.page.locator("#historyDeleteRecord").click()
        await self.expect(self.page.locator("#historyDeleteDialog")).to_be_hidden()
        await self.expect(self.page.locator("#detailDialog")).to_be_hidden()
        await self.expect(self.page.locator("#gallery .image-card")).to_have_count(0)
        self.assertIn(("DELETE", "/api/history/2"), self.requests)
        self.assertTrue((self.originals / "shared.png").exists())

    async def test_deleting_last_page_returns_to_valid_page_and_batch_favorite_still_works(self):
        await self.seed(26)
        await self.page.locator("#nextPage").click()
        await self.expect(self.page.locator("#pageLabel")).to_have_text("2 / 2")
        await self.page.locator("#artworkSelect").click()
        await self.card(2).click()
        await self.card(1).click()
        await self.page.locator("#artworkBatchDelete").click()
        await self.page.locator("#historyDeleteRecord").click()
        await self.expect(self.page.locator("#historyDeleteDialog")).to_be_hidden()
        await self.expect(self.page.locator("#pageLabel")).to_have_text("1 / 1")
        await self.expect(self.page.locator("#gallery .image-card")).to_have_count(24)
        await self.check_count(24)
        await self.page.locator("#artworkSelect").click()
        await self.card(26).click()
        await self.page.locator(f'#artworkFolderStrip [data-folder-id="{self.folder}"]').click()
        await self.page.locator("#artworkBatchAdd").click()
        await self.expect(self.page.locator("#artworkSelect")).to_have_text("选择")
        self.assertEqual(await self.selected(), [])
        await self.check_count(24)


if __name__ == "__main__":
    unittest.main()
