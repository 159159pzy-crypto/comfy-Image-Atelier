import tempfile
import unittest
from pathlib import Path

from anima_webui.prompt_pools import PromptPoolStore
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

            removed = await store.delete_item(
                "krea2", PromptPoolStore.item_id("A portrait in a studio")
            )
            self.assertEqual(removed["count"], 2)
            with self.assertRaises(KeyError):
                await store.delete_item("krea2", "missing")

    async def test_pagination_rejects_invalid_bounds(self):
        with tempfile.TemporaryDirectory() as directory:
            store = PromptPoolStore(Path(directory) / "prompt_pools.json")
            with self.assertRaises(WorkflowError):
                store.list("krea2", page=0)
            with self.assertRaises(WorkflowError):
                store.list("krea2", limit=501)


if __name__ == "__main__":
    unittest.main()
