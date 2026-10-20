"""The source dependency gate must reject findings and incomplete scans."""
import json
import sys
from types import SimpleNamespace

import pytest

from scripts import audit_dependencies


def run_audit(tmp_path, monkeypatch, dependencies, returncode=0):
    output = tmp_path / "audit.json"
    monkeypatch.setattr(sys, "argv", ["audit", "--source", "--output", str(output)])

    def audit(command, **kwargs):
        assert "--strict" in command
        assert "--no-deps" not in command
        assert "--disable-pip" not in command
        output.write_text(json.dumps({"dependencies": dependencies}), encoding="utf-8")
        return SimpleNamespace(returncode=returncode)

    monkeypatch.setattr(audit_dependencies.subprocess, "run", audit)
    audit_dependencies.main()


def clean_report():
    return [{"name": name, "version": "1", "vulns": []}
            for name in ("aiohttp", "multidict", "Pillow", "yarl")]


def test_source_audit_includes_transitive_dependencies(tmp_path, monkeypatch):
    run_audit(tmp_path, monkeypatch, clean_report())


@pytest.mark.parametrize("kind", ["missing", "skipped", "vulnerable", "failed"])
def test_source_audit_fails_closed(tmp_path, monkeypatch, kind):
    dependencies = clean_report()
    if kind == "missing":
        dependencies.pop(0)
    elif kind == "skipped":
        dependencies[-1]["skip_reason"] = "advisory service unavailable"
    elif kind == "vulnerable":
        dependencies[-1]["vulns"] = [{"id": "TEST-transitive-vulnerability"}]
    with pytest.raises(SystemExit):
        run_audit(tmp_path, monkeypatch, dependencies, returncode=2 if kind == "failed" else 0)
