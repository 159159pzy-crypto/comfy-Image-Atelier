"""Owned, durable source images for the single-image workbench."""
from __future__ import annotations

import hashlib
import io
import json
import os
import re
import tempfile
import threading
from pathlib import Path
from typing import Any

from PIL import Image, ImageOps, UnidentifiedImageError

from .workflow import WorkflowError

MAX_IMAGE_BYTES = 20 * 1024 * 1024
MAX_IMAGE_PIXELS = 40_000_000
MAX_METADATA_BYTES = 64 * 1024
_ASSET_ID = re.compile(r"^[a-f0-9]{64}$")


class InputImageStore:
    def __init__(self, directory: str | Path):
        self.path = Path(directory)
        self.path.mkdir(parents=True, exist_ok=True)
        # Hold this inside the worker thread: cancelling an asyncio.to_thread
        # caller must not release the guard while Pillow is still decoding.
        # Upload and prepare share a guard to bound large decoded buffers.
        self._processing_lock = threading.Lock()

    def _path(self, asset_id: str, suffix: str) -> Path:
        if not isinstance(asset_id, str) or not _ASSET_ID.fullmatch(asset_id):
            raise KeyError(asset_id)
        return self.path / f"{asset_id}{suffix}"

    @staticmethod
    def _write(path: Path, body: bytes) -> None:
        descriptor, temporary = tempfile.mkstemp(prefix=".upload-", dir=path.parent)
        try:
            with os.fdopen(descriptor, "wb") as stream:
                stream.write(body)
                stream.flush()
                os.fsync(stream.fileno())
            os.replace(temporary, path)
        finally:
            if os.path.exists(temporary):
                os.unlink(temporary)

    def add(self, body: bytes, *, filename: str = "image", source_image_id: int | None = None) -> dict[str, Any]:
        if not body or len(body) > MAX_IMAGE_BYTES:
            raise WorkflowError("输入图片必须非空且不超过 20 MiB")
        with self._processing_lock:
            return self._add(body, filename=filename, source_image_id=source_image_id)

    def _add(self, body: bytes, *, filename: str, source_image_id: int | None) -> dict[str, Any]:
        try:
            with Image.open(io.BytesIO(body)) as original:
                if original.format not in {"PNG", "JPEG", "WEBP"}:
                    raise WorkflowError("输入图片仅支持 PNG、JPEG、WebP")
                if original.width * original.height > MAX_IMAGE_PIXELS:
                    raise WorkflowError("输入图片不能超过 4000 万像素")
                if getattr(original, "n_frames", 1) != 1:
                    raise WorkflowError("请输入单帧图片")
                original.load()
                with ImageOps.exif_transpose(original) as oriented:
                    if oriented.mode in {"RGBA", "LA", "PA"} or "transparency" in oriented.info:
                        with oriented.convert("RGBA") as rgba:
                            image = Image.new("RGB", rgba.size, "white")
                            with rgba.getchannel("A") as alpha:
                                image.paste(rgba, mask=alpha)
                    else:
                        image = oriented.convert("RGB")
                    with image:
                        width, height = image.size
                        encoded = io.BytesIO()
                        image.save(encoded, format="PNG")
        except WorkflowError:
            raise
        except (UnidentifiedImageError, OSError, ValueError, Image.DecompressionBombError) as error:
            raise WorkflowError("无法读取输入图片或图片数据已损坏") from error
        pixels = encoded.getvalue()
        asset_id = hashlib.sha256(pixels).hexdigest()
        metadata = {
            "id": asset_id, "filename": Path(filename.replace("\\", "/")).name[:240],
            "width": width, "height": height, "content_type": "image/png",
            "size": len(pixels), "source_image_id": source_image_id,
            "image_url": f"/api/input-images/{asset_id}/image",
        }
        self._write(self._path(asset_id, ".png"), pixels)
        self._write(self._path(asset_id, ".json"), json.dumps(metadata, ensure_ascii=False).encode("utf-8"))
        return metadata

    def get(self, asset_id: str) -> dict[str, Any]:
        path = self._path(asset_id, ".json")
        image_path = self._path(asset_id, ".png")
        if not path.is_file() or not image_path.is_file():
            raise KeyError(asset_id)
        try:
            with path.open("rb") as stream:
                body = stream.read(MAX_METADATA_BYTES + 1)
            if len(body) > MAX_METADATA_BYTES:
                raise ValueError("metadata is too large")
            metadata = json.loads(body.decode("utf-8"))
            if not isinstance(metadata, dict) or metadata.get("id") != asset_id:
                raise ValueError("invalid asset metadata")
            width, height = metadata.get("width"), metadata.get("height")
            if (
                type(width) is not int or type(height) is not int
                or width <= 0 or height <= 0 or width * height > MAX_IMAGE_PIXELS
                or metadata.get("content_type") != "image/png"
                or type(metadata.get("size")) is not int
                or metadata["size"] != image_path.stat().st_size
                or not isinstance(metadata.get("filename"), str)
            ):
                raise ValueError("invalid asset dimensions or content")
            metadata["image_url"] = f"/api/input-images/{asset_id}/image"
            return metadata
        except (OSError, ValueError, UnicodeError) as error:
            raise WorkflowError("输入图片记录已损坏或无法读取，请重新上传图片") from error

    def image_path(self, asset_id: str) -> Path:
        self.get(asset_id)
        return self._path(asset_id, ".png")

    def prepare(self, asset_id: str, width: int, height: int, fit: str) -> bytes:
        if fit not in {"crop", "stretch", "pad"}:
            raise WorkflowError("输入图片适配方式必须为 crop、stretch 或 pad")
        if type(width) is not int or type(height) is not int or not (1 <= width <= 4096 and 1 <= height <= 4096):
            raise WorkflowError("输入图片目标尺寸必须为 1-4096 范围内的整数")
        with self._processing_lock:
            try:
                with Image.open(self.image_path(asset_id)) as original:
                    if original.format != "PNG" or original.width * original.height > MAX_IMAGE_PIXELS:
                        raise ValueError("invalid stored image")
                    size = (width, height)
                    if fit == "crop":
                        result = ImageOps.fit(original, size, method=Image.Resampling.LANCZOS)
                    elif fit == "pad":
                        result = ImageOps.pad(original, size, method=Image.Resampling.LANCZOS, color="white")
                    else:
                        result = original.resize(size, Image.Resampling.LANCZOS)
                    with result:
                        stream = io.BytesIO()
                        result.save(stream, format="PNG")
                        return stream.getvalue()
            except (OSError, ValueError, Image.DecompressionBombError) as error:
                raise WorkflowError("输入图片已损坏或无法读取，请重新上传图片") from error
