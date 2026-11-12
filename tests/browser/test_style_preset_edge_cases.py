"""Focused style-editor edge cases using temporary stores and network-free FakeComfy."""
from __future__ import annotations

import importlib
import io
import os
from pathlib import Path
import sys
import unittest

from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parent))
fixture = importlib.import_module("test_workbench_races")
ARTIFACTS = fixture.APP_DIR / "output" / "playwright" / "style-preset-edges"


def cover_file(name="cover.png"):
    buffer = io.BytesIO()
    Image.new("RGB", (800, 1200), "#91a5b8").save(buffer, format="PNG")
    return {"name": name, "mimeType": "image/png", "buffer": buffer.getvalue()}


@unittest.skipUnless(os.environ.get("ANIMA_BROWSER_TESTS") == "1", "opt in to Chrome acceptance")
class StylePresetEdgeCases(unittest.IsolatedAsyncioTestCase):
    open_presets = fixture.WorkbenchRaceTests.open_presets

    async def asyncSetUp(self):
        ARTIFACTS.mkdir(parents=True, exist_ok=True)
        await fixture.WorkbenchRaceTests.asyncSetUp(self)
        self.store = self.app[fixture.STYLE_PRESETS_KEY]

    async def asyncTearDown(self):
        for gate in self.gates:
            gate.release.set()
        if hasattr(self, "page") and not self.page.is_closed():
            await self.page.screenshot(path=str(ARTIFACTS / (self._testMethodName + ".png")))
        if hasattr(self, "context"):
            await self.context.tracing.stop(path=str(ARTIFACTS / (self._testMethodName + ".zip")))
        self.assertEqual(self.comfy.submissions, [])
        self.assertNotIn(("POST", "/api/batches"), self.requests)
        self.assertEqual(self.page_errors, [])

    async def open_library(self):
        await self.page.evaluate("loadStylePresets()")
        await self.open_presets()
        await self.page.locator("#manageStylePresets").click()
        await self.page.wait_for_function("document.querySelector('#stylePresetManager').dataset.motionState==='open'")

    async def test_mobile_128_groups_64_tags_ime_dirty_cancel_and_pending_save(self):
        groups = []
        for index in range(128):
            groups.append(await self.store.create_group({"name": f"分组 {index:03d} · 长名称自然光", "model_profile": "anima"}))
        tags = [f"标签 {index:02d} 柔光人像" for index in range(63)] + ["长" * 100]
        item = await self.store.create({"name": "密集表单验收", "settings": await self.page.evaluate("readSettings()"), "tags": tags})
        await self.page.set_viewport_size({"width": 390, "height": 844})
        await self.open_library()
        await self.page.locator(".spm-card-open").click()
        await self.expect(self.page.locator("#stylePresetGroups input")).to_have_count(128)
        await self.expect(self.page.locator("#stylePresetEditor .spm-tag-token")).to_have_count(64)
        bounds = await self.page.locator(".spm-editor-body").evaluate("el=>({scroll:el.scrollWidth,width:el.clientWidth})")
        self.assertLessEqual(bounds["scroll"], bounds["width"] + 1)
        entry = self.page.locator("#stylePresetTags")
        await entry.fill("第六十五个")
        await entry.press("Enter")
        await self.expect(entry).to_have_attribute("aria-invalid", "true")
        await self.expect(entry).to_have_value("第六十五个")
        await entry.fill("")
        await self.page.locator("#stylePresetEditor .spm-tag-remove").first.click()
        await entry.focus()
        await entry.dispatch_event("compositionstart")
        await entry.fill("输入法保留的标签")
        await entry.press("Enter")
        await self.expect(self.page.locator("#stylePresetEditor .spm-tag-token")).to_have_count(63)
        await self.page.keyboard.press("Escape")
        await self.expect(self.page.locator("#stylePresetConfirm")).to_be_visible()
        await self.page.locator("#stylePresetConfirm").get_by_role("button", name="取消", exact=True).click()
        await self.expect(entry).to_have_value("输入法保留的标签")
        await entry.dispatch_event("compositionend", {"data": "输入法保留的标签"})
        await self.page.locator("#stylePresetGroups input").last.check()
        await self.page.locator("#stylePresetSave").click()
        await self.expect(self.page.locator("#stylePresetEditor")).to_be_hidden()
        saved = self.store.get(item["id"])
        self.assertEqual(saved["tags"], tags[1:] + ["输入法保留的标签"])
        self.assertEqual(saved["groups"], [groups[-1]["id"]])
        self.assertTrue(saved["favorite"])

    async def test_cover_close_cancels_queued_crop_frame_and_pending_decode_urls(self):
        await self.open_library()
        await self.page.locator("#stylePresetNew").click()
        await self.page.locator("#stylePresetName").fill("封面清理")
        await self.page.evaluate("""() => {
          const create = URL.createObjectURL.bind(URL), revoke = URL.revokeObjectURL.bind(URL);
          window.edgeURLs = new Set(); window.edgeRevoked = [];
          URL.createObjectURL = blob => { const url=create(blob); edgeURLs.add(url); return url; };
          URL.revokeObjectURL = url => { edgeURLs.delete(url); edgeRevoked.push(url); return revoke(url); };
        }""")
        await self.page.locator("#stylePresetCoverFile").set_input_files(cover_file())
        await self.expect(self.page.locator("#stylePresetCoverZoom")).to_be_visible()
        await self.page.evaluate("() => new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))")
        result = await self.page.evaluate("""async () => {
          const request=requestAnimationFrame, cancel=cancelAnimationFrame, pending=new Set(), cancelled=[];
          window.requestAnimationFrame=cb=>{let id=request(t=>{pending.delete(id);cb(t)});pending.add(id);return id};
          window.cancelAnimationFrame=id=>{pending.delete(id);cancelled.push(id);cancel(id)};
          try {
            const zoom=document.querySelector('#stylePresetCoverZoom');zoom.value='2';zoom.dispatchEvent(new Event('input',{bubbles:true}));
            const queued=[...pending];
            document.querySelector('#stylePresetEditor .spm-heading button').click();
            document.querySelector('#stylePresetConfirmAccept').click();
            await Promise.resolve(); await Promise.resolve();
            return {queued,cancelled,pending:[...pending],urls:edgeURLs.size,editorOpen:document.querySelector('#stylePresetEditor').open};
          } finally {window.requestAnimationFrame=request;window.cancelAnimationFrame=cancel;}
        }""")
        self.assertEqual(len(result["queued"]), 1, result)
        self.assertTrue(set(result["queued"]).issubset(result["cancelled"]), result)
        self.assertEqual(result["pending"], [], result)
        self.assertEqual(result["urls"], 0, result)
        self.assertFalse(result["editorOpen"], result)
        await self.page.locator("#stylePresetNew").click()
        await self.page.locator("#stylePresetName").fill("等待解码的旧会话")
        await self.page.evaluate("""() => {
          const decode=HTMLImageElement.prototype.decode;
          window.edgeDecodeStarted=false;
          HTMLImageElement.prototype.decode=function(){
            const decoded=decode.call(this);edgeDecodeStarted=true;
            return decoded.then(()=>new Promise(resolve=>{window.edgeReleaseDecode=resolve}));
          };
          window.edgeRestoreDecode=()=>{HTMLImageElement.prototype.decode=decode};
        }""")
        await self.page.locator("#stylePresetCoverFile").set_input_files(cover_file("pending.png"))
        await self.page.wait_for_function("edgeDecodeStarted && typeof edgeReleaseDecode==='function'")
        await self.page.locator("#stylePresetEditor .spm-heading button").click()
        await self.page.locator("#stylePresetConfirmAccept").click()
        self.assertEqual(await self.page.evaluate("edgeURLs.size"), 0)
        await self.page.locator("#stylePresetNew").click()
        await self.page.locator("#stylePresetName").fill("新的草稿")
        await self.page.evaluate("async()=>{edgeReleaseDecode();edgeRestoreDecode();await Promise.resolve();await Promise.resolve();}")
        await self.expect(self.page.locator("#stylePresetName")).to_have_value("新的草稿")
        await self.expect(self.page.locator("#stylePresetCropStage > img")).to_have_count(0)
        self.assertEqual(await self.page.evaluate("edgeURLs.size"), 0)
        self.assertEqual(self.store.list("anima")["count"], 0)

    async def test_confirm_exit_editor_reopen_keeps_new_focus_and_modal_layers(self):
        settings = await self.page.evaluate("readSettings()")
        for name in ("第一个风格", "新的风格"):
            await self.store.create({"name": name, "settings": settings})
        next_item = self.store.list("anima")["items"][0]
        await self.page.emulate_media(reduced_motion="no-preference")
        await self.open_library()
        await self.page.locator(".spm-card-open").last.click()
        await self.page.wait_for_function("document.querySelector('#stylePresetEditor').dataset.motionState==='open'")
        await self.page.locator("#stylePresetName").fill("尚未保存")
        await self.page.locator("#stylePresetEditor .spm-heading button").click()
        await self.page.wait_for_function("document.querySelector('#stylePresetConfirm').dataset.motionState==='open'")
        audit = await self.page.evaluate("""async item => {
          document.querySelector('#stylePresetConfirmAccept').click();
          await Promise.resolve(); await Promise.resolve();
          const editor=document.querySelector('#stylePresetEditor'),confirm=document.querySelector('#stylePresetConfirm');
          const closing={editor:StudioMotion.target(editor),confirm:StudioMotion.target(confirm),confirmOpen:confirm.open};
          void StylePresetManager.edit(item);
          await Promise.resolve();
          return closing;
        }""", next_item)
        self.assertEqual(audit, {"editor": False, "confirm": False, "confirmOpen": True})
        await self.page.wait_for_function("document.querySelector('#stylePresetEditor').dataset.motionState==='open' && !document.querySelector('#stylePresetConfirm').open")
        await self.expect(self.page.locator("#stylePresetName")).to_have_value(next_item["name"])
        await self.expect(self.page.locator("#stylePresetName")).to_be_focused()
        layers = await self.page.locator(".spm-dialog[open]").evaluate_all("nodes=>nodes.map(el=>({id:el.id,layer:el.dataset.spmLayer,covered:el.classList.contains('is-covered')}))")
        self.assertEqual(layers, [{"id": "stylePresetManager", "layer": "base", "covered": True}, {"id": "stylePresetEditor", "layer": "nested", "covered": False}])
        await self.page.locator("#stylePresetEditor .spm-heading button").click()
        await self.expect(self.page.locator("#stylePresetEditor")).to_be_hidden()
        await self.expect(self.page.locator(".spm-card-open").last).to_be_focused()
