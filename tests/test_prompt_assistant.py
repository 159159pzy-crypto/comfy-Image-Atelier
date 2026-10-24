import asyncio
import copy
import json
import tempfile
import unittest
from contextlib import asynccontextmanager
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

from aiohttp.test_utils import TestClient, TestServer

from anima_webui.comfy import ComfyError
from anima_webui.prompt_assistant import pool_output_template
from anima_webui.prompt_processing import OUTPUT_NODE
from anima_webui.runner import PROMPT_JOB_TIMEOUT
from anima_webui.server import HISTORY_KEY, MANAGER_KEY, create_app
from anima_webui.workflow import validate_settings
from test_server import APP_DIR, _write_fake_tools
from test_single_backend import SingleComfy


class AssistantComfy(SingleComfy):
    def __init__(self):
        super().__init__()
        self.template_only = True
        self.plugin_error = False
        self.calls = []
        self.services = [
            {"id": "service_1", "name": "My provider", "type": "openai_compatible",
             "api_key": "secret-do-not-return", "api_key_encrypted": "also-secret",
             "base_url": "https://example.invalid/private",
             "llm_models": [{"name": "test/model", "api_key": "nested-secret"}]},
        ]
        self.templates = {
            "expand_custom": {"name": "My template", "content": "Only the user's system instructions.",
                              "category": "Personal", "role": "system"},
        }

    async def wait_for_history(self, prompt_id, should_abort=None, missing_timeout=30):
        result = await super().wait_for_history(prompt_id, should_abort, missing_timeout)
        graph = self.submissions[int(prompt_id) - 1]["prompt"]
        if any("Application output contract" in node.get("inputs", {}).get("custom_rule_content", "")
               for node in graph.values()):
            result["outputs"][OUTPUT_NODE]["text"] = [json.dumps({
                "title": "林间少女", "subtitle": "午后的柔和光线", "prompt": "expanded final prompt",
            }, ensure_ascii=False)]
        return result

    async def _json(self, method, path, **kwargs):
        self.calls.append((method, path))
        if path == "/object_info/PromptExpand":
            required = {"rule": ["COMBO", {"options": ["My template"]}],
                        "llm_service": [["My provider/test/model"]]}
            return {"PromptExpand": {"python_module": "custom_nodes.renamed-prompt-assistant",
                                    "input": {"required": required, "optional": {
                                        **({"template_only": ["BOOLEAN"]} if self.template_only else {})
                                    }}}}
        if path == "/object_info/PreviewAny":
            return {"PreviewAny": {"output_node": True}}
        if self.plugin_error:
            raise ComfyError("server error containing secret-do-not-return")
        if path == "/renamed-prompt-assistant/api/services":
            return {"success": True, "services": copy.deepcopy(self.services)}
        if path == "/renamed-prompt-assistant/api/config/system_prompts":
            return {"expand_prompts": copy.deepcopy(self.templates),
                    "vision_prompts": {"untouched": {"content": "not exposed"}}}
        raise AssertionError(path)


class PromptAssistantTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temp = tempfile.TemporaryDirectory()
        root = Path(self.temp.name)
        self.comfy = AssistantComfy()
        self.app = create_app(app_dir=APP_DIR, data_dir=root / "data", comfy=self.comfy,
                              anima_tools_dir=_write_fake_tools(root))
        self.client = TestClient(TestServer(self.app))
        await self.client.start_server()
        self.manager = self.app[MANAGER_KEY]

    async def asyncTearDown(self):
        if self.comfy.block:
            self.comfy.block.set()
        await self.client.close()
        self.temp.cleanup()

    def request(self, **overrides):
        return {"kind": "rewrite", "model_profile": "krea2", "source_text": "Original input, NASA.\nKeep paragraphs.",
                "service_id": "service_1", "model_name": "test/model", "template_id": "expand_custom", **overrides}

    async def create(self, **overrides):
        response = await self.client.post("/api/prompt-jobs", json=self.request(**overrides))
        self.assertEqual(response.status, 201, await response.text())
        return await response.json()

    async def submitted(self):
        for _ in range(200):
            if self.comfy.submissions:
                return
            await asyncio.sleep(0.001)
        self.fail("rewrite was not submitted")

    async def test_options_discover_renamed_plugin_and_allowlist_all_values(self):
        response = await self.client.get("/api/prompt-assistant/options")
        self.assertEqual(response.status, 200)
        data = await response.json()
        self.assertTrue(data["available"])
        self.assertTrue(data["template_only"])
        self.assertEqual(data["services"][0]["models"], [{"name": "test/model"}])
        self.assertEqual(data["templates"], [{"id": "expand_custom", "name": "My template", "category": "Personal"}])
        serialized = json.dumps(data)
        for private in ("secret", "api_key", "base_url", "not exposed", "system instructions"):
            self.assertNotIn(private, serialized)
        self.assertIn(("GET", "/renamed-prompt-assistant/api/services"), self.comfy.calls)
        self.assertEqual(self.comfy.submissions, [])

    async def test_rewrite_contract_keeps_ui_text_and_frozen_user_content(self):
        job = await self.create()
        await self.manager.wait()
        saved = await self.manager.prompt_jobs.get(job["id"])
        self.assertEqual(saved["kind"], "rewrite")
        self.assertEqual(saved["status"], "completed")
        self.assertEqual(saved["result"], {"text": "expanded final prompt", "pool_item": {
            "title": "林间少女", "subtitle": "午后的柔和光线", "prompt": "expanded final prompt"}})
        self.assertEqual(saved["settings"]["pool_output_version"], 1)
        graph = self.comfy.submissions[0]["prompt"]
        self.assertEqual({node["class_type"] for node in graph.values()}, {"PromptExpand", "PreviewAny"})
        node = graph["studio_prompt_rewrite"]["inputs"]
        self.assertEqual(node["source_text"], self.request()["source_text"])
        self.assertEqual(node["custom_rule_content"], pool_output_template(self.templates_content(), "krea2"))
        self.assertTrue(node["custom_rule"])
        self.assertTrue(node["template_only"])
        self.assertEqual(node["user_prompt"], "")
        self.assertEqual(node["llm_service"], "My provider/test/model")
        self.assertEqual(graph[OUTPUT_NODE]["inputs"]["source"], ["studio_prompt_rewrite", 0])
        self.assertEqual((await self.app[HISTORY_KEY].list_images())["total"], 0)
        self.assertEqual(self.app[HISTORY_KEY].connection.execute("SELECT COUNT(*) FROM batches").fetchone()[0], 0)
        self.assertNotIn("secret", json.dumps(saved))

    def templates_content(self):
        return self.comfy.templates["expand_custom"]["content"]

    async def test_queued_task_keeps_template_snapshot_and_reports_busy(self):
        self.comfy.block = asyncio.Event()
        first = await self.create()
        await self.submitted()
        second = await self.create(model_profile="anima", source_text="Independent second source")
        before = pool_output_template(self.templates_content(), "anima")
        current = await (await self.client.get("/api/batches/current")).json()
        self.assertTrue(self.manager.active())
        self.assertEqual(current["queue"][0]["queue_id"], second["id"])
        self.comfy.templates["expand_custom"]["content"] = "Changed after queueing"
        self.comfy.block.set()
        await self.manager.wait()
        self.assertEqual((await self.manager.prompt_jobs.get(first["id"]))["status"], "completed")
        self.assertEqual(self.comfy.submissions[1]["prompt"]["studio_prompt_rewrite"]["inputs"]["custom_rule_content"], before)

    async def test_queued_task_with_removed_service_errors_without_switching_provider(self):
        self.comfy.block = asyncio.Event()
        first = await self.create()
        await self.submitted()
        second = await self.create()
        self.comfy.services[0]["id"] = "different-service"
        self.comfy.block.set()
        await self.manager.wait()
        self.assertEqual((await self.manager.prompt_jobs.get(first["id"]))["status"], "completed")
        saved = await self.manager.prompt_jobs.get(second["id"])
        self.assertEqual(saved["status"], "error")
        self.assertIn("所选服务或模型已不可用", saved["error"])
        self.assertEqual(len(self.comfy.submissions), 1)

    async def test_queued_task_with_deleted_template_does_not_use_another_template(self):
        self.comfy.block = asyncio.Event()
        first = await self.create()
        await self.submitted()
        second = await self.create()
        self.comfy.templates = {
            "another-template": {"name": "Another template", "content": "Different instructions"},
        }
        self.comfy.block.set()
        await self.manager.wait()
        self.assertEqual((await self.manager.prompt_jobs.get(first["id"]))["status"], "completed")
        saved = await self.manager.prompt_jobs.get(second["id"])
        self.assertEqual(saved["status"], "error")
        self.assertIn("模板已删除", saved["error"])
        self.assertEqual(len(self.comfy.submissions), 1)

    async def test_cancel_running_does_not_cancel_next_task(self):
        self.comfy.block = asyncio.Event()
        first = await self.create()
        await self.submitted()
        second = await self.create()
        response = await self.client.post(f"/api/prompt-jobs/{first['id']}/cancel")
        self.assertEqual(response.status, 200)
        self.comfy.block.set()
        await self.manager.wait()
        self.assertEqual((await self.manager.prompt_jobs.get(first["id"]))["status"], "cancelled")
        self.assertEqual((await self.manager.prompt_jobs.get(second["id"]))["status"], "completed")
        self.assertTrue(self.comfy.cancelled)
        self.assertEqual(set(self.comfy.cancelled), {"1"})

    async def test_timeout_targets_only_owned_job_and_sets_terminal_error(self):
        self.comfy.block = asyncio.Event()
        with patch("anima_webui.runner.PROMPT_JOB_TIMEOUT", 0.5):
            job = await self.create()
            await self.submitted()
            await self.manager.wait()
        saved = await self.manager.prompt_jobs.get(job["id"])
        self.assertEqual(saved["status"], "error")
        self.assertIn("已取消", saved["error"])
        self.assertEqual(set(self.comfy.cancelled), {"1"})
        self.assertEqual((await self.app[HISTORY_KEY].list_images())["total"], 0)

    async def test_execution_timeout_starts_after_configuration_lookup_and_submission(self):
        original_transport = self.manager.prompt_assistant.transport
        original_submit = self.comfy.submit
        original_wait = self.comfy.wait_for_history
        original_timeout = asyncio.timeout
        phases = []
        timeout_active = False

        async def observed_transport(snapshot):
            self.assertFalse(timeout_active)
            phases.append("configuration:start")
            snapshot = await original_transport(snapshot)
            self.assertFalse(timeout_active)
            phases.append("configuration:done")
            return snapshot

        async def observed_submit(graph):
            self.assertFalse(timeout_active)
            phases.append("submission:start")
            prompt_id = await original_submit(graph)
            self.assertFalse(timeout_active)
            phases.append("submission:done")
            return prompt_id

        @asynccontextmanager
        async def observed_timeout(delay):
            nonlocal timeout_active
            self.assertEqual(delay, PROMPT_JOB_TIMEOUT)
            phases.append("execution:start")
            async with original_timeout(delay):
                timeout_active = True
                try:
                    yield
                finally:
                    timeout_active = False
                    phases.append("execution:done")

        async def observed_wait(*args, **kwargs):
            self.assertTrue(timeout_active)
            phases.append("history:wait")
            return await original_wait(*args, **kwargs)

        # Observe the actual budget boundary without making a disk write and
        # event-loop scheduling race against a 100 ms artificial deadline.
        # Keep aiohttp's own connection timeouts outside this observation.
        observed_asyncio = SimpleNamespace(**vars(asyncio))
        observed_asyncio.timeout = observed_timeout
        with patch("anima_webui.runner.asyncio", observed_asyncio), \
                patch.object(self.manager.prompt_assistant, "transport", side_effect=observed_transport), \
                patch.object(self.comfy, "submit", side_effect=observed_submit), \
                patch.object(self.comfy, "wait_for_history", side_effect=observed_wait):
            job = await self.create()
            await self.manager.wait()
        self.assertEqual(phases, [
            "configuration:start", "configuration:done",
            "submission:start", "submission:done",
            "execution:start", "history:wait", "execution:done",
        ])
        saved = await self.manager.prompt_jobs.get(job["id"])
        self.assertEqual(saved["status"], "completed", saved["error"])
        self.assertEqual(saved["result"]["text"], "expanded final prompt")
        self.assertEqual(self.comfy.cancelled, [])

    async def test_prompt_cancel_without_targeted_support_never_globally_interrupts(self):
        calls = []

        async def interrupt():
            calls.append("global")

        self.manager.state = {"kind": "prompt_preview", "prompt_id": "owned-job"}
        with patch.object(self.comfy, "cancel_prompt", None), \
                patch.object(self.comfy, "interrupt", interrupt, create=True):
            await self.manager._interrupt_current()
        self.assertEqual(calls, [])

    async def test_missing_capability_and_plugin_errors_fail_closed_without_secrets(self):
        self.comfy.template_only = False
        options = await (await self.client.get("/api/prompt-assistant/options")).json()
        self.assertFalse(options["available"])
        response = await self.client.post("/api/prompt-jobs", json=self.request())
        self.assertEqual(response.status, 400)
        self.assertEqual(self.comfy.submissions, [])
        self.comfy.template_only = True
        self.comfy.plugin_error = True
        response = await self.client.get("/api/prompt-assistant/options")
        self.assertNotIn("secret", await response.text())
        response = await self.client.post("/api/prompt-jobs", json=self.request())
        self.assertEqual(response.status, 400)
        self.assertNotIn("secret", await response.text())

    async def test_invalid_selection_and_unknown_parameters_never_submit(self):
        for overrides in ({"source_text": ""}, {"model_profile": "other"}, {"service_id": "missing"},
                          {"model_name": "missing"}, {"template_id": "missing"}, {"language": "en"}):
            response = await self.client.post("/api/prompt-jobs", json=self.request(**overrides))
            self.assertEqual(response.status, 400, await response.text())
        self.assertEqual(self.comfy.submissions, [])

    async def test_regular_generation_never_runs_legacy_ai_or_reuses_old_result(self):
        settings = validate_settings({"workbench": "single", "count": 1, "single": {
            "positive_prompt": "authored prompt", "prompt_job_id": "missing-old-job",
            "processed_prompt_override": "old AI result",
            "prompt_processing": {"expand": {"enabled": True, "llm_service": "removed/model"}},
        }})
        response = await self.client.post("/api/batches", json=settings)
        self.assertEqual(response.status, 201, await response.text())
        await self.manager.wait()
        self.assertEqual(len(self.comfy.submissions), 1)
        self.assertNotIn(OUTPUT_NODE, self.comfy.submissions[0]["prompt"])
        image = (await self.app[HISTORY_KEY].list_images())["items"][0]
        self.assertEqual(image["single_runtime"]["positive"], "authored prompt")
        self.assertEqual(image["single_runtime"]["prompt_job_id"], "")

    async def test_rewrite_failure_does_not_generate_or_expose_provider_errors(self):
        with patch.object(self.comfy, "submit", side_effect=ComfyError("secret-provider-token")):
            job = await self.create()
            await self.manager.wait()
        saved = await self.manager.prompt_jobs.get(job["id"])
        self.assertEqual(saved["status"], "error")
        self.assertNotIn("secret", json.dumps(saved))
        self.assertNotIn("secret", json.dumps(self.manager.snapshot()))
        self.assertEqual((await self.app[HISTORY_KEY].list_images())["total"], 0)

    async def test_empty_rewrite_output_is_an_error_without_a_preview_result(self):
        entry = {"outputs": {OUTPUT_NODE: {"text": [" \n "]}},
                 "status": {"status_str": "success", "completed": True}}
        with patch.object(self.comfy, "wait_for_history", return_value=entry):
            job = await self.create()
            await self.manager.wait()
        saved = await self.manager.prompt_jobs.get(job["id"])
        self.assertEqual(saved["status"], "error")
        self.assertIn("非空文本", saved["error"])
        self.assertFalse(saved["result"])
        self.assertEqual((await self.app[HISTORY_KEY].list_images())["total"], 0)

    async def test_invalid_structured_output_fails_without_a_second_model_request(self):
        entry = {"outputs": {OUTPUT_NODE: {"text": ['{"title":"missing prompt"}']}},
                 "status": {"status_str": "success", "completed": True}}
        with patch.object(self.comfy, "wait_for_history", return_value=entry):
            job = await self.create()
            await self.manager.wait()
        saved = await self.manager.prompt_jobs.get(job["id"])
        self.assertEqual(saved["status"], "error")
        self.assertIn("随机池格式无效", saved["error"])
        self.assertFalse(saved["result"])
        self.assertEqual(len(self.comfy.submissions), 1)

    async def test_old_frozen_rewrite_keeps_plain_text_contract(self):
        snapshot = await self.manager.prompt_assistant.prepare(self.request())
        snapshot.pop("pool_output_version")
        snapshot["template_content"] = self.templates_content()
        job = await self.manager.prompt_jobs.create(snapshot, "legacy-text-job")
        await self.manager._begin_prompt_preview(job)
        await self.manager.wait()
        saved = await self.manager.prompt_jobs.get(job["id"])
        self.assertEqual(saved["status"], "completed", saved["error"])
        self.assertEqual(saved["result"], {"text": "expanded final prompt"})

    async def test_frozen_legacy_ai_result_survives_replay_without_processing(self):
        settings = validate_settings({"workbench": "single", "count": 1, "single": {
            "positive_prompt": "original pre-AI source", "prompt_job_id": "deleted-job",
            "prompt_processing": {"expand": {"enabled": True, "llm_service": "deleted/model"}},
        }})
        frozen = {"positive": "frozen AI output",
                  "effective_positive": {"primary": "frozen AI output", "secondary": "frozen AI output"},
                  "negative": "frozen negative", "prompt_job_id": "deleted-job"}
        await self.manager.start(settings, regeneration={
            "mode": "replay", "frozen_positive_prompt": "frozen AI output",
            "frozen_negative_prompt": "frozen negative", "single_runtime": frozen,
        })
        await self.manager.wait()
        self.assertEqual(len(self.comfy.submissions), 1)
        self.assertNotIn(OUTPUT_NODE, self.comfy.submissions[0]["prompt"])
        image = (await self.app[HISTORY_KEY].list_images())["items"][0]
        self.assertEqual(image["single_runtime"], frozen)

    async def test_prompt_rule_api_keeps_profiles_isolated(self):
        response = await self.client.post("/api/prompt-rules", json={
            "model_profile": "krea2", "from": "NASA, Studio_Name", "to": "New prose.",
        })
        self.assertEqual(response.status, 201, await response.text())
        rule_id = (await response.json())["id"]
        listing = await (await self.client.get("/api/prompt-rules?model_profile=krea2")).json()
        self.assertIn(rule_id, [item["id"] for item in listing["items"]])
        listing = await (await self.client.get("/api/prompt-rules")).json()
        self.assertNotIn(rule_id, [item["id"] for item in listing["items"]])
        for profile, expected in (("krea2", "New prose."), ("anima", "nasa, studio name")):
            response = await self.client.post("/api/prompts/normalize", json={
                "model_profile": profile, "fields": {"extra_prompt": "NASA, Studio_Name"},
            })
            self.assertEqual((await response.json())["fields"]["extra_prompt"], expected)
        self.assertEqual((await self.client.delete(f"/api/prompt-rules/{rule_id}")).status, 404)
        response = await self.client.put(f"/api/prompt-rules/{rule_id}", json={
            "model_profile": "krea2", "to": "Updated prose.",
        })
        self.assertEqual(response.status, 200, await response.text())
        response = await self.client.delete(f"/api/prompt-rules/{rule_id}?model_profile=krea2")
        self.assertEqual(response.status, 200, await response.text())
