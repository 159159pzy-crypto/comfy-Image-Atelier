"""Collect restored NuGet licenses and runtime notices into a release directory."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import re
import xml.etree.ElementTree as ET


def export(assets: Path, output: Path) -> None:
    data = json.loads(assets.read_text(encoding="utf-8"))
    roots = [Path(path) for path in data["packageFolders"]]
    packages = {name: item["path"] for name, item in data["libraries"].items() if item["type"] == "package"}
    for framework in data["project"]["frameworks"].values():
        for item in framework.get("downloadDependencies", []):
            versions = re.findall(r"[\d]+\.[\d]+\.[\d]+(?:\.[\d]+)?", item["version"])
            if not versions or len(set(versions)) != 1:
                raise ValueError("Runtime package must be pinned: " + item["name"])
            version = versions[0]
            packages[f'{item["name"]}/{version}'] = f'{item["name"].lower()}/{version}'
    inventory, notices = [], []
    for identity, relative in sorted(packages.items()):
        directory = next((root / relative for root in roots if (root / relative).is_dir()), None)
        if directory is None:
            raise FileNotFoundError("Restored package not found: " + identity)
        nuspec = next(directory.glob("*.nuspec"))
        metadata = {element.tag.rsplit("}", 1)[-1]: (element.text or "").strip()
                    for element in ET.parse(nuspec).getroot().findall(".//*")}
        paths = {path for path in directory.iterdir() if path.is_file() and (
            "license" in path.name.lower() or "thirdpartynotice" in path.name.lower())}
        declared = metadata.get("license", "")
        declared_path = (directory / declared).resolve()
        if declared and declared_path.is_relative_to(directory.resolve()) and declared_path.is_file():
            paths.add(declared_path)
        if identity.startswith("H."):
            paths.add(Path(__file__).resolve().parents[1] / "licenses/H.NotifyIcon.txt")
        entry = {"package": identity, "license": declared or metadata.get("licenseUrl", ""),
                 "project": metadata.get("projectUrl", ""), "authors": metadata.get("authors", ""),
                 "notice_files": sorted(path.name for path in paths)}
        inventory.append(entry)
        notices.append("\n" + "=" * 72 + "\n" + identity + "\n" +
                       "License: " + entry["license"] + "\nProject: " + entry["project"] + "\n")
        for path in sorted(paths):
            notices.append("\n--- " + path.name + " ---\n" + path.read_text(encoding="utf-8-sig", errors="replace"))
    output.mkdir(parents=True, exist_ok=True)
    introduction = (
        "Anima Random Studio - third-party notices\n"
        "This inventory covers restored launcher packages and runtime packs, including build-only dependencies.\n"
        "Not every listed package is redistributed. Original notices are reproduced below.\n"
        "ComfyUI, Python packages, custom nodes and models are downloaded separately and retain their own licenses.\n"
        "H.NotifyIcon license source: https://github.com/HavenDV/H.NotifyIcon/blob/f7dc5cbb9b7a598236148af30b50d675d85c32c2/LICENSE.md\n"
    )
    (output / "THIRD-PARTY-NOTICES.txt").write_text(introduction + "\n".join(notices), encoding="utf-8")
    (output / "third-party-packages.json").write_text(json.dumps(inventory, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"Exported license metadata and original notices for {len(inventory)} packages.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--assets", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    export(args.assets, args.output)
