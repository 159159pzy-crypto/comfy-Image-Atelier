"""Audit the pinned managed runtime; fail on new or unreviewed findings."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[1]


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    manifest = json.loads((ROOT / "launcher/manifests/launcher-manifest.json").read_text(encoding="utf-8"))
    # The CUDA wheel suffix is not a PyPI project version. Query advisories for
    # its exact upstream version instead of silently skipping the GPU packages.
    requirements = [line.split("+")[0] if line.startswith(("torch==", "torchvision==", "torchaudio==")) else line
                    for line in manifest["pythonConstraints"]]
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.unlink(missing_ok=True)
    with tempfile.TemporaryDirectory() as directory:
        path = Path(directory) / "runtime.txt"
        path.write_text("\n".join(requirements) + "\n", encoding="utf-8")
        result = subprocess.run([sys.executable, "-m", "pip_audit", "--no-deps", "--disable-pip", "-r", str(path),
                                 "--format", "json", "--output", str(args.output)], cwd=ROOT)
    if result.returncode not in (0, 1) or not args.output.is_file():
        raise SystemExit("Dependency audit did not complete")
    report = json.loads(args.output.read_text(encoding="utf-8"))
    expected = {line.split("==", 1)[0].lower().replace("_", "-") for line in requirements}
    actual = {item["name"].lower().replace("_", "-") for item in report.get("dependencies", [])}
    if actual != expected:
        raise SystemExit("Dependency audit is incomplete")
    blockers, reviewed = [], []
    for dependency in report.get("dependencies", []):
        if dependency.get("skip_reason"):
            blockers.append(dependency["skip_reason"])
        for advisory in dependency.get("vulns", []):
            identities = {advisory["id"], *advisory.get("aliases", [])}
            # This exception is version-specific and is documented with its
            # exact trigger and supported scope in docs/dependencies.md.
            if (dependency["name"] == "torch" and dependency.get("version") == "2.11.0"
                    and "GHSA-rrmf-rvhw-rf47" in identities):
                reviewed.append(f"torch 2.11.0 {advisory['id']}: malformed Python type annotations passed to torch.jit.script; unsupported by Studio input/workflow APIs")
            else:
                blockers.append(f"{dependency['name']} {dependency.get('version')} {advisory['id']}")
    for item in reviewed:
        print("REVIEWED UPSTREAM LIMITATION: " + item)
    for item in blockers:
        print("BLOCKING: " + item)
    if blockers:
        raise SystemExit(1)
    print(f"Audit complete: {len(report['dependencies'])} pinned packages, {len(reviewed)} explicitly reviewed upstream limitation(s), no unreviewed findings.")


if __name__ == "__main__":
    main()
