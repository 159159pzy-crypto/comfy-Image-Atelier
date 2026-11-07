"""Shared prompt-source requirements for history inspection and execution."""
from __future__ import annotations

from typing import Any


def uses_frozen_prompt(regeneration: dict[str, Any] | None) -> bool:
    return (regeneration or {}).get('mode') in {'replay', 'prompt_variant'}


def prompt_source_issues(settings: dict[str, Any], pools: Any,
                         regeneration: dict[str, Any] | None = None) -> list[dict[str, str]]:
    if (uses_frozen_prompt(regeneration) or settings.get('workbench') == 'single'
            or settings.get('model_profile') != 'krea2'):
        return []
    selection = settings.get('krea_prompt_pool') or {}
    if selection.get('enabled') and (pools is None or not pools.resolve('krea2', selection.get('ids'))):
        return [{'type': 'prompt_pool', 'label': 'Krea 2 提示词池',
                 'name': 'Krea 2 自然语言提示词池为空，请先导入提示词'}]
    return []
