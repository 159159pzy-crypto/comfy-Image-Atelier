"""Inspect declared requirements using metadata only; never install or import nodes.

The launcher supplies its private, hash-verified packaging archive. No pip,
packaging, PyYAML, torch or other package is needed in the inspected environment.
"""
from __future__ import annotations

import hashlib
import importlib.metadata as metadata
import json
from pathlib import Path
import re
import sys
import tomllib
from urllib.parse import parse_qs, unquote, urlsplit, urlunsplit


def workbench_contract(app_root: str) -> dict:
    if not app_root:
        return {"requirementsText": "", "identity": ""}
    path = Path(app_root) / "pyproject.toml"
    project = tomllib.loads(path.read_text(encoding="utf-8-sig")).get("project", {})
    requirements = project.get("dependencies")
    if not isinstance(requirements, list) or not all(isinstance(item, str) and "\n" not in item and "\r" not in item for item in requirements):
        raise ValueError("工作台 pyproject.toml 缺少有效的 project.dependencies")
    text = "\n".join(requirements) + ("\n" if requirements else "")
    # Bind the actual selected workbench contract, not the launcher's bundled version.
    identity = hashlib.sha256(text.encode("utf-8")).hexdigest()
    return {"requirementsText": text, "identity": identity}


def inspect(request: dict) -> dict:
    from packaging.requirements import InvalidRequirement, Requirement
    from packaging.utils import canonicalize_name
    from packaging.version import InvalidVersion

    issues: list[dict] = []
    requirements: list[tuple] = []
    constraints: list[tuple] = []
    seen_files: set[tuple] = set()
    visited: set[tuple] = set()

    def issue(state: str, requirement: str, source: str, message: str):
        value = {"state": state, "requirement": requirement, "source": source, "message": message}
        if value not in issues:
            issues.append(value)

    def parse(text: str, source: str, constraint: bool = False):
        pending = ""
        for number, raw in enumerate(text.splitlines(), 1):
            line = pending + raw.strip()
            if line.endswith("\\"):
                pending = line[:-1] + " "
                continue
            pending = ""
            line = re.sub(r"\s+#.*$", "", line).strip()
            if not line or line.startswith("#"):
                continue
            location = f"{source}:{number}"
            include = re.match(r"^(?:(-r)\s*|(--requirement)(?:=|\s)+|(-c)\s*|(--constraint)(?:=|\s)+)(.+)$", line)
            if include:
                value = include[5].strip().strip("\"'")
                if re.match(r"https?://", value, re.I) or "${" in value:
                    issue("unknown", line, location, "不能离线验证远程或环境变量引用的依赖文件")
                else:
                    read_file(Path(source).parent / value, constraint or bool(include[3] or include[4]))
                continue
            # These affect package retrieval, not whether installed metadata satisfies a requirement.
            if re.match(r"^--(?:index-url|extra-index-url|find-links|trusted-host|only-binary|no-binary)(?:=|\s)", line) or line in {"--no-index", "--prefer-binary", "--pre", "--require-hashes"}:
                continue
            if line.startswith("-") or "${" in line:
                issue("unknown", line, location, "依赖使用了需要安装解析器确认的选项或路径")
                continue
            line = re.sub(r"\s+--hash(?:=|\s+)\S+", "", line)
            try:
                requirement = Requirement(line)
                if requirement.marker and not requirement.marker.evaluate({"extra": ""}):
                    continue
            except (InvalidRequirement, ValueError, KeyError) as error:
                issue("unknown", line, location, "无法离线解析依赖：" + str(error))
                continue
            (constraints if constraint else requirements).append((requirement, location))
        if pending:
            issue("unknown", pending, source, "依赖文件以未完成的续行结尾")

    def read_file(path: Path, constraint: bool = False):
        path = path.resolve()
        key = (str(path).casefold(), constraint)
        if key in seen_files:
            return
        seen_files.add(key)
        try:
            parse(path.read_text(encoding="utf-8-sig"), str(path), constraint)
        except (OSError, UnicodeError) as error:
            issue("unknown", str(path), str(path), "无法读取依赖文件：" + str(error))

    installed: dict[str, list] = {}
    for distribution in metadata.distributions():
        name = distribution.metadata.get("Name")
        if name:
            installed.setdefault(canonicalize_name(name), []).append(distribution)

    def origin_matches(requirement, distribution) -> bool | None:
        """Only immutable, recorded direct origins prove a URL requirement."""
        try:
            origin_text = distribution.read_text("direct_url.json")
            if not origin_text:
                return None
            origin = json.loads(origin_text)
            expected = urlsplit(requirement.url)
            recorded = urlsplit(origin.get("url", ""))
            if origin.get("subdirectory", "") != parse_qs(expected.fragment).get("subdirectory", [""])[0]:
                return False
            def clean(parts):
                return urlunsplit((parts.scheme.lower(), parts.netloc.lower(), unquote(parts.path).rstrip("/"), parts.query, ""))
            if expected.scheme.startswith("git+"):
                path, separator, commit = expected.path.rpartition("@")
                if not separator or not re.fullmatch(r"[a-fA-F0-9]{40}|[a-fA-F0-9]{64}", commit):
                    return None
                expected = expected._replace(scheme=expected.scheme[4:], path=path)
                actual_commit = origin.get("vcs_info", {}).get("commit_id", "")
                return clean(expected).removesuffix(".git") == clean(recorded).removesuffix(".git") and actual_commit.lower() == commit.lower()
            hashes = parse_qs(expected.fragment)
            if "sha256" not in hashes or not re.fullmatch(r"[a-fA-F0-9]{64}", hashes["sha256"][0]):
                return None
            actual = origin.get("archive_info", {}).get("hashes", {}).get("sha256")
            return clean(expected) == clean(recorded) and actual == hashes["sha256"][0]
        except (TypeError, ValueError, KeyError, AttributeError):
            return None

    def check(requirement, source: str, constraint: bool = False):
        name = canonicalize_name(requirement.name)
        matches = installed.get(name, [])
        if not matches:
            if not constraint:
                issue("missing", str(requirement), source, "尚未安装 " + requirement.name)
            return
        if len(matches) != 1:
            issue("unknown", str(requirement), source, "发现重复的包元数据：" + requirement.name)
            return
        distribution = matches[0]
        try:
            if requirement.specifier and not requirement.specifier.contains(distribution.version, prereleases=True):
                issue("incompatible", str(requirement), source, f"{requirement.name} 已安装 {distribution.version}，要求 {requirement.specifier}")
                return
        except (InvalidVersion, TypeError) as error:
            issue("unknown", str(requirement), source, "包版本元数据无效：" + str(error))
            return
        if requirement.url:
            match = origin_matches(requirement, distribution)
            if match is not True:
                issue("unknown" if match is None else "incompatible", str(requirement), source,
                      "无法从已安装元数据验证固定下载来源" if match is None else "已安装包的来源与固定依赖不同")
                return
        if constraint:
            return
        key = (name, tuple(sorted(requirement.extras)))
        if key in visited:
            return
        visited.add(key)
        for value in distribution.requires or []:
            try:
                child = Requirement(value)
                if child.marker and not any(child.marker.evaluate({"extra": extra}) for extra in ({""} | requirement.extras)):
                    continue
                check(child, f"{requirement.name}=={distribution.version} → {source}")
            except (InvalidRequirement, ValueError, KeyError) as error:
                issue("unknown", value, source, "无法读取传递依赖：" + str(error))

    for file in request.get("files", []):
        read_file(Path(file))
    contract = workbench_contract(request.get("appRoot", ""))
    parse(contract["requirementsText"], str(Path(request.get("appRoot", "")) / "pyproject.toml"))
    for requirement, source in requirements:
        check(requirement, source)
    for requirement, source in constraints:
        check(requirement, source, constraint=True)
    return {"issues": issues, "workbench": contract}


def main():
    # The archive path is supplied by the launcher after verifying its embedded bytes.
    sys.path.insert(0, sys.argv[1])
    request = json.loads(sys.argv[3])
    result = workbench_contract(request.get("appRoot", "")) if sys.argv[2] == "contract" else inspect(request)
    print(json.dumps(result, ensure_ascii=True))


if __name__ == "__main__":
    main()
