import asyncio
import copy
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import pytest
from aiohttp.test_utils import TestClient, TestServer

from anima_webui.comfy import ComfyError
from anima_webui.favorites import FavoritesExtensionStore, FavoritesService
from anima_webui.prompt_pools import PromptPoolStore
from anima_webui.server import CUSTOM_PROMPTS_KEY, PROMPT_POOLS_KEY, FAVORITES_KEY, create_app
from test_server import APP_DIR, FakeComfy, _write_fake_tools


class ProfileFavoritesApiTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.comfy = FakeComfy()
        self.app = create_app(
            app_dir=APP_DIR, data_dir=self.root / "data", comfy=self.comfy,
            anima_tools_dir=_write_fake_tools(self.root),
        )
        self.client = TestClient(TestServer(self.app))
        await self.client.start_server()

    async def asyncTearDown(self):
        await self.client.close()
        self.temp.cleanup()

    async def test_anima_text_is_retryable_and_merges_groups_without_renaming_source(self):
        groups = []
        for name in ("First", "Second"):
            response = await self.client.post("/api/favorites/pose/groups", json={"name": name})
            groups.append((await response.json())["group"]["id"])
        body = {"title": "Standing memo", "subtitle": "Rainy pose", "text": " Standing in rain ", "groupIds": groups[:1]}
        first = await self.client.post("/api/favorites/pose/text", json=body)
        self.assertEqual(first.status, 200)
        first = await first.json()
        self.assertEqual(first["model_profile"], "anima")
        body.update(title="New favorite name", groupIds=groups[1:], text="standing in rain")
        second = await self.client.post("/api/favorites/pose/text?model_profile=anima", json=body)
        second = await second.json()
        self.assertEqual(second["item_id"], first["item_id"])
        self.assertFalse(second["created"])
        self.assertEqual(len(second["items"]), 1)
        self.assertEqual(second["items"][0]["groupIds"], groups)
        self.assertEqual(second["items"][0]["nickname"], "Standing memo")
        custom = self.app[CUSTOM_PROMPTS_KEY].list("pose")
        self.assertEqual(len(custom), 1)
        self.assertEqual(custom[0]["title"], "Standing memo")
        self.assertEqual(custom[0]["subtitle"], "Rainy pose")
        visible = await (await self.client.get(f"/api/pools/pose?collection={groups[1]}")).json()
        self.assertIn(first["item_id"], [item["id"] for item in visible["items"]])

    async def test_anima_upstream_timeout_preserves_source_and_retry_deduplicates(self):
        body = {"title": "Saved word", "text": "standing in rain"}
        original_save = self.comfy.save_favorites

        async def commit_then_timeout(payload):
            await original_save(payload)
            raise ComfyError("reply lost")

        with patch.object(self.comfy, "save_favorites", side_effect=commit_then_timeout):
            response = await self.client.post("/api/favorites/pose/text", json=body)
        partial = await response.json()
        self.assertEqual(response.status, 502)
        self.assertEqual(partial["code"], "favorite_save_pending")
        self.assertTrue(partial["source_saved"])
        self.assertEqual(len(self.app[CUSTOM_PROMPTS_KEY].list("pose")), 1)
        self.assertEqual(len(self.comfy.favorites_data["pose"]["items"]), 1)
        response = await self.client.post("/api/favorites/pose/text", json=body)
        result = await response.json()
        self.assertEqual(response.status, 200)
        self.assertEqual(result["item_id"], partial["item_id"])
        self.assertEqual(len(result["items"]), 1)
        self.assertEqual(len(self.app[CUSTOM_PROMPTS_KEY].list("pose")), 1)

    async def test_invalid_groups_and_cross_profile_sections_do_not_write(self):
        before = copy.deepcopy(self.comfy.favorites_data)
        for path in (
            "/api/favorites/pose/text?model_profile=krea2",
            "/api/favorites/prompt/text?model_profile=anima",
            "/api/favorites/pose/text?model_profile=unknown",
            "/api/favorites/artist/text?model_profile=anima",
        ):
            response = await self.client.post(path, json={"title": "Name", "text": "word"})
            self.assertEqual(response.status, 400, path)
        response = await self.client.post(
            "/api/favorites/pose/text", json={"title": "Name", "text": "word", "groupIds": ["missing"]}
        )
        self.assertEqual(response.status, 400)
        self.assertEqual(self.app[CUSTOM_PROMPTS_KEY].list(), [])
        self.assertEqual(self.comfy.favorites_data, before)
        response = await self.client.get("/api/favorites/artist?model_profile=krea2")
        self.assertEqual(response.status, 400)
        response = await self.client.put(
            "/api/favorites/artist/item?model_profile=krea2", json={"name": "test", "favorite": True}
        )
        self.assertEqual(response.status, 400)
        self.assertEqual(self.comfy.favorites_data, before)

    async def test_krea_snapshots_and_groups_are_local_and_survive_restart(self):
        before = copy.deepcopy(self.comfy.favorites_data)
        path = "/api/favorites/prompt"
        query = "?model_profile=krea2"
        with patch.object(self.comfy, "favorites", side_effect=AssertionError("Krea accessed Anima")):
            group = await self.client.post(path + "/groups" + query, json={"name": "Scenes"})
            group_id = (await group.json())["group"]["id"]
            response = await self.client.post(
                path + "/text" + query,
                json={"title": "A scene", "subtitle": "Morning fog", "text": "A misty forest", "groupIds": [group_id]},
            )
            result = await response.json()
            item_id = result["item_id"]
            source_id = result["source_item_id"]
            self.assertNotEqual(item_id, source_id)
            source = self.app[PROMPT_POOLS_KEY].all_items("krea2")[0]
            self.assertEqual(source["title"], "A scene")
            self.assertEqual(source["subtitle"], "Morning fog")
            duplicate = await self.client.post(
                path + "/text" + query, json={"title": "Forest", "text": " a MISTY forest "}
            )
            duplicate = await duplicate.json()
            self.assertEqual(duplicate["item_id"], item_id)
            self.assertEqual(duplicate["items"][0]["groupIds"], [group_id, "default"])
            self.assertEqual(duplicate["items"][0]["title"], "A scene")
            response = await self.client.put(path + f"/groups/{group_id}" + query, json={"name": "Landscapes"})
            self.assertEqual(response.status, 200)
        restored = FavoritesService(
            self.comfy, None, FavoritesExtensionStore(self.root / "data" / "favorite_extensions.json"), "krea2"
        )
        saved = await restored.get("prompt")
        self.assertEqual(saved["favorite_keys"], [item_id])
        self.assertEqual(saved["items"][0]["text"], "A misty forest")
        self.assertEqual(saved["items"][0]["subtitle"], "Morning fog")
        await self.app[PROMPT_POOLS_KEY].delete_item("krea2", source_id)
        self.assertEqual((await restored.get("prompt"))["items"][0]["id"], item_id)
        self.assertEqual(saved["groups"][1]["name"], "Landscapes")
        self.assertEqual(self.comfy.favorites_data, before)
        response = await self.client.delete(path + f"/groups/{group_id}" + query)
        self.assertEqual((await response.json())["items"][0]["groupIds"], ["default"])
        response = await self.client.put(path + "/item" + query, json={"id": item_id, "favorite": False})
        self.assertEqual((await response.json())["items"], [])
        self.assertEqual(self.comfy.favorites_data, before)

    async def test_ensure_api_preserves_existing_create_conflict_contract(self):
        body = {"model_profile": "krea2", "text": "A scene"}
        first = await self.client.post("/api/prompt-pools/items/ensure", json=body)
        first = await first.json()
        second = await self.client.post(
            "/api/prompt-pools/items/ensure", json={**body, "text": " a SCENE "}
        )
        second = await second.json()
        self.assertTrue(first["created"])
        self.assertFalse(second["created"])
        self.assertEqual(first["id"], second["id"])
        self.assertEqual(second["text"], "A scene")
        conflict = await self.client.post("/api/prompt-pools/items", json=body)
        self.assertEqual(conflict.status, 400)

    async def test_krea_validates_groups_before_source_and_retries_partial_save(self):
        path = "/api/favorites/prompt/text?model_profile=krea2"
        body = {"title": "Sunset", "subtitle": "Evening light", "text": "A golden sunset"}
        rejected = await self.client.post(path, json={**body, "groupIds": ["missing"]})
        self.assertEqual(rejected.status, 400)
        self.assertEqual(self.app[PROMPT_POOLS_KEY].all_items("krea2"), [])
        extension = self.app[FAVORITES_KEY].expression_store
        with patch.object(extension, "save", side_effect=OSError("temporary write failure")):
            pending = await self.client.post(path, json=body)
        pending_body = await pending.json()
        self.assertEqual(pending.status, 502)
        self.assertEqual(pending_body["code"], "favorite_save_pending")
        self.assertTrue(pending_body["source_saved"])
        self.assertEqual(len(self.app[PROMPT_POOLS_KEY].all_items("krea2")), 1)
        retried = await self.client.post(path, json=body)
        result = await retried.json()
        self.assertEqual(retried.status, 200)
        self.assertEqual(result["source_item_id"], pending_body["item_id"])
        self.assertEqual(result["items"][0]["subtitle"], "Evening light")
        self.assertEqual(len(result["items"]), 1)
        self.assertEqual(len(self.app[PROMPT_POOLS_KEY].all_items("krea2")), 1)

    async def test_krea_read_only_stores_never_publish_half_created_favorites(self):
        path = "/api/favorites/prompt/text?model_profile=krea2"
        body = {"title": "Sunset", "subtitle": "Evening", "text": "A sunset"}
        extension = self.app[FAVORITES_KEY].expression_store
        pool = self.app[PROMPT_POOLS_KEY]
        extension._protect_storage("test protection")
        response = await self.client.post(path, json=body)
        self.assertEqual(response.status, 503)
        self.assertEqual((await response.json())["store"], "favorite_extensions")
        self.assertEqual(pool.all_items("krea2"), [])
        extension.read_only_reason = None
        pool._protect_storage("test protection")
        response = await self.client.post(path, json=body)
        self.assertEqual(response.status, 503)
        self.assertEqual((await response.json())["store"], "prompt_pools")
        self.assertEqual(extension.get("krea2", "prompt")["items"], [])

    async def test_krea_bulk_favorites_are_independent_snapshots_and_atomic(self):
        pool = self.app[PROMPT_POOLS_KEY]
        sources = [await pool.ensure_item("krea2", f"Scene {number}", title=f"Title {number}", subtitle="Description") for number in range(2)]
        path = "/api/favorites/prompt"
        query = "?model_profile=krea2"
        group = await self.client.post(path + "/groups" + query, json={"name": "Collection"})
        group_id = (await group.json())["group"]["id"]
        invalid = await self.client.post(path + "/selection" + query, json={"selection": {"mode": "include", "ids": [sources[0]["id"], "missing"]}, "groupIds": [group_id]})
        self.assertEqual(invalid.status, 400)
        self.assertEqual((await (await self.client.get(path + query)).json())["items"], [])
        response = await self.client.post(path + "/selection" + query, json={"selection": {"mode": "all", "excluded_ids": []}, "groupIds": [group_id]})
        result = await response.json()
        self.assertEqual(response.status, 200)
        self.assertEqual(result["createdCount"], 2)
        self.assertTrue(all(item["id"].startswith("favorite_") for item in result["items"]))
        self.assertEqual(result["items"][0]["subtitle"], "Description")
        repeated = await self.client.post(path + "/selection" + query, json={"selection": {"mode": "include", "ids": [sources[0]["id"]]}, "groupIds": ["default"]})
        result = await repeated.json()
        self.assertEqual(result["createdCount"], 0)
        self.assertEqual(result["updatedCount"], 1)
        await pool.delete_item("krea2", sources[0]["id"])
        saved = await (await self.client.get(path + query)).json()
        self.assertEqual(len(saved["items"]), 2)
        self.assertEqual(saved["items"][0]["text"], "Scene 0")

    async def test_krea_custom_group_subtree_import_uses_existing_favorite_route(self):
        pool = self.app[PROMPT_POOLS_KEY]
        source_group = (await pool.create_group("krea2", {"name": "Nature"}))["group"]
        source_child = (await pool.create_group("krea2", {"name": "Forests", "parentId": source_group["id"]}))["group"]
        source = await pool.ensure_item("krea2", "A foggy forest", title="Forest", subtitle="At dawn", group_ids=[source_child["id"]])
        path = "/api/favorites/prompt/groups"
        query = "?model_profile=krea2"
        parent = (await (await self.client.post(path + query, json={"name": "Favorite scenes"})).json())["group"]
        response = await self.client.post(f"{path}/{parent['id']}/children/import{query}", json={"customGroupId": source_group["id"]})
        result = await response.json()
        self.assertEqual(response.status, 201, result)
        self.assertEqual(result["group"]["sourceCustomGroupId"], source_group["id"])
        self.assertEqual(result["group"]["parentId"], parent["id"])
        self.assertEqual(result["items"][0]["title"], "Forest")
        self.assertEqual(result["items"][0]["subtitle"], "At dawn")
        self.assertNotEqual(result["items"][0]["id"], source["id"])
        duplicate = await self.client.post(f"{path}/{parent['id']}/children/import{query}", json={"customGroupId": source_group["id"]})
        self.assertEqual(duplicate.status, 400)

    async def test_duplicate_text_preserves_renamed_favorite(self):
        for section, profile in (("pose", "anima"), ("prompt", "krea2")):
            path = f"/api/favorites/{section}"
            query = f"?model_profile={profile}"
            original = await self.client.post(path + "/text" + query, json={"title": "Original", "text": "Same text"})
            item_id = (await original.json())["item_id"]
            updated = await self.client.put(path + "/item" + query, json={"id": item_id, "nickname": "Renamed"})
            self.assertEqual(updated.status, 200)
            repeated = await self.client.post(path + "/text" + query, json={"title": "Accidental title", "text": "SAME TEXT"})
            result = await repeated.json()
            self.assertEqual(result["item_id"], item_id)
            self.assertEqual(result["items"][0]["nickname"], "Renamed")

    async def test_capacity_rejection_happens_before_creating_anima_source(self):
        with patch("anima_webui.favorites.MAX_ITEMS", 1):
            first = await self.client.post("/api/favorites/pose/text", json={"title": "First", "text": "First text"})
            self.assertEqual(first.status, 200)
            second = await self.client.post("/api/favorites/pose/text", json={"title": "Second", "text": "Second text"})
            self.assertEqual(second.status, 400)
            self.assertEqual(len(self.app[CUSTOM_PROMPTS_KEY].list("pose")), 1)
            again = await self.client.post("/api/favorites/pose/text", json={"title": "Alias", "text": "First text"})
            self.assertEqual(again.status, 200)


def test_extension_v1_migrates_with_backup_and_failed_backup_preserves_source(tmp_path):
    original = {"version": 1, "expression": {
        "groups": [{"id": "default", "name": "Default"}],
        "items": [{"id": "custom:one", "nickname": "Keep", "groupIds": ["default"]}],
    }}
    path = tmp_path / "favorites.json"
    path.write_text(json.dumps(original), encoding="utf-8")
    before = path.read_bytes()
    store = FavoritesExtensionStore(path)
    assert store.value["items"][0]["nickname"] == "Keep"
    assert path.with_name(path.name + ".v2.bak").read_bytes() == before
    migrated = json.loads(path.read_text(encoding="utf-8"))
    assert migrated["version"] == 2
    assert migrated["profiles"]["anima"]["expression"]["items"][0]["id"] == "custom:one"

    second = tmp_path / "protected.json"
    second.write_bytes(before)
    with patch("anima_webui.favorites.backup_for_migration", side_effect=PermissionError("locked")):
        protected = FavoritesExtensionStore(second)
    assert protected.value["items"][0]["nickname"] == "Keep"
    assert protected.storage_status()["read_only"]
    assert second.read_bytes() == before


@pytest.mark.parametrize("version", [[], {}, None, "2"])
def test_invalid_extension_version_is_recovered_without_startup_crash(tmp_path, version):
    path = tmp_path / "favorites.json"
    path.write_text(json.dumps({"version": version}), encoding="utf-8")
    before = path.read_bytes()
    store = FavoritesExtensionStore(path)
    assert store.value["items"] == []
    assert store.load_warnings
    assert path.with_name(path.name + ".corrupt.bak").read_bytes() == before


def test_source_creation_disk_failure_never_publishes_ghost_item(tmp_path):
    from anima_webui.catalog import PromptCatalog
    from anima_webui.custom_prompts import CustomPromptStore

    catalog = PromptCatalog(APP_DIR, _write_fake_tools(tmp_path))
    path = tmp_path / "custom.json"
    store = CustomPromptStore(path, catalog)
    before = catalog.count("pose")

    async def run():
        with patch.object(store, "_write_text", side_effect=OSError("disk unavailable")):
            with pytest.raises(OSError):
                await store.create_or_reuse({"section": "pose", "title": "Word", "prompt": "new word"})
        assert store.list() == []
        assert catalog.count("pose") == before
        assert not path.exists()
        first, created = await store.create_or_reuse({"section": "pose", "title": "Word", "prompt": "new word"})
        second, repeated = await store.create_or_reuse({"section": "pose", "title": "Alias", "prompt": "NEW WORD"})
        assert created and not repeated
        assert first["id"] == second["id"]

    asyncio.run(run())


def test_concurrent_profiles_and_pool_ensure_do_not_lose_updates(tmp_path):
    extension = FavoritesExtensionStore(tmp_path / "favorites.json")
    krea = FavoritesService(None, None, extension, "krea2")
    pool = PromptPoolStore(tmp_path / "pool.json")

    async def run():
        await asyncio.gather(
            *(krea.save_text("prompt", {"title": f"Name {i}", "text": f"Text {i}"}, prompt_pools=pool) for i in range(12)),
            extension.save({"groups": [], "items": [{"id": "expression", "groupIds": ["default"]}]}),
        )
        assert len((await krea.get("prompt"))["items"]) == 12
        assert extension.value["items"][0]["id"] == "expression"
        results = await asyncio.gather(*(pool.ensure_item("krea2", "Same text") for _ in range(12)))
        assert sum(result["created"] for result in results) == 1
        assert pool.resolve("krea2").count("Same text") == 1
        assert len(pool.resolve("krea2")) == 13

    asyncio.run(run())


def test_krea_custom_group_import_copies_metadata_and_preserves_existing_snapshot(tmp_path):
    extension = FavoritesExtensionStore(tmp_path / "favorites.json")
    favorites = FavoritesService(None, None, extension, "krea2")
    sources = [{"id": "pool-one", "title": "Forest", "subtitle": "Morning fog", "prompt": "A forest"}]

    async def run():
        parent = (await favorites.create_group("prompt", {"name": "Landscapes"}))["group"]
        result = await favorites.import_custom_group("prompt", parent["id"], {"id": "source-one", "name": "Nature"}, sources)
        child = result["group"]
        item = result["items"][0]
        assert child["parentId"] == parent["id"]
        assert item["id"] != sources[0]["id"]
        assert item["title"] == "Forest" and item["subtitle"] == "Morning fog"
        assert item["groupIds"] == [child["id"]]
        sources[0].update(title="Changed source title", subtitle="Changed source subtitle")
        result = await favorites.import_custom_group("prompt", parent["id"], {"id": "source-two", "name": "Nature copy"}, sources)
        assert len(result["items"]) == 1
        assert result["items"][0]["title"] == "Forest"
        assert result["items"][0]["subtitle"] == "Morning fog"
        assert result["items"][0]["groupIds"] == [child["id"], result["group"]["id"]]
        restored = FavoritesService(None, None, FavoritesExtensionStore(extension.path), "krea2")
        assert (await restored.get("prompt"))["items"][0]["text"] == "A forest"

    asyncio.run(run())
