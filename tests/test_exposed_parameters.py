"""Screenshot parameter contracts, migrations, and the original repair chain."""
import copy
import itertools
from pathlib import Path

import pytest

from anima_webui.runner import BatchManager
from anima_webui.sampling import DUAL_DEFAULTS
from anima_webui.style_presets import preset_settings
from anima_webui.workflow import DEFAULT_SETTINGS, WorkflowError, WorkflowTemplates, validate_settings


@pytest.fixture(scope="module")
def templates():
    return WorkflowTemplates.load(Path(__file__).resolve().parents[1] / "templates")


def render(templates, settings):
    return templates.submission(settings, 123, 456, "ParameterContract/test", "client", 1,
                                resolved_prompt="test prompt")["prompt"]


def test_new_defaults_match_the_screenshot_without_overwriting_saved_values():
    assert DEFAULT_SETTINGS["krea_variance"]["enabled"] is False
    for profile, base in itertools.product(("anima", "krea2"), ("normal", "dual_sampler")):
        settings = validate_settings({"model_profile": profile, "base_mode": base})
        assert (settings["steps"], settings["cfg"], settings["sampler_name"], settings["scheduler"]) == (
            20 if base == "normal" else 30, 8, "euler", "simple")
        assert settings["base_denoise"] == 1
        latent = settings["upscalers"]["latent"]
        assert (latent["scale_by"], latent["upscale_method"], latent["model_format"]) == (1.5, "sesqui", "Wan 2.1")
        sampler = latent["second_sampler"]
        assert (sampler["steps"], sampler["cfg"], sampler["sampler_name"], sampler["denoise"]) == (20, 8, "euler", 1)
        face = settings["upscalers"]["face_refine"]
        assert (face["steps"], face["cfg_scale"], face["sampler"], face["scheduler"], face["denoise_strength"]) == (
            8, 1, "res_2m_ode", "beta57", .3)
    saved = {"steps": 12, "cfg": 3, "sampler_name": "heun", "base_denoise": .4,
             "upscalers": {"latent": {"upscale_method": "bicubic", "scale_by": 3.0,
                                       "second_sampler": {"cfg": 2.5}},
                           "face_refine": {"crop_padding": 2.2}, "seedvr2": {"weight_dtype": "fp16"}}}
    restored = validate_settings(saved)
    assert (restored["steps"], restored["cfg"], restored["sampler_name"], restored["base_denoise"]) == (12, 3, "heun", .4)
    assert restored["upscalers"]["latent"]["second_sampler"]["cfg"] == 2.5
    assert restored["upscalers"]["face_refine"]["crop_padding"] == 2.2
    assert restored["upscalers"]["seedvr2"]["weight_dtype"] == "fp16"


def test_shared_variance_and_dlss_migrate_without_enabling_nvidia():
    old = {"workbench": "single", "model_profile": "krea2", "single": {
        "krea_variance": {"enabled": True, "strength": 37},
        "dlss": {"enabled": True, "style": "自然", "intensity": .4, "gpu_gen": "RTX 40"}}}
    original = copy.deepcopy(old)
    settings = validate_settings(old)
    assert old == original
    assert settings["krea_variance"]["enabled"] is True
    assert settings["krea_variance"]["strength"] == 37
    nvidia = settings["upscalers"]["nvidia"]
    assert nvidia["enabled"] is False
    assert "enabled" not in nvidia["dlss"]
    assert (nvidia["dlss"]["style"], nvidia["dlss"]["intensity"], nvidia["dlss"]["gpu_gen"]) == ("自然", .4, "RTX 40")
    assert validate_settings({"model_profile": "krea2"})["krea_variance"]["enabled"] is True
    explicit = validate_settings({**old, "krea_variance": {"enabled": False},
                                  "upscalers": {"nvidia": {"dlss": {"style": "默认"}}}})
    assert explicit["krea_variance"]["enabled"] is False
    assert explicit["krea_variance"]["strength"] == 37
    assert explicit["upscalers"]["nvidia"]["dlss"]["gpu_gen"] == "RTX 40"
    assert explicit["upscalers"]["nvidia"]["dlss"]["style"] == "默认"
    assert validate_settings(settings) == settings
    saved = preset_settings({**old, "base_denoise": .55})
    assert saved["base_denoise"] == .55 and saved["krea_variance"]["strength"] == 37


@pytest.mark.parametrize("workbench,base,profile", list(itertools.product(
    ("random", "single"), ("normal", "dual_sampler"), ("anima", "krea2"))))
@pytest.mark.parametrize("flags", list(itertools.product((False, True), repeat=4)))
def test_every_stage_combination_follows_original_workflow(templates, workbench, base, profile, flags):
    stages = ("latent", "face_refine", "seedvr2", "nvidia")
    values = {name: {"enabled": enabled} for name, enabled in zip(stages, flags)}
    values["nvidia"]["dlss"] = {"style": "自然", "intensity": .6, "local_tone": .7,
                                 "local_struct": .8, "output_mix": .9}
    settings = {"workbench": workbench, "base_mode": base, "model_profile": profile,
                "dual": DUAL_DEFAULTS, "krea_variance": {"enabled": False}, "upscalers": values,
                "single": {"dlss": {"enabled": True}}}
    api = render(templates, settings)
    current = ["691:1413" if base == "normal" else "701:1414", 1]
    mapping = {"latent": ("702:353", "704:1415"), "face_refine": ("1214:1209", "1253:1416"),
               "seedvr2": ("710:388", "712:1417"), "nvidia": ("1425:1419", "1432:1447")}
    for name, enabled in zip(stages, flags):
        selector, endpoint = mapping[name]
        assert (endpoint in api) == enabled
        if enabled:
            assert api[selector]["inputs"]["any_01"] == current
            current = [endpoint, 1]
    assert api["v2:output"]["inputs"]["images"] == current
    assert ("1432:1492" in api) == flags[-1]
    assert "single:dlss" not in api
    if flags[-1]:
        dlss = api["1432:1492"]
        assert dlss["class_type"] == "DLSS5NeuralRender"
        assert dlss["inputs"]["images"] == ["1432:1418", 0]
        assert api["1432:1445"]["inputs"]["images"] == ["1432:1492", 0]
        for name, value in values["nvidia"]["dlss"].items():
            assert dlss["inputs"][name] == value
    for node in api.values():
        for value in node.get("inputs", {}).values():
            if isinstance(value, list) and len(value) == 2 and isinstance(value[0], str) and isinstance(value[1], int):
                assert value[0] in api


@pytest.mark.parametrize("workbench", ("random", "single"))
def test_base_parameters_and_dual_model_format_reach_real_nodes(templates, workbench):
    values = {"workbench": workbench, "base_mode": "normal", "model_profile": "krea2",
              "base_denoise": .42, "krea_variance": {"enabled": True, "strength": 29}}
    api = render(templates, values)
    assert api["691:1240"]["inputs"]["denoise"] == .42
    variance_id = "single:variance" if workbench == "single" else "691:158"
    assert api[variance_id]["inputs"]["strength"] == 29
    dual = {**DUAL_DEFAULTS, "model_format": "Flux", "half_precision": False, "scale": 1.8}
    api = render(templates, {**values, "base_mode": "dual_sampler", "dual": dual, "single": {"dual": dual}})
    assert api["701:1450"]["inputs"]["model_format"] == "Flux"
    assert api["701:1450"]["inputs"]["half_precision"] is False


@pytest.mark.parametrize("workbench", ("random", "single"))
def test_live_preflight_reports_missing_face_options_and_dlss_without_replacing_them(workbench):
    settings = validate_settings({"workbench": workbench, "base_mode": "normal",
                                  "upscalers": {"face_refine": {"enabled": True}, "nvidia": {"enabled": True}}})
    resources = {"node_types": ["RTXVideoSuperResolution"], "samplers": ["euler"], "schedulers": ["simple"],
                 "node_inputs": {"DLSS5NeuralRender": {"style": [["自然"]]}}}
    names = {item["name"] for item in BatchManager._resource_inventory_issues(settings, resources)}
    assert {"res_2m_ode", "beta57", "DLSS5NeuralRender", "style=电影"} <= names
    assert settings["upscalers"]["face_refine"]["sampler"] == "res_2m_ode"


@pytest.mark.parametrize("value", [-.1, 1.1, float("nan"), True, "1"])
def test_base_denoise_rejects_invalid_values(value):
    with pytest.raises(WorkflowError, match="base_denoise"):
        validate_settings({"base_denoise": value})
