"""Refresh immutable tagger package locks from the official Hugging Face APIs."""
import hashlib
import json
from pathlib import Path
from urllib.request import urlopen

REPOS = [
    "SmilingWolf/wd-eva02-large-tagger-v3", "SmilingWolf/wd-vit-large-tagger-v3",
    "SmilingWolf/wd-vit-tagger-v3", "SmilingWolf/wd-swinv2-tagger-v3",
    "SmilingWolf/wd-convnext-tagger-v3", "Bedovyy/pixai-tagger-v1.0-timm",
    "Bedovyy/pixai-tagger-v0.9-timm", "Bedovyy/OppaiOracle-V1.1-timm",
]
REQUIRED = {"config.json", "selected_tags.csv", "model.safetensors"}
ALLOWED = REQUIRED | {"preprocess.json", "timm_arch.py"}


def main():
    models = []
    for repo in REPOS:
        with urlopen(f"https://huggingface.co/api/models/{repo}?blobs=true", timeout=60) as response:
            info = json.load(response)
        revision = info["sha"]
        files = []
        for entry in info["siblings"]:
            name = entry["rfilename"]
            if name not in ALLOWED:
                continue
            url = f"https://huggingface.co/{repo}/resolve/{revision}/{name}"
            sha = entry.get("lfs", {}).get("sha256")
            if not sha:
                with urlopen(url, timeout=60) as response:
                    content = response.read()
                assert len(content) == entry["size"], name
                sha = hashlib.sha256(content).hexdigest()
            files.append({"path": name, "size": entry["size"], "sha256": sha, "urls": [url]})
        assert REQUIRED <= {file["path"] for file in files}, repo
        name = repo.split("/")[-1]
        license_id = info.get("cardData", {}).get("license", "See model card")
        models.append({"id": "tagger-" + name, "name": name, "group": "本地反推模型（可选）",
                       "category": "wd_taggers", "relativePath": name, "aliases": [],
                       "size": sum(file["size"] for file in files), "sha256": "", "urls": [],
                       "license": license_id, "licenseUrl": f"https://huggingface.co/{repo}/blob/{revision}/README.md",
                       "sourceUrl": f"https://huggingface.co/{repo}", "revision": revision,
                       "recommended": name == "pixai-tagger-v0.9-timm", "files": files})
        print(name, revision, len(files), models[-1]["size"])
    target = Path(__file__).resolve().parents[2] / "anima_webui/tagger-models.json"
    target.write_text(json.dumps({"schemaVersion": 1, "models": models}, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
