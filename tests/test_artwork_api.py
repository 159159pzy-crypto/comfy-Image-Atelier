from __future__ import annotations

import io
import tempfile
import unittest
from pathlib import Path
from unittest.mock import AsyncMock, patch

from aiohttp import FormData
from aiohttp.test_utils import TestClient, TestServer
from PIL import Image

from anima_webui.server import ARTWORK_DELETION_KEY, IMPORTED_IMAGES_KEY, HISTORY_KEY, create_app
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

    async def test_removed_management_routes_are_unavailable(self):
        routes = (("GET", "/api/artwork-folders"), ("POST", "/api/artwork-folders"),
                  ("PATCH", "/api/artwork-folders/default"), ("DELETE", "/api/artwork-folders/default"),
                  ("POST", "/api/artwork-folders/members"), ("POST", "/api/history/delete"),
                  ("GET", "/api/image-cleanup"), ("PUT", "/api/image-cleanup"),
                  ("POST", "/api/image-cleanup/run"), ("POST", "/api/image-cleanup/leases"))
        for method, path in routes:
            with self.subTest(method=method, path=path):
                response = await self.client.request(method, path)
                self.assertEqual(response.status, 404)
        self.assertEqual((await self.history.list_images())["total"], 3)

    async def test_history_retains_source_and_cursor_pagination_without_favorites(self):
        first = await self.json("GET", "/api/history?workbench=single&limit=1")
        self.assertEqual(first["total"], 2)
        self.assertEqual(first["items"][0]["id"], self.ids[1])
        self.assertNotIn("folder_ids", first["items"][0])
        second = await self.json("GET", f"/api/history?workbench=single&limit=1&before_id={first['next_before_id']}")
        self.assertEqual(second["items"][0]["id"], self.ids[0])
        self.assertFalse(second["has_more"])
        # Retired query parameters follow the existing unknown-query policy:
        # they no longer filter or consult old artwork memberships.
        unfiltered = await self.json("GET", "/api/history?folder_id=missing&favorites_only=true")
        self.assertEqual(unfiltered["total"], 3)

    async def test_single_record_delete_and_missing_record(self):
        payload = await self.json("DELETE", f"/api/history/{self.ids[0]}?mode=record")
        self.assertTrue(payload["deleted"])
        self.assertEqual(payload["deleted_count"], 1)
        self.assertEqual(payload["file_deleted_count"], 0)
        self.assertEqual((await self.history.list_images())["total"], 2)
        await self.json("DELETE", f"/api/history/{self.ids[0]}", status=404)

    async def test_single_delete_rejects_paths_and_invalid_modes(self):
        for query in ("mode=wipe", "path=C:/file.png", "mode=file&path=x"):
            await self.json("DELETE", f"/api/history/{self.ids[0]}?{query}", status=400)
        self.assertEqual((await self.history.list_images())["total"], 3)

    async def test_file_mode_requires_same_origin_direct_local_request(self):
        service = self.app[ARTWORK_DELETION_KEY]
        result = {"results": [{"id": self.ids[0], "record_deleted": False,
                               "file_deleted": False, "file_missing": False, "code": "file_shared",
                               "error": "其他作品仍引用原图"}], "deleted_count": 0, "failed_count": 1}
        path = f"/api/history/{self.ids[0]}?mode=file"
        with patch.object(service, "delete", new=AsyncMock(return_value=result)) as deletion:
            await self.json("DELETE", path, status=403)
            deletion.assert_not_awaited()
            origin = str(self.client.make_url("/")).rstrip("/")
            await self.json("DELETE", path,
                            headers={"Origin": origin, "X-Forwarded-For": "127.0.0.1"}, status=403)
            deletion.assert_not_awaited()
            returned = await self.json("DELETE", path, headers={"Origin": origin}, status=409)
            self.assertEqual(returned["results"][0]["code"], "file_shared")
            self.assertFalse(returned["deleted"])
            deletion.assert_awaited_once()
            self.assertEqual(deletion.await_args.args[:2], ([self.ids[0]], "file"))

    async def upload(self, kind):
        buffer = io.BytesIO()
        with Image.new("RGB", (24, 20), "green") as image:
            image.save(buffer, format="PNG")
        form = FormData()
        form.add_field("image", buffer.getvalue(), filename="test.png", content_type="image/png")
        return await self.client.post(f"/api/{kind}-images", data=form)

    async def test_imported_image_upload_and_read_do_not_create_cleanup_tables(self):
        for kind in ("input", "prompt"):
            response = await self.upload(kind)
            self.assertEqual(response.status, 201)
            item = await response.json()
            saved = await self.json("GET", f"/api/{kind}-images/{item['id']}")
            self.assertEqual(saved["id"], item["id"])
            content = await self.client.get(f"/api/{kind}-images/{item['id']}/image")
            self.assertEqual(content.status, 200)
            self.assertTrue((await content.read()).startswith(b"\x89PNG"))
        names = await self.history._call(lambda: {row[0] for row in self.history.connection.execute("SELECT name FROM sqlite_master")})
        self.assertTrue(names.isdisjoint({"managed_image_files", "image_draft_leases", "image_cleanup_settings"}))
        self.assertIsNotNone(self.app[IMPORTED_IMAGES_KEY])

    async def test_read_only_history_blocks_upload_and_delete_but_keeps_image_readable(self):
        item = await (await self.upload("input")).json()
        self.history._protect_storage("test protection")
        self.assertEqual((await self.upload("input")).status, 503)
        self.assertEqual((await self.upload("prompt")).status, 503)
        await self.json("DELETE", f"/api/history/{self.ids[0]}", status=503)
        await self.json("GET", f"/api/input-images/{item['id']}")
        self.assertEqual((await self.client.get(f"/api/input-images/{item['id']}/image")).status, 200)


if __name__ == "__main__":
    unittest.main()
