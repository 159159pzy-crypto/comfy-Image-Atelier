"""Optional startup resource mapping supplied by the Windows launcher."""
from __future__ import annotations

import copy
from typing import Any

from .workflow import DEFAULT_SETTINGS, WorkflowError, WorkflowTemplates, normalize_lora_path


REMOVED_SETTINGS = ("hires", "detailers", "cfg_zero_star", "second_sampler")


def _public_defaults() -> dict[str, Any]:
    defaults = copy.deepcopy(DEFAULT_SETTINGS)
    for key in REMOVED_SETTINGS:
        defaults.pop(key, None)
    return defaults


def configure_resources(templates: WorkflowTemplates, mapping: dict[str, str] | None) -> dict[str, Any]:
    defaults = _public_defaults()
    # Store the normalized mapping on the template object as well as applying
    # it to the checked-in graph.  The v2 renderer rebinds profile loaders at
    # submission time; without this second copy a later Krea/Anima switch
    # would overwrite launcher-resolved subdirectory paths with basenames.
    templates.resource_paths = {}
    if mapping is None:
        return defaults
    if not isinstance(mapping, dict) or any(not isinstance(key, str) or not isinstance(value, str) for key, value in mapping.items()):
        raise WorkflowError("启动器模型映射必须是字符串字典")
    paths = {normalize_lora_path(key): normalize_lora_path(value) for key, value in mapping.items()}
    templates.resource_paths = paths

    def replace(value: Any) -> Any:
        if isinstance(value, str):
            return paths.get(value, value)
        if isinstance(value, list):
            return [replace(item) for item in value]
        if isinstance(value, dict):
            return {key: replace(item) for key, item in value.items()}
        return value

    templates.api = replace(templates.api)
    templates.ui = replace(templates.ui)
    defaults = replace(defaults)
    # A fresh install may deliberately omit optional models.  Keep the old
    # Anima filename mapping working for upgrades while preferring the v2
    # anima-base filename on clean installs.
    if DEFAULT_SETTINGS["model_name"] not in paths:
        for legacy in (
            "miaomiaoHarem_anima14.safetensors",
            "silvermoonmixAnima_v2329BTurbo.safetensors",
            "silvermoonmixAnima_v2329B.safetensors",
        ):
            if legacy in paths:
                defaults["model_name"] = paths[legacy]
                break
    return defaults
