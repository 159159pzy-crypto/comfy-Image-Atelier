"""Failure boundaries for source-linked favorites, using only temporary stores."""
import asyncio
import copy
import hashlib
import json
import threading
from contextlib import nullcontext
from types import SimpleNamespace
from unittest.mock import patch

import pytest

from anima_webui.catalog import PromptCatalog
from anima_webui.comfy import ComfyError
from anima_webui.custom_prompts import CustomPromptStore
from anima_webui.favorites import FavoritesExtensionStore, FavoritesService
from anima_webui.persistence import StorageReadOnlyError, backup_for_migration
from anima_webui.pool_lifecycle import PoolLifecycleError, PoolLifecycleService
from anima_webui.prompt_pools import PromptPoolStore
from test_server import APP_DIR, FakeComfy, _write_fake_tools


def make_world(root, tools=None, comfy=None):
    tools = tools or _write_fake_tools(root)
    catalog = PromptCatalog(APP_DIR, tools)
    custom = CustomPromptStore(root / "custom.json", catalog)
    prompts = PromptPoolStore(root / "prompts.json")
    extension = FavoritesExtensionStore(root / "favorites.json")
    comfy = comfy or FakeComfy()
    favorites = {
        "anima": FavoritesService(comfy, catalog, extension),
        "krea2": FavoritesService(comfy, catalog, extension, "krea2"),
    }
    lifecycle = PoolLifecycleService(root, custom, prompts, favorites)
    return SimpleNamespace(root=root, tools=tools, comfy=comfy, catalog=catalog, custom=custom,
                           prompts=prompts, extension=extension, favorites=favorites, lifecycle=lifecycle)


@pytest.fixture
def world(tmp_path):
    return make_world(tmp_path)


async def save(world, profile="anima", section="pose", title="Old", text="old text"):
    return await world.lifecycle.save_text(profile, section, {"title": title, "text": text})


def test_anima_edit_and_delete_preserve_alias_and_favorite_groups(world):
    async def run():
        saved = await save(world)
        item_id = saved["item_id"]
        favorite = world.favorites["anima"]
        group = (await favorite.create_group("pose", {"name": "Keep this group"}))["group"]
        await favorite.update_item("pose", {"id": item_id, "nickname": "My alias", "groupIds": [group["id"]]})
        edited = await world.lifecycle.source("anima", "pose", "update", item_id,
                                               {"title": "New", "prompt": "new text", "subtitle": "New detail"})
        current = (await favorite.get("pose"))["items"][0]
        assert current["nickname"] == "My alias"
        assert current["customContent"] == "new text" and current["subtitle"] == "New detail"
        assert edited["favoritesSync"]["state"] == "complete"
        deleted = await world.lifecycle.source("anima", "pose", "delete", item_id)
        assert deleted["deletedItemIds"] == [item_id]
        after = await favorite.get("pose")
        assert after["items"] == []
        assert group["id"] in {group["id"] for group in after["groups"]}
        assert world.catalog.get(item_id) is None
    asyncio.run(run())


def test_anima_import_overwrite_uses_same_lifecycle(world):
    async def run():
        saved = await save(world)
        row = {"action": "overwrite", "existingId": saved["item_id"],
               "item": {"section": "pose", "title": "Old", "prompt": "imported text"}}
        result = await world.lifecycle.source("anima", "pose", "commit_import", [row], "pose", [], "Updated pack")
        assert result["updated"] == 1
        assert (await world.favorites["anima"].get("pose"))["items"][0]["customContent"] == "imported text"
        assert world.catalog.get(saved["item_id"])["prompt"] == "imported text"
    asyncio.run(run())


@pytest.mark.parametrize("mode,removed", [("keep", 0), ("exclusive", 1), ("all", 2)])
def test_anima_group_delete_only_removes_affected_sources(world, mode, removed):
    async def run():
        first = await save(world, title="First", text="first")
        second = await save(world, title="Second", text="second")
        group = (await world.custom.create_group("pose", {"name": "Remove"}))["group"]
        other = (await world.custom.create_group("pose", {"name": "Keep"}))["group"]
        await world.custom.update(first["item_id"], {"groupIds": [group["id"]]})
        await world.custom.update(second["item_id"], {"groupIds": [group["id"], other["id"]]})
        result = await world.lifecycle.source("anima", "pose", "delete_group", "pose", group["id"], delete_mode=mode)
        assert len(result["deletedItemIds"]) == removed
        assert len((await world.favorites["anima"].get("pose"))["items"]) == 2 - removed
        assert len(world.custom.list("pose")) == 2 - removed
    asyncio.run(run())


def test_offline_preflight_never_changes_source_or_queues_operation(world):
    async def run():
        saved = await save(world)
        before, favorites = world.custom.path.read_bytes(), copy.deepcopy(world.comfy.favorites_data)
        with patch.object(world.comfy, "favorites", side_effect=ComfyError("offline")):
            with pytest.raises(PoolLifecycleError) as failure:
                await world.lifecycle.source("anima", "pose", "update", saved["item_id"], {"prompt": "rejected"})
        assert failure.value.payload["source_committed"] is False
        assert world.custom.path.read_bytes() == before
        assert world.comfy.favorites_data == favorites
        assert not world.lifecycle._journal("anima").exists()
    asyncio.run(run())


def test_source_disk_failure_rolls_back_only_affected_favorite(world):
    async def run():
        saved = await save(world)
        await save(world, title="Unrelated", text="other")
        before, favorites = world.custom.path.read_bytes(), copy.deepcopy(world.comfy.favorites_data)
        with patch.object(world.custom, "_write_text", side_effect=OSError("disk full")):
            with pytest.raises(PoolLifecycleError) as failure:
                await world.lifecycle.source("anima", "pose", "update", saved["item_id"], {"prompt": "rejected"})
        assert failure.value.payload["code"] == "favorite_sync_failed"
        assert world.custom.path.read_bytes() == before
        assert world.comfy.favorites_data == favorites
        assert not world.lifecycle._journal("anima").exists()
    asyncio.run(run())


def test_remote_reply_lost_is_confirmed_before_source_commit(world):
    async def run():
        saved = await save(world)
        write = world.comfy.save_favorites
        async def lost_reply(payload):
            await write(payload)
            raise ComfyError("reply lost")
        with patch.object(world.comfy, "save_favorites", side_effect=lost_reply):
            result = await world.lifecycle.source("anima", "pose", "update", saved["item_id"], {"prompt": "confirmed"})
        assert result["favoritesSync"]["state"] == "complete"
        assert world.catalog.get(saved["item_id"])["prompt"] == "confirmed"
        assert (await world.favorites["anima"].get("pose"))["items"][0]["customContent"] == "confirmed"
        assert not world.lifecycle._journal("anima").exists()
    asyncio.run(run())


def test_compensation_preserves_unrelated_external_favorite_changes(world):
    async def run():
        target = await save(world)
        unrelated = await save(world, title="Unrelated", text="other")
        def fail_source(_text):
            record = next(item for item in world.comfy.favorites_data["pose"]["items"] if item["id"] == unrelated["item_id"])
            record["nickname"] = "Changed in another app"
            raise OSError("source write failed")
        with patch.object(world.custom, "_write_text", side_effect=fail_source):
            with pytest.raises(PoolLifecycleError):
                await world.lifecycle.source("anima", "pose", "update", target["item_id"], {"prompt": "rejected"})
        records = {item["id"]: item for item in world.comfy.favorites_data["pose"]["items"]}
        assert records[target["item_id"]]["customContent"] == "old text"
        assert records[unrelated["item_id"]]["nickname"] == "Changed in another app"
        assert not world.lifecycle._journal("anima").exists()
    asyncio.run(run())


def test_conflicting_recovery_preserves_external_edit_and_blocks_new_source_write(world):
    async def run():
        saved = await save(world)
        source_before = world.custom.path.read_bytes()
        def fail_source(_text):
            world.comfy.favorites_data["pose"]["items"][0]["customContent"] = "External conflict"
            raise OSError("source write failed")
        with patch.object(world.custom, "_write_text", side_effect=fail_source):
            with pytest.raises(PoolLifecycleError) as failure:
                await world.lifecycle.source("anima", "pose", "update", saved["item_id"], {"prompt": "rejected"})
        assert failure.value.payload["recovery_required"]
        assert world.custom.path.read_bytes() == source_before
        assert world.lifecycle._journal("anima").exists()
        with pytest.raises(PoolLifecycleError):
            await world.lifecycle.source("anima", "pose", "delete", saved["item_id"])
        assert world.custom.path.read_bytes() == source_before
        assert world.comfy.favorites_data["pose"]["items"][0]["customContent"] == "External conflict"
    asyncio.run(run())


def test_disconnection_keeps_undo_log_and_restart_only_rolls_back(world):
    async def run():
        saved = await save(world)
        before, old_favorites = world.custom.path.read_bytes(), copy.deepcopy(world.comfy.favorites_data)
        read, write, offline = world.comfy.favorites, world.comfy.save_favorites, False
        async def read_until_offline():
            if offline:
                raise ComfyError("disconnected")
            return await read()
        async def write_then_disconnect(payload):
            nonlocal offline
            await write(payload)
            offline = True
            raise ComfyError("disconnected")
        with patch.object(world.comfy, "favorites", side_effect=read_until_offline), patch.object(world.comfy, "save_favorites", side_effect=write_then_disconnect):
            with pytest.raises(PoolLifecycleError) as failure:
                await world.lifecycle.source("anima", "pose", "update", saved["item_id"], {"prompt": "never replay this"})
            assert failure.value.payload["recovery_required"]
        assert world.custom.path.read_bytes() == before
        assert world.lifecycle._journal("anima").exists()
        restored = make_world(world.root, world.tools, world.comfy)
        await restored.lifecycle.reconcile("anima", "pose")
        assert restored.custom.path.read_bytes() == before
        assert world.comfy.favorites_data == old_favorites
        assert not restored.lifecycle._journal("anima").exists()
    asyncio.run(run())


def test_started_transaction_finishes_despite_repeated_cancellation(world):
    async def run():
        saved = await save(world)
        write, loop = world.custom._write_text, asyncio.get_running_loop()
        entered, release = asyncio.Event(), threading.Event()
        def held_write(text):
            loop.call_soon_threadsafe(entered.set)
            if not release.wait(5):
                raise TimeoutError("test writer timed out")
            write(text)
        with patch.object(world.custom, "_write_text", side_effect=held_write):
            task = asyncio.create_task(world.lifecycle.source("anima", "pose", "update", saved["item_id"], {"prompt": "finished"}))
            try:
                await asyncio.wait_for(entered.wait(), 2)
                task.cancel()
                await asyncio.sleep(0)
                task.cancel()
                await asyncio.sleep(0)
                assert not task.done()
            finally:
                release.set()
                outcome = await asyncio.gather(task, return_exceptions=True)
        assert isinstance(outcome[0], asyncio.CancelledError)
        assert world.catalog.get(saved["item_id"])["prompt"] == "finished"
        assert (await world.favorites["anima"].get("pose"))["items"][0]["customContent"] == "finished"
        assert not world.lifecycle._journal("anima").exists()
    asyncio.run(run())


def test_krea_migration_reuses_matching_source_restores_missing_and_preserves_aliases(world):
    async def run():
        source = await world.prompts.ensure_item("krea2", "Existing text", title="Source title")
        await world.extension.save({"groups": [{"id": "default", "name": "Default"}], "items": [
            {"id": "favorite_one", "title": "Alias one", "text": "EXISTING TEXT", "groupIds": ["default"]},
            {"id": "favorite_two", "nickname": "Alias two", "customContent": "Existing text", "groupIds": ["default"]},
            {"id": "favorite_three", "title": "Restored alias", "text": "Missing text", "groupIds": ["default"]},
        ]}, "krea2", "prompt")
        with patch.object(world.comfy, "favorites", side_effect=AssertionError("Krea accessed Comfy")):
            await world.lifecycle.reconcile("krea2", "prompt")
        items = world.extension.get("krea2", "prompt")["items"]
        assert [item["id"] for item in items] == ["favorite_one", "favorite_two", "favorite_three"]
        assert items[0]["source_item_id"] == items[1]["source_item_id"] == source["id"]
        assert items[0]["title"] == "Alias one" and items[1]["nickname"] == "Alias two"
        assert len(world.prompts.all_items("krea2")) == 2
        assert items[2]["title"] == "Restored alias"
        assert len(list(world.root.glob("*.bak"))) >= 2
        await world.lifecycle.reconcile("krea2", "prompt")
        assert world.extension.get("krea2", "prompt")["items"] == items
    asyncio.run(run())


def test_krea_edit_delete_keep_identity_and_favorite_groups_without_comfy(world):
    async def run():
        with patch.object(world.comfy, "favorites", side_effect=AssertionError("Krea accessed Comfy")):
            saved = await save(world, "krea2", "prompt")
            source_id = saved["source_item_id"]
            edited = await world.lifecycle.source("krea2", "prompt", "update_item", "krea2", source_id, "new scene", title="New")
            assert edited["id"] == source_id
            item = world.extension.get("krea2", "prompt")["items"][0]
            assert item["id"] == saved["item_id"] and item["source_item_id"] == source_id
            assert item["text"] == "new scene" and item["title"] == "New"
            await world.lifecycle.source("krea2", "prompt", "delete_item", "krea2", source_id)
            after = world.extension.get("krea2", "prompt")
            assert after["items"] == [] and len(after["groups"]) == 1
    asyncio.run(run())


def test_krea_extension_write_failure_keeps_pool_unchanged(world):
    async def run():
        saved = await save(world, "krea2", "prompt")
        before_pool, before_favorites = world.prompts.path.read_bytes(), world.extension.path.read_bytes()
        with patch.object(world.extension, "_write_text", side_effect=OSError("favorites unavailable")):
            with pytest.raises(PoolLifecycleError):
                await world.lifecycle.source("krea2", "prompt", "delete_item", "krea2", saved["source_item_id"])
        assert world.prompts.path.read_bytes() == before_pool
        assert world.extension.path.read_bytes() == before_favorites
        assert not world.lifecycle._journal("krea2").exists()
    asyncio.run(run())


def test_krea_replace_and_clear_remove_only_deleted_source_favorites(world):
    async def run():
        kept = await save(world, "krea2", "prompt", title="Keep", text="Keep text")
        await save(world, "krea2", "prompt", title="Remove", text="Remove text")
        result = await world.lifecycle.source("krea2", "prompt", "import_items", "krea2", ["Keep text"], mode="replace")
        assert result["valid_ids"] == [kept["source_item_id"]]
        records = world.extension.get("krea2", "prompt")["items"]
        assert [item["id"] for item in records] == [kept["item_id"]]
        await world.lifecycle.source("krea2", "prompt", "clear", "krea2")
        assert world.extension.get("krea2", "prompt")["items"] == []
        assert len(world.extension.get("krea2", "prompt")["groups"]) == 1
    asyncio.run(run())


def test_expression_and_krea_transactions_do_not_overwrite_shared_file(world):
    async def run():
        with patch.object(world.comfy, "favorites", side_effect=AssertionError("Local favorites accessed Comfy")):
            expression, krea = await asyncio.gather(save(world, "anima", "expression", text="smile"), save(world, "krea2", "prompt", text="A scene"))
        restored = FavoritesExtensionStore(world.extension.path)
        assert restored.get("anima", "expression")["items"][0]["id"] == expression["item_id"]
        assert restored.get("krea2", "prompt")["items"][0]["source_item_id"] == krea["source_item_id"]
    asyncio.run(run())


@pytest.mark.parametrize("method", ["source", "save_text", "reconcile"])
def test_missing_krea_source_cannot_erase_existing_linked_favorites(world, method):
    async def run():
        saved = await save(world, "krea2", "prompt")
        world.prompts.path.unlink()
        restored = make_world(world.root, world.tools, world.comfy)
        before = restored.extension.path.read_bytes()
        with pytest.raises(PoolLifecycleError):
            if method == "source":
                await restored.lifecycle.source("krea2", "prompt", "create_item", "krea2", "Unrelated")
            elif method == "save_text":
                await save(restored, "krea2", "prompt", text="Unrelated")
            else:
                await restored.lifecycle.reconcile("krea2", "prompt")
        assert restored.extension.path.read_bytes() == before
        assert restored.extension.get("krea2", "prompt")["items"][0]["id"] == saved["item_id"]
        assert not restored.prompts.path.exists()
    asyncio.run(run())


def test_invalid_source_is_never_used_to_clean_favorites(world):
    async def run():
        await save(world)
        world.custom.path.write_text("{broken", encoding="utf-8")
        restored = make_world(world.root, world.tools, world.comfy)
        before = copy.deepcopy(world.comfy.favorites_data)
        with pytest.raises((StorageReadOnlyError, PoolLifecycleError)):
            await restored.lifecycle.reconcile("anima", "pose")
        assert world.comfy.favorites_data == before
    asyncio.run(run())


@pytest.mark.parametrize("damage", ["null_items", "nonobject_item", "duplicate_id", "unknown_group", "unknown_section"])
def test_lossy_source_is_never_used_to_clean_favorites(world, damage):
    async def run():
        await save(world)
        payload = json.loads(world.custom.path.read_text(encoding="utf-8"))
        if damage == "null_items":
            payload["items"] = None
        elif damage == "nonobject_item":
            payload["items"] = [None]
        elif damage == "duplicate_id":
            payload["items"].append({**payload["items"][0], "prompt": "conflicting content"})
        elif damage == "unknown_group":
            payload["items"][0]["groupIds"] = ["missing"]
        else:
            payload["groups"]["unknown"] = []
        original = json.dumps(payload).encode()
        world.custom.path.write_bytes(original)
        before = copy.deepcopy(world.comfy.favorites_data)
        for _ in range(2):
            restored = make_world(world.root, world.tools, world.comfy)
            assert not restored.custom.load_trusted
            with pytest.raises((StorageReadOnlyError, PoolLifecycleError)):
                await restored.lifecycle.reconcile("anima", "pose")
            assert world.comfy.favorites_data == before
            assert world.custom.path.read_bytes() == original
            assert not restored.lifecycle._journal("anima").exists()
    asyncio.run(run())


def test_extension_write_then_raise_is_confirmed_from_disk_before_source_commit(world):
    async def run():
        saved = await save(world, "krea2", "prompt")
        write, calls = world.extension._write_text, 0
        def write_then_raise(text):
            nonlocal calls
            calls += 1
            write(text)
            if calls == 1:
                raise OSError("write succeeded but its caller failed")
        with patch.object(world.extension, "_write_text", side_effect=write_then_raise):
            result = await world.lifecycle.source("krea2", "prompt", "update_item", "krea2",
                                                    saved["source_item_id"], "confirmed durable text")
        assert result["favoritesSync"]["state"] == "complete"
        memory = world.extension.get("krea2", "prompt")
        durable = FavoritesExtensionStore(world.extension.path).get("krea2", "prompt")
        assert memory == durable
        assert durable["items"][0]["text"] == "confirmed durable text"
        assert world.prompts.all_items("krea2") == PromptPoolStore(world.prompts.path).all_items("krea2")
        assert world.prompts.all_items("krea2")[0]["text"] == durable["items"][0]["text"]
        assert not world.lifecycle._journal("krea2").exists()
    asyncio.run(run())


def test_existing_builtin_favorite_allows_first_custom_source_save(world):
    async def run():
        builtin = world.catalog.all_items("pose")[0]
        assert builtin["builtin"]
        favorite = world.favorites["anima"]
        await favorite.update_item("pose", {"id": builtin["id"]})
        before = copy.deepcopy(world.comfy.favorites_data["pose"]["items"])
        assert not world.custom.path.exists()
        saved = await save(world)
        assert world.custom.path.exists()
        assert world.custom.source_completeness == "complete"
        assert json.loads(world.custom.path.read_text(encoding="utf-8")).get("source_completeness", "complete") == "complete"
        assert world.custom.list("pose")[0]["id"] == saved["item_id"]
        records = (await favorite.get("pose"))["items"]
        assert len(records) == 2
        assert records[0] == before[0]
        assert records[1]["id"] == saved["item_id"]
    asyncio.run(run())


def test_legacy_krea_snapshot_without_any_source_file_is_restored_and_linked(world):
    async def run():
        assert not world.prompts.path.exists()
        await world.extension.save({"groups": [], "items": [{
            "id": "favorite_legacy", "title": "Keep this alias", "nickname": "Personal name",
            "text": "A scene preserved only in favorites", "groupIds": ["default"],
        }]}, "krea2", "prompt")
        await world.lifecycle.reconcile("krea2", "prompt")
        assert world.prompts.path.exists()
        sources = world.prompts.all_items("krea2")
        assert len(sources) == 1 and sources[0]["text"] == "A scene preserved only in favorites"
        saved = world.extension.get("krea2", "prompt")["items"][0]
        assert saved["id"] == "favorite_legacy" and saved["source_item_id"] == sources[0]["id"]
        assert saved["title"] == "Keep this alias" and saved["nickname"] == "Personal name"
        assert saved["groupIds"] == ["default"]
        assert FavoritesExtensionStore(world.extension.path).get("krea2", "prompt")["items"][0] == saved
    asyncio.run(run())


@pytest.mark.parametrize("profile,section", [("anima", "pose"), ("krea2", "prompt")])
def test_external_source_change_is_rejected_before_touching_favorites(world, profile, section):
    async def run():
        saved = await save(world, profile, section)
        store = world.custom if profile == "anima" else world.prompts
        digest = store.disk_digest
        assert digest == hashlib.sha256(store.path.read_bytes()).hexdigest()
        before = (copy.deepcopy(world.comfy.favorites_data) if profile == "anima"
                  else world.extension.path.read_bytes())
        document = json.loads(store.path.read_text(encoding="utf-8"))
        if profile == "anima":
            document["items"][0]["prompt"] = "External change"
        else:
            document["profiles"]["krea2"]["items"][0]["text"] = "External change"
        store.path.write_text(json.dumps(document), encoding="utf-8")
        external = store.path.read_bytes()
        assert hashlib.sha256(external).hexdigest() != digest
        with pytest.raises(PoolLifecycleError) as failure:
            if profile == "anima":
                await world.lifecycle.source(profile, section, "update", saved["item_id"], {"prompt": "rejected local change"})
            else:
                await world.lifecycle.source(profile, section, "update_item", profile,
                                             saved["source_item_id"], "rejected local change")
        assert failure.value.payload["code"] == "favorite_source_changed"
        assert store.path.read_bytes() == external and store.disk_digest == digest
        after = world.comfy.favorites_data if profile == "anima" else world.extension.path.read_bytes()
        assert after == before
        assert not world.lifecycle._journal(profile).exists()
    asyncio.run(run())


@pytest.mark.parametrize("operation", ["create", "group", "import", "cross_section_save"])
def test_missing_anima_shared_source_cannot_be_recreated_over_historical_references(world, operation):
    async def run():
        saved = await save(world)
        world.custom.path.unlink()
        restored = make_world(world.root, world.tools, world.comfy)
        before = copy.deepcopy(world.comfy.favorites_data)
        with pytest.raises(PoolLifecycleError) as failure:
            if operation == "create":
                await restored.lifecycle.source("anima", "pose", "create",
                                                 {"section": "pose", "title": "New", "prompt": "new"})
            elif operation == "group":
                await restored.lifecycle.source("anima", "pose", "create_group", "pose", {"name": "New group"})
            elif operation == "import":
                row = {"action": "create", "item": {"section": "pose", "title": "New", "prompt": "new"}}
                await restored.lifecycle.source("anima", "pose", "commit_import", [row], "pose", [], "New pack")
            else:
                await save(restored, section="character", title="Unrelated character", text="new character")
        assert failure.value.payload["code"] == "favorite_source_unavailable"
        assert world.comfy.favorites_data == before
        assert world.comfy.favorites_data["pose"]["items"][0]["id"] == saved["item_id"]
        assert not restored.custom.path.exists()
        assert restored.custom.list() == []
        assert not restored.lifecycle._journal("anima").exists()
    asyncio.run(run())


def test_offline_expression_save_persists_uncertainty_and_cannot_hide_missing_remote_sources(world):
    async def run():
        prior = await save(world)
        world.custom.path.unlink()
        restored = make_world(world.root, world.tools, world.comfy)
        old_favorites = copy.deepcopy(world.comfy.favorites_data)
        with patch.object(world.comfy, "favorites", side_effect=AssertionError("Expression read Comfy")), patch.object(world.comfy, "save_favorites", side_effect=AssertionError("Expression wrote Comfy")):
            expression = await save(restored, section="expression", title="Local smile", text="smile")
        assert restored.custom.source_completeness == "unverified"
        assert json.loads(restored.custom.path.read_text(encoding="utf-8"))["source_completeness"] == "unverified"
        assert world.comfy.favorites_data == old_favorites
        restarted = make_world(world.root, world.tools, world.comfy)
        assert restarted.custom.source_completeness == "unverified"
        source_bytes, extension_bytes = restarted.custom.path.read_bytes(), restarted.extension.path.read_bytes()
        with pytest.raises(PoolLifecycleError) as failure:
            await restarted.lifecycle.reconcile("anima", "pose")
        assert failure.value.payload["code"] == "favorite_source_unavailable"
        assert world.comfy.favorites_data == old_favorites
        assert world.comfy.favorites_data["pose"]["items"][0]["id"] == prior["item_id"]
        assert restarted.extension.get("anima", "expression")["items"][0]["id"] == expression["item_id"]
        assert restarted.custom.path.read_bytes() == source_bytes
        assert restarted.extension.path.read_bytes() == extension_bytes
    asyncio.run(run())


def test_unverified_local_source_becomes_complete_after_clean_upstream_check(world):
    async def run():
        with patch.object(world.comfy, "favorites", side_effect=AssertionError("Expression read Comfy")):
            expression = await save(world, section="expression", title="Local smile", text="smile")
        assert world.custom.source_completeness == "unverified"
        saved = await save(world)
        assert world.custom.source_completeness == "complete"
        payload = json.loads(world.custom.path.read_text(encoding="utf-8"))
        assert payload.get("source_completeness", "complete") == "complete"
        restarted = make_world(world.root, world.tools, world.comfy)
        assert restarted.custom.source_completeness == "complete"
        assert {item["id"] for item in restarted.custom.list()} == {expression["item_id"], saved["item_id"]}
    asyncio.run(run())


def test_invalid_journal_top_level_returns_recovery_error_without_writes(world):
    async def run():
        await save(world)
        source_before, favorites_before = world.custom.path.read_bytes(), copy.deepcopy(world.comfy.favorites_data)
        journal = world.lifecycle._journal("anima")
        journal.write_text("[]", encoding="utf-8")
        with pytest.raises(PoolLifecycleError) as failure:
            await world.lifecycle.reconcile("anima", "pose")
        assert failure.value.payload["code"] == "favorite_sync_recovery_required"
        assert failure.value.payload["recovery_required"] is True
        assert source_before == world.custom.path.read_bytes()
        assert favorites_before == world.comfy.favorites_data
        assert journal.read_text(encoding="utf-8") == "[]"
    asyncio.run(run())


@pytest.mark.parametrize("boundary", ["source_read", "journal_write"])
@pytest.mark.parametrize("error_type", [OSError, TimeoutError])
def test_prepare_io_failure_never_changes_business_data(world, boundary, error_type):
    async def run():
        saved = await save(world)
        source_before, favorites_before = world.custom.path.read_bytes(), copy.deepcopy(world.comfy.favorites_data)
        target = (patch("anima_webui.pool_lifecycle._read", side_effect=error_type("read failed"))
                  if boundary == "source_read" else
                  patch.object(world.lifecycle, "_save_journal", side_effect=error_type("journal failed")))
        with target, patch.object(world.comfy, "save_favorites", wraps=world.comfy.save_favorites) as writer:
            with pytest.raises(PoolLifecycleError) as failure:
                await world.lifecycle.source("anima", "pose", "update", saved["item_id"], {"prompt": "rejected"})
            writer.assert_not_called()
        assert failure.value.payload["code"] == "favorite_sync_prepare_failed"
        assert failure.value.payload["source_committed"] is False
        assert world.custom.path.read_bytes() == source_before
        assert world.comfy.favorites_data == favorites_before
        assert not world.lifecycle._journal("anima").exists()
    asyncio.run(run())


def test_initial_journal_write_then_raise_cleans_undo_record_without_replaying(world):
    async def run():
        saved = await save(world)
        source_before, favorites_before = world.custom.path.read_bytes(), copy.deepcopy(world.comfy.favorites_data)
        write, calls = world.lifecycle._save_journal, 0
        async def write_then_raise(profile, journal):
            nonlocal calls
            calls += 1
            await write(profile, journal)
            if calls == 1:
                raise OSError("journal durable but caller failed")
        with patch.object(world.lifecycle, "_save_journal", side_effect=write_then_raise), patch.object(world.comfy, "save_favorites", wraps=world.comfy.save_favorites) as writer:
            with pytest.raises(PoolLifecycleError) as failure:
                await world.lifecycle.source("anima", "pose", "update", saved["item_id"], {"prompt": "never replay"})
            writer.assert_not_called()
        assert failure.value.payload["code"] == "favorite_sync_prepare_failed"
        assert world.custom.path.read_bytes() == source_before
        assert world.comfy.favorites_data == favorites_before
        assert not world.lifecycle._journal("anima").exists()
        restarted = make_world(world.root, world.tools, world.comfy)
        await restarted.lifecycle.reconcile("anima", "pose")
        assert restarted.custom.list("pose")[0]["prompt"] == "old text"
    asyncio.run(run())


@pytest.mark.parametrize("failed_backup", [1, 2])
def test_krea_migration_backup_failure_preserves_both_stores(world, failed_backup):
    async def run():
        await world.prompts.ensure_item("krea2", "Existing source")
        await world.extension.save({"groups": [], "items": [{
            "id": "favorite_legacy", "text": "Restore this missing source", "title": "Keep alias", "groupIds": ["default"],
        }]}, "krea2", "prompt")
        source_before, favorite_before = world.prompts.path.read_bytes(), world.extension.path.read_bytes()
        source_items, favorite_items = world.prompts.all_items("krea2"), world.extension.get("krea2", "prompt")
        calls = 0
        def fail_selected_backup(path, version):
            nonlocal calls
            calls += 1
            if calls == failed_backup:
                raise OSError("backup failed")
            return backup_for_migration(path, version)
        with patch("anima_webui.pool_lifecycle.backup_for_migration", side_effect=fail_selected_backup):
            with pytest.raises(PoolLifecycleError) as failure:
                await world.lifecycle.reconcile("krea2", "prompt")
        assert failure.value.payload["code"] == "favorite_sync_prepare_failed"
        assert world.prompts.path.read_bytes() == source_before
        assert world.extension.path.read_bytes() == favorite_before
        assert world.prompts.all_items("krea2") == source_items
        assert world.extension.get("krea2", "prompt") == favorite_items
        assert not world.lifecycle._journal("krea2").exists()
    asyncio.run(run())


@pytest.mark.parametrize("damage", [
    "item_capacity", "group_capacity", "invalid_item", "unknown_membership",
    "duplicate_identity", "explicit_null_section", "invalid_top_level", "long_group_name",
])
def test_remote_favorites_that_would_lose_data_are_rejected_without_any_write(world, damage):
    async def run():
        saved = await save(world)
        section = world.comfy.favorites_data["pose"]
        if damage == "item_capacity":
            section["items"].append({"id": "foreign-kept", "name": "Foreign", "groupIds": ["default"], "isCustom": False})
        elif damage == "group_capacity":
            section["groups"].append({"id": "foreign-group", "name": "Keep this group"})
        elif damage == "invalid_item":
            section["items"].append("legacy unparsed record")
        elif damage == "unknown_membership":
            section["items"][0]["groupIds"].append("unrecognized-group")
        elif damage == "duplicate_identity":
            section["items"].append({**section["items"][0], "nickname": "Second alias with same ID"})
        elif damage == "explicit_null_section":
            world.comfy.favorites_data["pose"] = None
        elif damage == "invalid_top_level":
            world.comfy.favorites_data = []
        else:
            section["groups"].append({"id": "long-name", "name": "x" * 101})
        source_before, favorites_before = world.custom.path.read_bytes(), copy.deepcopy(world.comfy.favorites_data)
        limit = (patch("anima_webui.favorites.MAX_ITEMS", 1) if damage == "item_capacity" else
                 patch("anima_webui.favorites.MAX_GROUPS", 1) if damage == "group_capacity" else nullcontext())
        with limit, patch.object(world.comfy, "save_favorites", wraps=world.comfy.save_favorites) as writer:
            with pytest.raises(PoolLifecycleError) as failure:
                await world.lifecycle.source("anima", "pose", "update", saved["item_id"], {"prompt": "rejected"})
            writer.assert_not_called()
        assert failure.value.payload["code"] == "favorites_unavailable"
        assert world.custom.path.read_bytes() == source_before
        assert world.comfy.favorites_data == favorites_before
        assert not world.lifecycle._journal("anima").exists()
    asyncio.run(run())


def test_absent_remote_category_remains_compatible_with_first_save(world):
    async def run():
        world.comfy.favorites_data.pop("pose")
        saved = await save(world)
        assert world.custom.list("pose")[0]["id"] == saved["item_id"]
        assert world.comfy.favorites_data["pose"]["items"][0]["id"] == saved["item_id"]
    asyncio.run(run())
