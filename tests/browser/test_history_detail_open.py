"""Chromium acceptance for details that only present the decoded original artwork."""
import importlib
import io
import os
from pathlib import Path
import sys
import unittest

from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parent))
fixture = importlib.import_module("test_workbench_races")
from anima_webui.server import HISTORY_KEY  # noqa: E402


def solid_png(size, color):
    with Image.new("RGB", size, color) as image, io.BytesIO() as stream:
        image.save(stream, format="PNG")
        return stream.getvalue()


@unittest.skipUnless(os.environ.get("ANIMA_BROWSER_TESTS") == "1", "opt in to Chromium acceptance")
class HistoryDetailOpenTests(unittest.IsolatedAsyncioTestCase):
    hold = fixture.WorkbenchRaceTests.hold
    entered = fixture.WorkbenchRaceTests.entered
    release = fixture.WorkbenchRaceTests.release
    asyncTearDown = fixture.WorkbenchRaceTests.asyncTearDown

    async def asyncSetUp(self):
        await fixture.WorkbenchRaceTests.asyncSetUp(self)
        history = self.app[HISTORY_KEY]
        await history.create_batch("detail-open", 2, {"model_profile": "anima"})
        self.colors = {"random": (38, 135, 186), "single": (166, 84, 47)}
        self.sizes = {"random": (1024, 1280), "single": (960, 1440)}
        self.thumb_color = (231, 62, 179)
        thumbnail = solid_png((160, 240), self.thumb_color)
        self.paths, self.cards, originals = {}, {}, {}
        for sequence, (workbench, color) in enumerate(self.colors.items(), start=1):
            filename = f"{workbench}.png"
            originals[filename] = solid_png(self.sizes[workbench], color)
            record = await history.add_image(
                batch_id="detail-open", sequence=sequence, prompt_id=f"browser-{workbench}",
                image={"filename": filename}, sample_seed=sequence, prompt_seed=sequence,
                positive_prompt="browser fixture", negative_prompt="",
                settings={"workbench": workbench, "model_profile": "anima"},
            )
            self.paths[workbench] = f"/api/images/{record['id']}"
            self.cards[workbench] = self.page.locator(f'#gallery .image-card[data-record-id="{record["id"]}"]')

        async def image_bytes(record, *, max_bytes=None):
            # A distinct swatch in the bounded thumbnail read makes an enlarged
            # thumbnail distinguishable from the actual original in screenshots.
            return (thumbnail if max_bytes is not None else originals[record["filename"]]), "image/png"

        self.comfy.image_bytes = image_bytes
        self.dialog = self.page.locator("#detailDialog")
        self.image = self.dialog.locator("#detailImage")
        self.status = self.dialog.locator("#detailImageStatus")

    async def show_gallery(self):
        await self.page.evaluate("() => loadHistory(1)")

    async def open_card(self, workbench):
        card = self.cards[workbench]
        await card.scroll_into_view_if_needed()
        await self.expect(card).to_have_attribute("data-thumbnail-state", "loaded")
        await card.locator(".image-card-open").click()
        await self.expect(self.dialog).to_be_visible()

    async def observe_frames(self, opener):
        await opener.evaluate("""button => {
            button.addEventListener("click", () => {
                const dialog = document.getElementById("detailDialog"), figure = dialog.querySelector("figure");
                window.detailOnClick = {open: dialog.open, meta: document.getElementById("detailMeta").textContent};
                const frames = window.detailFrames = [];
                const sample = () => {
                    if (!dialog.open || frames !== window.detailFrames) return;
                    const visibility = {checkOpacity: true, checkVisibilityCSS: true};
                    if (dialog.checkVisibility(visibility))
                        frames.push([...figure.querySelectorAll("img")].filter(image => image.complete &&
                            image.naturalWidth && image.checkVisibility(visibility)).map(image => image.naturalWidth));
                    requestAnimationFrame(sample);
                };
                requestAnimationFrame(sample);
            }, {once: true});
        }""")

    async def assert_reveal(self, workbench, *, warm):
        width, height = self.sizes[workbench]
        await self.page.wait_for_function("width => window.detailFrames?.some(frame => frame.includes(width))", arg=width)
        self.assertEqual(await self.image.evaluate("img => [img.naturalWidth, img.naturalHeight]"), [width, height])
        await self.assert_pixels(workbench)
        frames = await self.page.evaluate("window.detailFrames")
        changes = [frame for index, frame in enumerate(frames) if not index or frame != frames[index - 1]]
        self.assertEqual(changes, [[width]] if warm else [[], [width]])

    async def assert_pixels(self, workbench=None):
        # Sample away from centered loading text and the image's rounded edges.
        capture = await self.dialog.locator("figure").screenshot()
        with Image.open(io.BytesIO(capture)) as screenshot:
            pixel = screenshot.getpixel((screenshot.width // 3, screenshot.height // 3))[:3]
        def matches(color):
            return all(abs(actual - target) <= 8 for actual, target in zip(pixel, color))
        self.assertFalse(matches(self.thumb_color), f"thumbnail painted in detail: {pixel}")
        if workbench:
            self.assertTrue(matches(self.colors[workbench]), (pixel, self.colors[workbench]))
        else:
            self.assertFalse(any(matches(color) for color in self.colors.values()), f"unexpected artwork: {pixel}")

    async def test_cold_gallery_opens_text_immediately_and_only_reveals_the_original(self):
        await self.page.emulate_media(reduced_motion="no-preference")
        gate = self.hold("GET", self.paths["single"])
        await self.show_gallery()
        await self.observe_frames(self.cards["single"].locator(".image-card-open"))
        await self.open_card("single")
        opening = await self.page.evaluate("window.detailOnClick")
        self.assertTrue(opening["open"])
        self.assertIn("single.png", opening["meta"])
        await self.entered(gate)
        await self.assert_pixels()
        size = self.dialog.locator('[data-detail-value="output-size"] dd')
        await self.expect(size).to_have_text("读取中…")
        await self.release(gate)
        await self.expect(size).to_have_text("960 × 1440")
        await self.assert_reveal("single", warm=False)
        self.assertEqual(self.requests.count(("GET", self.paths["single"])), 1)

    async def test_hovered_decoded_original_is_visible_on_first_detail_frame(self):
        await self.page.emulate_media(reduced_motion="no-preference")
        await self.show_gallery()
        card = self.cards["random"]
        await self.expect(card).to_have_attribute("data-thumbnail-state", "loaded")
        gate = self.hold("GET", self.paths["random"])
        # Observe production's actual decode completion, without issuing any
        # test-side load or decode that would itself prewarm the browser.
        await self.page.evaluate("""path => {
            const decode = HTMLImageElement.prototype.decode;
            HTMLImageElement.prototype.decode = function(...args) {
                const result = decode.apply(this, args);
                if (this.src === new URL(path, location.href).href)
                    result.then(() => {window.originalWarmed = true; HTMLImageElement.prototype.decode = decode;}, () => {});
                return result;
            };
        }""", self.paths["random"])
        opener = card.locator(".image-card-open")
        await opener.hover()
        await self.entered(gate)
        await self.release(gate)
        await self.page.wait_for_function("window.originalWarmed")
        await self.observe_frames(opener)
        await opener.click()
        await self.assert_reveal("random", warm=True)
        self.assertEqual(self.requests.count(("GET", self.paths["random"])), 1)

    async def test_failed_original_shows_error_without_thumbnail_and_reopen_recovers(self):
        await self.show_gallery()
        gate = self.hold("GET", self.paths["random"], error=True)
        await self.open_card("random")
        await self.entered(gate)
        await self.release(gate)
        await self.expect(self.status).to_contain_text("失败")
        await self.expect(self.status).to_be_visible()
        await self.assert_pixels()
        await self.page.keyboard.press("Escape")
        await self.expect(self.dialog).to_be_hidden()
        await self.open_card("random")
        await self.page.wait_for_function("document.getElementById('detailImage').naturalWidth === 1024")
        await self.expect(self.status).to_be_hidden()
        await self.assert_pixels("random")

    async def test_late_result_after_close_cannot_reveal_another_artwork(self):
        await self.show_gallery()
        first = self.hold("GET", self.paths["random"])
        second = self.hold("GET", self.paths["single"])
        await self.open_card("random")
        await self.entered(first)
        await self.page.keyboard.press("Escape")
        await self.expect(self.dialog).to_be_hidden()
        await self.observe_frames(self.cards["single"].locator(".image-card-open"))
        await self.open_card("single")
        await self.entered(second)
        await self.release(first)
        await self.assert_pixels()
        await self.release(second)
        await self.assert_reveal("single", warm=False)
        await self.expect(self.status).to_be_hidden()

    async def test_keyboard_opens_fullsize_while_original_is_pending(self):
        await self.page.emulate_media(reduced_motion="no-preference")
        await self.show_gallery()
        gate = self.hold("GET", self.paths["random"])
        await self.cards["random"].locator(".image-card-open").evaluate("""button => {
            button.addEventListener("click", () => {
                const image = document.getElementById("detailImage");
                image.focus();
                const focused = document.activeElement === image;
                image.dispatchEvent(new KeyboardEvent("keydown", {key: "Enter", bubbles: true, cancelable: true}));
                window.earlyDetailZoom = focused && document.getElementById("imagePreviewDialog").open;
            }, {once: true});
        }""")
        await self.open_card("random")
        self.assertTrue(await self.page.evaluate("window.earlyDetailZoom"))
        preview = self.page.locator("#imagePreviewDialog")
        await self.expect(preview.locator("#imagePreviewImage")).to_have_attribute("src", self.paths["random"])
        await self.entered(gate)
        await self.release(gate)
        await self.expect(preview.locator("#imagePreviewImage")).to_be_visible()
        await self.page.keyboard.press("Escape")
        await self.expect(preview).to_be_hidden()
        await self.expect(self.image).to_be_focused()
        await self.assert_pixels("random")

    async def test_single_workbench_opens_the_displayed_original_and_returns_focus(self):
        await self.page.locator('[data-workbench="single"]').click()
        canvas = self.page.locator("#singlePreviewImage")
        await self.expect(canvas).to_have_attribute("src", self.paths["single"])
        await canvas.evaluate("image => image.decode()")
        opener = self.page.locator("#singlePreviewOpen")
        await self.observe_frames(opener)
        await opener.click()
        await self.expect(self.dialog.locator('[data-detail-value="output-size"] dd')).to_have_text("960 × 1440")
        await self.assert_reveal("single", warm=True)
        await self.page.keyboard.press("Escape")
        await self.expect(opener).to_be_focused()


if __name__ == "__main__":
    unittest.main()
