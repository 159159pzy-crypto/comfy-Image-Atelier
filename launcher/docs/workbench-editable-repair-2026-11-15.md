# 工作台可编辑安装信息修复（2026-11-15）

## 故障与原因

真实环境 `F:\comfyui` 在更新到 ComfyUI 0.45.0 时，已按当前工作台源码安装 Pillow 13.0.0，但 Python 仍登记 `anima-random-studio 0.6.0`，其中依赖为 `Pillow>=10,<13`。`pip check` 因此失败。轻量恢复已将源码恢复至 0.43.0，但新版本的核心前端、模板、文档与 kitchen 包仍需按恢复版本修复，不能宣称 Python 已完整回滚。

原因是原解析流程只纳入工作台的依赖声明，未同步它自身的旧可编辑分发元数据。当前源码动态版本为 `launcher/version.txt` 中的 1.6.2；改变依赖或版本后，可编辑安装的旧元数据不会自动随源码刷新。[PEP 660](https://peps.python.org/pep-0660/) 说明了这一安装语义。

## 实现

- 准备阶段使用只读检查器确认唯一的本项目可编辑安装、真实 `AppRoot`、所选独立 Python、完整 RECORD 哈希及文件所有权；来源不同、普通非可编辑包、重复登记和共享文件均拒绝接管。
- 冻结 `pyproject.toml`、版本文件、完整依赖与 extras／Python 限制，生成仅映射 `anima_webui` 的临时可编辑 wheel。通过正常 pip 或 uv 离线安装，不执行工作台构建后端，不直接改写已安装 METADATA，也不将整个源码根目录加入 `sys.path`。
- 临时 wheel 有固定哈希，准备与应用之间再次核对；不进入持久下载缓存，完成、取消或释放准备对象后清理。普通依赖仍复用原缓存。保留原根约束文件与 GPU 版本。
- 安装前保存本环境、本次临时 wheel 与完整内容的授权记录；安装中取消后只收尾已经核实的本项目登记，不写整体成功凭据。强制退出后，检查／准备阶段只读识别精确的中断内容，后续应用再安装并完成登记；来源记录和 RECORD 之间的中断也保留可核对的前后哈希。
- 旧计划仍保持同一个 ComfyUI 提交；依赖修复成功后只清除过期的依赖解析结果，保留稳定标签、提交与声明，供下一次更新重新解析。
- 全局 `pip check` 继续执行；无关依赖冲突不能被本修复隐藏。成功仍需通过隔离 ComfyUI 核心探测，不宣称全部插件或生图链路已经验证。

## 复现证据

修复前使用冻结旧 Core、实际独立 venv 与本地测试 wheel 复现：当前源码声明检查通过，但安装 Pillow 13 后被旧工作台元数据阻止。共 12 项复现断言通过。

保留可执行复现：`output/workbench-metadata-repro-bb64638155a4432bb00510c17d9a2318/`，包含 `BaselineFixture.cs`、`Repro.csproj` 和旧 Core。

## 回归与构建

| 范围 | 结果 | 证据 |
| --- | --- | --- |
| 可编辑登记、实际 pip／uv、取消与硬退出恢复 | 66 项通过（60 专项＋6 通用） | `--workbench-metadata-only`；`output/launcher-tests/e9feca83b3ec4364b1eb7925f2de43c5/workbench-metadata-regression.json` |
| 原依赖准备、安装、缓存与构建隔离 | 最终代码 157 项通过 | `--dependency-installation-only`；`output/workbench-final-dependency-regression.log` |
| 轻量更新与恢复、修复后固定目标重试 | 101 项通过 | `--comfy-release-only`；新增 3 项验证只重置依赖审核 |
| 运行依赖、工作台联合解析和历史计划 | 96 项通过 | `output/workbench-runtime-regression.log` |

运行依赖测试原先借用开发 Python，受到其中已安装工作台包的影响；现改用独立 venv，保留实际安装归属检查，不通过放宽产品校验掩盖测试污染。各组包含重复通用检查，不能相加为去重用例总数。

新增组使用 6 个独立 venv、真实 pip 和已验证的捆绑 uv，全部资源来自本地测试 wheel，HTTP 下载次数为 0。覆盖 0.6.0 升级与同版本 1.6.2 登记刷新、旧固定计划、extras／Python 限制、源码 `.egg-info` 不重复暴露、来源／文件共占／漂移拒绝、准备与取消不写 Python、完整缓存重试、全局无关冲突仍失败、实际安装后取消、子进程退出码 77 跳过 finally、来源／RECORD 两文件间中断，以及旧包已卸载但新登记尚不存在时的只读识别与后续重装。源码、根约束和 GPU 包内容均有前后哈希检查。

构建命令：

```powershell
.\Build-Launcher.ps1 -CandidateDirectory 'dist\candidates\workbench-editable-fix-20261115-2'
```

最终构建日志 `output/workbench-editable-build-final.log` 无 warning/error。单文件 EXE、仅含 EXE 的便携 ZIP、安装器、嵌入资源与校验和均通过候选自检。EXE SHA-256 为 `f23528a76a010d926ae64f70ae6e4887c135b0bc0661322a6c001b4a2dd2f7c5`，保留版本号 1.6.2。`candidate.json.sha256` 是便携 ZIP 的哈希；逐文件校验使用 `SHA256SUMS.txt`，并已直接核对原入口与候选 EXE 字节一致。构建发生在本修复提交前，`build-info.json` 如实记录基线 `dc948b43e4f36774491bee6b8f862bc48342e7ca` 和 `sourceDirty=true`；未发布 GitHub Release。

## 真实环境恢复与入口部署

最终代码通过独立 harness 调用产品修复路径，持有缓存和环境操作 lease：环境／停机预检 → 按恢复提交联合解析 → 准备并校验 → 离线应用 → 恢复版声明检查 → `pip check` → 隔离核心探测 → 完成恢复记录 → 重置固定目标的依赖审核。没有使用测试 wheel，也没有手工修改正式 METADATA。

- 环境为 `F:\comfyui`，Python 为其 `.venv\Scripts\python.exe`，工作台来源为 `F:\comfyuishengtu`。
- `anima-random-studio` 从 0.6.0 登记为 1.6.2，来源仍是本项目 editable，旧 0.6.0 dist-info 已移除。Pillow 保持 13.0.0。
- 前端、模板、文档、kitchen 及三个模板子包恢复为 0.43.0 源码要求；SAM-2 保持原提交／版本。torch 2.13.0+cu130、torchvision 0.28.0+cu130、torchaudio 2.11.0+cu130 均保持不变。
- 两次 `pip check` 通过。独立核心在临时端口 6520 通过进程身份、HTTP 和核心节点检查，随后探测进程与端口清理。没有启动正式工作台或验证完整插件／生图链路。
- 恢复记录成为 `rolled-back / recovered / CoreVerified=true`。HEAD 仍为 `426f5eea52a7ece1dd599fff4cb6cd1f352ddd24`，必要配置哈希、`user/input/output` 路径／长度／修改时间／属性指纹均保持不变。用户数据目录未做逐文件内容哈希。
- 原选定目标 `v0.45.0 / 989d93d0d968e704c589ce0146bb18c77f6b50cc` 保留，依赖计划重置为未解析；本次没有继续升级该目标。临时桥接目录、注册 pending 均已清理。

真实验收返回码 0，证据在 `output/comfy-real-recovery-harness/evidence/20261114T235558067Z-repair/`，包含 `result.json`、`before.json`、`after.json`、`requirements-check.json` 和 `harness.log`。

通过原有 `Install-LauncherEntry.ps1` 将最终 `-2` 候选部署到 `F:\comfyuishengtu\AnimaRandomStudio.exe`，原 EXE 保留在 `.launcher/entry-backup-*.exe`，入口与候选 EXE 哈希一致。重新打开原入口后，真实环境页显示当前 v0.43.0、固定目标 v0.45.0、核心启动检查通过；「更新 ComfyUI」与「启动工作台」可用，不再显示待修复入口。截图和 UI 树保存于 `output/workbench-editable-ui-20261115/repaired-environment.png` 与 `repaired-environment-tree.txt`。两个正式服务保持停止，窗口留在环境页。
