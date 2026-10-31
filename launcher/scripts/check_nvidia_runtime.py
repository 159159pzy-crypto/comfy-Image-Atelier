"""Probe or execute the installed NVIDIA effects without downloading any files."""
from __future__ import annotations

import argparse
import ctypes
import importlib.metadata
import importlib.util
import json
from pathlib import Path
import platform
import re
import sys


def probe() -> dict:
    import torch
    gpu = torch.cuda.get_device_name(0) if torch.cuda.is_available() else ""
    match = re.search(r"\bRTX\s*([345])\d{2,3}\b", gpu, re.I)
    try:
        vfx = importlib.metadata.version("nvidia-vfx")
    except importlib.metadata.PackageNotFoundError:
        vfx = ""
    vc = False
    if sys.platform == "win32":
        try:
            ctypes.WinDLL("vcruntime140.dll")
            ctypes.WinDLL("msvcp140.dll")
            vc = True
        except OSError:
            pass
    generation = "rtx" + match.group(1) + "0" if match else ""
    supported = sys.platform == "win32" and platform.machine().lower() in {"amd64", "x86_64"} and bool(generation)
    return {"supported": supported, "generation": generation, "gpu": gpu, "pythonTag": f"cp{sys.version_info.major}{sys.version_info.minor}",
            "pythonVersion": platform.python_version(), "torchVersion": torch.__version__, "vfxVersion": vfx, "vcRuntime": vc}


def verify(dlss_root: Path, state_dir: Path, generation: str) -> dict:
    import numpy as np
    import torch
    import nvvfx
    result = probe()
    if not result["supported"] or result["generation"] != generation:
        raise RuntimeError("NVIDIA device identity changed; check the installation again")
    if nvvfx.__version__ != "0.1.0.1":
        raise RuntimeError("The installed NVIDIA VFX runtime does not match the installation plan")
    # Exercise the native SDK and its model assets. Import success alone is insufficient.
    frame = torch.linspace(0.05, 0.95, 3 * 256 * 256, device="cuda").reshape(3, 256, 256).contiguous()
    with nvvfx.VideoSuperRes(nvvfx.effects.QualityLevel.ULTRA) as effect:
        effect.output_width = 512
        effect.output_height = 512
        effect.load()
        output = torch.from_dlpack(effect.run(frame).image).clone()
        if tuple(output.shape) != (3, 512, 512) or not torch.isfinite(output).all().item():
            raise RuntimeError("NVIDIA VFX smoke test returned an invalid image")
    del frame, output
    torch.cuda.empty_cache()
    # Load only the backend; importing the node package would invoke its lazy downloader.
    spec = importlib.util.spec_from_file_location("anima_dlss_runtime_check", dlss_root / "dlss_engine.py")
    engine = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(engine)
    state_dir.mkdir(parents=True, exist_ok=True)
    engine.configure(str(dlss_root / "vendor/dlssnr_host.dll"), str(dlss_root / "vendor" / generation / "nvngx_dlssnr.dll"), str(state_dir / "dlss-check.log"))
    session = engine.Live(256, 256, {"preset": 1})
    try:
        rgba = np.full((256, 256, 4), 128, dtype=np.uint8)
        rgba[..., 3] = 255
        processed = session.process(rgba, reset=True)
        if processed is None or processed.shape != rgba.shape:
            raise RuntimeError("DLSS Feature 18 smoke test did not produce an image")
    finally:
        session.close()
    return {**result, "ready": True, "vfxOutput": [512, 512], "dlssOutput": [256, 256]}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--probe", action="store_true")
    parser.add_argument("--dlss-root", type=Path)
    parser.add_argument("--state-dir", type=Path)
    parser.add_argument("--generation", choices=("rtx30", "rtx40", "rtx50"))
    args = parser.parse_args()
    try:
        result = probe() if args.probe else verify(args.dlss_root, args.state_dir, args.generation)
    except Exception as error:
        print(json.dumps({"ready": False, "error": str(error)}, ensure_ascii=False))
        return 1
    print(json.dumps(result, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
