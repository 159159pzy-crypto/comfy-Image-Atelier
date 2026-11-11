"""Version-specific configuration and editable-draft upgrades for v4."""
import copy

import pytest

from anima_webui.model_profiles import get_model_profile, profile_public
from anima_webui.runner import BatchManager
from anima_webui.sampling import enabled_postprocess_parameter_issues
from anima_webui.style_presets import preset_settings
from anima_webui.workflow import DEFAULT_SETTINGS, WorkflowError, upgrade_pipeline_settings, validate_settings


@pytest.mark.parametrize("profile,steps,cfg,denoise", [("anima", 16, 4, .2), ("krea2", 10, 1, .3)])
def test_latent_sampler_defaults_are_profile_owned_and_independent(profile, steps, cfg, denoise):
    settings = validate_settings({"pipeline_version": 4, "model_profile": profile})
    sampler = settings["upscalers"]["latent"]["sampler"]
    assert sampler == {"steps": steps, "cfg": cfg, "denoise": denoise,
                       "sampler_name": "er_sde", "scheduler": "simple"}
    assert profile_public(profile)["latent_sampler_defaults"] == sampler
    settings["upscalers"]["post_sampler"]["cfg"] = 12
    assert sampler["cfg"] == cfg
    sampler["steps"] = 2
    assert get_model_profile(profile)["latent_sampler_defaults"]["steps"] == steps


def test_versionless_and_explicit_historical_validation_stay_frozen():
    assert DEFAULT_SETTINGS["pipeline_version"] == 5
    for version in (None, 2, 3):
        source = {"upscalers": {"latent": {"enabled": True}}}
        if version is not None:
            source["pipeline_version"] = version
        normalized = validate_settings(source)
        assert normalized["pipeline_version"] == (version or 2)
        assert "sampler" not in normalized["upscalers"]["latent"]


def test_v2_upgrade_keeps_established_post_sampler_conversion_then_adds_new_sampler():
    source = {"pipeline_version": 2, "model_profile": "anima", "upscalers": {
        "latent": {"enabled": True, "second_sampler": {"enabled": True, "steps": 23, "cfg": 6,
            "sampler_name": "heun", "scheduler": "karras", "denoise": .6}}, "nvidia": {"enabled": True}}}
    original = copy.deepcopy(source)
    upgraded = upgrade_pipeline_settings(source)
    assert upgraded["pipeline_version"] == 4
    assert upgraded["upscalers"]["post_sampler"] == source["upscalers"]["latent"]["second_sampler"]
    assert upgraded["upscalers"]["latent"]["sampler"] == get_model_profile("anima")["latent_sampler_defaults"]
    assert upgraded["upscalers"]["latent"]["enabled"] and upgraded["upscalers"]["dlss"]["enabled"]
    assert source == original


@pytest.mark.parametrize("post_enabled", [False, True])
def test_v3_upgrade_retains_existing_switches_and_parameters(post_enabled):
    source = validate_settings({"pipeline_version": 3, "model_profile": "krea2", "upscalers": {
        "latent": {"enabled": True, "second_sampler": {"steps": 40, "cfg": 9, "denoise": .9}},
        "post_sampler": {"enabled": post_enabled, "steps": 7, "cfg": 2, "denoise": .15},
        "pixel_hires": {"enabled": True}, "dlss": {"enabled": False}}})
    original = copy.deepcopy(source)
    upgraded = upgrade_pipeline_settings(source)
    assert upgraded["pipeline_version"] == 4
    assert upgraded["upscalers"]["latent"]["sampler"] == get_model_profile("krea2")["latent_sampler_defaults"]
    for stage in ("pixel_hires", "post_sampler", "dlss", "seedvr2", "nvidia", "face_refine"):
        assert upgraded["upscalers"][stage] == source["upscalers"][stage]
    assert source == original


@pytest.mark.parametrize("version", [4, 5])
def test_current_versions_are_idempotent_and_reject_future_version_instead_of_downgrading(version):
    current = validate_settings({"pipeline_version": version, "upscalers": {"latent": {
        "sampler": {"steps": 9, "cfg": 3, "denoise": .4, "sampler_name": "euler", "scheduler": "karras"}}}})
    assert upgrade_pipeline_settings(current) == current
    for operation in (validate_settings, upgrade_pipeline_settings):
        with pytest.raises(WorkflowError, match="pipeline_version"):
            operation({"pipeline_version": 6})


@pytest.mark.parametrize("sampler", [None, False, {"enabled": False}, {"seed": 1}, {"cfg": float("nan")}, {"steps": True}, {"sampler_name": ""}])
@pytest.mark.parametrize("version", [2, 3, 4, 5])
def test_latent_sampler_has_exactly_five_parameters_and_no_additional_toggle(sampler, version):
    with pytest.raises(WorkflowError, match="latent.sampler"):
        validate_settings({"pipeline_version": version, "upscalers": {"latent": {"sampler": sampler}}})


@pytest.mark.parametrize("method,accepted,rejected", [("bicubic", 8, 8.01), ("sesqui", 2, 2.01)])
def test_v4_latent_algorithm_uses_native_range(method, accepted, rejected):
    def settings(scale):
        return {"pipeline_version": 4, "base_mode": "normal", "upscalers": {
            "latent": {"enabled": True, "upscale_method": method, "scale_by": scale}}}
    assert validate_settings(settings(accepted))["upscalers"]["latent"]["scale_by"] == accepted
    with pytest.raises(WorkflowError, match="scale_by"):
        validate_settings(settings(rejected))


@pytest.mark.parametrize("version", [2, 3])
def test_historical_native_latent_range_is_not_rewritten(version):
    source = {"pipeline_version": version, "base_mode": "normal", "upscalers": {
        "latent": {"enabled": True, "upscale_method": "bicubic", "scale_by": 12}}}
    assert validate_settings(source)["upscalers"]["latent"]["scale_by"] == 12
    with pytest.raises(WorkflowError, match="0.01-8"):
        upgrade_pipeline_settings(source)


def test_v4_latent_sampler_resources_and_schema_are_checked_only_when_enabled():
    settings = validate_settings({"pipeline_version": 4, "base_mode": "normal", "upscalers": {
        "latent": {"enabled": True, "sampler": {"sampler_name": "heun", "scheduler": "karras", "denoise": .8}}}})
    resources = {"models": [settings["model_name"]], "samplers": [settings["sampler_name"]],
                 "schedulers": [settings["scheduler"]]}
    assert {(issue["type"], issue["name"]) for issue in BatchManager._resource_inventory_issues(settings, resources)} == {
        ("latent_sampler", "heun"), ("latent_scheduler", "karras")}
    schema = {"node_inputs": {"KSampler": {"denoise": ["FLOAT", {"max": .5}]}}}
    issues = enabled_postprocess_parameter_issues(settings, schema)
    assert len(issues) == 1 and "潜空间重采样" in issues[0]["label"]
    settings["upscalers"]["latent"]["enabled"] = False
    assert BatchManager._resource_inventory_issues(settings, resources) == []
    assert enabled_postprocess_parameter_issues(settings, schema) == []


def test_v4_preset_roundtrip_keeps_latent_and_post_sampler_independent():
    value = {"pipeline_version": 4, "model_profile": "krea2", "upscalers": {
        "latent": {"enabled": True, "sampler": {"steps": 13, "cfg": 2}},
        "post_sampler": {"enabled": True, "steps": 6, "cfg": 1}}}
    saved = preset_settings(value, include={"repair": True})
    assert "pipeline_version" not in saved
    assert saved["upscalers"]["latent"]["sampler"]["steps"] == 13
    assert saved["upscalers"]["post_sampler"]["steps"] == 6
    assert preset_settings(saved, include={"repair": True}, source={"pipeline_version": 4}) == saved
