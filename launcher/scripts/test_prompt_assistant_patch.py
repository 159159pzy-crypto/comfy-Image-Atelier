"""Offline acceptance tests for the exact upstream compatibility patch."""
import ast
import asyncio
import copy
import json
from pathlib import Path
import shutil
import tempfile
from types import SimpleNamespace
import unittest

from patch_prompt_assistant import BACKUP, HASHES, PIN, inspect, patch, transform

FIXTURE = Path(__file__).resolve().parents[1] / "tests/fixtures/prompt-assistant/pinned"


class PatchTests(unittest.TestCase):
    def test_compatibility_source_matches_installer_manifest(self):
        manifest_path = Path(__file__).resolve().parents[1] / "manifests/launcher-manifest.json"
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        source = next(item for item in manifest["nodes"] if item["id"] == "prompt-assistant")
        self.assertEqual(source["commit"], PIN)

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name) / "plugin"
        shutil.copytree(FIXTURE, self.root)

    def tearDown(self):
        self.temp.cleanup()

    def test_patch_is_backed_up_idempotent_and_check_is_read_only(self):
        before = {rel: (self.root / rel).read_bytes() for rel in HASHES}
        self.assertFalse(patch(self.root)["patched"])
        self.assertFalse((self.root / BACKUP).exists())
        self.assertTrue(patch(self.root, True)["changed"])
        after = {rel: (self.root / rel).read_bytes() for rel in HASHES}
        self.assertFalse(patch(self.root, True)["changed"])
        self.assertTrue(patch(self.root)["patched"])
        self.assertEqual(after, {rel: (self.root / rel).read_bytes() for rel in HASHES})
        for rel in HASHES:
            self.assertEqual(before[rel], (self.root / BACKUP / rel).read_bytes())

    def test_unknown_source_rejected_before_any_write(self):
        path = self.root / "services/llm.py"
        path.write_text(path.read_text(encoding="utf-8") + "\n# user change\n", encoding="utf-8")
        before = (self.root / "node/expand_node.py").read_bytes()
        with self.assertRaisesRegex(ValueError, "Unsupported"):
            patch(self.root, True)
        self.assertEqual(before, (self.root / "node/expand_node.py").read_bytes())
        self.assertFalse((self.root / BACKUP).exists())

    def test_windows_newlines_and_bom_are_preserved_in_backup(self):
        originals = {}
        for relative in HASHES:
            path = self.root / relative
            originals[relative] = b"\xef\xbb\xbf" + path.read_bytes().replace(b"\n", b"\r\n")
            path.write_bytes(originals[relative])
        self.assertTrue(patch(self.root, True)["patched"])
        for relative, original in originals.items():
            self.assertEqual((self.root / BACKUP / relative).read_bytes(), original)
        self.assertTrue(patch(self.root)["patched"])

    def test_partial_application_resumes_and_modified_patch_is_rejected(self):
        patch(self.root, True)
        original = self.root / BACKUP / "services/llm.py"
        shutil.copyfile(original, self.root / "services/llm.py")
        stale = self.root / "services/llm.py.anima-template-only.tmp"
        stale.write_text(transform("services/llm.py", original.read_text(encoding="utf-8")), encoding="utf-8", newline="\n")
        self.assertTrue(patch(self.root, True)["changed"])
        self.assertFalse(stale.exists())
        path = self.root / "node/expand_node.py"
        path.write_text(path.read_text(encoding="utf-8") + "\n# edit\n", encoding="utf-8")
        with self.assertRaises(ValueError):
            inspect(self.root)

    def test_template_only_reaches_node_and_affects_cache(self):
        source = transform("node/expand_node.py", (self.root / "node/expand_node.py").read_text(encoding="utf-8"))
        tree = ast.parse(source)
        methods = {item.name: item for item in tree.body[-1].body if isinstance(item, ast.FunctionDef)}
        for name in ("execute", "fingerprint_inputs"):
            self.assertEqual(methods[name].args.args[-1].arg, "template_only")
            self.assertIs(methods[name].args.defaults[-1].value, False)
        call = next(item for item in ast.walk(methods["execute"]) if isinstance(item, ast.Call) and any(k.arg == "system_message_override" for k in item.keywords))
        self.assertEqual(next(k.value.id for k in call.keywords if k.arg == "template_only"), "template_only")
        fingerprint = copy.deepcopy(methods["fingerprint_inputs"])
        fingerprint.decorator_list = []
        namespace = {"hashlib": __import__("hashlib")}
        exec(compile(ast.fix_missing_locations(ast.Module(body=[fingerprint], type_ignores=[])), "patched-node", "exec"), namespace)
        self.assertNotEqual(namespace["fingerprint_inputs"](None, template_only=False), namespace["fingerprint_inputs"](None, template_only=True))

    def test_real_expand_method_sends_only_selected_system_and_user(self):
        source = transform("services/llm.py", (self.root / "services/llm.py").read_text(encoding="utf-8"))
        method = next(node for node in ast.walk(ast.parse(source)) if isinstance(node, ast.AsyncFunctionDef) and node.name == "expand_prompt")
        # The actual method runs with transport/config dependencies injected; no
        # ComfyUI, provider credentials or network are involved.
        class Imports(ast.NodeTransformer):
            def visit_ImportFrom(self, node):
                return None
        method = Imports().visit(copy.deepcopy(method))
        method.decorator_list = []
        seen = []

        async def transport(**kwargs):
            seen.append(kwargs["messages"])
            return {"success": True, "content": "rewritten"}

        for provider_type in ("openai_compatible", "ollama"):
            service = {"type": provider_type, "disable_thinking": True}
            namespace = {
                "Optional": __import__("typing").Optional, "Callable": __import__("typing").Callable,
                "Dict": __import__("typing").Dict, "Any": __import__("typing").Any,
                "LLMService": SimpleNamespace(
                    get_provider_display_name=lambda _: "Test",
                    _is_chinese=lambda _: True,
                    _http_request_chat_completions=transport, _call_ollama_native=transport),
                "config_manager": SimpleNamespace(get_service=lambda _: service),
                "build_thinking_suppression": lambda *a, **k: {"think": False},
                "format_model_with_thinking": lambda *a: "model",
                "should_append_no_thinking_instruction": lambda *a: True,
                "postprocess_model_output": lambda content, **k: (True, content),
                "format_api_error": lambda error, _: str(error),
                "TASK_EXPAND": "expand",
            }
            exec(compile(ast.fix_missing_locations(ast.Module(body=[method], type_ignores=[])), "patched-llm", "exec"), namespace)
            kwargs = dict(prompt="原文", custom_provider="provider", custom_provider_config={"model": "test", "base_url": "http://localhost:11434"},
                          system_message_override={"role": "system", "content": "User-selected instructions"})
            result = asyncio.run(namespace["expand_prompt"](**kwargs, template_only=True))
            self.assertTrue(result["success"], result)
            self.assertEqual(seen[-1], [kwargs["system_message_override"], {"role": "user", "content": "原文"}])
            result = asyncio.run(namespace["expand_prompt"](**kwargs))
            self.assertTrue(result["success"], result)
            self.assertEqual(len(seen[-1]), 3)
            self.assertIn("请用中文回答", seen[-1][0]["content"])
            self.assertIn("<think>", seen[-1][0]["content"])


if __name__ == "__main__":
    unittest.main()
