"""Owned, durable source images for the single-image workbench."""
from __future__ import annotations

import hashlib
import io
import json
import os
import re
import tempfile
from pathlib import Path
from typing import Any

from PIL import Image, ImageOps, UnidentifiedImageError

from .workflow import WorkflowError

MAX_IMAGE_BYTES = 20 * 1024 * 1024
MAX_IMAGE_PIXELS = 40_000_000
_ASSET_ID = re.compile(r"^[a-f0-9]{64}$")


class InputImageStore:
    def __init__(self, directory: str | Path):
        self.path = Path(directory)
        self.path.mkdir(parents=True, exist_ok=True)

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
        try:
            with Image.open(io.BytesIO(body)) as original:
                if original.format not in {"PNG", "JPEG", "WEBP"}:
                    raise WorkflowError("输入图片仅支持 PNG、JPEG、WebP")
                if original.width * original.height > MAX_IMAGE_PIXELS:
                    raise WorkflowError("输入图片不能超过 4000 万像素")
                if getattr(original, "n_frames", 1) != 1:
                    raise WorkflowError("请输入单帧图片")
                original.load()
                oriented = ImageOps.exif_transpose(original)
                rgba = oriented.convert("RGBA")
                image = Image.new("RGB", rgba.size, "white")
                image.paste(rgba, mask=rgba.getchannel("A"))
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
            "width": image.width, "height": image.height, "content_type": "image/png",
            "size": len(pixels), "source_image_id": source_image_id,
            "image_url": f"/api/input-images/{asset_id}/image",
        }
        self._write(self._path(asset_id, ".png"), pixels)
        self._write(self._path(asset_id, ".json"), json.dumps(metadata, ensure_ascii=False).encode("utf-8"))
        return metadata

    def get(self, asset_id: str) -> dict[str, Any]:
        path = self._path(asset_id, ".json")
        if not path.is_file() or not self._path(asset_id, ".png").is_file():
            raise KeyError(asset_id)
        return json.loads(path.read_text(encoding="utf-8"))

    def image_path(self, asset_id: str) -> Path:
        self.get(asset_id)
        return self._path(asset_id, ".png")

    def prepare(self, asset_id: str, width: int, height: int, fit: str) -> bytes:
        if fit not in {"crop", "stretch", "pad"}:
            raise WorkflowError("输入图片适配方式必须为 crop、stretch 或 pad")
        with Image.open(self.image_path(asset_id)) as original:
            size = (width, height)
            if fit == "crop":
                result = ImageOps.fit(original, size, method=Image.Resampling.LANCZOS)
            elif fit == "pad":
                result = ImageOps.pad(original, size, method=Image.Resampling.LANCZOS, color="white")
            else:
                result = original.resize(size, Image.Resampling.LANCZOS)
            stream = io.BytesIO()
            result.save(stream, format="PNG")
            return stream.getvalue()
