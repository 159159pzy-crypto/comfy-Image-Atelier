import copy
import itertools
from pathlib import Path

import pytest

from anima_webui.pipeline_v3 import MAX_EXECUTION_NONCE, STAGE_ORDER, required_node_classes
from anima_webui.single_workbench import active_lora_pipelines, single_resource_issues
from anima_webui.workflow import DEFAULT_SETTINGS, MAX_SAMPLE_SEED, WorkflowError, WorkflowTemplates, upgrade_pipeline_settings, validate_settings


ROOT = Path(__file__).resolve().parents[1]
STAGES = ("pixel_hires", "nvidia", "seedvr2", "latent", "post_sampler", "face_refine", "dlss")


@pytest.fixture(scope="module")
def templates():
    return WorkflowTemplates.load(ROOT / "templates")


def submit(templates, *, enabled=(), **overrides):
    settings = {"pipeline_version": 3, "workbench": "single", "base_mode": "normal",
                "upscalers": {stage: {"enabled": stage in enabled} for stage in STAGES}, **overrides}
    return templates.submission(settings, 12345, 67890, "Pipeline/test", "client", 1)


def references(node):
    return [(key, value) for key, value in node["inputs"].items()
            if isinstance(value, list) and len(value) == 2 and isinstance(value[0], str) and isinstance(value[1], int)]


@pytest.mark.parametrize("workbench,base_mode,generation_mode", [
    ("random", "normal", "txt2img"), ("random", "dual_sampler", "txt2img"),
    ("single", "normal", "txt2img"), ("single", "dual_sampler", "txt2img"),
    ("single", "normal", "img2img"),
])
def test_execution_nonce_invalidates_generation_ancestry_without_changing_seed_or_resources(
        templates, workbench, base_mode, generation_mode):
    settings = {"pipeline_version": 3, "workbench": workbench, "base_mode": base_mode,
                "generation_mode": generation_mode, "upscalers": {stage: {"enabled": True} for stage in STAGES}}
    runtime = {"input_image": "reference.png"} if generation_mode == "img2img" else None
    args = (settings, MAX_SAMPLE_SEED, 54321, "same-output", "same-client", 1)
    first = templates.submission(*args, execution_nonce=9000000000000000, single_runtime=runtime)
    second = templates.submission(*args, execution_nonce=9000000000000001, single_runtime=runtime)
    graph, other = first["prompt"], second["prompt"]
    assert graph.keys() == other.keys()
    assert [key for key in graph if graph[key] != other[key]] == ["v3:run:nonce"]
    assert graph["v3:run:seed"]["inputs"]["value"] == MAX_SAMPLE_SEED
    assert graph["v3:run:gate"]["inputs"] == {
        "expression": "a", "values.a": ["v3:run:seed", 0], "values.b": ["v3:run:nonce", 0]}
    first_sampler = "691:1240" if base_mode == "normal" else "701:1245"
    field = "seed" if base_mode == "normal" else "noise_seed"
    assert graph[first_sampler]["inputs"][field] == ["v3:run:gate", 1]
    if base_mode == "dual_sampler":
        assert graph["701:1246"]["inputs"]["noise_seed"] == MAX_SAMPLE_SEED
    assert graph["v3:post"]["inputs"]["seed"] == MAX_SAMPLE_SEED
    assert graph["v3:seed:sample"]["inputs"]["seed"] == MAX_SAMPLE_SEED
    assert graph["v3:face"]["inputs"]["seed"] == MAX_SAMPLE_SEED

    def ancestors(key):
        return {key}.union(*(ancestors(source) for _, (source, _) in references(graph[key])))

    for key in (first_sampler, "v3:pixel:upscale", "v3:vsr", "v3:seed:sample", "v3:latent",
                "v3:post", "v3:face", "v3:dlss", "v2:output"):
        assert "v3:run:nonce" in ancestors(key), key
    for key, node in graph.items():
        if node["class_type"] in {"UNETLoader", "CLIPLoader", "VAELoader", "Lora Loader (LoraManager)",
                                  "CLIPTextEncode", "UpscaleModelLoader", "EmptyLatentImage", "LoadImage"}:
            assert "v3:run:nonce" not in ancestors(key), key
    metadata = first["extra_data"]["extra_pnginfo"]["anima_random_webui"]
    assert metadata["sample_seed"] == MAX_SAMPLE_SEED
    assert metadata["prompt_seed"] == 54321
    assert metadata["execution_context"] == {"execution_nonce": 9000000000000000}
    assert "execution_nonce" not in metadata["settings"]
    assert "execution_context" not in metadata["settings"]


@pytest.mark.parametrize("nonce", [-1, MAX_EXECUTION_NONCE + 1, True, 1.5, "123"])
def test_v3_execution_nonce_rejects_values_that_cannot_roundtrip_in_browser(templates, nonce):
    with pytest.raises(WorkflowError, match="execution_nonce"):
        templates.submission({"pipeline_version": 3}, 0, 0, "test", "client", 1, execution_nonce=nonce)


@pytest.mark.parametrize("seed", [0, MAX_SAMPLE_SEED])
def test_execution_gate_export_has_declared_dynamic_inputs_and_preserves_seed_boundaries(templates, seed):
    result = templates.submission({"pipeline_version": 3}, seed, 0, "test", "client", 1,
                                  execution_nonce=MAX_EXECUTION_NONCE)
    ui = result["extra_data"]["extra_pnginfo"]["workflow"]
    nodes = {node["properties"]["pipeline_api_id"]: node for node in ui["nodes"]}
    assert nodes["v3:run:seed"]["widgets_values"] == [seed, "fixed"]
    assert nodes["v3:run:nonce"]["widgets_values"] == [MAX_EXECUTION_NONCE, "fixed"]
    gate = nodes["v3:run:gate"]
    assert gate["widgets_values"] == ["a"]
    assert [(entry["name"], entry["type"]) for entry in gate["inputs"]] == [("values.a", "INT"), ("values.b", "INT")]
    assert all(entry["link"] is not None for entry in gate["inputs"])
    assert [entry["type"] for entry in gate["outputs"]] == ["FLOAT", "INT", "BOOLEAN"]
    assert ui["extra"]["pipeline"]["execution_nonce"] == MAX_EXECUTION_NONCE
    assert ui["extra"]["pipeline"]["api_prompt"] == result["prompt"]


@pytest.mark.parametrize("workbench", ["single", "random"])
def test_legacy_submissions_ignore_execution_nonce_and_keep_original_seed_graph(templates, workbench):
    settings = {"pipeline_version": 2, "workbench": workbench}
    args = (settings, 12345, 67890, "test", "client", 1)
    original = templates.submission(*args)
    assert templates.submission(*args, execution_nonce=123456) == original
    assert not any(key.startswith("v3:run:") for key in original["prompt"])
    assert "execution_context" not in original["extra_data"]["extra_pnginfo"]["anima_random_webui"]


def test_preview_render_has_stable_zero_execution_placeholder(templates):
    result = templates.submission({"pipeline_version": 3}, 0, 0, "preview", "client", 1)
    assert result["prompt"]["v3:run:nonce"]["inputs"]["value"] == 0


def assert_graph(api):
    seen, active = set(), set()

    def visit(key):
        assert key in api
        assert key not in active, f"cycle at {key}"
        if key in seen:
            return
        active.add(key)
        for _, (source, _) in references(api[key]):
            visit(source)
            src_stage = api[source]["_meta"]["pipeline_stage"]
            dst_stage = api[key]["_meta"]["pipeline_stage"]
            assert STAGE_ORDER.index(src_stage) <= STAGE_ORDER.index(dst_stage), (source, key)
        active.remove(key)
        seen.add(key)

    visit("v2:output")
    assert seen == set(api), "inactive or unrelated outputs survived pruning"
    assert sum(node["class_type"] == "SaveImage" for node in api.values()) == 1
    assert not any(node["class_type"] in {"PreviewAny", "MaskPreview", "PromptExpand", "VRAM_Debug"}
                   for node in api.values())


@pytest.mark.parametrize("enabled", [tuple(stage for stage, flag in zip(STAGES, flags) if flag)
                                     for flags in itertools.product((False, True), repeat=len(STAGES))])
def test_every_stage_combination_is_connected_ordered_and_pruned(templates, enabled):
    api = submit(templates, enabled=enabled)["prompt"]
    assert_graph(api)
    actual = {node["_meta"]["pipeline_stage"] for node in api.values()} - {"base", "export"}
    assert actual == set(enabled)


@pytest.mark.parametrize("workbench,profile,base", list(itertools.product(("single", "random"), ("anima", "krea2"), ("normal", "dual_sampler"))))
def test_both_profiles_and_workbenches_use_same_chain(templates, workbench, profile, base):
    result = submit(templates, enabled=STAGES, workbench=workbench, model_profile=profile, base_mode=base)
    assert_graph(result["prompt"])
    graph = result["prompt"]
    base_id = "691:1240" if base == "normal" else "701:1246"
    for name in ("model", "positive", "negative"):
        assert graph["v3:post"]["inputs"][name] == graph[base_id]["inputs"][name]
    assert graph["v2:output"]["inputs"]["images"] == ["v3:dlss", 0]
    assert graph["v3:dlss"]["inputs"]["images"] == ["v3:face", 0]
    assert graph["v3:post"]["inputs"]["latent_image"] == ["v3:latent", 0]
    assert graph["v3:seed:resize"]["inputs"]["image"] == ["v3:vsr", 0]
    assert graph["v3:vsr"]["inputs"]["images"] == ["v3:pixel:upscale", 0]
    assert graph["v3:seed:finish"]["inputs"]["original_resized_images"] == ["v3:seed:resize", 0]


@pytest.mark.parametrize("enabled,encodes,decodes", [
    ((), 0, 1), (("pixel_hires",), 0, 1), (("post_sampler",), 0, 1),
    (("latent", "post_sampler"), 0, 1), (("pixel_hires", "post_sampler"), 1, 2),
    (("seedvr2", "latent", "post_sampler"), 2, 3),
])
def test_vae_boundaries_are_only_added_when_representation_changes(templates, enabled, encodes, decodes):
    graph = submit(templates, enabled=enabled)["prompt"]
    assert sum(node["class_type"].startswith("VAEEncode") for node in graph.values()) == encodes
    assert sum(node["class_type"].startswith("VAEDecode") for node in graph.values()) == decodes


def test_pixel_native_output_is_only_shrunk_without_post_sampling(templates):
    plain = submit(templates, enabled=("pixel_hires",))["prompt"]
    assert plain["v3:pixel:resize"]["inputs"] == {
        "image": ["v3:pixel:upscale", 0], "scale_by": 0.5, "upscale_method": "lanczos"}
    refined = submit(templates, enabled=("pixel_hires", "post_sampler"))["prompt"]
    assert "v3:pixel:resize" not in refined
    encoded = [node for node in refined.values() if node["class_type"].startswith("VAEEncode")]
    assert encoded[0]["inputs"]["pixels"] == ["v3:pixel:upscale", 0]


def test_post_sampling_inherits_final_dual_stack_and_zero_negative(templates):
    result = submit(templates, enabled=("post_sampler",), base_mode="dual_sampler",
                    single={"dual": {"second_pipeline": "secondary"}, "secondary_zero_negative": True,
                            "loras_secondary": [{"filename": "detail.safetensors", "strength": 0.5, "enabled": True}]})
    api = result["prompt"]
    assert api["v3:post"]["inputs"]["model"] == ["243", 0]
    assert api["v3:post"]["inputs"]["negative"] == ["single:secondary:zero", 0]
    assert api["v3:post"]["inputs"]["seed"] == api["701:1246"]["inputs"]["noise_seed"]


def test_dormant_secondary_stack_and_main_encoder_are_not_required(templates):
    settings = validate_settings({"pipeline_version": 3, "workbench": "single", "base_mode": "normal",
                                  "upscalers": {"latent": {"enabled": True}, "post_sampler": {"enabled": True}},
                                  "single": {"latent": {"pipeline": "secondary"},
                                             "loras_secondary": [{"filename": "missing.safetensors", "strength": 1, "enabled": True}]}})
    assert active_lora_pipelines(settings) == {"primary"}
    assert "VAEEncodeTiled" not in required_node_classes(settings)
    api = templates.submission(settings, 1, 2, "test", "client", 1)["prompt"]
    assert "243" not in api
    resources = {"node_types": list({node["class_type"] for node in api.values()})}
    assert single_resource_issues(settings, resources) == []


def test_versionless_history_and_explicit_upgrade_keep_effective_switches(templates):
    original = {"upscalers": {"latent": {"enabled": False, "second_sampler": {"enabled": True}},
                              "nvidia": {"enabled": False}}, "single": {"dlss": {"enabled": True}}}
    before = copy.deepcopy(original)
    assert DEFAULT_SETTINGS["pipeline_version"] == 5
    assert validate_settings(original)["pipeline_version"] == 2
    upgraded = upgrade_pipeline_settings(original)
    assert upgraded["pipeline_version"] == 4
    assert not upgraded["upscalers"]["post_sampler"]["enabled"]
    assert not upgraded["upscalers"]["dlss"]["enabled"]
    assert original == before
    original["upscalers"]["latent"]["enabled"] = True
    original["upscalers"]["nvidia"]["enabled"] = True
    enabled = upgrade_pipeline_settings(original)
    assert enabled["upscalers"]["post_sampler"]["enabled"]
    assert enabled["upscalers"]["dlss"]["enabled"]
    old = templates.submission(original, 1, 2, "test", "client", 1)["prompt"]
    assert "704:1248" in old and "1432:1492" in old and "v3:post" not in old


def test_profile_defaults_and_v1_pixel_upgrade():
    anima = validate_settings({"pipeline_version": 3, "model_profile": "anima"})["upscalers"]
    krea = validate_settings({"pipeline_version": 3, "model_profile": "krea2"})["upscalers"]
    assert (anima["post_sampler"]["steps"], anima["post_sampler"]["cfg"], anima["post_sampler"]["denoise"]) == (16, 4, .2)
    assert (krea["post_sampler"]["steps"], krea["post_sampler"]["cfg"], krea["post_sampler"]["denoise"]) == (10, 1, .3)
    assert anima["pixel_hires"]["model_name"] != krea["pixel_hires"]["model_name"]
    upgraded = upgrade_pipeline_settings({"hires": {"enabled": True, "model_name": "custom.pth", "percent": 50}})
    assert upgraded["upscalers"]["pixel_hires"]["model_name"] == "custom.pth"
    assert upgraded["upscalers"]["pixel_hires"]["enabled"]
    assert not upgraded["upscalers"]["latent"]["enabled"]


@pytest.mark.parametrize("patch", [
    {"seedvr2": {"encode": {"tile_size": 64, "overlap": 64}}},
    {"seedvr2": {"encode": {"tile_size": 64, "overlap": 32}}},
    {"seedvr2": {"decode": {"temporal_size": 8, "temporal_overlap": 8}}},
    {"pixel_hires": {"percent": 0}}, {"pixel_hires": {"upscale_method": "bad"}},
    {"post_sampler": {"denoise": float("nan")}},
])
def test_reject_invalid_repair_contracts(patch):
    with pytest.raises(WorkflowError):
        validate_settings({"pipeline_version": 3, "upscalers": patch})


def test_editable_ui_contains_exact_executable_graph_and_sampling_values(templates):
    result = submit(templates, enabled=STAGES, base_mode="dual_sampler")
    api = result["prompt"]
    ui = result["extra_data"]["extra_pnginfo"]["workflow"]
    ids = ui["extra"]["pipeline_api_ids"]
    by_id = {node["id"]: node for node in ui["nodes"]}
    assert len(by_id) == len(api)
    assert ui["extra"]["pipeline"]["api_prompt"] == api
    for link_id, source, slot, target, target_slot, _ in ui["links"]:
        target_node = by_id[target]
        name = target_node["inputs"][target_slot]["name"]
        assert api[ids[str(target)]]["inputs"][name] == [ids[str(source)], slot]
        assert target_node["inputs"][target_slot]["link"] == link_id
    nodes = {node["properties"]["pipeline_api_id"]: node for node in ui["nodes"]}
    assert nodes["v3:post"]["widgets_values"] == [12345, "fixed", 16, 4.0, "er_sde", "simple", .2]
    assert nodes["v3:face"]["widgets_values"][5:8] == [12345, "fixed", 0]
    assert {node["type"] for node in ui["nodes"]} == {node["class_type"] for node in api.values()}
