"""Export the WebUI's profile contract without importing the server or workflow."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import runpy


def export_profiles(root: Path) -> dict:
    source = runpy.run_path(str(root / "anima_webui" / "model_profiles.py"))
    fields = {
        "id": "id", "label": "label", "model_name": "modelName",
        "clip_name": "clipName", "vae_name": "vaeName", "clip_type": "clipType",
        "seedvr2_model": "seedvr2Model", "seedvr2_vae": "seedvr2Vae",
    }
    return {
        "schemaVersion": source["MODEL_PROFILE_VERSION"],
        "defaultProfile": source["DEFAULT_MODEL_PROFILE"],
        "profiles": [
            {target: profile[name] for name, target in fields.items()}
            for profile in source["MODEL_PROFILES"].values()
        ],
    }


def main() -> None:
    root = Path(__file__).resolve().parents[2]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=root / "launcher" / "manifests" / "model-profiles.json")
    parser.add_argument("--check", action="store_true", help="Fail if the exported contract has drifted; do not write.")
    args = parser.parse_args()
    profiles = export_profiles(root)
    if args.check:
        if not args.output.exists() or json.loads(args.output.read_text(encoding="utf-8")) != profiles:
            parser.error(f"Model profiles have changed; run this script to update {args.output}.")
        print("Model profiles match the WebUI contract.")
    else:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(profiles, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        print(f"Exported {len(profiles['profiles'])} model profiles to {args.output}.")


if __name__ == "__main__":
    main()
