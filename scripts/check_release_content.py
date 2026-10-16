"""Reject local state and internal notes from the files selected for release."""
from __future__ import annotations

from pathlib import Path, PurePosixPath
import re
import subprocess

ROOT = Path(__file__).resolve().parents[1]
PUBLIC_MARKDOWN = {
    "README.md", "CHANGELOG.md", "RELEASE_NOTES.md",
    "launcher/README.md", "docs/dependencies.md",
}
LOCAL_PARTS = {
    ".git", ".codex-local", ".claude", ".launcher", ".venv", "venv",
    "__pycache__", ".pytest_cache", ".ruff_cache", ".playwright-cli",
    "node_modules", "bin", "obj", "test-results", "playwright-report",
}
LOCAL_ROOTS = {"data", "output", "dist", "tools", "anima_natural", "anima_studio"}
MACHINE_PATH = re.compile(r"(?i)(?:(?<![a-z0-9])[a-z]:[\\/]|/Users/|/home/)")


def check_path(name: str) -> None:
    path = PurePosixPath(name)
    if set(path.parts) & LOCAL_PARTS or path.parts[0] in LOCAL_ROOTS:
        raise ValueError(f"Local directory selected for release: {name}")
    if path.name.startswith(".env") or path.suffix.lower() in {".pyc", ".log", ".lnk", ".exe", ".dll", ".pdb", ".zip"}:
        raise ValueError(f"Local or generated file selected for release: {name}")
    if path.suffix.lower() == ".md" and name not in PUBLIC_MARKDOWN:
        raise ValueError(f"Unapproved Markdown selected for release: {name}")


def check_release_content() -> None:
    names = subprocess.check_output(
        ["git", "ls-files", "-z"], cwd=ROOT,
    ).decode("utf-8").split("\0")
    names = [name for name in names if name]
    for name in names:
        check_path(name)
        path = ROOT / name
        if not path.is_file():
            raise ValueError(f"Tracked release file is missing: {name}")
        if name in PUBLIC_MARKDOWN:
            content = path.read_text(encoding="utf-8")
            if MACHINE_PATH.search(content):
                raise ValueError(f"Machine-specific path in public documentation: {name}")
            for target in re.findall(r"\]\(([^)]+)\)", content):
                if "://" not in target and not target.startswith("#"):
                    linked = (path.parent / target.split("#", 1)[0]).resolve()
                    relative = linked.relative_to(ROOT).as_posix()
                    if relative not in names:
                        raise ValueError(f"Documentation link is absent from release: {name} -> {target}")
    print(f"Release content: {len(names)} tracked files; public documentation and local-state exclusions verified.")


if __name__ == "__main__":
    check_release_content()
