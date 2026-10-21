"""Bounded read-only format adapters derived from SD Prompt Reader 1.3.5.

Copyright (c) 2023 Rhys Yang. MIT; see LICENSE and UPSTREAM.txt.
"""
from __future__ import annotations

import json
import math
import re
import xml.etree.ElementTree as ET
import zlib
from typing import Any

from PIL import Image

MAX_METADATA_BYTES = 4 * 1024 * 1024
MAX_TEXT_LENGTH = 100_000
MAX_GRAPH_NODES = 10_000
MAX_GRAPH_DEPTH = 128


class MetadataError(ValueError):
    pass


def text(value: Any) -> str:
    if not isinstance(value, str):
        return ""
    if len(value) > MAX_TEXT_LENGTH:
        raise MetadataError("提示词超过 100000 字符")
    return value.strip()


def join_text(values: Any, separator: str = "\n") -> str:
    """Check intermediate lengths before allocating combined prompt strings."""
    parts = []
    length = 0
    for value in values:
        part = text(value)
        if not part:
            continue
        length += len(part) + (len(separator) if parts else 0)
        if length > MAX_TEXT_LENGTH:
            raise MetadataError("组合提示词超过 100000 字符")
        parts.append(part)
    return separator.join(parts)


def object_json(value: Any) -> dict[str, Any]:
    if isinstance(value, dict):
        return value
    if not isinstance(value, (str, bytes)) or len(value) > MAX_METADATA_BYTES:
        raise MetadataError("元数据大小或类型无效")
    data = json.loads(value)
    if not isinstance(data, dict):
        raise MetadataError("元数据必须是对象")
    return data


def safe_parameters(value: Any, *, depth: int = 0, key: str = "") -> Any:
    """Produce bounded JSON data; preserve large seeds across browser JSON."""
    if depth > 16:
        return None
    if isinstance(value, str):
        return value[:MAX_TEXT_LENGTH]
    if value is None or isinstance(value, bool):
        return value
    if isinstance(value, (int, float)):
        if isinstance(value, float) and not math.isfinite(value):
            return None
        return str(value) if "seed" in key.lower() or isinstance(value, int) and abs(value) > 2**53 - 1 else value
    if isinstance(value, dict):
        return {str(k)[:200]: safe_parameters(v, depth=depth + 1, key=str(k))
                for k, v in list(value.items())[:1000] if isinstance(k, (str, int))}
    if isinstance(value, (list, tuple)):
        return [safe_parameters(v, depth=depth + 1, key=key) for v in value[:1000]]
    return None


def parameters(data: dict[str, Any], aliases: dict[str, tuple[str, ...]] | None = None,
               exclude: tuple[str, ...] = ()) -> dict[str, Any]:
    result = {k: v for k, v in data.items() if k not in exclude and v is not None}
    mapping = {
        "model": ("Model", "model", "base_model", "model_weights", "use_stable_diffusion_model", "Stable Diffusion model"),
        "sampler": ("Sampler", "sampler", "sampler_name", "comfyuisampler", "autowebuisampler", "scheduler"),
        "seed": ("Seed", "seed", "noise_seed"),
        "steps": ("Steps", "steps", "num_inference_steps"),
        "cfg": ("CFG scale", "cfg", "cfg_scale", "cfgscale", "guidance_scale", "Guidance Scale", "scale"),
        "width": ("width", "Width"), "height": ("height", "Height"),
        "size": ("Size", "size"),
    }
    mapping.update(aliases or {})
    for target, keys in mapping.items():
        for name in keys:
            if name in data and data[name] is not None:
                result[target] = data[name]
                break
    if "size" not in result and result.get("width") is not None and result.get("height") is not None:
        result["size"] = f'{result["width"]}x{result["height"]}'
    if isinstance(result.get("model"), dict):
        result["model"] = result["model"].get("model_name", result["model"].get("name", result["model"]))
    return safe_parameters(result)


def candidate(identity: str, label: str, positive: Any, negative: Any = "",
              values: dict[str, Any] | None = None) -> dict[str, Any] | None:
    positive = text(positive)
    if not positive:
        return None
    return {"id": identity, "label": label, "text": positive, "negative": text(negative),
            "parameters": safe_parameters(values or {})}


def result(label: str, positive: Any = "", negative: Any = "",
           values: dict[str, Any] | None = None) -> dict[str, Any]:
    entry = candidate("metadata:0", label, positive, negative, values)
    return {"format": label, "candidates": [entry] if entry else [],
            "parameters": safe_parameters(values or {}), "warnings": []}


def a1111(raw: str, extra: str = "") -> dict[str, Any]:
    if len(raw.encode("utf-8")) > MAX_METADATA_BYTES:
        raise MetadataError("元数据过大")
    # The settings record starts at a line boundary, never at prompt text
    # containing an incidental 'Steps:' phrase.
    match = re.search(r"(?:^|\r?\n)Steps:\s*\d+", raw)
    prompt, setting = (raw[:match.start()], raw[match.start():].strip()) if match else (raw, "")
    negative_match = re.search(r"(?:^|\r?\n)Negative prompt:\s*", prompt)
    positive, negative = (prompt[:negative_match.start()], prompt[negative_match.end():]) if negative_match else (prompt, "")
    values: dict[str, Any] = {}
    # Only split at a following key, preserving commas inside quoted values.
    for entry in re.finditer(r'(?:^|,\s*)([^:,\n]+):\s*("(?:[^"\\]|\\.)*"|.*?)(?=,\s*[^:,\n]+:|$)', setting):
        key, value = entry.groups()
        values[key.strip()] = value.strip().strip('"')
    if extra:
        values["postprocessing"] = extra
    return result("A1111", positive, negative, parameters(values))


def easy_diffusion(data: dict[str, Any]) -> dict[str, Any]:
    positive = data.get("prompt", data.get("Prompt", ""))
    negative = data.get("negative_prompt", data.get("Negative Prompt", ""))
    return result("Easy Diffusion", positive, negative,
                  parameters(data, exclude=("prompt", "Prompt", "negative_prompt", "Negative Prompt")))


def swarm(data: dict[str, Any]) -> dict[str, Any]:
    values = object_json(data.get("sui_image_params"))
    return result("StableSwarmUI", values.get("prompt"), values.get("negativeprompt", ""),
                  parameters(values, exclude=("prompt", "negativeprompt")))


def fooocus(data: dict[str, Any]) -> dict[str, Any]:
    return result("Fooocus-MRE", data.get("prompt"), data.get("negative_prompt", ""),
                  parameters(data, exclude=("prompt", "negative_prompt")))


def novelai(data: dict[str, Any], *, stealth: bool = False) -> dict[str, Any]:
    comment = object_json(data.get("Comment", {}))
    values = {**data, **comment}
    return result("NovelAI (stealth)" if stealth else "NovelAI / Naifu", values.get("prompt", values.get("Description", "")),
                  values.get("uc", ""), parameters(values, exclude=("prompt", "Description", "uc", "Comment")))


def invokeai(info: dict[str, Any]) -> dict[str, Any]:
    if "invokeai_metadata" in info:
        values = object_json(info["invokeai_metadata"])
        return result("InvokeAI", values.get("positive_prompt"), values.get("negative_prompt", ""),
                      parameters(values, exclude=("positive_prompt", "negative_prompt")))
    if "sd-metadata" in info:
        values = object_json(info["sd-metadata"])
        image = object_json(values.get("image", {}))
        prompt = image.get("prompt", "")
        if isinstance(prompt, list):
            prompt = join_text((item.get("prompt") for item in prompt if isinstance(item, dict)), ", ")
        positive, negative = split_invoke_prompt(text(prompt))
        return result("InvokeAI (sd-metadata)", positive, negative,
                      parameters({**{k: v for k, v in values.items() if k != "image"}, **image}, exclude=("prompt",)))
    raw = text(info.get("Dream"))
    match = re.match(r'^"(.*?)"\s*(.*)$', raw, re.S)
    if not match:
        raise MetadataError("InvokeAI Dream 格式无效")
    positive, negative = split_invoke_prompt(match[1])
    options = dict(re.findall(r"-(\w+)\s+([\w.\-]+)", match[2]))
    values = {name: options[key] for key, name in (("s", "steps"), ("S", "seed"), ("W", "width"),
              ("H", "height"), ("C", "cfg"), ("A", "sampler")) if key in options}
    return result("InvokeAI (Dream)", positive, negative, parameters(values))


def split_invoke_prompt(value: str) -> tuple[str, str]:
    match = re.match(r"^(.*?)\[(.*?)\]$", value, re.S)
    return (match[1].strip(), match[2].strip()) if match else (value.strip(), "")


def draw_things(raw: str | bytes) -> dict[str, Any]:
    body = raw.encode("utf-8") if isinstance(raw, str) else raw
    if len(body) > MAX_METADATA_BYTES or re.search(br"<!\s*(DOCTYPE|ENTITY)\b", body.replace(b"\x00", b""), re.I):
        raise MetadataError("XMP 元数据超过限制或包含不支持的声明")
    root = ET.fromstring(body)
    for element in root.iter():
        if element.tag.rsplit("}", 1)[-1].split(":")[-1] != "UserComment":
            continue
        content = "".join(element.itertext()).strip()
        values = object_json(content)
        return result("Draw Things", values.get("c"), values.get("uc", ""),
                      parameters(values, exclude=("c", "uc")))
    raise MetadataError("XMP 中没有提示词")


def decode_user_comment(value: Any) -> str:
    if isinstance(value, str):
        return value
    if not isinstance(value, bytes) or len(value) > MAX_METADATA_BYTES:
        return ""
    prefix, payload = value[:8], value[8:]
    if prefix == b"ASCII\x00\x00\x00":
        return payload.decode("ascii").rstrip("\x00")
    if prefix == b"JIS\x00\x00\x00\x00\x00":
        return payload.decode("shift_jis").rstrip("\x00")
    if prefix == b"UNICODE\x00":
        if payload.startswith((b"\xff\xfe", b"\xfe\xff")):
            return payload.decode("utf-16").rstrip("\x00")
        # EXIF requires big endian; Swarm's historical writer also emitted
        # little endian without a BOM. Detect the ASCII JSON prefix reliably.
        encoding = "utf-16-le" if len(payload) >= 2 and payload[0] != 0 and payload[1] == 0 else "utf-16-be"
        return payload.decode(encoding).rstrip("\x00")
    return value.decode("utf-8").rstrip("\x00")


def exif_records(image: Image.Image) -> tuple[Any, str]:
    exif = image.getexif()
    model = exif.get(0x0110)
    comment = exif.get(0x9286)
    if not comment and 0x8769 in exif:
        comment = exif.get_ifd(0x8769).get(0x9286)
    return model, decode_user_comment(comment)


def stealth_metadata(image: Image.Image) -> dict[str, Any] | None:
    """NovelAI alpha-channel LSB stream, column-major, with strict capacity."""
    if image.mode != "RGBA":
        return None
    capacity = image.width * image.height
    magic = b"stealth_pngcomp"
    if capacity < len(magic) * 8 + 32:
        return None
    with image.getchannel("A") as alpha:
        pixels = alpha.load()
        position = 0

        def read(count: int) -> bytes:
            nonlocal position
            if count < 0 or count > MAX_METADATA_BYTES or position + count * 8 > capacity:
                raise MetadataError("隐写元数据长度无效")
            output = bytearray(count)
            for index in range(count):
                value = 0
                for _ in range(8):
                    value = (value << 1) | (pixels[position // image.height, position % image.height] & 1)
                    position += 1
                output[index] = value
            return bytes(output)

        if read(len(magic)) != magic:
            return None
        bits = int.from_bytes(read(4), "big")
        if bits % 8:
            raise MetadataError("隐写元数据位数无效")
        compressed = read(bits // 8)
    decoder = zlib.decompressobj(16 + zlib.MAX_WBITS)
    body = decoder.decompress(compressed, MAX_METADATA_BYTES + 1)
    if len(body) > MAX_METADATA_BYTES or decoder.unconsumed_tail or not decoder.eof:
        raise MetadataError("隐写元数据解压超限或不完整")
    return object_json(body)


def parse_image(image: Image.Image) -> dict[str, Any]:
    """Try independent recognized containers; malformed metadata is nonfatal."""
    from .comfyui import parse_comfyui

    info = image.info
    attempts: list[tuple[str, Any]] = []
    warnings: list[str] = []

    def with_image_size(parsed: dict[str, Any]) -> dict[str, Any]:
        if parsed["format"] == "ComfyUI" or parsed["format"].startswith("NovelAI"):
            for values in [parsed["parameters"], *(item["parameters"] for item in parsed["candidates"])]:
                values.setdefault("actual_image_size", f"{image.width}x{image.height}")
        return parsed
    total = sum(len(v.encode("utf-8")) if isinstance(v, str) else len(v) if isinstance(v, bytes) else 0 for v in info.values())
    if total > MAX_METADATA_BYTES:
        return {"format": "", "candidates": [], "parameters": {}, "warnings": ["图片元数据超过 4 MiB，已跳过提取"]}
    if "parameters" in info:
        raw = info["parameters"]
        attempts.append(("parameters", lambda: swarm(object_json(raw)) if "sui_image_params" in raw else a1111(raw, info.get("postprocessing", ""))))
    if "postprocessing" in info and "parameters" not in info:
        attempts.append(("A1111", lambda: a1111("", info["postprocessing"])))
    if "negative_prompt" in info or "Negative Prompt" in info or "Prompt" in info:
        attempts.append(("Easy Diffusion", lambda: easy_diffusion(info)))
    if any(k in info for k in ("invokeai_metadata", "sd-metadata", "Dream")):
        attempts.append(("InvokeAI", lambda: invokeai(info)))
    if info.get("Software") == "NovelAI" or "Description" in info and "Comment" in info:
        attempts.append(("NovelAI", lambda: novelai(info)))
    if "prompt" in info:
        attempts.append(("ComfyUI", lambda: parse_comfyui(object_json(info["prompt"]))))
    if "Comment" in info:
        attempts.append(("Fooocus-MRE", lambda: fooocus(object_json(info["Comment"]))))
    if "comment" in info:
        attempts.append(("Fooocus-MRE", lambda: fooocus(object_json(info["comment"]))))
    if "XML:com.adobe.xmp" in info:
        attempts.append(("Draw Things", lambda: draw_things(info["XML:com.adobe.xmp"])))
    try:
        model, comment = exif_records(image)
        if model:
            attempts.append(("StableSwarmUI (legacy)", lambda: swarm(object_json(model))))
        if comment:
            def parse_comment() -> dict[str, Any]:
                if comment.lstrip().startswith("{"):
                    values = object_json(comment)
                    return swarm(values) if "sui_image_params" in values else easy_diffusion(values)
                return a1111(comment)
            attempts.append(("EXIF", parse_comment))
    except (ValueError, TypeError, KeyError, OSError, UnicodeError):
        warnings.append("EXIF 元数据无法解析")
    partial = result("")
    for label, parse in attempts:
        try:
            parsed = with_image_size(parse())
            if parsed["candidates"]:
                parsed["warnings"] = warnings + parsed.get("warnings", [])
                return parsed
            if parsed.get("parameters") and not partial["parameters"]:
                partial = parsed
        except (ValueError, TypeError, KeyError, AttributeError, IndexError, RecursionError, ET.ParseError):
            warnings.append(f"{label} 元数据未能完整解析")
    try:
        hidden = stealth_metadata(image)
        if hidden:
            parsed = with_image_size(novelai(hidden, stealth=True))
            if parsed["candidates"]:
                parsed["warnings"] = warnings
                return parsed
            partial = parsed
    except (ValueError, TypeError, KeyError, OSError, zlib.error, RecursionError):
        warnings.append("NovelAI 隐写元数据未能完整解析")
    partial["warnings"] = warnings
    return partial
