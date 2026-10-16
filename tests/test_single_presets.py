import asyncio

from anima_webui.style_presets import StylePresetStore, preset_settings


def test_single_preset_keeps_advanced_parameters_without_source_asset_or_job():
    result = preset_settings({
        "workbench": "single", "generation_mode": "img2img", "base_mode": "normal",
        "model_profile": "anima", "clip_name": "custom-clip.safetensors", "vae_name": "custom-vae.safetensors",
        "single": {
            "positive_prompt": "a ceramic teapot", "input_image_id": "a" * 64,
            "prompt_job_id": "preview_example", "processed_prompt_override": "processed teapot",
            "denoise": 0.42, "weight_dtype": "fp8_e4m3fn",
            "dual": {"switch_step": 9, "second_cfg": 2.7},
            "loras_secondary": [{"filename": "second.safetensors", "enabled": True, "strength": 0.4}],
        },
    })
    assert result["clip_name"] == "custom-clip.safetensors"
    assert result["vae_name"] == "custom-vae.safetensors"
    assert result["single"]["denoise"] == 0.42
    assert result["single"]["dual"]["second_cfg"] == 2.7
    assert result["single"]["loras_secondary"][0]["filename"] == "second.safetensors"
    assert result["single"]["input_image_id"] == ""
    assert result["single"]["prompt_job_id"] == ""
    assert result["single"]["processed_prompt_override"] is None


def test_random_overwrite_preserves_advanced_style_and_reload(tmp_path):
    async def run():
        path = tmp_path / "styles.json"
        store = StylePresetStore(path)
        item = await store.create({"name": "Shared", "settings": {
            "workbench": "single", "base_mode": "normal", "clip_name": "custom-clip.safetensors",
            "single": {"denoise": 0.4, "dual": {"second_cfg": 2.5}},
        }})
        changed = await store.update(item["id"], {"settings": {"workbench": "random", "base_mode": "normal", "width": 1024}})
        assert changed["settings"]["width"] == 1024
        assert changed["settings"]["single"]["denoise"] == 0.4
        assert changed["settings"]["single"]["dual"]["second_cfg"] == 2.5
        assert changed["settings"]["clip_name"] == "custom-clip.safetensors"
        restored = StylePresetStore(path)
        assert restored.list()["items"][0]["settings"] == changed["settings"]

    asyncio.run(run())
