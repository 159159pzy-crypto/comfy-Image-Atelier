"""HTTP-level retention checks use only isolated files and a fake Comfy client."""
from __future__ import annotations

import io
import tempfile
import unittest
import uuid
from pathlib import Path

from aiohttp import FormData
from aiohttp.test_utils import TestClient, TestServer
from PIL import Image, PngImagePlugin

from anima_webui.server import HISTORY_KEY, IMPORTED_IMAGES_KEY, create_app
from test_artwork_collections import legacy_management_tables
from test_server import APP_DIR, FakeComfy, _write_fake_tools


def source_image(color="blue", parameters=None):
    stream = io.BytesIO()
    info = PngImagePlugin.PngInfo()
    if parameters is not None:
        info.add_text("parameters", parameters)
    with Image.new("RGB", (24, 16), color) as image:
        image.save(stream, format="PNG", pnginfo=info)
    return stream.getvalue()


class PromptImageCacheApiTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.app = create_app(app_dir=APP_DIR, data_dir=self.root / "data", comfy=FakeComfy(),
                              anima_tools_dir=_write_fake_tools(self.root))
        self.client = TestClient(TestServer(self.app))
        await self.client.start_server()
        self.history = self.app[HISTORY_KEY]
        self.service = self.app[IMPORTED_IMAGES_KEY]
        self.cache = self.service.prompt_cache
        self.now = 1_800_000_000.0
        self.cache.clock = lambda: self.now
        self.client_id = uuid.uuid4().hex

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

    async def upload(self, color="blue", *, kind="prompt", parameters=None):
        body = FormData()
        body.add_field("image", source_image(color, parameters), filename=f"{color}.png", content_type="image/png")
        return await self.json("POST", f"/api/{kind}-images", status=201, data=body)

    async def lease(self, revision, image_ids, *, client_id=None, status=200):
        return await self.json("PUT", f"/api/prompt-images/leases/{client_id or self.client_id}",
                               {"revision": revision, "image_ids": image_ids}, status=status)

    def asset_bytes(self, asset):
        return sum((self.root / "data" / "prompt-images" / f"{asset['id']}{suffix}").stat().st_size
                   for suffix in (".png", ".json"))

    async def test_leases_validate_payload_without_releasing_existing_protection(self):
        asset = await self.upload()
        response = await self.lease(1, [asset["id"]])
        self.assertEqual(response["revision"], 1)
        self.assertEqual(response["missing_image_ids"], [])
        self.assertEqual(response["expires_at"], self.now + 600)
        path = f"/api/prompt-images/leases/{self.client_id}"
        payloads = [
            {}, {"revision": True, "image_ids": []}, {"revision": -1, "image_ids": []},
            {"revision": 1.5, "image_ids": []}, {"revision": "2", "image_ids": []},
            {"revision": 2, "image_ids": "bad"}, {"revision": 2, "image_ids": [None]},
            {"revision": 2, "image_ids": ["../../outside"]},
            {"revision": 2, "image_ids": ["g" * 64]},
            {"revision": 2, "image_ids": [], "path": "outside"},
        ]
        for payload in payloads:
            with self.subTest(payload=payload):
                await self.json("PUT", path, payload, status=400)
        await self.json("PUT", path, [], status=400)
        await self.json("PUT", "/api/prompt-images/leases/bad!client", {"revision": 1, "image_ids": []}, status=400)
        await self.json("PUT", path, data="{}", headers={"Content-Type": "text/plain"}, status=415)
        self.now += 121
        self.cache.max_bytes = 0
        await self.cache.run()
        await self.json("GET", f"/api/prompt-images/{asset['id']}")

    async def test_multiple_pages_share_protection_until_last_lease_expires(self):
        asset = await self.upload()
        other_client = uuid.uuid4().hex
        await self.lease(1, [asset["id"]])
        await self.lease(1, [asset["id"]], client_id=other_client)
        self.now += 121
        self.cache.max_bytes = 0
        await self.lease(2, [])
        await self.cache.run()
        await self.json("GET", f"/api/prompt-images/{asset['id']}")
        self.now += 480
        await self.cache.run()
        await self.json("GET", f"/api/prompt-images/{asset['id']}", status=404)
        await self.json("GET", f"/api/prompt-images/{asset['id']}/image", status=404)

    async def test_late_revision_cannot_restore_released_reference(self):
        asset = await self.upload()
        await self.lease(1, [asset["id"]])
        released = await self.lease(3, [])
        self.now += 121
        stale = await self.lease(2, [asset["id"]])
        self.assertEqual(stale["revision"], 3)
        self.assertEqual(stale["expires_at"], released["expires_at"])
        self.cache.max_bytes = 0
        await self.cache.run()
        await self.json("GET", f"/api/prompt-images/{asset['id']}", status=404)

    async def test_missing_image_is_reported_without_losing_valid_page_reference(self):
        asset = await self.upload()
        missing = "f" * 64
        response = await self.lease(1, [asset["id"], missing])
        self.assertEqual(response["missing_image_ids"], [missing])
        self.now += 121
        self.cache.max_bytes = 0
        await self.cache.run()
        await self.json("GET", f"/api/prompt-images/{asset['id']}")
        await self.json("GET", f"/api/prompt-images/{missing}", status=404)

    async def test_recent_read_changes_eviction_order_and_under_limit_never_expires(self):
        first = await self.upload("blue")
        self.now += 1
        second = await self.upload("red")
        self.now += 1
        third = await self.upload("green")
        self.now += 121
        await self.json("GET", f"/api/prompt-images/{first['id']}")
        self.cache.max_bytes = self.asset_bytes(first) + self.asset_bytes(third)
        await self.cache.run()
        await self.json("GET", f"/api/prompt-images/{second['id']}", status=404)
        await self.json("GET", f"/api/prompt-images/{first['id']}")
        await self.json("GET", f"/api/prompt-images/{third['id']}")
        self.now += 365 * 24 * 60 * 60
        await self.cache.run()
        await self.json("GET", f"/api/prompt-images/{first['id']}")
        await self.json("GET", f"/api/prompt-images/{third['id']}")

    async def test_eviction_preserves_results_input_images_and_legacy_cleanup_tables(self):
        await self.history._call(legacy_management_tables, self.history.connection)
        tables = ("managed_image_files", "image_draft_leases", "image_cleanup_settings")

        async def legacy_rows():
            return await self.history._call(lambda: {
                name: [tuple(row) for row in self.history.connection.execute(f"SELECT * FROM {name}")]
                for name in tables
            })

        before_tables = await legacy_rows()
        reference = await self.upload("red", kind="input")
        input_paths = [self.root / "data" / "input-images" / f"{reference['id']}{suffix}"
                       for suffix in (".png", ".json")]
        before_inputs = {path: (path.read_bytes(), path.stat().st_mtime_ns) for path in input_paths}
        await self.history.create_batch("retention-test", 1, {"model_profile": "anima"})
        artwork = await self.history.add_image(
            batch_id="retention-test", sequence=1, prompt_id="generated-image",
            image={"filename": "original-output.png"}, positive_prompt="retained artwork", negative_prompt="",
            sample_seed=7, prompt_seed=8,
            settings={"workbench": "single", "single": {"input_image_id": reference["id"]}},
        )
        before_artwork = await self.history.get_image(artwork["id"])
        asset = await self.upload(parameters="a blue sky\nNegative prompt: blurry\nSteps: 20, Seed: 42")
        job = await self.json("POST", "/api/prompt-jobs", {
            "kind": "reverse", "method": "metadata", "image_id": asset["id"],
        }, status=201)
        self.assertEqual(job["status"], "completed")
        self.assertEqual(job["result"]["text"], "a blue sky")
        self.now += 121
        self.cache.max_bytes = 0
        await self.cache.run()
        await self.json("GET", f"/api/prompt-images/{asset['id']}", status=404)
        saved = await self.json("GET", f"/api/prompt-jobs/{job['id']}")
        self.assertEqual(saved["result"], job["result"])
        self.assertEqual(before_tables, await legacy_rows())
        self.assertEqual(before_artwork, await self.history.get_image(artwork["id"]))
        self.assertEqual(before_inputs, {path: (path.read_bytes(), path.stat().st_mtime_ns) for path in input_paths})
        await self.json("GET", f"/api/input-images/{reference['id']}")
        response = await self.client.get(f"/api/input-images/{reference['id']}/image")
        self.assertEqual(response.status, 200)
        self.assertTrue((await response.read()).startswith(b"\x89PNG"))
        for method, path in (("GET", "/api/image-cleanup"), ("PUT", "/api/image-cleanup"),
                             ("POST", "/api/image-cleanup/run"), ("POST", "/api/image-cleanup/leases")):
            with self.subTest(method=method, path=path):
                await self.json(method, path, status=404)

    async def test_storage_read_only_pauses_cleanup_but_preserves_image_reads(self):
        asset = await self.upload()
        self.now += 121
        self.cache.max_bytes = 0
        self.history._protect_storage("test protection")
        await self.cache.run()
        await self.json("GET", f"/api/prompt-images/{asset['id']}")
        response = await self.client.get(f"/api/prompt-images/{asset['id']}/image")
        self.assertEqual(response.status, 200)
        self.assertTrue((await response.read()).startswith(b"\x89PNG"))
        await self.lease(1, [asset["id"]], status=503)

    async def test_cross_origin_cannot_pin_images(self):
        response = await self.client.put(f"/api/prompt-images/leases/{self.client_id}",
                                         json={"revision": 1, "image_ids": []},
                                         headers={"Origin": "https://attacker.example"})
        self.assertEqual(response.status, 403)


if __name__ == "__main__":
    unittest.main()
