"""Frame-level preset refresh/reorder regression checks against temporary data."""
from __future__ import annotations

import asyncio
import importlib
import json
import os
from pathlib import Path
import sys
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))
fixture = importlib.import_module("test_workbench_races")
polish = importlib.import_module("test_style_preset_polish")
ARTIFACTS = fixture.APP_DIR / "output" / "playwright" / "style-preset-jitter"


@unittest.skipUnless(os.environ.get("ANIMA_BROWSER_TESTS") == "1", "opt in to Chrome acceptance")
class StylePresetJitterTests(unittest.IsolatedAsyncioTestCase):
    open_presets = fixture.WorkbenchRaceTests.open_presets
    hold = fixture.WorkbenchRaceTests.hold
    entered = fixture.WorkbenchRaceTests.entered
    release = fixture.WorkbenchRaceTests.release
    seed = polish.StylePresetPolishTests.seed
    open_library = polish.StylePresetPolishTests.open_library
    settle = polish.StylePresetPolishTests.settle

    async def asyncSetUp(self):
        ARTIFACTS.mkdir(parents=True, exist_ok=True)
        if "recorded" in self._testMethodName:
            api = importlib.import_module("playwright.async_api")
            original = api.Browser.new_context

            async def with_video(browser, **kwargs):
                return await original(browser, **kwargs, record_video_dir=str(ARTIFACTS / "video"),
                                      record_video_size={"width": 1440, "height": 1000})

            with mock.patch.object(api.Browser, "new_context", with_video):
                await fixture.WorkbenchRaceTests.asyncSetUp(self)
        else:
            await fixture.WorkbenchRaceTests.asyncSetUp(self)
        self.store = self.app[fixture.STYLE_PRESETS_KEY]

    async def asyncTearDown(self):
        for gate in self.gates:
            gate.release.set()
        if hasattr(self, "page") and not self.page.is_closed():
            await self.page.screenshot(path=str(ARTIFACTS / f"{self._testMethodName}.png"))
        if hasattr(self, "context"):
            await self.context.tracing.stop(path=str(ARTIFACTS / f"{self._testMethodName}.zip"))
            if "recorded" in self._testMethodName:
                video = self.page.video
                await self.context.close()
                await video.save_as(str(ARTIFACTS / "preset-jitter-fixed.webm"))
        self.assertEqual(self.comfy.submissions, [])
        self.assertNotIn(("POST", "/api/batches"), self.requests)
        self.assertEqual(self.page_errors, [])

    async def idle(self):
        await self.page.wait_for_function("""() => {
          const manager=document.querySelector('#stylePresetManager');
          return manager.dataset.motionState==='open' && document.querySelector('#stylePresetGrid').getAttribute('aria-busy')==='false'
            && [...document.querySelectorAll('.spm-card')].every(el=>!StudioMotion.snapshot(el,'spm-reorder')?.active);
        }""")
        await self.settle()

    async def begin_frames(self):
        await self.page.evaluate("""() => {
          const scroll=document.querySelector('.spm-scroll'),grid=document.querySelector('#stylePresetGrid');
          const nodes=[...grid.children], rect=el=>{const r=el.getBoundingClientRect();return {x:r.x,y:r.y,width:r.width,height:r.height}};
          const audit={frames:[],nodes,frame:null};window.jitterAudit=audit;
          const tick=()=>{audit.frames.push({grid:rect(grid),scroll:rect(scroll),scrollTop:scroll.scrollTop,
            order:[...grid.children].map(el=>el.dataset.presetId),cards:nodes.filter(el=>el.isConnected).map(el=>({id:el.dataset.presetId,...rect(el)})),
            identity:nodes.every(el=>!el.isConnected||grid.querySelector(`[data-preset-id='${el.dataset.presetId}']`)===el)});
            audit.frame=requestAnimationFrame(tick);};tick();
        }""")

    async def frames(self, count=8):
        await self.page.evaluate("async count=>{for(let i=0;i<count;i++)await new Promise(requestAnimationFrame)}", count)

    async def end_frames(self):
        return await self.page.evaluate("() => {cancelAnimationFrame(jitterAudit.frame);return jitterAudit.frames}")

    def assert_stable(self, frames, cards=True):
        self.assertGreater(len(frames), 2)
        before = frames[0]
        for sample in frames:
            self.assertTrue(sample["identity"])
            self.assertAlmostEqual(sample["scrollTop"], before["scrollTop"], delta=0.5)
            for area in ("grid", "scroll"):
                for axis in ("x", "y", "width", "height"):
                    self.assertAlmostEqual(sample[area][axis], before[area][axis], delta=0.5,
                                           msg=f"{area}.{axis}: whole library shifted")
            if cards:
                self.assertEqual(sample["order"], before["order"])
                self.assertEqual(len(sample["cards"]), len(before["cards"]))
                for actual, expected in zip(sample["cards"], before["cards"]):
                    self.assertEqual(actual["id"], expected["id"])
                    for axis in ("x", "y", "width", "height"):
                        self.assertAlmostEqual(actual[axis], expected[axis], delta=0.5,
                                               msg=f"{actual['id']}.{axis}: cached card shifted")

    async def test_three_entrypoints_keep_frame_geometry_across_layouts(self):
        await self.seed(20)
        await self.open_library()
        await self.page.locator("#stylePresetSort").select_option("name")
        report = []
        # Each width/theme/view/motion preference appears on both sides of the matrix.
        matrix = [(1440, "dark", "cards", "no-preference"), (1440, "light", "list", "reduce"),
                  (720, "light", "cards", "no-preference"), (720, "dark", "list", "reduce"),
                  (390, "dark", "cards", "reduce"), (390, "light", "list", "no-preference")]
        for width, theme, view, motion in matrix:
            with self.subTest(width=width, theme=theme, view=view, motion=motion):
                await self.page.set_viewport_size({"width": width, "height": 1000 if width == 1440 else 844})
                await self.page.emulate_media(reduced_motion=motion)
                await self.page.evaluate("theme=>document.documentElement.dataset.theme=theme", theme)
                await self.page.get_by_role("button", name="列表" if view == "list" else "紧凑卡片", exact=True).click()
                await self.idle()
                for action in ("create", "edit", "favorite"):
                    if action == "create":
                        await self.page.locator(".spm-scroll").evaluate("el=>el.scrollTop=140")
                        trigger = self.page.locator("#stylePresetNew")
                    else:
                        trigger = self.page.locator(".spm-card-open" if action == "edit" else ".spm-star").nth(6)
                        await trigger.scroll_into_view_if_needed()
                    await self.settle()
                    await self.begin_frames()
                    if action == "favorite":
                        item_id = await trigger.evaluate("el=>el.closest('.spm-card').dataset.presetId")
                        old_pressed = await trigger.get_attribute("aria-pressed")
                        put = self.hold("PUT", f"/api/style-presets/{item_id}")
                        reads = [self.hold("GET", "/api/style-presets", profile="anima") for _ in range(2)]
                        await trigger.click()
                        await self.entered(put)
                        await self.expect(trigger).to_have_attribute("aria-pressed", "false" if old_pressed == "true" else "true")
                        await self.frames()
                        await self.release(put)
                    else:
                        reads = [self.hold("GET", "/api/style-presets", profile="anima")]
                        await trigger.click()
                    for gate in reads:
                        await self.entered(gate)
                    await self.expect(self.page.locator(".spm-results")).to_contain_text("正在刷新")
                    await self.expect(self.page.locator(".spm-read-notice")).to_be_hidden()
                    await self.frames()
                    for gate in reads:
                        await self.release(gate)
                    await self.idle()
                    if action != "favorite":
                        await self.page.locator("#stylePresetEditor .spm-heading button").click()
                        await self.expect(self.page.locator("#stylePresetEditor")).to_be_hidden()
                        await self.expect(trigger).to_be_focused()
                    await self.frames()
                    samples = await self.end_frames()
                    self.assert_stable(samples)
                    report.append({"width": width, "theme": theme, "view": view, "motion": motion,
                                   "action": action, "frames": len(samples), "gridY": samples[0]["grid"]["y"],
                                   "scrollTop": samples[0]["scrollTop"], "maximumGridYDrift": max(abs(s["grid"]["y"]-samples[0]["grid"]["y"]) for s in samples)})
        (ARTIFACTS / "frame-geometry.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")

    async def test_recorded_favorite_commits_one_reorder_and_keeps_filtered_item_until_success(self):
        await self.seed(14)
        await self.page.emulate_media(reduced_motion="no-preference")
        await self.page.evaluate("document.documentElement.dataset.theme='dark'")
        await self.open_library()
        await self.idle()
        for trigger in (self.page.locator("#stylePresetNew"), self.page.locator(".spm-card-open").first):
            await trigger.click()
            await self.page.wait_for_function("document.querySelector('#stylePresetEditor').dataset.motionState==='open'")
            await self.frames(16)
            await self.page.locator("#stylePresetEditor .spm-heading button").click()
            await self.expect(self.page.locator("#stylePresetEditor")).to_be_hidden()
        await self.idle()
        target = self.page.locator('.spm-star[aria-pressed="true"]').first
        item_id = await target.evaluate("el=>el.closest('.spm-card').dataset.presetId")
        target = self.page.locator(f'[data-preset-id="{item_id}"] .spm-star')
        put = self.hold("PUT", f"/api/style-presets/{item_id}")
        reads = [self.hold("GET", "/api/style-presets", profile="anima") for _ in range(2)]
        await self.begin_frames()
        await target.click()
        await self.entered(put)
        await self.expect(target).to_have_attribute("aria-pressed", "false")
        await self.expect(target).to_have_attribute("aria-busy", "true")
        await self.expect(target).to_be_focused()
        await self.frames(20)
        pending = await self.page.evaluate("jitterAudit.frames")
        self.assert_stable(pending)
        await self.release(put)
        for gate in reads:
            await self.entered(gate)
        await self.frames(25)
        for gate in reads:
            await self.release(gate)
        await self.idle()
        samples = await self.end_frames()
        self.assert_stable(samples, cards=False)
        orders = []
        for sample in samples:
            if not orders or orders[-1] != sample["order"]:
                orders.append(sample["order"])
        self.assertEqual(len(orders), 2, "one commit must produce exactly one DOM reorder")
        expected = [item["id"] for item in sorted(self.store.list("anima")["items"], key=lambda item: not item["favorite"])]
        self.assertEqual(orders[-1], expected)
        self.assertEqual(orders[-1].index(item_id), 2, "unfavorite lands at the head of the nonfavorite section")
        await self.expect(target).to_be_focused()
        await self.page.get_by_role("button", name="列表", exact=True).click()
        await self.page.get_by_role("button", name="紧凑卡片", exact=True).click()
        await self.page.locator('[data-collection="favorite"]').click()
        visible = self.page.locator(".spm-card")
        self.assertEqual(await visible.count(), 2)
        target = visible.first.locator(".spm-star")
        filtered_id = await target.evaluate("el=>el.closest('.spm-card').dataset.presetId")
        put = self.hold("PUT", f"/api/style-presets/{filtered_id}")
        await target.click()
        await self.entered(put)
        await self.expect(target).to_have_attribute("aria-pressed", "false")
        self.assertEqual(await visible.count(), 2, "pending unstar must not remove the active card")
        await self.frames(20)
        await self.release(put)
        await self.expect(visible).to_have_count(1)
        await self.idle()
        await self.expect(visible.locator(".spm-card-open")).to_be_focused()
        (ARTIFACTS / "favorite-reorder.json").write_text(json.dumps({"frames": len(samples), "pendingFrames": len(pending),
            "orderChanges": len(orders)-1, "targetIndex": [order.index(item_id) for order in orders],
            "maximumGridYDrift": max(abs(s["grid"]["y"]-samples[0]["grid"]["y"]) for s in samples)}, indent=2), encoding="utf-8")

    async def test_failed_favorite_restores_star_without_reordering(self):
        await self.seed(14)
        await self.open_library()
        await self.idle()
        target = self.page.locator('.spm-star[aria-pressed="true"]').first
        item_id = await target.evaluate("el=>el.closest('.spm-card').dataset.presetId")
        target = self.page.locator(f'[data-preset-id="{item_id}"] .spm-star')
        entered, release = asyncio.Event(), asyncio.Event()

        async def fail_write(route):
            entered.set()
            await release.wait()
            await route.fulfill(status=503, json={"error": "测试：收藏写入失败"})

        await self.page.route(f"**/api/style-presets/{item_id}", fail_write)
        await self.begin_frames()
        await target.click()
        await asyncio.wait_for(entered.wait(), 10)
        await self.expect(target).to_have_attribute("aria-pressed", "false")
        await self.expect(target).to_have_attribute("aria-busy", "true")
        await self.expect(target).to_be_focused()
        await self.frames()
        pending = await self.end_frames()
        self.assert_stable(pending)
        release.set()
        await self.expect(target).to_have_attribute("aria-busy", "false")
        await self.expect(target).to_have_attribute("aria-pressed", "true")
        await self.expect(target).to_be_focused()
        await self.expect(self.page.locator("#stylePresetManager .spm-error")).to_contain_text("收藏写入失败")
        order = await self.page.locator(".spm-card").evaluate_all("nodes=>nodes.map(el=>el.dataset.presetId)")
        self.assertEqual(order, pending[0]["order"])
        self.assertTrue(self.store.get(item_id)["favorite"])
