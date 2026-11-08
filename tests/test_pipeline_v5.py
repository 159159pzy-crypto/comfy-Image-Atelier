"""v5 fixes pixel resize coupling without rewriting historical execution."""
import asyncio
import copy
import itertools
import json
from pathlib import Path

import pytest

from anima_webui.launcher import configure_resources
from anima_webui.pipeline_v3 import required_node_classes
from anima_webui.runner import BatchManager
from anima_webui.sampling import enabled_postprocess_parameter_issues
from anima_webui.style_presets import StylePresetStore, preset_settings
from anima_webui.workflow import CURRENT_PIPELINE_VERSION, DEFAULT_SETTINGS, WorkflowTemplates, validate_settings


ROOT = Path(__file__).resolve().parents[1]


@pytest.fixture(scope="module")
def templates():
    return WorkflowTemplates.load(ROOT / "templates")


def pixel_settings(version, *, post=True, latent=False, percent=50, method="lanczos", workbench="single"):
    return validate_settings({"pipeline_version": version, "base_mode": "normal", "workbench": workbench,
                              "upscalers": {"pixel_hires": {"enabled": True, "percent": percent, "upscale_method": method},
                                            "post_sampler": {"enabled": post}, "latent": {"enabled": latent}}})


@pytest.mark.parametrize("post,latent,percent,method,workbench", list(itertools.product(
    (False, True), (False, True), (25, 100), ("nearest-exact", "lanczos"), ("single", "random"))))
def test_v5_always_applies_pixel_resize_and_other_graph_edges_match_v4(templates, post, latent, percent, method, workbench):
    graphs = {}
    for version in (4, 5):
        settings = pixel_settings(version, post=post, latent=latent, percent=percent, method=method, workbench=workbench)
        payload = templates.submission(settings, 12, 34, "test", "client", 1, execution_nonce=56)
        graphs[version] = payload["prompt"]
        assert payload["extra_data"]["extra_pnginfo"]["workflow"]["extra"]["pipeline"]["version"] == version
    new = graphs[5]
    assert new["v3:pixel:resize"]["inputs"] == {
        "image": ["v3:pixel:upscale", 0], "upscale_method": method, "scale_by": percent / 100}
    assert ("v3:pixel:resize" in graphs[4]) == (not post)
    if post:
        # v5 only inserts the requested resize into this formerly bypassed
        # image edge; all model, conditioning, sampling and other stages match.
        restored = copy.deepcopy(new)
        restored.pop("v3:pixel:resize")
        for node in restored.values():
            for key, value in node["inputs"].items():
                if value == ["v3:pixel:resize", 0]:
                    node["inputs"][key] = ["v3:pixel:upscale", 0]
        assert restored == graphs[4]
    else:
        assert new == graphs[4]


@pytest.mark.parametrize("version", [3, 4, 5])
def test_resize_resource_preflight_matches_the_versioned_graph(templates, version):
    settings = pixel_settings(version)
    graph = templates.submission(settings, 12, 34, "test", "client", 1)["prompt"]
    advertised = {node["class_type"] for node in graph.values()} - {"ImageScaleBy"}
    issues = BatchManager._resource_inventory_issues(settings, {"node_types": sorted(advertised)})
    assert ("ImageScaleBy" in required_node_classes(settings)) == (version == 5)
    assert any(issue["name"] == "ImageScaleBy" for issue in issues) == (version == 5)
    schema = {"node_inputs": {"ImageScaleBy": {"upscale_method": [["bicubic"]]}}}
    parameter_issues = enabled_postprocess_parameter_issues(settings, schema)
    assert any("像素高清缩图" in issue["label"] for issue in parameter_issues) == (version == 5)


def test_launcher_uses_shared_new_version_but_versionless_settings_stay_legacy(templates):
    assert CURRENT_PIPELINE_VERSION == DEFAULT_SETTINGS["pipeline_version"] == 5
    assert configure_resources(templates, None)["pipeline_version"] == 5
    assert validate_settings({})["pipeline_version"] == 2


@pytest.mark.parametrize("version", [None, 2, 3, 4, 5])
@pytest.mark.parametrize("hidden_sampler", [False, True])
def test_preset_save_reload_and_edit_retain_source_version_and_nested_settings(tmp_path, version, hidden_sampler):
    source = {"base_mode": "dual_sampler", "dual": None, "workbench": "single",
              "single": {"dual": {"second_pipeline": "secondary"}, "latent": {"pipeline": "secondary"}},
              "upscalers": {"latent": {"enabled": False, "second_sampler": {"enabled": True, "steps": 23}},
                            "nvidia": {"enabled": False, "dlss": {"intensity": .6}},
                            "post_sampler": {"enabled": True, "steps": 7}}}
    if version is not None:
        source["pipeline_version"] = version
    sampler = {"steps": 11, "cfg": 3, "sampler_name": "heun", "scheduler": "karras", "denoise": .4}
    if hidden_sampler:
        source["upscalers"]["latent"]["sampler"] = sampler
    expected = preset_settings(source)
    assert expected["pipeline_version"] == (version or 2)
    assert expected["dual"] is None
    assert expected["upscalers"]["latent"]["second_sampler"]["enabled"]
    assert expected["upscalers"]["latent"]["second_sampler"]["steps"] == 23
    assert expected["upscalers"]["nvidia"]["dlss"]["intensity"] == .6
    assert expected["single"]["latent"]["pipeline"] == "secondary"
    assert ("sampler" in expected["upscalers"]["latent"]) == (hidden_sampler or (version or 2) >= 4)
    if hidden_sampler:
        assert expected["upscalers"]["latent"]["sampler"] == sampler

    async def scenario():
        path = tmp_path / "presets.json"
        store = StylePresetStore(path)
        item = await store.create({"name": "Legacy", "settings": source})
        assert item["settings"] == expected
        before = path.read_bytes()
        reloaded = StylePresetStore(path)
        assert path.read_bytes() == before
        assert reloaded.items[0]["settings"] == expected
        edited = await reloaded.update(item["id"], {"name": "Edited"})
        assert edited["settings"] == expected
        assert StylePresetStore(path).items[0]["settings"] == expected
    asyncio.run(scenario())


@pytest.mark.parametrize("version", [None, 2, 3])
@pytest.mark.parametrize("enabled", [False, True])
def test_legacy_hidden_sampler_roundtrip_does_not_change_graph_or_resource_requirements(templates, version, enabled):
    source = {"base_mode": "normal", "upscalers": {"latent": {"enabled": enabled}}}
    if version is not None:
        source["pipeline_version"] = version
    original = validate_settings(source)
    sampler = {"steps": 11, "cfg": 3, "sampler_name": "not-installed", "scheduler": "also-absent", "denoise": .4}
    source["upscalers"]["latent"]["sampler"] = sampler
    saved = validate_settings(source)
    assert saved["upscalers"]["latent"]["sampler"] == sampler
    assert validate_settings(saved) == saved
    assert required_node_classes(saved) == required_node_classes(original)
    resources = {"samplers": ["euler"], "schedulers": ["simple"]}
    assert BatchManager._resource_inventory_issues(saved, resources) == BatchManager._resource_inventory_issues(original, resources)
    schema = {"node_inputs": {"KSampler": {"sampler_name": [["euler"]], "scheduler": [["simple"]]}}}
    assert enabled_postprocess_parameter_issues(saved, schema) == enabled_postprocess_parameter_issues(original, schema)
    before = templates.submission(original, 12, 34, "test", "client", 1, execution_nonce=56)["prompt"]
    after = templates.submission(saved, 12, 34, "test", "client", 1, execution_nonce=56)["prompt"]
    assert after == before


def test_loading_versionless_preset_does_not_write_current_version_to_disk(tmp_path):
    path = tmp_path / "presets.json"
    source = {"hires": {"enabled": True, "percent": 50},
              "second_sampler": {"enabled": True, "steps": 23, "denoise": .6}}
    path.write_text(json.dumps({"version": 2, "items": [{"id": "old", "name": "Legacy", "settings": source}]}), encoding="utf-8")
    before = path.read_bytes()
    saved = StylePresetStore(path).items[0]["settings"]
    assert path.read_bytes() == before
    assert saved["pipeline_version"] == 2
    assert saved["upscalers"]["latent"]["scale_by"] == 2
    assert saved["upscalers"]["latent"]["second_sampler"]["steps"] == 23
    assert not saved["upscalers"]["pixel_hires"]["enabled"]
