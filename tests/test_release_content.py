import pytest

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
    "launcher/manifests/launcher-manifest.json", "anima_webui/server.py",
    "docs/screenshots/anima-studio-single-workbench.png",
])
def test_product_files_are_accepted(name):
    check_path(name)


def test_machine_path_scan_allows_web_links():
    assert not MACHINE_PATH.search("[download](https://github.com/example/releases)")
    assert MACHINE_PATH.search(r"Install into C:\Users\example\studio")
    assert MACHINE_PATH.search("Install into /home/example/studio")
