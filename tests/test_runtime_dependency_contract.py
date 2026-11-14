"""The installer must cover executable product graphs, not every UI-template node."""
import itertools
import json
from pathlib import Path
import unittest

from anima_webui.workflow import default_settings_for_profile, render_workflows

ROOT = Path(__file__).resolve().parents[1]


class RuntimeDependencyContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.manifest = json.loads((ROOT / "launcher/manifests/launcher-manifest.json").read_text(encoding="utf-8"))
        cls.api = json.loads((ROOT / "templates/workflow_api_v2.json").read_text(encoding="utf-8"))
        cls.ui = json.loads((ROOT / "templates/workflow_ui_v2.json").read_text(encoding="utf-8"))

    def test_all_supported_rendered_classes_have_an_installed_owner(self):
        declared = {name for repo in [self.manifest["comfy"], *self.manifest["nodes"]] for name in repo["requiredClasses"]}
        seen = {2: set(), 3: set(), 4: set()}
        for version in (2, 3, 4):
            for workbench, profile in itertools.product(("random", "single"), ("anima", "krea2")):
                for variant in ("default", "all-postprocess", "legacy-interpolation", "untiled-img2img"):
                    if variant == "untiled-img2img" and workbench != "single":
                        continue
                    settings = default_settings_for_profile(profile, workbench)
                    settings["pipeline_version"] = version
                    if variant == "all-postprocess":
                        for stage in settings["upscalers"].values():
                            stage["enabled"] = True
                    elif variant == "legacy-interpolation":
                        settings["base_mode"] = "dual_sampler"
                        settings["dual"]["upscale_method"] = "bilinear"
                        settings["single"]["dual"]["upscale_method"] = "bilinear"
                    elif variant == "untiled-img2img":
                        settings.update(base_mode="normal", generation_mode="img2img")
                        settings["single"]["vae_encode"]["tiled"] = False
                        settings["single"]["vae_decode"]["tiled"] = False
                    api, _ = render_workflows(self.api, self.ui, settings, 1, 2, "contract",
                        resolved_prompt="a cat", single_runtime={"input_image": "fixture.png", "positive": "a cat"} if workbench == "single" else None)
                    actual = {node["class_type"] for node in api.values()}
                    seen[version].update(actual)
                    self.assertFalse(actual - declared, (version, workbench, profile, variant, actual - declared))
        self.assertTrue({"UpscaleModelLoader", "ImageUpscaleWithModel", "ImageScaleBy", "SeedVR2Preprocess",
                         "SeedVR2PostProcessing", "SesquiLatentUpscale", "LatentUpscaleBy",
                         "RTXVideoSuperResolution", "DLSS5NeuralRender"} <= seen[3])
        self.assertNotIn("UpscaleModelLoader", seen[2])
        self.assertLess(len(seen[3]), len(seen[2]))
        self.assertEqual(seen[4], seen[3])
        for classes in seen.values():
            self.assertFalse({"AaaliceImagePicker", "ConditionalSaveImage", "SimpleNotify", "ImpactIfNone",
                              "PreviewImage", "PreviewAny", "MaskPreview", "PromptExpand"} & classes)

    def test_source_ownership_and_optional_auxiliary_contract(self):
        nodes = {node["id"]: node for node in self.manifest["nodes"]}
        self.assertIn("ComfyMathExpression", self.manifest["comfy"]["requiredClasses"])
        self.assertNotIn("ComfyMathExpression", nodes["comfyui-easy-use"]["requiredClasses"])
        self.assertTrue({"FloatConstant", "JoinStrings", "SomethingToString"} <= set(nodes["ComfyUI-KJNodes"]["requiredClasses"]))
        self.assertTrue({"PromptExpand", "ImageCaptionNode"} <= set(nodes["prompt-assistant"]["requiredClasses"]))
        self.assertEqual([], nodes["ComfyUI-WD-Timm-Tagger"]["requiredClasses"])
        self.assertTrue({"SesquiLSR", "forbidden-vision", "RES4LYF", "nvidia-rtx-vsr-pro", "DLSS5-Comfyui"} <= set(nodes))
        self.assertFalse(any("Nvidia_RTX_Nodes_ComfyUI" in node["url"] for node in nodes.values()))


if __name__ == "__main__":
    unittest.main()
