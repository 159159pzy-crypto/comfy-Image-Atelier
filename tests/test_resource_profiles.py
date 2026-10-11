import tempfile
import unittest
from pathlib import Path

from anima_webui.resource_profiles import ResourceProfileStore


class ResourceProfileStoreTests(unittest.IsolatedAsyncioTestCase):
    async def test_legacy_inventory_is_claimed_once_and_new_paths_are_stable(self):
        with tempfile.TemporaryDirectory() as directory:
            store = ResourceProfileStore(Path(directory) / "resource_profiles.json")
            first = await store.sync_inventory(
                "models",
                [
                    "anima-base-v1.0.safetensors",
                    "krea2_turbo_int8_convrot.safetensors",
                    "old-custom.safetensors",
                ],
            )
            self.assertEqual(first["anima-base-v1.0.safetensors"], "anima")
            self.assertEqual(first["krea2_turbo_int8_convrot.safetensors"], "krea2")
            self.assertEqual(first["old-custom.safetensors"], "anima")

            await store.assign("models", "old-custom.safetensors", "krea2")
            second = await store.sync_inventory(
                "models", ["old-custom.safetensors", "new.safetensors"]
            )
            self.assertEqual(second["old-custom.safetensors"], "krea2")
            self.assertIsNone(second["new.safetensors"])

    async def test_lora_paths_normalize_separators_and_assignment_can_clear(self):
        with tempfile.TemporaryDirectory() as directory:
            store = ResourceProfileStore(Path(directory) / "resource_profiles.json")
            await store.sync_inventory("loras", ["style\\one.safetensors"])
            self.assertEqual(
                store.ownership("loras", ["style/one.safetensors"])[0]["model_profile"],
                "anima",
            )
            await store.assign("loras", "style/one.safetensors", None)
            self.assertIsNone(
                store.ownership("loras", ["style\\one.safetensors"])[0]["model_profile"]
            )


if __name__ == "__main__":
    unittest.main()
