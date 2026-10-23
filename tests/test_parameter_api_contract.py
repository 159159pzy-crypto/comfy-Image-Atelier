"""Launcher defaults must not shadow parameter migration at the HTTP edge."""
import asyncio
from unittest.mock import AsyncMock

from aiohttp.test_utils import TestClient, TestServer
import pytest

from anima_webui.model_profiles import MODEL_PROFILES
from anima_webui.server import MANAGER_KEY, create_app
from anima_webui.workflow import validate_settings
from test_server import FakeComfy, _write_fake_tools


class ParameterComfy(FakeComfy):
    async def resource_inventory(self):
        resources = await super().resource_inventory()
        resources["models"] = [profile["model_name"] for profile in MODEL_PROFILES.values()]
        return resources


@pytest.mark.parametrize("mapping", [None, {}])
def test_launcher_submission_keeps_legacy_values_and_mode_defaults(tmp_path, mapping):
    async def run():
        app = create_app(
            data_dir=tmp_path / "data", comfy=ParameterComfy(),
            anima_tools_dir=_write_fake_tools(tmp_path), resource_paths=mapping,
        )
        async with TestClient(TestServer(app)) as client:
            manager = app[MANAGER_KEY]
            manager.start = AsyncMock(return_value={"id": "captured", "status": "queued"})
            cases = [
                ({"workbench": "single", "model_profile": "krea2", "base_mode": "normal",
                  "single": {"krea_variance": {"enabled": True, "strength": 33},
                             "dlss": {"enabled": True, "style": "自然", "local_tone": .4}}}, True),
                ({"workbench": "random", "model_profile": "krea2", "base_mode": "normal"}, True),
                ({"workbench": "single", "model_profile": "krea2", "base_mode": "normal",
                  "krea_variance": {"enabled": False},
                  "single": {"krea_variance": {"enabled": True}}}, False),
                ({"workbench": "random", "model_profile": "anima", "base_mode": "dual_sampler",
                  "steps": 24, "cfg": 2.5, "sampler_name": "heun",
                  "upscalers": {"latent": {"scale_by": 1.7, "upscale_method": "bicubic"}}}, False),
            ]
            for raw, expected_variance in cases:
                response = await client.post("/api/batches", json={**raw, "loras": [], "count": 1})
                assert response.status == 201, await response.text()
                submitted = validate_settings(manager.start.call_args.args[0])
                assert submitted["steps"] == raw.get("steps", 20)
                assert submitted["dual"] is None
                assert submitted["krea_variance"]["enabled"] is expected_variance
                if raw.get("single", {}).get("dlss"):
                    assert submitted["krea_variance"]["strength"] == 33
                    assert submitted["upscalers"]["nvidia"]["dlss"]["style"] == "自然"
                    assert submitted["upscalers"]["nvidia"]["dlss"]["local_tone"] == .4
                    assert not submitted["upscalers"]["nvidia"]["enabled"]
    asyncio.run(run())
