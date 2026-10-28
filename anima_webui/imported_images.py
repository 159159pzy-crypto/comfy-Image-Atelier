"""Durable image access without retention settings or background deletion."""
from __future__ import annotations

import asyncio
import re
from pathlib import Path
from typing import Any

from .workflow import WorkflowError


ASSET_ID = re.compile(r"^[a-f0-9]{64}$")


class ImportedImageService:
    """Keep file workers serialized until their atomic writes have finished."""

    def __init__(self, history: Any, input_images: Any, prompt_images: Any, comfy: Any):
        self.history = history
        self.stores = {"input": input_images, "prompt": prompt_images}
        self.comfy = comfy
        self._lock = asyncio.Lock()
        self._workers: set[asyncio.Task] = set()
        self._closed = False

    def _store(self, kind: str, asset_id: str | None = None) -> Any:
        if self._closed:
            raise WorkflowError("图片服务正在关闭")
        if kind not in self.stores or asset_id is not None and (
            not isinstance(asset_id, str) or not ASSET_ID.fullmatch(asset_id)
        ):
            raise WorkflowError("导入图片类型或 ID 无效")
        return self.stores[kind]

    async def _finish(self, operation: Any) -> Any:
        worker = asyncio.create_task(operation)
        self._workers.add(worker)
        try:
            return await asyncio.shield(worker)
        except asyncio.CancelledError:
            # Cancelling the await cannot stop an OS thread. Keep the lock and
            # shutdown wait alive until both image and metadata writes finish.
            while not worker.done():
                try:
                    await asyncio.shield(worker)
                except asyncio.CancelledError:
                    continue
                except Exception:
                    break
            if not worker.cancelled():
                worker.exception()
            raise
        finally:
            self._workers.discard(worker)

    async def _thread(self, function: Any, *args: Any, **kwargs: Any) -> Any:
        return await self._finish(asyncio.to_thread(function, *args, **kwargs))

    @staticmethod
    def _checked_path(store: Any, asset_id: str) -> Path:
        root = store.path.resolve(strict=True)
        for suffix in (".png", ".json"):
            path = store.path / f"{asset_id}{suffix}"
            if path.is_symlink() or not path.resolve().is_relative_to(root):
                raise WorkflowError("导入图片路径超出受管目录")
        return store.path / f"{asset_id}.png"

    async def add(self, kind: str, body: bytes, *, filename: str = "image",
                  source_image_id: int | None = None) -> dict[str, Any]:
        async with self._lock:
            store = self._store(kind)
            self.history.ensure_writable()
            kwargs = {"filename": filename}
            if kind == "input":
                kwargs["source_image_id"] = source_image_id
            return await self._thread(store.add, body, **kwargs)

    async def read(self, kind: str, asset_id: str) -> dict[str, Any]:
        async with self._lock:
            store = self._store(kind, asset_id)

            def read() -> dict[str, Any]:
                self._checked_path(store, asset_id)
                return store.get(asset_id)

            return await self._thread(read)

    async def read_bytes(self, kind: str, asset_id: str) -> bytes:
        async with self._lock:
            store = self._store(kind, asset_id)

            def read() -> bytes:
                path = self._checked_path(store, asset_id)
                store.get(asset_id)
                return path.read_bytes()

            return await self._thread(read)

    async def prepare(self, asset_id: str, width: int, height: int, fit: str) -> bytes:
        async with self._lock:
            store = self._store("input", asset_id)

            def prepare() -> bytes:
                self._checked_path(store, asset_id)
                return store.prepare(asset_id, width, height, fit)

            return await self._thread(prepare)

    async def upload_comfy(self, body: bytes, filename: str) -> str:
        if self._closed:
            raise WorkflowError("图片服务正在关闭")
        return await self._finish(self.comfy.upload_image(body, filename))

    async def close(self) -> None:
        self._closed = True
        async with self._lock:
            pass
        if self._workers:
            await asyncio.gather(*self._workers, return_exceptions=True)
