"""Cross-platform release gates shared by local builds and GitHub Actions."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import re
import subprocess
import sys
import tomllib
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]


def run(*command: str) -> None:
    print("CHECK: " + " ".join(command), flush=True)
    subprocess.run(command, cwd=ROOT, check=True)


def check_metadata() -> str:
    version = (ROOT / "launcher/version.txt").read_text(encoding="utf-8").strip()
    if not re.fullmatch(r"\d+\.\d+\.\d+", version):
        raise ValueError("launcher/version.txt must contain a release version")
    project = tomllib.loads((ROOT / "pyproject.toml").read_text(encoding="utf-8"))
    if project["tool"]["setuptools"]["dynamic"]["version"] != {"file": "launcher/version.txt"}:
        raise ValueError("Python package must use launcher/version.txt")
    manifest = json.loads((ROOT / "launcher/manifests/launcher-manifest.json").read_text(encoding="utf-8"))
    if manifest["version"] != version:
        raise ValueError("Launcher manifest version differs from release version")
    identity = ET.parse(ROOT / "launcher/Anima.Launcher.WinUI/app.manifest").find("{urn:schemas-microsoft-com:asm.v1}assemblyIdentity")
    if identity is None or identity.attrib.get("version") != version + ".0":
        raise ValueError("Windows application manifest version differs from release version")
    for path in (ROOT / "templates").glob("*.json"):
        json.loads(path.read_text(encoding="utf-8"))
    print(f"Release version and templates: {version}", flush=True)
    return version


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--skip-tests", action="store_true", help="Only metadata, lint and syntax checks")
    args = parser.parse_args()
    check_metadata()
    run(sys.executable, "scripts/check_release_content.py")
    run(sys.executable, "launcher/scripts/export_model_profiles.py", "--check")
    run(sys.executable, "-m", "ruff", "check", "anima_webui", "tests", "scripts", "run.py", "generate_templates.py")
    run(sys.executable, "-m", "compileall", "-q", "anima_webui", "tests", "scripts", "run.py", "generate_templates.py")
    for path in sorted((ROOT / "static").glob("*.js")):
        run("node", "--check", str(path))
    if not args.skip_tests:
        run(sys.executable, "-m", "pytest", "-q")
        run("node", "--test", *map(str, sorted((ROOT / "tests").glob("*.test.cjs"))))
        run(sys.executable, "launcher/scripts/test_prompt_assistant_patch.py")
        run(sys.executable, "launcher/scripts/test_patch_forbidden_vision.py")


if __name__ == "__main__":
    main()
