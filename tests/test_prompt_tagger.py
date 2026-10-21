import asyncio
import copy
import hashlib
import json
import os
import tempfile
import threading
import unittest
from pathlib import Path
from unittest.mock import patch

from anima_webui.comfy import ComfyError
from anima_webui.prompt_tagger import DEFAULTS, PromptTagger, resolve_tagger_directory
from anima_webui.workflow import WorkflowError


def write_model(root, name="custom-model", *, classes=1, arch=False):
    directory = root / name
    directory.mkdir(parents=True, exist_ok=True)
    (directory / "config.json").write_text(json.dumps({"architecture": "test_vit", "num_classes": classes}), encoding="utf-8")
    (directory / "selected_tags.csv").write_text("name,category,best_threshold\nbird,0,0.1\n", encoding="utf-8")
    header = json.dumps({"tensor": {"dtype": "F32", "shape": [1], "data_offsets": [0, 4]}}).encode()
    (directory / "model.safetensors").write_bytes(len(header).to_bytes(8, "little") + header + bytes(4))
    if arch:
        (directory / "timm_arch.py").write_text("raise RuntimeError('MUST NOT EXECUTE')\n", encoding="utf-8")
    return directory


def manifest_entry(directory):
    return {"name": directory.name, "relativePath": directory.name, "revision": "a" * 40,
            "files": [{"path": path.name, "size": path.stat().st_size,
                       "sha256": hashlib.sha256(path.read_bytes()).hexdigest(), "urls": []}
                      for path in sorted(directory.iterdir()) if path.is_file()]}


class TaggerComfy:
    def __init__(self, root):
        self.root = root
        self.names = ["custom-model"]
        self.calls = []
        self.missing = False
        self.fail = False
        self.folders = {"wd_taggers": [str(root / "models" / "wd_taggers")]}
        self.stats = {"system": {"argv": [str(root / "main.py"), "--listen", "127.0.0.1"]}}

    async def _json(self, method, path, **kwargs):
        self.calls.append(path)
        if self.fail:
            raise ComfyError("private-api-key F:/private/path provider-token")
        if path == "/object_info/WDTimmTagger":
            if self.missing:
                return {}
            return {"WDTimmTagger": {"input": {"required": {
                "image": ["IMAGE"], "model_name": [self.names], "dtype": [["auto", "bf16", "fp16"]],
                "general_threshold": ["FLOAT"], "character_threshold": ["FLOAT"], "add_rating": ["BOOLEAN"],
                "exclude_tags": ["STRING"], "batch_size": ["INT"],
            }}}}
        if path == "/object_info/PreviewAny":
            return {"PreviewAny": {"output_node": True}}
        if path == "/internal/folder_paths":
            return copy.deepcopy(self.folders)
        if path == "/system_stats":
            return copy.deepcopy(self.stats)
        raise AssertionError(f"Unexpected endpoint: {path}")


class TaggerDirectoryTests(unittest.TestCase):
    def test_default_and_absolute_overrides(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for argv, expected in [
                ([str(root / "main.py")], root / "models" / "wd_taggers"),
                (["main.py", "--base-directory", str(root)], root / "models" / "wd_taggers"),
                (["main.py", "--models-directory=" + str(root)], root / "wd_taggers"),
                (["main.py", "--base-directory", str(root / "ignored"), "--models-directory", str(root)], root / "wd_taggers"),
            ]:
                with self.subTest(argv=argv):
                    self.assertEqual(resolve_tagger_directory({"wd_taggers": [str(expected)]}, {"system": {"argv": argv}}), expected.resolve())

    def test_ambiguous_relative_missing_mismatch_and_duplicate_overrides_fail(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            expected = root / "models" / "wd_taggers"
            variants = [
                ({"wd_taggers": [str(expected), str(root / "extra")]}, [str(root / "main.py")]),
                ({"wd_taggers": [str(expected)]}, ["main.py"]),
                ({"wd_taggers": [str(expected)]}, [str(root / "main.py"), "--models-directory", "models"]),
                ({"wd_taggers": [str(expected)]}, [str(root / "main.py"), "--base-directory"]),
                ({"wd_taggers": [str(root / "wrong")]}, [str(root / "main.py")]),
                ({"wd_taggers": [str(expected)]}, [str(root / "main.py"), "--base-directory", str(root), "--base-directory", str(root)]),
                ({"wd_taggers": ["models/wd_taggers"]}, [str(root / "main.py")]),
                ({}, [str(root / "main.py")]),
            ]
            for folders, argv in variants:
                with self.subTest(folders=folders, argv=argv), self.assertRaises(WorkflowError):
                    resolve_tagger_directory(folders, {"system": {"argv": argv}})


class PromptTaggerTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.models = self.root / "models" / "wd_taggers"
        self.model = write_model(self.models)
        self.manifest = self.root / "manifest.json"
        self.manifest.write_text('{"schemaVersion":1,"models":[]}', encoding="utf-8")
        self.comfy = TaggerComfy(self.root)
        self.bridge = PromptTagger(self.comfy, manifest_path=self.manifest)

    async def asyncTearDown(self):
        self.temp.cleanup()

    def request(self, **tagger):
        return {"kind": "reverse", "method": "tagger", "image_id": "a" * 64, "model_profile": "anima",
                "tagger": {"model": "custom-model", **tagger}}

    def pin(self, *directories):
        self.manifest.write_text(json.dumps({"schemaVersion": 1, "models": [manifest_entry(path) for path in directories]}), encoding="utf-8")

    async def state(self, name="custom-model"):
        return next(item["state"] for item in (await self.bridge.options())["models"] if item["name"] == name)

    async def test_custom_model_ready_without_visual_provider_or_download_api(self):
        options = await self.bridge.options()
        self.assertTrue(options["available"])
        self.assertEqual(options["models"], [{"name": "custom-model", "state": "ready", "reason": ""}])
        self.assertEqual(options["defaults"], DEFAULTS)
        snapshot = await self.bridge.prepare(self.request(general_threshold=0, character_threshold=1, exclude_tags=""))
        self.assertEqual(snapshot["tagger"], {"model": "custom-model", "general_threshold": 0.0, "character_threshold": 1.0, "exclude_tags": ""})
        self.assertEqual(snapshot["workbench"], "single")
        self.assertNotIn(str(self.root), json.dumps(options))
        self.assertEqual(set(self.comfy.calls), {"/object_info/WDTimmTagger", "/object_info/PreviewAny", "/internal/folder_paths", "/system_stats"})

    async def test_missing_node_and_network_errors_sanitized(self):
        self.comfy.missing = True
        options = await self.bridge.options()
        self.assertFalse(options["available"])
        self.assertIn("WDTimmTagger", options["reason"])
        self.assertNotIn("/internal/folder_paths", self.comfy.calls)
        self.comfy.fail = True
        options = await self.bridge.options()
        self.assertFalse(options["available"])
        self.assertNotIn("private", json.dumps(options))
        with self.assertRaises(WorkflowError) as caught:
            await self.bridge.prepare(self.request())
        self.assertNotIn("provider-token", str(caught.exception))

    async def test_directory_ambiguity_marks_only_models_unverifiable_and_no_paths(self):
        self.comfy.folders["wd_taggers"].append(str(self.root / "extra"))
        options = await self.bridge.options()
        self.assertFalse(options["available"])
        self.assertEqual(options["models"][0]["state"], "unverifiable")
        self.assertNotIn(str(self.root), json.dumps(options))
        with self.assertRaises(WorkflowError):
            await self.bridge.prepare(self.request())

    async def test_missing_partial_and_other_directory_config_not_accepted(self):
        self.comfy.names += ["missing", "partial"]
        (self.models / "partial").mkdir()
        (self.models / "partial" / "model.safetensors").write_bytes((self.model / "model.safetensors").read_bytes())
        self.assertEqual(await self.state("missing"), "missing")
        self.assertEqual(await self.state("partial"), "incomplete")
        with self.assertRaises(WorkflowError):
            await self.bridge.prepare(self.request(model="partial"))

    async def test_known_models_require_manifest_and_exact_sha(self):
        name = "pixai-tagger-v0.9-timm"
        model = write_model(self.models, name)
        self.comfy.names = [name]
        self.assertEqual(await self.state(name), "unverifiable")
        self.pin(model)
        self.assertEqual(await self.state(name), "ready")
        snapshot = await self.bridge.prepare(self.request(model=name))
        weights = model / "model.safetensors"
        data = weights.read_bytes()
        weights.write_bytes(data[:-1] + b"\1")
        self.assertEqual(await self.state(name), "invalid")
        with self.assertRaises(WorkflowError):
            await self.bridge.transport(snapshot)

    @unittest.skipUnless(os.name == "nt", "Windows model directory aliases")
    async def test_case_variant_preset_keeps_runtime_name_but_requires_locked_hashes(self):
        canonical = "pixai-tagger-v0.9-timm"
        runtime_name = canonical.upper()
        model = write_model(self.models, canonical)
        self.comfy.names = [runtime_name]
        self.assertEqual(await self.state(runtime_name), "unverifiable")
        self.pin(model)
        self.assertEqual(await self.state(runtime_name), "ready")
        snapshot = await self.bridge.prepare(self.request(model=runtime_name))
        self.assertEqual(snapshot["tagger"]["model"], runtime_name)
        self.assertEqual((await self.bridge.transport(snapshot))["tagger"]["model"], runtime_name)
        weights = model / "model.safetensors"
        weights.write_bytes(weights.read_bytes()[:-1] + b"\1")
        self.assertEqual(await self.state(runtime_name), "invalid")
        with self.assertRaises(WorkflowError):
            await self.bridge.transport(snapshot)

    @unittest.skipUnless(os.name == "nt", "Windows model directory aliases")
    async def test_case_variant_pixai10_requires_locked_architecture(self):
        canonical = "pixai-tagger-v1.0-timm"
        runtime_name = canonical.upper()
        model = write_model(self.models, canonical, arch=True)
        self.comfy.names = [runtime_name]
        self.pin(model)
        self.assertEqual(await self.state(runtime_name), "ready")
        (model / "timm_arch.py").unlink()
        self.assertEqual(await self.state(runtime_name), "incomplete")
        # A malformed lock cannot downgrade the uppercase alias to custom.
        self.pin(model)
        self.assertEqual(await self.state(runtime_name), "unverifiable")

    async def test_pixai10_requires_arch_file_and_never_executes_it(self):
        name = "pixai-tagger-v1.0-timm"
        model = write_model(self.models, name, arch=True)
        self.comfy.names = [name]
        self.pin(model)
        self.assertEqual(await self.state(name), "ready")
        (model / "timm_arch.py").unlink()
        self.assertEqual(await self.state(name), "incomplete")

    async def test_frozen_parameters_unchanged_and_removed_model_rechecked(self):
        request = self.request(general_threshold=.2, exclude_tags="watermark")
        snapshot = await self.bridge.prepare(request)
        request["tagger"]["general_threshold"] = .9
        resolved = await self.bridge.transport(snapshot)
        self.assertEqual(resolved["tagger"]["general_threshold"], .2)
        self.assertEqual(resolved["tagger"]["exclude_tags"], "watermark")
        resolved["tagger"]["exclude_tags"] = "changed"
        self.assertEqual(snapshot["tagger"]["exclude_tags"], "watermark")
        self.comfy.names = []
        with self.assertRaises(WorkflowError):
            await self.bridge.transport(snapshot)

    async def test_strict_parameter_validation_before_any_runtime_access(self):
        for settings in [{"general_threshold": True}, {"general_threshold": float("nan")},
                         {"general_threshold": 10**1000},
                         {"character_threshold": float("inf")}, {"general_threshold": -.1},
                         {"character_threshold": 1.1}, {"general_threshold": "0.35"},
                         {"exclude_tags": "x" * 20001}, {"exclude_tags": []},
                         {"model": "../other"}, {"model": "C:\\private"}, {"dtype": "fp16"}]:
            with self.subTest(settings=settings), self.assertRaises(WorkflowError):
                await self.bridge.prepare(self.request(**settings))
        self.assertEqual(self.comfy.calls, [])

    async def test_invalid_structures_do_not_pass_nonempty_file_checks(self):
        for filename, content in [
            ("config.json", b"{}"), ("config.json", b"not json"),
            ("selected_tags.csv", b"wrong,columns\nbird,0\n"),
            ("selected_tags.csv", b"name,category\nbird,invalid\n"),
            ("selected_tags.csv", b"name,category,best_threshold\nbird,0,nan\n"),
            ("model.safetensors", b"some nonempty bytes"),
            ("model.safetensors", (2**63).to_bytes(8, "little")),
        ]:
            with self.subTest(filename=filename, content=content):
                write_model(self.models)
                (self.model / filename).write_bytes(content)
                self.assertEqual(await self.state(), "invalid")

    async def test_truncated_safetensors_and_label_count_mismatch(self):
        weights = self.model / "model.safetensors"
        weights.write_bytes(weights.read_bytes()[:-1])
        self.assertEqual(await self.state(), "invalid")

    async def test_unlocked_arch_or_preprocess_changes_known_model_behavior_and_fails(self):
        self.pin(self.model)
        (self.model / "timm_arch.py").write_text("raise RuntimeError('must not run')", encoding="utf-8")
        self.assertEqual(await self.state(), "invalid")
        (self.model / "timm_arch.py").unlink()
        (self.model / "preprocess.json").write_text("{}", encoding="utf-8")
        self.assertEqual(await self.state(), "invalid")

    async def test_custom_arch_and_preprocess_are_checked_without_execution(self):
        (self.model / "timm_arch.py").write_text("raise RuntimeError('must not run')", encoding="utf-8")
        (self.model / "preprocess.json").write_text("{}", encoding="utf-8")
        self.assertEqual(await self.state(), "ready")
        (self.model / "preprocess.json").write_text("not json", encoding="utf-8")
        self.assertEqual(await self.state(), "invalid")

    async def test_tensor_shape_and_payload_must_match(self):
        header = json.dumps({"tensor": {"dtype": "F32", "shape": [100], "data_offsets": [0, 4]}}).encode()
        (self.model / "model.safetensors").write_bytes(len(header).to_bytes(8, "little") + header + bytes(4))
        self.assertEqual(await self.state(), "invalid")
        write_model(self.models, classes=2)
        self.assertEqual(await self.state(), "invalid")

    async def test_expensive_checks_off_event_loop_and_cached_by_stat(self):
        self.pin(self.model)
        loop_thread = threading.get_ident()
        seen_threads = []
        original = self.bridge._hash

        def checked(path):
            seen_threads.append(threading.get_ident())
            return original(path)

        with patch.object(self.bridge, "_hash", side_effect=checked):
            self.assertEqual(await self.state(), "ready")
            self.assertEqual(await self.state(), "ready")
        self.assertTrue(seen_threads)
        self.assertTrue(all(thread != loop_thread for thread in seen_threads))
        self.assertEqual(len(self.bridge._hashes), 3)
        await asyncio.gather(self.bridge.options(), self.bridge.options())
        self.assertEqual(len(self.bridge._hashes), 3)

    async def test_bad_manifest_cannot_turn_known_model_into_unchecked_custom(self):
        name = "pixai-tagger-v0.9-timm"
        model = write_model(self.models, name)
        self.comfy.names = [name]
        for invalid in ["not json", '{"schemaVersion":1,"models":[]}',
                        json.dumps({"schemaVersion": 1, "models": [{**manifest_entry(model), "files": [{"path": "../../config.json", "size": 4, "sha256": "a" * 64}]}]})]:
            self.manifest.write_text(invalid, encoding="utf-8")
            self.assertEqual(await self.state(name), "unverifiable")

    async def test_dangerous_live_names_filtered(self):
        self.comfy.names += ["..", "a/b", "C:\\secret", "bad\x00name", "custom-model"]
        self.assertEqual([item["name"] for item in (await self.bridge.options())["models"]], ["custom-model"])
