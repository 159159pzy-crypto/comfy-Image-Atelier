"""Version-checked Prompt Assistant template-only compatibility patch.

Usage: python launcher/scripts/patch_prompt_assistant.py --root <plugin> --apply
Use --check for a read-only check. No plugin imports or network requests occur.
"""
from __future__ import annotations

import argparse
import ast
import hashlib
import json
import os
from pathlib import Path
import sys

PIN = "e0587e6d50939ae05e92922f1ed2fce008c7f622"
HASHES = {
    "node/expand_node.py": "7cb70bc20e445ec7ec67176ec2cfea62d27d87e1fa8bc9bcf4c1e85f216592c9",
    "services/llm.py": "0f701e760702ae63bcbfc47384e99c21d5abe668c31dd8dbc68c7488bcceae74",
}
BACKUP = ".anima-template-only-v1"


def digest(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def replace_once(text: str, old: str, new: str) -> str:
    if text.count(old) != 1:
        raise ValueError("Prompt Assistant source does not match the supported patch")
    return text.replace(old, new, 1)


def transform(relative: str, text: str) -> str:
    if relative == "node/expand_node.py":
        text = replace_once(text, '                # 原文输入端口（可选），默认为连接端口\n',
                            '                io.Boolean.Input("template_only", default=False, optional=True),\n'
                            '                # 原文输入端口（可选），默认为连接端口\n')
        text = replace_once(text, "        seed=None, source_text=None\n",
                            "        seed=None, source_text=None, template_only=False\n")
        text = replace_once(text, "            text_hash,\n",
                            "            text_hash,\n            bool(template_only),\n")
        text = replace_once(text, "        llm_service, ollama_auto_unload, seed=None, source_text=None\n",
                            "        llm_service, ollama_auto_unload, seed=None, source_text=None, template_only=False\n")
        text = replace_once(text, "                source=SOURCE_NODE\n",
                            "                source=SOURCE_NODE,\n                template_only=template_only\n")
    elif relative == "services/llm.py":
        # Modify only expand_prompt; translate and all old calls keep their behavior.
        start = text.index("    async def expand_prompt(")
        end = text.index("    @staticmethod", start)
        head, method, tail = text[:start], text[start:end], text[end:]
        method = replace_once(method, "        source: Optional[str] = None\n",
                              "        source: Optional[str] = None,\n        template_only: bool = False\n")
        start_messages = method.index('            lang_content = ')
        end_messages = method.index('\n\n', method.index('            messages = ', start_messages))
        original = method[start_messages:end_messages]
        method = method[:start_messages] + (
            '            if template_only:\n'
            '                messages = [system_message, {"role": "user", "content": prompt}]\n'
            '            else:\n' + "\n".join("    " + line if line else "" for line in original.splitlines())
        ) + method[end_messages:]
        text = head + method + tail
    else:
        raise ValueError("Unsupported patch file")
    ast.parse(text)
    return text


def inspect(root: Path) -> list[tuple[Path, str, str, bool]]:
    root = root.resolve(strict=True)
    plans = []
    for relative, expected in HASHES.items():
        path = root / relative
        if not path.resolve(strict=True).is_relative_to(root):
            raise ValueError("Plugin source resolves outside its directory")
        current = path.read_text(encoding="utf-8-sig")
        if digest(current) == expected:
            plans.append((path, current, transform(relative, current), False))
            continue
        backup = root / BACKUP / relative
        if not backup.resolve().is_relative_to(root):
            raise ValueError("Patch backup resolves outside the plugin directory")
        if backup.is_file():
            original = backup.read_text(encoding="utf-8-sig")
            if digest(original) == expected and current == transform(relative, original):
                plans.append((path, original, current, True))
                continue
        raise ValueError(
            f"Unsupported or modified Prompt Assistant source: {relative}; "
            f"supported source is {PIN}. Existing files were preserved."
        )
    return plans


def patch(root: Path, apply: bool = False) -> dict:
    root = root.resolve(strict=True)
    plans = inspect(root)  # Validate every file before any write.
    changed = [plan for plan in plans if not plan[3]]
    if apply and changed:
        for path, original, _, _ in changed:
            backup = root / BACKUP / path.relative_to(root)
            if not backup.resolve().is_relative_to(root):
                raise ValueError("Patch backup resolves outside the plugin directory")
            if backup.exists() and backup.read_text(encoding="utf-8-sig") != original:
                raise ValueError("Existing patch backup differs; files were preserved")
        for path, original, _, _ in changed:
            backup = root / BACKUP / path.relative_to(root)
            backup.parent.mkdir(parents=True, exist_ok=True)
            if not backup.exists():
                backup.write_bytes(path.read_bytes())
        # A crash between replacements is recoverable: inspect accepts the known
        # original and the exact patched form independently for each file.
        for path, _, replacement, _ in changed:
            temp = path.with_name(path.name + ".anima-template-only.tmp")
            if temp.is_symlink():
                raise ValueError("Patch staging file cannot be a symbolic link")
            if temp.exists():
                if temp.read_text(encoding="utf-8-sig") != replacement:
                    raise ValueError("Existing patch staging file differs; files were preserved")
            else:
                with temp.open("x", encoding="utf-8", newline="\n") as stream:
                    stream.write(replacement)
                    stream.flush()
                    os.fsync(stream.fileno())
            os.replace(temp, path)
    return {"supported": True, "patched": apply or not changed, "changed": bool(apply and changed), "source_commit": PIN}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    operation = parser.add_mutually_exclusive_group(required=True)
    operation.add_argument("--check", action="store_true")
    operation.add_argument("--apply", action="store_true")
    args = parser.parse_args()
    try:
        result = patch(args.root, apply=args.apply)
    except (OSError, ValueError, SyntaxError) as error:
        print(str(error), file=sys.stderr)
        return 1
    print(json.dumps(result))
    return 0 if result["patched"] else 2


if __name__ == "__main__":
    raise SystemExit(main())
