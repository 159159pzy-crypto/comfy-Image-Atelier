import asyncio
import copy
import json
from pathlib import Path

import pytest

from anima_webui.comfy import ComfyClient, ComfyError
from anima_webui.comfy_extension.atelier_resources.inspector import inspect_file, read_header, recognize_header
from anima_webui.resource_resolution import build_resource_catalog, resolve_resources
from anima_webui.resource_usage import ResourceUsageStore
from anima_webui.workflow import WorkflowError, validate_settings


def write_model(path, tensors):
    header = {key: {"dtype": "F16", "shape": shape, "data_offsets": [0, 2]} for key, shape in tensors.items()}
    raw = json.dumps(header).encode()
    Path(path).write_bytes(len(raw).to_bytes(8, "little") + raw + b"\0\0")


def catalog_inventory(settings, **extra):
    records = [("models", settings["model_name"], [settings["model_profile"] + ".model"]),
               ("clips", settings["clip_name"], [settings["model_profile"] + ".clip"]),
               ("vaes", settings["vae_name"], [settings["model_profile"] + ".vae"])]
    result = {kind: [name for item_kind, name, _ in records if item_kind == kind] for kind in ("models", "clips", "vaes")}
    result["resource_catalog"] = [{"kind": kind, "name": name, "roles": roles, "status": "recognized", "source": "header", "fingerprint": name} for kind, name, roles in records]
    result.update(extra)
    return result


@pytest.mark.parametrize("kind,tensors,roles", [
    ("models", {"blocks.0.mlp.layer1.weight": [1], "llm_adapter.blocks.0.cross_attn.q_proj.weight": [1]}, ["anima.model"]),
    ("models", {"net.blocks.0.mlp.layer1.weight": [1], "net.llm_adapter.blocks.0.cross_attn.q_proj.weight": [1]}, ["anima.model"]),
    ("models", {"txtfusion.projector.weight": [1], "first.weight": [1], "blocks.0.attn.wq.weight": [1]}, ["krea2.model"]),
    ("models", {"blocks.31.mlp.all.proj_in_gate.weight": [1]}, ["seedvr2.model"]),
    ("models", {"blocks.35.mlp.vid.proj_out.weight": [3072, 1]}, ["seedvr2.model"]),
    ("clips", {"model.layers.0.self_attn.q_norm.weight": [1], "model.layers.0.post_attention_layernorm.weight": [1024]}, ["anima.clip"]),
    ("clips", {"model.visual.deepstack_merger_list.0.norm.weight": [1], "model.visual.merger.linear_fc2.weight": [2560, 1]}, ["krea2.clip"]),
    ("vaes", {"decoder.up_blocks.2.upsamplers.0.upscale_conv.weight": [1]}, ["seedvr2.vae"]),
    ("vaes", {"decoder.middle.0.residual.0.gamma": [1], "decoder.head.2.weight": [3, 1], "encoder.conv1.weight": [1, 3]}, ["anima.vae", "krea2.vae"]),
])
def test_renamed_models_are_identified_from_headers(tmp_path, kind, tensors, roles):
    path = tmp_path / "entirely-renamed.safetensors"
    write_model(path, tensors)
    item = inspect_file(kind, path.name, path)
    assert item["roles"] == roles
    assert item["status"] == "recognized"
    assert item["fingerprint"].startswith("v1:")


def test_wrong_named_model_is_not_recognized_and_malformed_headers_are_bounded(tmp_path):
    path = tmp_path / "anima-base-v1.0.safetensors"
    write_model(path, {"head.modulation": [1]})
    assert inspect_file("models", path.name, path)["status"] == "incompatible"
    path.write_bytes((2**40).to_bytes(8, "little") + b"{}")
    with pytest.raises(ValueError, match="长度"):
        read_header(path)
    assert inspect_file("models", path.name, path)["status"] == "invalid"


def test_other_encoder_sizes_and_vae_layouts_are_incompatible():
    def tensor(shape):
        return {"shape": shape}
    header = {"model.layers.0.self_attn.q_norm.weight": tensor([1]), "model.layers.0.post_attention_layernorm.weight": tensor([2560])}
    assert recognize_header(header, "clips") == ([], "incompatible")
    header = {"decoder.middle.0.residual.0.gamma": tensor([1]), "decoder.upsamples.0.upsamples.0.residual.2.weight": tensor([1])}
    assert recognize_header(header, "vaes") == ([], "incompatible")


def test_known_encoder_in_unet_or_upscale_folder_cannot_be_manually_claimed():
    header = {"model.layers.0.self_attn.q_norm.weight": {"shape": [1]},
              "model.layers.0.post_attention_layernorm.weight": {"shape": [1024]}}
    assert recognize_header(header, "models") == ([], "incompatible")
    assert recognize_header(header, "upscale_models") == ([], "incompatible")


def test_manual_roles_are_bound_to_file_identity_and_persist(tmp_path):
    path = tmp_path / "unknown.safetensors"
    write_model(path, {"unknown": [1]})
    first = inspect_file("models", path.name, path)
    store = ResourceUsageStore(tmp_path / "roles.json")
    asyncio.run(store.set_usage("models", path.name, ["anima.model"], first["fingerprint"]))
    store = ResourceUsageStore(store.path)
    inventory = {"models": [path.name], "resource_inspection": {"items": [first]}}
    assert build_resource_catalog(inventory, [], store)[0]["source"] == "manual"
    write_model(path, {"different_unknown": [1]})
    inventory["resource_inspection"]["items"] = [inspect_file("models", path.name, path)]
    assert build_resource_catalog(inventory, [], store)[0]["status"] == "unknown"
    with pytest.raises(WorkflowError, match="不能覆盖"):
        asyncio.run(store.set_usage("models", path.name, ["anima.model"], "new-id", source_status="incompatible"))
    with pytest.raises(WorkflowError, match="不兼容"):
        asyncio.run(store.set_usage("clips", path.name, ["seedvr2.model"], "new-id"))
    for kind in ([], {}, None, 1):
        with pytest.raises(WorkflowError, match="类型"):
            asyncio.run(store.set_usage(kind, path.name, ["anima.model"], "new-id"))
        with pytest.raises(WorkflowError, match="类型"):
            asyncio.run(store.remove_usage(kind, path.name))


def test_catalog_uses_classification_not_canonical_names_and_accepts_upscale_loader():
    inventory = {"models": ["anima-base-v1.0.safetensors", "renamed.safetensors"], "upscale_models": ["custom.pth"]}
    result = build_resource_catalog(inventory, [{"name": "renamed.safetensors", "model_profile": "anima", "classification_status": "recognized"}])
    assert next(row for row in result if row["name"].startswith("anima-base"))["status"] == "unknown"
    assert next(row for row in result if row["name"] == "renamed.safetensors")["roles"] == ["anima.model"]
    assert next(row for row in result if row["name"] == "custom.pth")["roles"] == ["upscale"]


def test_disabled_stages_do_not_report_missing_and_enabled_seed_uses_other_model():
    settings = validate_settings({"base_mode": "normal"})
    inventory = catalog_inventory(settings)
    before = copy.deepcopy((settings, inventory))
    assert resolve_resources(settings, inventory)["ready"]
    settings["upscalers"]["seedvr2"]["enabled"] = True
    missing = resolve_resources(settings, inventory)
    assert {item["module"] for item in missing["issues"]} == {"seedvr2"}
    for kind, name, role in (("models", "Repair/renamed-7b.safetensors", "seedvr2.model"), ("vaes", "Repair/renamed-vae.safetensors", "seedvr2.vae")):
        inventory[kind].append(name)
        inventory["resource_catalog"].append({"kind": kind, "name": name, "roles": [role], "status": "recognized"})
    selected = resolve_resources(settings, inventory)
    assert selected["ready"] and len(selected["replacements"]) == 2
    assert settings["upscalers"]["seedvr2"]["model_name"] == before[0]["upscalers"]["seedvr2"]["model_name"]


def test_current_valid_selection_is_kept_and_missing_selection_uses_stable_order():
    settings = validate_settings({"base_mode": "normal", "model_name": "other.safetensors"})
    inventory = catalog_inventory(settings)
    inventory["models"] += ["z.safetensors", "a.safetensors"]
    inventory["resource_catalog"] += [{"kind": "models", "name": name, "roles": ["anima.model"], "status": "recognized"} for name in ["z.safetensors", "a.safetensors"]]
    assert resolve_resources(settings, inventory)["replacements"] == []
    settings["model_name"] = "missing.safetensors"
    assert resolve_resources(settings, inventory)["effective_settings"]["model_name"] == "a.safetensors"


def test_basename_collision_does_not_silently_select_a_path():
    settings = validate_settings({"base_mode": "normal", "model_name": "same.safetensors"})
    inventory = catalog_inventory(settings)
    inventory["models"] = ["A/same.safetensors", "B/same.safetensors"]
    inventory["resource_catalog"] = [row for row in inventory["resource_catalog"] if row["kind"] != "models"]
    inventory["resource_catalog"] += [{"kind": "models", "name": name, "roles": ["anima.model"], "status": "recognized"} for name in inventory["models"]]
    result = resolve_resources(settings, inventory)
    assert result["issues"][0]["code"] == "ambiguous_path"
    assert result["effective_settings"]["model_name"] == settings["model_name"]


def test_dual_sampling_depends_on_sesqui_even_with_repair_disabled():
    settings = validate_settings({"base_mode": "dual_sampler"})
    inventory = catalog_inventory(settings, capabilities={"sesqui.Wan 2.1": {"status": "missing", "message": "missing weight"}})
    result = resolve_resources(settings, inventory)
    assert {row["module"] for row in result["issues"]} == {"base_dual"}


def test_native_and_face_dependencies_are_checked_independently():
    settings = validate_settings({"pipeline_version": 5, "base_mode": "normal", "upscalers": {"nvidia": {"enabled": True}, "face_refine": {"enabled": True, "enable_segmentation": False}}})
    inventory = catalog_inventory(settings, capabilities={"nvidia.vsr": {"status": "ready"}, "nvidia.dlss": {"status": "missing"}, "face.detect": {"status": "ready"}, "face.segment": {"status": "missing"}})
    assert resolve_resources(settings, inventory)["ready"]
    settings["upscalers"]["dlss"]["enabled"] = True
    assert {row["module"] for row in resolve_resources(settings, inventory)["issues"]} == {"dlss"}


def test_missing_enabled_lora_never_replaced():
    settings = validate_settings({"base_mode": "normal", "loras": [{"filename": "old.safetensors", "enabled": True, "strength": 1}]})
    result = resolve_resources(settings, catalog_inventory(settings, loras=["other.safetensors"]))
    assert result["issues"][0]["code"] == "missing_lora"
    assert result["effective_settings"]["loras"] == settings["loras"]


def test_lora_identity_only_freezes_enabled_entries_in_active_stacks():
    settings = validate_settings({"workbench": "single", "base_mode": "normal",
        "loras": [{"filename": "inactive.safetensors", "enabled": True, "strength": 1}],
        "single": {"normal_pipeline": "secondary", "loras_secondary": [
            {"filename": "selected.safetensors", "enabled": True, "strength": 1},
            {"filename": "disabled.safetensors", "enabled": False, "strength": 1}]}})
    inventory = catalog_inventory(settings, lora_files=[
        {"name": "selected.safetensors", "fingerprint": "file-v1", "status": "available"}])
    before = copy.deepcopy((settings, inventory))
    result = resolve_resources(settings, inventory)
    assert result["ready"] and not result["replacements"]
    assert [row for row in result["resources"] if row["kind"] == "loras"] == [{
        "field": "single.loras_secondary.0.filename", "kind": "loras",
        "name": "selected.safetensors", "fingerprint": "file-v1"}]
    assert (settings, inventory) == before
    inventory["lora_files"][0]["fingerprint"] = ""
    assert resolve_resources(settings, inventory)["issues"][0]["code"] == "invalid_lora"
    inventory["lora_files"] = []
    assert resolve_resources(settings, inventory)["issues"][0]["code"] == "missing_lora"


def test_encoder_type_follows_resolved_profile_and_is_reported():
    settings = validate_settings({"model_profile": "krea2", "base_mode": "normal"})
    settings["clip_type"] = "stable_diffusion"
    result = resolve_resources(settings, catalog_inventory(settings))
    assert result["effective_settings"]["clip_type"] == "krea2"
    assert {row["field"] for row in result["replacements"]} == {"clip_type"}


@pytest.mark.parametrize("version,post_enabled,expect_scale", [(2, False, False), (3, True, False), (4, True, False), (5, True, True)])
def test_pixel_node_requirements_follow_version(version, post_enabled, expect_scale):
    settings = validate_settings({"base_mode": "normal", "upscalers": {"pixel_hires": {"enabled": True}, "post_sampler": {"enabled": post_enabled}}})
    settings["pipeline_version"] = version
    inventory = catalog_inventory(settings, upscale_models=[settings["upscalers"]["pixel_hires"]["model_name"]],
                                  node_types=["UNETLoader", "CLIPLoader", "VAELoader", "CLIPTextEncode", "SaveImage", "KSampler", "UpscaleModelLoader", "ImageUpscaleWithModel"])
    inventory["resource_catalog"].append({"kind": "upscale_models", "name": settings["upscalers"]["pixel_hires"]["model_name"], "roles": ["upscale"], "status": "recognized"})
    result = resolve_resources(settings, inventory)
    assert any(row["field"] == "nodes.ImageScaleBy" for row in result["issues"]) is expect_scale


def test_invalid_fast_face_choice_is_not_treated_as_a_model_free_algorithm():
    settings = validate_settings({"base_mode": "normal", "upscalers": {"face_refine": {"enabled": True}}})
    settings["upscalers"]["face_refine"]["upscaler_model"] = "Fast imaginary option"
    result = resolve_resources(settings, catalog_inventory(settings))
    assert any(row["code"] == "invalid_choice" for row in result["issues"])


def test_empty_inventory_is_a_valid_configuration_state():
    class Empty(ComfyClient):
        async def object_info(self):
            return {"CLIPLoader": {"input": {"required": {"clip_name": [[]]}}}}
    client = Empty()
    inventory = asyncio.run(client.resource_inventory())
    assert inventory["models"] == inventory["clips"] == inventory["samplers"] == []
    result = resolve_resources(validate_settings({"base_mode": "normal"}), inventory)
    assert not result["ready"] and any(item["code"] == "missing_model" for item in result["issues"])


def test_missing_inspection_extension_does_not_raise_and_modules_are_explicit():
    class Client(ComfyClient):
        def __init__(self):
            super().__init__()
            self.paths = []
        async def _json(self, method, path, **kwargs):
            self.paths.append(path)
            raise ComfyError("404")
    async def run():
        client = Client()
        assert not (await client.resource_inspection())["supported"]
        await client.resource_inspection(modules=["nvidia"])
        assert client.paths == ["/anima/resources/inspect", "/anima/resources/inspect?probe=nvidia"]
    asyncio.run(run())
