# 启动器新手引导与资源检查验收（2026-11-15）

本轮保留六步向导、新建 Anima 三件套与 Sesqui、工作台默认生成参数和旧冻结任务。选择功能后统一检查；确认页展示真实补齐内容，完成页分别说明环境连接和生成资源状态。所有候选与验收配置独立保存，日常入口和模型保持原样。

本文为公开验收摘要，路径已匿名化：`<ComfyUI>` 表示被检查的环境根目录，`<项目目录>` 表示仓库根目录，`<隔离验收目录>` 表示专用测试目录。下文 `output/` 路径均相对于仓库根目录；原始日志、截图和完整本机报告保存在被 Git 忽略的该目录中，不随源码或发行包公开。资源记录是一套已有环境的验收样本，不代表新安装会下载全部资源；重跑结果取决于所用环境与源码版本。

## 资源审计

启动器保存配置中的 `<ComfyUI>/.venv/Scripts/python.exe` 及仓库的 `<项目目录>/.venv/Scripts/python.exe` 均有 aiohttp 3.14.5、multidict 6.9.1、Pillow 13.0.0，满足本轮工作台声明，`pip check` 通过。证据分别为 `output/launcher-onboarding-configured-python.json`、`output/launcher-onboarding-configured-pip-check.log` 和 `output/launcher-onboarding-workspace-python.json`、`output/launcher-onboarding-workspace-pip-check.log`。这不代表全部 GPU 分支已经执行。已修正的问题集中在检查范围、SAM2 来源声明、导入解释器兼容提示和向导状态展示；新安装、功能准备与旧任务分别保留各自资源范围。

**实时进程与保存配置不同：** 末轮只读复核时，日常 ComfyUI 0.45.0 进程使用全局安装的 Python 3.11 启动 `<ComfyUI>/main.py`，不是保存配置中的 `.venv`。这个全局 Python 的 aiohttp 3.13.4、multidict 6.7.1、Pillow 12.2.0 不满足本轮工作台相应的新声明；它的 `pip check` 无已登记包之间的冲突，并不等于满足本项目要求。本轮没有切换或停止该服务，不能把已配置 venv 的检查结果归到该运行进程。证据：`output/launcher-onboarding-runtime-identity.json`、`output/launcher-onboarding-daily-python.json`、`output/launcher-onboarding-daily-pip-check.log`。

全局 Python 还报告一项项目目录外的无效安装元数据；未发现本项目对它的引用，已记录并保留，没有执行删除或卸载。原始包名、路径和文件清单仅保存在本地证据 `output/launcher-onboarding-global-residual.json`。

| 已核对的资源 | 相对于 `<ComfyUI>` 的示例位置 | 用途与处理 |
| --- | --- | --- |
| Impact Pack | `custom_nodes/comfyui-impact-pack` | 旧工作流兼容，保留 |
| Impact Subpack | `custom_nodes/comfyui-impact-subpack` | 旧工作流兼容，保留 |
| Easy Use | `custom_nodes/comfyui-easy-use` | 旧工作流兼容，保留 |
| Neural corrector | `models/forbidden_vision/ForbiddenVision_neural_corrector_v1.safetensors` | 其他 Forbidden Vision 工作流使用，当前工作台无需，保留 |
| Remacri | `models/upscale_models/4x_foolhardy_Remacri.pth` | 当前默认及旧预设引用，保留 |
| AnimeSharp | `models/upscale_models/4x-AnimeSharp.pth` | 当前可选下载模型，保留 |

插件目录体积受 `.git` 与缓存影响，本地库存大小不作为安装预算。没有执行大模型全盘哈希或删除。样本环境还通过 `extra_model_paths.yaml` 接入外部共享模型目录，不能按本工作台未引用判定可删除。旧约束表和旧节点记录用于冻结计划兼容，不作为卸载清单。

## 验证记录

### 自动回归

| 检查 | 结果 | 本轮证据 |
| --- | --- | --- |
| .NET Release 测试项目构建 | 0 警告、0 错误 | `output/launcher-onboarding-tests-final-build.log` |
| 六步向导、就绪状态、模型恢复 | 316 项通过 | `output/launcher-onboarding-tests-final.log` |
| 工作台依赖合同与来源检查 | 102 项通过 | `output/launcher-onboarding-dependencies-20261115/runtime-dependencies-tests.log` |
| 导入环境预检 | 28 项通过 | `output/launcher-onboarding-dependencies-20261115/setup-import-tests.log` |
| 资源分类与冻结策略 | 169 项通过 | `output/launcher-onboarding-resource-policy.log` |
| 功能准备与下载状态 | 54 项通过 | `output/launcher-onboarding-feature-tests.log` |
| 节点更新、回退和资源范围 | 212 项通过 | `output/launcher-onboarding-node-updates.log` |
| Python 相关回归 | 74 项及 22 个子测试通过 | `output/launcher-onboarding-python.log` |

各分组存在重叠，不相加宣称去重总数。覆盖新建默认选择、完整/缺模型导入合同、依赖冲突、SAM2 正确/错误提交及未知来源、未选插件分类、旧冻结计划恢复、仅 AnimeSharp/两种放大模型共存/全部缺失、共享 VAE 别名复用，以及过期检查结果不能恢复安装权限。

旧 Python 与位数检查通过模拟 `sys.version_info` 和指针位数覆盖 Python 3.10 x64、Python 3.11 x86；没有在本机另装旧版或 32 位解释器。新建和模型测试使用临时目录、小文件与模拟服务，不等于真实完整 ComfyUI 冷安装。暂停、取消、失败、重试及恢复通过本地任务记录写入、对象重建和冻结计划回读验证，尚未执行真实大模型下载中断后的整套恢复。

节点测试使用真实本地 Git 仓库与文件备份/回退；依赖安装与 Torch 接口使用测试回调。已验证不含 Sesqui 的更新范围不准备其权重，不附带 Triton；尚未运行真实插件升级后的 GPU 推理。

### 可复现的针对性检查

在 Windows 上准备 .NET 8 SDK、Git 和 Python 3.11+ x64，并按仓库开发说明安装 Python 测试依赖。从仓库根目录可运行：

```powershell
dotnet build .\launcher\Anima.Launcher.Tests -c Release
if ($LASTEXITCODE -ne 0) { throw 'Launcher test build failed.' }
foreach ($group in @('--onboarding-only', '--runtime-dependencies-only', '--setup-import-only', '--resource-policy-only', '--feature-preparation-only', '--node-updates-only')) {
    dotnet run --no-build -c Release --project .\launcher\Anima.Launcher.Tests -- $group
    if ($LASTEXITCODE -ne 0) { throw "Launcher test group failed: $group" }
}
python -m pytest tests/test_launcher_dependency_probe.py tests/test_resource_resolution.py
```

这里的 Python 命令是与本轮修改直接相关的最小重跑入口，不是上表历史 74 项回归的完整命令。重新运行的测试数以所用提交为准；这些命令不能代替原生界面、安装器或 GPU 验收。

如需准备界面状态，可使用显式选择的测试夹具命令；下面只生成一个新的隔离目录，不启动候选或服务：

```powershell
$fixture = Join-Path (Get-Location) ('output\onboarding-ui-' + [Guid]::NewGuid().ToString('N'))
dotnet run --no-build -c Release --project .\launcher\Anima.Launcher.Tests -- --onboarding-ui-fixture $fixture configuration-issues
```

夹具使用仓库虚拟环境或 PATH 中可用的 Python；其 ComfyUI 入口故意报错，不应将该目录用于实际依赖安装或生图。`assistant-skipped`、`assistant-configured`、`selection-review` 可替换末尾场景名，这些是预设显示状态，不能作为相应服务已运行的证据。

### 真实独立 Python 环境

在 `<隔离验收目录>/.venv` 创建空 venv，初始无第三方包、无 pip、无 packaging。真实探针报告 3 项工作台直接依赖缺失，经宿主安装工具定向安装 11 个包后，探针问题为 0，实际模块导入通过；目标 venv 仍不包含 pip 或 packaging。

证据保存在 `output/launcher-onboarding-dependencies-20261115/`：`result.json`、`before-probe.json`、`after-probe.json`、`install.log`、`imports.json` 和安装前后 metadata。此项只覆盖工作台 Python 依赖，没有对日常 ComfyUI、插件或模型安装/升级。初次测试受到当前目录中的 egg-info 干扰，随后用隔离解释器模式重跑；以修正后的记录为准。

### 原生 WinUI

使用真实候选 EXE、独立 `ANIMA_LAUNCHER_HOME` 和故意不能启动的 ComfyUI 测试目录。截图与 UI Automation 文本位于 `output/launcher-onboarding-gui-20261115/`。这些是界面与恢复状态验收，不是完整 GPU 环境验收。

- 第一版候选确认深色/浅色、约 800 × 594 小窗口滚动与固定底部按钮、Tab 焦点环及 Enter 展开详情；连续勾选 Anima、SeedVR2、AnimeSharp 后不自动检查，主按钮变为“检查所选内容”。切到日志再返回、重开后，配置缺项仍可见；助手跳过显示“稍后配置”。证据 `01` 至 `07`。
- 界面验收发现恢复导入时，启动校验仍退回默认三件套。已将完成页与启动检查统一到冻结计划的 `RequiredDownloads`，增加回归后再构建第二版候选。第二版不再报错误的“必装模型未全部通过”，而是到达测试目录预设的 ComfyUI 启动失败；旧配置问题保留，并显示“重试启动”。证据 `08-final-completion`、`09-final-import-recheck` 及 `final-completion/logs/ComfyUI.log`。
- 第二版重开未确认的功能选择显示“上次选择尚未确认”，不会误称环境文件改变。复查又发现共享 Python 的复合异常仍在摘要中泄漏长路径，第三版改为明确的两种处理方式，原始诊断完整保留在详情。`10-final-selection-resume`、`11-final-review-details-before` 为前一版记录；`12-final-review-summary` 与 `13-final-review-details` 为最终候选的检查终态。

界面测试没有安装所勾选的大模型，也没有执行供应商请求。助手已配置分支由状态回归覆盖；真实安装过程中的暂停恢复仍是上述自动回归证据，不能记成原生 GUI 实际大文件下载验收。

### 首轮单 EXE 候选与安装器

首轮最终候选目录：`dist/candidates/onboarding-resource-20261115-3/`，版本仍为 `1.6.2`。使用 `Build-Launcher.ps1 -CandidateDirectory ...` 构建，未使用安装日常入口或快捷方式的选项；`Test-LauncherCandidate` 验证独立 EXE、只含 EXE 的便携 ZIP、Setup 校验和及内嵌资源全部通过。内嵌 652 个文件，包含工作台、运行时、下载工具及许可文件。下方记录保留首轮证据；后续公开提交复审的候选见文末。

| 文件 | 大小（字节） | SHA-256 |
| --- | ---: | --- |
| `AnimaRandomStudio.exe` | 126,592,427 | `cba15f6e8c21cb8d719511c87f4a7e8b5934338659cb3d85d1753a0931ff5e8d` |
| `AnimaRandomStudio-portable-win-x64.zip` | 119,853,085 | `d9b59d53988c26bfecb9df5423b6513c9b3c822f5c4aa994dcdbcb4ba493842e` |
| `AnimaRandomStudio-Setup-win-x64.exe` | 119,710,607 | `443a72e6140b373d2373e878683c83c6a28349a15296ae947171fb167ab2b644` |

来源 HEAD 为 `754664ee5bd512dca91ec262c79f025fa418885a`，`sourceDirty=true`。候选包含当前工作区已有的其他改动，并非只包含本轮启动器修改；原有改动已保留。第一、二版候选留作验收证据，不作为最终交付。最终候选的默认 `.preview-state` 未用于测试，GUI 配置存放在 `output` 下。

如需试用已构建候选，从仓库根目录运行下列命令。首次使用会创建候选目录内的 `.preview-state`，后续运行会复用该隔离配置：

```powershell
pwsh -NoProfile -File .\launcher\scripts\Start-IsolatedCandidate.ps1 -CandidateDirectory .\dist\candidates\onboarding-resource-20261115-3
```

最终候选的真实安装器以独立 AppId 在 `<隔离验收目录>/installer-final3` 验证 10 项：状态探针、已有新版与待更新阻断、每用户安装 EXE 字节一致、运行中保护、更新中安装/卸载保护、降级拒绝、同版本修复、仅迁移归属明确的测试快捷方式、卸载保留用户数据。结果全部通过；证据 `output/launcher-onboarding-installer-final3.log`。测试没有替换日常启动入口。

### 未完成的实机范围

本轮隔离 GPU 服务启动被自动审批拒绝，仅返回 `blocked by policy`，未提供具体原因。没有绕过重试、生成新图片或留下隔离 GPU 进程；历史 GPU 验收不计为本轮重新通过。

完整新建/导入环境到首次生图闭环、全新 Windows 冷安装、真实旧 Python/x86、真实网络中断后整套安装恢复、其他显卡及全部选装功能仍未实机验收。当前结论是实现、相关回归、独立依赖安装、原生界面和单 EXE/安装器检查通过，不声明全功能 GPU 验收完成。

## 后续：公开 Git 提交前复审

再次追踪恢复调用链，修复了以下问题：

- 修改目录、Python 或来源后，新检查结果不等于安装确认；退出重开仍需确认。明确从下载列表恢复原冻结任务时，撤销其他草稿的待确认标记，避免已完成任务重开后反复回到安装位置页。
- 改变实际安装目标时重置助手状态；同环境修改来源、Python 或等价路径继续保留状态，避免新环境错误显示旧环境的“已配置”。
- SAM2 声明的行尾注释在检查与实际安装准备中按同一规则处理；未知来源、URL 片段与错误安装提交仍不会被认可。
- 后装功能从当前环境已确认的冻结任务恢复选择，完成、暂停和失败后不会变成“未选可选项”；取消、待审核、损坏或异环境记录不会扩大检查范围。
- 节点更新使用已保存的 SAM2 提交、核心提交和依赖约束；缺少冻结 SAM2 的旧记录先重新预检，并保留已选节点目标提交。

公开报告移除真实用户目录与无关本机库存明细，完整原始报告留在被忽略的 `output/`。内部验收文档设置 `export-ignore`，不会进入源码发行压缩包；这不隐藏 GitHub 仓库中的文件，因此新增内容仍需匿名化。构建必需的 `packaging-26.0.zip` 仅通过名称和固定 SHA-256 双重校验后进入源码包，其他生成压缩包继续被拒绝；公开说明链接改为仓库链接以避免源码包内的断链。

复审验证：

| 范围 | 结果 | 本地证据 |
| --- | --- | --- |
| 当前工作区完整质量门禁 | Python 2359 项、826 子测试通过，63 项按环境条件跳过；Node 930 项通过；许可/内容范围、语法、Ruff 和两组补丁测试通过 | `output/launcher-public-review-quality.log` |
| 最新 .NET 向导 | 346 项通过 | `output/launcher-public-review-onboarding.log` |
| 最新 .NET 资源与恢复策略 | 188 项通过 | `output/launcher-public-review-resource-policy.log` |
| 最新 .NET 依赖合同 | 104 项通过 | `output/launcher-public-review-dependencies.log` |
| 最新 .NET 节点更新事务 | 96 项通过，覆盖更新、依赖预检、失败恢复和回滚顺序 | `output/launcher-public-review-node-transactions.log` |
| 全部待提交内容导出的源码归档 | 内容门禁、语法和 Ruff 通过；针对性 Python 64 项通过 | `output/launcher-public-review-all-source-gates.log`、`output/launcher-public-review-all-source-tests.log` |

完整工作区测试覆盖启动器与一并提交的作品库筛选改动；64 项源码归档回归专门核对启动器依赖、资源解析和发布内容，不能替代全部功能测试。本地测试不等于 GitHub CI 已通过。各 .NET 分组仍有重叠，不合并成去重总数。无仓库 `.venv`、PATH 首项是 WindowsApps 别名的显示夹具创建已验证，能继续寻找后续真实 Python。

复审候选为 `dist/candidates/onboarding-public-review-20261115-2/`，单 EXE、便携 ZIP、Setup 和内嵌文件校验通过。EXE SHA-256 为 `8d4381fbee4dc0f51b063927b3873a95feaf144e7dcea96515e4f5c5bf9bb5cb`，构建日志为 `output/launcher-public-review-candidate-final.log`。该候选仍从有其他未提交改动的工作区构建，未安装到日常入口，也未作为正式发行发布。本次复审没有重复原生 GUI、真实完整安装或 GPU 验收，前述未覆盖范围继续适用。
