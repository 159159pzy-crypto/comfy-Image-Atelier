"""A page of previews must not normalize the whole model library per image."""
import asyncio
import copy
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from anima_webui.comfy import ComfyError
from anima_webui.lora_library import LoraLibraryService, LoraLibraryStore
from anima_webui.lora_triggers import LoraTriggerOverrideStore
from anima_webui.resource_classification import classify_base_model
from test_lora_library import Characters, LibraryClient


class CachedLibraryClient(LibraryClient):
    def __init__(self, count=3):
        super().__init__(count)
        self.cached = None
        self.generation = 0
        self.offline = False
        self.expired = False

    def classification_generation(self, kind):
        return self.generation

    async def resource_classifications(self, kind, refresh=False):
        if self.offline:
            raise ComfyError("fixture offline")
        if self.cached is None or refresh or self.expired:
            self.cached = await super().resource_classifications(kind, refresh)
            self.generation += 1
            self.expired = False
        return self.cached


class InventoryCacheTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.directory = tempfile.TemporaryDirectory()
        root = Path(self.directory.name)
        self.store = LoraLibraryStore(root / "library.json")
        self.triggers = LoraTriggerOverrideStore(root / "triggers.json")
        self.client = CachedLibraryClient(1000)
        self.service = LoraLibraryService(self.client, self.store, self.triggers, Characters())

    async def asyncTearDown(self):
        await self.service.close()
        self.directory.cleanup()

    async def test_sixty_previews_share_one_normalized_thousand_item_inventory(self):
        with patch("anima_webui.lora_library.classify_base_model", wraps=classify_base_model) as classify:
            items = await self.service.inventory()
            result = await asyncio.gather(*(self.service.item(item["id"]) for item in items[:60]))
            self.assertEqual(classify.call_count, 1000)
            self.assertEqual([item["id"] for item in result], [item["id"] for item in items[:60]])
            result[0]["tags"].append("caller mutation")
            self.assertNotIn("caller mutation", (await self.service.item(result[0]["id"]))["tags"])

    async def test_remote_expiry_local_revision_and_trigger_changes_are_visible(self):
        first = (await self.service.inventory())[0]
        resource_id = first["id"]
        self.client.raw[0]["notes"] = "changed externally"
        self.assertEqual((await self.service.item(resource_id))["notes"], "")
        self.client.expired = True
        self.assertEqual((await self.service.item(resource_id))["notes"], "changed externally")

        def edit(data):
            data["groups"].append({"id": "group", "name": "People", "parentId": ""})
            data["resources"][resource_id] = {key: copy.deepcopy(first[key]) for key in ("filename", "file_path", "sha256")}
            data["resources"][resource_id].update(default_strength=0.4, groupIds=["group"])
        await self.store.change(edit)
        changed = await self.service.item(resource_id)
        self.assertEqual(changed["default_strength"], 0.4)
        self.assertEqual(changed["groupIds"], ["group"])
        await self.triggers.set(first["filename"], ["custom trigger"])
        self.assertEqual((await self.service.item(resource_id))["trigger_words"], ["custom trigger"])
        await self.triggers.delete(first["filename"])
        self.assertEqual((await self.service.item(resource_id))["trigger_words"], first["trigger_words"])
        self.client.raw.pop(0)
        with self.assertRaises(KeyError):
            await self.service.item(resource_id, refresh=True)

    async def test_connection_and_pending_flags_are_not_cached(self):
        first = (await self.service.inventory())[0]
        resource_id = first["id"]
        self.service._pending_local[resource_id] = {}
        self.assertTrue((await self.service.item(resource_id))["pending_local"])
        self.service._pending_local.clear()
        self.client.offline = True
        item = await self.service.item(resource_id, allow_cached=True)
        self.assertTrue(item["offline"])
        self.assertFalse(item["available"])
        self.assertFalse(item["writable"])
        self.assertFalse(item["pending_local"])
        self.client.offline = False
        item = await self.service.item(resource_id, model_profile="krea2")
        self.assertFalse(item["offline"])
        self.assertFalse(item["available"])
        self.assertTrue((await self.service.item(resource_id, model_profile="anima"))["available"])
        self.store.read_only_reason = "fixture protection"
        self.assertFalse((await self.service.item(resource_id))["writable"])

    async def test_failed_trigger_save_does_not_publish_or_cache_uncommitted_words(self):
        first = (await self.service.inventory())[0]
        entered, finish = asyncio.Event(), asyncio.Event()

        async def fail_save(*args):
            entered.set()
            await finish.wait()
            raise OSError("fixture disk full")

        with patch.object(self.triggers, "_save", side_effect=fail_save):
            task = asyncio.create_task(self.triggers.set(first["filename"], ["not committed"]))
            await entered.wait()
            try:
                self.assertEqual((await self.service.item(first["id"]))["trigger_words"], first["trigger_words"])
            finally:
                finish.set()
                with self.assertRaises(OSError):
                    await task
        self.assertEqual((await self.service.item(first["id"]))["trigger_words"], first["trigger_words"])
