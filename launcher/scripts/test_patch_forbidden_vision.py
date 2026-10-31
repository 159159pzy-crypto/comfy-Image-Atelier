import importlib.util
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("patch_forbidden_vision", Path(__file__).with_name("patch_forbidden_vision.py"))
patcher = importlib.util.module_from_spec(spec)
spec.loader.exec_module(patcher)

MANAGER = '''import os
class ForbiddenVisionModelManager:
    MODELS_CONFIG = {'face_detect': {'filename': 'face.pt'}}
    def _download_model(self, model_key):
        try:
            config = self.MODELS_CONFIG[model_key]
            local_path = os.path.join(self.models_dir, config['filename'])
            if os.path.isfile(local_path):
                return local_path
            return hf_hub_download(model_key)
        except Exception:
            return None
'''
INITIALIZER = '''import os
def initialize_forbidden_vision():
    model_manager = ForbiddenVisionModelManager.get_instance()
    validation_status = model_manager.validate_model_availability()
    if not all(validation_status.values()):
        print("downloading defaults")
        model_manager.initialize_default_models()
        validation_status = model_manager.validate_model_availability()
    return validation_status
'''


class ManagedDownloadsTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.custom = Path(self.temp.name) / "custom_nodes"
        self.root = self.custom / "forbidden-vision"
        (self.root / "src").mkdir(parents=True)
        (self.root / "__init__.py").write_text(INITIALIZER, encoding="utf-8")
        (self.root / "src/model_manager.py").write_text(MANAGER, encoding="utf-8")

    def test_idempotent_and_new_commit_with_compatible_source(self):
        (self.root / "src/model_manager.py").write_text(MANAGER + "\n# unrelated upstream change\n", encoding="utf-8")
        self.assertFalse(patcher.patch(self.root)["patched"])
        self.assertTrue(patcher.patch(self.root, True)["changed"])
        self.assertFalse(patcher.patch(self.root, True)["changed"])
        self.assertTrue(patcher.patch(self.root)["patched"])

    def test_partial_commit_recovers_without_overwriting_user_changes(self):
        patcher.patch(self.root, True)
        source = self.root / "src/model_manager.py"
        source.write_text(MANAGER, encoding="utf-8")
        self.assertTrue(patcher.patch(self.root, True)["changed"])
        changed = source.read_text(encoding="utf-8") + "\n# user modification\n"
        source.write_text(changed, encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "Unknown local changes"):
            patcher.patch(self.root, True)
        self.assertEqual(changed, source.read_text(encoding="utf-8"))

    def test_unknown_contract_fails_before_any_write(self):
        source = self.root / "src/model_manager.py"
        source.write_text(MANAGER.replace("self, model_key", "self, model_key, token"), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "signature"):
            patcher.patch(self.root, True)
        self.assertFalse((self.root / patcher.BACKUP).exists())
        self.assertEqual(INITIALIZER, (self.root / "__init__.py").read_text(encoding="utf-8"))

    def test_missing_optional_model_never_downloads_but_existing_file_loads(self):
        patcher.patch(self.root, True)
        downloads = []
        source = self.root / "src/model_manager.py"
        namespace = {"__file__": str(source), "hf_hub_download": lambda model: downloads.append(model) or "network"}
        exec(compile(source.read_text(encoding="utf-8"), str(source), "exec"), namespace)
        manager = namespace["ForbiddenVisionModelManager"]()
        manager.models_dir = str(self.root / "models")
        Path(manager.models_dir).mkdir()
        self.assertEqual("network", manager._download_model("face_detect"))
        (self.custom / "skip_download_model").write_text("managed")
        self.assertIsNone(manager._download_model("face_detect"))
        local = Path(manager.models_dir) / "face.pt"
        local.write_bytes(b"x" * 1001)
        self.assertEqual(str(local), manager._download_model("face_detect"))
        local.write_bytes(b"broken")
        self.assertIsNone(manager._download_model("face_detect"))
        self.assertEqual(b"broken", local.read_bytes())
        self.assertEqual(["face_detect"], downloads)

    def test_import_skips_default_downloads_when_marker_exists(self):
        patcher.patch(self.root, True)
        (self.custom / "skip_download_model").write_text("managed")
        calls = []
        class Manager:
            @classmethod
            def get_instance(cls): return cls()
            def validate_model_availability(self): return {"face": False}
            def initialize_default_models(self): calls.append("download")
        namespace = {"__file__": str(self.root / "__init__.py"), "ForbiddenVisionModelManager": Manager}
        exec((self.root / "__init__.py").read_text(encoding="utf-8"), namespace)
        self.assertEqual({"face": False}, namespace["initialize_forbidden_vision"]())
        self.assertEqual([], calls)


if __name__ == "__main__":
    unittest.main()
