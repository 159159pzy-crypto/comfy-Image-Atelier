"""Read-only ComfyUI path inventory. Does not import custom nodes or torch."""
import contextlib
import io
import json
import os
from pathlib import Path
import sys


def probe(root, arguments=()):
    root = Path(root).resolve()
    os.chdir(root)
    sys.path.insert(0, str(root))
    sys.argv = [str(root / "main.py"), *arguments]
    with contextlib.redirect_stdout(io.StringIO()):
        import comfy.options
        comfy.options.enable_args_parsing()
        import folder_paths
        from utils.extra_config import load_extra_path_config

        # Prefer canonical destinations for new files before applying user paths.
        # Loading YAML afterwards preserves an explicit is_default legacy unet/clip
        # choice, which ComfyUI itself places first.
        for key, legacy, canonical in [("diffusion_models", "unet", "diffusion_models"), ("text_encoders", "clip", "text_encoders")]:
            configured = folder_paths.folder_names_and_paths.get(key)
            defaults = {str(Path(folder_paths.models_dir) / legacy), str(Path(folder_paths.models_dir) / canonical)}
            if configured and configured[0] and configured[0][0] in defaults:
                preferred = str(Path(folder_paths.models_dir) / canonical)
                folder_paths.folder_names_and_paths[key] = (
                    [preferred] + [p for p in configured[0] if p != preferred], configured[1]
                )
        extra = root / "extra_model_paths.yaml"
        if extra.is_file():
            load_extra_path_config(str(extra))
        paths = {key: list(value[0]) for key, value in folder_paths.folder_names_and_paths.items()}
    models = Path(folder_paths.models_dir)
    paths["wd_taggers"] = [str(models / "wd_taggers")]
    paths.setdefault("sams", [str(models / "sams")])
    paths.setdefault("ultralytics", [str(models / "ultralytics")])
    # Impact Subpack also recognizes these extra_model_paths keys.
    for key, suffix in [("ultralytics_bbox", "bbox"), ("ultralytics_segm", "segm")]:
        paths.setdefault(key, [str(Path(p) / suffix) for p in paths["ultralytics"]])
    return {"root": str(root), "python": sys.executable, "paths": paths}


if __name__ == "__main__":
    result = probe(sys.argv[1], sys.argv[2:])
    print(json.dumps(result, ensure_ascii=True))
