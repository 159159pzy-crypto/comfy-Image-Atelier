"""Model profiles used by the v2 workflow.

The profile is the single source of truth for resources which must change
together when the user switches between Anima and Krea 2.  Keeping this data
outside :mod:`workflow` also lets the API and the front end inspect the same
contract without importing the workflow renderer.
"""
from __future__ import annotations

import copy
from typing import Any, Mapping


class ModelProfileError(ValueError):
    """Raised when a model profile identifier is not known."""


MODEL_PROFILE_VERSION = 1
DEFAULT_MODEL_PROFILE = "anima"
MODEL_PROFILE_IDS = ("anima", "krea2")

# Values intentionally contain filenames only.  Launcher resource mappings can
# safely prefix these names with a model directory before templates are used.
MODEL_PROFILES: dict[str, dict[str, Any]] = {
    "anima": {
        "id": "anima",
        "label": "Anima",
        "model_name": "anima-base-v1.0.safetensors",
        "clip_name": "qwen_3_06b_base.safetensors",
        "vae_name": "qwen_image_vae.safetensors",
        # The attached Anima Turbo workflow loads this encoder through the
        # stock stable-diffusion CLIPLoader mode.  Krea 2 is the profile that
        # explicitly opts into the dedicated ``krea2`` encoder mode below.
        "clip_type": "stable_diffusion",
        "prompt_mode": "tag_or_natural",
        "defaults": {
            "steps": 30,
            "cfg": 4.0,
            "sampler_name": "er_sde",
            "scheduler": "simple",
            "base_mode": "dual_sampler",
        },
        "seedvr2_model": "seedvr2_3b_int8_convrot.safetensors",
        "seedvr2_vae": "ema_vae_fp16.safetensors",
        "lora_namespace": "anima",
        "pool_namespace": "anima",
        "style_namespace": "anima",
        # Older installs used this Silvermoon/Anima filename.  It remains a
        # migration alias so existing saved settings continue to work.
        "legacy_model_names": (
            "miaomiaoHarem_anima14.safetensors",
            "silvermoonmixAnima_v2329BTurbo.safetensors",
            "silvermoonmixAnima_v2329B.safetensors",
        ),
    },
    "krea2": {
        "id": "krea2",
        "label": "Krea 2",
        "model_name": "krea2_turbo_int8_convrot.safetensors",
        "clip_name": "qwen3vl_4b_fp8_scaled.safetensors",
        "vae_name": "qwen_image_vae.safetensors",
        # Krea 2 uses the Qwen image text encoder through the same ComfyUI
        # CLIPLoader node; the value is kept explicit for workflow contracts
        # and future encoder node variants.
        "clip_type": "krea2",
        "prompt_mode": "natural_language",
        "defaults": {
            "steps": 30,
            "cfg": 4.0,
            "sampler_name": "er_sde",
            "scheduler": "simple",
            "base_mode": "normal",
        },
        "seedvr2_model": "seedvr2_3b_int8_convrot.safetensors",
        "seedvr2_vae": "ema_vae_fp16.safetensors",
        "lora_namespace": "krea2",
        "pool_namespace": "krea2",
        "style_namespace": "krea2",
        "legacy_model_names": (),
    },
}

# Human-friendly values are accepted at API boundaries.  Persisted values are
# always the stable lower-case ids above.
_ALIASES = {
    "anima": "anima",
    "anima1": "anima",
    "anima_v1": "anima",
    "anima-base": "anima",
    "krea": "krea2",
    "krea2": "krea2",
    "krea 2": "krea2",
    "krea-2": "krea2",
}


def normalize_model_profile(value: Any) -> str:
    """Return a canonical profile id or raise :class:`ModelProfileError`."""

    if not isinstance(value, str):
        raise ModelProfileError("model_profile 必须是字符串")
    key = value.strip().casefold()
    profile_id = _ALIASES.get(key)
    if profile_id is None:
        raise ModelProfileError(
            f"model_profile 无效，可选值: {', '.join(MODEL_PROFILE_IDS)}"
        )
    return profile_id


def get_model_profile(value: Any = DEFAULT_MODEL_PROFILE) -> dict[str, Any]:
    """Return a defensive copy of a profile definition."""

    profile_id = normalize_model_profile(value)
    return copy.deepcopy(MODEL_PROFILES[profile_id])


def model_profile_defaults(value: Any = DEFAULT_MODEL_PROFILE) -> dict[str, Any]:
    """Return settings controlled by a profile.

    ``model_profile`` and the bound resource names are included so callers can
    merge this result into user settings without accidentally retaining the
    previous model's CLIP or VAE.
    """

    profile = get_model_profile(value)
    defaults = copy.deepcopy(profile["defaults"])
    defaults.update(
        {
            "model_profile": profile["id"],
            "model_name": profile["model_name"],
            "clip_name": profile["clip_name"],
            "vae_name": profile["vae_name"],
            "clip_type": profile["clip_type"],
        }
    )
    return defaults


def profile_resource_names(value: Any = DEFAULT_MODEL_PROFILE) -> dict[str, str]:
    profile = get_model_profile(value)
    return {
        "model_name": profile["model_name"],
        "clip_name": profile["clip_name"],
        "vae_name": profile["vae_name"],
        "seedvr2_model": profile["seedvr2_model"],
        "seedvr2_vae": profile["seedvr2_vae"],
    }


def profile_public(
    value: Any = DEFAULT_MODEL_PROFILE,
    *,
    available: Mapping[str, Any] | None = None,
) -> dict[str, Any]:
    """Build the stable JSON shape returned by ``GET /api/resources``.

    ``available`` may contain inventories from ComfyUI (``models``, ``clips``,
    ``vaes``, ``upscale_models`` and ``nodes``).  Unknown/missing inventories
    are reported as ``None`` rather than pretending a resource is installed.
    """

    profile = get_model_profile(value)
    inventory = dict(available or {})

    def _contains(kind: str, filename: str) -> bool | None:
        values = inventory.get(kind)
        if values is None:
            return None
        try:
            wanted = str(filename).replace("\\", "/").casefold()
            wanted_base = wanted.rsplit("/", 1)[-1]
            available = {
                str(item).replace("\\", "/").casefold() for item in values
            }
            # object_info may expose a model relative to a configured
            # subdirectory while the profile stores its canonical basename.
            # A basename match is sufficient here because ComfyUI rejects
            # duplicate model names in the same loader inventory; preserve
            # exact matching for normal installs.
            return wanted in available or wanted_base in {
                item.rsplit("/", 1)[-1] for item in available
            }
        except TypeError:
            return None

    resource_map = {
        "model": ("model_name", profile["model_name"]),
        "clip": ("clip_name", profile["clip_name"]),
        "vae": ("vae_name", profile["vae_name"]),
        "seedvr2": ("seedvr2_model", profile["seedvr2_model"]),
    }
    # SeedVR2 has a separate VAE loader in the attachment.  Keep its
    # availability separate from the generation VAE when the client advertises
    # the v2 inventory field.  Older test/integration clients retain the
    # compact four-key profile shape until they upgrade their inventory.
    if "seedvr2_vaes" in inventory:
        resource_map["seedvr2_vae"] = ("seedvr2_vae", profile["seedvr2_vae"])
    # ComfyUI's stock object_info does not expose CLIP/VAE choices in the
    # previous client contract.  Accept both old and explicit inventory keys.
    aliases = {
        "clip": ("clips", "clip_models"),
        "vae": ("vaes", "vae_models"),
        "seedvr2_vae": ("seedvr2_vaes", "vaes", "vae_models"),
    }
    availability: dict[str, bool | None] = {}
    missing: list[str] = []
    for key, (field, filename) in resource_map.items():
        kinds = aliases.get(key, ("seedvr2_models", "models") if key == "seedvr2" else ("models",))
        value_available: bool | None = None
        for kind in kinds:
            value_available = _contains(kind, filename)
            if value_available is not None:
                break
        availability[key] = value_available
        if value_available is False:
            missing.append(filename)

    def _component_status(category: str, names: tuple[str, ...]) -> bool | None:
        values = inventory.get(category)
        if values is None:
            return None
        expected = {name.casefold() for name in names}
        if isinstance(values, Mapping):
            normalized = {str(key).casefold(): bool(value) for key, value in values.items()}
            matches = [normalized[name] for name in expected if name in normalized]
            return any(matches) if matches else False
        if isinstance(values, (str, bytes)):
            values = [values]
        try:
            installed = {str(value).casefold() for value in values}
        except TypeError:
            return None
        return bool(expected & installed)

    node_status = {
        "unet_loader": _component_status("nodes", ("UNETLoader",)),
        "clip_loader": _component_status("nodes", ("CLIPLoader",)),
        "vae_loader": _component_status("nodes", ("VAELoader",)),
        "seedvr2": _component_status(
            "nodes",
            ("SeedVR2", "SeedVR2Conditioning", "SeedVR2Preprocess", "SeedVR2PostProcessing"),
        ),
        "nvidia_upscale": _component_status(
            "nodes", ("NvidiaUpscale", "RTXVideoSuperResolution")
        ),
        "face_refine": _component_status(
            "nodes", ("ForbiddenVisionFaceProcessorIntegrated",)
        ),
    }
    plugin_status = {
        name: _component_status("plugins", (name,))
        for name in ("seedvr2", "nvidia_upscale", "forbidden_vision_face")
    }
    result = {
        "id": profile["id"],
        "label": profile["label"],
        "model_name": profile["model_name"],
        "clip_name": profile["clip_name"],
        "vae_name": profile["vae_name"],
        "clip_type": profile["clip_type"],
        "prompt_mode": profile["prompt_mode"],
        "defaults": copy.deepcopy(profile["defaults"]),
        "seedvr2_model": profile["seedvr2_model"],
        "seedvr2_vae": profile["seedvr2_vae"],
        "namespaces": {
            "lora": profile["lora_namespace"],
            "pool": profile["pool_namespace"],
            "style": profile["style_namespace"],
        },
        "available": availability,
        "missing": missing,
        "nodes": node_status,
        "plugins": plugin_status,
    }
    return result


def profile_ids() -> tuple[str, ...]:
    return MODEL_PROFILE_IDS

