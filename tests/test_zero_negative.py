"""Negative-condition policy survives rendering, persistence and old workflows."""
import asyncio
import copy
import json
from pathlib import Path

from aiohttp.test_utils import TestClient, TestServer
import pytest

from anima_webui.runner import BatchManager
from anima_webui.server import create_app
from anima_webui.style_presets import preset_settings
from anima_webui.workflow import (
    DEFAULT_SETTINGS, WorkflowError, WorkflowTemplates, prepare_templates,
    render_workflows, validate_settings,
)
from test_server import FakeComfy, _write_fake_tools


ROOT = Path(__file__).resolve().parents[1]


@pytest.fixture(scope="module")
def templates():
    return WorkflowTemplates.load(ROOT / "templates")


def submission(templates, **settings):
    return templates.submission(settings, 123, 456, "zero-test", "client", 1)


def assert_visual_edges_match(payload):
    api = payload["prompt"]
    ui = payload["extra_data"]["extra_pnginfo"]["workflow"]
    visual = {n["properties"]["pipeline_api_id"]: n for n in ui["nodes"]}
    links = {link[0]: link for link in ui["links"]}
    for api_id, node in api.items():
        assert visual[api_id]["type"] == node["class_type"]
        for name, value in node["inputs"].items():
            if not (isinstance(value, list) and len(value) == 2 and isinstance(value[0], str)
                    and isinstance(value[1], int)):
                continue
            port = next(p for p in visual[api_id]["inputs"] if p["name"] == name)
            link = links[port["link"]]
            assert link[1:3] == [visual[value[0]]["id"], value[1]]


def test_zero_defaults_and_preset_round_trip():
    assert DEFAULT_SETTINGS["zero_negative"] is False
    assert validate_settings({})["zero_negative"] is False
    for enabled in (False, True):
        saved = preset_settings({"zero_negative": enabled, "negative_prompt": "keep these words"})
        assert preset_settings(saved)["zero_negative"] is enabled
        assert saved["negative_prompt"] == "keep these words"
    single = preset_settings({"workbench": "single", "zero_negative": True,
                              "single": {"zero_negative": False, "secondary_zero_negative": True}})
    assert single["single"]["zero_negative"] is False
    assert single["single"]["secondary_zero_negative"] is True


@pytest.mark.parametrize("value", [None, 0, 1, "true", "false", [], {}])
def test_zero_rejects_non_boolean_values(value):
    with pytest.raises(WorkflowError, match="zero_negative"):
        validate_settings({"zero_negative": value})


@pytest.mark.parametrize("version", [3, 4, 5])
@pytest.mark.parametrize("base_mode", ["normal", "dual_sampler"])
@pytest.mark.parametrize("profile", ["anima", "krea2"])
@pytest.mark.parametrize("enabled", [False, True])
def test_random_zero_routes_follow_the_setting(templates, version, base_mode, profile, enabled):
    payload = submission(templates, pipeline_version=version, base_mode=base_mode,
                         model_profile=profile, zero_negative=enabled, negative_prompt="retain negative",
                         upscalers={"post_sampler": {"enabled": True}, "face_refine": {"enabled": True},
                                    "latent": {"enabled": True}, "seedvr2": {"enabled": True}})
    api = payload["prompt"]
    first = "701:1245" if base_mode == "dual_sampler" else "691:1240"
    final = "701:1246" if base_mode == "dual_sampler" else first
    for node_id in (first, final, "v3:post", "v3:face", *(["v4:latent:sample"] if version >= 4 else [])):
        negative = api[node_id]["inputs"]["negative"]
        node = api[negative[0]]
        assert node["class_type"] == ("ConditioningZeroOut" if enabled else "CLIPTextEncode")
        if enabled:
            node = api[node["inputs"]["conditioning"][0]]
        assert node["inputs"]["text"] == "retain negative"
    assert api["v3:post"]["inputs"]["negative"] == api[final]["inputs"]["negative"]
    assert api["v3:seed:sample"]["inputs"]["negative"] == ["v3:seed:conditioning", 1]
    assert payload["extra_data"]["extra_pnginfo"]["anima_random_webui"]["settings"]["zero_negative"] is enabled
    assert_visual_edges_match(payload)


@pytest.mark.parametrize("version", [2, 5])
@pytest.mark.parametrize("workbench", ["random", "single"])
def test_preflight_requires_zero_only_when_active(templates, version, workbench):
    settings = validate_settings({"pipeline_version": version, "workbench": workbench,
                                  "base_mode": "normal", "zero_negative": workbench == "random",
                                  "single": {"zero_negative": workbench == "single"}})
    graph = submission(templates, **settings)["prompt"]
    resources = {"node_types": sorted({n["class_type"] for n in graph.values()} - {"ConditioningZeroOut"})}
    issues = BatchManager._resource_inventory_issues(settings, resources)
    assert any(item["name"] == "ConditioningZeroOut" for item in issues)
    settings["zero_negative"] = False
    settings["single"]["zero_negative"] = False
    # A dormant secondary flag must not impose a node dependency.
    settings["single"]["secondary_zero_negative"] = True
    issues = BatchManager._resource_inventory_issues(settings, resources)
    assert not any(item["name"] == "ConditioningZeroOut" for item in issues)


@pytest.mark.parametrize("primary,secondary", [(False, True), (True, False)])
@pytest.mark.parametrize("version", [2, 5])
def test_single_keeps_distinct_pipeline_values_and_ignores_random_flag(templates, primary, secondary, version):
    payload = submission(templates, workbench="single", pipeline_version=version, base_mode="dual_sampler",
                         zero_negative=not primary,
                         single={"zero_negative": primary, "secondary_zero_negative": secondary,
                                 "dual": {"first_pipeline": "primary", "second_pipeline": "secondary"}})
    api = payload["prompt"]
    for node_id, enabled in (("701:1245", primary), ("701:1246", secondary)):
        assert api[api[node_id]["inputs"]["negative"][0]]["class_type"] == (
            "ConditioningZeroOut" if enabled else "CLIPTextEncode")
    assert_visual_edges_match(payload)


def test_v2_random_zero_switches_and_export_agree(templates):
    payload = submission(templates, pipeline_version=2, zero_negative=True, base_mode="dual_sampler")
    for node_id in ("1010:1002", "1026:1018"):
        assert payload["prompt"][node_id]["inputs"]["switch"] is True
        ui = payload["extra_data"]["extra_pnginfo"]["workflow"]
        node = next(n for n in ui["nodes"] if n["properties"]["pipeline_api_id"] == node_id)
        assert node["widgets_values"] == [True]
    assert_visual_edges_match(payload)


def test_legacy_embedded_template_zeroes_all_negative_consumers():
    def read(name):
        return json.loads((ROOT / "sources" / name).read_text(encoding="utf-8"))
    api_template, ui_template = prepare_templates(read("AnimaBasicV7 (1).json"), read("AnimaBasicV7 (3).json"))
    settings = {**copy.deepcopy(DEFAULT_SETTINGS), "zero_negative": True,
                "second_sampler": {"enabled": True}, "negative_prompt": "saved negative"}
    api, ui = render_workflows(api_template, ui_template, settings, 1, 2, "legacy")
    assert api["45"]["inputs"]["text"] == "saved negative"
    for node in api.values():
        if "negative" in node["inputs"]:
            assert node["inputs"]["negative"] == ["legacy:negative:zero", 0]
    assert_visual_edges_match({"prompt": api, "extra_data": {"extra_pnginfo": {"workflow": ui}}})


def test_normalize_endpoint_preserves_zero_flags(tmp_path):
    async def run():
        app = create_app(data_dir=tmp_path / "data", comfy=FakeComfy(), anima_tools_dir=_write_fake_tools(tmp_path))
        async with TestClient(TestServer(app)) as client:
            for workbench in ("random", "single"):
                raw = {"workbench": workbench, "zero_negative": True,
                       "single": {"zero_negative": False, "secondary_zero_negative": True}}
                response = await client.post("/api/settings/normalize", json={"settings": raw})
                assert response.status == 200, await response.text()
                normalized = await response.json()
                assert normalized["settings"]["zero_negative"] is True
                assert normalized["settings"]["single"]["zero_negative"] is False
                assert normalized["settings"]["single"]["secondary_zero_negative"] is True
    asyncio.run(run())
