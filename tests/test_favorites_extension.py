import asyncio
import copy
import json
from unittest.mock import patch

import pytest

from anima_webui.favorites import FavoritesExtensionStore, FavoritesService
from anima_webui.persistence import StorageReadOnlyError


class WhitelistedComfy:
    """Match the real plugin: silently discard expression writes."""

    def __init__(self):
        self.payload = {"artist": {"groups": [{"id": "default", "name": "Default"}], "items": [{"name": "anmi", "nickname": "keep", "groupIds": ["default"]}]}}
        self.writes = []

    async def favorites(self):
        return copy.deepcopy(self.payload)

    async def save_favorites(self, payload):
        self.writes.append(copy.deepcopy(payload))
        for section, value in payload.items():
            if section != "expression":
                self.payload[section] = copy.deepcopy(value)


class Catalog:
    def get(self, item_id):
        return {"id": item_id, "favorite_key": item_id, "section": "expression", "title": item_id, "prompt": "smile", "builtin": False}


def test_expression_survives_restart_and_keeps_upstream_untouched(tmp_path):
    async def run():
        comfy = WhitelistedComfy()
        original = copy.deepcopy(comfy.payload)
        path = tmp_path / "favorite_extensions.json"
        service = FavoritesService(comfy, Catalog(), FavoritesExtensionStore(path))
        group = (await service.create_group("expression", {"name": "表情"}))["group"]
        await service.update_item("expression", {"id": "custom:smile", "groupIds": [group["id"]], "nickname": "轻微微笑"})
        restored = FavoritesService(comfy, Catalog(), FavoritesExtensionStore(path))
        result = await restored.get("expression")
        assert result["favorite_keys"] == ["custom:smile"]
        assert result["items"][0]["nickname"] == "轻微微笑"
        assert result["items"][0]["groupIds"] == [group["id"]]
        assert comfy.payload == original
        assert comfy.writes == []
        await restored.delete_group("expression", group["id"])
        assert (await restored.get("expression"))["items"][0]["groupIds"] == ["default"]
        assert (await restored.get("artist"))["items"][0]["nickname"] == "keep"

    asyncio.run(run())


def test_simultaneous_expression_updates_are_serialized(tmp_path):
    async def run():
        path = tmp_path / "favorite_extensions.json"
        service = FavoritesService(WhitelistedComfy(), Catalog(), FavoritesExtensionStore(path))
        await asyncio.gather(*(service.update_item("expression", {"id": f"custom:{index}"}) for index in range(12)))
        assert len((await service.get("expression"))["items"]) == 12
        assert len(json.loads(path.read_text(encoding="utf-8"))["profiles"]["anima"]["expression"]["items"]) == 12

    asyncio.run(run())


def test_failed_write_preserves_previous_memory_and_file(tmp_path):
    async def run():
        path = tmp_path / "favorite_extensions.json"
        store = FavoritesExtensionStore(path)
        service = FavoritesService(WhitelistedComfy(), Catalog(), store)
        await service.update_item("expression", {"id": "original"})
        before = path.read_bytes()
        with patch.object(store, "_write_text", side_effect=OSError("disk unavailable")):
            with pytest.raises(StorageReadOnlyError):
                await service.update_item("expression", {"id": "lost"})
        assert path.read_bytes() == before
        assert (await service.get("expression"))["favorite_keys"] == ["original"]
        assert store.storage_status()["read_only"] is True

    asyncio.run(run())


def test_unreadable_extension_does_not_overwrite_source(tmp_path):
    path = tmp_path / "favorite_extensions.json"
    path.write_text("{broken", encoding="utf-8")
    with patch("anima_webui.persistence.backup_corrupt_file", side_effect=PermissionError("locked")):
        store = FavoritesExtensionStore(path)
    assert store.storage_status()["read_only"] is True
    assert path.read_text(encoding="utf-8") == "{broken"
