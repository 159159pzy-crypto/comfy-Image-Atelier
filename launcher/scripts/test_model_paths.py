"""Verify model paths against copied real ComfyUI modules, without importing the source.

Run with --comfy-root <existing ComfyUI> --output <test artifact directory>.
Use --python when the ComfyUI interpreter is not in a standard venv/portable path.
All fixtures and evidence are written under --output, never into --comfy-root.
"""
from __future__ import annotations
import argparse
from datetime import datetime
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys

REPO = next(parent for parent in Path(__file__).resolve().parents
            if (parent / "launcher/scripts/probe_comfy.py").is_file())
SOURCE_FILES = ("folder_paths.py", "utils/extra_config.py", "comfy/cli_args.py", "comfy/options.py")
HASH_FILES = (*SOURCE_FILES, "utils/__init__.py", "comfy/__init__.py", "extra_model_paths.yaml")
RAW_CAPTURE = r"""
import json, os, sys
from pathlib import Path
root = Path(sys.argv[1]).resolve()
os.chdir(root)
sys.path.insert(0, str(root))
sys.argv = [str(root / "main.py")]
import folder_paths
from utils.extra_config import load_extra_path_config
if (root / "extra_model_paths.yaml").is_file():
    load_extra_path_config(str(root / "extra_model_paths.yaml"))
print(json.dumps({
    "module_path": folder_paths.__file__,
    "paths": {key: list(value[0]) for key, value in folder_paths.folder_names_and_paths.items()},
    "legacy_model": folder_paths.get_full_path("diffusion_models", "legacy-test.safetensors"),
    "legacy_encoder": folder_paths.get_full_path("text_encoders", "legacy-encoder.safetensors"),
    "loaded_torch": any(key == "torch" or key.startswith("torch.") for key in sys.modules),
}, ensure_ascii=True))
"""

def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest() if path.is_file() else None

def run_json(command, cwd):
    env = dict(os.environ, PYTHONDONTWRITEBYTECODE="1", PYTHONUTF8="1")
    result = subprocess.run(command, cwd=cwd, env=env, encoding="utf-8",
                            capture_output=True, timeout=30, check=False)
    if result.returncode:
        raise RuntimeError(f"Command failed ({result.returncode}): {result.stderr}")
    return json.loads(result.stdout)

def fixture(case_dir, source, yaml_text):
    case_dir.mkdir(parents=True)
    for relative in SOURCE_FILES:
        target = case_dir / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source / relative, target)
    for package in ("utils", "comfy"):
        original = source / package / "__init__.py"
        if original.is_file():
            shutil.copyfile(original, case_dir / package / "__init__.py")
        else:
            (case_dir / package / "__init__.py").write_text("", encoding="utf-8")
    (case_dir / "main.py").write_text("# Path-only acceptance fixture.\n", encoding="utf-8")
    for relative in ("models/unet", "models/diffusion_models", "models/clip", "models/text_encoders",
                     "models/loras", "input", "额外 资源/底模 新", "额外 资源/LoRA 甲",
                     "额外 资源/LoRA 乙", "额外 资源/旧式 UNet", "额外 资源/旧式 CLIP"):
        (case_dir / relative).mkdir(parents=True, exist_ok=True)
    (case_dir / "models/unet/legacy-test.safetensors").write_bytes(b"path-fixture")
    (case_dir / "models/clip/legacy-encoder.safetensors").write_bytes(b"path-fixture")
    if yaml_text is not None:
        (case_dir / "extra_model_paths.yaml").write_text(yaml_text, encoding="utf-8")

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--comfy-root", type=Path, required=True,
                        help="Existing ComfyUI directory containing folder_paths.py (read only).")
    parser.add_argument("--probe", type=Path, default=REPO / "launcher/scripts/probe_comfy.py",
                        help="Launcher probe to test; defaults to this repository's probe_comfy.py.")
    parser.add_argument("--output", type=Path, required=True,
                        help="Artifact directory outside ComfyUI; a unique run subdirectory is created.")
    parser.add_argument("--python", type=Path,
                        help="Python with PyYAML; auto-detects the ComfyUI venv/portable interpreter.")
    args = parser.parse_args()
    source, probe, output = args.comfy_root.resolve(), args.probe.resolve(), args.output.resolve()
    requested_root = source
    if not (source / "folder_paths.py").is_file() and (source / "ComfyUI/folder_paths.py").is_file():
        source = source / "ComfyUI"
    if any(output == root or root in output.parents for root in (requested_root, source)):
        parser.error("--output must be outside --comfy-root; the source environment is read only.")
    if not probe.is_file():
        parser.error(f"Probe not found: {probe}")
    candidates = [source / relative for relative in (
        ".venv/Scripts/python.exe", "venv/Scripts/python.exe", ".venv/bin/python", "venv/bin/python",
        "python_embeded/python.exe", "python_embedded/python.exe")]
    candidates += [source.parent / relative for relative in (
        "python_embeded/python.exe", "python_embedded/python.exe")]
    python = args.python.resolve() if args.python else next((p.resolve() for p in candidates if p.is_file()), None)
    if python is None or not python.is_file():
        parser.error("ComfyUI Python was not found; provide --python with an interpreter that has PyYAML.")
    source_before = {name: digest(source / name) for name in HASH_FILES}
    for name in SOURCE_FILES:
        if source_before[name] is None:
            parser.error(f"Required ComfyUI module not found: {source / name}")
    run_dir = output / ("run-" + datetime.now().strftime("%Y%m%d-%H%M%S-%f"))
    run_dir.mkdir(parents=True)
    cases = [
        ("01-defaults", None, False),
        ("02-extra-multiple-loras", """shared:
  base_path: 额外 资源
  diffusion_models: 底模 新
  loras: |
    LoRA 甲
    LoRA 乙
""", False),
        ("03-explicit-default", """preferred:
  base_path: 额外 资源
  is_default: true
  diffusion_models: 底模 新
  loras: |
    LoRA 甲
    LoRA 乙
""", True),
        ("04-legacy-alias-default", """legacy:
  base_path: 额外 资源
  is_default: true
  unet: 旧式 UNet
  clip: 旧式 CLIP
  loras: LoRA 甲
""", True),
        ("05-explicit-local-legacy-default", """preferred:
  base_path: .
  is_default: true
  unet: models/unet
  clip: models/clip
  loras: models/loras
""", True),
    ]
    results = []
    for name, yaml_text, explicit_default in cases:
        case_dir = run_dir / ("中文 空格夹具 " + name)
        fixture(case_dir, source, yaml_text)
        raw = run_json([str(python), "-B", "-c", RAW_CAPTURE, str(case_dir)], case_dir)
        actual = run_json([str(python), "-B", str(probe), str(case_dir)], case_dir)
        checks = []
        def check(label, condition):
            checks.append({"check": label, "pass": bool(condition)})
        check("Real copied folder_paths.py is loaded", Path(raw["module_path"]).resolve() == case_dir / "folder_paths.py")
        check("No torch import", not raw["loaded_torch"])
        check("Probe interpreter is the requested ComfyUI Python", Path(actual["python"]).resolve() == python)
        check("Tagger root follows actual folder_paths.models_dir", actual["paths"]["wd_taggers"] == [str(case_dir / "models/wd_taggers")])
        override = case_dir / "独立 模型目录"
        override.mkdir()
        overridden = run_json([str(python), "-B", str(probe), str(case_dir), "--models-directory", str(override)], case_dir)
        check("Explicit models-directory reaches the probe", overridden["paths"]["wd_taggers"] == [str(override / "wd_taggers")])
        based = run_json([str(python), "-B", str(probe), str(case_dir), "--base-directory", str(override)], case_dir)
        check("Explicit base-directory reaches the probe", based["paths"]["wd_taggers"] == [str(override / "models/wd_taggers")])
        check("LoRA paths and ordering exactly match real ComfyUI", actual["paths"]["loras"] == raw["paths"]["loras"])
        check("All diffusion roots remain searchable", set(actual["paths"]["diffusion_models"]) == set(raw["paths"]["diffusion_models"]))
        check("All encoder roots remain searchable", set(actual["paths"]["text_encoders"]) == set(raw["paths"]["text_encoders"]))
        check("Legacy UNet marker resolves via actual ComfyUI", Path(raw["legacy_model"]) == case_dir / "models/unet/legacy-test.safetensors")
        check("Legacy CLIP marker resolves via actual ComfyUI", Path(raw["legacy_encoder"]) == case_dir / "models/clip/legacy-encoder.safetensors")
        if explicit_default:
            check("Explicit diffusion default is respected", actual["paths"]["diffusion_models"][0] == raw["paths"]["diffusion_models"][0])
            check("Explicit encoder default is respected", actual["paths"]["text_encoders"][0] == raw["paths"]["text_encoders"][0])
        else:
            check("Without explicit default new models target diffusion_models", actual["paths"]["diffusion_models"][0] == str(case_dir / "models/diffusion_models"))
            check("Without explicit default new encoders target text_encoders", actual["paths"]["text_encoders"][0] == str(case_dir / "models/text_encoders"))
        if yaml_text and "LoRA 乙" in yaml_text:
            check("Three LoRA roots include both Unicode/space extras", len(actual["paths"]["loras"]) == 3
                  and str(case_dir / "额外 资源/LoRA 甲") in actual["paths"]["loras"]
                  and str(case_dir / "额外 资源/LoRA 乙") in actual["paths"]["loras"])
        record = {"case": name, "fixture": str(case_dir), "yaml": yaml_text, "checks": checks,
                  "comfyui_paths": {key: raw["paths"][key] for key in ("loras", "diffusion_models", "text_encoders")},
                  "probe_paths": {key: actual["paths"][key] for key in ("loras", "diffusion_models", "text_encoders")},
                  "loras_menu_input": actual["paths"]["loras"], "raw": raw, "probe": actual}
        (case_dir / "evidence.json").write_text(json.dumps(record, ensure_ascii=False, indent=2), encoding="utf-8")
        results.append(record)
    source_after = {name: digest(source / name) for name in source_before}
    passed = sum(check["pass"] for case in results for check in case["checks"])
    count = sum(len(case["checks"]) for case in results)
    report = {"source": str(source), "python": str(python), "probe": str(probe),
              "probe_sha256": digest(probe), "source_hashes_before": source_before,
              "source_hashes_after": source_after, "source_unchanged": source_before == source_after,
              "actual_source_yaml_present": (source / "extra_model_paths.yaml").is_file(),
              "passed": passed, "total": count, "cases": results}
    report_json = run_dir / "path-probe-report.json"
    report_json.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    lines = ["# ComfyUI path probe acceptance", "",
             f"- Source modules: {source}", f"- Python: {python}", f"- Probe: {probe}",
             f"- Result: {passed}/{count} assertions pass.",
             f"- Original source hashes unchanged: {source_before == source_after}.",
             f"- Real source extra_model_paths.yaml exists: {report['actual_source_yaml_present']}.",
             "- Every fixture uses byte-for-byte copies of the real folder_paths, extra_config, cli_args and options modules.",
             "- Subprocesses use -B and PYTHONDONTWRITEBYTECODE=1. Only the specified output directory is written.",
             "- Per-case evidence.json stores actual ComfyUI and launcher probe outputs; no mirror implementation is tested.", ""]
    for case in results:
        failures = [check["check"] for check in case["checks"] if not check["pass"]]
        lines += [f"## {case['case']}", "", f"Result: {'FAIL' if failures else 'PASS'}", ""]
        if failures:
            lines += ["- " + failure for failure in failures] + [""]
        for key in ("diffusion_models", "text_encoders", "loras"):
            lines += [f"{key} launcher menu/order:", ""]
            lines += [f"{i + 1}. {value}" for i, value in enumerate(case["probe_paths"][key])] + [""]
        if failures:
            lines += ["Actual ComfyUI explicit defaults:"]
            lines += [f"- {key}: {case['comfyui_paths'][key][0]}" for key in ("diffusion_models", "text_encoders")] + [""]
    lines += ["## UI integration boundary", "",
              "LauncherState.OpenModelFolderAsync consumes probe.Paths[category], normalizes/distincts paths, and labels the first entry as default. "
              "loras_menu_input arrays are the exact menu input. This verifies parser/menu data, not a native menu click.",
              "", "## Re-run", "",
              f'"{sys.executable}" -B "{Path(__file__).resolve()}" --comfy-root "{source}" --python "{python}" --probe "{probe}" --output "{output}"', ""]
    (run_dir / "path-probe-report.md").write_text("\n".join(lines), encoding="utf-8")
    print(json.dumps({"passed": passed, "total": count, "source_unchanged": source_before == source_after,
                     "report": str(report_json), "failures": [
                         {"case": case["case"], "checks": [check["check"] for check in case["checks"] if not check["pass"]]}
                         for case in results if any(not check["pass"] for check in case["checks"])]}, ensure_ascii=True))
    return 0 if passed == count and source_before == source_after else 1

if __name__ == "__main__":
    raise SystemExit(main())
