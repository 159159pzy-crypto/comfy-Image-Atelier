"""Read an owned editable distribution; never import project code or run its backend."""
import base64
import csv
import hashlib
import importlib.metadata as metadata
import io
import json
import os
from pathlib import Path
import re
import stat
import sys
import sysconfig
import tomllib
import uuid
from urllib.parse import unquote, urlsplit


def digest(data):
    return hashlib.sha256(data).hexdigest()


def inspect(app_root, bridge=None, authorizations=()):
    from packaging.requirements import Requirement
    from packaging.specifiers import SpecifierSet
    from packaging.utils import canonicalize_name
    from packaging.version import Version

    name = "anima-random-studio"
    found = [d for d in metadata.distributions() if canonicalize_name(d.metadata.get("Name", "")) == name]
    if not found:
        if not authorizations:
            return None
        if not app_root:
            raise ValueError("中断工作台注册缺少原 AppRoot，未重新注册。")
        root = Path(app_root).resolve(strict=True)
        raw = (root / "pyproject.toml").read_bytes()
        project = tomllib.loads(raw.decode("utf-8-sig")).get("project", {})
        version_bytes = (root / "launcher" / "version.txt").read_bytes() if project.get("dynamic") else b""
        identity = digest(raw + b"\0" + version_bytes + b"\0" + os.path.normcase(str(root)).encode("utf-8"))
        selected = next((item for item in reversed(authorizations) if item["plan"]["appRoot"] == str(root)
            and item["plan"]["inputIdentity"] == identity), None)
        package = root / "anima_webui" / "__init__.py"
        if selected is None or canonicalize_name(project.get("name", "")) != name or not package.is_file() or package.resolve().parent != root / "anima_webui":
            raise ValueError("缺失工作台包的中断记录与当前完整源码输入不符，未重新注册。")
        if project.get("requires-python") and not SpecifierSet(project["requires-python"]).contains(".".join(map(str, sys.version_info[:3])), prereleases=True):
            raise ValueError("当前 Python 不满足工作台 Requires-Python。")
        prefix = Path(sys.prefix).resolve(strict=True)
        base = Path(sysconfig.get_path("purelib")).resolve(strict=True)
        targets = set()
        for relative, sha in selected["payload"].items():
            target = base / relative
            resolved = target.resolve()
            if not resolved.is_relative_to(prefix):
                raise ValueError("中断工作台注册目标超出 Python。")
            targets.add(os.path.normcase(str(resolved)))
            for parent in (target, *target.parents):
                if parent == prefix.parent:
                    break
                if parent.exists():
                    info = parent.lstat()
                    if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 1024):
                        raise ValueError("缺失注册的目标路径含重解析点。")
            if target.exists() and (not target.is_file() or digest(target.read_bytes()) != sha):
                raise ValueError("缺失注册的目标存在未知内容，未覆盖。")
        for other in metadata.distributions():
            other_base = str(other.locate_file(""))
            if any(os.path.normcase(os.path.abspath(os.path.join(other_base, str(path)))) in targets for path in other.files or []):
                raise ValueError("缺失工作台注册目标已由其他分发占用，未覆盖。")
        plan = dict(selected["plan"], installedIdentity=digest(("missing\n" + selected["transportSha256"]).encode("utf-8")), installedVersion="")
        return {"plan": plan, "files": {}, "installedMetadata": "", "pendingTransportUrl": selected["transportUrl"], "finalOriginSha256": "", "finalRecordSha256": ""}
    if len(found) != 1:
        raise ValueError("工作台存在重复的包元数据，未接管或修改。")
    if not app_root:
        raise ValueError("已安装工作台包，但未指定可确认归属的工作台目录。")
    root = Path(app_root).resolve(strict=True)
    prefix = Path(sys.prefix).resolve(strict=True)
    distribution = found[0]
    base = Path(distribution.locate_file("")).resolve(strict=True)
    if not base.is_relative_to(prefix):
        raise ValueError("工作台包位于所选独立 Python 之外，未接管。")

    def safe_file(path):
        lexical = Path(os.path.abspath(path))
        resolved = lexical.resolve(strict=True)
        if not resolved.is_relative_to(prefix):
            raise ValueError("工作台包 RECORD 指向 Python 之外，未接管。")
        for item in (lexical, *lexical.parents):
            if item == prefix.parent:
                break
            info = item.lstat()
            if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 1024):
                raise ValueError("工作台包路径含重解析点，未接管。")
        if not resolved.is_file():
            raise ValueError("工作台包 RECORD 不是普通文件。")
        return resolved

    metadata_paths = [str(f).replace("\\", "/") for f in (distribution.files or []) if str(f).replace("\\", "/").endswith(".dist-info/METADATA")]
    if len(metadata_paths) != 1:
        raise ValueError("工作台包缺少唯一且可验证的 dist-info/RECORD，未接管。")
    folder = Path(metadata_paths[0]).parent
    record_path = safe_file(base / folder / "RECORD")
    record_bytes = record_path.read_bytes()
    authorized = [item for item in authorizations if item["plan"]["appRoot"] == str(root)]
    if bridge:
        authorized = [bridge, *authorized]
    final_origin_bytes = (json.dumps({"url": root.as_uri(), "dir_info": {"editable": True}}, sort_keys=True) + "\n").encode("utf-8")
    pending_record = [item for item in authorized if item.get("originalRecordSha256") == digest(record_bytes)
                      and item.get("finalOriginSha256") == digest(final_origin_bytes)]
    interrupted_origin = False
    rows = list(csv.reader(record_bytes.decode("utf-8").splitlines()))
    files = {}
    paths = set()
    for row in rows:
        if len(row) != 3:
            raise ValueError("工作台包 RECORD 格式无效。")
        relative, recorded, size = row
        relative = relative.replace("\\", "/")
        lexical = Path(os.path.abspath(base / relative))
        if not lexical.is_relative_to(prefix):
            raise ValueError("工作台包 RECORD 路径超出 Python，未接管。")
        # Bytecode is generated by Python, not an installation ownership proof.
        if relative.endswith(".pyc") and not recorded:
            if lexical.exists():
                paths.add(os.path.normcase(str(safe_file(lexical))))
            continue
        path = safe_file(lexical)
        key = os.path.normcase(str(path))
        if key in paths:
            raise ValueError("工作台包 RECORD 存在重复路径。")
        paths.add(key)
        data = path.read_bytes()
        if path != record_path:
            algorithm, separator, value = recorded.partition("=")
            if not separator or algorithm not in ("sha256", "sha384", "sha512"):
                raise ValueError("工作台包文件缺少可验证的 RECORD 哈希，未接管。")
            actual = base64.urlsafe_b64encode(hashlib.new(algorithm, data).digest()).rstrip(b"=").decode("ascii")
            if actual != value or size and len(data) != int(size):
                if relative == (folder / "direct_url.json").as_posix() and pending_record and data == final_origin_bytes:
                    interrupted_origin = True
                else:
                    raise ValueError("工作台包文件与 RECORD 不一致，未接管：" + relative)
        files[relative] = digest(data)
    owned_basenames = {Path(path).name for path in paths}
    for other in metadata.distributions():
        if canonicalize_name(other.metadata.get("Name", "")) == name:
            continue
        other_base = str(other.locate_file(""))
        for relative in other.files or []:
            declared = os.path.abspath(os.path.join(other_base, str(relative)))
            if os.path.normcase(declared) in paths or Path(declared).name in owned_basenames and os.path.normcase(str(Path(declared).resolve())) in paths:
                raise ValueError("工作台 RECORD 与其他已安装分发共用文件，未授权卸载或替换：" + str(relative))
    for required in ("METADATA", "RECORD", "direct_url.json"):
        if (folder / required).as_posix() not in files:
            raise ValueError("工作台包 RECORD 缺少 " + required)
    origin = json.loads(safe_file(base / folder / "direct_url.json").read_text(encoding="utf-8"))
    authorized = [item for item in authorized if all(files.get(path) == sha for path, sha in item["payload"].items())]
    if interrupted_origin:
        authorized = [item for item in authorized if item in pending_record]
        if not authorized:
            raise ValueError("中断注册与本计划 payload 不一致，未接管。")
    matched_authorization = None
    url = urlsplit(origin.get("url", ""))
    if url.scheme != "file" or url.netloc not in ("", "localhost") or url.query or url.fragment:
        raise ValueError("已安装工作台并非可确认的本项目 editable 包，未接管。")
    local = unquote(url.path)
    if os.name == "nt" and re.match(r"^/[A-Za-z]:/", local):
        local = local[1:]
    if origin.get("dir_info", {}).get("editable") is True:
        if os.path.normcase(str(Path(local).resolve(strict=True))) != os.path.normcase(str(root)):
            raise ValueError("已安装工作台 editable 来源与当前 AppRoot 不同，未接管。")
        if interrupted_origin:
            matched_authorization = authorized[0]
        else:
            matched_authorization = next((item for item in authorized if item.get("finalRecordSha256") == digest(record_bytes)
                and item.get("finalOriginSha256") == files[(folder / "direct_url.json").as_posix()]), None)
    else:
        matched_authorization = next((item for item in authorized if origin.get("url") == item["transportUrl"]
            and origin.get("archive_info", {}).get("hashes", {}).get("sha256", item["transportSha256"]) == item["transportSha256"]), None)
        if matched_authorization is None:
            raise ValueError("已安装工作台不是本计划刚安装的精确临时桥接资源，未接管。")
    if matched_authorization:
        allowed = set(matched_authorization["payload"]) | {(folder / file).as_posix() for file in ("RECORD", "INSTALLER", "REQUESTED", "direct_url.json", "uv_cache.json")}
        if set(files) - allowed:
            raise ValueError("中断工作台注册含未授权文件，未接管。")

    raw = (root / "pyproject.toml").read_bytes()
    document = tomllib.loads(raw.decode("utf-8-sig"))
    project = document.get("project", {})
    if canonicalize_name(project.get("name", "")) != name:
        raise ValueError("当前目录不是已确认的 anima-random-studio 项目。")
    if document.get("build-system", {}).get("build-backend", "setuptools.build_meta") != "setuptools.build_meta":
        raise ValueError("当前工作台使用不受支持的构建后端，未执行或接管。")
    if any(project.get(key) for key in ("scripts", "gui-scripts", "entry-points")):
        raise ValueError("当前工作台声明了桥接器不支持的入口点，未接管。")
    dynamic = project.get("dynamic", [])
    version_bytes = b""
    if dynamic:
        if dynamic != ["version"] or document.get("tool", {}).get("setuptools", {}).get("dynamic", {}).get("version", {}).get("file") != "launcher/version.txt":
            raise ValueError("当前工作台版本声明不受支持，未执行动态代码。")
        version_bytes = (root / "launcher" / "version.txt").read_bytes()
        version = str(Version(version_bytes.decode("utf-8-sig").strip()))
    else:
        version = str(Version(project["version"]))
    package = root / "anima_webui" / "__init__.py"
    if not package.is_file() or package.resolve().parent != root / "anima_webui":
        raise ValueError("工作台 anima_webui 源码缺失或重定向。")
    headers = ["Metadata-Version: 2.3", "Name: " + name, "Version: " + version]
    python = project.get("requires-python", "")
    if python:
        if "\n" in python or "\r" in python or not SpecifierSet(python).contains(".".join(map(str, sys.version_info[:3])), prereleases=True):
            raise ValueError("当前 Python 不满足工作台 Requires-Python。")
        headers.append("Requires-Python: " + python)
    requirements = project.get("dependencies")
    if not isinstance(requirements, list) or not all(isinstance(v, str) and "\n" not in v and "\r" not in v for v in requirements):
        raise ValueError("工作台 dependencies 无效。")
    for value in requirements:
        headers.append("Requires-Dist: " + str(Requirement(value)))
    extras = project.get("optional-dependencies", {})
    if not isinstance(extras, dict):
        raise ValueError("工作台 extras 无效。")
    seen_extras = set()
    for extra, values in sorted(extras.items()):
        normalized = canonicalize_name(extra)
        if not re.fullmatch(r"[a-z0-9]+(?:-[a-z0-9]+)*", normalized) or normalized in seen_extras or not isinstance(values, list):
            raise ValueError("工作台 extra 名称无效或重复。")
        seen_extras.add(normalized)
        headers.append("Provides-Extra: " + normalized)
        for value in values:
            requirement = Requirement(value)
            marker = str(requirement.marker) if requirement.marker else ""
            requirement.marker = None
            headers.append("Requires-Dist: " + str(requirement) + "; " + ("(" + marker + ") and " if marker else "") + "extra == '" + normalized + "'")
    content = "\n".join(headers) + "\n\n"
    installed_metadata = safe_file(base / metadata_paths[0]).read_text(encoding="utf-8")
    origin_relative = (folder / "direct_url.json").as_posix()
    finish_rows = [row for row in rows if row[0].replace("\\", "/") != origin_relative]
    finish_rows.append([origin_relative, "sha256=" + base64.urlsafe_b64encode(hashlib.sha256(final_origin_bytes).digest()).rstrip(b"=").decode("ascii"), str(len(final_origin_bytes))])
    record_text = io.StringIO(newline="")
    csv.writer(record_text).writerows(finish_rows)
    final_record_bytes = record_text.getvalue().encode("utf-8")
    result = {"plan": {"schemaVersion": 1, "name": name, "version": version, "appRoot": str(root),
        "inputIdentity": digest(raw + b"\0" + version_bytes + b"\0" + os.path.normcase(str(root)).encode("utf-8")),
        "metadata": content, "requirementsText": "\n".join(requirements) + ("\n" if requirements else ""),
        "installedIdentity": digest(json.dumps(files, sort_keys=True, separators=(",", ":")).encode("utf-8")),
        "installedVersion": distribution.version}, "files": files, "installedMetadata": installed_metadata,
        "pendingTransportUrl": matched_authorization["transportUrl"] if matched_authorization else "",
        "finalOriginSha256": digest(final_origin_bytes), "finalRecordSha256": digest(final_record_bytes)}
    if bridge:
        expected = bridge["plan"]
        if any(result["plan"][key] != expected[key] for key in ("schemaVersion", "name", "version", "appRoot", "inputIdentity", "metadata", "requirementsText")):
            raise ValueError("工作台桥接输入已变化，未完成注册收尾。")
        if distribution.version != version or installed_metadata != content or any(files.get(path) != sha for path, sha in bridge["payload"].items()):
            raise ValueError("已安装内容并非本计划的精确桥接 payload，未完成注册收尾。")
        allowed = set(bridge["payload"]) | {(folder / file).as_posix() for file in ("RECORD", "INSTALLER", "REQUESTED", "direct_url.json", "uv_cache.json")}
        if set(files) - allowed:
            raise ValueError("工作台注册含未授权文件，未完成注册收尾。")
        if not bridge.get("finalize"):
            return result
        if digest(record_bytes) not in (bridge.get("originalRecordSha256"), bridge.get("finalRecordSha256")):
            raise ValueError("注册收尾前 RECORD 已变化，未覆盖。")
        # This is the editable frontend's PEP 610 step, not a METADATA patch.
        origin_path = safe_file(base / folder / "direct_url.json")
        origin_bytes = final_origin_bytes
        temporary = record_path.with_name("RECORD.anima-" + uuid.uuid4().hex)
        origin_temporary = origin_path.with_name("direct_url.anima-" + uuid.uuid4().hex)
        try:
            with temporary.open("xb") as output:
                output.write(final_record_bytes)
            with origin_temporary.open("xb") as output:
                output.write(origin_bytes)
            os.replace(origin_temporary, origin_path)
            os.replace(temporary, record_path)
        finally:
            if origin_temporary.exists():
                origin_temporary.unlink()
            if temporary.exists():
                temporary.unlink()
        return inspect(app_root)
    return result


if __name__ == "__main__":
    sys.path.insert(0, sys.argv[1])
    request = json.loads(sys.argv[2])
    print(json.dumps(inspect(request["appRoot"], request.get("bridge"), request.get("authorizations", [])), ensure_ascii=True))
