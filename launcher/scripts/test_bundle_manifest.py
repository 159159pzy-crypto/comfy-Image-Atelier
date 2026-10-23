"""Offline regression checks for the single-file bundle inventory."""
from __future__ import annotations

import hashlib
from pathlib import Path
import tempfile
import unittest

from export_bundle_manifest import export


class BundleManifestTests(unittest.TestCase):
    def test_exact_inventory_excludes_host_and_itself(self):
        with tempfile.TemporaryDirectory(prefix="Anima bundle 中文 ") as temporary:
            root = Path(temporary)
            source = root / "runtime file.dll"
            source.write_bytes(b"runtime bytes")
            inputs, output = root / "inputs", root / "FILES.sha256"
            inputs.write_text(f"{source}|AnimaRandomStudio.exe\n{source}|FILES.sha256\n{source}|目录/runtime file.dll\n", encoding="utf-8-sig")
            export(inputs, "AnimaRandomStudio.exe", output)
            self.assertEqual(output.read_text(encoding="utf-8"), hashlib.sha256(source.read_bytes()).hexdigest() + "  目录/runtime file.dll\n")

    def test_rejects_unsafe_paths_and_case_collisions(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "source"
            source.write_bytes(b"runtime")
            inputs, output = root / "inputs", root / "FILES.sha256"
            for relative in ("../escape.dll", "/absolute.dll", "nested//file.dll", "C:/file.dll", "nested/./file.dll"):
                with self.subTest(relative=relative):
                    inputs.write_text(f"{source}|{relative}\n", encoding="utf-8")
                    with self.assertRaises(ValueError):
                        export(inputs, "AnimaRandomStudio.exe", output)
            inputs.write_text(f"{source}|Runtime.dll\n{source}|runtime.dll\n", encoding="utf-8")
            with self.assertRaises(ValueError):
                export(inputs, "AnimaRandomStudio.exe", output)


if __name__ == "__main__":
    unittest.main()
