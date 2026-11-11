"""Narrow preset-fade acceptance with temporary data, FakeComfy and local Chrome.

Opt in with ANIMA_BROWSER_TESTS=1 ANIMA_BROWSER_CHANNEL=chrome. This deliberately
protects the existing compact typography; it is not a whole-site UI audit.
"""
from __future__ import annotations

import importlib
import json
import os
from pathlib import Path
import sys
import unittest
from unittest import mock


sys.path.insert(0, str(Path(__file__).resolve().parent))
# Do not import/alias its TestCase, which would duplicate pytest collection.
_fixture = importlib.import_module("test_workbench_races")
_ARTIFACTS = _fixture.APP_DIR / "output" / "playwright" / "preset-fade-only"

_AUDIT = """() => {
  const list=document.querySelector('#presetList'), wrapper=document.querySelector('#presetScroll');
  const rows=[...list.querySelectorAll('.preset-row')], s=getComputedStyle(list), r=list.getBoundingClientRect();
  const first=rows[0], action=first?.querySelector('.preset-actions button');
  return {count:rows.length,top:list.scrollTop,maximum:Math.max(0,list.scrollHeight-list.clientHeight),
    height:list.clientHeight,gap:parseFloat(s.rowGap)||0,rows:rows.map(el=>el.getBoundingClientRect().height),
    fadeTop:parseFloat(s.getPropertyValue('--preset-fade-top'))||0,
    fadeBottom:parseFloat(s.getPropertyValue('--preset-fade-bottom'))||0,
    nativeBar:list.offsetWidth-list.clientWidth,stripe:parseFloat(s.getPropertyValue('--preset-scrollbar-width'))||0,
    gutter:parseFloat(s.getPropertyValue('--preset-overlay-gutter'))||0,
    contentRight:rows.length?Math.max(...rows.map(el=>el.getBoundingClientRect().right)):r.right,listRight:r.right,
    mask:s.maskImage,before:getComputedStyle(wrapper,'::before').content,after:getComputedStyle(wrapper,'::after').content,
    typography:first?{title:parseFloat(getComputedStyle(first.querySelector('strong')).fontSize),
      summary:parseFloat(getComputedStyle(first.querySelector('small')).fontSize),
      buttonWidth:action.getBoundingClientRect().width,buttonHeight:action.getBoundingClientRect().height}:null,
    viewport:innerWidth,documentWidth:document.documentElement.scrollWidth};
}"""

_PERFORMANCE = """async () => {
  const list=document.querySelector('#presetList'), rect=Element.prototype.getBoundingClientRect;
  const height=Object.getOwnPropertyDescriptor(HTMLElement.prototype,'offsetHeight');
  let reads=0;const timestamps=[],longTasks=[];
  Element.prototype.getBoundingClientRect=function(...args){if(this.classList?.contains('preset-row'))reads++;return rect.apply(this,args);};
  Object.defineProperty(HTMLElement.prototype,'offsetHeight',{...height,get(){if(this.classList?.contains('preset-row'))reads++;return height.get.call(this);}});
  const observer=new PerformanceObserver(entries=>longTasks.push(...entries.getEntries().map(x=>x.duration)));
  observer.observe({type:'longtask'});
  try {
    const max=list.scrollHeight-list.clientHeight;
    for(let i=0;i<36;i++){timestamps.push(await new Promise(requestAnimationFrame));list.scrollTop=max*(.5-.5*Math.cos(i/35*Math.PI*2));}
    await new Promise(requestAnimationFrame);
    const times=timestamps.slice(1).map((t,i)=>t-timestamps[i]).sort((a,b)=>a-b);
    return {frames:timestamps.length,rowGeometryReads:reads,longTasks,frameMs:{p50:times[Math.floor(times.length*.5)],p95:times[Math.floor(times.length*.95)],max:times.at(-1)}};
  } finally {Element.prototype.getBoundingClientRect=rect;Object.defineProperty(HTMLElement.prototype,'offsetHeight',height);observer.disconnect();}
}"""


@unittest.skipUnless(os.environ.get("ANIMA_BROWSER_TESTS") == "1", "set ANIMA_BROWSER_TESTS=1 for Chrome acceptance")
class PresetScrollTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        _ARTIFACTS.mkdir(parents=True, exist_ok=True)
        api = importlib.import_module("playwright.async_api")
        launch = api.BrowserType.launch

        async def visible_scrollbars(browser_type, **kwargs):
            kwargs["ignore_default_args"] = ["--hide-scrollbars"]
            if "overlay" in self._testMethodName:
                kwargs["args"] = ["--enable-features=OverlayScrollbar"]
            return await launch(browser_type, **kwargs)

        with mock.patch.object(api.BrowserType, "launch", visible_scrollbars):
            await _fixture.WorkbenchRaceTests.asyncSetUp(self)
        self.cdp = await self.context.new_cdp_session(self.page)
        self.addAsyncCleanup(self.cdp.detach)

    async def asyncTearDown(self):
        for gate in self.gates:
            gate.release.set()
        if hasattr(self, "page") and not self.page.is_closed():
            await self.page.screenshot(path=str(_ARTIFACTS / f"{self._testMethodName}.png"))
        if hasattr(self, "context"):
            await self.context.tracing.stop(path=str(_ARTIFACTS / f"{self._testMethodName}.zip"))
        self.assertEqual(self.comfy.submissions, [], "preset scroll checks must never submit GPU work")
        self.assertNotIn(("POST", "/api/batches"), self.requests)
        self.assertEqual(self.page_errors, [])

    open_presets = _fixture.WorkbenchRaceTests.open_presets
    switch_profile = _fixture.WorkbenchRaceTests.switch_profile

    async def settled(self):
        await self.page.evaluate("async () => {await document.fonts.ready;for(let i=0;i<6;i++)await new Promise(requestAnimationFrame);}")

    async def seed(self, count, profile="anima"):
        store = self.app[_fixture.STYLE_PRESETS_KEY]
        for index in range(store.list(profile)["count"], count):
            await store.create({"name": f"柔光与纸感 {index + 1:02d}", "model_profile": profile,
                                "favorite": index < 2, "settings": {"model_profile": profile, "pipeline_version": 5}})
        await self.page.evaluate("loadStylePresets()")
        await self.settled()

    async def theme(self, value):
        if await self.page.locator("html").get_attribute("data-theme") != value:
            await self.page.locator("#themeToggle").click()
        await self.settled()

    async def position(self, value):
        await self.page.locator("#presetList").evaluate("(el,value)=>{el.scrollTop=value}", value)
        await self.page.evaluate("() => new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))")
        return await self.page.evaluate(_AUDIT)

    def assert_compact_style(self, audit):
        self.assertTrue(all(abs(height - 52) < .1 for height in audit["rows"]), audit)
        if audit["count"]:
            self.assertEqual(audit["typography"], {"title": 11.5, "summary": 8.5, "buttonWidth": 27, "buttonHeight": 27})
        self.assertLessEqual(audit["documentWidth"], audit["viewport"] + 1, audit)
        self.assertEqual((audit["before"], audit["after"]), ("none", "none"), "old color-cover pseudo-elements must be absent")

    async def performance(self):
        await self.settled()  # Exclude initialization/font/ResizeObserver measurements.
        await self.cdp.send("Performance.enable")
        before = {entry["name"]: entry["value"] for entry in (await self.cdp.send("Performance.getMetrics"))["metrics"]}
        result = await self.page.evaluate(_PERFORMANCE)
        after = {entry["name"]: entry["value"] for entry in (await self.cdp.send("Performance.getMetrics"))["metrics"]}
        result["layoutCount"] = after["LayoutCount"] - before["LayoutCount"]
        self.assertEqual(result["rowGeometryReads"], 0, result)
        return result

    async def test_classic_counts_edges_focus_preferences_and_performance(self):
        await self.open_presets()
        counts = []
        for count in (0, 1, 6, 7, 50):
            await self.seed(count)
            audit = await self.position(0)
            counts.append(audit)
            self.assertEqual(audit["count"], count)
            self.assert_compact_style(audit)
            if count <= 6:
                self.assertEqual((audit["maximum"], audit["fadeTop"], audit["fadeBottom"]), (0, 0, 0))
            else:
                self.assertAlmostEqual(audit["height"], sum(audit["rows"][:6]) + 6 * audit["gap"] + 24, delta=1)
                self.assertEqual((audit["fadeTop"], audit["fadeBottom"]), (0, 24))
        self.assertGreater(audit["nativeBar"], 0, "classic Chrome must expose its scrollbar in this fixture")
        self.assertEqual(audit["stripe"], audit["nativeBar"])
        await self.theme("dark")
        edges = []
        maximum = audit["maximum"]
        for label, value in (("top", 0), ("near-top", 6), ("middle", maximum / 2), ("near-bottom", maximum - 6), ("bottom", maximum)):
            audit = await self.position(value)
            edges.append({"position": label, **audit})
            if label in ("top", "middle", "bottom"):
                await self.page.locator("#presetScroll").screenshot(path=str(_ARTIFACTS / f"dark-{label}.png"))
        self.assertEqual([(entry["fadeTop"], entry["fadeBottom"]) for entry in edges], [(0, 24), (6, 24), (24, 24), (24, 6), (24, 0)])
        performance = await self.performance()
        await self.page.keyboard.press("Tab")
        focus = []
        for index in (49, 24, 0):
            await self.page.locator(".preset-row").nth(index).locator("[data-preset-delete]").focus()
            await self.settled()
            result = await self.page.evaluate("""() => {const list=document.querySelector('#presetList'),s=getComputedStyle(list),r=list.getBoundingClientRect(),a=document.activeElement.getBoundingClientRect();return {top:a.top-r.top,bottom:r.bottom-a.bottom,fadeTop:parseFloat(s.getPropertyValue('--preset-fade-top')),fadeBottom:parseFloat(s.getPropertyValue('--preset-fade-bottom'))};}""")
            focus.append(result)
            self.assertGreaterEqual(result["top"] + 1, result["fadeTop"], result)
            self.assertGreaterEqual(result["bottom"] + 1, result["fadeBottom"], result)
        await self.page.locator("#presetList").focus()
        outline = await self.page.locator("#presetScroll").evaluate("el=>{const s=getComputedStyle(el);return {style:s.outlineStyle,width:parseFloat(s.outlineWidth)}}")
        self.assertEqual(outline, {"style": "solid", "width": 2})
        preferences = []
        for name, value in (("prefers-contrast", "more"), ("prefers-reduced-transparency", "reduce"), ("forced-colors", "active")):
            await self.cdp.send("Emulation.setEmulatedMedia", {"features": [{"name": name, "value": value}]})
            await self.settled()
            audit = await self.page.evaluate(_AUDIT)
            self.assertEqual(audit["mask"], "none", (name, audit))
            self.assert_compact_style(audit)
            preferences.append({"feature": name, "mask": audit["mask"]})
        await self.cdp.send("Emulation.setEmulatedMedia", {"features": []})
        self.write_report("classic-scroll.json", {"counts": counts, "edges": edges, "focus": focus, "preferences": preferences, "performance": performance})

    async def test_selected_profiles_workbenches_themes_and_mobile(self):
        await self.seed(7)
        await self.seed(7, "krea2")
        records = []
        for profile, workbench, theme, width in (("anima", "random", "dark", 1440), ("anima", "single", "light", 390),
                                                ("krea2", "random", "light", 1440), ("krea2", "single", "dark", 390)):
            await self.page.set_viewport_size({"width": width, "height": 1000 if width == 1440 else 844})
            await self.page.locator(f"#{workbench}WorkbenchTab").click()
            if workbench == "single":
                await self.page.locator("#singleCategory_common").click()
            await self.switch_profile(profile)
            if workbench == "single":
                await self.page.locator("#singleCategory_more").click()
            await self.open_presets()
            await self.theme(theme)
            audit = await self.position(0)
            self.assert_compact_style(audit)
            self.assertEqual(audit["count"], 7)
            self.assertEqual((audit["fadeTop"], audit["fadeBottom"]), (0, 24))
            label = f"{profile}-{workbench}-{theme}-{width}"
            records.append({"case": label, **audit})
            await self.page.locator("#presetScroll").screenshot(path=str(_ARTIFACTS / f"{label}.png"))
        self.write_report("selected-contexts.json", records)

    async def test_overlay_scrollbar_gutter_and_performance(self):
        await self.seed(50)
        await self.open_presets()
        await self.theme("dark")
        audit = await self.position(0)
        self.assert_compact_style(audit)
        self.assertEqual(audit["nativeBar"], 0)
        self.assertEqual((audit["stripe"], audit["gutter"]), (12, 12))
        self.assertLessEqual(audit["contentRight"], audit["listRight"] - 12 + .5)
        self.assertIn("linear-gradient", audit["mask"])
        await self.page.locator("#presetScroll").screenshot(path=str(_ARTIFACTS / "dark-overlay.png"))
        self.write_report("overlay-scroll.json", {"geometry": audit, "performance": await self.performance()})

    @staticmethod
    def write_report(name, value):
        (_ARTIFACTS / name).write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")
