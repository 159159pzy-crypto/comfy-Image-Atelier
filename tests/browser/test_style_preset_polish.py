"""Style-library acceptance against temporary data and FakeComfy; never GPU work.

ANIMA_BROWSER_TESTS=1 ANIMA_BROWSER_CHANNEL=chrome python -m pytest
tests/browser/test_style_preset_polish.py
"""
from __future__ import annotations

import importlib
import io
import json
import os
from pathlib import Path
import sys
import unittest
from unittest import mock

from PIL import Image, ImageDraw

sys.path.insert(0, str(Path(__file__).resolve().parent))
fixture = importlib.import_module("test_workbench_races")
from anima_webui.server import STYLE_PRESET_COVERS_KEY  # noqa: E402

ARTIFACTS = fixture.APP_DIR / "output" / "playwright" / "style-preset-polish"


def cover_image():
    image = Image.new("RGB", (640, 480), "#9eafb3")
    draw = ImageDraw.Draw(image)
    draw.ellipse((200, 60, 440, 300), fill="#d3c2ac")
    draw.ellipse((120, 280, 520, 720), fill="#737e83")
    buffer = io.BytesIO()
    image.save(buffer, format="PNG")
    return buffer.getvalue()


@unittest.skipUnless(os.environ.get("ANIMA_BROWSER_TESTS") == "1", "opt in to Chrome acceptance")
class StylePresetPolishTests(unittest.IsolatedAsyncioTestCase):
    open_presets = fixture.WorkbenchRaceTests.open_presets
    switch_profile = fixture.WorkbenchRaceTests.switch_profile
    hold = fixture.WorkbenchRaceTests.hold
    entered = fixture.WorkbenchRaceTests.entered
    release = fixture.WorkbenchRaceTests.release

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
            await self.page.screenshot(path=str(ARTIFACTS / (self._testMethodName + ".png")))
        if hasattr(self, "context"):
            await self.context.tracing.stop(path=str(ARTIFACTS / (self._testMethodName + ".zip")))
            if "recorded" in self._testMethodName:
                video = self.page.video
                await self.context.close()
                await video.save_as(str(ARTIFACTS / "preset-interactions.webm"))
        self.assertEqual(self.comfy.submissions, [])
        self.assertNotIn(("POST", "/api/batches"), self.requests)
        self.assertEqual(self.page_errors, [])

    async def settle(self):
        await self.page.evaluate("async () => {await document.fonts.ready; await new Promise(requestAnimationFrame); await new Promise(requestAnimationFrame);}")

    async def seed(self, count=14):
        group = await self.store.create_group({"name": "常用人像", "model_profile": "anima"})
        cover = await self.app[STYLE_PRESET_COVERS_KEY].add(cover_image())
        settings = await self.page.evaluate("readSettings()")
        names = ["晨雾柔光", "纸上水彩", "胶片人像", "清透线稿", "暮色与暖光", "低饱和自然光", "细腻油画",
                 "日常速写", "柔和边缘与细节保留的长名称预设", "复古色彩", "冷调月光", "轻盈笔触", "安静午后", "远山"]
        for index in range(count):
            await self.store.create({"name": names[index] if index < len(names) else f"测试预设 {index:03d}",
                                     "settings": settings, "tags": ["柔光", "人像"] if index % 2 == 0 else ["插画"],
                                     "groups": [group["id"]] if index < 3 else [], "favorite": index < 3,
                                     "cover_id": cover["id"] if index % 3 == 0 else None})
        await self.page.evaluate("loadStylePresets()")

    async def open_library(self):
        await self.open_presets()
        await self.page.locator("#manageStylePresets").click()
        await self.expect(self.page.locator("#stylePresetManager")).to_be_visible()
        await self.expect(self.page.locator(".spm-card:visible")).to_have_count(self.store.list("anima")["count"])

    async def test_stable_nodes_focus_selection_and_view_preference(self):
        await self.seed(40)
        await self.open_library()
        await self.page.evaluate("window.auditCards = [...document.querySelectorAll('.spm-card')]")
        card = self.page.locator(".spm-card").nth(15)
        preset_id = await card.get_attribute("data-preset-id")
        await card.locator(".spm-card-open").click()
        await self.expect(self.page.locator("#stylePresetEditor")).to_be_visible()
        before = await self.page.locator(".spm-scroll").evaluate("el => el.scrollTop")
        await self.page.evaluate("StylePresetManager.refresh()")
        self.assertTrue(await self.page.evaluate("auditCards.every(el => el.isConnected)"))
        await self.page.locator("#stylePresetEditor .spm-heading button").click()
        await self.expect(self.page.locator(f'[data-preset-id="{preset_id}"] .spm-card-open')).to_be_focused()
        self.assertEqual(await self.page.locator(".spm-scroll").evaluate("el => el.scrollTop"), before)
        await self.page.locator("#stylePresetSearch").fill("纸上")
        await self.expect(self.page.locator(".spm-card:visible")).to_have_count(1)
        await self.page.locator("#stylePresetSearch").fill("")
        self.assertTrue(await self.page.evaluate("auditCards.every(el => el.isConnected && document.querySelector(`#stylePresetGrid [data-preset-id='${el.dataset.presetId}']`) === el)"))
        await self.page.locator("#stylePresetSelect").click()
        first = self.page.locator(".spm-card-open").first
        await first.click()
        await self.expect(first).to_have_attribute("aria-pressed", "true")
        await self.page.get_by_role("button", name="选择当前结果", exact=True).click()
        await self.expect(self.page.get_by_role("button", name="选择当前结果", exact=True)).to_be_focused()
        anchor = await self.page.evaluate("() => {const s=document.querySelector('.spm-scroll');s.scrollTop=(s.scrollHeight-s.clientHeight)/2;const top=s.getBoundingClientRect().top;const card=[...document.querySelectorAll('.spm-card')].find(e=>e.getBoundingClientRect().bottom>top);return {id:card.dataset.presetId,offset:card.getBoundingClientRect().top-top};}")
        await self.page.get_by_role("button", name="列表", exact=True).click()
        await self.expect(self.page.locator("#stylePresetGrid")).to_have_attribute("data-view", "list")
        after_offset = await self.page.locator(f'#stylePresetGrid [data-preset-id="{anchor["id"]}"]').evaluate("el=>el.getBoundingClientRect().top-document.querySelector('.spm-scroll').getBoundingClientRect().top")
        self.assertAlmostEqual(after_offset, anchor["offset"], delta=1)
        await self.expect(first).to_have_attribute("aria-pressed", "true")
        await self.page.keyboard.press("Escape")
        await self.page.reload()
        await self.page.wait_for_function("initialized")
        await self.open_library()
        await self.expect(self.page.locator("#stylePresetGrid")).to_have_attribute("data-view", "list")

    async def test_tags_ime_pending_input_and_field_errors(self):
        await self.open_library()
        await self.page.locator("#stylePresetNew").click()
        await self.page.locator("#stylePresetSave").click()
        await self.expect(self.page.locator("#stylePresetName")).to_have_attribute("aria-invalid", "true")
        await self.expect(self.page.locator("#stylePresetName")).to_be_focused()
        await self.page.locator("#stylePresetName").fill("标签验收")
        tags = self.page.locator("#stylePresetTags")
        await tags.fill("Soft，soft,人像")
        await tags.press("Enter")
        await self.expect(self.page.locator("#stylePresetEditor .spm-tag-token")).to_have_count(2)
        await tags.dispatch_event("compositionstart")
        await tags.fill("水彩")
        await tags.press("Enter")
        await self.expect(self.page.locator("#stylePresetEditor .spm-tag-token")).to_have_count(2)
        await tags.dispatch_event("compositionend", {"data": "水彩"})
        await tags.press("Enter")
        await self.expect(self.page.locator("#stylePresetEditor .spm-tag-token")).to_have_count(3)
        await self.page.locator("#stylePresetEditor .spm-tag-remove").first.click()
        await tags.fill("待提交")
        await self.page.locator("#stylePresetSave").click()
        await self.expect(self.page.locator("#stylePresetEditor")).to_be_hidden()
        item = self.store.list("anima")["items"][0]
        self.assertEqual(item["tags"], ["人像", "水彩", "待提交"])
        await self.page.locator(".spm-card-open").click()
        await self.page.get_by_role("button", name="新建分组", exact=True).click()
        await self.page.locator("#stylePresetGroupName").fill("第二分组")
        await self.page.locator("#stylePresetOrganizeSave").click()
        await self.expect(self.page.locator("#stylePresetOrganize")).to_be_hidden()
        await self.expect(self.page.get_by_role("button", name="新建分组", exact=True)).to_be_focused()
        await self.expect(self.page.locator("#stylePresetEditor .spm-heading button")).to_have_attribute("aria-label", "关闭编辑风格预设")

    async def test_failed_read_retry_cached_refresh_and_apply_error(self):
        await self.seed(2)
        async def fail_read(route):
            await route.fulfill(status=503, json={"error": "测试：读取失败"})
        await self.page.route("**/api/style-presets?*", fail_read)
        await self.open_presets()
        await self.page.locator("#manageStylePresets").click()
        await self.expect(self.page.locator(".spm-read-notice")).to_contain_text("读取")
        await self.expect(self.page.locator("#stylePresetManager")).not_to_contain_text("第一种风格")
        await self.page.unroute("**/api/style-presets?*", fail_read)
        await self.page.get_by_role("button", name="重新读取", exact=True).click()
        await self.expect(self.page.locator(".spm-card:visible")).to_have_count(2)
        await self.page.route("**/api/style-presets?*", fail_read)
        await self.page.evaluate("StylePresetManager.refresh()")
        await self.expect(self.page.locator(".spm-card:visible")).to_have_count(2)
        await self.page.unroute("**/api/style-presets?*", fail_read)
        async def fail_apply(route):
            await route.fulfill(status=503, json={"error": "测试：资源解析失败"})
        await self.page.route("**/api/style-presets/*/resolve", fail_apply)
        current = await self.page.evaluate("readSettings()")
        await self.page.locator(".spm-apply").first.click()
        await self.expect(self.page.locator(".spm-apply-error").first).to_contain_text("资源解析失败")
        await self.expect(self.page.locator("#stylePresetManager")).to_be_visible()
        self.assertEqual(await self.page.evaluate("readSettings()"), current)

    async def test_crop_pointer_isolation_cancel_and_geometry(self):
        await self.open_library()
        await self.page.locator("#stylePresetNew").click()
        await self.page.locator("#stylePresetCoverFile").set_input_files({"name": "portrait.png", "mimeType": "image/png", "buffer": cover_image()})
        await self.expect(self.page.locator("#stylePresetCoverZoom")).to_be_visible()
        await self.page.locator("#stylePresetCoverZoom").fill("2")
        stage = self.page.locator("#stylePresetCropStage")
        bounds = await stage.bounding_box()
        x, y = bounds["x"] + bounds["width"] / 2, bounds["y"] + bounds["height"] / 2
        await self.page.mouse.move(x, y)
        await self.page.mouse.down()
        await self.page.mouse.move(x + 20, y + 10, steps=4)
        await self.settle()
        before = await stage.locator("img").get_attribute("style")
        await stage.dispatch_event("pointerdown", {"pointerId": 98, "pointerType": "touch", "clientX": x, "clientY": y, "isPrimary": False})
        await stage.dispatch_event("pointermove", {"pointerId": 98, "pointerType": "touch", "clientX": x + 80, "clientY": y + 80})
        await self.settle()
        self.assertEqual(await stage.locator("img").get_attribute("style"), before)
        await stage.dispatch_event("pointercancel", {"pointerId": 1})
        await self.page.mouse.move(x - 30, y - 30)
        await self.page.mouse.up()
        await self.settle()
        self.assertEqual(await stage.locator("img").get_attribute("style"), before)
        geometry = await stage.evaluate("el => {const a=el.getBoundingClientRect(),b=el.querySelector('img').getBoundingClientRect();return {covered:b.left<=a.left+1&&b.top<=a.top+1&&b.right>=a.right-1&&b.bottom>=a.bottom-1,transform:el.querySelector('img').style.transform};}")
        self.assertTrue(geometry["covered"])
        self.assertIn("translate", geometry["transform"])

    async def test_visual_matrix_and_typography(self):
        await self.seed()
        await self.open_library()
        typography = await self.page.evaluate("() => Object.fromEntries(['#savePreset','#stylePresetNew','.spm-heading h2'].map(q=>{const s=getComputedStyle(document.querySelector(q));return [q,{family:s.fontFamily,size:s.fontSize,weight:s.fontWeight}]}))")
        self.assertEqual(typography["#stylePresetNew"], typography["#savePreset"])
        self.assertEqual(typography[".spm-heading h2"]["size"], "20px")
        matrix = []
        for theme in ("light", "dark"):
            await self.page.evaluate("v => document.documentElement.dataset.theme=v", theme)
            for width, height in ((1440, 1000), (720, 900), (390, 844)):
                await self.page.set_viewport_size({"width": width, "height": height})
                await self.settle()
                for surface in ("manager", "edit", "create"):
                    if surface == "edit":
                        await self.page.locator(".spm-card-open").first.click()
                    elif surface == "create":
                        await self.page.locator("#stylePresetNew").click()
                    await self.settle()
                    dialog_id = "#stylePresetManager" if surface == "manager" else "#stylePresetEditor"
                    geometry = await self.page.locator(dialog_id).evaluate("el => {const r=el.getBoundingClientRect();return {left:r.left,right:r.right,top:r.top,bottom:r.bottom,scroll:el.scrollWidth,width:el.clientWidth}}")
                    self.assertGreaterEqual(geometry["left"], 0)
                    self.assertLessEqual(geometry["right"], width + 1)
                    self.assertLessEqual(geometry["bottom"], height + 1)
                    self.assertLessEqual(geometry["scroll"], geometry["width"] + 1)
                    matrix.append({"theme": theme, "viewportWidth": width, "surface": surface, **geometry})
                    await self.page.screenshot(path=str(ARTIFACTS / f"after-{surface}-{theme}-{width}.png"))
                    if surface != "manager":
                        await self.page.locator("#stylePresetEditor .spm-heading button").click()
                await self.page.get_by_role("button", name="列表", exact=True).click()
                await self.page.screenshot(path=str(ARTIFACTS / f"after-list-{theme}-{width}.png"))
                await self.page.get_by_role("button", name="紧凑卡片", exact=True).click()
        # Desktop 200% zoom at 1440x1000: half the CSS viewport, twice the pixel
        # density. CSS zoom on <html> is not equivalent and can misplace dialogs.
        zoom_context = await self.browser.new_context(viewport={"width": 720, "height": 500}, device_scale_factor=2, reduced_motion="reduce")
        try:
            async def local_only(route):
                await route.continue_() if route.request.url.startswith(self.origin + "/") else await route.abort()
            await zoom_context.route("**/*", local_only)
            zoom_page = await zoom_context.new_page()
            zoom_page.on("pageerror", lambda failure: self.page_errors.append(str(failure)))
            await zoom_page.goto(self.origin)
            await zoom_page.wait_for_function("initialized")
            await zoom_page.evaluate("document.documentElement.dataset.theme='dark'")
            await zoom_page.locator(".preset-section > summary").click()
            await zoom_page.locator("#manageStylePresets").click()
            for surface in ("manager", "create"):
                if surface == "create":
                    await zoom_page.locator("#stylePresetNew").click()
                await zoom_page.evaluate("async()=>{await document.fonts.ready;await new Promise(requestAnimationFrame);await new Promise(requestAnimationFrame);}")
                selector = "#stylePresetManager" if surface == "manager" else "#stylePresetEditor"
                bounds = await zoom_page.locator(selector).evaluate("el=>{const r=el.getBoundingClientRect();return {top:r.top,bottom:r.bottom,left:r.left,right:r.right}}")
                self.assertGreaterEqual(bounds["top"], 0)
                self.assertLessEqual(bounds["bottom"], 500)
                self.assertGreaterEqual(bounds["left"], 0)
                self.assertLessEqual(bounds["right"], 720)
                await self.expect(zoom_page.locator(selector + " .spm-heading")).to_be_in_viewport()
                if surface == "create":
                    await self.expect(zoom_page.locator("#stylePresetSave")).to_be_in_viewport()
                await zoom_page.screenshot(path=str(ARTIFACTS / f"after-{surface}-200-percent.png"))
                matrix.append({"surface": surface, "zoomEquivalent": 2, **bounds})
                if surface == "create":
                    await zoom_page.locator("#stylePresetEditor .spm-heading button").click()
        finally:
            await zoom_context.close()
        cdp = await self.context.new_cdp_session(self.page)
        for feature in ("prefers-reduced-transparency", "prefers-contrast"):
            await cdp.send("Emulation.setEmulatedMedia", {"features": [{"name": feature, "value": "more" if feature == "prefers-contrast" else "reduce"}]})
            self.assertEqual(await self.page.locator("#stylePresetManager").evaluate("el=>getComputedStyle(el).backdropFilter"), "none")
        await cdp.detach()
        (ARTIFACTS / "layout-matrix.json").write_text(json.dumps({"typography": typography, "layouts": matrix}, ensure_ascii=False, indent=2), encoding="utf-8")

    async def test_recorded_motion_and_large_library_performance(self):
        await self.seed(256)
        await self.page.emulate_media(reduced_motion="no-preference")
        await self.open_library()
        await self.page.wait_for_function("document.querySelector('#stylePresetManager').dataset.motionState==='open'")
        await self.page.locator(".spm-card-open").first.click()
        await self.page.wait_for_function("document.querySelector('#stylePresetEditor').dataset.motionState==='open'")
        layers = await self.page.locator(".spm-dialog[open]").evaluate_all("nodes=>nodes.map(el=>({layer:el.dataset.spmLayer,blur:getComputedStyle(el,'::backdrop').backdropFilter}))")
        self.assertEqual(layers[-1], {"layer": "nested", "blur": "none"})
        await self.page.get_by_role("button", name="新建分组", exact=True).click()
        await self.page.locator("#stylePresetGroupName").fill("录像分组")
        await self.page.keyboard.press("Escape")
        await self.page.locator("#stylePresetConfirmAccept").click()
        await self.expect(self.page.locator("#stylePresetOrganize")).to_be_hidden()
        await self.page.locator("#stylePresetEditor .spm-heading button").click()
        await self.expect(self.page.locator("#stylePresetEditor")).to_be_hidden()
        await self.page.locator(".spm-star").nth(5).click()
        await self.expect(self.page.locator('.spm-star[aria-busy="true"]')).to_have_count(0)
        await self.page.get_by_role("button", name="列表", exact=True).click()
        await self.page.get_by_role("button", name="紧凑卡片", exact=True).click()
        await self.page.locator("#stylePresetSearch").fill("测试预设 1")
        await self.page.locator("#stylePresetSearch").fill("")
        await self.settle()
        performance = await self.page.evaluate("""async () => {
          const scroll=document.querySelector('.spm-scroll'), search=document.querySelector('#stylePresetSearch');
          const longTasks=[],frames=[], searches=[]; const observer=new PerformanceObserver(entries=>longTasks.push(...entries.getEntries().map(e=>e.duration)));
          observer.observe({type:'longtask'});
          const max=scroll.scrollHeight-scroll.clientHeight;
          for(let i=0;i<45;i++){frames.push(await new Promise(requestAnimationFrame));scroll.scrollTop=max*(.5-.5*Math.cos(i/44*Math.PI*2));}
          for(const query of ['测试预设 1','柔光','不存在','']){const start=performance.now();search.value=query;search.dispatchEvent(new Event('input',{bubbles:true}));searches.push(performance.now()-start);await new Promise(requestAnimationFrame);}
          await new Promise(requestAnimationFrame);observer.disconnect();
          const delta=frames.slice(1).map((t,i)=>t-frames[i]).sort((a,b)=>a-b);
          return {count:document.querySelectorAll('.spm-card').length,searchMs:searches,longTasks,frames:{p50:delta[Math.floor(delta.length*.5)],p95:delta[Math.floor(delta.length*.95)],max:delta.at(-1)}};
        }""")
        self.assertEqual(performance["count"], 256)
        # Keep measured budgets in the report, not as machine-speed dependent CI assertions.
        await self.page.locator("#stylePresetNew").click()
        await self.page.locator("#stylePresetCoverFile").set_input_files({"name": "portrait.png", "mimeType": "image/png", "buffer": cover_image()})
        await self.expect(self.page.locator("#stylePresetCoverZoom")).to_be_visible()
        await self.page.locator("#stylePresetCoverZoom").fill("2")
        await self.page.wait_for_function("document.querySelector('#stylePresetEditor').dataset.motionState==='open'")
        box = await self.page.locator("#stylePresetCropStage").bounding_box()
        await self.page.evaluate("""() => {
          const stage=document.querySelector('#stylePresetCropStage'), original=Element.prototype.getBoundingClientRect;
          const audit={reads:0,frames:[],longTasks:[],original,frame:null};window.cropAudit=audit;
          Element.prototype.getBoundingClientRect=function(...args){if(this===stage)audit.reads++;return original.apply(this,args);};
          audit.observer=new PerformanceObserver(entries=>audit.longTasks.push(...entries.getEntries().map(e=>e.duration)));audit.observer.observe({type:'longtask'});
          const tick=t=>{audit.frames.push(t);audit.frame=requestAnimationFrame(tick);};audit.frame=requestAnimationFrame(tick);
        }""")
        await self.page.mouse.move(box["x"] + box["width"] / 2, box["y"] + box["height"] / 2)
        await self.page.mouse.down()
        await self.page.mouse.move(box["x"] + box["width"] / 2 + 45, box["y"] + box["height"] / 2 + 15, steps=30)
        await self.page.mouse.up()
        performance["crop"] = await self.page.evaluate("""() => {
          const a=window.cropAudit;cancelAnimationFrame(a.frame);a.observer.disconnect();Element.prototype.getBoundingClientRect=a.original;
          const delta=a.frames.slice(1).map((t,i)=>t-a.frames[i]).sort((x,y)=>x-y);
          return {geometryReads:a.reads,longTasks:a.longTasks,frames:delta.length,p95:delta[Math.floor(delta.length*.95)]||0};
        }""")
        self.assertEqual(performance["crop"]["geometryReads"], 1, "only pointerdown measures the crop stage")
        (ARTIFACTS / "performance.json").write_text(json.dumps(performance, indent=2), encoding="utf-8")
        await self.page.keyboard.press("Escape")
        await self.page.locator("#stylePresetConfirmAccept").click()
        await self.expect(self.page.locator("#stylePresetEditor")).to_be_hidden()
