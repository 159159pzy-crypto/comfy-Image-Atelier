"""Favorite ordering acceptance using temporary stores and the real browser UI."""
from __future__ import annotations

import importlib
import os
from pathlib import Path
import sys
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))
jitter = importlib.import_module("test_style_preset_jitter")


@unittest.skipUnless(os.environ.get("ANIMA_BROWSER_TESTS") == "1", "opt in to Chrome acceptance")
class StylePresetFavoriteOrderTests(unittest.IsolatedAsyncioTestCase):
    asyncSetUp = jitter.StylePresetJitterTests.asyncSetUp
    asyncTearDown = jitter.StylePresetJitterTests.asyncTearDown
    open_presets = jitter.StylePresetJitterTests.open_presets
    open_library = jitter.StylePresetJitterTests.open_library
    settle = jitter.StylePresetJitterTests.settle
    idle = jitter.StylePresetJitterTests.idle
    begin_frames = jitter.StylePresetJitterTests.begin_frames
    end_frames = jitter.StylePresetJitterTests.end_frames
    frames = jitter.StylePresetJitterTests.frames
    assert_stable = jitter.StylePresetJitterTests.assert_stable

    async def seed_plain(self, *, tied=False):
        settings = await self.page.evaluate("readSettings()")
        names = ["Style 1", "Style 01", "Style 2", "Style 3", "Style 4", "Style 5", "Style 6", "Style 7"]
        for index, name in enumerate(names):
            timestamp = f"2026-11-01T00:00:{0 if tied else index:02d}+00:00"
            with mock.patch.object(self.store, "_now", return_value=timestamp):
                await self.store.create({"name": name, "settings": settings})
        await self.page.evaluate("loadStylePresets()")

    async def order(self, *, homepage=False):
        selector = "#presetList .preset-row" if homepage else "#stylePresetGrid .spm-card"
        return await self.page.locator(selector).evaluate_all("nodes => nodes.map(node => node.dataset.presetId)")

    async def homepage_toggle(self, preset_id, favorite):
        star = self.page.locator(f'#presetList [data-preset-id="{preset_id}"] [data-preset-favorite]')
        async with self.page.expect_response(lambda response: response.request.method == "PUT"
                                            and response.url.endswith(f"/api/style-presets/{preset_id}")) as written:
            await star.click()
        self.assertTrue((await written.value).ok)
        await self.page.wait_for_function("stylePresetWrites.size === 0")
        await self.expect(star).to_have_attribute("title", "取消收藏" if favorite else "收藏")
        await self.settle()

    async def manager_toggle(self, preset_id, favorite):
        star = self.page.locator(f'#stylePresetGrid [data-preset-id="{preset_id}"] .spm-star')
        async with self.page.expect_response(lambda response: response.request.method == "PUT"
                                            and response.url.endswith(f"/api/style-presets/{preset_id}")) as written:
            await star.click()
        self.assertTrue((await written.value).ok)
        await self.expect(star).to_have_attribute("aria-busy", "false")
        await self.expect(star).to_have_attribute("aria-pressed", "true" if favorite else "false")
        await self.idle()
        await self.expect(star).to_be_focused()

    async def test_homepage_latest_favorite_and_original_positions_survive_reload(self):
        await self.seed_plain()
        await self.open_presets()
        baseline = await self.order(homepage=True)
        original_times = {item["id"]: item["updated_at"] for item in self.store.items}
        first, second = baseline[-1], baseline[-3]
        await self.homepage_toggle(first, True)
        self.assertEqual(await self.order(homepage=True), [first, *[item for item in baseline if item != first]])
        await self.homepage_toggle(second, True)
        self.assertEqual(await self.order(homepage=True), [second, first, *[item for item in baseline if item not in (first, second)]])
        await self.homepage_toggle(first, False)
        self.assertEqual(await self.order(homepage=True), [second, *[item for item in baseline if item != second]])
        await self.homepage_toggle(first, True)
        self.assertEqual((await self.order(homepage=True))[:2], [first, second])
        self.store.reload()
        await self.page.reload()
        await self.page.wait_for_function("typeof initialized !== 'undefined' && initialized")
        await self.open_presets()
        self.assertEqual((await self.order(homepage=True))[:2], [first, second])
        await self.homepage_toggle(second, False)
        await self.homepage_toggle(first, False)
        self.assertEqual(await self.order(homepage=True), baseline)
        self.store.reload()
        await self.page.evaluate("loadStylePresets()")
        self.assertEqual(await self.order(homepage=True), baseline)
        self.assertEqual({item["id"]: item["updated_at"] for item in self.store.items}, original_times)

    async def test_manager_all_sorts_and_views_keep_order_with_tied_values(self):
        await self.seed_plain(tied=True)
        await self.open_library()
        await self.expect(self.page.locator("#stylePresetSort")).to_have_value("updated")
        self.assertEqual(await self.page.locator("#stylePresetSort option").evaluate_all("nodes => nodes.map(node => node.value)"),
                         ["updated", "created", "name"])
        original_times = {item["id"]: item["updated_at"] for item in self.store.items}
        for view in ("cards", "list"):
            await self.page.get_by_role("button", name="紧凑卡片" if view == "cards" else "列表", exact=True).click()
            for sort in ("updated", "created", "name"):
                with self.subTest(view=view, sort=sort):
                    await self.page.locator("#stylePresetSort").select_option(sort)
                    await self.idle()
                    before = await self.order()
                    preset_id = before[-3]
                    star = self.page.locator(f'#stylePresetGrid [data-preset-id="{preset_id}"] .spm-star')
                    await star.scroll_into_view_if_needed()
                    await star.focus()
                    await self.settle()
                    await self.begin_frames()
                    await self.manager_toggle(preset_id, True)
                    self.assertEqual((await self.order(homepage=True))[0], preset_id)
                    await self.frames()
                    await self.manager_toggle(preset_id, False)
                    await self.frames()
                    samples = await self.end_frames()
                    self.assert_stable(samples)
                    self.assertEqual(await self.order(), before)
        self.assertEqual({item["id"]: item["updated_at"] for item in self.store.items}, original_times)

    async def test_editor_and_batch_favorites_keep_management_order_and_sync_homepage(self):
        await self.seed_plain()
        await self.open_library()
        baseline = await self.order()
        original_times = {item["id"]: item["updated_at"] for item in self.store.items}
        target_id = baseline[-2]
        target = self.page.locator(f'#stylePresetGrid [data-preset-id="{target_id}"] .spm-card-open')
        for favorite in (True, False):
            await target.click()
            await self.expect(self.page.locator("#stylePresetEditor")).to_be_visible()
            await self.page.locator("#stylePresetFavorite").set_checked(favorite)
            await self.page.locator("#stylePresetSave").click()
            await self.expect(self.page.locator("#stylePresetEditor")).to_be_hidden()
            await self.idle()
            await self.expect(target).to_be_focused()
            self.assertEqual(await self.order(), baseline)
            self.assertEqual(self.store.get(target_id)["favorite"], favorite)
            self.assertEqual((await self.order(homepage=True))[0], target_id if favorite else baseline[0])
        await self.page.locator("#stylePresetSelect").click()
        selected = [baseline[-1], baseline[-3]]
        for preset_id in selected:
            await self.page.locator(f'#stylePresetGrid [data-preset-id="{preset_id}"] .spm-card-open').click()
        for action in ("favorite", "unfavorite"):
            button = self.page.locator(f'[data-batch-action="{action}"]')
            async with self.page.expect_response(lambda response: response.request.method == "POST"
                                                and response.url.endswith("/api/style-presets/batch")) as written:
                await button.click()
            self.assertTrue((await written.value).ok)
            await self.expect(button).to_be_enabled()
            await self.idle()
            self.assertEqual(await self.order(), baseline)
            self.assertEqual([self.store.get(preset_id)["favorite"] for preset_id in selected], [action == "favorite"] * 2)
            homepage = await self.order(homepage=True)
            if action == "favorite":
                self.assertEqual(set(homepage[:2]), set(selected))
            else:
                self.assertEqual(homepage, baseline)
        self.assertEqual({item["id"]: item["updated_at"] for item in self.store.items}, original_times)
