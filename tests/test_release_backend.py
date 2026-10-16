"""Release regressions using isolated HTTP/storage and a non-inference Comfy fake."""
import asyncio
import copy
from contextlib import asynccontextmanager

import pytest
from aiohttp.test_utils import TestClient, TestServer

from anima_webui.comfy import ComfyError
from anima_webui.prompt_processing import OUTPUT_NODE, prompt_fingerprint
from anima_webui.server import HISTORY_KEY, MANAGER_KEY, create_app
from anima_webui.single_workbench import single_resource_issues
from anima_webui.workflow import WorkflowTemplates, validate_settings
from test_server import APP_DIR, _write_fake_tools
from test_single_backend import SingleComfy


@asynccontextmanager
async def studio(tmp_path, comfy=None):
    comfy = comfy or SingleComfy()
    app = create_app(app_dir=APP_DIR, data_dir=tmp_path / "data", comfy=comfy,
                     anima_tools_dir=_write_fake_tools(tmp_path))
    client = TestClient(TestServer(app))
    await client.start_server()
    try:
        yield app, client, comfy
    finally:
        await client.close()


def settings(**overrides):
    return validate_settings({"workbench": "single", "single": {"positive_prompt": "release portrait"}, **overrides})


@pytest.mark.parametrize("kind", ["generation", "prompt_preview"])
@pytest.mark.parametrize("clear_queue", [False, True])
def test_late_stop_preserves_completion_and_honors_queue_choice(tmp_path, kind, clear_queue):
    async def run():
        async with studio(tmp_path) as (app, _client, comfy):
            manager, history = app[MANAGER_KEY], app[HISTORY_KEY]
            entered, release = asyncio.Event(), asyncio.Event()
            store = history if kind == "generation" else manager.prompt_jobs
            method = "update_batch" if kind == "generation" else "update"
            original = getattr(store, method)

            async def delayed_update(job_id, **kwargs):
                result = await original(job_id, **kwargs)
                if kwargs.get("status") == "completed":
                    entered.set()
                    await release.wait()
                return result

            setattr(store, method, delayed_update)
            try:
                first = await (manager.start(settings()) if kind == "generation"
                               else manager.start_prompt_preview(settings()))
                await asyncio.wait_for(entered.wait(), 2)
                pending = await manager.start_prompt_preview(settings())
                await manager.request_stop(first["id"], clear_queue=clear_queue)
            finally:
                release.set()
                await manager.wait()
            assert manager.snapshot()["status"] == "completed"
            persisted = await (history.get_batch(first["id"]) if kind == "generation"
                               else manager.prompt_jobs.get(first["id"]))
            assert persisted["status"] == "completed"
            assert (await manager.prompt_jobs.get(pending["id"]))["status"] == ("cancelled" if clear_queue else "completed")
            assert manager.queue == []
            assert comfy.cancelled == []
    asyncio.run(run())


@pytest.mark.parametrize("active", ["primary", "secondary"])
def test_unused_lora_stack_is_preserved_without_blocking_generation_or_replay(tmp_path, active):
    async def run():
        async with studio(tmp_path) as (app, client, comfy):
            dormant = [{"filename": "removed.safetensors", "enabled": True, "strength": 0.7}]
            payload = settings()
            payload["single"]["normal_pipeline"] = active
            if active == "primary":
                payload["single"].update(loras_secondary=dormant, secondary_managed_triggers=["saved dormant trigger"])
            else:
                payload.update(loras=dormant, lora_managed_triggers=["saved dormant trigger"])
            response = await client.post("/api/batches", json=payload)
            assert response.status == 201, await response.text()
            await app[MANAGER_KEY].wait()
            saved = (await app[HISTORY_KEY].list_images())["items"][0]
            saved_stack = saved["settings"]["single"] if active == "primary" else saved["settings"]
            assert saved_stack["loras_secondary" if active == "primary" else "loras"] == dormant
            assert saved_stack["secondary_managed_triggers" if active == "primary" else "lora_managed_triggers"] == ["saved dormant trigger"]
            assert ("243" if active == "primary" else "72") not in comfy.submissions[-1]["prompt"]
            assert not await app[MANAGER_KEY].resource_issues(saved["settings"])
            options = await (await client.get(f"/api/history/{saved['id']}/regeneration-options")).json()
            assert options["modes"]["replay"]["available"]
            replay = await client.post(f"/api/history/{saved['id']}/regenerate", json={"mode": "replay"})
            assert replay.status == 201, await replay.text()
            await app[MANAGER_KEY].wait()
            assert app[MANAGER_KEY].snapshot()["status"] == "completed"
    asyncio.run(run())


@pytest.mark.parametrize("stage", ["normal", "dual_first", "dual_second", "latent", "face"])
def test_every_used_secondary_lora_stack_is_still_validated(tmp_path, stage):
    async def run():
        async with studio(tmp_path) as (app, client, comfy):
            payload = settings()
            payload["single"]["loras_secondary"] = [{"filename": "missing.safetensors", "enabled": True, "strength": 1}]
            if stage == "normal":
                payload["single"]["normal_pipeline"] = "secondary"
            elif stage.startswith("dual_"):
                payload["base_mode"] = "dual_sampler"
                payload["single"]["dual"][stage.removeprefix("dual_") + "_pipeline"] = "secondary"
            elif stage == "latent":
                payload["upscalers"]["latent"].update(enabled=True, second_sampler={"enabled": True})
            else:
                payload["upscalers"]["face_refine"]["enabled"] = True
            response = await client.post("/api/batches", json=payload)
            assert response.status == 400
            assert "LoRA" in (await response.json())["error"]
            assert comfy.submissions == []
            assert app[MANAGER_KEY].snapshot() is None
    asyncio.run(run())


def test_latent_resize_without_resampling_does_not_use_secondary_stack(tmp_path):
    async def run():
        async with studio(tmp_path) as (app, client, comfy):
            payload = settings()
            payload["single"]["loras_secondary"] = [{"filename": "missing.safetensors", "enabled": True, "strength": 1}]
            payload["upscalers"]["latent"].update(enabled=True, second_sampler={"enabled": False})
            response = await client.post("/api/batches", json=payload)
            assert response.status == 201, await response.text()
            await app[MANAGER_KEY].wait()
            assert "243" not in comfy.submissions[-1]["prompt"]
            assert app[MANAGER_KEY].snapshot()["status"] == "completed"
    asyncio.run(run())


@pytest.mark.parametrize("active", ["primary", "secondary"])
def test_dormant_zero_negative_does_not_require_an_unused_node(active):
    payload = settings()
    payload["single"]["normal_pipeline"] = active
    payload["single"]["secondary_zero_negative" if active == "primary" else "zero_negative"] = True
    graph = WorkflowTemplates.load(APP_DIR / "templates").submission(payload, 1, 1, "release", "client", 1)["prompt"]
    installed_nodes = {node["class_type"] for node in graph.values()}
    assert "ConditioningZeroOut" not in installed_nodes
    assert single_resource_issues(payload, {"node_types": sorted(installed_nodes)}) == []


class StrictPromptComfy(SingleComfy):
    def __init__(self, rules, modern):
        super().__init__()
        self.rules = rules
        self.modern = modern

    async def object_info(self):
        info = await super().object_info()
        info["PromptExpand"]["input"]["required"]["rule"] = (
            ["COMBO", {"options": self.rules, "default": self.rules[-1] if self.rules else ""}]
            if self.modern else [self.rules]
        )
        return info

    async def submit(self, payload):
        for node in payload["prompt"].values():
            if node["class_type"] == "PromptExpand" and node["inputs"]["rule"] not in self.rules:
                raise ComfyError("ComfyUI rule: Value not in list")
        return await super().submit(payload)


@pytest.mark.parametrize("modern", [False, True])
def test_custom_prompt_rule_uses_live_placeholder_without_changing_saved_settings(tmp_path, modern):
    async def run():
        comfy = StrictPromptComfy(["current rule", "other rule"], modern)
        async with studio(tmp_path, comfy) as (app, client, _comfy):
            payload = settings(single={"positive_prompt": "portrait", "prompt_processing": {"oc": {
                "enabled": True, "rule": "removed rule", "llm_service": "test/model", "character": "OC character",
            }}})
            original = copy.deepcopy(payload)
            response = await client.post("/api/prompt-jobs", json={"settings": payload})
            assert response.status == 201, await response.text()
            job_id = (await response.json())["id"]
            await app[MANAGER_KEY].wait()
            job = await app[MANAGER_KEY].prompt_jobs.get(job_id)
            assert job["status"] == "completed", job
            assert job["settings"] == original
            assert job["input_fingerprint"] == prompt_fingerprint(original)
            assert comfy.submissions[0]["prompt"]["studio_prompt_oc"]["inputs"]["rule"] in comfy.rules
            payload["single"]["prompt_job_id"] = job_id
            response = await client.post("/api/batches", json=payload)
            assert response.status == 201, await response.text()
            await app[MANAGER_KEY].wait()
            assert app[MANAGER_KEY].snapshot()["status"] == "completed"
            assert sum(OUTPUT_NODE in item["prompt"] for item in comfy.submissions) == 1
    asyncio.run(run())


@pytest.mark.parametrize("custom,rules", [(False, ["available rule"]), (True, [])])
def test_invalid_prompt_rule_fails_before_creating_jobs(tmp_path, custom, rules):
    async def run():
        async with studio(tmp_path, StrictPromptComfy(rules, True)) as (app, client, comfy):
            payload = settings(single={"positive_prompt": "portrait", "prompt_processing": {"expand": {
                "enabled": True, "rule": "removed rule", "custom_rule": custom,
                "custom_rule_content": "custom instruction", "llm_service": "test/model",
            }}})
            response = await client.post("/api/prompt-jobs", json={"settings": payload})
            assert response.status == 400, await response.text()
            assert app[HISTORY_KEY].connection.execute("SELECT COUNT(*) FROM prompt_jobs").fetchone()[0] == 0
            assert comfy.submissions == []
    asyncio.run(run())


@pytest.mark.parametrize("path", [("base_mode",), ("character_detail",), *[
    ("pools", section, "mode") for section in ("character", "clothing", "pose", "background", "expression")
]])
@pytest.mark.parametrize("bad", [[], {}])
def test_malformed_enum_types_return_400_without_starting_work(tmp_path, path, bad):
    async def run():
        async with studio(tmp_path) as (app, client, comfy):
            payload = settings()
            target = payload
            for key in path[:-1]:
                target = target[key]
            target[path[-1]] = bad
            for endpoint, body in (("/api/batches", payload), ("/api/prompt-jobs", {"settings": payload})):
                response = await client.post(endpoint, json=body)
                assert response.status == 400, await response.text()
                assert (await response.json())["error"]
            assert comfy.classification_calls == []
            assert comfy.submissions == []
            assert app[MANAGER_KEY].snapshot() is None
            assert app[HISTORY_KEY].connection.execute("SELECT COUNT(*) FROM prompt_jobs").fetchone()[0] == 0
    asyncio.run(run())


@pytest.mark.parametrize("bad", [[], {}])
def test_malformed_regeneration_mode_does_not_create_a_new_batch(tmp_path, bad):
    async def run():
        async with studio(tmp_path) as (app, client, comfy):
            await app[MANAGER_KEY].start(settings())
            await app[MANAGER_KEY].wait()
            original = app[MANAGER_KEY].snapshot()
            saved = (await app[HISTORY_KEY].list_images())["items"][0]
            response = await client.post(f"/api/history/{saved['id']}/regenerate", json={"mode": bad})
            assert response.status == 400, await response.text()
            assert "mode" in (await response.json())["error"]
            assert app[MANAGER_KEY].snapshot() == original
            assert app[MANAGER_KEY].queue == []
            assert len(comfy.submissions) == 1
    asyncio.run(run())
