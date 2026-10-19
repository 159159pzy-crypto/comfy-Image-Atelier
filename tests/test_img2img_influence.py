"""The reference image must remain an ancestor of every enabled image stage."""
import copy
from pathlib import Path

import pytest

from anima_webui.workflow import WorkflowTemplates, validate_settings


@pytest.fixture(scope="module")
def templates():
    return WorkflowTemplates.load(Path(__file__).resolve().parents[1] / "templates")


def ancestors(graph, node_id):
    found = set()
    pending = [node_id]
    while pending:
        current = pending.pop()
        if current in found:
            continue
        found.add(current)
        for value in graph[current]["inputs"].values():
            if (isinstance(value, list) and len(value) == 2
                    and isinstance(value[0], str) and isinstance(value[1], int)):
                pending.append(value[0])
    return found


@pytest.mark.parametrize("profile", ["anima", "krea2"])
@pytest.mark.parametrize("denoise", [0, 0.1, 0.65])
@pytest.mark.parametrize("postprocess", [None, "latent", "seedvr2"])
def test_img2img_reference_reaches_output_and_postprocess(templates, profile, denoise, postprocess):
    upscalers = {stage: {"enabled": stage == postprocess}
                 for stage in ("latent", "seedvr2", "nvidia", "face_refine")}
    if postprocess == "latent":
        upscalers["latent"]["second_sampler"] = {"enabled": True, "denoise": 0.23}
    if postprocess == "seedvr2":
        upscalers["seedvr2"]["sampler"] = {"denoise": 0.17}
    settings = validate_settings({
        "workbench": "single", "model_profile": profile, "generation_mode": "img2img", "base_mode": "normal",
        "single": {"positive_prompt": "fixed prompt", "input_image_id": "source_a", "denoise": denoise},
        "loras": [], "upscalers": upscalers,
    })
    graph = templates.submission(settings, 123, 456, "Verification/img2img", "test", 1,
                                 single_runtime={"input_image": "AnimaStudio/source-a.png"})["prompt"]
    assert graph["691:1240"]["inputs"]["denoise"] == denoise
    assert graph["691:1240"]["inputs"]["latent_image"] == ["single:encode", 0]
    assert graph["single:encode"]["inputs"]["pixels"] == ["single:input", 0]
    assert graph["single:encode"]["inputs"]["vae"] == ["167:58", 0]
    assert "single:input" in ancestors(graph, "v2:output")
    assert not any(node["class_type"].startswith("Empty") and "Latent" in node["class_type"]
                   for node in graph.values())
    for node_id, node in graph.items():
        if node["class_type"] in {"KSampler", "KSamplerAdvanced"}:
            assert "single:input" in ancestors(graph, node_id)
    if postprocess == "latent":
        assert graph["704:1248"]["inputs"]["denoise"] == 0.23
    if postprocess == "seedvr2":
        assert graph["712:1208"]["inputs"]["denoise"] == 0.17

    # A same-seed request must use the newly selected asset, with all sampling
    # parameters unchanged. This also guards against stale runtime references.
    other = templates.submission(settings, 123, 456, "Verification/img2img", "test", 1,
                                 single_runtime={"input_image": "AnimaStudio/source-b.png"})["prompt"]
    expected = copy.deepcopy(graph)
    expected["single:input"]["inputs"]["image"] = "AnimaStudio/source-b.png"
    assert other == expected
