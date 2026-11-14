"""Versioned execution order, latent resampling and historical graph contracts."""
import hashlib
import itertools
import json
from pathlib import Path

import pytest

from anima_webui.pipeline_v3 import STAGE_ORDER, STAGE_ORDER_V4, needs_main_encode, required_node_classes
from anima_webui.single_workbench import single_resource_issues
from anima_webui.workflow import WorkflowTemplates, validate_settings
from test_pipeline_memory_cleanup import without_cleanup


ROOT = Path(__file__).resolve().parents[1]
STAGES = ("pixel_hires", "latent", "post_sampler", "face_refine", "seedvr2", "nvidia", "dlss")


@pytest.fixture(scope="module")
def templates():
    return WorkflowTemplates.load(ROOT / "templates")


def submit(templates, *, enabled=(), **overrides):
    settings = {"pipeline_version": 4, "workbench": "single", "base_mode": "normal",
                "upscalers": {stage: {"enabled": stage in enabled} for stage in STAGES}, **overrides}
    return templates.submission(settings, 12345, 67890, "Pipeline/test", "client", 1, execution_nonce=654321)


def refs(node):
    return [(name, value) for name, value in node["inputs"].items()
            if isinstance(value, list) and len(value) == 2 and isinstance(value[0], str) and type(value[1]) is int]


@pytest.mark.parametrize("enabled", [tuple(stage for stage, flag in zip(STAGES, flags) if flag)
                                     for flags in itertools.product((False, True), repeat=len(STAGES))])
def test_v4_every_combination_follows_reference_order_and_has_one_connected_sink(templates, enabled):
    result = submit(templates, enabled=enabled)
    graph = result["prompt"]
    visited, active = set(), set()

    def visit(key):
        assert key in graph and key not in active
        if key in visited:
            return
        active.add(key)
        for _, (source, _) in refs(graph[key]):
            visit(source)
            assert STAGE_ORDER_V4.index(graph[source]["_meta"]["pipeline_stage"]) <= STAGE_ORDER_V4.index(graph[key]["_meta"]["pipeline_stage"])
        active.remove(key)
        visited.add(key)

    visit("v2:output")
    assert visited == graph.keys()
    assert sum(node["class_type"] == "SaveImage" for node in graph.values()) == 1
    assert ("v4:latent:sample" in graph) == ("latent" in enabled)
    if "latent" in enabled:
        assert graph["v4:latent:sample"]["inputs"]["latent_image"] == ["v3:latent", 0]
        if "post_sampler" in enabled:
            assert graph["v3:post"]["inputs"]["latent_image"] == ["v3:cleanup:latent", 0]
            assert graph["v3:cleanup:latent"]["inputs"]["any_input"] == ["v4:latent:sample", 0]
    metadata = result["extra_data"]["extra_pnginfo"]["workflow"]["extra"]["pipeline"]
    assert metadata["version"] == 4
    assert metadata["stages"] == ["base", *enabled, "export"]
    assert metadata["execution_nonce"] == 654321
    assert metadata["api_prompt"] == graph


@pytest.mark.parametrize("workbench,profile,base", list(itertools.product(("single", "random"), ("anima", "krea2"), ("normal", "dual_sampler"))))
def test_v4_both_latent_and_post_sampling_inherit_final_base_resources(templates, workbench, profile, base):
    result = submit(templates, enabled=STAGES, workbench=workbench, model_profile=profile, base_mode=base,
                    single={"dual": {"second_pipeline": "secondary"}, "secondary_zero_negative": True,
                            "loras_secondary": [{"filename": "detail.safetensors", "strength": .5, "enabled": True}]})
    graph = result["prompt"]
    final = graph["701:1246" if base == "dual_sampler" else "691:1240"]["inputs"]
    for node in ("v4:latent:sample", "v3:post"):
        assert graph[node]["inputs"]["seed"] == 12345
        for key in ("model", "positive", "negative"):
            assert graph[node]["inputs"][key] == final[key]
    assert graph["v3:seed:resize"]["inputs"]["image"] == ["v3:cleanup:face_refine", 1]
    assert graph["v3:vsr"]["inputs"]["images"] == ["v3:cleanup:seedvr2", 1]
    assert graph["v3:dlss"]["inputs"]["images"] == ["v3:cleanup:nvidia", 1]
    ui = result["extra_data"]["extra_pnginfo"]["workflow"]
    nodes = {node["properties"]["pipeline_api_id"]: node for node in ui["nodes"]}
    expected = [12345, "fixed", 16, 4, "er_sde", "simple", .2] if profile == "anima" else [12345, "fixed", 10, 1, "er_sde", "simple", .3]
    assert nodes["v4:latent:sample"]["widgets_values"] == expected
    assert nodes["v3:face"]["pos"][0] < nodes["v3:seed:resize"]["pos"][0] < nodes["v3:vsr"]["pos"][0]


@pytest.mark.parametrize("enabled,encodes,decodes", [
    ((), 0, 1), (("latent",), 0, 1), (("latent", "post_sampler"), 0, 1),
    (("latent", "seedvr2", "nvidia"), 0, 1), (("post_sampler", "seedvr2"), 0, 1),
    (("pixel_hires", "latent"), 1, 2), (("pixel_hires", "latent", "post_sampler"), 1, 2),
    (STAGES, 1, 2),
])
def test_v4_main_vae_only_crosses_before_pixel_repair_and_after_latent_sampling(templates, enabled, encodes, decodes):
    graph = submit(templates, enabled=enabled)["prompt"]
    main = [node for key, node in graph.items() if not key.startswith("v3:seed:")]
    assert sum(node["class_type"].startswith("VAEEncode") for node in main) == encodes
    assert sum(node["class_type"].startswith("VAEDecode") for node in main) == decodes
    settings = validate_settings({"pipeline_version": 4, "workbench": "single", "base_mode": "normal",
                                  "upscalers": {stage: {"enabled": stage in enabled} for stage in STAGES}})
    assert needs_main_encode(settings) == bool(encodes)


def test_v4_latent_sampler_parameters_are_independent_from_post_sampler(templates):
    latent_sampler = {"steps": 13, "cfg": 2.5, "sampler_name": "dpmpp_2m", "scheduler": "karras", "denoise": .4}
    post_sampler = {"enabled": True, "steps": 7, "cfg": 3, "sampler_name": "euler", "scheduler": "simple", "denoise": .15}
    graph = submit(templates, upscalers={"latent": {"enabled": True, "sampler": latent_sampler},
                                        "post_sampler": post_sampler})["prompt"]
    for key, value in latent_sampler.items():
        assert graph["v4:latent:sample"]["inputs"][key] == value
    for key, value in post_sampler.items():
        if key != "enabled":
            assert graph["v3:post"]["inputs"][key] == value


@pytest.mark.parametrize("tiled", [False, True])
def test_v4_img2img_preparation_then_latent_and_post_stay_in_one_latent_chain(templates, tiled):
    settings = {"pipeline_version": 4, "workbench": "single", "base_mode": "normal", "generation_mode": "img2img",
                "upscalers": {"latent": {"enabled": True}, "post_sampler": {"enabled": True}},
                "single": {"vae_encode": {"tiled": tiled}, "vae_decode": {"tiled": tiled}}}
    graph = templates.submission(settings, 0, 0, "test", "client", 1, execution_nonce=999,
                                 single_runtime={"input_image": "reference.png"})["prompt"]
    assert graph["single:encode"]["class_type"] == ("VAEEncodeTiled" if tiled else "VAEEncode")
    assert sum(node["class_type"].startswith("VAEEncode") for node in graph.values()) == 1
    assert sum(node["class_type"].startswith("VAEDecode") for node in graph.values()) == 1
    assert graph["691:1240"]["inputs"]["latent_image"] == ["single:encode", 0]
    assert graph["v3:latent"]["inputs"]["latent"] == ["v3:cleanup:base", 0]
    assert graph["v3:cleanup:base"]["inputs"]["any_input"] == ["691:1240", 0]
    assert graph["v3:post"]["inputs"]["latent_image"] == ["v3:cleanup:latent", 0]
    assert graph["v3:cleanup:latent"]["inputs"]["any_input"] == ["v4:latent:sample", 0]
    assert graph["v4:latent:sample"]["inputs"]["seed"] == graph["v3:post"]["inputs"]["seed"] == 0


def test_v4_latent_sampler_is_required_and_validated_even_without_post_sampling():
    settings = validate_settings({"pipeline_version": 4, "workbench": "single", "base_mode": "dual_sampler",
                                  "cfg": 1, "upscalers": {"latent": {"enabled": True}, "post_sampler": {"enabled": False}},
                                  "single": {"dual": {"second_cfg": 1}}})
    assert "KSampler" in required_node_classes(settings)
    issues = single_resource_issues(settings, {"node_inputs": {"KSampler": {"cfg": ["FLOAT", {"min": 0, "max": 2}]}}})
    assert any("潜空间" in issue["label"] and issue["name"].startswith("cfg=") for issue in issues)


# v2 API and UI remain frozen. For v3, hashes captured before automatic cleanup
# cover the API after removing only cleanup passthroughs; its UI now includes
# the intentionally added nodes, whose socket contract is tested separately.
FROZEN_GRAPHS = [
    "d2f5040b0c3fc90447309e0ebc4051d5a933f75b9395f2f79b9a9f06b65cd33c",
    "6c429e67227a8a0bc608c847b48921b1f6ea93db01f5c5f1217ca2e16fade402",
    "2b7249e16ce28ca236f923b7c7041bfc57f42584103c7dfc6bcf7e909f3aec0d",
    "32af3937ac7329a18013fae1d945b79e088cca326af8ea15a758e8eb12a067e6",
    "53345904d32a2b9db9b01de47d504f89a172dd9e199e3c8264c3bcc77ce804b4",
    "c979a8342f21c03f2babddfb6f920111a7a225d5bbb8ccd6753d3b227e71cd31",
    "07aea7aba8c9ae8123b66522b28ffa5e2aeedac037db66b81da0ff31808c45bf",
    "2528326389be3f14250ef3c3489b8de3863d9d5955abdcabd14041846a24d367",
    "15cf761149b435ecd33306dd5626019e0a187ee247682609b9c6b916059756c9",
    "4464cf87cefb4d7219920bb9b86795fec7ad1c52bd0d576adea8d5aa7916f18a",
    "00f63df438f3bb57b288b20f65ad5061c50017080040b58b4b479fa109687720",
    "07d5804bf021422e3563eb2e322761f54d25999afc470c8646d6f4a529f23b33",
    "c12bd83c9a837f7b280dc0de48825833c446c154c073407a995e6fb00f0a5e2a",
    "345ea69c8eeeae574e9c0b9a1badb28bfbe4b6c674bb665ab42ee6c2d9d60e19",
    "51c61cf0be6b9000183c9bf9320cf50360c1e6209ab3956740245379064d3e20",
    "4a2ae5b51b142f2724bc1ce590fb11079f675a01dd6fd90233ac3a212c43ed04",
]


@pytest.mark.parametrize("case,expected", list(zip(itertools.product((2, 3), ("anima", "krea2"), ("random", "single"), ("normal", "dual_sampler")), FROZEN_GRAPHS)))
def test_v2_graph_stays_frozen_and_v3_only_adds_cleanup_across_profiles_and_base_modes(templates, case, expected):
    version, profile, workbench, base_mode = case
    settings = {"pipeline_version": version, "model_profile": profile, "workbench": workbench, "base_mode": base_mode,
                "upscalers": {stage: {"enabled": True} for stage in STAGES}}
    result = templates.submission(settings, 12345, 67890, "Frozen/output", "client", 1, execution_nonce=123456)
    content = ([result["prompt"], result["extra_data"]["extra_pnginfo"]["workflow"]]
               if version == 2 else without_cleanup(result["prompt"]))
    actual = hashlib.sha256(json.dumps(content, sort_keys=True, ensure_ascii=True, separators=(",", ":")).encode()).hexdigest()
    assert actual == expected
    if version == 3:
        assert result["extra_data"]["extra_pnginfo"]["workflow"]["extra"]["pipeline"]["stages"] == list(STAGE_ORDER)
        assert "v4:latent:sample" not in result["prompt"]
