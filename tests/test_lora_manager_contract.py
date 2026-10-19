"""Check generated stacks against the Lora Manager consumer's native contract."""
import copy
from pathlib import Path

import pytest

from anima_webui.workflow import WorkflowTemplates


@pytest.fixture(scope="module")
def templates():
    return WorkflowTemplates.load(Path(__file__).resolve().parents[1] / "templates")


def collected_entries(inputs):
    # The plugin's get_loras_list unwraps __value__; _collect_widget_entries
    # skips entries without active=True before reading name and clipStrength.
    return [
        (entry["name"], float(entry["strength"]), float(entry["clipStrength"]))
        for entry in inputs["loras"]["__value__"] if entry.get("active", False)
    ]


LORAS = [
    {"filename": "styles/ink.safetensors", "enabled": True, "strength": 0.6},
    {"filename": "disabled.safetensors", "enabled": False, "strength": 0.9},
    {"filename": "zoom.safetensors", "enabled": True, "strength": -1.0},
    {"filename": "zero.safetensors", "enabled": True, "strength": 0.0},
]
EXPECTED = [("styles/ink.safetensors", 0.6, 0.6), ("zoom.safetensors", -1.0, -1.0),
            ("zero.safetensors", 0.0, 0.0)]


@pytest.mark.parametrize("workbench", ["single", "random"])
@pytest.mark.parametrize("base_mode", ["normal", "dual_sampler"])
@pytest.mark.parametrize("profile", ["anima", "krea2"])
def test_submitted_loras_reach_manager_and_saved_workflow(templates, workbench, base_mode, profile):
    settings = {"workbench": workbench, "model_profile": profile, "base_mode": base_mode,
                "loras": copy.deepcopy(LORAS)}
    before = copy.deepcopy(settings)
    payload = templates.submission(settings, 123, 456, "LoraContract/test", "client", 1,
                                   frozen_positive_prompt="a portrait")
    api = payload["prompt"]
    assert collected_entries(api["72"]["inputs"]) == EXPECTED
    workflow = payload["extra_data"]["extra_pnginfo"]["workflow"]
    visual = next(node for node in workflow["nodes"] if str(node["id"]) == "72")
    assert collected_entries({"loras": {"__value__": visual["widgets_values"][2]}}) == EXPECTED
    assert settings == before


def test_secondary_stack_keeps_its_own_native_entries_in_api_and_png(templates):
    payload = templates.submission({
        "workbench": "single", "loras": copy.deepcopy(LORAS),
        "single": {"normal_pipeline": "secondary", "loras_secondary": [
            {"filename": "other.safetensors", "enabled": True, "strength": 0.25}]},
    }, 123, 456, "LoraContract/secondary", "client", 1, frozen_positive_prompt="a portrait")
    api = payload["prompt"]
    assert api["691:1240"]["inputs"]["model"] == ["243", 0]
    assert collected_entries(api["243"]["inputs"]) == [("other.safetensors", 0.25, 0.25)]
    workflow = payload["extra_data"]["extra_pnginfo"]["workflow"]
    visual = next(node for node in workflow["nodes"] if str(node["id"]) == "243")
    assert collected_entries({"loras": {"__value__": visual["widgets_values"][2]}}) == [("other.safetensors", 0.25, 0.25)]
