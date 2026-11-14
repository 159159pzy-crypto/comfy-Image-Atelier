"""Stage cleanup must preserve the existing sampling graph and codec boundaries."""
import copy
import hashlib
import itertools
import json
from pathlib import Path

import pytest

from anima_webui.pipeline_v3 import export_ui_workflow, stage_order
from anima_webui.workflow import WorkflowTemplates


ROOT = Path(__file__).resolve().parents[1]
STAGES = ("pixel_hires", "latent", "post_sampler", "face_refine", "seedvr2", "nvidia", "dlss")
# Captured from HEAD before cleanup was added. Each digest covers the complete
# API graphs for all 128 stage combinations, including codecs and numeric inputs.
BASELINE_COMBINATIONS = {
    3: "219bc333e629125cea5ec18c273abdeaf3f537170912282ed6c8c6b2a1b6681b",
    4: "f421ff11d29acadb3208b668cdbac7c6530c4b48f8562fd4b87a2bb1e04d2a54",
    5: "52edfcaae171be6cfcbf003cd1ebb5b22b09cc7acaa77eae58ba39cd36555538",
}
BASELINE_VARIANTS = {
    3: "bbf0663b5bd9ed2d4bc9aeafb1e75a962367b43a190f817deb9b8efa6f835ebf",
    4: "a97ef5ab2dbf69e44b84bd29683e8ecdc32964331821916510c5b294cd936d7c",
    5: "4ff1ba5de18fda68565c5af7a342e39dba5f50774fff1062f80ec33d7e01bf29",
}


@pytest.fixture(scope="module")
def templates():
    return WorkflowTemplates.load(ROOT / "templates")


def digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, ensure_ascii=True,
                                     separators=(",", ":")).encode()).hexdigest()


def references(node):
    return [value for value in node["inputs"].values()
            if isinstance(value, list) and len(value) == 2
            and isinstance(value[0], str) and type(value[1]) is int]


def without_cleanup(graph):
    """Remove only the new transparent nodes, retaining every other graph value."""
    result = copy.deepcopy(graph)
    cleanup = {key: node for key, node in graph.items() if key.startswith("v3:cleanup:")}

    def original(value):
        if not (isinstance(value, list) and len(value) == 2
                and isinstance(value[0], str) and value[0] in cleanup):
            return value
        node = cleanup[value[0]]
        assert node["class_type"] == "VRAM_Debug"
        assert value[1] in (0, 1)
        return original(node["inputs"]["any_input" if value[1] == 0 else "image_pass"])

    for key in cleanup:
        result.pop(key)
    for node in result.values():
        node["inputs"] = {name: original(value) for name, value in node["inputs"].items()}
    return result


def assert_stage_cleanup(graph, version, enabled):
    stages = [stage for stage in stage_order(version) if stage == "base" or stage in enabled]
    expected = {f"v3:cleanup:{stage}" for stage in stages}
    assert {key for key, node in graph.items() if node["class_type"] == "VRAM_Debug"} == expected
    assert len(expected) == 1 + len(enabled)

    def ancestors(key):
        seen, pending = set(), [key]
        while pending:
            current = pending.pop()
            if current not in seen:
                seen.add(current)
                pending.extend(source for source, _ in references(graph[current]))
        return seen

    assert expected <= ancestors("v2:output")
    for index, stage in enumerate(stages):
        key = f"v3:cleanup:{stage}"
        node = graph[key]
        following = stages[index + 1] if index + 1 < len(stages) else "export"
        is_latent = stage in {"base", "latent", "post_sampler"} and following in {"latent", "post_sampler"}
        trigger = "any_input" if is_latent else "image_pass"
        assert set(node["inputs"]) == {"empty_cache", "gc_collect", "unload_all_models", trigger}
        assert [node["inputs"][name] for name in ("empty_cache", "gc_collect", "unload_all_models")] == [True, True, False]
        assert node["_meta"]["pipeline_stage"] == stage
        assert "v3:run:nonce" in ancestors(key)
        if index:
            assert f"v3:cleanup:{stages[index - 1]}" in ancestors(key)
        if stage in {"base", "latent", "post_sampler"} and not is_latent:
            # A required decode belongs before this stage's cleanup, including
            # base-only generation and the last latent stage before SaveImage.
            source, slot = node["inputs"][trigger]
            assert graph[source]["class_type"] in {"VAEDecode", "VAEDecodeTiled"}
            assert slot == 0
        consumers = [value for candidate in graph.values() for value in references(candidate) if value[0] == key]
        assert consumers and all(value[1] == (0 if is_latent else 1) for value in consumers)
    assert graph["v2:output"]["inputs"]["images"] == [f"v3:cleanup:{stages[-1]}", 1]


@pytest.mark.parametrize("version", [3, 4, 5])
def test_all_stage_combinations_add_only_cleanup_and_preserve_prechange_api(templates, version):
    originals = []
    for flags in itertools.product((False, True), repeat=len(STAGES)):
        enabled = [stage for stage, flag in zip(STAGES, flags) if flag]
        settings = {"pipeline_version": version, "workbench": "single", "base_mode": "normal",
                    "upscalers": {stage: {"enabled": flag} for stage, flag in zip(STAGES, flags)}}
        graph = templates.submission(settings, 12345, 67890, "Pipeline/test", "client", 1,
                                     execution_nonce=654321)["prompt"]
        assert_stage_cleanup(graph, version, enabled)
        originals.append(without_cleanup(graph))
    assert digest(originals) == BASELINE_COMBINATIONS[version]


@pytest.mark.parametrize("version", [3, 4, 5])
def test_profiles_workbenches_and_both_base_modes_keep_prechange_sampling(templates, version):
    originals = []
    for profile, workbench, base_mode in itertools.product(("anima", "krea2"), ("random", "single"),
                                                          ("normal", "dual_sampler")):
        settings = {"pipeline_version": version, "model_profile": profile, "workbench": workbench,
                    "base_mode": base_mode, "upscalers": {stage: {"enabled": True} for stage in STAGES}}
        graph = templates.submission(settings, 12345, 67890, "Pipeline/test", "client", 1,
                                     execution_nonce=654321)["prompt"]
        assert_stage_cleanup(graph, version, STAGES)
        originals.append(without_cleanup(graph))
    assert digest(originals) == BASELINE_VARIANTS[version]


@pytest.mark.parametrize("version,tiled", list(itertools.product((3, 4, 5), (False, True))))
def test_img2img_keeps_one_encode_and_decode_with_latent_cleanup(templates, version, tiled):
    settings = {"pipeline_version": version, "workbench": "single", "base_mode": "normal",
                "generation_mode": "img2img", "single": {"vae_encode": {"tiled": tiled}, "vae_decode": {"tiled": tiled}},
                "upscalers": {"latent": {"enabled": True}, "post_sampler": {"enabled": True}}}
    graph = templates.submission(settings, 12345, 67890, "Pipeline/test", "client", 1,
                                 execution_nonce=654321, single_runtime={"input_image": "reference.png"})["prompt"]
    assert_stage_cleanup(graph, version, ("latent", "post_sampler"))
    assert sum(node["class_type"].startswith("VAEEncode") for node in graph.values()) == 1
    assert sum(node["class_type"].startswith("VAEDecode") for node in graph.values()) == 1


def assert_cleanup_ui(api, ui):
    nodes = {node["properties"]["pipeline_api_id"]: node for node in ui["nodes"]}
    by_id = {node["id"]: node for node in ui["nodes"]}
    for key, source in api.items():
        if source["class_type"] != "VRAM_Debug":
            continue
        node = nodes[key]
        assert node["widgets_values"] == [True, True, False]
        assert [item["name"] for item in node["inputs"]] == ["any_input", "image_pass", "model_pass"]
        assert [item["name"] for item in node["outputs"]] == ["any_output", "image_pass", "model_pass", "freemem_before", "freemem_after"]
        assert [item["type"] for item in node["outputs"]] == ["*", "IMAGE", "MODEL", "INT", "INT"]
        input_slot = 0 if "any_input" in source["inputs"] else 1
        assert [item["link"] is not None for item in node["inputs"]] == [index == input_slot for index in range(3)]
        expected_type = "LATENT" if input_slot == 0 else "IMAGE"
        for link in ui["links"]:
            _, origin, origin_slot, target, target_slot, wire_type = link
            if origin == node["id"]:
                assert origin_slot == input_slot
                assert wire_type == expected_type
                assert by_id[target]["inputs"][target_slot]["type"] == expected_type
            if target == node["id"]:
                assert target_slot == input_slot
                assert wire_type == expected_type


@pytest.mark.parametrize("version", [3, 4, 5])
def test_export_retains_real_cleanup_sockets_widgets_and_typed_wires(templates, version):
    settings = {"pipeline_version": version, "upscalers": {stage: {"enabled": True} for stage in STAGES}}
    result = templates.submission(settings, 12345, 67890, "Pipeline/test", "client", 1, execution_nonce=654321)
    assert_cleanup_ui(result["prompt"], result["extra_data"]["extra_pnginfo"]["workflow"])


def test_cleanup_export_does_not_depend_on_template_examples():
    api = {
        "latent": {"class_type": "EmptyLatentImage", "inputs": {"width": 512, "height": 512, "batch_size": 1}},
        "v3:cleanup:base": {"class_type": "VRAM_Debug", "inputs": {
            "empty_cache": True, "gc_collect": True, "unload_all_models": False, "any_input": ["latent", 0]}},
        "vae": {"class_type": "VAELoader", "inputs": {"vae_name": "test.safetensors"}},
        "decode": {"class_type": "VAEDecode", "inputs": {"samples": ["v3:cleanup:base", 0], "vae": ["vae", 0]}},
        "v3:cleanup:post_sampler": {"class_type": "VRAM_Debug", "inputs": {
            "empty_cache": True, "gc_collect": True, "unload_all_models": False, "image_pass": ["decode", 0]}},
        "save": {"class_type": "SaveImage", "inputs": {"images": ["v3:cleanup:post_sampler", 1], "filename_prefix": "test"}},
    }
    assert_cleanup_ui(api, export_ui_workflow(api, {}))
