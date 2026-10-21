"""Read-only preparation for the installed WD Timm Tagger node.

Model downloads belong to the launcher. Checking exact, complete model folders
before both enqueue and execution prevents the node's implicit first-use download.
"""
from __future__ import annotations

import asyncio
import copy
import csv
import hashlib
import json
import math
import os
import re
from collections import OrderedDict
from pathlib import Path
from typing import Any

from .comfy import ComfyError
from .model_profiles import normalize_model_profile
from .prompt_assistant import choices
from .workflow import WorkflowError

DEFAULTS = {
    "model": "pixai-tagger-v0.9-timm",
    "general_threshold": 0.35,
    "character_threshold": 0.75,
    "exclude_tags": "watermark, sample watermark, too many watermarks, text, signature, logo, username, "
                    "artist name, web address, copyright, stamp, sample, trademark, url, patreon logo, "
                    "twitter username, weibo watermark, character name, title, barcode, UI, lower text, "
                    "bad quality, low quality, blurry",
}
PRESETS = {
    "wd-eva02-large-tagger-v3", "wd-vit-large-tagger-v3", "wd-vit-tagger-v3",
    "wd-swinv2-tagger-v3", "wd-convnext-tagger-v3", "pixai-tagger-v1.0-timm",
    "pixai-tagger-v0.9-timm", "OppaiOracle-V1.1-timm",
}


def _model_key(name: str) -> str:
    # Windows folder names are case insensitive; keep the runtime spelling in
    # the snapshot while matching aliases against the same immutable package.
    return os.path.normcase(name)


REQUIRED_FILES = {"config.json", "model.safetensors", "selected_tags.csv"}
ALLOWED_FILES = REQUIRED_FILES | {"timm_arch.py", "preprocess.json"}
DTYPE_BYTES = {"BOOL": 1, "U8": 1, "I8": 1, "I16": 2, "U16": 2, "I32": 4, "U32": 4,
               "I64": 8, "U64": 8, "F16": 2, "BF16": 2, "F32": 4, "F64": 8,
               "F8_E4M3": 1, "F8_E4M3FN": 1, "F8_E5M2": 1}
PATH_REASON = "无法确认本地打标模型目录，请在启动器中检查 ComfyUI 目录设置"
MODEL_REASONS = {
    "ready": "",
    "missing": "本地模型尚未安装，请在启动器中下载所选模型",
    "incomplete": "本地模型文件不完整，请在启动器中修复所选模型",
    "invalid": "本地模型校验失败，请在启动器中修复所选模型",
    "unverifiable": "无法验证本地模型，请在启动器中检查模型配置",
}


def _safe_name(value: Any) -> bool:
    return (isinstance(value, str) and 0 < len(value) <= 200 and value == value.strip()
            and value not in {".", ".."} and not re.search(r"[\\/:\x00-\x1f]", value))


def resolve_tagger_directory(folders: Any, stats: Any) -> Path:
    """Confirm the node's models_dir, rather than choosing an extra search path."""
    try:
        paths = folders.get("wd_taggers") if isinstance(folders, dict) else None
        argv = stats.get("system", {}).get("argv") if isinstance(stats, dict) else None
        if (not isinstance(paths, list) or not paths or not isinstance(argv, list)
                or not argv or not all(isinstance(item, str) for item in argv)):
            raise ValueError
        if not all(isinstance(item, str) and Path(item).is_absolute() for item in paths):
            raise ValueError
        unique = {Path(item).resolve() for item in paths}
        if len(unique) != 1:
            raise ValueError
        overrides: dict[str, Path] = {}
        for index, arg in enumerate(argv):
            flag, equals, attached = arg.partition("=")
            if flag not in {"--base-directory", "--models-directory"}:
                continue
            value = attached if equals else argv[index + 1] if index + 1 < len(argv) else ""
            if flag in overrides or not value or not Path(value).is_absolute():
                raise ValueError
            overrides[flag] = Path(value).resolve()
        if "--models-directory" in overrides:
            models = overrides["--models-directory"]
        elif "--base-directory" in overrides:
            models = overrides["--base-directory"] / "models"
        elif Path(argv[0]).is_absolute() and Path(argv[0]).suffix.lower() == ".py":
            models = Path(argv[0]).resolve().parent / "models"
        else:
            raise ValueError
        expected = (models / "wd_taggers").resolve()
        if expected not in unique:
            raise ValueError
        return expected
    except (ValueError, TypeError, AttributeError, OSError, RuntimeError) as error:
        raise WorkflowError(PATH_REASON) from error


def _json_file(path: Path, limit: int = 1024 * 1024) -> dict[str, Any]:
    if not 0 < path.stat().st_size <= limit:
        raise ValueError
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ValueError
    return value


def _validate_structure(directory: Path, required: set[str]) -> None:
    config = _json_file(directory / "config.json")
    classes = config.get("num_classes")
    if (not isinstance(config.get("architecture"), str) or not config["architecture"].strip()
            or type(classes) is not int or not 0 < classes <= 200000):
        raise ValueError
    labels_path = directory / "selected_tags.csv"
    if not 0 < labels_path.stat().st_size <= 32 * 1024 * 1024:
        raise ValueError
    count = 0
    with labels_path.open(encoding="utf-8-sig", newline="") as stream:
        reader = csv.DictReader(stream)
        if not {"name", "category"}.issubset(reader.fieldnames or []):
            raise ValueError
        for row in reader:
            count += 1
            if (count > 200000 or not isinstance(row.get("name"), str)
                    or len(row["name"]) > 10000 or not 0 <= int(row["category"]) <= 99):
                raise ValueError
            threshold = float(row.get("best_threshold") or 0)
            if not math.isfinite(threshold) or not 0 <= threshold <= 1:
                raise ValueError
    if count != classes:
        raise ValueError
    weights = directory / "model.safetensors"
    size = weights.stat().st_size
    with weights.open("rb") as stream:
        length_raw = stream.read(8)
        length = int.from_bytes(length_raw, "little")
        if len(length_raw) != 8 or not 2 <= length <= min(16 * 1024 * 1024, size - 8):
            raise ValueError
        header = json.loads(stream.read(length))
    if not isinstance(header, dict) or len(header) > 200000:
        raise ValueError
    ranges = []
    payload_size = size - length - 8
    for name, tensor in header.items():
        if name == "__metadata__":
            if not isinstance(tensor, dict):
                raise ValueError
            continue
        if not isinstance(tensor, dict) or tensor.get("dtype") not in DTYPE_BYTES:
            raise ValueError
        shape, offsets = tensor.get("shape"), tensor.get("data_offsets")
        if (not isinstance(shape, list) or len(shape) > 32
                or not all(type(value) is int and 0 <= value <= 2**48 for value in shape)
                or not isinstance(offsets, list) or len(offsets) != 2
                or not all(type(value) is int for value in offsets)
                or not 0 <= offsets[0] <= offsets[1] <= payload_size):
            raise ValueError
        elements = math.prod(shape)
        if elements * DTYPE_BYTES[tensor["dtype"]] != offsets[1] - offsets[0]:
            raise ValueError
        ranges.append(tuple(offsets))
    if not ranges or max(end for _, end in ranges) != payload_size:
        raise ValueError
    cursor = 0
    for start, end in sorted(ranges):
        if start != cursor:
            raise ValueError
        cursor = end
    if "preprocess.json" in required:
        _json_file(directory / "preprocess.json")
    # An architecture file is hashed, never imported or executed by this checker.
    if "timm_arch.py" in required and not 0 < (directory / "timm_arch.py").stat().st_size <= 2 * 1024 * 1024:
        raise ValueError


class PromptTagger:
    def __init__(self, comfy: Any, *, manifest_path: Path | None = None):
        self.comfy = comfy
        self.manifest_path = manifest_path or Path(__file__).with_name("tagger-models.json")
        self._hashes: OrderedDict[tuple[Any, ...], str] = OrderedDict()
        self._check_lock = asyncio.Lock()

    def _manifest(self) -> dict[str, list[dict[str, Any]]]:
        try:
            data = _json_file(self.manifest_path, 2 * 1024 * 1024)
            if data.get("schemaVersion") != 1 or not isinstance(data.get("models"), list):
                return {}
            result = {}
            for entry in data["models"]:
                if not isinstance(entry, dict):
                    continue
                name, files = entry.get("relativePath"), entry.get("files")
                if not _safe_name(name) or not isinstance(files, list) or not files:
                    continue
                seen = set()
                for file in files:
                    if (not isinstance(file, dict) or file.get("path") not in ALLOWED_FILES
                            or file["path"] in seen or type(file.get("size")) is not int or file["size"] <= 0
                            or not isinstance(file.get("sha256"), str)
                            or not re.fullmatch(r"[a-fA-F0-9]{64}", file["sha256"])):
                        break
                    seen.add(file["path"])
                else:
                    if REQUIRED_FILES <= seen and (_model_key(name) != _model_key("pixai-tagger-v1.0-timm") or "timm_arch.py" in seen):
                        result[_model_key(name)] = files
            return result
        except (OSError, ValueError, UnicodeError, RecursionError):
            return {}

    @staticmethod
    def _signature(path: Path) -> tuple[Any, ...]:
        stat = path.stat()
        return (str(path.resolve()), stat.st_size, stat.st_mtime_ns, stat.st_ctime_ns, stat.st_dev, stat.st_ino)

    def _hash(self, path: Path) -> str:
        signature = self._signature(path)
        cached = self._hashes.get(signature)
        if cached is not None:
            self._hashes.move_to_end(signature)
            return cached
        digest = hashlib.sha256()
        with path.open("rb") as stream:
            while chunk := stream.read(1024 * 1024):
                digest.update(chunk)
        if signature != self._signature(path):
            raise ValueError
        result = digest.hexdigest()
        self._hashes[signature] = result
        while len(self._hashes) > 256:
            self._hashes.popitem(last=False)
        return result

    def _model_state(self, root: Path, name: str, manifest: dict[str, list[dict[str, Any]]]) -> str:
        path = root / name
        try:
            if not path.exists():
                return "missing"
            if not path.is_dir():
                return "invalid"
            key = _model_key(name)
            files = manifest.get(key)
            if files is None and key in {_model_key(preset) for preset in PRESETS}:
                return "unverifiable"
            required = {item["path"] for item in files} if files else REQUIRED_FILES.copy()
            if key == _model_key("pixai-tagger-v1.0-timm"):
                required.add("timm_arch.py")
            optional_present = {filename for filename in ALLOWED_FILES - REQUIRED_FILES if (path / filename).exists()}
            if files and optional_present - required:
                # The node loads these even when they are not in the model lock.
                return "invalid"
            required |= optional_present
            if not all((path / filename).is_file() for filename in required):
                return "incomplete"
            signatures = {filename: self._signature(path / filename) for filename in required}
            if files:
                for file in files:
                    target = path / file["path"]
                    if target.stat().st_size != file["size"] or self._hash(target) != file["sha256"].lower():
                        return "invalid"
            _validate_structure(path, required)
            if any(signature != self._signature(path / filename) for filename, signature in signatures.items()):
                return "invalid"
            return "ready"
        except (OSError, ValueError, TypeError, KeyError, UnicodeError, csv.Error, RecursionError):
            return "invalid"

    async def _nodes(self) -> list[str]:
        try:
            node_data, preview_data = await asyncio.gather(
                self.comfy._json("GET", "/object_info/WDTimmTagger"),
                self.comfy._json("GET", "/object_info/PreviewAny"),
            )
        except (ComfyError, OSError, asyncio.TimeoutError, AttributeError) as error:
            raise WorkflowError("无法连接本地打标节点，请在启动器中检查服务") from error
        if (not isinstance(node_data, dict) or not isinstance(node_data.get("WDTimmTagger"), dict)
                or not isinstance(preview_data, dict) or "PreviewAny" not in preview_data):
            raise WorkflowError("ComfyUI 缺少 WDTimmTagger 或 PreviewAny 节点，请在启动器中安装")
        node = node_data["WDTimmTagger"]
        raw_inputs = node.get("input")
        if not isinstance(raw_inputs, dict):
            raise WorkflowError("本地打标节点接口不兼容，请在启动器中检查插件")
        required, optional = raw_inputs.get("required", {}), raw_inputs.get("optional", {})
        if not isinstance(required, dict) or not isinstance(optional, dict):
            raise WorkflowError("本地打标节点接口不兼容，请在启动器中检查插件")
        inputs = {**required, **optional}
        fields = {"image", "model_name", "dtype", "general_threshold", "character_threshold", "add_rating", "exclude_tags", "batch_size"}
        if not fields <= inputs.keys() or "auto" not in choices(inputs.get("dtype")):
            raise WorkflowError("本地打标节点接口不兼容，请在启动器中检查插件")
        return list(dict.fromkeys(name for name in choices(inputs.get("model_name")) if _safe_name(name)))[:512]

    async def _directory(self) -> Path:
        try:
            folders, stats = await asyncio.gather(
                self.comfy._json("GET", "/internal/folder_paths"),
                self.comfy._json("GET", "/system_stats"),
            )
        except (ComfyError, OSError, asyncio.TimeoutError, AttributeError) as error:
            raise WorkflowError(PATH_REASON) from error
        return resolve_tagger_directory(folders, stats)

    async def _states(self, root: Path, names: list[str]) -> list[dict[str, str]]:
        def inspect() -> list[dict[str, str]]:
            manifest = self._manifest()
            result = []
            for name in names:
                state = self._model_state(root, name, manifest)
                result.append({"name": name, "state": state, "reason": MODEL_REASONS[state]})
            return result
        async with self._check_lock:
            return await asyncio.to_thread(inspect)

    async def options(self) -> dict[str, Any]:
        public: dict[str, Any] = {"available": False, "reason": "", "models": [], "defaults": DEFAULTS.copy()}
        try:
            names = await self._nodes()
            try:
                root = await self._directory()
            except WorkflowError as error:
                public["models"] = [{"name": name, "state": "unverifiable", "reason": str(error)} for name in names]
                raise
            public["models"] = await self._states(root, names)
            public["available"] = any(item["state"] == "ready" for item in public["models"])
            if not public["available"]:
                public["reason"] = "请在启动器中准备本地反推模型后刷新"
        except WorkflowError as error:
            public["reason"] = str(error)
        return public

    @staticmethod
    def _settings(payload: Any) -> dict[str, Any]:
        if not isinstance(payload, dict) or set(payload) - DEFAULTS.keys():
            raise WorkflowError("本地打标参数无效")
        result = {**DEFAULTS, **payload}
        if not _safe_name(result["model"]):
            raise WorkflowError("本地打标模型名称无效")
        for field in ("general_threshold", "character_threshold"):
            value = result[field]
            if type(value) not in (int, float) or not 0 <= value <= 1 or not math.isfinite(value):
                raise WorkflowError("本地打标阈值必须在 0 到 1 之间")
            result[field] = float(value)
        if not isinstance(result["exclude_tags"], str) or len(result["exclude_tags"]) > 20000:
            raise WorkflowError("排除标签必须是最多 20000 字符的文本")
        return result

    async def _ready(self, settings: dict[str, Any]) -> None:
        names = await self._nodes()
        if settings["model"] not in names:
            raise WorkflowError("所选本地打标模型已不可用，请刷新后重新选择")
        root = await self._directory()
        state = (await self._states(root, [settings["model"]]))[0]
        if state["state"] != "ready":
            raise WorkflowError(state["reason"])

    async def prepare(self, payload: dict[str, Any]) -> dict[str, Any]:
        settings = self._settings(payload.get("tagger", {}))
        try:
            profile = normalize_model_profile(payload.get("model_profile", "anima"))
        except ValueError as error:
            raise WorkflowError("模型档案无效") from error
        image_id = payload.get("image_id")
        if not isinstance(image_id, str) or not re.fullmatch(r"[a-f0-9]{64}", image_id):
            raise WorkflowError("反推图片 ID 无效，请重新上传图片")
        await self._ready(settings)
        return {"kind": "reverse", "method": "tagger", "workbench": "single", "model_profile": profile,
                "image_id": image_id, "tagger": settings}

    async def transport(self, snapshot: dict[str, Any]) -> dict[str, Any]:
        settings = self._settings(snapshot.get("tagger", {}))
        await self._ready(settings)
        return {**copy.deepcopy(snapshot), "tagger": settings}
