import copy
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from anima_webui.persistence import StorageReadOnlyError
from anima_webui.prompt_pools import PromptPoolStore
from anima_webui.runner import BatchManager
from anima_webui.workflow import WorkflowError


class StablePromptIdentityTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name) / "pools.json"
        self.store = PromptPoolStore(self.path)

    async def test_v3_migration_retains_ids_groups_and_exact_text_through_edit_and_reload(self):
        text = "Exact Case, punctuation.\nA second line."
        payload = {"version": 3, "profiles": {"krea2": {
            "items": [{"title": "Original", "subtitle": "Detail", "text": text, "groupIds": ["g"]}],
            "groups": [{"id": "g", "name": "Group", "parentId": None, "kind": "group"}],
        }}}
        original = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.path.write_bytes(original)
        store = PromptPoolStore(self.path)
        self.assertTrue(store.load_trusted)
        self.assertEqual(self.path.with_name("pools.json.v4.bak").read_bytes(), original)
        item = store.all_items("krea2")[0]
        old_id = PromptPoolStore.item_id(text)
        self.assertEqual((item["id"], item["text"], item["groupIds"]), (old_id, text, ["g"]))
        changed = await store.update_item("krea2", old_id, "Changed text", title="Changed")
        self.assertEqual(changed["id"], old_id)
        recreated = await store.create_item("krea2", text)
        self.assertNotEqual(recreated["id"], old_id)
        reloaded = PromptPoolStore(self.path)
        self.assertEqual(reloaded.resolve("krea2", [old_id]), ["Changed text"])
        self.assertEqual(reloaded.resolve_records("krea2", [old_id])[0]["id"], old_id)
        self.assertEqual(reloaded.all_items("krea2"), store.all_items("krea2"))

    async def test_ensure_append_replace_and_structured_replace_reuse_existing_identity(self):
        original = await self.store.create_item("krea2", "Original body", title="Old")
        for result in (
            await self.store.ensure_item("krea2", " ORIGINAL BODY "),
            (await self.store.ensure_items("krea2", [{"text": "original BODY"}]))["items"][0],
        ):
            self.assertEqual(result["id"], original["id"])
            self.assertFalse(result["created"])
        appended = await self.store.import_items("krea2", ["original body"], mode="append")
        self.assertEqual(appended["imported_ids"], [])
        replaced = await self.store.import_items("krea2", [{"text": "ORIGINAL BODY", "title": "New"}], mode="replace")
        self.assertEqual(replaced["valid_ids"], [original["id"]])
        preview = self.store.preview_structured_import("krea2", [{"prompt": "original body", "title": "Structured"}], "json", bundle_name="Replacement", mode="replace")
        result = await self.store.commit_structured_import("krea2", preview["rows"], bundle_name="Replacement", mode="replace")
        self.assertEqual(result["valid_ids"], [original["id"]])
        self.assertEqual(self.store.all_items("krea2")[0]["title"], "Structured")
        overwrite = self.store.preview_structured_import("krea2", [{"prompt": "original body", "title": "Overwritten"}], "json", bundle_name="Replacement")
        overwrite["rows"][0]["action"] = "overwrite"
        self.assertEqual(overwrite["rows"][0]["existingId"], original["id"])
        await self.store.commit_structured_import("krea2", overwrite["rows"], bundle_name="Replacement")
        self.assertEqual(self.store.all_items("krea2")[0]["id"], original["id"])

    async def test_multiple_favorites_follow_source_identity_and_never_reattach_by_text(self):
        source = await self.store.create_item("krea2", "Old text", title="Old")
        favorites = {"groups": [{"id": "root"}, {"id": "child", "parentId": "root"}], "items": [
            {"id": "favorite_a", "source_item_id": source["id"], "text": "Old text", "groupIds": ["root"]},
            {"id": "favorite_b", "source_item_id": source["id"], "text": "Old text", "groupIds": ["child"]},
            {"id": "legacy_unlinked", "text": "Old text", "groupIds": ["root"]},
        ]}
        before = copy.deepcopy(favorites)
        await self.store.update_item("krea2", source["id"], "Current text", title="Current")
        view = self.store.list("krea2", collection="child", favorites=favorites)
        self.assertEqual(view["count"], 1)
        item = view["items"][0]
        self.assertEqual((item["id"], item["text"], item["title"]), (source["id"], "Current text", "Current"))
        self.assertEqual(item["favorite_ids"], ["favorite_a", "favorite_b"])
        self.assertEqual(item["favorite_id"], "favorite_a")
        self.assertEqual(favorites, before)
        await self.store.delete_item("krea2", source["id"])
        replacement = await self.store.create_item("krea2", "Old text")
        self.assertNotEqual(replacement["id"], source["id"])
        self.assertEqual(self.store.list("krea2", collection="__all__", favorites=favorites)["count"], 0)
        self.assertFalse(self.store.list("krea2", favorites=favorites)["items"][0]["favorite"])

    async def test_runner_snapshot_records_durable_id_and_keeps_previous_snapshot(self):
        source = await self.store.create_item("krea2", "First text")
        manager = BatchManager.__new__(BatchManager)
        manager.prompt_pools, manager.prompt_rules = self.store, None
        settings = {"model_profile": "krea2", "krea_prompt_pool": {"enabled": True, "ids": [source["id"]]}}
        first = manager._resolve_prompt(settings, 7)
        await self.store.update_item("krea2", source["id"], "Updated text")
        second = manager._resolve_prompt(settings, 7)
        self.assertEqual(first["selected"]["krea_prompt_pool"], [{"id": source["id"], "text": "First text"}])
        self.assertEqual(second["selected"]["krea_prompt_pool"], [{"id": source["id"], "text": "Updated text"}])
        self.assertEqual(first["full_prompt"], "First text")
        self.assertEqual(second["full_prompt"], "Updated text")

    def test_invalid_persisted_ids_or_duplicate_rows_are_not_silently_migrated(self):
        for version, items in (
            (3, [{"text": "Same"}, {"text": "same"}]),
            (4, [{"id": "prompt_same", "text": "One"}, {"id": "prompt_same", "text": "Two"}]),
            (4, [{"text": "Missing ID"}]),
            (4, ["Unstructured"]),
        ):
            with self.subTest(version=version, items=items):
                payload = {"version": version, "profiles": {"krea2": {"items": items, "groups": []}}}
                original = json.dumps(payload).encode()
                path = self.path.with_name(f"invalid-{version}-{len(items)}-{len(original)}.json")
                path.write_bytes(original)
                store = PromptPoolStore(path)
                self.assertFalse(store.load_trusted)
                backups = list(path.parent.glob(path.name + ".corrupt*.bak"))
                self.assertEqual(backups[-1].read_bytes(), original)
                self.assertFalse(path.with_name(path.name + ".v4.bak").exists())
                self.assertEqual(path.read_bytes(), original)
                with self.assertRaises(StorageReadOnlyError):
                    store.ensure_writable()
                restarted = PromptPoolStore(path)
                self.assertFalse(restarted.load_trusted)
                self.assertEqual(restarted.load_state, "invalid")
                self.assertEqual(path.read_bytes(), original)
                with self.assertRaises(StorageReadOnlyError):
                    restarted.ensure_writable()

    async def test_invalid_source_backup_failure_keeps_source_and_explicit_repair_unlocks(self):
        original = b"{broken source"
        self.path.write_bytes(original)
        with patch("anima_webui.prompt_pools.shutil.copy2", side_effect=PermissionError("backup denied")):
            store = PromptPoolStore(self.path)
        self.assertFalse(store.load_trusted)
        self.assertIn("备份失败", store.read_only_reason)
        self.assertEqual(self.path.read_bytes(), original)
        self.assertEqual(list(self.path.parent.glob("pools.json.corrupt*.bak")), [])
        with self.assertRaises(StorageReadOnlyError):
            await store.create_item("krea2", "Blocked")
        self.path.write_text('{"version":4,"profiles":{}}', encoding="utf-8")
        store.reload()
        self.assertTrue(store.load_trusted)
        self.assertEqual(store.load_state, "loaded")
        self.assertIsNone(store.read_only_reason)
        await store.create_item("krea2", "After explicit repair")

    async def test_conflicting_edit_does_not_merge_distinct_source_identities(self):
        first = await self.store.create_item("krea2", "One", title="Same title")
        second = await self.store.create_item("krea2", "Two", title="Same title")
        before = self.path.read_bytes()
        with self.assertRaises(WorkflowError):
            await self.store.update_item("krea2", first["id"], "TWO")
        self.assertEqual(self.path.read_bytes(), before)
        self.assertEqual({item["id"] for item in self.store.all_items("krea2")}, {first["id"], second["id"]})
