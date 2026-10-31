"""Keep Forbidden Vision model downloads in the launcher's verified download queue.

The source contract is structural, rather than a single upstream commit. Receipts
prove every accepted local change before an install, update, or rollback proceeds.
No plugin imports or network requests occur in this script.
"""
from __future__ import annotations

import argparse
import ast
import hashlib
import json
import os
from pathlib import Path
import sys


BACKUP = ".anima-managed-models-v1"
FILES = ("__init__.py", "src/model_manager.py")
MARKER = "# Anima managed model downloads v1"


def digest(value: str) -> str:
    return hashlib.sha256(value.encode("utf-8")).hexdigest()


def one(items: list, description: str):
    if len(items) != 1:
        raise ValueError(f"Forbidden Vision source contract changed: {description}")
    return items[0]


def transform(relative: str, text: str) -> str:
    if MARKER in text:
        raise ValueError("A managed patch without a matching receipt was preserved")
    tree = ast.parse(text)
    if not any(isinstance(node, ast.Import) and any(item.name == "os" and item.asname is None for item in node.names) for node in tree.body):
        raise ValueError("Forbidden Vision source no longer imports os")
    lines = text.splitlines(keepends=True)
    if relative == "src/model_manager.py":
        owner = one([node for node in tree.body if isinstance(node, ast.ClassDef) and node.name == "ForbiddenVisionModelManager"], "model manager class")
        method = one([node for node in owner.body if isinstance(node, ast.FunctionDef) and node.name == "_download_model"], "model download method")
        if [arg.arg for arg in method.args.args] != ["self", "model_key"] or method.args.vararg or method.args.kwarg or method.args.kwonlyargs:
            raise ValueError("Forbidden Vision download signature changed")
        if not any(isinstance(node, ast.Call) and isinstance(node.func, ast.Name) and node.func.id == "hf_hub_download" for node in ast.walk(method)):
            raise ValueError("Forbidden Vision downloader no longer uses the supported download path")
        first = method.body[0]
        if isinstance(first, ast.Expr) and isinstance(first.value, ast.Constant) and isinstance(first.value.value, str):
            at = first.end_lineno
        else:
            at = first.lineno - 1
        indent = " " * (method.col_offset + 4)
        guard = (
            f"{indent}{MARKER}\n"
            f"{indent}if os.path.isfile(os.path.join(os.path.dirname(__file__), '..', '..', 'skip_download_model')):\n"
            f"{indent}    config = self.MODELS_CONFIG[model_key]\n"
            f"{indent}    managed_path = os.path.join(self.models_dir, config['filename'])\n"
            f"{indent}    return managed_path if os.path.isfile(managed_path) and os.path.getsize(managed_path) > 1000 else None\n"
        )
        lines.insert(at, guard)
    elif relative == "__init__.py":
        method = one([node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == "initialize_forbidden_vision"], "initialization method")
        def downloads(statement: ast.AST) -> bool:
            return isinstance(statement, ast.Expr) and isinstance(statement.value, ast.Call) and isinstance(statement.value.func, ast.Attribute) and statement.value.func.attr == "initialize_default_models"
        branch = one([node for node in ast.walk(method) if isinstance(node, ast.If) and any(downloads(statement) for statement in node.body)], "initial model download branch")
        if branch.orelse:
            raise ValueError("Forbidden Vision initialization branch changed")
        start, end = branch.body[0].lineno - 1, branch.body[-1].end_lineno
        indent = " " * branch.body[0].col_offset
        original = lines[start:end]
        lines[start:end] = [
            f"{indent}{MARKER}\n",
            f"{indent}if os.path.isfile(os.path.join(os.path.dirname(__file__), '..', 'skip_download_model')):\n",
            f"{indent}    print('ForbiddenVision: Optional models are managed by the launcher.')\n",
            f"{indent}else:\n",
            *("    " + line if line.strip() else line for line in original),
        ]
    else:
        raise ValueError("Unknown compatibility source")
    result = "".join(lines)
    ast.parse(result)
    return result


def contained(root: Path, relative: str, *, required: bool = False) -> Path:
    path = root / relative
    if not path.resolve(strict=required).is_relative_to(root):
        raise ValueError("Compatibility file resolves outside its node directory")
    if path.is_symlink():
        raise ValueError("Compatibility file is a symbolic link")
    return path


def inspect(root: Path) -> tuple[list, dict]:
    root = root.resolve(strict=True)
    receipt_path = contained(root, f"{BACKUP}/receipt.json")
    receipt = json.loads(receipt_path.read_text(encoding="utf-8")) if receipt_path.is_file() else None
    if receipt is not None and (receipt.get("schemaVersion") != 1 or set(receipt.get("files", {})) != set(FILES)):
        raise ValueError("Invalid managed model patch receipt")
    plans, entries = [], {}
    for relative in FILES:
        path = contained(root, relative, required=True)
        current = path.read_text(encoding="utf-8-sig")
        backup = contained(root, f"{BACKUP}/{relative}")
        if receipt is not None:
            if not backup.is_file():
                raise ValueError(f"Managed patch backup is missing: {relative}")
            original = backup.read_text(encoding="utf-8-sig")
        else:
            original = current
            if backup.is_file() and backup.read_text(encoding="utf-8-sig") != original:
                raise ValueError(f"Unknown local changes were preserved: {relative}")
        patched = transform(relative, original)
        entry = {"originalSha256": digest(original), "patchedSha256": digest(patched)}
        if receipt is not None and (receipt["files"][relative] != entry or current not in (original, patched)):
            raise ValueError(f"Unknown local changes were preserved: {relative}")
        entries[relative] = entry
        plans.append((path, backup, original, patched, current == patched))
    return plans, {"schemaVersion": 1, "files": entries}


def atomic_write(path: Path, text: str) -> None:
    temporary = path.with_name(path.name + ".anima-tmp")
    if temporary.is_symlink():
        raise ValueError("Compatibility staging file is a symbolic link")
    with temporary.open("w", encoding="utf-8", newline="\n") as stream:
        stream.write(text)
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(temporary, path)


def patch(root: Path, apply: bool = False) -> dict:
    root = root.resolve(strict=True)
    plans, receipt = inspect(root)
    changed = [plan for plan in plans if not plan[4]]
    if apply and changed:
        for _, backup, original, _, _ in plans:
            backup.parent.mkdir(parents=True, exist_ok=True)
            if not backup.exists():
                atomic_write(backup, original)
        receipt_path = contained(root, f"{BACKUP}/receipt.json")
        atomic_write(receipt_path, json.dumps(receipt, indent=2) + "\n")
        for path, _, _, replacement, _ in changed:
            atomic_write(path, replacement)
    return {"supported": True, "patched": bool(apply or not changed), "changed": bool(apply and changed)}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    operation = parser.add_mutually_exclusive_group(required=True)
    operation.add_argument("--check", action="store_true")
    operation.add_argument("--apply", action="store_true")
    args = parser.parse_args()
    try:
        result = patch(args.root, args.apply)
    except (OSError, ValueError, SyntaxError) as error:
        print(str(error), file=sys.stderr)
        return 1
    print(json.dumps(result))
    return 0 if result["patched"] else 2


if __name__ == "__main__":
    raise SystemExit(main())
