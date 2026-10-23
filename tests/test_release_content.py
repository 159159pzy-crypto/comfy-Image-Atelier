import pytest
import subprocess

from scripts import check_release_content as release_content

from scripts.check_release_content import MACHINE_PATH, check_path


@pytest.mark.parametrize("name", [
    "data/history.json", "output/report.json", "dist/package.zip",
    ".codex-local/notes.md", ".claude/settings.json", "launcher/Core/obj/build.json",
    "anima_webui/__pycache__/server.pyc", ".env", "AGENTS.md",
    "docs/acceptance.md", "docs/conversation.md", "AnimaRandomStudio.exe",
])
def test_local_files_are_rejected(name):
    with pytest.raises(ValueError):
        check_path(name)


@pytest.mark.parametrize("name", [
    "README.md", "launcher/README.md", "docs/dependencies.md",
    "CONTRIBUTING.md", "SECURITY.md", ".github/pull_request_template.md",
    "launcher/tests/fixtures/prompt-assistant/README.md",
    "launcher/manifests/launcher-manifest.json", "anima_webui/server.py",
    "docs/screenshots/anima-studio-single-workbench.png",
])
def test_product_files_are_accepted(name):
    check_path(name)


def test_machine_path_scan_allows_web_links():
    assert not MACHINE_PATH.search("[download](https://github.com/example/releases)")
    assert MACHINE_PATH.search(r"Install into C:\Users\example\studio")
    assert MACHINE_PATH.search("Install into /home/example/studio")


def test_source_release_respects_export_ignore_without_allowing_internal_notes(tmp_path, monkeypatch):
    subprocess.run(["git", "init", "-q", str(tmp_path)], check=True)
    (tmp_path / "docs").mkdir()
    (tmp_path / "docs/acceptance.md").write_text("Internal evidence", encoding="utf-8")
    (tmp_path / ".gitattributes").write_text("docs/acceptance.md export-ignore\n", encoding="utf-8")
    (tmp_path / "README.md").write_text("Product guide", encoding="utf-8")
    monkeypatch.setattr(release_content, "ROOT", tmp_path)
    release_content.check_release_content()
    # Removing the explicit archive exclusion restores the original rejection.
    (tmp_path / ".gitattributes").write_text("", encoding="utf-8")
    with pytest.raises(ValueError, match="Unapproved Markdown"):
        release_content.check_release_content()
