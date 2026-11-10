"""Selecting one effect must not require importing or executing the other."""
import importlib.util
from pathlib import Path
import sys
from types import SimpleNamespace
from unittest.mock import patch


spec = importlib.util.spec_from_file_location("nvidia_feature_probe", Path(__file__).resolve().parents[1] / "launcher/scripts/check_nvidia_runtime.py")
probe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(probe)


def test_vsr_verification_does_not_require_dlss_paths_or_backend():
    with patch.dict(sys.modules, {"torch": SimpleNamespace()}), \
            patch.object(probe, "probe", return_value={"supported": True, "generation": "rtx40"}), \
            patch.object(probe, "verify_vsr", return_value={"vfxOutput": [512, 512]}) as vsr, \
            patch.object(probe, "verify_dlss", side_effect=AssertionError("DLSS must stay dormant")):
        result = probe.verify(None, None, "rtx40", ("nvidia",))
        assert result["ready"] and result["features"] == ["nvidia"]
        vsr.assert_called_once()


def test_dlss_verification_does_not_require_vfx_runtime(tmp_path):
    with patch.dict(sys.modules, {"torch": SimpleNamespace()}), \
            patch.object(probe, "probe", return_value={"supported": True, "generation": "rtx40"}), \
            patch.object(probe, "verify_dlss", return_value={"dlssOutput": [256, 256]}) as dlss, \
            patch.object(probe, "verify_vsr", side_effect=AssertionError("VFX must stay dormant")):
        result = probe.verify(tmp_path, tmp_path, "rtx40", ("dlss",))
        assert result["ready"] and result["features"] == ["dlss"]
        dlss.assert_called_once()
