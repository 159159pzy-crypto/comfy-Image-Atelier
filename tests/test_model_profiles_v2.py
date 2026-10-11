import copy
import sys
import tempfile
import unittest
from pathlib import Path


APP_DIR = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(APP_DIR))

from anima_webui.model_profiles import (  # noqa: E402
    DEFAULT_MODEL_PROFILE,
    MODEL_PROFILE_IDS,
    MODEL_PROFILES,
    ModelProfileError,
    get_model_profile,
    model_profile_defaults,
    normalize_model_profile,
    profile_public,
)
from anima_webui.lora_triggers import LoraTriggerOverrideStore  # noqa: E402
from anima_webui.prompt_pools import PromptPoolStore  # noqa: E402
from anima_webui.runner import BatchManager  # noqa: E402
from anima_webui.style_presets import PRESET_SETTING_KEYS, StylePresetStore  # noqa: E402
from anima_webui.workflow import (  # noqa: E402
    DEFAULT_SETTINGS,
    WorkflowError,
    migrate_settings,
    validate_settings,
)


class ModelProfileContractTests(unittest.TestCase):
    def test_api_v2_template_has_attached_loader_contract_and_no_removed_nodes(self):
        template_path = APP_DIR / "templates" / "workflow_api_v2.json"
        self.assertTrue(template_path.is_file(), "v2 API template must be checked in")
        import json

        api = json.loads(template_path.read_text(encoding="utf-8"))
        self.assertGreaterEqual(len(api), 60)
        self.assertTrue(
            all(isinstance(node.get("class_type"), str) and node["class_type"] for node in api.values()),
            "API format must not retain unresolved/unknown nodes",
        )
        for node_id, node in api.items():
            for input_name, value in (node.get("inputs") or {}).items():
                if (
                    isinstance(value, list)
                    and len(value) == 2
                    and isinstance(value[0], str)
                    and isinstance(value[1], int)
                ):
                    self.assertIn(value[0], api, f"{node_id}.{input_name} references missing node {value[0]}")
        classes = {node.get("class_type") for node in api.values() if isinstance(node, dict)}
        self.assertNotIn("CFGZeroStar", classes)
        self.assertNotIn("UpscaleModelLoader", classes)
        self.assertNotIn("ImageUpscaleWithModel", classes)
        self.assertNotIn("FaceDetailerPipe", classes)
        self.assertIn("UNETLoader", classes)
        self.assertIn("CLIPLoader", classes)
        self.assertIn("VAELoader", classes)

        loaders = {
            node_id: node
            for node_id, node in api.items()
            if node.get("class_type") in {"UNETLoader", "CLIPLoader", "VAELoader"}
        }
        unet = next(node for node in loaders.values() if node["class_type"] == "UNETLoader")
        clip = next(node for node in loaders.values() if node["class_type"] == "CLIPLoader")
        vae = next(node for node in loaders.values() if node["class_type"] == "VAELoader")
        self.assertEqual(unet["inputs"]["unet_name"], "anima-base-v1.0.safetensors")
        self.assertEqual(clip["inputs"]["clip_name"], "qwen_3_06b_base.safetensors")
        self.assertEqual(clip["inputs"]["type"], "stable_diffusion")
        self.assertEqual(vae["inputs"]["vae_name"], "qwen_image_vae.safetensors")

    def test_public_profiles_match_the_attached_workflow_resources(self):
        self.assertEqual(MODEL_PROFILE_IDS, ("anima", "krea2"))
        self.assertEqual(DEFAULT_MODEL_PROFILE, "anima")
        self.assertEqual(
            (MODEL_PROFILES["anima"]["model_name"], MODEL_PROFILES["anima"]["clip_name"], MODEL_PROFILES["anima"]["vae_name"]),
            (
                "anima-base-v1.0.safetensors",
                "qwen_3_06b_base.safetensors",
                "qwen_image_vae.safetensors",
            ),
        )
        self.assertEqual(
            (MODEL_PROFILES["krea2"]["model_name"], MODEL_PROFILES["krea2"]["clip_name"], MODEL_PROFILES["krea2"]["vae_name"]),
            (
                "krea2_turbo_int8_convrot.safetensors",
                "qwen3vl_4b_fp8_scaled.safetensors",
                "qwen_image_vae.safetensors",
            ),
        )

    def test_profiles_isolate_namespaces_and_provide_model_specific_modes(self):
        anima = get_model_profile("anima")
        krea = get_model_profile("krea2")
        self.assertEqual(anima["defaults"]["base_mode"], "dual_sampler")
        self.assertEqual(krea["defaults"]["base_mode"], "normal")
        self.assertEqual(anima["prompt_mode"], "tag_or_natural")
        self.assertEqual(krea["prompt_mode"], "natural_language")
        self.assertEqual(krea["clip_type"], "krea2")
        for namespace in ("lora_namespace", "pool_namespace", "style_namespace"):
            with self.subTest(namespace=namespace):
                self.assertNotEqual(anima[namespace], krea[namespace])
        self.assertTrue(anima["seedvr2_model"])
        self.assertTrue(anima["seedvr2_vae"])
        self.assertEqual(anima["seedvr2_model"], krea["seedvr2_model"])
        self.assertEqual(anima["seedvr2_vae"], krea["seedvr2_vae"])

    def test_normalization_accepts_labels_and_rejects_unknown_profiles(self):
        self.assertEqual(normalize_model_profile(" Anima "), "anima")
        self.assertEqual(normalize_model_profile("Krea 2"), "krea2")
        self.assertEqual(normalize_model_profile("krea"), "krea2")
        for invalid in (None, 1, "", "other"):
            with self.subTest(invalid=invalid), self.assertRaises(ModelProfileError):
                normalize_model_profile(invalid)

    def test_profile_values_are_returned_as_defensive_copies(self):
        profile = get_model_profile("anima")
        profile["defaults"]["steps"] = -1
        profile["model_name"] = "mutated.safetensors"
        self.assertEqual(MODEL_PROFILES["anima"]["defaults"]["steps"], 30)
        self.assertEqual(MODEL_PROFILES["anima"]["model_name"], "anima-base-v1.0.safetensors")

        defaults = model_profile_defaults("krea2")
        defaults["steps"] = -1
        self.assertEqual(model_profile_defaults("krea2")["steps"], 30)

    def test_resource_summary_reports_missing_files_and_checks_names_case_insensitively(self):
        krea = get_model_profile("krea2")
        inventory = {
            "models": [krea["model_name"].upper()],
            "clips": [krea["clip_name"]],
            "vaes": [krea["vae_name"]],
            "seedvr2_models": [krea["seedvr2_model"]],
        }
        summary = profile_public("krea2", available=inventory)
        self.assertEqual(summary["available"], {"model": True, "clip": True, "vae": True, "seedvr2": True})
        self.assertEqual(summary["missing"], [])
        self.assertEqual(summary["namespaces"], {"lora": "krea2", "pool": "krea2", "style": "krea2"})
        self.assertEqual(
            summary["nodes"],
            {
                "unet_loader": None,
                "clip_loader": None,
                "vae_loader": None,
                "seedvr2": None,
                "nvidia_upscale": None,
                "face_refine": None,
            },
        )
        inventory.update(
            {
                "nodes": {
                    "UNETLoader": True,
                    "CLIPLoader": True,
                    "VAELoader": True,
                    "SeedVR2": True,
                    "RTXVideoSuperResolution": True,
                    "ForbiddenVisionFaceProcessorIntegrated": True,
                },
                "plugins": {
                    "seedvr2": True,
                    "nvidia_upscale": True,
                    "forbidden_vision_face": True,
                },
            }
        )
        summary = profile_public("krea2", available=inventory)
        self.assertTrue(all(summary["nodes"].values()))
        self.assertTrue(all(summary["plugins"].values()))

        missing = profile_public("krea2", available={"models": []})
        self.assertFalse(missing["available"]["model"])
        self.assertIsNone(missing["available"]["clip"])
        self.assertIn(krea["model_name"], missing["missing"])

    def test_legacy_settings_migrate_without_mutating_and_drop_removed_controls(self):
        legacy = {
            "model_name": "miaomiaoHarem_anima14.safetensors",
            "hires": {"enabled": True, "model_name": "4x_foolhardy_Remacri.pth", "percent": 60},
            "detailers": {"face": True},
            "cfg_zero_star": True,
            "second_sampler": {"enabled": True, "steps": 12},
        }
        original = copy.deepcopy(legacy)
        migrated = migrate_settings(legacy)
        self.assertEqual(legacy, original)
        self.assertEqual(migrated["model_profile"], "anima")
        # Legacy hires chained a nominal 4x model and then applied percent;
        # preserve its total geometry as the new direct latent multiplier.
        self.assertTrue(migrated["upscalers"]["latent"]["enabled"])
        self.assertEqual(migrated["upscalers"]["latent"]["scale_by"], 2.4)
        self.assertEqual(
            migrated["upscalers"]["latent"]["second_sampler"]["steps"], 12
        )
        for removed in ("hires", "detailers", "cfg_zero_star", "second_sampler"):
            self.assertNotIn(removed, migrated)

        disabled_legacy_sampler = migrate_settings(
            {
                "hires": {"enabled": True, "percent": 50},
                "second_sampler": {"enabled": False},
            }
        )
        self.assertTrue(disabled_legacy_sampler["upscalers"]["latent"]["enabled"])
        self.assertFalse(
            disabled_legacy_sampler["upscalers"]["latent"]["second_sampler"]["enabled"]
        )

    def test_explicit_profile_switch_rebinds_profile_resources_but_preserves_selected_unet(self):
        for profile_id in ("anima", "krea2"):
            with self.subTest(profile=profile_id):
                settings = validate_settings(
                    {
                        "model_profile": profile_id,
                        "model_name": "wrong.safetensors",
                        "clip_name": "wrong-clip.safetensors",
                        "vae_name": "wrong-vae.safetensors",
                        "clip_type": "stable_diffusion",
                    }
                )
                profile = get_model_profile(profile_id)
                self.assertEqual(settings["model_name"], "wrong.safetensors")
                for key in ("clip_name", "vae_name", "clip_type"):
                    self.assertEqual(settings[key], profile[key])
                self.assertEqual(settings["base_mode"], profile["defaults"]["base_mode"])
                self.assertEqual(settings["sampler_name"], profile["defaults"]["sampler_name"])
                self.assertEqual(settings["scheduler"], profile["defaults"]["scheduler"])

                # Omitting the selection uses the profile's canonical model.
                defaults = validate_settings({"model_profile": profile_id})
                self.assertEqual(defaults["model_name"], profile["model_name"])

    def test_new_repair_chain_is_independently_validated_and_old_controls_are_not_new_api(self):
        settings = validate_settings(
            {
                **DEFAULT_SETTINGS,
                "base_mode": "dual_sampler",
                "upscalers": {
                    "latent": {"enabled": True, "scale_by": 1.75},
                    "seedvr2": {"enabled": True, "model_name": "seedvr2.safetensors"},
                    "nvidia": {"enabled": True, "model_name": "nvidia.pth"},
                    "face_refine": {"enabled": True},
                },
            }
        )
        self.assertEqual(settings["base_mode"], "dual_sampler")
        self.assertTrue(all(settings["upscalers"][key]["enabled"] for key in settings["upscalers"]))

        for bad in ("both", "dual", "normal+dual"):
            with self.subTest(base_mode=bad), self.assertRaises(WorkflowError):
                validate_settings({"base_mode": bad})

        for bad in (
            {"latent": {"second_sampler": {"cfg": 31}}},
            {"seedvr2": {"encode": {"tile_size": 0}}},
            {"nvidia": {"scale": 0}},
            {"face_refine": {"detection_confidence": 1.1}},
        ):
            with self.subTest(upscalers=bad), self.assertRaises(WorkflowError):
                validate_settings({"upscalers": bad})


class ProfileNamespaceIsolationTests(unittest.IsolatedAsyncioTestCase):
    def _preset_snapshot(self, profile: str) -> dict:
        from anima_webui.workflow import DEFAULT_SETTINGS

        return {
            **{
                key: copy.deepcopy(DEFAULT_SETTINGS[key])
                for key in PRESET_SETTING_KEYS
                if key in DEFAULT_SETTINGS
            },
            "model_profile": profile,
        }

    async def test_lora_trigger_and_natural_language_pools_do_not_cross_profiles(self):
        with tempfile.TemporaryDirectory() as directory:
            trigger_store = LoraTriggerOverrideStore(Path(directory) / "triggers.json")
            await trigger_store.set("shared.safetensors", ["@anima"], "anima")
            await trigger_store.set("shared.safetensors", ["@krea"], "krea2")
            self.assertEqual(trigger_store.get("shared.safetensors", "anima"), ["@anima"])
            self.assertEqual(trigger_store.get("shared.safetensors", "krea2"), ["@krea"])

            pool_store = PromptPoolStore(Path(directory) / "pools.json")
            await pool_store.import_items("anima", ["tag prompt"])
            await pool_store.import_items("krea2", ["natural language prompt"])
            self.assertEqual([item["text"] for item in pool_store.list("anima")["items"]], ["tag prompt"])
            self.assertEqual(
                [item["text"] for item in pool_store.list("krea2")["items"]],
                ["natural language prompt"],
            )

    async def test_legacy_lora_trigger_file_is_migrated_into_anima_with_backup(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "triggers.json"
            path.write_text(
                '{"version": 1, "overrides": {"legacy.safetensors": ["@legacy"]}}',
                encoding="utf-8",
            )
            store = LoraTriggerOverrideStore(path)
            self.assertEqual(store.get("legacy.safetensors", "anima"), ["@legacy"])
            self.assertIsNone(store.get("legacy.safetensors", "krea2"))
            self.assertTrue(any("迁移" in warning for warning in store.load_warnings))
            self.assertTrue(any(path.name in candidate.name for candidate in path.parent.glob("*.bak")))

    async def test_style_presets_filter_by_profile_and_allow_same_name_in_each_namespace(self):
        with tempfile.TemporaryDirectory() as directory:
            store = StylePresetStore(Path(directory) / "styles.json")
            anima = await store.create(
                {"name": "Shared", "favorite": False, "model_profile": "anima", "settings": self._preset_snapshot("anima")}
            )
            krea = await store.create(
                {"name": "Shared", "favorite": False, "model_profile": "krea2", "settings": self._preset_snapshot("krea2")}
            )
            self.assertNotEqual(anima["id"], krea["id"])
            self.assertEqual(store.list("anima")["count"], 1)
            self.assertEqual(store.list("krea2")["count"], 1)
            self.assertEqual(store.list()["count"], 2)

    async def test_krea_prompt_pool_uses_prompt_seed_and_preserves_selected_id(self):
        with tempfile.TemporaryDirectory() as directory:
            pool_store = PromptPoolStore(Path(directory) / "pools.json")
            await pool_store.import_items("krea2", ["a natural scene", "a studio portrait"])
            manager = BatchManager(
                templates=object(), history=object(), comfy=object(), prompt_pools=pool_store
            )
            settings = validate_settings(
                {"model_profile": "krea2", "krea_prompt_pool": {"enabled": True, "ids": []}}
            )
            first = manager._resolve_prompt(settings, 123)
            replay = manager._resolve_prompt(settings, 123)
            self.assertEqual(first, replay)
            self.assertIn(first["composer_prompt"], {"a natural scene", "a studio portrait"})
            self.assertEqual(first["full_prompt"], first["composer_prompt"])
            self.assertEqual(first["selected"]["krea_prompt_pool"][0]["text"], first["composer_prompt"])
            self.assertTrue(first["selected"]["krea_prompt_pool"][0]["id"].startswith("prompt_"))

    async def test_krea_prompt_pool_without_items_fails_before_generation(self):
        with tempfile.TemporaryDirectory() as directory:
            pool_store = PromptPoolStore(Path(directory) / "pools.json")
            manager = BatchManager(
                templates=object(), history=object(), comfy=object(), prompt_pools=pool_store
            )
            settings = validate_settings(
                {"model_profile": "krea2", "krea_prompt_pool": {"enabled": True, "ids": []}}
            )
            with self.assertRaisesRegex(WorkflowError, "提示词池为空"):
                manager._resolve_prompt(settings, 123)


if __name__ == "__main__":
    unittest.main()
