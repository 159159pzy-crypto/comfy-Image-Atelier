"""Offline regression for the embedded metadata builder against a locked plugin checkout.

Run: python launcher/scripts/test_model_metadata.py --plugin-root <ComfyUI-Lora-Manager>
Only two read-only source files are copied to a temporary test directory. No model,
sidecar, Python environment or running ComfyUI installation is modified.
"""

import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import textwrap


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--plugin-root", type=Path, required=True)
    options = parser.parse_args()
    core = Path(__file__).resolve().parents[1] / "Anima.Launcher.Core" / "ModelMetadata.cs"
    match = re.search(r'public const string BuildMetadataScript = """\n(.*?)\n\s*""";', core.read_text(encoding="utf-8"), re.S)
    assert match, "Embedded metadata builder must be present"
    script = textwrap.dedent(match.group(1))
    checks = 0

    def check(condition: bool, label: str) -> None:
        nonlocal checks
        assert condition, label
        checks += 1
        print("PASS: " + label)

    with tempfile.TemporaryDirectory(prefix="anima-metadata-tests-") as temporary:
        root = Path(temporary)
        plugin = root / "plugin"
        utils = plugin / "py" / "utils"
        utils.mkdir(parents=True)
        for name in ("models.py", "model_utils.py"):
            shutil.copyfile(options.plugin_root / "py" / "utils" / name, utils / name)
        model = root / "中文 模型" / "anima-base-v1.0.safetensors"
        model.parent.mkdir()
        model.write_bytes(b"Small fixture; C# validates the real model hash before invoking this builder.")
        digest = hashlib.sha256(model.read_bytes()).hexdigest()
        sidecar = model.with_suffix(".metadata.json")
        original = b'{"notes": "user content must remain untouched"}'
        sidecar.write_bytes(original)

        def build() -> subprocess.CompletedProcess[str]:
            return subprocess.run(
                [sys.executable, "-I", "-B", "-X", "utf8", "-c", script, str(plugin), str(model), digest],
                text=True, encoding="utf-8", capture_output=True, timeout=30,
            )

        result = build()
        check(result.returncode == 0, "locked official modules generate metadata: " + result.stderr)
        payload = json.loads(result.stdout)
        check(
            payload["base_model"] == "Anima" and payload["sub_type"] == "diffusion_model"
            and payload["sha256"] == digest and payload["hash_status"] == "completed"
            and payload["size"] == model.stat().st_size and payload["file_path"] == model.as_posix(),
            "classification and verified-file identity survive Unicode paths",
        )
        check(
            {"file_name", "model_name", "modified", "preview_url", "notes", "civitai", "tags",
             "trainedWords", "favorite", "exclude", "metadata_source", "last_checked_at",
             "from_civitai", "skip_metadata_refresh", "modelDescription"} <= payload.keys()
            and payload["from_civitai"] is False and payload["civitai"] == {},
            "official CheckpointMetadata emits its complete schema without fabricated Civitai data",
        )
        check(sidecar.read_bytes() == original, "builder has no sidecar write side effects")

        for source in utils.glob("*.py"):
            source.write_bytes(source.read_bytes().replace(b"\r\n", b"\n").replace(b"\n", b"\r\n"))
        check(build().returncode == 0, "Git CRLF checkout remains compatible with source hash verification")
        sentinel = root / "unexpected-code-executed"
        with (utils / "models.py").open("a", encoding="utf-8") as source:
            source.write("\nopen(" + repr(str(sentinel)) + ", 'w').write('bad')\n")
        result = build()
        check(result.returncode != 0 and "differs from locked revision" in result.stderr and not sentinel.exists(),
              "modified plugin source is rejected before executing it")
        check(not list(plugin.rglob("__pycache__")), "isolated builder leaves no plugin bytecode or application state")
    print(f"{checks} metadata builder checks passed")


if __name__ == "__main__":
    main()
