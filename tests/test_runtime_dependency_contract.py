"""The installer must cover executable product graphs, not every UI-template node."""
import copy
import json
from pathlib import Path
import unittest

from anima_webui.model_profiles import model_profile_defaults
from anima_webui.workflow import DEFAULT_SETTINGS, render_workflows

ROOT = Path(__file__).resolve().parents[1]


class RuntimeDependencyContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.manifest = json.loads((ROOT / "launcher/manifests/launcher-manifest.json").read_text(encoding="utf-8"))
        cls.api = json.loads((ROOT / "templates/workflow_api_v2.json").read_text(encoding="utf-8"))
        cls.ui = json.loads((ROOT / "templates/workflow_ui_v2.json").read_text(encoding="utf-8"))

    def test_all_supported_rendered_classes_have_an_installed_owner(self):
        declared = {name for repo in [self.manifest["comfy"], *self.manifest["nodes"]] for name in repo["requiredClasses"]}
        seen = set()
        for workbench in ("random", "single"):
            for profile in ("anima", "krea2"):
                for variant in ("default", "all-postprocess", "legacy-interpolation", "untiled-img2img"):
                    if variant == "untiled-img2img" and workbench != "single":
                        continue
                    settings = copy.deepcopy(DEFAULT_SETTINGS)
                    settings.update(model_profile_defaults(profile), workbench=workbench)
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
                    seen.update(actual)
                    self.assertFalse(actual - declared, (workbench, profile, variant, actual - declared))
        self.assertEqual(50, len(seen))
        self.assertFalse({"AaaliceImagePicker", "ConditionalSaveImage", "SimpleNotify", "ImpactIfNone", "PreviewImage"} & seen)

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
