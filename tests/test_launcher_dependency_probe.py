"""Interpreter errors must be actionable before the target needs TOML or packages."""
import builtins
import importlib.util
from pathlib import Path
import sys

import pytest


@pytest.fixture
def probe(monkeypatch):
    root = Path(__file__).resolve().parents[1]
    monkeypatch.syspath_prepend(str(root / "launcher/vendor/packaging-26.0.zip"))
    spec = importlib.util.spec_from_file_location("launcher_dependency_probe", root / "launcher/scripts/probe_dependencies.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


@pytest.mark.parametrize("version,bits", [((3, 10, 14), 64), ((3, 11, 9), 32)])
def test_old_or_32_bit_python_fails_before_toml(probe, monkeypatch, version, bits):
    monkeypatch.setattr(sys, "version_info", version)
    monkeypatch.setattr(probe.struct, "calcsize", lambda _: bits // 8)
    real_import = builtins.__import__

    def guarded_import(name, *args, **kwargs):
        assert name != "tomllib", "unsupported Python must be rejected before importing TOML"
        return real_import(name, *args, **kwargs)

    monkeypatch.setattr(builtins, "__import__", guarded_import)
    result = probe.interpreter_compatibility("does-not-exist")
    assert not result["ready"]
    assert result["version"] == ".".join(map(str, version))
    assert result["bits"] == bits
    assert "64 位" in result["detail"] and "3.11" in result["detail"]


@pytest.mark.parametrize("required,ready", [(">=99", False), (">=3.11", True)])
def test_selected_workbench_python_range(probe, tmp_path, required, ready):
    (tmp_path / "pyproject.toml").write_text(f"[project]\nrequires-python='{required}'\ndependencies=[]\n", encoding="utf-8")
    result = probe.interpreter_compatibility(str(tmp_path))
    assert result["ready"] is ready
    assert result["requiresPython"] == required
    if not ready:
        assert required in result["detail"]
