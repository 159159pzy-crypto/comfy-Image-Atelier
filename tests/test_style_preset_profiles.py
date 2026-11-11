import asyncio
import copy

import pytest

from anima_webui.style_presets import StylePresetStore
from anima_webui.workflow import WorkflowError


def test_preset_update_rejects_profile_changes_without_mutating_memory_or_disk(tmp_path):
    async def run():
        path = tmp_path / "presets.json"
        store = StylePresetStore(path)
        for profile, other in (("anima", "krea2"), ("krea2", "anima")):
            item = await store.create({"name": profile, "model_profile": profile, "settings": {"model_profile": profile}})
            for patch in (
                {"favorite": True, "model_profile": other},
                {"name": "changed", "settings": {"model_profile": other}},
                {"model_profile": profile, "settings": {"model_profile": other}},
            ):
                before = copy.deepcopy(store.items)
                raw = path.read_bytes()
                with pytest.raises(WorkflowError, match="模型档案"):
                    await store.update(item["id"], patch)
                assert store.items == before
                assert path.read_bytes() == raw
    asyncio.run(run())


def test_krea_updates_without_profile_keep_ownership_and_same_profile_remains_supported(tmp_path):
    async def run():
        store = StylePresetStore(tmp_path / "presets.json")
        item = await store.create({"name": "Krea", "model_profile": "krea2", "settings": {"model_profile": "krea2"}})
        updated = await store.update(item["id"], {"favorite": True})
        assert updated["settings"]["model_profile"] == "krea2"
        updated = await store.update(item["id"], {"include": {"size": True}, "settings": {"width": 896}})
        assert updated["settings"]["model_profile"] == "krea2"
        updated = await store.update(item["id"], {"name": "Renamed", "model_profile": "krea2"})
        assert updated["name"] == "Renamed"
        assert updated["settings"]["width"] == 896
    asyncio.run(run())
