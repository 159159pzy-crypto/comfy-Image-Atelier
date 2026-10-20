"""Public HTTP boundaries must fail safely before reaching mutable services."""

import asyncio
import json
import tempfile
import unittest
from pathlib import Path
from unittest import mock

from aiohttp.test_utils import TestClient, TestServer

from anima_webui.server import COMFY_KEY, HISTORY_KEY, MANAGER_KEY, create_app
from test_server import APP_DIR, FakeComfy, _write_fake_tools


class ServerSecurityTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temp = tempfile.TemporaryDirectory()
        directory = Path(self.temp.name)
        self.app = create_app(
            app_dir=APP_DIR,
            data_dir=directory / "data",
            comfy=FakeComfy(),
            anima_tools_dir=_write_fake_tools(directory),
            shutdown_token="test-token",
            trusted_hostnames={"studio.example"},
        )
        self.client = TestClient(TestServer(self.app))
        await self.client.start_server()

    async def asyncTearDown(self):
        await self.client.close()
        self.temp.cleanup()

    async def test_other_local_port_cannot_modify_data(self):
        response = await self.client.post(
            "/api/prompt-pools/items",
            data=json.dumps({"model_profile": "krea2", "text": "cross-origin insertion"}),
            headers={
                "Origin": f"http://127.0.0.1:{self.client.port + 1}",
                "Content-Type": "text/plain",
            },
        )
        self.assertEqual(response.status, 403)
        pool = await (await self.client.get("/api/prompt-pools?model_profile=krea2")).json()
        self.assertEqual(pool["total"], 0)

    async def test_host_and_origin_must_be_strict_authorities(self):
        invalid_headers = [
            {"Host": f"user@127.0.0.1:{self.client.port}"},
            {"Host": f"127.0.0.1:{self.client.port}/ignored"},
            {"Host": "127.0.0.1:invalid"},
            {"Origin": f"file://127.0.0.1:{self.client.port}"},
            {"Origin": f"http://user@127.0.0.1:{self.client.port}"},
            {"Origin": f"http://127.0.0.1:{self.client.port}/ignored"},
            {"Origin": f"https://127.0.0.1:{self.client.port}"},
            {"Origin": f"http://localhost:{self.client.port}"},
        ]
        for headers in invalid_headers:
            with self.subTest(headers=headers):
                response = await self.client.get("/api/config", headers=headers)
                self.assertEqual(response.status, 403)

    async def test_same_origin_loopback_and_explicit_tls_proxy_work(self):
        authorities = [
            (f"127.0.0.1:{self.client.port}", "http"),
            (f"localhost:{self.client.port}", "http"),
            (f"[::1]:{self.client.port}", "http"),
            ("studio.example", "https"),
            ("studio.example:8443", "https"),
        ]
        for authority, scheme in authorities:
            with self.subTest(authority=authority):
                response = await self.client.get(
                    "/api/config", headers={
                        "Host": authority,
                        "Origin": f"{scheme}://{authority}",
                        "Sec-Fetch-Site": "same-origin",
                    },
                )
                self.assertEqual(response.status, 200)
        response = await self.client.get(
            "/api/config", headers={
                "Host": "studio.example",
                "Origin": "https://studio.example:8443",
                "X-Forwarded-Host": "studio.example:8443",
            },
        )
        self.assertEqual(response.status, 403)

    async def test_cross_site_fetch_without_origin_is_rejected(self):
        response = await self.client.get(
            "/api/config", headers={"Sec-Fetch-Site": "cross-site"}
        )
        self.assertEqual(response.status, 403)
        # A user following the README's link can still open the actual UI.
        response = await self.client.get("/", headers={"Sec-Fetch-Site": "cross-site"})
        self.assertEqual(response.status, 200)

    async def test_non_json_content_type_is_rejected(self):
        response = await self.client.post(
            "/api/prompt-pools/items",
            data=json.dumps({"model_profile": "krea2", "text": "plain form"}),
            headers={"Content-Type": "text/plain"},
        )
        self.assertEqual(response.status, 415)
        self.assertIn("error", await response.json())

    async def test_non_finite_and_deep_json_are_invalid_requests(self):
        payloads = (
            '{"page": Infinity}',
            '{"page": NaN}',
            '{"page": 1e9999}',
            '{"page": ' + "9" * 5000 + "}",
            '{"selection": ' + "[" * 1100 + "0" + "]" * 1100 + "}",
        )
        for payload in payloads:
            with self.subTest(payload=payload[:50]):
                response = await self.client.post(
                    "/api/pools/pose/query",
                    data=payload,
                    headers={"Content-Type": "application/json"},
                )
                self.assertEqual(response.status, 400)
                self.assertIn("error", await response.json())

    async def test_body_limit_preserves_413_json_contract(self):
        # Exercise aiohttp's real payload limit without allocating 21 MiB.
        self.app._client_max_size = 128
        response = await self.client.post(
            "/api/pools/pose/query", json={"q": "x" * 256}
        )
        self.assertEqual(response.status, 413)
        self.assertIn("error", await response.json())

    async def test_malformed_upload_is_a_client_error(self):
        for content_type, payload in [
            ("multipart/form-data", b"no boundary"),
            ("multipart/form-data; boundary=outer", b"not a multipart body"),
            (
                "multipart/form-data; boundary=outer",
                b"--outer\r\nContent-Disposition: form-data; name=\"image\"\r\n"
                b"Content-Type: multipart/mixed; boundary=inner\r\n\r\n"
                b"--inner--\r\n--outer--\r\n",
            ),
            (
                "multipart/form-data; boundary=outer",
                b'--outer\r\nContent-Disposition: form-data; name="_charset_"\r\n\r\n'
                + b"x" * 32 + b"\r\n--outer--\r\n",
            ),
        ]:
            with self.subTest(content_type=content_type, payload=payload[:40]):
                response = await self.client.post(
                    "/api/input-images", data=payload,
                    headers={"Content-Type": content_type},
                )
                self.assertEqual(response.status, 400)
                self.assertIn("error", await response.json())

    async def test_chunked_multipart_preamble_counts_toward_body_limit(self):
        self.app._client_max_size = 128

        async def chunks():
            yield b"ignored preamble\r\n" * 12
            yield (
                b"--outer\r\nContent-Disposition: form-data; name=\"image\"; "
                b"filename=\"small.png\"\r\n\r\nimage\r\n--outer--\r\n"
            )

        response = await self.client.post(
            "/api/input-images", data=chunks(),
            headers={"Content-Type": "multipart/form-data; boundary=outer"},
        )
        self.assertEqual(response.status, 413)
        self.assertIn("error", await response.json())

    async def test_chunked_multipart_epilogue_counts_toward_body_limit(self):
        self.app._client_max_size = 256

        async def chunks():
            yield (
                b'--outer\r\nContent-Disposition: form-data; name="image"; '
                b'filename="small.png"\r\n\r\nimage\r\n--outer--\r\n'
            )
            yield b"\r\n\r\n"
            yield b"x" * 512

        response = await self.client.post(
            "/api/input-images", data=chunks(),
            headers={"Content-Type": "multipart/form-data; boundary=outer"},
        )
        self.assertEqual(response.status, 413)
        self.assertIn("error", await response.json())

    async def test_invalid_gzip_body_returns_400_and_closes_broken_connection(self):
        for endpoint, content_type in [
            ("/api/pools/pose/query", "application/json"),
            ("/api/input-images", "multipart/form-data; boundary=outer"),
        ]:
            with self.subTest(endpoint=endpoint):
                response = await self.client.post(
                    endpoint, data=b"not gzip",
                    headers={"Content-Type": content_type, "Content-Encoding": "gzip"},
                )
                self.assertEqual(response.status, 400)
                self.assertEqual(response.headers["Connection"], "close")
                self.assertIn("error", await response.json())
                self.assertEqual((await self.client.get("/api/config")).status, 200)

    async def test_invalid_multipart_headers_are_client_errors(self):
        headers = (
            b"Broken Header\r\n",
            b"X-Test: " + b"a" * 9000 + b"\r\n",
            b"X-Test: a\r\n" * 129,
        )
        for extra_header in headers:
            with self.subTest(header=extra_header[:30]):
                payload = (
                    b'--outer\r\nContent-Disposition: form-data; name="image"\r\n'
                    + extra_header + b"\r\nimage\r\n--outer--\r\n"
                )
                response = await self.client.post(
                    "/api/input-images", data=payload,
                    headers={"Content-Type": "multipart/form-data; boundary=outer"},
                )
                self.assertEqual(response.status, 400)
                self.assertIn("error", await response.json())

    async def test_sqlite_ids_and_offsets_are_bounded(self):
        huge_id = str(2**63)
        endpoints = [
            ("GET", f"/api/images/{huge_id}", None),
            ("GET", f"/api/history/{huge_id}/regeneration-options", None),
            ("DELETE", f"/api/history/{huge_id}", None),
            ("POST", f"/api/history/{huge_id}/regenerate", {"mode": "replay"}),
            ("POST", "/api/input-images/from-history", {"image_id": 2**63}),
            ("GET", f"/api/history?page={huge_id}", None),
        ]
        for method, endpoint, body in endpoints:
            with self.subTest(endpoint=endpoint):
                response = await self.client.request(method, endpoint, json=body)
                self.assertEqual(response.status, 400)
                self.assertIn("error", await response.json())

    async def test_unicode_shutdown_token_is_forbidden(self):
        for token in ("不是令牌", "\ud800"):
            with self.subTest(token=repr(token)):
                response = await self.client.post(
                    "/api/launcher-shutdown", json={"token": token}
                )
                self.assertEqual(response.status, 403)
                self.assertIn("error", await response.json())

    async def test_launcher_shutdown_refuses_active_work(self):
        manager = self.app[MANAGER_KEY]
        manager.task = asyncio.create_task(asyncio.Event().wait())
        try:
            response = await self.client.post(
                "/api/launcher-shutdown", json={"token": "test-token"}
            )
            self.assertEqual(response.status, 409)
            self.assertFalse(manager.shutting_down)
            self.assertFalse(manager.task.done())
        finally:
            manager.task.cancel()
            await asyncio.gather(manager.task, return_exceptions=True)
            manager.task = None

    async def test_launcher_shutdown_releases_resources_once_before_exit(self):
        comfy, history = self.app[COMFY_KEY], self.app[HISTORY_KEY]
        with (
            mock.patch("anima_webui.server.os._exit") as exit_process,
            mock.patch.object(comfy, "close", wraps=comfy.close) as close_client,
            mock.patch.object(history, "close", wraps=history.close) as close_history,
        ):
            responses = await asyncio.gather(*[
                self.client.post("/api/launcher-shutdown", json={"token": "test-token"})
                for _ in range(2)
            ])
            self.assertEqual([response.status for response in responses], [200, 200])
            close_client.assert_awaited_once()
            close_history.assert_awaited_once()
            self.assertTrue(self.app[MANAGER_KEY].shutting_down)
            await asyncio.sleep(0.6)
            exit_process.assert_called_once_with(0)

    async def test_api_http_errors_are_json_and_page_cannot_be_framed(self):
        missing = await self.client.get("/api/no-such-endpoint")
        self.assertEqual(missing.status, 404)
        self.assertIn("error", await missing.json())
        wrong_method = await self.client.post("/api/config", json={})
        self.assertEqual(wrong_method.status, 405)
        self.assertIn("GET", wrong_method.headers["Allow"])
        self.assertIn("error", await wrong_method.json())
        page = await self.client.get("/")
        self.assertEqual(page.headers["X-Frame-Options"], "DENY")
        self.assertEqual(page.headers["X-Content-Type-Options"], "nosniff")

    async def test_unexpected_error_is_logged_without_exposing_exception(self):
        with mock.patch(
            "anima_webui.server.PromptPoolStore.list",
            side_effect=RuntimeError("sensitive private storage path"),
        ):
            with self.assertLogs("anima_webui.server", level="ERROR"):
                response = await self.client.get("/api/prompt-pools?model_profile=krea2")
        self.assertEqual(response.status, 500)
        self.assertIn("error", await response.json())
        self.assertNotIn("sensitive", await response.text())
        self.assertEqual((await self.client.get("/api/config")).status, 200)

    async def test_request_cancellation_is_not_converted_to_server_error(self):
        from anima_webui.server import error_middleware

        async def cancelled(_):
            raise asyncio.CancelledError()

        with self.assertRaises(asyncio.CancelledError):
            await error_middleware(mock.Mock(path="/api/config"), cancelled)

    async def test_cleanup_closes_history_even_when_worker_and_client_fail(self):
        app = create_app(
            app_dir=APP_DIR,
            data_dir=Path(self.temp.name) / "cleanup-failure",
            comfy=FakeComfy(),
            anima_tools_dir=Path(self.temp.name) / "tools",
        )
        manager, comfy, history = (
            app[MANAGER_KEY], app[COMFY_KEY], app[HISTORY_KEY]
        )
        with (
            mock.patch.object(manager, "shutdown", side_effect=RuntimeError("worker cleanup")) as shutdown,
            mock.patch.object(comfy, "close", side_effect=RuntimeError("client cleanup")) as close_client,
            mock.patch.object(history, "close", wraps=history.close) as close_history,
        ):
            with self.assertRaisesRegex(RuntimeError, "client cleanup"):
                await app.on_cleanup[-1](app)
        shutdown.assert_awaited_once()
        close_client.assert_awaited_once()
        close_history.assert_awaited_once()
