"""Run the bundled path probe in a clean interpreter, without ComfyUI dependencies."""

import json
from pathlib import Path
import subprocess
import sys
import textwrap
import venv

import pytest


PROBE = Path(__file__).resolve().parents[1] / "launcher/scripts/probe_comfy.py"


@pytest.fixture(scope="module")
def clean_python(tmp_path_factory):
    root = tmp_path_factory.mktemp("probe-venv")
    venv.EnvBuilder(with_pip=False).create(root)
    return root / ("Scripts/python.exe" if sys.platform == "win32" else "bin/python")


@pytest.fixture
def comfy_root(tmp_path):
    root = tmp_path / "ComfyUI 中文 空格"
    (root / "comfy").mkdir(parents=True)
    (root / "utils").mkdir()
    (root / "comfy/__init__.py").write_text("", encoding="utf-8")
    (root / "utils/__init__.py").write_text("", encoding="utf-8")
    (root / "comfy/options.py").write_text(
        "def enable_args_parsing():\n    pass\n", encoding="utf-8"
    )
    (root / "folder_paths.py").write_text(textwrap.dedent("""\
        import argparse
        import os
        from pathlib import Path

        parser = argparse.ArgumentParser()
        parser.add_argument('--base-directory', default=os.getcwd())
        parser.add_argument('--models-directory')
        args = parser.parse_args()
        models_dir = args.models_directory or str(Path(args.base_directory) / 'models')
        folder_names_and_paths = {
            'diffusion_models': ([str(Path(models_dir) / 'unet'), str(Path(models_dir) / 'diffusion_models')], set()),
            'text_encoders': ([str(Path(models_dir) / 'clip'), str(Path(models_dir) / 'text_encoders')], set()),
            'loras': ([str(Path(models_dir) / 'loras')], set()),
        }
    """), encoding="utf-8")
    # A small ComfyUI loader contract fixture; JSON input below is also valid YAML.
    # The probe must delegate to this loader instead of ignoring user paths.
    (root / "utils/extra_config.py").write_text(textwrap.dedent("""\
        import yaml
        import folder_paths

        def load_extra_path_config(path):
            with open(path, encoding='utf-8') as source:
                paths = yaml.safe_load(source)
            for key, values in paths.items():
                key = {'unet': 'diffusion_models', 'clip': 'text_encoders'}.get(key, key)
                old, extensions = folder_paths.folder_names_and_paths[key]
                folder_paths.folder_names_and_paths[key] = (values + [p for p in old if p not in values], extensions)
            print('Loaded extra model paths')
    """), encoding="utf-8")
    return root


def run_probe(python, root, *arguments):
    # Ignore inherited Python paths and site packages even if the host has PyYAML.
    return subprocess.run(
        [str(python), "-I", "-S", "-B", "-X", "utf8", str(PROBE), str(root), *map(str, arguments)],
        capture_output=True, text=True, encoding="utf-8", timeout=30, check=False,
    )


def test_clean_venv_without_extra_paths_does_not_require_yaml(clean_python, comfy_root):
    result = run_probe(clean_python, comfy_root)
    assert result.returncode == 0, result.stderr
    actual = json.loads(result.stdout)
    models = comfy_root / "models"
    assert Path(actual["python"]) == clean_python
    assert actual["paths"]["diffusion_models"] == [str(models / "diffusion_models"), str(models / "unet")]
    assert actual["paths"]["text_encoders"] == [str(models / "text_encoders"), str(models / "clip")]
    assert actual["paths"]["wd_taggers"] == [str(models / "wd_taggers")]


@pytest.mark.parametrize("argument, suffix", [("--base-directory", "models"), ("--models-directory", "")])
def test_clean_venv_preserves_model_directory_arguments(clean_python, comfy_root, tmp_path, argument, suffix):
    alternate = tmp_path / "外部 模型"
    result = run_probe(clean_python, comfy_root, argument, alternate)
    assert result.returncode == 0, result.stderr
    actual = json.loads(result.stdout)
    models = alternate / suffix
    assert actual["paths"]["diffusion_models"][0] == str(models / "diffusion_models")
    assert actual["paths"]["wd_taggers"] == [str(models / "wd_taggers")]


def test_extra_paths_with_missing_yaml_still_fail(clean_python, comfy_root):
    (comfy_root / "extra_model_paths.yaml").write_text("{}", encoding="utf-8")
    result = run_probe(clean_python, comfy_root)
    assert result.returncode != 0
    assert "No module named 'yaml'" in result.stderr
    assert not result.stdout.strip()


def test_extra_paths_loader_preserves_explicit_legacy_defaults(clean_python, comfy_root, tmp_path):
    (comfy_root / "yaml.py").write_text("from json import load as safe_load\n", encoding="utf-8")
    models = comfy_root / "models"
    loras = [str(tmp_path / "额外 甲"), str(tmp_path / "额外 乙")]
    (comfy_root / "extra_model_paths.yaml").write_text(json.dumps({
        "unet": [str(models / "unet")], "clip": [str(models / "clip")], "loras": loras,
    }), encoding="utf-8")
    result = run_probe(clean_python, comfy_root)
    assert result.returncode == 0, result.stderr
    actual = json.loads(result.stdout)
    assert actual["paths"]["diffusion_models"] == [str(models / "unet"), str(models / "diffusion_models")]
    assert actual["paths"]["text_encoders"] == [str(models / "clip"), str(models / "text_encoders")]
    assert actual["paths"]["loras"] == [*loras, str(models / "loras")]


def test_invalid_extra_paths_are_not_silently_replaced_with_defaults(clean_python, comfy_root):
    (comfy_root / "yaml.py").write_text("from json import load as safe_load\n", encoding="utf-8")
    (comfy_root / "extra_model_paths.yaml").write_text("invalid config", encoding="utf-8")
    result = run_probe(clean_python, comfy_root)
    assert result.returncode != 0
    assert "JSONDecodeError" in result.stderr
    assert not result.stdout.strip()


def test_broken_folder_paths_still_fail(clean_python, comfy_root):
    (comfy_root / "folder_paths.py").write_text("raise RuntimeError('broken folder paths')\n", encoding="utf-8")
    result = run_probe(clean_python, comfy_root)
    assert result.returncode != 0
    assert "broken folder paths" in result.stderr
    assert not result.stdout.strip()
