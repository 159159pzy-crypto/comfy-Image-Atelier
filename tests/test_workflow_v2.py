import copy
import json
import sys
import unittest
from pathlib import Path


APP_DIR = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(APP_DIR))

from anima_webui.model_profiles import get_model_profile  # noqa: E402
from anima_webui.launcher import configure_resources  # noqa: E402
from anima_webui.workflow import (  # noqa: E402
    DEFAULT_SETTINGS,
    WorkflowError,
    WorkflowTemplates,
    render_workflows,
    validate_settings,
)


class V2WorkflowRendererTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.api_template = json.loads(
            (APP_DIR / "templates" / "workflow_api_v2.json").read_text(encoding="utf-8")
        )
        cls.ui_template = json.loads(
            (APP_DIR / "templates" / "workflow_ui_v2.json").read_text(encoding="utf-8")
        )

    def render(self, **overrides):
        settings = copy.deepcopy(DEFAULT_SETTINGS)
        for key, value in overrides.items():
            settings[key] = value
        return render_workflows(
            self.api_template,
            self.ui_template,
            settings,
            sample_seed=12345,
            prompt_seed=67890,
            filename_prefix="AnimaRandom/v2-test",
            resolved_prompt="a frozen test prompt",
        )

    @staticmethod
    def assert_no_dangling_refs(test_case, api):
        for node_id, node in api.items():
            for input_name, value in (node.get("inputs") or {}).items():
                if (
                    isinstance(value, list)
                    and len(value) == 2
                    and isinstance(value[0], str)
                    and isinstance(value[1], int)
                ):
                    test_case.assertIn(
                        value[0], api, f"{node_id}.{input_name} references missing {value[0]}"
                    )

    @staticmethod
    def assert_link(test_case, api, node_id, input_name, source_id, output=0):
        test_case.assertEqual(
            api[node_id]["inputs"][input_name],
            [source_id, output],
            f"{node_id}.{input_name} must be wired to {source_id}[{output}]",
        )

    def test_profile_switch_binds_all_model_resources_and_sampler_controls(self):
        for profile_id in ("anima", "krea2"):
            with self.subTest(profile=profile_id):
                api, _ = self.render(
                    model_profile=profile_id,
                    base_mode="normal",
                    upscalers={
                        "latent": {"enabled": False, "scale_by": 1.5},
                        "seedvr2": {"enabled": True, "model_name": ""},
                        "nvidia": {"enabled": False, "model_name": ""},
                        "face_refine": {"enabled": False},
                    },
                )
                profile = get_model_profile(profile_id)
                self.assertEqual(api["167:56"]["inputs"]["unet_name"], profile["model_name"])
                self.assertEqual(api["167:57"]["inputs"]["clip_name"], profile["clip_name"])
                self.assertEqual(api["167:57"]["inputs"]["type"], profile["clip_type"])
                self.assertEqual(api["167:58"]["inputs"]["vae_name"], profile["vae_name"])
                self.assertEqual(api["712:716"]["inputs"]["unet_name"], profile["seedvr2_model"])
                self.assertEqual(api["712:715"]["inputs"]["vae_name"], profile["seedvr2_vae"])

    def test_v2_render_does_not_append_removed_v1_readback_nodes(self):
        api, _ = self.render()
        self.assertFalse(any(str(node_id) in {"5", "25", "37", "42", "45", "48", "49", "60"} for node_id in api))
        classes = {node.get("class_type") for node in api.values() if isinstance(node, dict)}
        self.assertNotIn("CFGZeroStar", classes)
        self.assertNotIn("FaceDetailerPipe", classes)
        self.assertNotIn("UpscaleModelLoader", classes)
        self.assertNotIn("ImageUpscaleWithModel", classes)

    def test_random_sections_require_resolved_text_and_frozen_replay_still_works(self):
        templates = WorkflowTemplates(self.api_template, self.ui_template)
        for section in ("character", "clothing", "pose", "background", "expression"):
            settings = {**DEFAULT_SETTINGS, f"random_{section}": True}
            with self.subTest(section=section):
                with self.assertRaisesRegex(WorkflowError, "随机提示词尚未解析"):
                    templates.submission(settings, 1, 2, "test", "client", 1)
                frozen = "historical exact prompt, "
                payload = templates.submission(settings, 1, 2, "test", "client", 1,
                                               frozen_positive_prompt=frozen)
                self.assertEqual(payload["prompt"]["168"]["inputs"]["value"], frozen)
                self.assertEqual(payload["extra_data"]["extra_pnginfo"]["anima_random_webui"]["resolved_prompt_full"], frozen)

    def test_launcher_mapping_is_applied_after_dynamic_profile_switch(self):
        templates = WorkflowTemplates(self.api_template, self.ui_template)
        configure_resources(
            templates,
            {
                "anima-base-v1.0.safetensors": "Anima/anima-base-v1.0.safetensors",
                "krea2_turbo_int8_convrot.safetensors": "Krea/krea2_turbo_int8_convrot.safetensors",
                "qwen_3_06b_base.safetensors": "Anima/qwen_3_06b_base.safetensors",
                "qwen3vl_4b_fp8_scaled.safetensors": "Krea/qwen3vl_4b_fp8_scaled.safetensors",
                "qwen_image_vae.safetensors": "shared/qwen_image_vae.safetensors",
                "seedvr2_3b_int8_convrot.safetensors": "SeedVR2/seedvr2_3b_int8_convrot.safetensors",
                "ema_vae_fp16.safetensors": "SeedVR2/ema_vae_fp16.safetensors",
            },
        )
        settings = validate_settings(
            {
                "model_profile": "krea2",
                "upscalers": {
                    "seedvr2": {"enabled": True},
                },
            }
        )
        payload = templates.submission(settings, 12345, 67890, "mapping", "client", 1)
        api = payload["prompt"]
        self.assertEqual(api["167:56"]["inputs"]["unet_name"], "Krea/krea2_turbo_int8_convrot.safetensors")
        self.assertEqual(api["167:57"]["inputs"]["clip_name"], "Krea/qwen3vl_4b_fp8_scaled.safetensors")
        self.assertEqual(api["167:58"]["inputs"]["vae_name"], "shared/qwen_image_vae.safetensors")
        self.assertEqual(api["712:716"]["inputs"]["unet_name"], "SeedVR2/seedvr2_3b_int8_convrot.safetensors")
        self.assertEqual(api["712:715"]["inputs"]["vae_name"], "SeedVR2/ema_vae_fp16.safetensors")

    def test_base_mode_is_exclusive_and_stage_disable_bypasses_groups(self):
        for base_mode, inactive in (("normal", "701:"), ("dual_sampler", "691:")):
            with self.subTest(base_mode=base_mode):
                api, ui = self.render(base_mode=base_mode)
                self.assertFalse(any(str(node_id).startswith(inactive) for node_id in api))
                self.assert_no_dangling_refs(self, api)
                visible_groups = {
                    int(node["id"]): node.get("mode")
                    for node in ui.get("nodes") or []
                    if int(node.get("id")) in {691, 701}
                }
                self.assertEqual(visible_groups[691], 0 if base_mode == "normal" else 4)
                self.assertEqual(visible_groups[701], 0 if base_mode == "dual_sampler" else 4)

        api, _ = self.render(
            base_mode="normal",
            upscalers={
                "latent": {"enabled": False, "scale_by": 1.75},
                "seedvr2": {"enabled": False, "model_name": ""},
                "nvidia": {"enabled": False, "model_name": ""},
                "face_refine": {"enabled": False},
            },
        )
        for prefix in ("704:", "712:", "1432:", "1253:"):
            self.assertFalse(any(str(node_id).startswith(prefix) for node_id in api))
        self.assert_no_dangling_refs(self, api)

    def test_repair_chain_order_and_stage_parameters_are_rendered(self):
        api, ui = self.render(
            model_profile="krea2",
            base_mode="normal",
            upscalers={
                "latent": {"enabled": True, "scale_by": 1.75},
                "seedvr2": {"enabled": True, "model_name": "stale-model.safetensors"},
                "nvidia": {"enabled": True, "model_name": "nvidia-model.pth"},
                "face_refine": {"enabled": True},
            },
        )
        self.assertEqual(api["704:1064"]["inputs"]["scale"], 1.75)
        # User-selected SeedVR2 assets are kept and checked against the live
        # inventory by BatchManager before submission.
        self.assertEqual(api["712:716"]["inputs"]["unet_name"], "stale-model.safetensors")
        # The RTXVideoSuperResolution node receives its quality/scale controls
        # directly; unlike the removed pixel-upscale chain it has no model
        # loader input.  The selected N-card resource is validated separately
        # by the preflight API.
        self.assertEqual(api["1432:1418"]["inputs"]["resize_type.scale"], 2.0)
        self.assertEqual(api["1253:1215"]["inputs"]["seed"], 12345)

        # The four selectors should point to the previous surviving endpoint,
        # yielding base -> latent -> face -> SeedVR2 -> N-card order.
        self.assertEqual(api["702:353"]["inputs"]["any_01"], ["691:1413", 1])
        self.assertEqual(api["710:388"]["inputs"]["any_01"], ["1253:1416", 1])
        self.assertEqual(api["1425:1419"]["inputs"]["any_01"], ["712:1417", 1])
        self.assertEqual(api["1214:1209"]["inputs"]["any_01"], ["704:1415", 1])
        self.assert_no_dangling_refs(self, api)
        modes = {
            int(node["id"]): node.get("mode")
            for node in ui.get("nodes") or []
            if int(node.get("id")) in {691, 701, 704, 712, 1432, 1253}
        }
        self.assertTrue(all(modes[group] == 0 for group in (691, 704, 712, 1432, 1253)))

    def test_configurable_post_processing_fields_bind_to_real_nodes(self):
        settings = validate_settings(
            {
                "model_profile": "anima",
                "model_name": "Anima/custom-selected.safetensors",
                "steps": 30,
                "loras": [],
                "upscalers": {
                    "latent": {
                        "enabled": True,
                        "scale_by": 2.25,
                        "upscale_method": "bicubic",
                        "second_sampler": {
                            "enabled": True,
                            "steps": 17,
                            "cfg": 5.5,
                            "sampler_name": "euler",
                            "scheduler": "karras",
                            "denoise": 0.42,
                        },
                    },
                    "seedvr2": {
                        "enabled": True,
                        "model_name": "SeedVR2/custom.safetensors",
                        "vae_name": "SeedVR2/custom-vae.safetensors",
                        "scale_by": 2.75,
                        "weight_dtype": "fp16",
                        "color_correction_method": "lab",
                        "sampler": {
                            "steps": 5,
                            "cfg": 2.5,
                            "sampler_name": "heun",
                            "scheduler": "normal",
                            "denoise": 0.8,
                        },
                        "encode": {
                            "tile_size": 640,
                            "overlap": 48,
                            "temporal_size": 80,
                            "temporal_overlap": 4,
                        },
                        "decode": {
                            "tile_size": 768,
                            "overlap": 96,
                            "temporal_size": 2048,
                            "temporal_overlap": 6,
                        },
                    },
                    "nvidia": {
                        "enabled": True,
                        "resize_type": "scale by multiplier",
                        "scale": 3.0,
                        "quality": "HIGH",
                    },
                    "face_refine": {
                        "enabled": True,
                        "steps": 11,
                        "cfg_scale": 1.7,
                        "sampler": "heun",
                        "scheduler": "normal",
                        "denoise_strength": 0.55,
                        "detection_confidence": 0.83,
                        "processing_resolution": 1536,
                        "upscaler_model": "RealESRGAN x4",
                        "crop_padding": 2.1,
                        "face_positive_prompt": "clear eyes",
                        "replace_positive_prompt": True,
                        "face_negative_prompt": "blur",
                        "replace_negative_prompt": True,
                        "exclusions": "profile",
                        "blend_softness": 10,
                        "mask_expansion": 3,
                        "sampling_mask_blur_size": 25,
                        "sampling_mask_blur_strength": 0.6,
                        "enable_segmentation": False,
                        "offload_models_to_cpu": False,
                    },
                },
            }
        )
        api, _ = render_workflows(
            self.api_template, self.ui_template, settings, 12345, 67890, "configured"
        )
        self.assertEqual(api["167:56"]["inputs"]["unet_name"], "Anima/custom-selected.safetensors")

        self.assertEqual(api["704:1064"]["inputs"]["scale_by"], 2.25)
        self.assertEqual(api["704:1064"]["inputs"]["upscale_method"], "bicubic")
        second = api["704:1248"]["inputs"]
        self.assertEqual(second["steps"], 17)
        self.assertEqual(second["cfg"], 5.5)
        self.assertEqual(second["scheduler"], "karras")
        self.assertEqual(second["denoise"], 0.42)
        self.assertEqual(second["seed"], 12345)
        self.assertEqual(second["model"], ["1026:1021", 0])

        self.assertEqual(api["712:716"]["inputs"]["unet_name"], "SeedVR2/custom.safetensors")
        self.assertEqual(api["712:716"]["inputs"]["weight_dtype"], "fp16")
        self.assertEqual(api["712:715"]["inputs"]["vae_name"], "SeedVR2/custom-vae.safetensors")
        self.assertEqual(api["712:907"]["inputs"]["value"], 2.75)
        seed_sampler = api["712:1208"]["inputs"]
        self.assertEqual(seed_sampler["steps"], 5)
        self.assertEqual(seed_sampler["sampler_name"], "heun")
        self.assertEqual(seed_sampler["scheduler"], "normal")
        self.assertEqual(seed_sampler["seed"], 12345)
        self.assertEqual(api["712:1177"]["inputs"]["tile_size"], 640)
        self.assertEqual(api["712:1177"]["inputs"]["overlap"], 48)
        self.assertEqual(api["712:718"]["inputs"]["tile_size"], 768)
        self.assertEqual(api["712:718"]["inputs"]["temporal_size"], 2048)

        self.assertEqual(api["1432:1418"]["inputs"]["resize_type.scale"], 3.0)
        self.assertEqual(api["1432:1418"]["inputs"]["quality"], "HIGH")
        face = api["1253:1215"]["inputs"]
        self.assertEqual(face["steps"], 11)
        self.assertEqual(face["cfg_scale"], 1.7)
        self.assertEqual(face["detection_confidence"], 0.83)
        self.assertEqual(face["processing_resolution"], 1536)
        self.assertEqual(face["upscaler_model"], "RealESRGAN x4")
        self.assertEqual(face["replace_positive_prompt"], True)
        self.assertEqual(face["enable_segmentation"], False)
        self.assertEqual(face["offload_models_to_cpu"], False)

    def test_latent_upscale_can_skip_its_second_sampler(self):
        api, _ = self.render(
            base_mode="normal",
            upscalers={
                "latent": {
                    "enabled": True,
                    "second_sampler": {"enabled": False},
                },
                "seedvr2": {"enabled": False},
                "nvidia": {"enabled": False},
                "face_refine": {"enabled": False},
            },
        )
        self.assertNotIn("704:1248", api)
        self.assertEqual(api["704:359"]["inputs"]["samples"], ["704:1064", 0])
        self.assert_no_dangling_refs(self, api)

    def test_v2_base_and_repair_subgraphs_have_required_boundary_wires_and_output_sink(self):
        """Protect the graphToPrompt boundary links and the executable output."""
        for base_mode, base_endpoint in (
            ("normal", "691:1413"),
            ("dual_sampler", "701:1414"),
        ):
            with self.subTest(base_mode=base_mode):
                api, _ = self.render(
                    base_mode=base_mode,
                    upscalers={
                        "latent": {"enabled": True, "scale_by": 1.75},
                        "seedvr2": {"enabled": True, "model_name": ""},
                        "nvidia": {"enabled": True, "model_name": ""},
                        "face_refine": {"enabled": True},
                    },
                )

                if base_mode == "normal":
                    for input_name, source_id in (
                        ("model", "1010:1005"),
                        ("positive", "691:213"),
                        ("negative", "1010:1002"),
                        ("latent_image", "691:1577"),
                    ):
                        self.assert_link(self, api, "691:1240", input_name, source_id)
                    self.assert_link(self, api, "691:213", "on_false", "1010:995")
                    self.assert_link(self, api, "691:239", "samples", "691:1240")
                    self.assert_link(self, api, "691:239", "vae", "167:58")
                else:
                    for node_id, expected in (
                        (
                            "701:1245",
                            {
                                "model": "1010:1005",
                                "positive": "701:538",
                                "negative": "1010:1002",
                                "latent_image": "701:496",
                            },
                        ),
                        (
                            "701:1246",
                            {
                                "model": "1026:1021",
                                "positive": "1026:1011",
                                "negative": "1026:1018",
                                "latent_image": "701:1450",
                            },
                        ),
                    ):
                        for input_name, source_id in expected.items():
                            self.assert_link(self, api, node_id, input_name, source_id)
                    self.assert_link(self, api, "701:1450", "latent", "701:1245")
                    self.assert_link(self, api, "701:539", "samples", "701:1246")
                    self.assert_link(self, api, "701:539", "vae", "167:58")

                # Latent upscale must round-trip through the selected VAE and
                # feed a model/conditioning-complete sampler.
                self.assert_link(self, api, "704:1181", "images", "702:353")
                self.assert_link(self, api, "704:1182", "pixels", "704:1181")
                self.assert_link(self, api, "704:1182", "vae", "167:58")
                self.assert_link(self, api, "704:1064", "latent", "704:1182")
                for input_name, source_id in (
                    ("model", "1026:1021"),
                    ("positive", "1026:1011"),
                    ("negative", "1026:1018"),
                    ("latent_image", "704:1064"),
                ):
                    self.assert_link(self, api, "704:1248", input_name, source_id)
                self.assert_link(self, api, "704:359", "samples", "704:1248")
                self.assert_link(self, api, "704:359", "vae", "167:58")

                # SeedVR2 and face refinement have boundary inputs absent from
                # graphToPrompt's flattened export; these explicit links keep
                # each stage executable after profile switching.
                self.assert_link(self, api, "712:714", "image", "710:388")
                self.assert_link(self, api, "712:1177", "pixels", "712:720")
                self.assert_link(self, api, "712:1177", "vae", "712:715")
                self.assert_link(self, api, "712:1208", "model", "712:716")
                self.assert_link(self, api, "712:1208", "positive", "712:722")
                self.assert_link(self, api, "712:1208", "negative", "712:722", 1)
                self.assert_link(self, api, "712:1208", "latent_image", "712:1177")
                self.assert_link(self, api, "712:718", "samples", "712:1208")
                self.assert_link(self, api, "712:718", "vae", "712:715")

                self.assert_link(self, api, "1432:1448", "images", "1425:1419")
                self.assert_link(self, api, "1432:1418", "images", "1432:1448")
                self.assert_link(self, api, "1432:1492", "images", "1432:1418")
                self.assert_link(self, api, "1253:1259", "images", "1214:1209")
                self.assert_link(self, api, "1253:1215", "image", "1253:1259")
                for input_name, source_id in (
                    ("model", "1026:1021"),
                    ("vae", "167:58"),
                    ("positive", "1026:1011"),
                    ("negative", "1026:1018"),
                    ("clip", "1026:1025"),
                ):
                    self.assert_link(self, api, "1253:1215", input_name, source_id)

                # ConditionalSaveImage helpers in this workflow are not API
                # output nodes. Exactly one core SaveImage sink must schedule
                # and expose the last image stage to ComfyUI history.
                save_nodes = {
                    node_id: node
                    for node_id, node in api.items()
                    if node.get("class_type") == "SaveImage"
                }
                self.assertEqual(set(save_nodes), {"v2:output"})
                self.assertEqual(
                    save_nodes["v2:output"]["inputs"],
                    {"filename_prefix": "AnimaRandom/v2-test", "images": ["1432:1447", 1]},
                )
                self.assertFalse(
                    any(
                        node.get("class_type")
                        in {"SimpleNotify", "ConditionalSaveImage", "AaaliceImagePicker"}
                        for node in api.values()
                    )
                )
                self.assertFalse(
                    any(
                        node_id.startswith("863:")
                        for node_id, node in api.items()
                    )
                )
                self.assertNotIn("691:1578", api)
                self.assert_link(self, api, "1214:1209", "any_01", "704:1415", 1)
                self.assert_link(self, api, "1425:1419", "any_01", "712:1417", 1)
                self.assert_link(self, api, "710:388", "any_01", "1253:1416", 1)
                self.assert_no_dangling_refs(self, api)

    def test_frozen_prompts_and_seeds_are_written_to_v2_nodes(self):
        api, _ = render_workflows(
            self.api_template,
            self.ui_template,
            validate_settings({**DEFAULT_SETTINGS, "base_mode": "normal"}),
            987,
            654,
            "frozen",
            resolved_prompt="ignored because full prompt is supplied",
            resolved_prompt_full="full deterministic prompt",
            frozen_positive_prompt="frozen positive",
            frozen_negative_prompt="frozen negative",
        )
        self.assertEqual(api["168"]["inputs"]["value"], "frozen positive")
        self.assertEqual(api["250"]["inputs"]["value"], "frozen negative")
        self.assertEqual(api["691:1240"]["inputs"]["seed"], 987)
        self.assertEqual(api["691:1241"]["inputs"]["values.a"], 654)


if __name__ == "__main__":
    unittest.main()
