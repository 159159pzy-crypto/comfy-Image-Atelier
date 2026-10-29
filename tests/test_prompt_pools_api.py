import tempfile
import unittest
from pathlib import Path
from urllib.parse import urlencode

from aiohttp.test_utils import TestClient, TestServer

from anima_webui.prompt_pools import PromptPoolStore
from anima_webui.server import create_app


APP_DIR = Path(__file__).resolve().parents[1]


class PromptPoolApiTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.data_dir = Path(self.temp.name) / "data"
        app = create_app(app_dir=APP_DIR, data_dir=self.data_dir)
        self.client = TestClient(TestServer(app))
        await self.client.start_server()

    async def asyncTearDown(self):
        await self.client.close()
        self.temp.cleanup()

    async def test_preview_import_and_selected_only_filtering(self):
        initial = await self.client.post(
            "/api/prompt-pools/import",
            json={
                "model_profile": "krea2",
                "mode": "replace",
                "format": "text",
                "content": "A forest at dusk\nA portrait in a studio\nForest with rain",
            },
        )
        self.assertEqual(initial.status, 200)
        imported = await initial.json()
        self.assertEqual(imported["count"], 3)
        self.assertEqual(len(imported["imported_ids"]), 3)

        preview_response = await self.client.post(
            "/api/prompt-pools/import/preview",
            json={
                "model_profile": "krea2",
                "mode": "append",
                "format": "csv",
                "content": "prompt\nA forest at dusk\nA new scene\na NEW SCENE",
            },
        )
        self.assertEqual(preview_response.status, 200)
        preview = await preview_response.json()
        self.assertEqual(preview["items"], ["A forest at dusk", "A new scene"])
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

        selected_ids = imported["imported_ids"][:2]
        response = await self.client.get(
            "/api/prompt-pools?" + urlencode(
                {
                    "model_profile": "krea2",
                    "q": "forest",
                    "page": 1,
                    "limit": 1,
                    "selected_ids": ",".join(selected_ids),
                    "selected_only": "true",
                }
            )
        )
        self.assertEqual(response.status, 200)
        result = await response.json()
        self.assertEqual(result["count"], 1)
        self.assertEqual(result["pool_count"], 3)
        self.assertEqual(result["selected_count"], 1)
        self.assertEqual(result["items"][0]["text"], "A forest at dusk")

        appended = await self.client.post(
            "/api/prompt-pools/import",
            json={
                "model_profile": "krea2",
                "mode": "append",
                "format": "json",
                "content": '{"prompts":[{"text":"A forest at dusk"},{"prompt":"A new scene"}]}',
            },
        )
        payload = await appended.json()
        self.assertEqual(appended.status, 200)
        self.assertEqual(payload["count"], 4)
        self.assertEqual(
            payload["imported_ids"],
            [next(item["id"] for item in payload["items"] if item["text"] == "A new scene")],
        )

    async def test_top_level_json_prompt_objects_are_importable(self):
        for content, prompt in (
            ('{"text":"A top-level text object"}', "A top-level text object"),
            ('{"prompt":"A top-level prompt object"}', "A top-level prompt object"),
        ):
            with self.subTest(content=content):
                response = await self.client.post(
                    "/api/prompt-pools/import/preview",
                    json={"model_profile": "krea2", "format": "json", "content": content},
                )
                self.assertEqual(response.status, 200)
                preview = await response.json()
                self.assertEqual(preview["items"], [prompt])

    async def test_import_endpoints_reject_non_string_modes_as_client_errors(self):
        for path in ("/api/prompt-pools/import/preview", "/api/prompt-pools/import"):
            for mode in (None, [], {}, 1):
                with self.subTest(path=path, mode=mode):
                    response = await self.client.post(
                        path,
                        json={
                            "model_profile": "krea2",
                            "mode": mode,
                            "format": "text",
                            "content": "A valid prompt",
                        },
                    )
                    self.assertEqual(response.status, 400)

    async def test_single_mutations_bulk_delete_and_persistent_profile_isolation(self):
        created = await self.client.post(
            "/api/prompt-pools/items",
            json={"model_profile": "krea2", "text": "First prompt"},
        )
        self.assertEqual(created.status, 201)
        first = await created.json()
        self.assertEqual(first["model_profile"], "krea2")
        self.assertEqual(first["text"], "First prompt")

        second = await self.client.post(
            "/api/prompt-pools/items",
            json={"model_profile": "krea2", "text": "Second prompt"},
        )
        second_payload = await second.json()
        update = await self.client.put(
            "/api/prompt-pools/items",
            json={
                "model_profile": "krea2",
                "id": second_payload["id"],
                "text": "Updated prompt",
            },
        )
        updated = await update.json()
        self.assertEqual(update.status, 200)
        self.assertEqual(updated["text"], "Updated prompt")

        conflict = await self.client.put(
            "/api/prompt-pools/items",
            json={"model_profile": "krea2", "id": updated["id"], "text": "First prompt"},
        )
        self.assertEqual(conflict.status, 400)

        unknown = await self.client.post(
            "/api/prompt-pools/items/delete",
            json={"model_profile": "krea2", "ids": [first["id"]], "extra": True},
        )
        self.assertEqual(unknown.status, 400)

        deleted = await self.client.post(
            "/api/prompt-pools/items/delete",
            json={"model_profile": "krea2", "ids": [first["id"], first["id"]]},
        )
        deleted_payload = await deleted.json()
        self.assertEqual(deleted.status, 200)
        self.assertEqual(deleted_payload["model_profile"], "krea2")
        self.assertEqual(deleted_payload["deleted"], [first["id"]])
        self.assertEqual(deleted_payload["count"], 1)

        reloaded = PromptPoolStore(self.data_dir / "prompt_pools.json")
        self.assertEqual(reloaded.resolve("krea2"), ["Updated prompt"])
        self.assertEqual(reloaded.resolve("anima"), [])

    async def test_import_and_preview_reject_unknown_fields(self):
        for path, body in (
            (
                "/api/prompt-pools/import/preview",
                {"format": "text", "content": "scene", "unexpected": True},
            ),
            (
                "/api/prompt-pools/import",
                {"items": ["scene"], "unexpected": True},
            ),
        ):
            with self.subTest(path=path):
                response = await self.client.post(path, json=body)
                self.assertEqual(response.status, 400)


if __name__ == "__main__":
    unittest.main()
