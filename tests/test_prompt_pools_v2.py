import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from anima_webui.prompt_pools import (
    PromptPoolStore,
    parse_prompt_import,
    parse_prompt_import_details,
)
from anima_webui.workflow import WorkflowError


class PromptPoolPaginationTests(unittest.IsolatedAsyncioTestCase):
    async def test_search_pagination_selection_and_delete(self):
        with tempfile.TemporaryDirectory() as directory:
            store = PromptPoolStore(Path(directory) / "prompt_pools.json")
            await store.import_items(
                "krea2", ["A forest at dusk", "A portrait in a studio", "Forest with rain"]
            )

            result = store.list("krea2", query="forest", page=1, limit=1)
            self.assertEqual(result["total"], 2)
            self.assertEqual(result["pages"], 2)
            self.assertEqual(result["items"][0]["text"], "A forest at dusk")

            selected = store.list(
                "krea2",
                page=2,
                limit=1,
                selected_ids=[PromptPoolStore.item_id("A forest at dusk")],
            )
            self.assertFalse(selected["items"][0]["selected"])
            self.assertEqual(selected["page"], 2)
            self.assertEqual(selected["pool_count"], 3)

            filtered_selected = store.list(
                "krea2",
                query="forest",
                page=1,
                limit=1,
                selected_ids=[
                    PromptPoolStore.item_id("A forest at dusk"),
                    PromptPoolStore.item_id("A portrait in a studio"),
                ],
                selected_only=True,
            )
            self.assertEqual(filtered_selected["count"], 1)
            self.assertEqual(filtered_selected["pool_count"], 3)
            self.assertEqual(filtered_selected["selected_count"], 1)
            self.assertEqual(filtered_selected["items"][0]["text"], "A forest at dusk")
            self.assertEqual(
                store.list("krea2", selected_ids=[], selected_only=True)["count"], 0
            )

            removed = await store.delete_item(
                "krea2", PromptPoolStore.item_id("A portrait in a studio")
            )
            self.assertEqual(removed["count"], 2)
            with self.assertRaises(KeyError):
                await store.delete_item("krea2", "missing")

    def test_import_mode_must_be_a_supported_string(self):
        with tempfile.TemporaryDirectory() as directory:
            store = PromptPoolStore(Path(directory) / "prompt_pools.json")
            for mode in (None, [], {}, 1):
                with self.subTest(mode=mode), self.assertRaises(WorkflowError):
                    store.preview_import("krea2", {"items": ["scene"]}, mode=mode)

    async def test_pagination_rejects_invalid_bounds(self):
        with tempfile.TemporaryDirectory() as directory:
            store = PromptPoolStore(Path(directory) / "prompt_pools.json")
            with self.assertRaises(WorkflowError):
                store.list("krea2", page=0)
            with self.assertRaises(WorkflowError):
                store.list("krea2", limit=501)


class PromptPoolImportTests(unittest.TestCase):
    def test_json_text_and_object_shapes_share_one_parser(self):
        self.assertEqual(
            parse_prompt_import(
                '{"prompts":[{"text":"A forest"},{"prompt":"A studio"}]}',
                "json",
            ),
            ["A forest", "A studio"],
        )
        self.assertEqual(
            parse_prompt_import('[{"text":"A forest"}]', "json"), ["A forest"]
        )
        self.assertEqual(
            parse_prompt_import('{"text":"A top-level scene"}', "json"),
            ["A top-level scene"],
        )
        self.assertEqual(
            parse_prompt_import('{"prompt":"Another top-level scene"}', "json"),
            ["Another top-level scene"],
        )
        self.assertEqual(
            parse_prompt_import("first scene\nsecond scene\n", "text"),
            ["first scene", "second scene"],
        )
        self.assertEqual(
            parse_prompt_import("a scene with rain, lanterns", "auto"),
            ["a scene with rain, lanterns"],
        )
        self.assertEqual(
            parse_prompt_import('label,prompt\nScene,"a scene with rain, lanterns"\n', "auto"),
            ["a scene with rain, lanterns"],
        )

    def test_csv_header_and_case_insensitive_duplicate_preview(self):
        parsed = parse_prompt_import_details(
            "title,prompt\nForest,A forest\nStudio,A studio\nDuplicate,a FOREST\n",
            "csv",
        )
        self.assertEqual(parsed["items"], ["A forest", "A studio"])
        self.assertEqual(
            {key: parsed[key] for key in ("total", "unique", "duplicates")},
            {"total": 3, "unique": 2, "duplicates": 1},
        )
        self.assertEqual(
            parse_prompt_import("提示词\n林中夜景\n", "csv"), ["林中夜景"]
        )
        self.assertEqual(
            parse_prompt_import("\ufeffprompt\nBOM scene\n", "csv"), ["BOM scene"]
        )

    def test_parser_rejects_unknown_or_ambiguous_json_fields(self):
        for raw in (
            '{"items":["a"],"unexpected":true}',
            '[{"text":"a","extra":1}]',
            '[{"text":"a","prompt":"b"}]',
        ):
            with self.subTest(raw=raw), self.assertRaises(WorkflowError):
                parse_prompt_import(raw, "json")


class PromptPoolMutationTests(unittest.IsolatedAsyncioTestCase):
    async def test_append_replace_edit_conflict_bulk_delete_and_persistence(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "prompt_pools.json"
            store = PromptPoolStore(path)
            await store.import_items("krea2", ["first", "second"])
            await store.import_items("anima", ["Anima only"])

            appended = await store.import_items("krea2", ["SECOND", "third"], mode="append")
            self.assertEqual(
                [item["text"] for item in appended["items"]], ["first", "second", "third"]
            )
            self.assertEqual(
                appended["imported_ids"], [PromptPoolStore.item_id("third")]
            )
            duplicate_append = await store.import_items("krea2", ["fourth", "FOURTH"], mode="append")
            self.assertEqual(
                duplicate_append["imported_ids"], [PromptPoolStore.item_id("fourth")]
            )
            replacement = await store.import_items("krea2", ["replacement"], mode="replace")
            self.assertEqual(
                [item["text"] for item in replacement["items"]], ["replacement"]
            )
            self.assertEqual(
                replacement["imported_ids"], [PromptPoolStore.item_id("replacement")]
            )

            created = await store.create_item("krea2", "Editable")
            self.assertEqual(created["count"], 2)
            await store.create_item("krea2", "Collision")
            with self.assertRaises(WorkflowError):
                await store.update_item("krea2", created["id"], "collision")

            updated = await store.update_item("krea2", created["id"], "Edited")
            self.assertEqual(updated["text"], "Edited")
            self.assertNotEqual(updated["id"], created["id"])
            deleted = await store.delete_items(
                "krea2", [updated["id"], updated["id"], "missing"]
            )
            self.assertEqual(deleted["deleted"], [updated["id"]])
            self.assertEqual(deleted["count"], 2)

            reloaded = PromptPoolStore(path)
            self.assertEqual(reloaded.resolve("krea2"), ["replacement", "Collision"])
            self.assertEqual(reloaded.resolve("anima"), ["Anima only"])

    async def test_preview_counts_existing_duplicates_and_preserves_profile_isolation(self):
        with tempfile.TemporaryDirectory() as directory:
            store = PromptPoolStore(Path(directory) / "prompt_pools.json")
            await store.import_items("krea2", ["Existing"])
            await store.import_items("anima", ["Other profile"])
            parsed = parse_prompt_import_details(
                "Existing\nNEW\nnew\n", "text"
            )
            preview = store.preview_import("krea2", parsed, mode="append")
            self.assertEqual(preview["items"], ["Existing", "NEW"])
            self.assertEqual(
                preview["summary"],
                {
                    "total": 3,
                    "unique": 2,
                    "duplicates": 1,
                    "existingDuplicates": 1,
                    "new": 1,
                },
            )
            anima_preview = store.preview_import("anima", parsed, mode="replace")
            self.assertEqual(anima_preview["summary"]["existingDuplicates"], 0)
            replacement_preview = store.preview_import("krea2", parsed, mode="replace")
            self.assertEqual(replacement_preview["summary"]["new"], 2)

    async def test_append_preview_rejects_exceeding_pool_capacity(self):
        with tempfile.TemporaryDirectory() as directory:
            store = PromptPoolStore(Path(directory) / "prompt_pools.json")
            with patch("anima_webui.prompt_pools.MAX_PROMPTS", 2):
                await store.import_items("krea2", ["Existing"])
                parsed = parse_prompt_import_details("New one\nNew two", "text")
                with self.assertRaisesRegex(WorkflowError, "追加后提示词池"):
                    store.preview_import("krea2", parsed, mode="append")


if __name__ == "__main__":
    unittest.main()
