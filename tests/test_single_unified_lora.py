"""Exercise the single workbench's one-list contract at graph and API boundaries.

The frontend supplies primary stage choices for new drafts. Backend defaults stay
unchanged so a historical two-stack request can still be replayed exactly.
"""
import asyncio
import copy

import pytest
from aiohttp import FormData
from aiohttp.test_utils import TestClient, TestServer

from anima_webui.server import HISTORY_KEY, MANAGER_KEY, create_app
from anima_webui.style_presets import PRESET_SETTING_KEYS, preset_settings
from anima_webui.workflow import WorkflowTemplates, validate_settings
from test_server import APP_DIR, _write_fake_tools
from test_single_backend import SingleComfy, png
from test_single_workbench import assert_connected


LORAS = [
    {"filename": "ink.safetensors", "enabled": True, "strength": 0.7},
    {"filename": "unused.safetensors", "enabled": False, "strength": 0.9},
    {"filename": "paper.safetensors", "enabled": True, "strength": -0.2},
]
SECONDARY = [{"filename": "legacy.safetensors", "enabled": True, "strength": 0.4}]


@pytest.fixture(scope="module")
def templates():
    return WorkflowTemplates.load(APP_DIR / "templates")


def single_settings(*, profile="anima", base="normal", mode="txt2img"):
    return validate_settings({
        "workbench": "single", "model_profile": profile,
        "base_mode": base, "generation_mode": mode,
        "loras": copy.deepcopy(LORAS),
        "lora_managed_triggers": ["shared word", "blue ink", "paper grain"],
        "negative_prompt": "blurred",
        "single": {
            "positive_prompt": "portrait, shared word",
            "normal_pipeline": "primary",
            "dual": {"first_pipeline": "primary", "second_pipeline": "primary"},
            "latent": {"pipeline": "primary"}, "face_pipeline": "primary",
            "loras_secondary": [], "secondary_managed_triggers": [],
        },
        "upscalers": {
            "latent": {"enabled": True, "second_sampler": {"enabled": True}},
            "face_refine": {"enabled": True},
        },
    })


def assert_pipeline(api, node_id, pipeline="primary", *, zero=False, variance=False):
    loader = "72" if pipeline == "primary" else "243"
    inputs = api[node_id]["inputs"]
    assert inputs["model"] == [loader, 0]
    expected_positive = [f"single:{pipeline}:positive", 0]
    if variance:
        assert inputs["positive"] == ["single:variance", 0]
        assert api["single:variance"]["inputs"]["conditioning"] == expected_positive
    else:
        assert inputs["positive"] == expected_positive
    negative_node = f"single:{pipeline}:negative"
    if zero:
        assert inputs["negative"] == [f"single:{pipeline}:zero", 0]
        assert api[f"single:{pipeline}:zero"]["inputs"]["conditioning"] == [negative_node, 0]
    else:
        assert inputs["negative"] == [negative_node, 0]
    assert api[f"single:{pipeline}:positive"]["inputs"]["clip"] == [loader, 1]
    assert api[negative_node]["inputs"]["clip"] == [loader, 1]
    if node_id == "1253:1215":
        assert inputs["clip"] == [loader, 1]


@pytest.mark.parametrize("profile", ["anima", "krea2"])
@pytest.mark.parametrize("base,mode", [
    ("normal", "txt2img"), ("normal", "img2img"), ("dual_sampler", "txt2img"),
])
@pytest.mark.parametrize("zero", [False, True])
def test_one_lora_list_drives_every_sampler_and_face_clip(templates, profile, base, mode, zero):
    settings = single_settings(profile=profile, base=base, mode=mode)
    settings["single"]["zero_negative"] = zero
    # A dormant legacy stack must never accidentally feed the repair stages.
    settings["single"]["loras_secondary"] = copy.deepcopy(SECONDARY)
    settings["single"]["secondary_managed_triggers"] = ["wrong legacy trigger"]
    runtime = {"input_image": "AnimaStudio/owned.png"} if mode == "img2img" else None
    api = templates.submission(settings, 123, 456, "Unified/test", "client", 1,
                               single_runtime=runtime)["prompt"]
    assert_connected(api)
    assert "243" not in api
    assert api["72"]["inputs"]["loras"]["__value__"] == [
        {"active": item["enabled"], "name": item["filename"], "strength": item["strength"], "clipStrength": item["strength"]}
        for item in LORAS
    ]
    # Triggers are frozen into the encoding once, never re-appended by the loader.
    assert api["72"]["inputs"]["text"] == ""
    assert api["single:primary:positive"]["inputs"]["text"] == "portrait, shared word, blue ink, paper grain"
    assert api["single:primary:negative"]["inputs"]["text"] == "blurred"
    nodes = ["691:1240"] if base == "normal" else ["701:1245", "701:1246"]
    for node_id in [*nodes, "704:1248", "1253:1215"]:
        assert_pipeline(api, node_id, zero=zero)
    if mode == "img2img":
        assert api["691:1240"]["inputs"]["latent_image"] == ["single:encode", 0]
        assert api["single:input"]["inputs"]["image"] == "AnimaStudio/owned.png"


@pytest.mark.parametrize("base", ["normal", "dual_sampler"])
def test_krea_variance_preserves_primary_conditioning(templates, base):
    settings = single_settings(profile="krea2", base=base)
    settings["krea_variance"]["enabled"] = True
    api = templates.submission(settings, 123, 456, "Unified/variance", "client", 1)["prompt"]
    assert_connected(api)
    assert_pipeline(api, "691:1240" if base == "normal" else "701:1245", variance=True)
    for node_id in (["701:1246"] if base == "dual_sampler" else []) + ["704:1248", "1253:1215"]:
        assert_pipeline(api, node_id)


def test_legacy_defaults_and_two_stack_preset_remain_compatible(templates):
    legacy = validate_settings({"workbench": "single"})
    assert legacy["single"]["latent"]["pipeline"] == "secondary"
    assert legacy["single"]["face_pipeline"] == "secondary"
    legacy = single_settings(base="dual_sampler")
    legacy["single"].update(loras_secondary=copy.deepcopy(SECONDARY), face_pipeline="secondary")
    legacy["single"]["dual"]["second_pipeline"] = "secondary"
    legacy["single"]["latent"]["pipeline"] = "secondary"
    saved = preset_settings({key: value for key, value in legacy.items() if key in PRESET_SETTING_KEYS})
    assert saved["loras"] == LORAS
    assert saved["single"]["loras_secondary"] == SECONDARY
    frozen = {"positive": "historical authored text", "negative": "historical negative",
              "effective_positive": {"primary": "historical ink, ", "secondary": "historical paper, "}}
    payload = templates.submission(saved, 123, 456, "Unified/legacy", "client", 1, single_runtime=frozen)
    api = payload["prompt"]
    assert_connected(api)
    assert_pipeline(api, "701:1245")
    for node_id in ("701:1246", "704:1248", "1253:1215"):
        assert_pipeline(api, node_id, "secondary")
    for pipeline in ("primary", "secondary"):
        assert api[f"single:{pipeline}:positive"]["inputs"]["text"] == frozen["effective_positive"][pipeline]
        assert api[f"single:{pipeline}:negative"]["inputs"]["text"] == frozen["negative"]
    metadata = payload["extra_data"]["extra_pnginfo"]["anima_random_webui"]
    assert metadata["single_runtime"]["effective_positive"] == frozen["effective_positive"]


@pytest.mark.parametrize("base,mode", [
    ("normal", "txt2img"), ("normal", "img2img"), ("dual_sampler", "txt2img"),
])
def test_real_upload_generation_and_replay_keep_unified_triggers(tmp_path, base, mode):
    """Real HTTP/storage/runner boundaries, with only Comfy inference replaced."""
    async def run():
        comfy = SingleComfy()
        comfy.preview_items = [
            {"filename": "ink.safetensors", "trigger_words": ["shared word", "blue ink"]},
            {"filename": "unused.safetensors", "trigger_words": ["disabled trigger"]},
            {"filename": "paper.safetensors", "trigger_words": ["shared word", "paper grain"]},
        ]
        app = create_app(app_dir=APP_DIR, data_dir=tmp_path / "data", comfy=comfy,
                         anima_tools_dir=_write_fake_tools(tmp_path))
        client = TestClient(TestServer(app))
        await client.start_server()
        try:
            settings = single_settings(base=base, mode=mode)
            if mode == "img2img":
                form = FormData()
                form.add_field("image", png(), filename="reference.png", content_type="image/png")
                response = await client.post("/api/input-images", data=form)
                assert response.status == 201, await response.text()
                asset = await response.json()
                settings["single"]["input_image_id"] = asset["id"]
                assert (tmp_path / "data" / "input-images" / f"{asset['id']}.png").is_file()
                assert (await client.get(asset["image_url"])).status == 200
                assert comfy.uploads == []  # Local storage does not contact ComfyUI.
            response = await client.post("/api/batches", json=settings)
            assert response.status == 201, await response.text()
            await app[MANAGER_KEY].wait()
            assert app[MANAGER_KEY].snapshot()["status"] == "completed", app[MANAGER_KEY].snapshot()
            original = (await app[HISTORY_KEY].list_images())["items"][0]
            expected = "portrait, shared word, blue ink, paper grain"
            assert original["single_runtime"]["effective_positive"]["primary"] == expected
            assert "disabled trigger" not in original["positive_prompt"]
            assert len(comfy.uploads) == int(mode == "img2img")

            # Simulate inventory changes after the artwork was saved. Replay
            # must retain its recorded trigger words and complete stage routes.
            for item in comfy.preview_items:
                item["trigger_words"] = ["new inventory trigger"]
            response = await client.post(f"/api/history/{original['id']}/regenerate", json={"mode": "replay"})
            assert response.status == 201, await response.text()
            await app[MANAGER_KEY].wait()
            assert app[MANAGER_KEY].snapshot()["status"] == "completed", app[MANAGER_KEY].snapshot()
            replay = (await app[HISTORY_KEY].list_images())["items"][0]
            assert replay["single_runtime"]["effective_positive"] == original["single_runtime"]["effective_positive"]
            assert replay["sample_seed"] == original["sample_seed"]
            assert replay["prompt_seed"] == original["prompt_seed"]
            assert replay["settings"]["loras"] == original["settings"]["loras"]
            api = comfy.submissions[-1]["prompt"]
            assert_connected(api)
            assert "243" not in api
            assert api["single:primary:positive"]["inputs"]["text"] == expected
            nodes = ["691:1240"] if base == "normal" else ["701:1245", "701:1246"]
            for node_id in [*nodes, "704:1248", "1253:1215"]:
                assert_pipeline(api, node_id)
            assert len(comfy.uploads) == 2 * int(mode == "img2img")
        finally:
            await client.close()

    asyncio.run(run())
