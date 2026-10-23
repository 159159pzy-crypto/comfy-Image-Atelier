"""Public random dual controls and legacy frozen-graph compatibility."""
import copy
from pathlib import Path

import pytest

from anima_webui.runner import BatchManager
from anima_webui.sampling import DUAL_DEFAULTS
from anima_webui.style_presets import preset_settings
from anima_webui.workflow import WorkflowError, WorkflowTemplates, migrate_settings, validate_settings


@pytest.fixture(scope="module")
def templates():
    return WorkflowTemplates.load(Path(__file__).resolve().parents[1] / "templates")


def submit(templates, **overrides):
    settings = {"workbench": "random", "model_profile": "anima", "base_mode": "dual_sampler", **overrides}
    return templates.submission(settings, 123, 456, "Compatibility/test", "client", 1, resolved_prompt="fixed cat")


@pytest.mark.parametrize("steps", [8, 30, 40])
def test_legacy_random_graph_preserves_explicit_sampling_values(templates, steps):
    values = {"steps": steps, "cfg": 4, "sampler_name": "er_sde", "scheduler": "simple",
              "loras": [{"filename": "style.safetensors", "enabled": True, "strength": .7}],
              "upscalers": {"latent": {"enabled": True, "scale_by": 1.75, "upscale_method": "bicubic"}}}
    graphs = [submit(templates, **values, **version)["prompt"] for version in ({}, {"dual": None})]
    assert graphs[0] == graphs[1]
    for node_id in ("701:1245", "701:1246"):
        sampler = graphs[0][node_id]["inputs"]
        assert (sampler["steps"], sampler["cfg"], sampler["sampler_name"]) == (steps, 4, "er_sde")
    assert graphs[0]["701:1246"]["inputs"]["start_at_step"] == ["701:802", 0]
    assert graphs[0]["701:1450"]["inputs"]["scale_by"] == 1.75


@pytest.mark.parametrize("profile", ["anima", "krea2"])
def test_explicit_dual_binds_independent_parameters_without_rewiring_random_prompt(templates, profile):
    values = {"model_profile": profile, "steps": 24, "cfg": 3, "sampler_name": "euler", "scheduler": "simple",
              "loras": [{"filename": "style.safetensors", "enabled": True, "strength": .7}]}
    legacy = submit(templates, **values)["prompt"]
    payload = submit(templates, **values, dual={"switch_step": 9, "scale": 1.7, "second_cfg": 5,
        "second_sampler_name": "heun", "second_scheduler": "beta", "upscale_method": "sesqui"})
    api = payload["prompt"]
    first, second = (api[node]["inputs"] for node in ("701:1245", "701:1246"))
    assert first["steps"] == first["end_at_step"] == second["steps"] == second["end_at_step"] == 24
    assert first["start_at_step"] == 0 and second["start_at_step"] == 9
    assert (first["cfg"], first["sampler_name"], first["scheduler"]) == (3, "euler", "simple")
    assert (second["cfg"], second["sampler_name"], second["scheduler"]) == (5, "heun", "beta")
    for node_id in ("701:1245", "701:1246"):
        for key in ("model", "positive", "negative", "latent_image", "noise_seed", "add_noise", "return_with_leftover_noise"):
            assert api[node_id]["inputs"][key] == legacy[node_id]["inputs"][key]
    for node_id in ("72", "243", "701:1242", "701:538", "1010:995", "1026:1011"):
        assert api[node_id] == legacy[node_id]
    assert api["701:1450"]["class_type"] == "SesquiLatentUpscale"
    assert api["701:1450"]["inputs"] == {"latent": ["701:1245", 0], "scale": 1.7, "model_format": "Wan 2.1", "half_precision": True}
    assert payload["extra_data"]["extra_pnginfo"]["anima_random_webui"]["settings"]["dual"]["switch_step"] == 9


def test_dual_resize_and_optional_latent_repair_are_independent(templates):
    dual = {**DUAL_DEFAULTS, "upscale_method": "bicubic", "scale": 2.5}
    latent = {"enabled": True, "scale_by": 1.6, "upscale_method": "sesqui", "model_format": "Flux", "half_precision": False}
    api = submit(templates, dual=dual, upscalers={"latent": latent})["prompt"]
    assert api["701:1450"]["inputs"] == {"samples": ["701:1245", 0], "scale_by": 2.5, "upscale_method": "bicubic"}
    assert api["704:1064"]["class_type"] == "SesquiLatentUpscale"
    assert api["704:1064"]["inputs"] == {"latent": ["704:1182", 0], "scale": 1.6, "model_format": "Flux", "half_precision": False}
    different = submit(templates, dual=dual, upscalers={"latent": {**latent, "enabled": False, "scale_by": 3}})["prompt"]
    assert different["701:1450"] == api["701:1450"]
    assert not any(node.startswith("704:") for node in different)
    no_resample = submit(templates, dual=dual, upscalers={"latent": {**latent, "second_sampler": {"enabled": False}}})["prompt"]
    assert "704:1248" not in no_resample
    assert no_resample["704:359"]["inputs"]["samples"] == ["704:1064", 0]


def test_ordinary_mode_retains_hidden_dual_values_without_cross_field_or_resource_checks(templates):
    values = {**DUAL_DEFAULTS, "switch_step": 20, "scale": 7, "second_sampler_name": "missing", "second_scheduler": "missing"}
    settings = validate_settings({"base_mode": "normal", "steps": 8, "dual": values})
    assert settings["dual"] == values
    assert BatchManager._resource_inventory_issues(settings, {"node_types": []}) == []
    assert not any(key.startswith("701:") for key in submit(templates, **settings)["prompt"])
    with pytest.raises(WorkflowError, match="起始步"):
        validate_settings({**settings, "base_mode": "dual_sampler"})


@pytest.mark.parametrize("dual", [False, [], {"unknown": 1}, {"switch_step": True}, {"scale": float("nan")},
                                 {"scale": 2.1}, {"second_cfg": 31}, {"upscale_method": "bad"}, {"second_sampler_name": ""}])
def test_explicit_dual_invalid_parameters_are_rejected(dual):
    with pytest.raises(WorkflowError):
        validate_settings({"base_mode": "dual_sampler", "dual": dual})


def test_legacy_and_explicit_presets_remain_distinct():
    legacy = {"base_mode": "dual_sampler", "steps": 8, "upscalers": {"latent": {"scale_by": 1.75}}}
    for saved in (validate_settings(legacy), preset_settings(legacy)):
        assert saved["dual"] is None
        assert validate_settings(migrate_settings(saved))["dual"] is None
    explicit = preset_settings({"base_mode": "dual_sampler", "dual": {**DUAL_DEFAULTS, "scale": 1.8}})
    assert explicit["dual"]["scale"] == 1.8
    assert preset_settings(explicit)["dual"] == explicit["dual"]
    assert explicit["single"]["dual"]["scale"] == 1


def test_random_sampling_preflight_uses_enabled_nodes_and_live_ranges():
    settings = validate_settings({"base_mode": "dual_sampler", "dual": DUAL_DEFAULTS})
    resources = {"node_types": ["KSamplerAdvanced"], "samplers": ["euler"], "schedulers": ["simple"]}
    assert {item["name"] for item in BatchManager._resource_inventory_issues(settings, resources)} == {"SesquiLatentUpscale"}
    resources["node_types"].append("SesquiLatentUpscale")
    resources["node_inputs"] = {"SesquiLatentUpscale": {"scale": ["FLOAT", {"min": 1.5, "max": 2}], "model_format": [["Flux"]]}}
    names = {item["name"] for item in BatchManager._resource_inventory_issues(settings, resources)}
    assert names == {"scale=1.0", "model_format=Wan 2.1"}
    settings["dual"]["second_sampler_name"] = "missing"
    assert "missing" in {item["name"] for item in BatchManager._resource_inventory_issues(settings, resources)}
    settings["base_mode"] = "normal"
    assert BatchManager._resource_inventory_issues(settings, resources) == []


def test_single_contract_and_preview_metadata_stay_separate(templates):
    dual = {**DUAL_DEFAULTS, "switch_step": 19, "scale": 1.8}
    single = {"dual": {"switch_step": 6, "scale": 1.2}, "latent": {"upscale_method": "bicubic"}}
    payload = submit(templates, workbench="single", dual=dual, single=single,
                     upscalers={"latent": {"enabled": True, "upscale_method": "sesqui", "scale_by": 3}})
    assert payload["prompt"]["701:1246"]["inputs"]["start_at_step"] == 6
    assert payload["prompt"]["701:1450"]["inputs"]["scale"] == 1.2
    assert payload["prompt"]["704:1064"]["inputs"]["upscale_method"] == "bicubic"
    assert payload["extra_data"]["preview_method"] == "latent2rgb"
    assert "workflow" in payload["extra_data"]["extra_pnginfo"]
    assert "preview_method" not in submit(templates)["extra_data"]
    assert single == {"dual": {"switch_step": 6, "scale": 1.2}, "latent": {"upscale_method": "bicubic"}}


@pytest.mark.parametrize("stage,values,schemas,expected", [
    ("latent", {"second_sampler": {"steps": 120}}, {"KSampler": {"steps": ["INT", {"min": 1, "max": 100}]}}, {"steps=120"}),
    ("seedvr2", {"encode": {"temporal_overlap": 0}, "decode": {"tile_size": 8192}, "weight_dtype": "fp16", "color_correction_method": "obsolete"},
     {"VAEEncodeTiled": {"temporal_overlap": ["INT", {"min": 4, "max": 4096}]},
      "VAEDecodeTiled": {"tile_size": ["INT", {"min": 64, "max": 4096}]},
      "UNETLoader": {"weight_dtype": [["default"]]},
      "SeedVR2PostProcessing": {"color_correction_method": [["lab", "none"]]}},
     {"temporal_overlap=0", "tile_size=8192", "weight_dtype=fp16", "color_correction_method=obsolete"}),
    ("nvidia", {"scale": 8, "quality": "obsolete"}, {"RTXVideoSuperResolution": {
        "resize_type": ["COMFY_DYNAMICCOMBO_V3", {"options": [{"key": "scale by multiplier", "inputs": {"required": {"scale": ["FLOAT", {"min": 1, "max": 4}]}}}]}],
        "quality": ["COMBO", {"options": ["HIGH", "ULTRA"]}]}}, {"resize_type.scale=8.0", "quality=obsolete"}),
    ("nvidia", {"resize_type": "target dimensions"}, {"RTXVideoSuperResolution": {
        "resize_type": ["COMFY_DYNAMICCOMBO_V3", {"options": [{"key": "target dimensions", "inputs": {"required": {"width": ["INT", {"min": 64, "max": 16384}]}}}]}]}}, {"resize_type.width"}),
    ("face_refine", {"steps": 120, "manual_rotation": "90", "processing_resolution": 256}, {"ForbiddenVisionFaceProcessorIntegrated": {
        "steps": ["INT", {"min": 1, "max": 100}], "manual_rotation": [["None", "90° CW", "90° CCW", "180°"]],
        "processing_resolution": ["INT", {"min": 512, "max": 2048}]}}, {"steps=120", "manual_rotation=90", "processing_resolution=256"}),
])
def test_both_workbenches_validate_enabled_repair_inputs_against_the_same_live_schema(stage, values, schemas, expected):
    for workbench in ("random", "single"):
        settings = validate_settings({"workbench": workbench, "base_mode": "normal", "upscalers": {stage: {"enabled": True, **values}}})
        resources = {"node_inputs": schemas}
        issues = BatchManager._resource_inventory_issues(settings, resources)
        assert {issue["name"] for issue in issues} == expected, (workbench, issues)
        settings["upscalers"][stage]["enabled"] = False
        assert BatchManager._resource_inventory_issues(settings, resources) == []


REPAIR_VALUES = {
    "latent": {"enabled": True, "scale_by": 1.6, "upscale_method": "bicubic", "second_sampler": {
        "enabled": True, "steps": 17, "cfg": 5.5, "sampler_name": "euler", "scheduler": "karras", "denoise": .42}},
    "seedvr2": {"enabled": True, "model_name": "SeedVR2/custom.safetensors", "vae_name": "SeedVR2/custom-vae.safetensors",
        "scale_by": 2.75, "weight_dtype": "fp16", "color_correction_method": "lab",
        "sampler": {"steps": 5, "cfg": 2.5, "sampler_name": "heun", "scheduler": "normal", "denoise": .8},
        "encode": {"tile_size": 640, "overlap": 48, "temporal_size": 80, "temporal_overlap": 4},
        "decode": {"tile_size": 768, "overlap": 96, "temporal_size": 2048, "temporal_overlap": 6}},
    "nvidia": {"enabled": True, "resize_type": "scale by multiplier", "scale": 3, "quality": "HIGH"},
    "face_refine": {"enabled": True, "steps": 11, "cfg_scale": 1.7, "sampler": "heun", "scheduler": "normal",
        "denoise_strength": .55, "face_selection": 1, "detection_confidence": .83, "manual_rotation": "90° CW",
        "processing_resolution": 1536, "upscaler_model": "RealESRGAN x4", "crop_padding": 2.1,
        "face_positive_prompt": "clear eyes", "replace_positive_prompt": True, "face_negative_prompt": "blur",
        "replace_negative_prompt": True, "exclusions": "profile", "blend_softness": 10, "mask_expansion": 3,
        "sampling_mask_blur_size": 25, "sampling_mask_blur_strength": .6, "enable_pre_upscale": False,
        "enable_color_correction": False, "enable_segmentation": False, "enable_differential_diffusion": False,
        "enable_lightness_rescue": False, "enable_final_refinement": False, "offload_models_to_cpu": False},
}


@pytest.mark.parametrize("enabled", [("latent",), ("seedvr2",), ("nvidia",), ("face_refine",), tuple(REPAIR_VALUES)])
@pytest.mark.parametrize("base", ["normal", "dual_sampler"])
@pytest.mark.parametrize("profile", ["anima", "krea2"])
def test_each_repair_and_combined_chain_bind_the_same_public_values_in_both_workbenches(templates, enabled, base, profile):
    stages = copy.deepcopy(REPAIR_VALUES)
    for name, values in stages.items():
        values["enabled"] = name in enabled
    for workbench in ("random", "single"):
        settings = {"workbench": workbench, "model_profile": profile, "base_mode": base, "dual": DUAL_DEFAULTS,
                    "upscalers": stages, "single": {"latent": {"upscale_method": "bicubic"}}}
        api = submit(templates, **settings)["prompt"]
        for node_id, node in api.items():
            for name, value in node["inputs"].items():
                if isinstance(value, list) and len(value) == 2 and isinstance(value[0], str) and isinstance(value[1], int):
                    assert value[0] in api, f"{workbench}: {node_id}.{name} -> {value}"
        for stage, prefix in {"latent": "704:", "seedvr2": "712:", "nvidia": "1432:", "face_refine": "1253:"}.items():
            assert any(key.startswith(prefix) for key in api) == (stage in enabled)
        if "latent" in enabled:
            assert api["704:1064"]["class_type"] == "LatentUpscaleBy"
            assert api["704:1064"]["inputs"]["scale_by"] == 1.6
            assert api["704:1064"]["inputs"]["upscale_method"] == "bicubic"
            for name, value in stages["latent"]["second_sampler"].items():
                if name != "enabled":
                    assert api["704:1248"]["inputs"][name] == value
        if "seedvr2" in enabled:
            seed = stages["seedvr2"]
            assert api["712:716"]["inputs"]["unet_name"] == seed["model_name"]
            assert api["712:716"]["inputs"]["weight_dtype"] == seed["weight_dtype"]
            assert api["712:715"]["inputs"]["vae_name"] == seed["vae_name"]
            assert api["712:907"]["inputs"]["value"] == seed["scale_by"]
            assert api["712:721"]["inputs"]["color_correction_method"] == seed["color_correction_method"]
            for group, node_id in (("sampler", "712:1208"), ("encode", "712:1177"), ("decode", "712:718")):
                for name, value in seed[group].items():
                    assert api[node_id]["inputs"][name] == value
        if "nvidia" in enabled:
            nvidia = api["1432:1418"]["inputs"]
            assert (nvidia["resize_type"], nvidia["resize_type.scale"], nvidia["quality"]) == ("scale by multiplier", 3, "HIGH")
        if "face_refine" in enabled:
            for name, value in stages["face_refine"].items():
                if name != "enabled":
                    assert api["1253:1215"]["inputs"][name] == value
