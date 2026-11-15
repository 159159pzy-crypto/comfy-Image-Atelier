# 启动器依赖补齐验收（2026-11-15）

本次范围为当前工作台实际使用的功能，不迁移原始 ComfyUI 编辑器的整套交互插件，不修改工作台默认底模与开关。代码和候选包留在本地，日常启动入口、ComfyUI 依赖与模型文件没有被替换。

## 对照来源与补齐内容

已阅读 `templates/workflow_ui_v2.json` 的节点 909「必看」全文，包含截图之外的模型下载和自动下载说明。该说明列出的自动补全、工作流订阅、图像选择器属于原始编辑器功能，不是当前工作台提交图的必要节点；没有据此扩大安装范围。SilvermoonMix、FLUX.2 Klein 等新底模方向保持未引入。

| 检查项 | 结果与实现 |
| --- | --- |
| 工作台 Python 依赖 | 读取实际 AppRoot 的 `pyproject.toml`，纳入安装、导入预检、修复、缓存身份及安装后验证；元数据探针检查版本、条件、递归需求和约束，未知来源明确报告。解析工具随 EXE 携带，不要求空环境预装 pip / packaging。 |
| 现有插件 | 保留核心节点和已有安装脚本逻辑，按所选功能准备插件与运行文件；导入不能再只凭目录存在或几个模块可导入就判为完整。 |
| 像素高清 | 补齐 Remacri、AnimeSharp 固定版本、大小、SHA-256 和许可信息。 |
| SeedVR2 | 增加可选 7B Sharp INT8，保留原 3B 默认设置；两个变体共用 VAE，并识别同内容别名。 |
| 功能准备入口 | 模型页新增「一键准备完整功能」，显示所选模型、插件、运行文件、依赖问题和空间估计，确认后统一准备；保留「仅下载模型」。 |
| 实际就绪 | 对本次明确选择的模型逐一核对运行接口中的实际名称，拒绝用另一兼容模型替代后宣称该下载已就绪；所选功能的节点或运行文件失败阻止任务完成。 |
| 向导 | 新建 Anima 环境的 Sesqui 属于必需检查；没有提示词助手节点时跳过其供应商配置。 |
| 兼容性 | 已有模型任务仍只下载文件；历史计划不自动扩大资源或依赖范围；完整功能任务复用现有下载任务的暂停、恢复、错误和详情。 |

完整功能任务为全部所选插件联合解析一次依赖，并按该次确认的任务身份保存固定依赖图。暂停恢复复用同一图，安装脚本合法增加的包记入恢复记录；任务外的包变动、需求声明变动或 ComfyUI 来源身份变化会要求重新检查，不能静默重新选版本。预览中显示的依赖问题也随计划保留。

## 已完成的运行验收

工作台依赖使用隔离空虚拟环境安装，通过真实包元数据与导入检查：aiohttp 3.14.5、multidict 6.9.1、Pillow 13.0.0，实际安装 11 个包。探针没有向目标环境安装 pip 或 packaging。证据保存在 `E:\CodexAcceptance\launcher-dependencies-b55137d5190e4b72a1ef37789ccbb797` 的 `before.json`、`install.log`、`after.json` 和 `imports.json`。

新增三个模型在 RTX 4060 Laptop 8 GB 上真实推理通过。验收端口为隔离的 19888，复用已有解释器和只读模型，输入为新建 128×128 校准图。Remacri 和 AnimeSharp 输出 512×512，SeedVR2 7B 输出 256×256；实际服务执行时间分别为 1.95 秒、0.91 秒、6.41 秒。四份模型文件（含共享 VAE）完整 SHA-256 与清单一致。三个工作流通过节点契约验证，输出经过尺寸、非空和可视检查。详见 `output/gpu-model-acceptance-20261115.md`，完整证据和图片在 `E:\atelier-dependency-qa-20261115\gpu-smoke`。隔离进程已关闭，日常 8188 保持运行。

工作台相关 Python 回归通过 77 项和 33 个子测试；依赖探针及许可导出脚本通过 Ruff。测试日志位于 `output/launcher-dependency-completion-20261115/`。

完整 .NET 回归单次通过 1,666 项（`dotnet-full.log`）。之后补充的完整功能依赖检查点与具体缺项展示，在独立编译产物上直接执行测试 DLL，功能准备通过 54 项、运行依赖通过 96 项；日志在 `E:\CodexAcceptance\feature-checkpoint-direct.log` 和 `feature-checkpoint-runtime.log`。这两组为最终补充改动的定向复验，不与前述完整套件相加宣称去重总数。

最终候选 `-3` 的独立安装器测试通过首次安装、原 EXE 字节一致性、正在运行及更新中的保护、降级保护、同版本修复、专属快捷方式迁移和卸载保留数据检查。测试使用唯一 AppId 和 `E:\atelier-dependency-qa-20261115\installer-final`，没有安装或卸载日常入口；日志为 `installer-final.log`。此前 `-1` 也通过同组检查，证据保存在独立的 `installer` 目录。

原生 GUI 使用独立配置验收：候选 `-1` 通过深色、浅色、808×594 小窗口和功能选项滚动检查；候选 `-3` 重新验证完整功能清单，明确选择 3B、7B、像素高清与提示词助手后，只列出 Remacri、3B、7B 和一份共享 VAE，并显示所需插件、依赖缺项、保存位置和空间估计。确认及取消按钮可用，验收只检查预览并取消，没有点击安装。最终清单使用明确禁止启动的 GUI 专用假环境和空独立 venv，不作为真实 ComfyUI 安装成功的证据。截图及可访问性文本在 `output/launcher-dependency-completion-20261115/gui/`。

## 验收边界

空环境试验只覆盖工作台 Python 依赖；GPU 试验复用已有 Python / Torch / 模型，不是全新 Windows 或从零 ComfyUI 冷安装。7B 使用小图验证兼容性，不代表大图性能、显存余量或画质评测。可选 NVIDIA、脸部细化等既有功能没有在本轮重新执行全部 GPU 分支。

忙碌队列阻断通过单元回归；本地模拟队列服务的启动被工具自动审批拒绝，仅返回 `blocked by policy`，没有更具体原因。未重试或绕过，因此本轮未完成该状态的原生 GUI 实测。

本机日常环境中较旧的工作台依赖未被直接升级；需要修复时，新启动器会显示具体缺项并通过所选环境的修复流程处理。

## 本地候选包

最终候选目录：`dist/candidates/dependency-completion-20261115-3`，版本仍为 1.6.2，`sourceDirty=true`，未发布。此前的 `-1`、`-2` 为验收过程中的中间产物，应使用 `-3`。

| 文件 | SHA-256 |
| --- | --- |
| `AnimaRandomStudio.exe` | `d182d6b092efbe326ef7c724241b1d1bef11a261153a0cdecc6361c0304f6b60` |
| `AnimaRandomStudio-portable-win-x64.zip` | `c6a99a55ee643762092a0d690bdf29622a515cd9bd7f1cf9fe731777a3f7af48` |
| `AnimaRandomStudio-Setup-win-x64.exe` | `7c246ab3935766b3e3eec8f37330d4c624bdb13b19628e38b40fded5a5649a16` |

构建已校验独立 EXE、便携 ZIP、安装包哈希与嵌入文件。候选包包含构建时工作树中已有的其他修改，`build-info.json` 保留实际来源记录；此候选包在提交前构建，未发布，也未替换日常入口。

如需隔离试用，运行：

```powershell
pwsh -NoProfile -File F:\comfyuishengtu\launcher\scripts\Start-IsolatedCandidate.ps1 -CandidateDirectory F:\comfyuishengtu\dist\candidates\dependency-completion-20261115-3
```

该脚本使用候选目录内独立配置，避免同版本协调器转回日常入口。

## 后续变更：新手引导与资源检查

本文件保留上述候选构建时的验收事实。其后的当前下载清单已只提供 AnimeSharp；Remacri 的旧任务、磁盘文件和工作台默认选择保持兼容。后续只读检查确认启动器保存配置中的 `<ComfyUI>/.venv/Scripts/python.exe` 已有 aiohttp 3.14.5、multidict 6.9.1、Pillow 13.0.0，满足本轮工作台要求且 `pip check` 通过。验收时的日常服务进程另由全局 Python 启动，版本与保存配置不同；两个解释器的证据应分别阅读，不能将其中一个的结果归到另一个。占位符与本地原始证据的保留方式见下文链接。

六步向导统一检查、资源分类、依赖误报修正及本轮独立候选的验证范围，见 [新手引导与资源检查验收](launcher-onboarding-resource-acceptance-2026-11-15.md)。
