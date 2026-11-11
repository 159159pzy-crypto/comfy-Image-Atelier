import asyncio
import copy
import json
from pathlib import Path
from unittest.mock import patch

import pytest

from anima_webui.preset_contract import DEVICE_PATHS, INCLUDE_KEYS, preset_settings
from anima_webui.style_presets import StylePresetStore
from anima_webui.workflow import WorkflowError, validate_settings


def settings(**overrides):
    return validate_settings({"pipeline_version": 5, "workbench": "single", "base_mode": "normal", **overrides})


def at(value, path):
    for key in path.split("."):
        value = value[key]
    return value


def has(value, path):
    try:
        at(value, path)
        return True
    except (KeyError, TypeError):
        return False


def test_default_patch_contains_only_style_and_middle_dual_resize():
    source = settings(width=1024, single={"positive_prompt": "private body", "prefix": "prefix",
                      "denoise": .4, "weight_dtype": "fp8_e4m3fn", "dual": {"scale": 1.7}})
    source["single"]["prompt_processing"] = "unfinished form"
    source["krea_prompt_pool"] = {"enabled": True, "ids": ["pool-id"]}
    saved = preset_settings(source)
    for key in ("width", "height", "upscalers", "pipeline_version", "generation_mode", "workbench", "krea_prompt_pool"):
        assert key not in saved
    for key in ("positive_prompt", "prefix", "suffix", "denoise", "resize_mode", "prompt_processing",
                "prompt_job_id", "processed_prompt_override", "input_image_id"):
        assert key not in saved["single"]
    assert saved["single"]["dual"]["scale"] == 1.7
    assert all(not has(saved, path) for path in DEVICE_PATHS)


@pytest.mark.parametrize("flag", INCLUDE_KEYS)
def test_each_optional_group_is_independent(flag):
    source = settings(width=1024, height=768, single={"prefix": "ink", "suffix": "paper", "denoise": .42,
                      "resize_mode": "pad"}, upscalers={"pixel_hires": {"enabled": True}})
    saved = preset_settings(source, include={flag: True})
    assert ("width" in saved) == (flag == "size")
    assert ("upscalers" in saved) == (flag == "repair")
    assert ("denoise" in saved["single"]) == (flag == "img2img")
    assert ("prefix" in saved["single"]) == (flag == "affixes")
    assert all(not has(saved, path) for path in DEVICE_PATHS)


@pytest.mark.parametrize("include", [{"unknown": True}, {"repair": 1}, [], False])
def test_invalid_include_is_rejected(include):
    with pytest.raises(WorkflowError):
        preset_settings({}, include=include)


@pytest.mark.parametrize("repair", [False, True])
def test_resolve_preserves_body_pool_ai_devices_and_reports_only_explicit_version_change(tmp_path, repair):
    async def run():
        store = StylePresetStore(tmp_path / "presets.json")
        source = settings(pipeline_version=2, model_profile="krea2", cfg=2, width=768,
                          single={"positive_prompt": "old", "prefix": "old prefix", "weight_dtype": "fp8_e4m3fn",
                                  "vae_decode": {"tile_size": 768}, "dual": {"half_precision": False}},
                          upscalers={"pixel_hires": {"enabled": True}, "seedvr2": {"weight_dtype": "fp16"}})
        item = await store.create({"name": "Style", "include": {"repair": repair}, "settings": source})
        current = settings(model_profile="krea2", width=1152, dual={"half_precision": False}, single={
            "positive_prompt": "current body", "prefix": "current prefix", "suffix": "current suffix",
            "input_image_id": "a" * 64, "prompt_job_id": "preview_job", "processed_prompt_override": "stale",
            "denoise": .23, "resize_mode": "stretch", "weight_dtype": "fp8_e5m2",
            "vae_decode": {"tiled": False}, "dual": {"half_precision": False},
            "prompt_processing": {"expand": {"enabled": True, "llm_service": "local"}}},
            krea_prompt_pool={"enabled": True, "ids": ["keep"]},
            upscalers={"seedvr2": {"weight_dtype": "fp8_e4m3fn", "encode": {"tile_size": 768}},
                       "nvidia": {"dlss": {"gpu_gen": "RTX 40"}}, "dlss": {"gpu_gen": "RTX 50"},
                       "face_refine": {"offload_models_to_cpu": False}, "latent": {"half_precision": False}})
        before = copy.deepcopy(current)
        disk = store.path.read_bytes()
        resolved = store.resolve(item["id"], current, "krea2")
        value = resolved["settings"]
        assert value["cfg"] == 2
        assert value["width"] == current["width"]
        assert value["krea_prompt_pool"] == current["krea_prompt_pool"]
        for key in ("positive_prompt", "prefix", "suffix", "denoise", "resize_mode", "input_image_id", "prompt_processing"):
            assert value["single"][key] == current["single"][key]
        for path in DEVICE_PATHS:
            assert at(value, path) == at(current, path), path
        assert value["single"]["prompt_job_id"] == ""
        assert value["single"]["processed_prompt_override"] is None
        assert resolved["version_change"] == ({"from": 5, "to": 2} if repair else None)
        assert value["pipeline_version"] == (2 if repair else 5)
        if not repair:
            assert value["upscalers"] == current["upscalers"]
        assert current == before and store.path.read_bytes() == disk
    asyncio.run(run())


def test_cross_workbench_random_preserves_routes_resources_and_only_maps_active_zero_negative(tmp_path):
    async def run():
        store = StylePresetStore(tmp_path / "presets.json")
        item = await store.create({"name": "Random", "settings": {"workbench": "random", "base_mode": "normal", "zero_negative": True}})
        current = settings(clip_name="my-clip", vae_name="my-vae", single={"normal_pipeline": "secondary",
                           "zero_negative": False, "secondary_zero_negative": False,
                           "loras_secondary": [{"filename": "old.safetensors", "enabled": True, "strength": .4}]})
        target = store.resolve(item["id"], current)["settings"]
        assert target["single"]["zero_negative"] is False
        assert target["single"]["secondary_zero_negative"] is True
        assert target["single"]["normal_pipeline"] == "secondary"
        assert target["single"]["loras_secondary"] == current["single"]["loras_secondary"]
        for key in ("clip_name", "vae_name", "clip_type"):
            assert target[key] == current[key]
    asyncio.run(run())


def test_cross_workbench_single_uses_final_base_route_zero_negative(tmp_path):
    async def run():
        store = StylePresetStore(tmp_path / "presets.json")
        item = await store.create({"name": "Single", "settings": settings(base_mode="dual_sampler", steps=30,
            single={"zero_negative": False, "secondary_zero_negative": True,
                    "dual": {"second_pipeline": "secondary", "second_cfg": 2.5, "scale": 1.8}})})
        current = validate_settings({"pipeline_version": 5, "workbench": "random"})
        target = store.resolve(item["id"], current)["settings"]
        assert target["workbench"] == "random" and target["zero_negative"] is True
        assert target["dual"]["second_cfg"] == 2.5 and target["dual"]["scale"] == 1.8
        assert target["single"]["loras_secondary"] == current["single"]["loras_secondary"]
    asyncio.run(run())


def test_metadata_edits_do_not_recapture_source_or_settings_and_new_flags_require_capture(tmp_path):
    async def run():
        store = StylePresetStore(tmp_path / "presets.json")
        item = await store.create({"name": "Style", "settings": settings(pipeline_version=2)})
        for change in ({"name": "Renamed"}, {"favorite": True}, {"tags": ["warm", "Warm"]}, {"cover_id": "a" * 64}):
            updated = await store.update(item["id"], change)
            assert updated["settings"] == item["settings"] and updated["source"] == item["source"]
        with pytest.raises(WorkflowError, match="当前 settings"):
            await store.update(item["id"], {"include": {"size": True}})
        with pytest.raises(WorkflowError):
            await store.update(item["id"], {"include": {"unknown": True}})
        with pytest.raises(WorkflowError):
            store.resolve(item["id"], settings(model_profile="krea2"))
    asyncio.run(run())


def test_groups_tags_and_batch_are_atomic_and_isolated(tmp_path):
    async def run():
        store = StylePresetStore(tmp_path / "presets.json")
        group = await store.create_group({"name": "Ink", "model_profile": "anima"})
        krea_group = await store.create_group({"name": "Ink", "model_profile": "krea2"})
        with pytest.raises(WorkflowError, match="同名"):
            await store.create_group({"name": "INK", "model_profile": "anima"})
        a = await store.create({"name": "A", "settings": {}})
        b = await store.create({"name": "B", "settings": {}})
        krea = await store.create({"name": "K", "model_profile": "krea2", "settings": {}})
        payload = {"model_profile": "anima", "ids": [a["id"], b["id"]], "action": "groups",
                   "value": {"mode": "add", "values": [group["id"]]}}
        await store.batch(payload)
        assert all(store.get(item["id"])["favorite"] for item in (a, b))
        await store.batch({**payload, "action": "favorite", "value": False})
        assert store.get(a["id"])["groups"] == [group["id"]]
        await store.batch({**payload, "action": "tags", "value": {"mode": "add", "values": [" Warm ", "warm", "Ink"]}})
        assert store.get(a["id"])["tags"] == ["Warm", "Ink"]
        before, disk = copy.deepcopy((store.items, store.groups)), store.path.read_bytes()
        for invalid in ({**payload, "ids": [a["id"], krea["id"]]},
                        {**payload, "value": {"mode": "replace", "values": [krea_group["id"]]}}):
            with pytest.raises(WorkflowError):
                await store.batch(invalid)
            assert (store.items, store.groups) == before and store.path.read_bytes() == disk
        with patch.object(store, "_write_text", side_effect=OSError("disk full")):
            with pytest.raises(OSError):
                await store.batch({**payload, "action": "favorite", "value": True})
            with pytest.raises(OSError):
                await store.delete_group(group["id"], "anima")
        assert (store.items, store.groups) == before and store.path.read_bytes() == disk
        await store.delete_group(group["id"], "anima")
        assert store.get(a["id"])["groups"] == [] and store.get(a["id"])["favorite"] is False
        assert len(store.items) == 3
        restored = StylePresetStore(store.path)
        assert restored.items == store.items and restored.groups == store.groups
    asyncio.run(run())


def test_real_legacy_presets_migrate_only_in_temporary_copy(tmp_path):
    source = Path(__file__).resolve().parents[1] / "data" / "style_presets.json"
    if not source.is_file():
        pytest.skip("local legacy dataset is unavailable")
    original = source.read_bytes()
    if json.loads(original)["version"] >= 3:
        pytest.skip("local dataset is already migrated")
    original_items = json.loads(original)["items"]
    destination = tmp_path / "style_presets.json"
    destination.write_bytes(original)
    store = StylePresetStore(destination)
    assert len(store.items) == len(original_items)
    assert source.read_bytes() == original
    assert all(not any(item["include"].values()) for item in store.items)
    assert all("upscalers" not in item["settings"] and "width" not in item["settings"] for item in store.items)
    if json.loads(original)["version"] < 3:
        assert destination.with_name(destination.name + ".v3.bak").read_bytes() == original
    assert json.loads(destination.read_text(encoding="utf-8"))["version"] == 3
    assert StylePresetStore(destination).items == store.items


@pytest.mark.parametrize("enabled", [False, True])
def test_all_optional_groups_resolve_and_can_be_removed_without_default_backfill(tmp_path, enabled):
    async def run():
        store = StylePresetStore(tmp_path / "presets.json")
        source = settings(width=1024, height=768, single={"prefix": "ink", "suffix": "paper", "denoise": .42,
                          "resize_mode": "pad", "dual": {"scale": 1.8}},
                          upscalers={"pixel_hires": {"enabled": True, "percent": 70}})
        item = await store.create({"name": "Style", "settings": source, "include": {key: enabled for key in INCLUDE_KEYS}})
        current = settings(width=768, height=1152, single={"prefix": "current", "denoise": .6, "resize_mode": "crop"})
        target = store.resolve(item["id"], current)["settings"]
        chosen = source if enabled else current
        for key in ("width", "height", "upscalers"):
            assert target[key] == chosen[key]
        for key in ("prefix", "suffix", "denoise", "resize_mode"):
            assert target["single"][key] == chosen["single"][key]
        # Middle resize belongs to the base recipe regardless of repair flag.
        assert target["single"]["dual"]["scale"] == 1.8
        await store.update(item["id"], {"include": {}})
        restored = StylePresetStore(store.path)
        after = restored.resolve(item["id"], current)["settings"]
        for key in ("width", "height", "upscalers"):
            assert after[key] == current[key]
        assert after["single"]["prefix"] == "current"
    asyncio.run(run())


@pytest.mark.parametrize("version", [1, 2])
def test_legacy_migration_backup_failure_keeps_source_and_protects_store(tmp_path, version):
    destination = tmp_path / "presets.json"
    body = json.dumps({"version": version, "items": [{"id": "old", "name": "Old", "settings": {
        "workbench": "single", "base_mode": "normal", "single": {"positive_prompt": "discard body"}, "width": 1024}}]}).encode()
    destination.write_bytes(body)
    with patch("anima_webui.style_presets.backup_for_migration", side_effect=OSError("no space")):
        store = StylePresetStore(destination)
    assert destination.read_bytes() == body
    assert store.read_only_reason and len(store.items) == 1
    assert not any(store.items[0]["include"].values())
    assert "positive_prompt" not in store.items[0]["settings"]["single"]


def test_legacy_random_dual_is_independent_of_current_repair_and_keeps_current_precision(tmp_path):
    async def run():
        store = StylePresetStore(tmp_path / "presets.json")
        item = await store.create({"name": "Legacy dual", "settings": {
            "pipeline_version": 2, "workbench": "random", "base_mode": "dual_sampler", "steps": 30,
            "cfg": 3, "sampler_name": "heun", "scheduler": "karras", "dual": None,
            "upscalers": {"latent": {"scale_by": 1.75, "upscale_method": "bicubic", "half_precision": True}}}})
        current = validate_settings({"pipeline_version": 5, "workbench": "random", "base_mode": "normal", "dual": None,
                                     "upscalers": {"latent": {"enabled": True, "scale_by": 1.2, "half_precision": False}}})
        resolved = store.resolve(item["id"], current)
        target = resolved["settings"]
        assert resolved["version_change"] is None
        assert target["upscalers"] == current["upscalers"]
        assert target["dual"] == {"switch_step": 15, "scale": 1.75, "second_cfg": 3,
            "second_sampler_name": "heun", "second_scheduler": "karras", "upscale_method": "bicubic",
            "model_format": "Wan 2.1", "half_precision": False}
        assert target["pipeline_version"] == 5
    asyncio.run(run())
