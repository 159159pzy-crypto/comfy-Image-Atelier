"""Fresh model defaults must not retune existing drafts or generated history."""
import asyncio
import copy
import itertools
from pathlib import Path
from unittest.mock import AsyncMock

from aiohttp.test_utils import TestClient, TestServer
import pytest

from anima_webui.model_profiles import profile_public
from anima_webui.server import MANAGER_KEY, create_app
from anima_webui.workflow import (
    CURRENT_PIPELINE_VERSION, DEFAULT_SETTINGS, WorkflowTemplates,
    default_settings_for_profile, validate_settings,
)
from test_parameter_api_contract import ParameterComfy
from test_server import _write_fake_tools


ROOT = Path(__file__).resolve().parents[1]
BASELINES = {"anima": (30, 4, "er_sde", 15, 16), "krea2": (8, 1, "euler", 4, 8)}


@pytest.mark.parametrize("profile,workbench", list(itertools.product(BASELINES, ("random", "single"))))
def test_fresh_defaults_preserve_modes_switches_and_profile_sampling(profile, workbench):
    steps, cfg, sampler, switch, face_steps = BASELINES[profile]
    raw = default_settings_for_profile(profile, workbench)
    settings = validate_settings(raw)
    assert settings == raw
    assert settings["pipeline_version"] == CURRENT_PIPELINE_VERSION == 5
    assert (settings["steps"], settings["cfg"], settings["sampler_name"], settings["scheduler"]) == (steps, cfg, sampler, "simple")
    assert settings["base_mode"] == ("dual_sampler" if (profile, workbench) == ("anima", "random") else "normal")
    assert settings["count"] == (1 if workbench == "single" else 10)
    assert (settings["width"], settings["height"]) == (832, 1216)
    for dual in (settings["dual"], settings["single"]["dual"]):
        assert (dual["switch_step"], dual["second_cfg"], dual["second_sampler_name"], dual["second_scheduler"], dual["scale"]) == (switch, cfg, sampler, "simple", 1)
    face = settings["upscalers"]["face_refine"]
    assert (face["steps"], face["cfg_scale"], face["sampler"], face["scheduler"], face["denoise_strength"]) == (face_steps, cfg, sampler, "simple", .3)
    assert not any(stage["enabled"] for stage in settings["upscalers"].values())
    assert not settings["zero_negative"]
    assert not settings["krea_variance"]["enabled"]
    assert settings["single"]["dual"]["first_add_noise"]
    assert settings["single"]["dual"]["second_add_noise"]
    assert settings["single"]["dual"]["first_leftover_noise"]
    assert not settings["single"]["dual"]["second_leftover_noise"]
    settings["dual"]["switch_step"] = -1
    assert default_settings_for_profile(profile, workbench)["dual"]["switch_step"] == switch


@pytest.mark.parametrize("profile,workbench,version", list(itertools.product(BASELINES, ("random", "single"), (None, 2, 3, 4, 5))))
def test_partial_saved_settings_keep_all_historical_sampling_fallbacks(profile, workbench, version):
    raw = {"model_profile": profile, "workbench": workbench, "dual": {}, "single": {"dual": {}}, "upscalers": {"face_refine": {}}}
    if version is not None:
        raw["pipeline_version"] = version
    before = copy.deepcopy(raw)
    settings = validate_settings(raw)
    assert raw == before
    mode = "dual_sampler" if (profile, workbench) == ("anima", "random") else "normal"
    assert settings["base_mode"] == mode
    assert (settings["steps"], settings["cfg"], settings["sampler_name"], settings["scheduler"]) == (30 if mode == "dual_sampler" else 20, 8, "euler", "simple")
    for dual in (settings["dual"], settings["single"]["dual"]):
        assert (dual["switch_step"], dual["second_cfg"], dual["second_sampler_name"]) == (15, 8, "euler")
    face = settings["upscalers"]["face_refine"]
    assert (face["steps"], face["cfg_scale"], face["sampler"], face["scheduler"]) == (8, 1, "res_2m_ode", "beta57")
    assert validate_settings(settings) == settings
    raw.pop("dual")
    assert validate_settings(raw)["dual"] is None


@pytest.mark.parametrize("profile", BASELINES)
def test_published_defaults_are_independent_and_complete(profile):
    public = profile_public(profile)
    settings = default_settings_for_profile(profile)
    assert public["dual_defaults"] == settings["dual"]
    assert public["post_sampler_defaults"] == settings["upscalers"]["post_sampler"]
    assert public["latent_sampler_defaults"] == settings["upscalers"]["latent"]["sampler"]
    assert all(settings["upscalers"]["face_refine"][key] == value for key, value in public["face_refine_defaults"].items())
    public["dual_defaults"]["switch_step"] = -1
    public["face_refine_defaults"]["steps"] = -1
    assert profile_public(profile)["dual_defaults"]["switch_step"] == BASELINES[profile][3]
    assert profile_public(profile)["face_refine_defaults"]["steps"] == BASELINES[profile][4]


@pytest.mark.parametrize("profile,workbench,mode", list(itertools.product(BASELINES, ("random", "single"), ("normal", "dual_sampler"))))
def test_generated_graph_uses_profile_defaults_in_both_modes_and_face_stage(profile, workbench, mode):
    settings = default_settings_for_profile(profile, workbench)
    settings["base_mode"] = mode
    settings["upscalers"]["face_refine"]["enabled"] = True
    settings = validate_settings(settings)
    templates = WorkflowTemplates.load(ROOT / "templates")
    graph = templates.submission(settings, 123, 456, "defaults-test", "client", 1)["prompt"]
    steps, cfg, sampler, switch, face_steps = BASELINES[profile]
    sampler_ids = ("691:1240",) if mode == "normal" else ("701:1245", "701:1246")
    for node_id in sampler_ids:
        inputs = graph[node_id]["inputs"]
        assert (inputs["steps"], inputs["cfg"], inputs["sampler_name"], inputs["scheduler"]) == (steps, cfg, sampler, "simple")
    if mode == "dual_sampler":
        assert graph["701:1245"]["inputs"]["start_at_step"] == 0
        assert graph["701:1245"]["inputs"]["end_at_step"] == steps
        assert graph["701:1246"]["inputs"]["start_at_step"] == switch < steps
        assert graph["701:1246"]["inputs"]["end_at_step"] == steps
    face = next(node["inputs"] for node in graph.values() if node["class_type"] == "ForbiddenVisionFaceProcessorIntegrated")
    assert (face["steps"], face["cfg_scale"], face["sampler"], face["scheduler"], face["denoise_strength"]) == (face_steps, cfg, sampler, "simple", .3)


@pytest.mark.parametrize("mapping", (None, {}))
def test_http_defaults_and_submissions_keep_new_values_and_old_values_separate(tmp_path, mapping):
    async def run():
        app = create_app(data_dir=tmp_path / "data", comfy=ParameterComfy(),
                         anima_tools_dir=_write_fake_tools(tmp_path), resource_paths=mapping)
        async with TestClient(TestServer(app)) as client:
            config = await (await client.get("/api/config")).json()
            assert config["defaults"] == default_settings_for_profile()
            assert config["defaults"]["cfg"] == DEFAULT_SETTINGS["cfg"] == 4
            manager = app[MANAGER_KEY]
            manager.start = AsyncMock(return_value={"id": "captured", "status": "queued"})
            for profile, workbench in itertools.product(BASELINES, ("random", "single")):
                fresh = default_settings_for_profile(profile, workbench)
                response = await client.post("/api/batches", json=fresh)
                assert response.status == 201, await response.text()
                actual = manager.start.call_args.args[0]
                for key in ("steps", "cfg", "sampler_name", "scheduler", "base_mode", "dual", "upscalers"):
                    assert actual[key] == fresh[key]
                # Partial saved requests are normalized with their historical
                # defaults even when the launcher supplies new startup values.
                old = {"model_profile": profile, "workbench": workbench, "count": 1}
                response = await client.post("/api/batches", json=old)
                assert response.status == 201, await response.text()
                actual = manager.start.call_args.args[0]
                assert (actual["cfg"], actual["sampler_name"], actual["scheduler"]) == (8, "euler", "simple")
    asyncio.run(run())
