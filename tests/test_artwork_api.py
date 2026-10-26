from __future__ import annotations

import io
import tempfile
import unittest
from pathlib import Path
from unittest.mock import AsyncMock, patch

from aiohttp import FormData
from aiohttp.test_utils import TestClient, TestServer
from PIL import Image

from anima_webui.server import ARTWORK_DELETION_KEY, ASSET_LIFECYCLE_KEY, HISTORY_KEY, create_app
from test_server import APP_DIR, FakeComfy, _write_fake_tools


class ArtworkApiTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temp = tempfile.TemporaryDirectory()
        root = Path(self.temp.name)
        self.app = create_app(app_dir=APP_DIR, data_dir=root / "data", comfy=FakeComfy(),
                              anima_tools_dir=_write_fake_tools(root))
        self.client = TestClient(TestServer(self.app))
        await self.client.start_server()
        self.history = self.app[HISTORY_KEY]
        await self.history.create_batch("artwork", 3, {"model_profile": "anima"})
        self.ids = []
        for i, (workbench, profile) in enumerate((("single", "anima"), ("single", "krea2"), ("random", "anima"))):
            item = await self.history.add_image(
                batch_id="artwork", sequence=i + 1, prompt_id=f"p-{i}",
                image={"filename": f"output-{i}.png"}, positive_prompt="test", negative_prompt="",
                sample_seed=1, prompt_seed=2, settings={"workbench": workbench, "model_profile": profile},
            )
            self.ids.append(item["id"])

    async def asyncTearDown(self):
        await self.client.close()
        self.temp.cleanup()

    async def json(self, method, path, body=None, status=200, **kwargs):
        if body is not None:
            kwargs["json"] = body
        response = await self.client.request(method, path, **kwargs)
        payload = await response.json()
        self.assertEqual(response.status, status, payload)
        return payload

    async def test_folder_membership_shared_across_profiles_and_filter_cursor(self):
        payload = await self.json("POST", "/api/artwork-folders", {"name": "精选"}, status=201)
        folder_id = payload["folder"]["id"]
        await self.json("POST", "/api/artwork-folders/members", {
            "image_ids": self.ids, "folder_ids": [folder_id], "action": "add",
        })
        first = await self.json("GET", f"/api/history?folder_id={folder_id}&workbench=single&limit=1")
        self.assertEqual(first["total"], 2)
        self.assertEqual(first["items"][0]["id"], self.ids[1])
        self.assertIn(folder_id, first["items"][0]["folder_ids"])
        second = await self.json("GET", f"/api/history?folder_id={folder_id}&workbench=single&limit=1&before_id={first['next_before_id']}")
        self.assertEqual(second["items"][0]["id"], self.ids[0])
        self.assertFalse(second["has_more"])
        await self.json("PATCH", f"/api/artwork-folders/{folder_id}", {"name": "成品"})
        await self.json("DELETE", f"/api/artwork-folders/{folder_id}")
        kept = await self.json("GET", "/api/history?folder_id=default")
        self.assertEqual(kept["total"], 3)

    async def test_batch_record_delete_removes_favorites_and_missing_is_idempotent(self):
        await self.json("POST", "/api/artwork-folders/members", {
            "image_ids": self.ids, "folder_ids": ["default"], "action": "add",
        })
        payload = await self.json("POST", "/api/history/delete", {"image_ids": self.ids[:2], "mode": "record"})
        self.assertEqual(payload["deleted_count"], 2)
        self.assertTrue(all(item["record_deleted"] for item in payload["results"]))
        kept = await self.json("GET", "/api/history?favorites_only=true")
        self.assertEqual([item["id"] for item in kept["items"]], self.ids[2:])
        await self.json("DELETE", f"/api/history/{self.ids[2]}")
        await self.json("DELETE", f"/api/history/{self.ids[2]}", status=404)
        repeated = await self.json("POST", "/api/history/delete", {"image_ids": self.ids, "mode": "record"})
        self.assertEqual(repeated["failed_count"], 0)

    async def test_validation_rejects_paths_bad_modes_and_oversized_batches(self):
        for body in ({"image_ids": self.ids, "mode": "wipe"}, {"image_ids": [True]},
                     {"image_ids": []}, {"image_ids": list(range(1, 202))},
                     {"image_ids": self.ids, "path": "C:/file.png"}):
            await self.json("POST", "/api/history/delete", body, status=400)
        for body in ({"name": ""}, {"name": "x", "path": "bad"}):
            await self.json("POST", "/api/artwork-folders", body, status=400)
        await self.json("GET", "/api/history?favorites_only=maybe", status=400)
        await self.json("POST", "/api/artwork-folders/members", {
            "image_ids": self.ids, "folder_ids": ["missing"], "action": "add",
        }, status=404)
        self.assertEqual((await self.history.list_images())["total"], 3)

    async def test_file_mode_requires_same_origin_direct_local_request(self):
        service = self.app[ARTWORK_DELETION_KEY]
        result = {"results": [{"id": self.ids[0], "record_deleted": False,
                               "file_deleted": False, "file_missing": False, "code": "file_shared",
                               "error": "其他作品仍引用原图"}], "deleted_count": 0, "failed_count": 1}
        with patch.object(service, "delete", new=AsyncMock(return_value=result)) as deletion:
            await self.json("POST", "/api/history/delete", {"image_ids": self.ids[:1], "mode": "file"}, status=403)
            deletion.assert_not_awaited()
            origin = str(self.client.make_url("/")).rstrip("/")
            await self.json("POST", "/api/history/delete", {"image_ids": self.ids[:1], "mode": "file"},
                            headers={"Origin": origin, "X-Forwarded-For": "127.0.0.1"}, status=403)
            deletion.assert_not_awaited()
            returned = await self.json("POST", "/api/history/delete", {"image_ids": self.ids[:1], "mode": "file"},
                                       headers={"Origin": origin})
            self.assertEqual(returned["results"][0]["code"], "file_shared")
            deletion.assert_awaited_once()

    async def test_cleanup_upload_read_lease_settings_and_missing_draft(self):
        buffer = io.BytesIO()
        Image.new("RGB", (24, 20), "green").save(buffer, format="PNG")
        ids = {}
        for kind in ("input", "prompt"):
            form = FormData()
            form.add_field("image", buffer.getvalue(), filename="test.png", content_type="image/png")
            response = await self.client.post(f"/api/{kind}-images", data=form)
            self.assertEqual(response.status, 201)
            item = await response.json()
            ids[kind] = item["id"]
            content = await self.client.get(f"/api/{kind}-images/{item['id']}/image")
            self.assertEqual(content.status, 200)
            self.assertTrue((await content.read()).startswith(b"\x89PNG"))
        missing = "f" * 64
        lease = await self.json("POST", "/api/image-cleanup/leases", {
            "session_id": "artwork-api-test", "input_image_ids": [ids["input"], missing],
            "prompt_image_ids": [ids["prompt"]],
        })
        self.assertIn(ids["input"], lease["accepted"]["input_image_ids"])
        self.assertIn(missing, lease["missing"]["input_image_ids"])
        defaults = await self.json("GET", "/api/image-cleanup")
        self.assertTrue(defaults["enabled"])
        self.assertEqual(defaults["retention_days"], 7)
        changed = await self.json("PUT", "/api/image-cleanup", {"enabled": False, "retention_days": 14})
        self.assertFalse(changed["enabled"])
        self.assertEqual(changed["retention_days"], 14)
        for body in ({"retention_days": 0}, {"retention_days": True}, {"enabled": "yes"}, {"path": "x"}):
            await self.json("PUT", "/api/image-cleanup", body, status=400)
        await self.json("POST", "/api/image-cleanup/run", {"force": True}, status=400)
        await self.json("POST", "/api/image-cleanup/run", {})

    async def test_read_only_history_blocks_cleanup_and_artwork_mutations(self):
        self.history._protect_storage("test protection")
        status = await self.json("GET", "/api/image-cleanup")
        self.assertTrue(status["read_only"])
        await self.json("POST", "/api/artwork-folders", {"name": "blocked"}, status=503)
        await self.json("POST", "/api/image-cleanup/run", {}, status=503)
        self.assertIsNotNone(self.app[ASSET_LIFECYCLE_KEY])


if __name__ == "__main__":
    unittest.main()
