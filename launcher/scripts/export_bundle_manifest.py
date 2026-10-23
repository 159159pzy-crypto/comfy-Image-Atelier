"""Inventory the SDK's exact single-file bundle inputs, excluding the outer host."""
from __future__ import annotations

import argparse
import hashlib
from pathlib import Path, PurePosixPath


def export(inputs: Path, apphost: str, output: Path) -> None:
    files: dict[str, tuple[str, str]] = {}
    for line in inputs.read_text(encoding="utf-8-sig").splitlines():
        source, relative = line.split("|", 1)
        relative = relative.replace("\\", "/")
        if relative.casefold() in {apphost.casefold(), "files.sha256"}:
            continue
        path = PurePosixPath(relative)
        if not relative or path.is_absolute() or any(part in {"", ".", ".."} for part in relative.split("/")) or ":" in relative:
            raise ValueError(f"Unsafe bundle path: {relative}")
        key = relative.casefold()
        digest = hashlib.sha256(Path(source).read_bytes()).hexdigest()
        if key in files:
            if files[key] != (relative, digest):
                raise ValueError(f"Conflicting bundle paths: {relative}")
            continue
        files[key] = (relative, digest)
    output.write_text("".join(f"{digest}  {name}\n" for name, digest in sorted(files.values())), encoding="utf-8")
    print(f"Inventoried {len(files)} embedded files.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--inputs", required=True, type=Path)
    parser.add_argument("--apphost", required=True)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    export(args.inputs, args.apphost, args.output)
