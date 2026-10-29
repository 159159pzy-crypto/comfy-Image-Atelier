import asyncio
import copy
import json
from unittest.mock import patch

import pytest

from anima_webui.catalog import CatalogError
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
    with patch("anima_webui.favorites.backup_for_migration", side_effect=PermissionError("locked")):
        store = FavoritesExtensionStore(path)
    assert store.storage_status()["read_only"] is True
    assert path.read_text(encoding="utf-8") == "{broken"
    assert store.source_trusted is False
    assert store.load_state == "invalid"


@pytest.mark.parametrize("section", [
    {"groups": [None], "items": []},
    {"groups": [{"id": "g", "name": "First"}, {"id": "g", "name": "Second"}], "items": []},
    {"groups": [{"id": "g", "name": "x" * 101}], "items": []},
    {"groups": [], "items": [None]},
    {"groups": [], "items": [{"id": "favorite_one", "groupIds": ["missing"]}]},
    {"groups": [], "items": [{"id": "favorite_one"}, {"id": "favorite_one"}]},
])
def test_invalid_extension_never_drops_records_or_becomes_writable_after_restart(tmp_path, section):
    path = tmp_path / "favorites.json"
    original = json.dumps({"version": 2, "profiles": {"krea2": {"prompt": section}}}).encode()
    path.write_bytes(original)
    for _ in range(2):
        store = FavoritesExtensionStore(path)
        assert not store.source_trusted and store.load_state == "invalid"
        assert store.storage_status()["read_only"]
        assert path.read_bytes() == original
        with pytest.raises(StorageReadOnlyError):
            asyncio.run(store.save({"groups": [], "items": []}, "krea2", "prompt"))
    assert path.with_name(path.name + ".v3.bak").read_bytes() == original


def test_over_capacity_extension_is_protected_without_truncating(tmp_path):
    path = tmp_path / "favorites.json"
    original = json.dumps({"version": 2, "profiles": {"krea2": {"prompt": {
        "groups": [], "items": [{"id": f"favorite_{index}"} for index in range(3)],
    }}}}).encode()
    path.write_bytes(original)
    with patch("anima_webui.favorites.MAX_ITEMS", 2):
        store = FavoritesExtensionStore(path)
    assert store.storage_status()["read_only"] and not store.source_trusted
    assert path.read_bytes() == original
    assert path.with_name(path.name + ".v3.bak").read_bytes() == original


def test_v2_migration_preserves_legacy_aliases_groups_and_source_links(tmp_path):
    path = tmp_path / "favorite_extensions.json"
    original = {"version": 2, "profiles": {
        "anima": {"expression": {"groups": [], "items": [{"id": "custom:expression", "nickname": "Keep"}]}},
        "krea2": {"prompt": {
            "groups": [{"id": "default", "name": "Default"}, {"id": "scene", "name": "Scenes"}],
            "items": [
                {"id": "favorite_legacy", "text": "A forest", "title": "My alias", "groupIds": ["scene"]},
                {"id": "favorite_linked", "text": "A lake", "source_item_id": "prompt_lake", "groupIds": ["default"]},
            ],
        }},
    }}
    path.write_text(json.dumps(original), encoding="utf-8")
    before = path.read_bytes()
    store = FavoritesExtensionStore(path)
    assert store.source_trusted is True and store.load_state == "loaded"
    assert path.with_name(path.name + ".v3.bak").read_bytes() == before
    assert json.loads(path.read_text(encoding="utf-8"))["version"] == 3
    legacy, linked = store.get("krea2", "prompt")["items"]
    assert legacy["id"] == "favorite_legacy" and legacy["title"] == "My alias"
    assert legacy["groupIds"] == ["scene"] and "source_item_id" not in legacy
    assert linked["source_item_id"] == "prompt_lake"
    assert store.value["items"][0]["nickname"] == "Keep"
    restored = FavoritesExtensionStore(path)
    assert restored.get("krea2", "prompt") == store.get("krea2", "prompt")
    assert len(list(tmp_path.glob("*.bak"))) == 1


def test_missing_and_unreadable_sources_are_not_trusted(tmp_path):
    path = tmp_path / "favorite_extensions.json"
    missing = FavoritesExtensionStore(path)
    assert missing.source_trusted is False and missing.load_state == "missing"
    assert not path.exists()
    path.write_text('{"version":3,"profiles":{}}', encoding="utf-8")
    with patch("pathlib.Path.read_text", side_effect=PermissionError("locked")):
        unreadable = FavoritesExtensionStore(path)
    assert unreadable.source_trusted is False and unreadable.load_state == "unreadable"
    assert unreadable.storage_status()["read_only"]
    assert path.exists()


@pytest.mark.parametrize("source_id", [None, "", [], 17])
def test_invalid_source_link_is_not_silently_trusted(tmp_path, source_id):
    path = tmp_path / "favorite_extensions.json"
    path.write_text(json.dumps({"version": 3, "profiles": {"krea2": {"prompt": {
        "groups": [], "items": [{"id": "favorite_one", "source_item_id": source_id}],
    }}}}), encoding="utf-8")
    store = FavoritesExtensionStore(path)
    assert not store.source_trusted and store.load_state == "invalid"
    assert store.get("krea2", "prompt")["items"] == []
    assert path.with_name(path.name + ".v3.bak").exists()
    assert path.exists() and store.storage_status()["read_only"]


def test_source_link_roundtrip_survives_other_profile_save(tmp_path):
    async def run():
        path = tmp_path / "favorite_extensions.json"
        store = FavoritesExtensionStore(path)
        await store.save({"groups": [], "items": [{"id": "favorite_one", "source_item_id": "prompt_one"}]}, "krea2", "prompt")
        await store.save({"groups": [], "items": [{"id": "custom:smile", "nickname": "Smile"}]})
        restored = FavoritesExtensionStore(path)
        assert restored.source_trusted and restored.load_state == "loaded"
        assert restored.get("krea2", "prompt")["items"][0]["source_item_id"] == "prompt_one"
        assert restored.value["items"][0]["nickname"] == "Smile"
    asyncio.run(run())


def test_krea_merge_uses_source_identity_and_retains_individual_aliases():
    current = {"groups": [], "items": [
        {"id": "favorite_first", "source_item_id": "source_one", "text": "Same text",
         "title": "First alias", "groupIds": ["first"]},
        {"id": "favorite_second", "source_item_id": "source_one", "text": "Same text",
         "title": "Second alias", "groupIds": ["second"]},
    ]}
    sources = [
        {"id": "source_one", "text": "Same text", "title": "Source"},
        {"id": "source_two", "text": "Same text", "title": "Different source"},
    ]
    created, updated = FavoritesService._merge_prompt_snapshots(current, sources, ["added"])
    assert (created, updated) == (1, 1)
    assert len(current["items"]) == 3
    assert current["items"][0]["id"] == "favorite_first"
    assert current["items"][0]["title"] == "First alias"
    assert current["items"][0]["groupIds"] == ["first", "added"]
    assert current["items"][1]["id"] == "favorite_second"
    assert current["items"][1]["title"] == "Second alias"
    assert current["items"][1]["groupIds"] == ["second"]
    assert current["items"][2]["source_item_id"] == "source_two"


def test_krea_cancel_by_source_removes_all_links_and_retains_groups(tmp_path):
    async def run():
        store = FavoritesExtensionStore(tmp_path / "favorites.json")
        service = FavoritesService(None, None, store, "krea2")
        groups = [{"id": "default", "name": "Default"}, {"id": "leaf", "name": "Leaf", "parentId": "default"}]
        await store.save({"groups": groups, "items": [
            {"id": "favorite_a", "source_item_id": "source_one", "groupIds": ["leaf"]},
            {"id": "favorite_b", "source_item_id": "source_one", "groupIds": ["default"]},
            {"id": "favorite_c", "source_item_id": "source_two", "groupIds": ["default"]},
        ]}, "krea2", "prompt")
        result = await service.update_item("prompt", {"source_item_id": "source_one", "favorite": False})
        assert result["removedCount"] == 2
        assert [item["id"] for item in result["items"]] == ["favorite_c"]
        assert {group["id"] for group in result["groups"]} == {"default", "leaf"}
        repeated = await service.update_item("prompt", {"source_item_id": "source_one", "favorite": False})
        assert repeated["removedCount"] == 0
        before = store.path.read_bytes()
        for payload in (
            {"source_item_id": "source_two", "favorite": True},
            {"source_item_id": "source_two", "id": "favorite_c", "favorite": False},
            {"source_item_id": "", "favorite": False},
        ):
            with pytest.raises(CatalogError):
                await service.update_item("prompt", payload)
        assert store.path.read_bytes() == before
    asyncio.run(run())


def test_krea_cancel_by_favorite_id_keeps_other_aliases(tmp_path):
    async def run():
        store = FavoritesExtensionStore(tmp_path / "favorites.json")
        service = FavoritesService(None, None, store, "krea2")
        await store.save({"groups": [], "items": [
            {"id": "favorite_a", "source_item_id": "source_one", "groupIds": ["default"]},
            {"id": "favorite_b", "source_item_id": "source_one", "groupIds": ["default"]},
        ]}, "krea2", "prompt")
        result = await service.update_item("prompt", {"id": "favorite_a", "favorite": False})
        assert [item["id"] for item in result["items"]] == ["favorite_b"]
    asyncio.run(run())
