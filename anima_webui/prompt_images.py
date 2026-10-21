"""Owned reverse-prompt images, identified by their original uploaded bytes."""
from __future__ import annotations

import hashlib
import io
import json
import os
import re
import tempfile
import threading
import warnings
import zlib
from pathlib import Path
from typing import Any

from PIL import Image, ImageOps, UnidentifiedImageError

from .prompt_metadata import empty_metadata, extract_metadata
from ._vendor.sd_prompt_reader.formats import MAX_METADATA_BYTES, MAX_TEXT_LENGTH
from .workflow import WorkflowError

MAX_IMAGE_BYTES = 20 * 1024 * 1024
MAX_IMAGE_PIXELS = 40_000_000
MAX_RECORD_BYTES = MAX_METADATA_BYTES + 64 * 1024
_ASSET_ID = re.compile(r"^[a-f0-9]{64}$")


def _unsafe_png_metadata(body: bytes) -> bool:
    """Bound compressed ancillary data before Pillow allocates its text store."""
    if not body.startswith(b"\x89PNG\r\n\x1a\n"):
        return False
    offset, used = 8, 0
    while offset + 12 <= len(body):
        length = int.from_bytes(body[offset:offset + 4], "big")
        end = offset + 12 + length
        if end > len(body):
            return False  # The pixel decoder reports truncated image data.
        kind, payload = body[offset + 4:offset + 8], body[offset + 8:end - 4]
        offset = end
        if kind not in {b"tEXt", b"zTXt", b"iTXt", b"eXIf", b"iCCP"}:
            continue
        compressed = False
        try:
            if kind in {b"zTXt", b"iCCP"}:
                name, separator, payload = payload.partition(b"\x00")
                used += len(name) + 2
                if not separator or not payload or payload[0] != 0:
                    return True
                payload, compressed = payload[1:], True
            elif kind == b"iTXt":
                name, separator, payload = payload.partition(b"\x00")
                used += len(name) + 3
                if not separator or len(payload) < 2 or payload[0] not in {0, 1} or payload[1] != 0:
                    return True
                compressed, payload = bool(payload[0]), payload[2:]
                for _ in range(2):  # language tag and translated keyword
                    field, separator, payload = payload.partition(b"\x00")
                    if not separator:
                        return True
                    used += len(field) + 1
            if used > MAX_METADATA_BYTES:
                return True
            if compressed:
                decoder = zlib.decompressobj()
                payload = decoder.decompress(payload, MAX_METADATA_BYTES - used + 1)
                if decoder.unconsumed_tail or not decoder.eof:
                    return True
            used += len(payload)
            if used > MAX_METADATA_BYTES:
                return True
        except (ValueError, zlib.error):
            return True
    return False


def _png_without_metadata(body: bytes) -> bytes:
    """Allow valid pixels with malformed text metadata to reach AI fallback.

    Preserve color/transparency and all critical chunks. No decompression or
    interpretation of untrusted text chunks is needed for this retry.
    """
    if not body.startswith(b"\x89PNG\r\n\x1a\n"):
        return body
    output = bytearray(body[:8])
    offset = 8
    while offset + 12 <= len(body):
        length = int.from_bytes(body[offset:offset + 4], "big")
        end = offset + 12 + length
        if end > len(body):
            return body
        kind = body[offset + 4:offset + 8]
        if kind not in {b"tEXt", b"zTXt", b"iTXt", b"eXIf", b"iCCP"}:
            output.extend(body[offset:end])
        offset = end
        if kind == b"IEND":
            return bytes(output)
    return body


class PromptImageStore:
    def __init__(self, directory: str | Path):
        self.path = Path(directory)
        self.path.mkdir(parents=True, exist_ok=True)
        self._processing_lock = threading.Lock()

    def _path(self, asset_id: str, suffix: str) -> Path:
        if not isinstance(asset_id, str) or not _ASSET_ID.fullmatch(asset_id):
            raise KeyError(asset_id)
        return self.path / f"{asset_id}{suffix}"

    @staticmethod
    def _write(path: Path, body: bytes) -> None:
        descriptor, temporary = tempfile.mkstemp(prefix=".prompt-upload-", dir=path.parent)
        try:
            with os.fdopen(descriptor, "wb") as stream:
                stream.write(body)
                stream.flush()
                os.fsync(stream.fileno())
            os.replace(temporary, path)
        finally:
            if os.path.exists(temporary):
                os.unlink(temporary)

    def add(self, body: bytes, *, filename: str = "image") -> dict[str, Any]:
        if not body or len(body) > MAX_IMAGE_BYTES:
            raise WorkflowError("反推图片必须非空且不超过 20 MiB")
        with self._processing_lock:
            return self._add(body, filename=filename)

    @staticmethod
    def _decode(body: bytes) -> Image.Image:
        original = Image.open(io.BytesIO(body))
        try:
            if original.format not in {"PNG", "JPEG", "WEBP"}:
                raise WorkflowError("反推图片仅支持 PNG、JPEG、WebP")
            if original.width * original.height > MAX_IMAGE_PIXELS:
                raise WorkflowError("反推图片不能超过 4000 万像素")
            if getattr(original, "n_frames", 1) != 1:
                raise WorkflowError("请输入单帧图片")
            original.load()
            return original
        except BaseException:
            original.close()
            raise

    def _add(self, body: bytes, *, filename: str) -> dict[str, Any]:
        fallback = _unsafe_png_metadata(body)
        decoded_body = _png_without_metadata(body) if fallback else body
        try:
            try:
                original = self._decode(decoded_body)
            except (ValueError, OSError, SyntaxError):
                cleaned = _png_without_metadata(body)
                if cleaned == body:
                    raise
                original = self._decode(cleaned)
                fallback = True
            with original:
                metadata = (empty_metadata(warning="图片文字元数据损坏或超限，可使用 AI 反推")
                            if fallback else extract_metadata(original))
                # Metadata has already been extracted from the untouched image.
                # Preview pixels are orientation-corrected and contain no EXIF,
                # generation prompt, alpha-channel hidden data, or other metadata.
                with warnings.catch_warnings():
                    warnings.simplefilter("ignore", UserWarning)
                    try:
                        oriented = ImageOps.exif_transpose(original)
                    except (ValueError, TypeError, KeyError, OSError, SyntaxError):
                        oriented = original.copy()
                with oriented:
                    with oriented.convert("RGBA") as rgba:
                        preview = Image.new("RGB", rgba.size, "white")
                        with preview, rgba.getchannel("A") as alpha:
                            preview.paste(rgba, mask=alpha)
                            width, height = preview.size
                            output = io.BytesIO()
                            preview.save(output, format="PNG")
        except WorkflowError:
            raise
        except (UnidentifiedImageError, OSError, ValueError, TypeError, SyntaxError, Image.DecompressionBombError) as error:
            raise WorkflowError("无法读取反推图片或图片数据已损坏") from error
        pixels = output.getvalue()
        asset_id = hashlib.sha256(body).hexdigest()
        asset = {"id": asset_id, "filename": Path(str(filename).replace("\\", "/")).name[:240],
                 "width": width, "height": height, "size": len(pixels), "content_type": "image/png",
                 "image_url": f"/api/prompt-images/{asset_id}/image", "metadata": metadata}
        record = json.dumps(asset, ensure_ascii=False, allow_nan=False).encode("utf-8")
        if len(record) > MAX_RECORD_BYTES:
            raise WorkflowError("反推图片元数据过大")
        self._write(self._path(asset_id, ".png"), pixels)
        self._write(self._path(asset_id, ".json"), record)
        return asset

    def get(self, asset_id: str) -> dict[str, Any]:
        record_path, image_path = self._path(asset_id, ".json"), self._path(asset_id, ".png")
        if not record_path.is_file() or not image_path.is_file():
            raise KeyError(asset_id)
        try:
            with record_path.open("rb") as stream:
                body = stream.read(MAX_RECORD_BYTES + 1)
            if len(body) > MAX_RECORD_BYTES:
                raise ValueError("record too large")
            asset = json.loads(body)
            if not isinstance(asset, dict) or asset.get("id") != asset_id:
                raise ValueError("invalid record")
            width, height = asset.get("width"), asset.get("height")
            if (type(width) is not int or type(height) is not int or width <= 0 or height <= 0
                    or width * height > MAX_IMAGE_PIXELS or asset.get("content_type") != "image/png"
                    or type(asset.get("size")) is not int or asset["size"] != image_path.stat().st_size
                    or not isinstance(asset.get("filename"), str)):
                raise ValueError("invalid image record")
            metadata = asset.get("metadata")
            if (not isinstance(metadata, dict) or metadata.get("source") not in {"metadata", "none"}
                    or not isinstance(metadata.get("parameters"), dict) or not isinstance(metadata.get("warnings"), list)
                    or not isinstance(metadata.get("candidates"), list)):
                raise ValueError("invalid prompt record")
            for key in ("format", "text", "negative"):
                if not isinstance(metadata.get(key), str) or len(metadata[key]) > MAX_TEXT_LENGTH:
                    raise ValueError("invalid prompt text")
            for entry in metadata["candidates"]:
                if (not isinstance(entry, dict) or not isinstance(entry.get("parameters"), dict)
                        or not all(isinstance(entry.get(key), str) and len(entry[key]) <= MAX_TEXT_LENGTH
                                   for key in ("id", "label", "text", "negative")) or not entry["text"].strip()):
                    raise ValueError("invalid prompt candidate")
            asset["image_url"] = f"/api/prompt-images/{asset_id}/image"
            return asset
        except (ValueError, OSError, UnicodeError, RecursionError) as error:
            raise WorkflowError("反推图片记录已损坏或无法读取，请重新上传图片") from error

    def image_path(self, asset_id: str) -> Path:
        self.get(asset_id)
        return self._path(asset_id, ".png")
