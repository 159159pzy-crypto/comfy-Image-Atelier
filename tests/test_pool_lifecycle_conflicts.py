"""External favorite edits during transaction preparation must never be lost."""
import asyncio
import copy
import json
from unittest.mock import patch

import pytest

from anima_webui.pool_lifecycle import PoolLifecycleError
from anima_webui.comfy import ComfyError
from test_pool_lifecycle import make_world, save


@pytest.mark.parametrize("profile,section", [("anima", "pose"), ("krea2", "prompt")])
@pytest.mark.parametrize("change_target", [False, True], ids=["unrelated-favorite", "target-favorite"])
def test_prepared_transaction_preserves_external_favorite_edit(tmp_path, profile, section, change_target):
    async def run():
        world = make_world(tmp_path)
        target = await save(world, profile, section)
        unrelated = await save(world, profile, section, title="Unrelated", text="other text")
        source = world.prompts if profile == "krea2" else world.custom
        source_id = target["source_item_id"] if profile == "krea2" else target["item_id"]
        changed_favorite_id = (target if change_target else unrelated)["item_id"]
        source_before = source.path.read_bytes()
        original_save_journal = world.lifecycle._save_journal
        injected = False

        async def journal_then_external_edit(model, journal):
            nonlocal injected
            await original_save_journal(model, journal)
            if injected:
                return
            injected = True
            assert world.lifecycle._journal(profile).exists()
            if profile == "anima":
                records = world.comfy.favorites_data[section]["items"]
                next(item for item in records if item["id"] == changed_favorite_id)["nickname"] = "External alias"
            else:
                payload = json.loads(world.extension.path.read_text(encoding="utf-8"))
                records = payload["profiles"][profile][section]["items"]
                next(item for item in records if item["id"] == changed_favorite_id)["nickname"] = "External alias"
                # Simulate a second writer changing disk, without changing this
                # process's cached extension state.
                world.extension.path.write_text(json.dumps(payload), encoding="utf-8")

        with patch.object(world.lifecycle, "_save_journal", side_effect=journal_then_external_edit), patch.object(
            world.lifecycle, "_write_favorites", wraps=world.lifecycle._write_favorites
        ) as favorite_write:
            with pytest.raises(PoolLifecycleError) as failure:
                if profile == "anima":
                    await world.lifecycle.source(profile, section, "update", source_id, {"prompt": "rejected new text"})
                else:
                    await world.lifecycle.source(profile, section, "update_item", profile, source_id, "rejected new text")
            assert injected
            favorite_write.assert_not_called()

        assert source.path.read_bytes() == source_before
        records = (world.comfy.favorites_data[section]["items"] if profile == "anima" else
                   json.loads(world.extension.path.read_text(encoding="utf-8"))["profiles"][profile][section]["items"])
        by_id = {item["id"]: item for item in records}
        assert len(by_id) == 2
        assert by_id[changed_favorite_id]["nickname"] == "External alias"
        assert by_id[target["item_id"]]["customContent"] == "old text"
        assert by_id[unrelated["item_id"]]["customContent"] == "other text"
        assert failure.value.payload["source_committed"] is False
        assert failure.value.payload["recovery_required"] is change_target
        assert world.lifecycle._journal(profile).exists() is change_target

        if change_target:
            assert failure.value.payload["code"] == "favorite_sync_recovery_required"
            with pytest.raises(PoolLifecycleError) as retry:
                if profile == "anima":
                    await world.lifecycle.source(profile, section, "delete", source_id)
                else:
                    await world.lifecycle.source(profile, section, "delete_item", profile, source_id)
            assert retry.value.payload["recovery_required"] is True
            assert source.path.read_bytes() == source_before
            assert world.lifecycle._journal(profile).exists()
        else:
            assert failure.value.payload["code"] == "favorite_sync_failed"

    asyncio.run(run())


@pytest.mark.parametrize("operation", ["create", "create_group"])
@pytest.mark.parametrize("section", ["pose", "expression"])
def test_new_anima_sources_require_available_favorite_storage_except_local_expression(tmp_path, operation, section):
    async def run():
        world = make_world(tmp_path)
        await save(world)
        assert world.custom.source_completeness == "complete"
        source_before = world.custom.path.read_bytes()
        catalog_before = copy.deepcopy(world.catalog._items)
        favorites_before = copy.deepcopy(world.comfy.favorites_data)
        args = ({"section": section, "title": "New item", "prompt": "new content"},) if operation == "create" else (
            section, {"name": "New group"},
        )
        with patch.object(world.comfy, "favorites", side_effect=ComfyError("offline")) as read_remote, patch.object(
            world.comfy, "save_favorites", side_effect=AssertionError("must not write unavailable favorites")
        ) as write_remote:
            if section == "pose":
                with pytest.raises(PoolLifecycleError) as failure:
                    await world.lifecycle.source("anima", section, operation, *args)
                assert failure.value.payload["code"] == "favorites_unavailable"
                assert failure.value.payload["source_committed"] is False
                assert world.custom.path.read_bytes() == source_before
                assert world.catalog._items == catalog_before
                read_remote.assert_called()
            else:
                result = await world.lifecycle.source("anima", section, operation, *args)
                assert result["favoritesSync"]["state"] == "complete"
                assert world.custom.path.read_bytes() != source_before
                read_remote.assert_not_called()
            write_remote.assert_not_called()
        assert world.comfy.favorites_data == favorites_before
        assert not world.lifecycle._journal("anima").exists()

    asyncio.run(run())
