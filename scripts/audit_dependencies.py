"""Audit managed pins or resolved source dependencies; fail on incomplete scans."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import tomllib

ROOT = Path(__file__).resolve().parents[1]


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--source", action="store_true",
                        help="Resolve pyproject runtime dependencies including transitives")
    args = parser.parse_args()
    if args.source:
        project = tomllib.loads((ROOT / "pyproject.toml").read_text(encoding="utf-8"))
        requirements = project["project"]["dependencies"]
    else:
        manifest = json.loads((ROOT / "launcher/manifests/launcher-manifest.json").read_text(encoding="utf-8"))
        # CUDA wheel suffixes are not PyPI versions. Audit their upstream pins.
        requirements = [line.split("+")[0] if line.startswith(("torch==", "torchvision==", "torchaudio==")) else line
                        for line in manifest["pythonConstraints"]]
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.unlink(missing_ok=True)
    with tempfile.TemporaryDirectory() as directory:
        path = Path(directory) / "runtime.txt"
        path.write_text("\n".join(requirements) + "\n", encoding="utf-8")
        options = [] if args.source else ["--no-deps", "--disable-pip"]
        result = subprocess.run([sys.executable, "-m", "pip_audit", *options, "-r", str(path),
                                 "--strict", "--format", "json", "--output", str(args.output)], cwd=ROOT)
    if result.returncode not in (0, 1) or not args.output.is_file():
        raise SystemExit("Dependency audit did not complete")
    report = json.loads(args.output.read_text(encoding="utf-8"))
    # Match distribution names independently of version specifiers.
    expected = {re.split(r"[\s<>=!~;\[]", line, maxsplit=1)[0].lower().replace("_", "-") for line in requirements}
    actual = {item["name"].lower().replace("_", "-") for item in report.get("dependencies", [])}
    complete = expected.issubset(actual) if args.source else actual == expected
    if not complete:
        raise SystemExit("Dependency audit is incomplete")
    blockers, reviewed = [], []
    for dependency in report.get("dependencies", []):
        if dependency.get("skip_reason"):
            blockers.append(dependency["skip_reason"])
        for advisory in dependency.get("vulns", []):
            identities = {advisory["id"], *advisory.get("aliases", [])}
            # This exception is version-specific and is documented with its
            # exact trigger and supported scope in docs/dependencies.md.
            if (not args.source and dependency["name"] == "torch" and dependency.get("version") == "2.11.0"
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
    scope = "resolved runtime packages" if args.source else "pinned packages"
    print(f"Audit complete: {len(report['dependencies'])} {scope}, {len(reviewed)} explicitly reviewed upstream limitation(s), no unreviewed findings.")


if __name__ == "__main__":
    main()
