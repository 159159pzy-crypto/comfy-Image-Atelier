import copy
import json
from pathlib import Path

import pytest

from anima_webui.single_workbench import compose_single_prompt, effective_single_prompts, single_resource_issues
from anima_webui.workflow import WorkflowError, WorkflowTemplates, validate_settings


ROOT = Path(__file__).resolve().parents[1]


@pytest.fixture(scope="module")
def templates():
    return WorkflowTemplates.load(ROOT / "templates")


def submit(templates, overrides=None, runtime=None):
    settings = {"workbench": "single", "single": {"positive_prompt": "a cat"}}
    settings.update(overrides or {})
    return templates.submission(settings, 123, 456, "Single/test", "client", 1, single_runtime=runtime)


def assert_connected(api):
    for node_id, node in api.items():
        for name, value in node["inputs"].items():
            if isinstance(value, list) and len(value) == 2 and isinstance(value[0], str) and isinstance(value[1], int):
                assert value[0] in api, f"{node_id}.{name} -> {value}"


def test_single_defaults_preserve_random_contract():
    old = validate_settings({"steps": 8})
    assert old["workbench"] == "random"
    single = validate_settings({"workbench": "single"})
    assert single["count"] == 1
    assert single["base_mode"] == "normal"
    assert single["single"]["resize_mode"] == "crop"
    assert single["single"]["dual"]["scale"] == 1.0
    assert not any(stage["enabled"] for stage in single["upscalers"].values())
    assert not any(stage["enabled"] for stage in single["single"]["prompt_processing"].values())


@pytest.mark.parametrize("single", [
    {"runtime": {"input_image": "x.png"}}, {"input_image_id": "../x.png"},
    {"resize_mode": "original"}, {"denoise": float("nan")}, {"denoise": True},
    {"vae_decode": {"overlap": 512}}, {"dual": {"switch_step": 30}},
    {"vae_decode": {"temporal_overlap": 0}},
    {"dual": {"scale": 2.1}}, {"dlss": {"intensity": 1.1}},
    {"prompt_processing": {"expand": {"enabled": True}}},
])
def test_reject_invalid_single_settings(single):
    with pytest.raises(WorkflowError):
        validate_settings({"workbench": "single", "base_mode": "dual_sampler", "single": single})


def test_resource_unlock_only_changes_single():
    custom = {"model_profile": "anima", "clip_name": "custom-clip", "vae_name": "custom-vae", "clip_type": "custom"}
    assert validate_settings(custom)["clip_name"] == "qwen_3_06b_base.safetensors"
    assert validate_settings({**custom, "workbench": "single"})["clip_name"] == "custom-clip"
    assert validate_settings({**custom, "workbench": "single", "single": {"unlock_resources": False}})["clip_name"] == "qwen_3_06b_base.safetensors"


def test_prompt_composition_never_duplicates_managed_words():
    settings = validate_settings({"workbench": "single", "lora_managed_triggers": ["CAT", "soft fur"],
                                  "single": {"prefix": "prefix", "positive_prompt": "cat", "suffix": "suffix", "secondary_managed_triggers": ["dog"]}})
    assert compose_single_prompt(settings) == "prefix, cat, suffix"
    assert effective_single_prompts(settings, "cat") == {"primary": "cat, soft fur", "secondary": "cat, dog"}


def test_normal_uses_frozen_per_pipeline_prompt_and_prunes_llm(templates):
    payload = submit(templates, {"single": {"normal_pipeline": "secondary", "secondary_zero_negative": True,
        "loras_secondary": [{"filename": "example.safetensors", "enabled": True, "strength": 0.7}] }},
        {"positive": "edited", "effective_positive": {"primary": "one", "secondary": "two"}})
    api = payload["prompt"]
    assert_connected(api)
    sampler = api["691:1240"]["inputs"]
    assert sampler["model"] == ["243", 0]
    assert api["single:secondary:positive"]["inputs"]["text"] == "two"
    assert api["single:secondary:zero"]["class_type"] == "ConditioningZeroOut"
    assert api["243"]["inputs"]["text"] == ""
    assert "72" not in api
    assert not any(n["class_type"] in {"PromptExpand", "PreviewAny", "AaaliceImagePicker"} for n in api.values())
    metadata = payload["extra_data"]["extra_pnginfo"]["anima_random_webui"]
    assert metadata["single_runtime"]["effective_positive"]["secondary"] == "two"


def test_img2img_requires_internal_uploaded_image(templates):
    settings = {"generation_mode": "img2img", "single": {"input_image_id": "asset123", "denoise": 0.37}}
    validate_settings({"workbench": "single", "generation_mode": "img2img"})  # draft without upload
    with pytest.raises(WorkflowError):
        submit(templates, settings)
    with pytest.raises(WorkflowError):
        submit(templates, settings, {"input_image": "C:/private.png"})
    api = submit(templates, settings, {"input_image": "single/asset123.png"})["prompt"]
    assert_connected(api)
    assert api["single:input"]["inputs"]["image"] == "single/asset123.png"
    assert api["single:encode"]["class_type"] == "VAEEncodeTiled"
    assert api["691:1240"]["inputs"]["latent_image"] == ["single:encode", 0]
    assert api["691:1240"]["inputs"]["denoise"] == 0.37
    assert "691:53" not in api
    with pytest.raises(WorkflowError):
        submit(templates, {**settings, "base_mode": "dual_sampler"})


def test_dual_has_independent_samplers_loras_and_real_sesqui(templates):
    """The attachment runs a full first pass, then a partial second pass."""
    api = submit(templates, {"base_mode": "dual_sampler", "steps": 24, "cfg": 3,
        "single": {"dual": {"switch_step": 10, "scale": 1.7, "second_cfg": 5,
                             "second_pipeline": "secondary", "second_sampler_name": "heun", "second_scheduler": "beta"}}})["prompt"]
    assert_connected(api)
    first, second = [api[node]["inputs"] for node in ("701:1245", "701:1246")]
    assert first["steps"] == first["end_at_step"] == second["steps"] == second["end_at_step"] == 24
    assert first["cfg"] == 3 and second["cfg"] == 5
    assert second["start_at_step"] == 10 and second["model"] == ["243", 0]
    assert second["sampler_name"] == "heun" and second["scheduler"] == "beta"
    assert api["701:1450"]["class_type"] == "SesquiLatentUpscale"
    assert api["701:1450"]["inputs"]["scale"] == 1.7


@pytest.mark.parametrize("base", ["normal", "dual_sampler"])
@pytest.mark.parametrize("nvidia", [False, True])
def test_all_postprocess_stages_connect_and_dlss_is_real(templates, base, nvidia):
    payload = submit(templates, {"base_mode": base, "single": {"dlss": {"enabled": True}, "vae_decode": {"tiled": False}},
        "upscalers": {"latent": {"enabled": True}, "seedvr2": {"enabled": True},
                      "nvidia": {"enabled": nvidia}, "face_refine": {"enabled": True}}})
    api = payload["prompt"]
    assert_connected(api)
    assert api["704:1064"]["class_type"] == "SesquiLatentUpscale"
    assert api["704:359"]["class_type"] == "VAEDecode"
    assert any(node["class_type"] == "DLSS5NeuralRender" for node in api.values())
    assert api["1253:1215"]["inputs"]["model"] == ["243", 0]


def test_no_runtime_or_template_mutation(templates):
    runtime = {"positive": "frozen", "effective_positive": {"primary": "a", "secondary": "b"}}
    before = copy.deepcopy(runtime)
    template_before = json.dumps(templates.api, sort_keys=True)
    submit(templates, runtime=runtime)
    assert runtime == before
    assert json.dumps(templates.api, sort_keys=True) == template_before


def test_resource_checks_are_specific_to_enabled_single_features():
    settings = validate_settings({"workbench": "single", "base_mode": "dual_sampler", "single": {"dlss": {"enabled": True}}})
    issues = single_resource_issues(settings, {"node_types": [], "samplers": ["euler"], "schedulers": ["simple"]})
    names = {item["name"] for item in issues}
    assert {"DLSS5NeuralRender", "SesquiLatentUpscale", "er_sde"} <= names
    assert "PromptExpand" not in names
    assert single_resource_issues(validate_settings({}), {"node_types": []}) == []


def test_runtime_asset_and_job_ids_are_retained_in_png_metadata(templates):
    payload = submit(templates, runtime={"positive": "saved", "prompt_job_id": "job_123", "input_image_id": "asset_123"})
    runtime = payload["extra_data"]["extra_pnginfo"]["anima_random_webui"]["single_runtime"]
    assert runtime["prompt_job_id"] == "job_123"
    assert runtime["input_image_id"] == "asset_123"


def test_live_face_contract_rejects_stale_rotation_and_wide_saved_ranges():
    settings = validate_settings({"workbench": "single", "upscalers": {"face_refine": {
        "enabled": True, "manual_rotation": "90", "steps": 120, "processing_resolution": 256}}})
    resources = {"node_inputs": {"ForbiddenVisionFaceProcessorIntegrated": {
        "manual_rotation": [["None", "90° CW", "90° CCW", "180°"]],
        "steps": ["INT", {"min": 1, "max": 100}],
        "processing_resolution": ["INT", {"min": 512, "max": 2048}],
    }}}
    names = {issue["name"] for issue in single_resource_issues(settings, resources)}
    assert names == {"manual_rotation=90", "steps=120", "processing_resolution=256"}
    settings["upscalers"]["face_refine"]["enabled"] = False
    assert single_resource_issues(settings, resources) == []
    settings["workbench"] = "random"
    settings["upscalers"]["face_refine"]["enabled"] = True
    assert single_resource_issues(settings, resources) == []


def test_live_nvidia_dynamic_combo_checks_selected_branch_and_quality():
    settings = validate_settings({"workbench": "single", "upscalers": {"nvidia": {
        "enabled": True, "scale": 8, "quality": "obsolete"}}})
    resources = {"node_inputs": {"RTXVideoSuperResolution": {
        "resize_type": ["COMFY_DYNAMICCOMBO_V3", {"options": [
            {"key": "scale by multiplier", "inputs": {"required": {"scale": ["FLOAT", {"min": 1, "max": 4}]}}},
            {"key": "target dimensions", "inputs": {"required": {"width": ["INT", {"min": 64, "max": 16384}]}}},
        ]}],
        "quality": ["COMBO", {"options": ["LOW", "MEDIUM", "HIGH", "ULTRA"]}],
    }}}
    names = {issue["name"] for issue in single_resource_issues(settings, resources)}
    assert names == {"resize_type.scale=8.0", "quality=obsolete"}
    nvidia = settings["upscalers"]["nvidia"]
    nvidia.update(scale=4, quality="HIGH")
    assert single_resource_issues(settings, resources) == []
    nvidia["resize_type"] = "target dimensions"
    assert {item["name"] for item in single_resource_issues(settings, resources)} == {"resize_type.width"}
    nvidia["enabled"] = False
    assert single_resource_issues(settings, resources) == []


def test_live_vae_contract_checks_only_encoders_that_will_run():
    settings = validate_settings({"workbench": "single", "single": {"vae_encode": {"temporal_overlap": 4}}})
    resources = {"node_inputs": {"VAEEncodeTiled": {"temporal_overlap": ["INT", {"min": 8, "max": 4096}]}}}
    # A txt2img base does not encode an image; saved img2img settings may differ.
    assert single_resource_issues(settings, resources) == []
    settings["generation_mode"] = "img2img"
    assert {item["name"] for item in single_resource_issues(settings, resources)} == {"temporal_overlap=4"}
    settings["single"]["vae_encode"]["tiled"] = False
    assert single_resource_issues(settings, resources) == []


def test_seedvr_tiles_use_the_same_live_vae_limits_as_base_tiles():
    settings = validate_settings({"workbench": "single", "upscalers": {"seedvr2": {
        "enabled": True, "encode": {"temporal_overlap": 0}, "decode": {"tile_size": 8192}}}})
    resources = {"node_inputs": {
        "VAEEncodeTiled": {"temporal_overlap": ["INT", {"min": 4, "max": 4096}]},
        "VAEDecodeTiled": {"tile_size": ["INT", {"min": 64, "max": 4096}]},
    }}
    issues = single_resource_issues(settings, resources)
    assert {item["name"] for item in issues} == {"temporal_overlap=0", "tile_size=8192"}
    assert all("SeedVR2" in item["label"] for item in issues)
    settings["upscalers"]["seedvr2"]["enabled"] = False
    assert single_resource_issues(settings, resources) == []


def test_live_single_optional_enums_can_change_without_invalidating_disabled_drafts():
    settings = validate_settings({"workbench": "single", "single": {"dlss": {"enabled": True}}})
    resources = {"node_inputs": {"DLSS5NeuralRender": {"style": [["自然"]]}}}
    assert {item["name"] for item in single_resource_issues(settings, resources)} == {"style=电影"}
    settings["single"]["dlss"]["enabled"] = False
    assert single_resource_issues(settings, resources) == []


def test_live_face_blur_strength_accepts_node_supported_values_above_one():
    settings = validate_settings({"workbench": "single", "upscalers": {"face_refine": {
        "enabled": True, "sampling_mask_blur_strength": 6}}})
    resources = {"node_inputs": {"ForbiddenVisionFaceProcessorIntegrated": {
        "sampling_mask_blur_strength": ["FLOAT", {"min": 0.1, "max": 6}],
    }}}
    assert single_resource_issues(settings, resources) == []
    settings["upscalers"]["face_refine"]["sampling_mask_blur_strength"] = 0
    assert single_resource_issues(settings, resources)
