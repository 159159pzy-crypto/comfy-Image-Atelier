from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from anima_webui.persistence import StorageReadOnlyError
from anima_webui.prompt_rules import PromptRuleStore
from anima_webui.style_presets import preset_settings
from anima_webui.workflow import WorkflowError, validate_settings


class KreaPromptRulesTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name) / "rules.json"
        self.store = PromptRuleStore(self.path)

    def normalize(self, text, **kwargs):
        return self.store.normalize_text(text, "positive", model_profile="krea2", **kwargs)[0]

    def test_prose_roundtrip_and_whitespace_idempotence(self):
        cases = [
            'A Portrait of Alice, Cinematic Light',
            '2026, NASA, iPhone, Studio_Name, SCORE 9',
            'red, red, blue',
            '第一段，保留中文标点。\n\n第二段。',
            'The sign reads "Hello, World".\n\n(Blue_Hair:1.2), @Exact_LoRA',
            '  First paragraph.  \r\n\r\n  Keep indentation. \t\r\nLast line.  ',
        ]
        for text in cases:
            expected = "\n".join(line.rstrip() for line in text.replace("\r\n", "\n").split("\n")).strip()
            with self.subTest(text=text):
                value = self.normalize(text)
                self.assertEqual(value, expected)
                self.assertEqual(self.normalize(value), value)

    async def test_turning_off_cleanup_is_a_true_noop(self):
        await self.store.update("natural-whitespace", {"enabled": False}, model_profile="krea2")
        raw = " \r\nAlice, Alice  \r\nNASA \t"
        self.assertEqual(self.normalize(raw), raw)
        self.assertEqual(self.store.normalize_text("Blue_Hair", "positive")[0], "blue hair")
        reopened = PromptRuleStore(self.path)
        self.assertFalse(reopened.list("krea2")["items"][0]["enabled"])

    async def test_profile_crud_isolation_and_whole_text_exact_matching(self):
        first = await self.store.create({"from": "Alice, NASA", "to": "Portrait of Alice.\n\nNASA",
                                         "scopes": ["positive"], "model_profile": "krea2"})
        await self.store.create({"from": first["to"], "to": "Cascaded", "model_profile": "krea2"})
        self.assertEqual(self.normalize("Alice, NASA"), first["to"])
        self.assertEqual(self.normalize("alice, nasa"), "alice, nasa")
        self.assertEqual(self.normalize("Alice, NASA, more"), "Alice, NASA, more")
        self.assertEqual(self.normalize("Alice, NASA", apply_custom=False), "Alice, NASA")
        self.assertEqual(self.store.normalize_text("Alice, NASA", "positive")[0], "alice, nasa")
        with self.assertRaises(KeyError):
            await self.store.update(first["id"], {"to": "Wrong profile"})
        self.assertFalse(await self.store.delete(first["id"]))
        reopened = PromptRuleStore(self.path)
        self.assertEqual(reopened.list("krea2")["count"], 3)
        self.assertEqual(reopened.list()["count"], 7)
        self.assertTrue(await reopened.delete(first["id"], "krea2"))

    async def test_custom_lora_rules_do_not_rewrite_fragments(self):
        await self.store.create({"from": "Exact_LoRA", "to": "New_LoRA", "scopes": ["lora"],
                                 "model_profile": "krea2"})
        result = self.store.normalize_fields(
            {"extra_prompt": "A Portrait, Exact_LoRA", "manual_artist": "by Artist_Name"},
            ["Exact_LoRA"], model_profile="krea2",
        )
        self.assertEqual(result["fields"]["extra_prompt"], "A Portrait, Exact_LoRA")
        self.assertEqual(result["fields"]["manual_artist"], "by Artist_Name")
        settings, _ = self.store.normalize_settings({
            "model_profile": "krea2", "extra_prompt": "Exact_LoRA",
            "lora_managed_triggers": ["Exact_LoRA", "Exact_LoRA"],
        })
        self.assertEqual(settings["extra_prompt"], "New_LoRA")
        self.assertEqual(settings["lora_managed_triggers"], ["New_LoRA"])

    async def test_existing_v1_rules_migrate_only_to_anima_with_original_backup(self):
        original = {
            "version": 1, "disabledBuiltins": ["lowercase-tags"],
            "rules": [{"id": "prompt_rule_old", "from": "Alice", "to": "Bob",
                       "scopes": ["positive"], "enabled": True}],
        }
        self.path.write_text(json.dumps(original), encoding="utf-8")
        store = PromptRuleStore(self.path)
        self.assertEqual(json.loads(self.path.with_name("rules.json.v1.bak").read_text()), original)
        self.assertEqual(json.loads(self.path.read_text())["version"], 2)
        self.assertEqual(store.normalize_text("Alice", "positive")[0], "Bob")
        self.assertEqual(store.normalize_text("Alice", "positive", model_profile="krea2")[0], "Alice")
        PromptRuleStore(self.path)
        self.assertFalse(self.path.with_name("rules.json.v1-1.bak").exists())

    async def test_failed_migration_preserves_file_and_readable_rules(self):
        raw = '{"version":1,"disabledBuiltins":["lowercase-tags"],"rules":[]}'
        self.path.write_text(raw)
        with patch("anima_webui.prompt_rules.backup_for_migration", side_effect=OSError("denied")):
            store = PromptRuleStore(self.path)
        self.assertEqual(self.path.read_text(), raw)
        self.assertTrue(store.storage_status()["read_only"])
        self.assertEqual(store.normalize_text("NASA", "positive")[0], "NASA")
        with self.assertRaises(StorageReadOnlyError):
            await store.update("lowercase-tags", {"enabled": True})

    async def test_failed_write_does_not_change_effective_rules(self):
        with patch.object(self.store, "_write_text", side_effect=OSError("disk full")):
            with self.assertRaises(OSError):
                await self.store.update("natural-whitespace", {"enabled": False}, "krea2")
            with self.assertRaises(OSError):
                await self.store.create({"from": "Alice", "to": "Bob", "model_profile": "krea2"})
        self.assertEqual(self.normalize("  Alice  "), "Alice")
        self.assertEqual(self.store.list("krea2")["count"], 1)

    def test_preset_and_workflow_preserve_krea_prose_and_artist(self):
        value = {"model_profile": "krea2", "manual_artist": "by Studio_Name",
                 "quality_prompt": "2026, NASA", "extra_prompt": "First.\n\nSecond."}
        preset = preset_settings(value, self.store)
        for key in ("manual_artist", "quality_prompt", "extra_prompt"):
            self.assertEqual(preset[key], value[key])
        self.assertEqual(validate_settings(value)["manual_artist"], "by Studio_Name")

    async def test_unknown_profile_and_foreign_builtin_are_rejected(self):
        with self.assertRaises(WorkflowError):
            self.store.normalize_fields({"extra_prompt": "x"}, model_profile="unknown")
        with self.assertRaises(KeyError):
            await self.store.update("lowercase-tags", {"enabled": False}, "krea2")
        with self.assertRaises(WorkflowError):
            await self.store.create({"from": "a,b", "to": "c"})

    async def test_weighted_anima_custom_substitution_is_not_cascaded(self):
        await self.store.create({"from": "(Original:1.2)", "to": "(Middle:1.2)"})
        await self.store.create({"from": "Middle", "to": "Final"})
        self.assertEqual(self.store.normalize_text("(Original:1.2)", "positive")[0], "(middle:1.2)")
