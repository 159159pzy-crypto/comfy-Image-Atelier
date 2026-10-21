from __future__ import annotations

import gzip
import hashlib
import io
import json
import struct
import zlib
from pathlib import Path

import pytest
from PIL import Image
from PIL.PngImagePlugin import PngInfo

from anima_webui._vendor.sd_prompt_reader import UPSTREAM_COMMIT
from anima_webui._vendor.sd_prompt_reader.comfyui import parse_comfyui
from anima_webui._vendor.sd_prompt_reader.formats import MAX_METADATA_BYTES, decode_user_comment, draw_things
from anima_webui.prompt_images import PromptImageStore
from anima_webui.workflow import WorkflowError


def image_bytes(info=None, *, format="PNG", exif=None, comment=None, image=None):
    stream = io.BytesIO()
    own = image is None
    image = image or Image.new("RGB", (32, 24), "#446699")
    kwargs = {}
    if info:
        pnginfo = PngInfo()
        for key, value in info.items():
            pnginfo.add_text(key, value if isinstance(value, str) else json.dumps(value, ensure_ascii=False))
        kwargs["pnginfo"] = pnginfo
    if exif is not None:
        kwargs["exif"] = exif
    if comment is not None:
        kwargs["comment"] = comment
    if format == "WEBP":
        kwargs.update(lossless=True, exact=True)
    image.save(stream, format=format, **kwargs)
    if own:
        image.close()
    return stream.getvalue()


def exif_comment(value, *, model=None):
    exif = Image.Exif()
    if value is not None:
        exif[0x8769] = {0x9286: b"UNICODE\x00" + value.encode("utf-16-be")}
    if model is not None:
        exif[0x0110] = model
    return exif


def assert_prompt(tmp_path, body, expected="a blue bird", negative="blur", format_contains=None):
    asset = PromptImageStore(tmp_path).add(body, filename="original.png")
    result = asset["metadata"]
    assert result["source"] == "metadata", result
    assert result["text"] == expected
    assert result["negative"] == negative
    assert len(result["candidates"]) == 1
    if format_contains:
        assert format_contains in result["format"]
    return result


@pytest.mark.parametrize("format", ["PNG", "JPEG", "WEBP"])
def test_a1111_all_containers_unicode_and_large_seeds(tmp_path, format):
    raw = 'a blue bird\nNegative prompt: blur\nSteps: 24, Sampler: Euler a, CFG scale: 0, Seed: 18446744073709551615, Size: 512x768, Model: "bird, painted", Denoising strength: 0.4'
    body = image_bytes({"parameters": raw}) if format == "PNG" else image_bytes(format=format, exif=exif_comment(raw))
    parsed = assert_prompt(tmp_path, body, format_contains="A1111")
    assert parsed["parameters"]["seed"] == "18446744073709551615"
    assert parsed["parameters"]["cfg"] == "0"
    assert parsed["parameters"]["model"] == "bird, painted"
    assert "Denoising strength" in parsed["parameters"]


@pytest.mark.parametrize("has_prompt", [True, False])
def test_a1111_postprocessing_keeps_parameters_and_requires_a_usable_prompt(tmp_path, has_prompt):
    extra = "Postprocess upscaler: ESRGAN, Postprocess upscale by: 2"
    info = {"postprocessing": extra}
    if has_prompt:
        info["parameters"] = "a blue bird\nNegative prompt: blur\nSteps: 24, Seed: 0"
    parsed = PromptImageStore(tmp_path).add(image_bytes(info))["metadata"]
    assert parsed["format"] == "A1111"
    assert parsed["parameters"]["postprocessing"] == extra
    if has_prompt:
        assert parsed["source"] == "metadata"
        assert parsed["text"] == "a blue bird"
        assert parsed["negative"] == "blur"
        assert parsed["parameters"]["seed"] == "0"
    else:
        assert parsed["source"] == "none"
        assert parsed["text"] == ""
        assert not parsed["candidates"]


@pytest.mark.parametrize("legacy", [False, True])
@pytest.mark.parametrize("format", ["PNG", "JPEG", "WEBP"])
def test_easy_diffusion_all_containers_and_field_generations(tmp_path, legacy, format):
    values = {"prompt": "a bird's blue feather", "negative_prompt": "blur", "seed": 0,
              "use_stable_diffusion_model": "C:/models/bird.safetensors", "width": 512, "height": 768,
              "num_inference_steps": 24, "guidance_scale": 7}
    if legacy:
        names = {"prompt": "Prompt", "negative_prompt": "Negative Prompt", "seed": "Seed",
                 "use_stable_diffusion_model": "Stable Diffusion model", "width": "Width", "height": "Height",
                 "num_inference_steps": "Steps", "guidance_scale": "Guidance Scale"}
        values = {names[k]: v for k, v in values.items()}
    body = image_bytes(values) if format == "PNG" else image_bytes(format=format, exif=exif_comment(json.dumps(values)))
    parsed = assert_prompt(tmp_path, body, expected="a bird's blue feather", format_contains="Easy Diffusion")
    assert parsed["parameters"]["seed"] == "0"


@pytest.mark.parametrize("legacy", [False, True])
@pytest.mark.parametrize("format", ["PNG", "JPEG"])
def test_swarm_current_and_legacy(tmp_path, legacy, format):
    values = {"sui_image_params": {"prompt": "a blue bird", "negativeprompt": "blur", "seed": 0,
                                   "steps": 24, "cfgscale": 7, "comfyuisampler": "euler", "model": "bird"}}
    raw = json.dumps(values)
    if legacy:
        body = image_bytes(format=format, exif=exif_comment(None, model=raw))
    elif format == "PNG":
        body = image_bytes({"parameters": raw})
    else:
        body = image_bytes(format=format, exif=exif_comment(raw))
    parsed = assert_prompt(tmp_path, body, format_contains="StableSwarmUI")
    assert parsed["parameters"]["seed"] == "0"
    assert parsed["parameters"]["sampler"] == "euler"


@pytest.mark.parametrize("format", ["PNG", "JPEG"])
def test_fooocus_mre(tmp_path, format):
    data = {"prompt": "a blue bird", "negative_prompt": "blur", "seed": 123, "base_model": "bird",
            "steps": 30, "cfg": 6, "sampler": "dpmpp", "width": 512, "height": 768}
    body = image_bytes({"Comment": data}) if format == "PNG" else image_bytes(format=format, comment=json.dumps(data).encode())
    parsed = assert_prompt(tmp_path, body, format_contains="Fooocus-MRE")
    assert parsed["parameters"]["model"] == "bird"


@pytest.mark.parametrize("software", ["NovelAI", None])
def test_novelai_legacy_and_naifu_compatible_metadata(tmp_path, software):
    info = {"Description": "a blue bird", "Comment": {"uc": "blur", "seed": 0, "steps": 28, "scale": 5}}
    if software:
        info["Software"] = software
    parsed = assert_prompt(tmp_path, image_bytes(info), format_contains="NovelAI")
    assert parsed["parameters"]["seed"] == "0"
    assert parsed["parameters"]["actual_image_size"] == "32x24"


def stealth_image(data, *, declared_bits=None):
    compressed = gzip.compress(json.dumps(data).encode())
    payload = b"stealth_pngcomp" + struct.pack(">I", len(compressed) * 8 if declared_bits is None else declared_bits) + compressed
    image = Image.new("RGBA", (max(128, (len(payload) * 8 + 127) // 128), 128), (30, 60, 90, 254))
    pixels = image.load()
    index = 0
    for byte in payload:
        for shift in range(7, -1, -1):
            x, y = index // image.height, index % image.height
            pixels[x, y] = (30, 60, 90, 254 | ((byte >> shift) & 1))
            index += 1
    return image


@pytest.mark.parametrize("format", ["PNG", "WEBP"])
def test_novelai_stealth_all_containers(tmp_path, format):
    data = {"Description": "unused", "Comment": json.dumps({"prompt": "a blue bird", "uc": "blur", "seed": 0})}
    with stealth_image(data) as image:
        parsed = assert_prompt(tmp_path, image_bytes(image=image, format=format), format_contains="stealth")
    assert parsed["parameters"]["seed"] == "0"


@pytest.mark.parametrize("variant", ["current", "sd-metadata", "Dream"])
def test_invokeai_three_generations(tmp_path, variant):
    if variant == "current":
        info = {"invokeai_metadata": {"positive_prompt": "a blue bird", "negative_prompt": "blur", "seed": 0,
                                      "model": {"model_name": "bird"}, "scheduler": "euler", "cfg_scale": 7, "steps": 30}}
    elif variant == "sd-metadata":
        info = {"sd-metadata": {"model_weights": "bird", "image": {"prompt": [{"prompt": "a blue bird[blur]"}], "seed": 0}}}
    else:
        info = {"Dream": '"a blue bird[blur]" -s 30 -S 0 -W 512 -H 768 -C 7 -A euler'}
    parsed = assert_prompt(tmp_path, image_bytes(info), format_contains="InvokeAI")
    assert parsed["parameters"]["seed"] == "0"


def test_draw_things_xmp_whitespace_and_namespaces(tmp_path):
    value = json.dumps({"c": "a blue bird", "uc": "blur", "seed": 0, "size": "512x768", "model": "bird"})
    xmp = f'<x:xmpmeta xmlns:x="adobe:ns:meta/" xmlns:exif="http://ns.adobe.com/exif/1.0/" xmlns:rdf="rdf"><rdf:RDF><exif:UserComment><rdf:Alt><rdf:li>{value}</rdf:li></rdf:Alt></exif:UserComment></rdf:RDF></x:xmpmeta>'
    assert_prompt(tmp_path, image_bytes({"XML:com.adobe.xmp": xmp}), format_contains="Draw Things")


def graph(positive="a blue bird", negative="blur", seed=0, suffix=""):
    return {"out" + suffix: {"class_type": "SaveImage", "inputs": {"images": ["decode" + suffix, 0]}},
            "decode" + suffix: {"class_type": "VAEDecode", "inputs": {"samples": ["sampler" + suffix, 0]}},
            "sampler" + suffix: {"class_type": "KSampler", "inputs": {"positive": ["single:positive" + suffix, 0],
                "negative": ["single:negative" + suffix, 0], "seed": seed, "steps": 30, "cfg": 7,
                "sampler_name": "euler", "model": ["model" + suffix, 0]}},
            "single:positive" + suffix: {"class_type": "CLIPTextEncode", "inputs": {"text": positive}},
            "single:negative" + suffix: {"class_type": "CLIPTextEncode", "inputs": {"text": negative}},
            "model" + suffix: {"class_type": "CheckpointLoaderSimple", "inputs": {"ckpt_name": "bird"}}}


def test_comfyui_connected_graph_ignores_decoys_and_preserves_colon_ids(tmp_path):
    data = graph()
    data["unused"] = {"class_type": "CLIPTextEncode", "inputs": {"text": "must not appear"}}
    parsed = assert_prompt(tmp_path, image_bytes({"prompt": data}), format_contains="ComfyUI")
    assert parsed["parameters"]["seed"] == "0"
    assert parsed["parameters"]["model"] == "bird"
    assert parsed["parameters"]["actual_image_size"] == "32x24"
    assert "size" not in parsed["parameters"]


def test_comfyui_multiple_outputs_require_selection_with_matching_params(tmp_path):
    data = {**graph(seed=10), **graph("a red bird", "noise", 20, "b")}
    result = PromptImageStore(tmp_path).add(image_bytes({"prompt": data}))["metadata"]
    assert result["source"] == "metadata"
    assert result["text"] == result["negative"] == ""
    assert "seed" not in result["parameters"]
    assert {(c["text"], c["negative"], c["parameters"]["seed"]) for c in result["candidates"]} == {
        ("a blue bird", "blur", "10"), ("a red bird", "noise", "20")}


def test_comfyui_sdxl_clip_prompts_remain_labeled(tmp_path):
    data = graph()
    data["single:positive"] = {"class_type": "CLIPTextEncodeSDXL", "inputs": {"text_g": "global bird", "text_l": "local bird"}}
    data["single:negative"] = {"class_type": "CLIPTextEncodeSDXL", "inputs": {"text_g": "global blur", "text_l": "local blur"}}
    result = PromptImageStore(tmp_path).add(image_bytes({"prompt": data}))["metadata"]
    assert result["text"] == ""
    assert {(c["text"], c["negative"]) for c in result["candidates"]} == {("global bird", "global blur"), ("local bird", "local blur")}
    assert all("CLIP" in c["label"] for c in result["candidates"])


def test_comfyui_sdxl_refiner_preserves_its_prompt_pair_and_label(tmp_path):
    data = graph()
    data["single:positive"] = {"class_type": "CLIPTextEncodeSDXLRefiner", "inputs": {
        "text": "a refined blue bird", "ascore": 6, "width": 32, "height": 24}}
    data["single:negative"] = {"class_type": "CLIPTextEncodeSDXLRefiner", "inputs": {
        "text": "refiner blur", "ascore": 2.5, "width": 32, "height": 24}}
    parsed = assert_prompt(tmp_path, image_bytes({"prompt": data}), expected="a refined blue bird", negative="refiner blur")
    assert "Refiner" in parsed["candidates"][0]["label"]
    assert parsed["parameters"]["seed"] == "0"


def test_comfyui_sampler_custom_advanced_guider_and_linked_seed(tmp_path):
    data = graph()
    data["sampler"] = {"class_type": "SamplerCustomAdvanced", "inputs": {"guider": ["guider", 0], "noise": ["noise", 0],
                         "sampler": ["algorithm", 0], "sigmas": ["schedule", 0]}}
    data["guider"] = {"class_type": "CFGGuider", "inputs": {"positive": ["single:positive", 0], "negative": ["single:negative", 0], "cfg": 7}}
    data["noise"] = {"class_type": "RandomNoise", "inputs": {"noise_seed": ["seed", 0]}}
    data["seed"] = {"class_type": "CR Seed", "inputs": {"seed": 18446744073709551615}}
    data["algorithm"] = {"class_type": "KSamplerSelect", "inputs": {"sampler_name": "euler"}}
    data["schedule"] = {"class_type": "BasicScheduler", "inputs": {"steps": 25, "scheduler": "normal"}}
    parsed = assert_prompt(tmp_path, image_bytes({"prompt": data}))
    assert parsed["parameters"]["seed"] == "18446744073709551615"
    assert parsed["parameters"]["steps"] == 25


def test_comfyui_parameter_generator_resolves_each_target_field(tmp_path):
    data = graph()
    data["parameters"] = {"class_type": "SDParameterGenerator", "inputs": {
        "seed": 17, "steps": 30, "cfg": 7, "sampler_name": "euler"}}
    for key in ("seed", "steps", "cfg", "sampler_name"):
        data["sampler"]["inputs"][key] = ["parameters", 0]
    parsed = assert_prompt(tmp_path, image_bytes({"prompt": data}))
    assert {key: parsed["parameters"][key] for key in ("seed", "steps", "cfg", "sampler")} == {
        "seed": "17", "steps": 30, "cfg": 7, "sampler": "euler"}


@pytest.mark.parametrize("single", [False, True])
def test_native_anima_has_priority_and_preserves_pipeline_details(tmp_path, single):
    native = {"resolved_prompt_full": "a blue bird", "sample_seed": 0, "prompt_seed": 18446744073709551615,
              "settings": {"negative_prompt": "blur", "unet_name": "anima", "steps": 20}}
    if single:
        native["single_runtime"] = {"positive": "a blue bird", "negative": "runtime blur", "effective_positive": {"primary": "trigger, a blue bird", "secondary": "a blue bird"}}
    parsed = assert_prompt(tmp_path, image_bytes({"anima_random_webui": native, "prompt": graph("wrong prompt")}),
                           negative="runtime blur" if single else "blur", format_contains="Anima")
    assert parsed["parameters"]["seed"] == "0"
    assert parsed["parameters"]["prompt_seed"] == "18446744073709551615"
    if single:
        assert parsed["parameters"]["pipeline_prompts"]["primary"] == "trigger, a blue bird"


def test_native_anima_tools_multiple_cached_prompt_records(tmp_path):
    info = {"anima_prompt": {"42": {"positive": "a blue bird", "negative": "blur"}, "43": {"positive": "a red bird"}}}
    result = PromptImageStore(tmp_path).add(image_bytes(info))["metadata"]
    assert result["source"] == "metadata"
    assert len(result["candidates"]) == 2
    assert result["text"] == ""


@pytest.mark.parametrize("active_secondary", [False, True])
def test_native_anima_offers_only_distinct_active_effective_pipeline_prompts(tmp_path, active_secondary):
    import copy
    from anima_webui.workflow import DEFAULT_SETTINGS

    settings = copy.deepcopy(DEFAULT_SETTINGS)
    settings["workbench"] = "single"
    settings["base_mode"] = "normal"
    settings["single"]["normal_pipeline"] = "secondary" if active_secondary else "primary"
    native = {"resolved_prompt_full": "base bird", "sample_seed": 12, "settings": settings,
              "single_runtime": {"positive": "base bird", "negative": "blur", "effective_positive": {
                  "primary": "base bird, main trigger", "secondary": "base bird, second trigger"}}}
    parsed = PromptImageStore(tmp_path).add(image_bytes({"anima_random_webui": native}))["metadata"]
    assert parsed["source"] == "metadata"
    assert parsed["text"] == ""
    assert {item["text"] for item in parsed["candidates"]} == {
        "base bird", "base bird, second trigger" if active_secondary else "base bird, main trigger"}


def test_native_anima_dual_active_pipelines_keep_frozen_runtime_prompts(tmp_path):
    import copy
    from anima_webui.workflow import DEFAULT_SETTINGS

    settings = copy.deepcopy(DEFAULT_SETTINGS)
    settings["workbench"] = "single"
    settings["base_mode"] = "dual_sampler"
    settings["single"]["dual"].update(first_pipeline="primary", second_pipeline="secondary")
    settings["single"]["positive_prompt"] = "stale editor draft"
    native = {"resolved_prompt_full": "frozen base", "frozen_positive_prompt": "stale requested override", "settings": settings,
              "single_runtime": {"positive": "frozen base", "negative": "", "effective_positive": {
                  "primary": "frozen base, actual primary", "secondary": "frozen base, actual secondary"}}}
    parsed = PromptImageStore(tmp_path).add(image_bytes({"anima_random_webui": native}))["metadata"]
    assert {item["text"] for item in parsed["candidates"]} == {
        "frozen base", "frozen base, actual primary", "frozen base, actual secondary"}
    assert all(item["negative"] == "" for item in parsed["candidates"])


@pytest.mark.parametrize("info", [{}, {"parameters": ""}, {"prompt": "not json"}, {"invokeai_metadata": "[]"},
                                  {"parameters": "Steps: 30, Seed: 0"}])
def test_valid_image_without_usable_positive_requests_ai(tmp_path, info):
    metadata = PromptImageStore(tmp_path).add(image_bytes(info))["metadata"]
    assert metadata["source"] == "none"
    assert not metadata["text"]
    assert not metadata["candidates"]
    if info.get("parameters", "").startswith("Steps:"):
        assert metadata["parameters"]["seed"] == "0"


def test_malformed_native_metadata_can_use_generic_format(tmp_path):
    assert_prompt(tmp_path, image_bytes({"anima_random_webui": "{broken", "parameters": "a blue bird\nNegative prompt: blur"}))


def test_tiny_rgba_truncated_stealth_and_decompression_bomb_terminate(tmp_path):
    store = PromptImageStore(tmp_path)
    with Image.new("RGBA", (1, 1)) as image:
        assert store.add(image_bytes(image=image))["metadata"]["source"] == "none"
    with stealth_image({"Description": "bird"}, declared_bits=2**32 - 8) as image:
        assert store.add(image_bytes(image=image))["metadata"]["source"] == "none"
    with stealth_image({"Description": "a" * (MAX_METADATA_BYTES + 1)}) as image:
        assert store.add(image_bytes(image=image))["metadata"]["source"] == "none"


def test_graph_cycles_and_limits_finish_without_guessing_prompts(tmp_path):
    data = graph()
    data["single:positive"] = {"class_type": "CLIPTextEncode", "inputs": {"text": ["single:positive", 0]}}
    assert PromptImageStore(tmp_path).add(image_bytes({"prompt": data}))["metadata"]["source"] == "none"
    with pytest.raises(ValueError):
        parse_comfyui({str(i): {} for i in range(10001)})


def test_conditioning_intermediate_concatenation_is_bounded(monkeypatch):
    from anima_webui._vendor.sd_prompt_reader import comfyui

    data = graph()
    for prefix, content in (("p", "a" * 1000), ("q", "b" * 1000)):
        data[prefix + "0"] = {"class_type": "CLIPTextEncode", "inputs": {"text": content}}
    for index in range(1, 11):
        for prefix, first, second in (("p", "p", "q"), ("q", "q", "p")):
            data[f"{prefix}{index}"] = {"class_type": "ConditioningCombine", "inputs": {
                "conditioning_1": [f"{first}{index - 1}", 0], "conditioning_2": [f"{second}{index - 1}", 0]}}
    data["sampler"]["inputs"]["positive"] = ["p10", 0]
    # Oversized strings must be stopped inside conditioning traversal, before
    # reaching final candidate construction (which has its own length check).
    def should_not_reach_candidate(*args, **kwargs):
        raise AssertionError("oversized intermediate reached candidate")
    monkeypatch.setattr(comfyui, "candidate", should_not_reach_candidate)
    parsed = comfyui.parse_comfyui(data)
    assert not parsed["candidates"]
    assert parsed["warnings"]


def test_text_limit_and_xml_entities_are_rejected_without_losing_pixels(tmp_path):
    assert PromptImageStore(tmp_path).add(image_bytes({"parameters": "x" * 100001}))["metadata"]["source"] == "none"
    with pytest.raises(ValueError):
        draw_things('<!DOCTYPE x [<!ENTITY a "bad">]><x>&a;</x>')
    with pytest.raises(ValueError):
        draw_things('<!DOCTYPE x [<!ENTITY a "bad">]><x>&a;</x>'.encode("utf-16"))


def test_exif_ascii_jis_unicode_and_swarm_endianness():
    for raw in (b"ASCII\0\0\0bird", b"UNICODE\0" + "bird".encode("utf-16-be"), b"UNICODE\0" + "bird".encode("utf-16-le")):
        assert decode_user_comment(raw) == "bird"
    assert decode_user_comment(b"JIS\0\0\0\0\0" + "鳥".encode("shift_jis")) == "鳥"


def test_original_hash_separates_identical_pixels_and_sanitized_preview(tmp_path):
    store = PromptImageStore(tmp_path)
    first_body = image_bytes({"parameters": "first prompt"})
    second_body = image_bytes({"parameters": "second prompt"})
    first = store.add(first_body, filename="../../bird.png")
    second = store.add(second_body)
    assert first["id"] == hashlib.sha256(first_body).hexdigest()
    assert first["id"] != second["id"]
    assert first["filename"] == "bird.png"
    assert store.get(first["id"])["metadata"]["text"] == "first prompt"
    assert store.image_path(first["id"]).read_bytes() == store.image_path(second["id"]).read_bytes()
    with Image.open(store.image_path(first["id"])) as image:
        assert image.mode == "RGB"
        assert not image.info


def test_bad_pixels_unsupported_animation_and_invalid_id_reject(tmp_path):
    store = PromptImageStore(tmp_path)
    with pytest.raises(WorkflowError):
        store.add(b"not an image")
    with pytest.raises(WorkflowError):
        store.add(image_bytes(format="GIF"))
    with pytest.raises(KeyError):
        store.get("../invalid")
    stream = io.BytesIO()
    with Image.new("RGB", (16, 16), "red") as first, Image.new("RGB", (16, 16), "blue") as second:
        first.save(stream, format="WEBP", save_all=True, append_images=[second])
    with pytest.raises(WorkflowError, match="单帧"):
        store.add(stream.getvalue())


def test_corrupt_saved_record_is_actionable_and_non_destructive(tmp_path):
    store = PromptImageStore(tmp_path)
    asset = store.add(image_bytes({"parameters": "bird"}))
    record = tmp_path / f'{asset["id"]}.json'
    pixels = store.image_path(asset["id"]).read_bytes()
    record.write_bytes(b"{broken")
    with pytest.raises(WorkflowError, match="重新上传"):
        store.get(asset["id"])
    assert record.read_bytes() == b"{broken"
    assert (tmp_path / f'{asset["id"]}.png').read_bytes() == pixels


@pytest.mark.parametrize("after_pixels", [False, True])
def test_invalid_compressed_png_metadata_does_not_prevent_ai_fallback(tmp_path, after_pixels, monkeypatch):
    body = image_bytes()
    payload = b"parameters\0\0" + zlib.compress(b"x" * (MAX_METADATA_BYTES + 1))
    chunk = struct.pack(">I", len(payload)) + b"zTXt" + payload + struct.pack(">I", zlib.crc32(b"zTXt" + payload))
    # Metadata is legal both before and after IDAT; late text is only decoded
    # by Image.load(), so both paths must retain usable pixels for AI fallback.
    offset = len(body) - 12 if after_pixels else 33
    body = body[:offset] + chunk + body[offset:]
    from anima_webui import prompt_images
    real_open = Image.open
    def preflighted_open(source, *args, **kwargs):
        # The oversized zTXt never reaches Pillow's initial metadata decoder.
        assert chunk not in source.getvalue()
        return real_open(source, *args, **kwargs)
    monkeypatch.setattr(prompt_images.Image, "open", preflighted_open)
    asset = PromptImageStore(tmp_path).add(body)
    assert asset["metadata"]["source"] == "none"
    assert asset["metadata"]["warnings"]


def test_vendor_attribution_and_package_configuration():
    import tomllib
    from setuptools import find_packages

    root = Path(__file__).resolve().parents[1]
    data = tomllib.loads((root / "pyproject.toml").read_text(encoding="utf-8"))
    packages = find_packages(str(root), include=data["tool"]["setuptools"]["packages"]["find"]["include"])
    assert "anima_webui._vendor.sd_prompt_reader" in packages
    vendor = root / "anima_webui" / "_vendor" / "sd_prompt_reader"
    assert "Copyright (c) 2023 Rhys Yang" in (vendor / "LICENSE").read_text()
    assert UPSTREAM_COMMIT in (vendor / "UPSTREAM.txt").read_text()
    assert data["tool"]["setuptools"]["package-data"]["anima_webui._vendor.sd_prompt_reader"] == ["LICENSE", "UPSTREAM.txt"]
