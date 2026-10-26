import copy
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from aiohttp.test_utils import TestClient, TestServer

from anima_webui.persistence import StorageReadOnlyError
from anima_webui.pool_entries import normalize_krea_entry, normalize_prompt_entry
from anima_webui.prompt_pools import PromptPoolStore, parse_prompt_records
from anima_webui.server import FAVORITES_KEY, PROMPT_POOLS_KEY, create_app
from anima_webui.workflow import WorkflowError


class StructuredPoolTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name) / "prompt_pools.json"
        self.store = PromptPoolStore(self.path)

    async def test_v2_migration_preserves_text_ids_and_backup(self):
        prompt = "A" * 240 + "\nExact Case, punctuation.  Another sentence."
        original = json.dumps({"version": 2, "profiles": {"anima": ["legacy"], "krea2": [prompt]}}).encode()
        self.path.write_bytes(original)
        store = PromptPoolStore(self.path)
        self.assertEqual(self.path.with_name(self.path.name + ".v3.bak").read_bytes(), original)
        self.assertEqual(json.loads(self.path.read_text())["version"], 3)
        item = store.all_items("krea2")[0]
        self.assertEqual(item["id"], PromptPoolStore.item_id(prompt))
        self.assertEqual(item["title"], "A" * 200)
        self.assertEqual((item["subtitle"], item["groupIds"]), ("", []))
        self.assertEqual(store.resolve("krea2", [item["id"]]), [prompt])
        self.assertEqual(store.resolve("anima"), ["legacy"])
        self.assertEqual(PromptPoolStore(self.path).all_items("krea2"), [item])

    async def test_failed_v2_migration_retains_source_and_read_view(self):
        original = b'{"version":2,"profiles":{"krea2":["kept"]}}'
        self.path.write_bytes(original)
        with patch.object(PromptPoolStore, "_write_text", side_effect=PermissionError("locked")):
            store = PromptPoolStore(self.path)
        self.assertEqual(self.path.read_bytes(), original)
        self.assertEqual(store.resolve("krea2"), ["kept"])
        with self.assertRaises(StorageReadOnlyError):
            await store.create_group("krea2", {"name": "Blocked"})

    def test_malformed_schema_is_quarantined_instead_of_crashing(self):
        bad = [{"version": value, "profiles": {}} for value in ([], {}, True, None)]
        bad.append({"version": 3, "profiles": {"krea2": {"items": [], "groups": [
            {"id": "bad", "name": "bad", "kind": []}
        ]}}})
        for index, payload in enumerate(bad):
            with self.subTest(payload=payload):
                path = self.path.with_name(f"bad-{index}.json")
                original = json.dumps(payload).encode()
                path.write_bytes(original)
                store = PromptPoolStore(path)
                self.assertEqual(store.all_items("krea2"), [])
                self.assertEqual(path.with_name(path.name + ".corrupt.bak").read_bytes(), original)

    async def test_metadata_ensure_and_group_hierarchy_roundtrip(self):
        parent = (await self.store.create_group("krea2", {"name": "Parent"}))["group"]
        child = (await self.store.create_group("krea2", {"name": "Child", "parentId": parent["id"]}))["group"]
        other = (await self.store.create_group("krea2", {"name": "Other"}))["group"]
        item = await self.store.create_item("krea2", "A scene", title="场景", subtitle="说明", group_ids=[child["id"], other["id"]])
        await self.store.create_item("krea2", "Only child", group_ids=[child["id"]])
        ensured = await self.store.ensure_item("krea2", "a SCENE", title="Do not replace", subtitle="No")
        self.assertFalse(ensured["created"])
        self.assertEqual((ensured["title"], ensured["subtitle"]), ("场景", "说明"))
        renamed = await self.store.update_item("krea2", item["id"], "A scene", title="新名称")
        self.assertEqual(renamed["id"], item["id"])
        self.assertEqual(self.store.list("krea2", custom_group=parent["id"])["count"], 2)
        await self.store.update_group("krea2", child["id"], {"name": "Renamed"})
        self.assertEqual(PromptPoolStore(self.path).list_groups("krea2"), self.store.list_groups("krea2"))
        deleted = await self.store.delete_group("krea2", parent["id"], "exclusive")
        self.assertEqual(deleted["deletedItemCount"], 1)
        self.assertEqual(self.store.all_items("krea2")[0]["groupIds"], [other["id"]])
        self.assertEqual(self.store.all_items("anima"), [])

    async def test_import_preview_overwrite_and_invalid_tail_are_atomic(self):
        old = await self.store.create_item("krea2", "Exact Text", title="Old")
        extra = (await self.store.create_group("krea2", {"name": "Existing"}))["group"]
        content = {"version": 1, "items": [
            {"section": "prompt", "title": "New metadata", "subtitle": "Explanation", "prompt": "EXACT TEXT", "groups": ["Scenes"]},
            {"section": "prompt", "title": "Same title allowed", "prompt": "Another body", "groups": ["Scenes", "Night"]},
            {"section": "character", "title": "Wrong profile", "prompt": "Invalid"},
        ]}
        preview = self.store.preview_structured_import("krea2", content, "json", bundle_name="Import", target_group_ids=[extra["id"]])
        self.assertEqual(preview["summary"], {"new": 1, "conflict": 1, "error": 1})
        rows = preview["rows"]
        rows[0]["action"] = "overwrite"
        before = self.path.read_bytes()
        invalid = copy.deepcopy(rows)
        invalid[-1]["action"] = "create"
        with self.assertRaises(WorkflowError):
            await self.store.commit_structured_import("krea2", invalid, bundle_name="Import")
        self.assertEqual(self.path.read_bytes(), before)
        with patch.object(self.store, "_write_text", side_effect=OSError("disk full")):
            with self.assertRaises(OSError):
                await self.store.commit_structured_import("krea2", rows, bundle_name="Import")
        self.assertEqual(self.path.read_bytes(), before)
        self.assertEqual(len(self.store.all_items("krea2")), 1)
        result = await self.store.commit_structured_import("krea2", rows, bundle_name="Import", target_group_ids=[extra["id"]])
        self.assertEqual((result["imported"], result["updated"], result["skipped"]), (1, 1, 1))
        updated = self.store.all_items("krea2")[0]
        self.assertEqual((updated["id"], updated["text"]), (old["id"], "Exact Text"))
        self.assertEqual((updated["title"], updated["subtitle"]), ("New metadata", "Explanation"))
        self.assertIn(extra["id"], updated["groupIds"])
        bundle = next(group for group in result["groups"] if group["kind"] == "import")
        self.assertEqual(bundle["count"], 2)
        self.assertEqual(len(self.store.list_group_items("krea2", bundle["id"])), 2)
        self.assertEqual(PromptPoolStore(self.path).all_items("krea2"), self.store.all_items("krea2"))

    async def test_replace_and_stale_overwrite_validation(self):
        created = await self.store.create_item("krea2", "Old", title="Old")
        await self.store.create_group("krea2", {"name": "Removed"})
        preview = self.store.preview_structured_import("krea2", [{"title": "Keep", "prompt": "New"}], "json", bundle_name="Replacement", mode="replace")
        result = await self.store.commit_structured_import("krea2", preview["rows"], bundle_name="Replacement", mode="replace")
        self.assertEqual(result["valid_ids"], [self.store.item_id("New")])
        self.assertNotIn(created["id"], result["valid_ids"])
        self.assertNotIn("Removed", [group["name"] for group in result["groups"]])
        stale = self.store.preview_structured_import("krea2", [{"title": "Edit", "prompt": "New"}], "json", bundle_name="Replacement")
        stale["rows"][0].update(action="overwrite", existingId="not-the-source")
        with self.assertRaisesRegex(WorkflowError, "重新预览"):
            await self.store.commit_structured_import("krea2", stale["rows"], bundle_name="Replacement")
        self.assertEqual(self.store.all_items("krea2")[0]["title"], "Keep")

    async def test_legacy_import_endpoint_does_not_discard_new_metadata(self):
        records = parse_prompt_records({"version": 1, "items": [
            {"section": "prompt", "title": "Name", "subtitle": "Detail", "prompt": "Body", "groups": ["Nature", "NATURE"]}
        ]})
        await self.store.import_items("krea2", records, mode="append")
        item = self.store.all_items("krea2")[0]
        groups = self.store.list_groups("krea2")["groups"]
        self.assertEqual((item["title"], item["subtitle"]), ("Name", "Detail"))
        self.assertEqual([group["name"] for group in groups], ["Nature"])
        self.assertEqual(item["groupIds"], [groups[0]["id"]])
        preview = self.store.preview_structured_import("krea2", [
            {"title": "A", "prompt": "One", "groups": ["Nature"]},
            {"title": "B", "prompt": "Two", "groups": ["NATURE"]},
        ], "json", bundle_name="Bundle")
        self.assertEqual(preview["bundle"]["newChildCount"], 1)

    async def test_favorite_snapshot_query_is_read_only_and_lazy_ensure_is_atomic(self):
        await self.store.create_item("krea2", "Unstarred")
        existing = await self.store.create_item("krea2", "Original text", title="Pool title")
        favorites = {"groups": [{"id": "default", "parentId": None}, {"id": "child", "parentId": "default"}],
                     "items": [{"id": "favorite-1", "title": "Snapshot", "subtitle": "Old explanation", "text": "Original text", "groupIds": ["child"]},
                               {"id": "favorite-2", "title": "Orphan", "text": "Only in favorites", "groupIds": ["default"]}]}
        before = self.path.read_bytes()
        view = self.store.list("krea2", collection="default", favorites=favorites)
        self.assertEqual(view["count"], 2)
        self.assertEqual([item["in_pool"] for item in view["items"]], [True, False])
        self.assertEqual(view["items"][0]["title"], "Snapshot")
        self.assertEqual(self.path.read_bytes(), before)
        sorted_items = self.store.list("krea2", favorites=favorites, sort="favorite-first")["items"]
        self.assertEqual(sorted_items[0]["id"], existing["id"])
        with self.assertRaises(WorkflowError):
            await self.store.ensure_items("krea2", [{"text": "First"}, {"text": ""}])
        self.assertEqual(self.path.read_bytes(), before)
        orphan = view["items"][1]
        ensured = await self.store.ensure_items("krea2", [{"title": orphan["title"], "text": orphan["text"]}, {"text": "only IN FAVORITES"}])
        self.assertEqual(ensured["created_ids"], [orphan["id"]])
        self.assertEqual(len(ensured["items"]), 1)
        await self.store.update_item("krea2", existing["id"], "Edited text", title="Edited")
        self.assertFalse(self.store.list("krea2", collection="__all__", favorites=favorites)["items"][0]["in_pool"])
        self.assertEqual(favorites["items"][0]["text"], "Original text")

    async def test_selection_total_counts_only_actual_pool_ids_across_filters(self):
        first = await self.store.create_item("krea2", "First scene")
        second = await self.store.create_item("krea2", "Second scene")
        orphan_id = self.store.item_id("Only a favorite")
        favorites = {"groups": [{"id": "default"}], "items": [
            {"id": "orphan", "text": "Only a favorite", "groupIds": ["default"]},
        ]}
        selected = [first["id"], first["id"], second["id"], "missing", orphan_id]
        filtered = self.store.list("krea2", query="First", selected_ids=selected)
        self.assertEqual((filtered["count"], filtered["selected_count"], filtered["selection_total"]), (1, 1, 2))
        before = self.path.read_bytes()
        orphan = self.store.list("krea2", collection="default", favorites=favorites, selected_ids=selected)
        self.assertEqual((orphan["count"], orphan["selected_count"], orphan["selection_total"]), (1, 0, 2))
        self.assertEqual(self.path.read_bytes(), before)
        empty = self.store.list("krea2", query="no matches", selected_ids=selected, selected_only=True)
        self.assertEqual((empty["count"], empty["selected_count"], empty["selection_total"]), (0, 0, 2))

    def test_templates_and_csv_preserve_multiline_metadata(self):
        for fmt in ("json", "csv"):
            with self.subTest(fmt=fmt):
                _, _, payload = self.store.template(fmt)
                record = parse_prompt_records(payload.decode("utf-8"), fmt)[0]
                self.assertEqual(record["title"], "雾中森林")
                self.assertEqual(record["section"], "prompt")
                self.assertEqual(record["groups"], ["自然风景"])
        content = 'section,title,subtitle,prompt,groups\nprompt,"雨,雾","第一行\n第二行","A Scene,\nWith NEW Lines.",A|B\n'
        record = parse_prompt_records(content, "csv")[0]
        self.assertEqual(record["prompt"], "A Scene,\nWith NEW Lines.")
        self.assertEqual(record["subtitle"], "第一行\n第二行")
        self.assertEqual(record["groups"], ["A", "B"])
        with self.assertRaises(ValueError):
            normalize_krea_entry({"section": "pose", "prompt": "x"})
        with self.assertRaises(ValueError):
            normalize_prompt_entry({"prompt": "x"}, require_title=True)


class StructuredPoolApiTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.app = create_app(app_dir=Path(__file__).resolve().parents[1], data_dir=Path(self.temp.name) / "data")
        self.client = TestClient(TestServer(self.app))
        await self.client.start_server()

    async def asyncTearDown(self):
        await self.client.close()
        self.temp.cleanup()

    async def test_public_contract_templates_groups_import_and_lazy_favorites(self):
        group_response = await self.client.post("/api/prompt-pools/groups", json={"model_profile": "krea2", "name": "Existing"})
        self.assertEqual(group_response.status, 201)
        group = (await group_response.json())["group"]
        template = await self.client.get("/api/prompt-pools/templates/json")
        self.assertIn("attachment", template.headers["Content-Disposition"])
        preview_response = await self.client.post("/api/prompt-pools/import/preview", json={
            "model_profile": "krea2", "bundleName": "Example", "format": "json",
            "content": await template.text(), "targetGroupIds": [group["id"]],
        })
        self.assertEqual(preview_response.status, 200)
        preview = await preview_response.json()
        committed = await self.client.post("/api/prompt-pools/import", json={
            "model_profile": "krea2", "bundleName": "Example", "rows": preview["rows"], "targetGroupIds": [group["id"]],
        })
        self.assertEqual(committed.status, 200)
        self.assertEqual((await committed.json())["imported"], 1)
        extension = self.app[FAVORITES_KEY].expression_store
        await extension.save({"items": [{"id": "old-favorite", "title": "Legacy", "text": "Never pooled", "groupIds": ["default"]}]}, "krea2", "prompt")
        before = copy.deepcopy(self.app[PROMPT_POOLS_KEY].items)
        query = await self.client.post("/api/prompt-pools/query", json={"model_profile": "krea2", "collection": "__all__", "selected_ids": [], "selected_only": False})
        self.assertEqual(query.status, 200)
        orphan = (await query.json())["items"][0]
        self.assertFalse(orphan["in_pool"])
        self.assertEqual(self.app[PROMPT_POOLS_KEY].items, before)
        result = await self.client.post("/api/prompt-pools/items/ensure-many", json={"model_profile": "krea2", "items": [{"title": orphan["title"], "text": orphan["text"]}]})
        self.assertEqual(result.status, 200)
        self.assertEqual((await result.json())["created_ids"], [orphan["id"]])
        selected = await self.client.post("/api/prompt-pools/query", json={"model_profile": "krea2", "collection": "__all__", "selected_ids": [orphan["id"]], "selected_only": True})
        self.assertTrue((await selected.json())["items"][0]["selected"])
        deleted = await self.client.delete(f"/api/prompt-pools/groups/{group['id']}?model_profile=krea2&deleteMode=keep")
        self.assertEqual(deleted.status, 200)

    async def test_invalid_profile_and_payloads_do_not_mutate(self):
        for profile in (None, False, 0, [], ""):
            with self.subTest(profile=profile):
                response = await self.client.post("/api/prompt-pools/items/ensure", json={"model_profile": profile, "text": "No write"})
                self.assertEqual(response.status, 400)
        for endpoint, body in [
            ("/query", {"selected_ids": {}, "model_profile": "krea2"}),
            ("/query", {"selected_only": "maybe"}),
            ("/items", {"text": "x", "prompt": "x"}),
            ("/items", {"text": "x", "title": None}),
            ("/import", {"rows": [], "items": [], "bundleName": "Mixed"}),
            ("/import/preview", {"content": "x", "targetGroupIds": []}),
        ]:
            with self.subTest(endpoint=endpoint, body=body):
                response = await self.client.post("/api/prompt-pools" + endpoint, json=body)
                self.assertEqual(response.status, 400)
        self.assertEqual(self.app[PROMPT_POOLS_KEY].all_items("anima"), [])
        self.assertEqual(self.app[PROMPT_POOLS_KEY].all_items("krea2"), [])


if __name__ == "__main__":
    unittest.main()
