"""Restart recovery runs before serving dependent pool and favorite reads."""
import asyncio
import copy
import hashlib
import json
from pathlib import Path
from types import SimpleNamespace

import pytest
from aiohttp.test_utils import TestClient, TestServer

from anima_webui.catalog import PromptCatalog
from anima_webui.comfy import ComfyError
from anima_webui.custom_prompts import CHARACTER_FIELDS, CUSTOM_DATA_VERSION, CustomPromptStore
from anima_webui.favorites import FavoritesExtensionStore, FavoritesService, favorite_key
from anima_webui.pool_lifecycle import PoolLifecycleService
from anima_webui.persistence import StorageReadOnlyError
from anima_webui.prompt_pools import PromptPoolStore
from anima_webui.server import CUSTOM_PROMPTS_KEY, FAVORITES_KEY, create_app
from test_server import APP_DIR, FakeComfy, _write_fake_tools


class SwitchableComfy(FakeComfy):
    offline = False

    async def favorites(self):
        if self.offline:
            raise ComfyError("temporarily offline")
        return await super().favorites()

    async def save_favorites(self, payload):
        if self.offline:
            raise ComfyError("temporarily offline")
        return await super().save_favorites(payload)


def prepare(root: Path):
    tools = _write_fake_tools(root)
    catalog = PromptCatalog(APP_DIR, tools)
    custom = CustomPromptStore(root / "custom_prompts.json", catalog)
    prompts = PromptPoolStore(root / "prompt_pools.json")
    extension = FavoritesExtensionStore(root / "favorite_extensions.json")
    comfy = SwitchableComfy()
    favorites = {profile: FavoritesService(comfy, catalog, extension, profile) for profile in ("anima", "krea2")}
    lifecycle = PoolLifecycleService(root, custom, prompts, favorites)
    return SimpleNamespace(root=root, tools=tools, custom=custom, prompts=prompts,
                           extension=extension, comfy=comfy, lifecycle=lifecycle)


async def leave_pending(world, profile):
    section = "prompt" if profile == "krea2" else "pose"
    saved = await world.lifecycle.save_text(profile, section, {"title": "Old", "text": "old text"})
    before = (await world.lifecycle._read_favorites(profile, [section]))[section]
    after = copy.deepcopy(before)
    after["items"][0]["customContent"] = "uncommitted new text"
    if profile == "krea2":
        after["items"][0]["text"] = "uncommitted new text"
        await world.extension.save(after, profile, section)
    else:
        await world.comfy.save_favorites({section: after})
    source = world.prompts if profile == "krea2" else world.custom
    source_bytes = source.path.read_bytes()
    journal = world.lifecycle._journal(profile)
    journal.write_text(json.dumps({
        "version": 1, "id": f"pending-{profile}", "profile": profile,
        "before_hash": hashlib.sha256(source_bytes).hexdigest(),
        "after_hash": hashlib.sha256(b"source write never happened").hexdigest(),
        "commit_decided": False,
        "favorites": {section: [{"key": favorite_key(section, before["items"][0]),
                                  "before": before["items"][0], "after": after["items"][0]}]},
    }), encoding="utf-8")
    return SimpleNamespace(saved=saved, before=before, source=source, source_bytes=source_bytes, journal=journal)


@pytest.mark.parametrize("profile", ["anima", "krea2"])
def test_startup_recovers_existing_journal_before_any_http_request(tmp_path, profile):
    async def run():
        world = prepare(tmp_path)
        pending = await leave_pending(world, profile)
        app = create_app(app_dir=APP_DIR, comfy=world.comfy, data_dir=tmp_path, anima_tools_dir=world.tools)
        async with TestClient(TestServer(app)):
            assert not pending.journal.exists()
            assert pending.source.path.read_bytes() == pending.source_bytes
            restored = (world.comfy.favorites_data["pose"] if profile == "anima" else
                        app[FAVORITES_KEY].expression_store.get("krea2", "prompt"))
            assert restored == pending.before
    asyncio.run(run())


def test_one_offline_profile_does_not_block_startup_other_recovery_or_config(tmp_path, caplog):
    async def run():
        world = prepare(tmp_path)
        anima = await leave_pending(world, "anima")
        krea = await leave_pending(world, "krea2")
        world.comfy.offline = True
        app = create_app(app_dir=APP_DIR, comfy=world.comfy, data_dir=tmp_path, anima_tools_dir=world.tools)
        async with TestClient(TestServer(app)) as client:
            assert anima.journal.exists()
            assert not krea.journal.exists()
            assert "anima" in caplog.text and "temporarily offline" in caplog.text
            assert (await client.get("/api/config")).status == 200
            assert (await client.get("/api/prompt-pools?model_profile=krea2")).status == 200
            for endpoint in ("/api/favorites/pose", "/api/pools/pose?collection=__all__",
                             "/api/custom-prompts?section=pose", "/api/custom-groups/pose"):
                response = await client.get(endpoint)
                payload = await response.json()
                assert response.status == 503, (endpoint, payload)
                assert payload["recovery_required"] is True
                assert payload["code"] == "favorite_sync_recovery_required"
            assert anima.source.path.read_bytes() == anima.source_bytes
            world.comfy.offline = False
            response = await client.get("/api/pools/pose?collection=__all__")
            assert response.status == 200, await response.text()
            assert not anima.journal.exists()
            assert world.comfy.favorites_data["pose"] == anima.before
    asyncio.run(run())


def test_startup_without_journal_does_not_migrate_legacy_favorites(tmp_path):
    async def run():
        world = prepare(tmp_path)
        await world.extension.save({"groups": [{"id": "default", "name": "Default"}], "items": [
            {"id": "legacy-favorite", "title": "Legacy", "text": "keep until requested", "groupIds": ["default"]},
        ]}, "krea2", "prompt")
        favorite_bytes = world.extension.path.read_bytes()
        app = create_app(app_dir=APP_DIR, comfy=world.comfy, data_dir=tmp_path, anima_tools_dir=world.tools)
        async with TestClient(TestServer(app)):
            assert not world.prompts.path.exists()
            assert world.extension.path.read_bytes() == favorite_bytes
            assert not world.lifecycle._journal("krea2").exists()
    asyncio.run(run())


async def leave_v4_character_journal(world, committed):
    """Represent either durable side of an interrupted pre-upgrade transaction."""
    await world.lifecycle.save_text("anima", "character", {"title": "Custom Alpha", "text": "alpha, series"})
    before = (await world.lifecycle._read_favorites("anima", ["character"]))["character"]
    after = copy.deepcopy(before)
    after["items"][0]["customContent"] = "series, alpha, white shirt"
    original = json.loads(world.custom.path.read_text(encoding="utf-8"))
    original["version"] = 4
    for item in original["items"]:
        for key in CHARACTER_FIELDS:
            item.pop(key, None)
    changed = copy.deepcopy(original)
    changed["items"][0]["prompt"] = "series, alpha, white shirt"
    before_bytes = json.dumps(original).encode()
    after_bytes = json.dumps(changed).encode()
    source_bytes = after_bytes if committed else before_bytes
    world.custom.path.write_bytes(source_bytes)
    # Force recovery to actually apply the journal, rather than only remove it.
    await world.comfy.save_favorites({"character": before if committed else after})
    journal = world.lifecycle._journal("anima")
    journal.write_text(json.dumps({
        "version": 1, "id": "pending-v4", "profile": "anima",
        "before_hash": hashlib.sha256(before_bytes).hexdigest(),
        "after_hash": hashlib.sha256(after_bytes).hexdigest(), "commit_decided": committed,
        "favorites": {"character": [{"key": favorite_key("character", before["items"][0]),
                                       "before": before["items"][0], "after": after["items"][0]}]},
    }), encoding="utf-8")
    return SimpleNamespace(source_bytes=source_bytes, journal=journal, before=before, after=after)


@pytest.mark.parametrize("committed", [False, True])
def test_v4_source_migration_waits_for_both_journal_recovery_endpoints(tmp_path, committed):
    async def run():
        world = prepare(tmp_path)
        pending = await leave_v4_character_journal(world, committed)
        app = create_app(app_dir=APP_DIR, comfy=world.comfy, data_dir=tmp_path, anima_tools_dir=world.tools)
        store = app[CUSTOM_PROMPTS_KEY]
        assert store.migration_pending and store.storage_status()["read_only"]
        assert store.path.read_bytes() == pending.source_bytes
        with pytest.raises(StorageReadOnlyError):
            store.ensure_writable()
        async with TestClient(TestServer(app)):
            assert not pending.journal.exists()
            assert not store.migration_pending and not store.storage_status()["read_only"]
            assert world.comfy.favorites_data["character"] == (pending.after if committed else pending.before)
            migrated = json.loads(store.path.read_text(encoding="utf-8"))
            assert migrated["version"] == CUSTOM_DATA_VERSION
            assert migrated["items"][0]["character_status"] == "ready"
            assert migrated["items"][0]["prompt"] == ("series, alpha, white shirt" if committed else "alpha, series")
            assert store.path.with_name("custom_prompts.json.pre-v5.bak").read_bytes() == pending.source_bytes
            # A later explicit edit and reload must retain the post-recovery v5
            # classification instead of falling back to journal-era metadata.
            item = store.items[0]
            parts = copy.deepcopy(item["character_parts"])
            for part in parts:
                part["source"] = "manual"
            edited = await store.update(item["id"], {"title": "Reviewed", "character_parts": parts})
            store.reload()
            assert store.load_trusted and store.items[0] == edited
    asyncio.run(run())


def test_failed_v4_journal_recovery_keeps_original_bytes_and_defers_migration(tmp_path):
    async def run():
        world = prepare(tmp_path)
        pending = await leave_v4_character_journal(world, False)
        world.comfy.offline = True
        app = create_app(app_dir=APP_DIR, comfy=world.comfy, data_dir=tmp_path, anima_tools_dir=world.tools)
        store = app[CUSTOM_PROMPTS_KEY]
        async with TestClient(TestServer(app)) as client:
            assert pending.journal.exists()
            assert store.migration_pending and store.storage_status()["read_only"]
            assert store.path.read_bytes() == pending.source_bytes
            assert not list(tmp_path.glob("custom_prompts.json.pre-v5*.bak"))
            with pytest.raises(StorageReadOnlyError):
                await store.update(store.items[0]["id"], {"title": "Must not write"})
            response = await client.get("/api/custom-prompts?section=character")
            assert response.status == 503
            assert store.path.read_bytes() == pending.source_bytes
            world.comfy.offline = False
            response = await client.get("/api/custom-prompts?section=character")
            assert response.status == 200, await response.text()
            assert not pending.journal.exists() and not store.migration_pending
            assert world.comfy.favorites_data["character"] == pending.before
            assert json.loads(store.path.read_text(encoding="utf-8"))["version"] == CUSTOM_DATA_VERSION
    asyncio.run(run())
