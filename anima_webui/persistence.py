"""本地持久化文件的通用工具。"""

from __future__ import annotations

import os
import shutil
from pathlib import Path


def backup_corrupt_file(path: Path) -> Path:
    """把无法读取的数据文件改名备份,让应用可以用空数据继续启动。

    原文件内容不会丢失:用户可以手工检查/修复备份文件后再改回原名。
    """
    backup = path.with_name(path.name + ".corrupt.bak")
    counter = 1
    while backup.exists():
        backup = path.with_name(f"{path.name}.corrupt-{counter}.bak")
        counter += 1
    os.replace(path, backup)
    return backup


def backup_for_migration(path: Path, version: int) -> Path | None:
    """Keep the original persisted file before an in-place schema upgrade."""
    if not path.is_file():
        return None
    backup = path.with_name(f"{path.name}.v{version}.bak")
    counter = 1
    while backup.exists():
        backup = path.with_name(f"{path.name}.v{version}-{counter}.bak")
        counter += 1
    shutil.copy2(path, backup)
    return backup
