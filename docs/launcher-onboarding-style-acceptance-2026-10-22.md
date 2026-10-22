# 启动器引导与样式验收（2026-10-22）

## 状态与范围

本轮交付候选为 **r3**，已构建并通过 516 文件的 ZIP 完整性与来源检查，最终包的原生重启、模型预览、下载进度、暂停、扫描失败、初次配置、安装恢复及危险确认弹窗已验证；测试进程已全部退出。r1 保留为早期行为与样式证据；**r2 存在严重的对话框运行错误，禁止交付**。下文区分各构建来源与未覆盖场景，不把早期截图自动视为最终包的实测结果。

本轮目标是保留五步安装向导，改善首次安装与已有环境的说明、基础模型补齐和失败恢复，并统一整个 WinUI 启动器的控件、提示、弹窗与深浅色样式。网页端同时存在其他修改，本轮记录不将这些改动归因于启动器任务。

## 已确认的基线

| 项目 | 结果与限制 |
| --- | --- |
| 修改前 WinUI 构建 | `dotnet build launcher/Anima.Launcher.WinUI/Anima.Launcher.WinUI.csproj -c Release --no-restore`，22.51 秒，0 警告、0 错误；SDK 8.0.425。该结果不是修改后的验收结果。 |
| 修改前向导检查 | 主任务运行 `--onboarding-only`，95 checks 通过；修改后需要按实际逻辑变化复核。 |
| 原生工具 | `node_repl` 中 `@oai/sky` 初始化与 `sky.list_windows()` 成功；这是准备阶段检查，后续原生结果另列于下方。 |
| 隔离夹具 | 已生成 `output/launcher-onboarding-style-acceptance-20261022/run-20261022T104717Z-0b8ee8c8`，共 10 个状态。真实 `probe_comfy.py` 在缺模型夹具中成功返回，在扫描失败夹具中按预期失败；启动脚本语法检查通过。准备阶段未启动 UI，后续使用这些隔离状态执行原生验收。 |

## 隔离环境与执行方法

1. 在 `output/launcher-onboarding-style-acceptance-20261022` 运行 `Prepare-Fixtures.ps1`；脚本只在新的 `run-*` 子目录生成测试状态与 ComfyUI 路径夹具，不读取或复制真实启动器配置。
2. 从生成的 `fixture-index.json` 选择场景，通过 `Start-IsolatedLauncher.ps1 -LauncherPath <本轮EXE路径> -StateDirectory <场景state目录>` 打开。脚本仅为子进程设置 `ANIMA_LAUNCHER_HOME`，传入 `--show`，所有场景禁用自动启动服务。
3. 用 Computer Use 的 `@oai/sky` 列举并唯一定位启动器窗口；每次交互前读取最新截图或 UIA 控件树，交互后重新观察。不要复用旧控件索引或截图坐标。
4. 页面样式和基础模型预览可在夹具中验证；只有明确要验证下载状态时才触发实际下载。夹具不是可用的 ComfyUI，`main.py` 会主动报错退出，用于验证启动失败恢复。
5. 保存实测状态、截图引用及相关日志位置，记录所用 EXE 路径、SHA-256、版本、候选 `build-info.json`。退出后确认本次测试进程已结束；窗口关闭到托盘不算进程退出。

准备的场景：`fresh`（首次打开）、`configured`（普通页面）、`complete-missing`（完成但缺模型）、`complete-scan-failure`（模型探测失败）、`pending`（上次安装未完成）。每种场景各有浅色与深色状态，实际路径以 `fixture-index.json` 为准。

## 行为与视觉验收

| 场景 | 通过标准 | 本轮结果 |
| --- | --- | --- |
| 欢迎与方式选择 | 首次安装、连接已有 ComfyUI 的用途明确；系统检测与重新检测有状态反馈；两种方式选中样式一致。 | r1 已检查深色小窗口欢迎页；r3 浅色欢迎页重新检测出现「正在检测」禁用态，随后恢复 RTX 4060 Laptop / 610.74 的检测结果。 |
| 位置与检查 | 输入标签清楚；高级选项可收起；回退不丢草稿；无效路径给出可执行提示；修改路径或来源后旧检查不再授权安装。 | r1 深色小窗口导入位置页已检查。r3 真实空间预检报告 F 盘可用 11.7 GiB、需预留 26.5 GiB，已知下载 7.9 GiB；问题与「更换安装位置」位于安装摘要前。点击更换位置返回 Location，草稿保留且未覆盖生效环境。无效路径和来源修改后的检查失效未逐项原生复测。 |
| 安装与恢复 | 阶段、当前状态和下一步清楚；暂停、恢复、失败、重试有正确按钮状态；日志返回原步骤。 | r3 未完成安装夹具恢复到 Installing，显示「等待继续安装」「检查并继续」「更改目录或来源」，没有自动开始安装；本轮未运行整套环境安装。完成页的模型暂停、恢复及错误重试另列于下方。 |
| 完成且缺模型 | 主操作为「准备基础模型」；预览说明文件、大小、保存位置；确认后才下载，并留在向导中。 | 原生实测预览显示 3 项、剩余 5.2 GiB，并列出来源与实际位置；查看日志后返回保留预览。随后在该隔离夹具内明确点击「开始下载」，官方 Hugging Face 来源实际开始传输。 |
| 模型补齐 | 暂停、继续下载、重试下载可用；补齐后重新扫描；重启完成页后重新确认缺项，不能直接恢复旧下载授权。 | r1 已实测真实下载、暂停续传与异常文件拦截。r3 重启同一状态后自动复扫、不自动下载，重新确认后只下载 2 项可补齐资源；再次真实下载和暂停通过，仍在 Complete。下载未完成全部 5.2 GiB，全部补齐成功后的最终扫描未实测。 |
| 完成与启动失败 | 齐备时「打开工作台」；缺项或扫描异常可「先打开工作台」；「稍后打开」保留为退出入口；启动失败不退出向导且可重试。 | 原生实测点击「先打开工作台」后，故意退出的 ComfyUI 夹具触发错误，仍留在完成页且主按钮为「重试启动」。状态核对通过，详见下文；实际服务成功启动未验证。 |
| 全页样式 | 概览、环境、模型、日志、设置与向导的字体、间距、圆角、颜色及层级一致；所有原生下拉、提示条、扩展区和对话框融入主题。 | r1 在系统深色下强制应用浅色，概览、模型、菜单、设置显示正常；日志、助手与导入位置页的小窗口内容已检查。修复后 Release 构建的主题下拉选中指示条为蓝色；r3 下载进度在预览上方可直接看到，暂停重复提示已去除。 |
| 主题与状态 | 浅色、深色、跟随系统切换后当前页面和新开弹窗一致；聚焦、悬停、选中、禁用及错误状态可区分。 | 系统 `AppsUseLightTheme=0` 时应用浅色仍正确；r1 应用浅色→深色即时切换通过，助手错误文字随主题保持可读。r3 浅色扫描失败页使用简洁提示且不显示成功。跟随系统模式及全部交互状态的覆盖未完整验证。 |
| 小窗口与键盘 | 默认 1160×820 与最小 760×600 逻辑尺寸下无关键内容裁切；长内容可滚动；底部操作可达；Tab 顺序与当前步骤语义合理。 | r1 小窗口截图为 754×594，外框最小尺寸为 760×600；模型、日志、助手内容可滚动且底部操作固定，Esc 可关闭菜单与确认弹窗。修复后 Release 深色及最终 r3 浅色危险确认弹窗仅取消按钮为蓝色并有焦点环，Enter 实测取消且未写入停止日志；完整 Tab 顺序未验证。 |

构建、逻辑测试、原生界面和候选包应分别记录结果，不用其中一项替代其他项。真实联网下载、实际服务启动或生图未运行时，应在最终结果中明确保留为未验证。

原生行为证据为 `output/launcher-onboarding-style-acceptance-20261022/start-failure-tree.txt`，其中包含模型预览、错误提示、完成步骤与「重试启动」按钮。`start-failure-dark.png` 来自中间构建，只保留作行为检查记录，不作为最终样式验收图。

启动失败后的 `complete-missing-dark/launcher.json` 仍为 `setupComplete=false`、`installPending=false`、`startOnLaunch=false`；`onboarding.json` 仍为 `step=4`（Complete）、`installStarted=true`、`installFinished=true`，且草稿与生效配置的 ComfyUI 目标一致。没有错误地标记工作台启动成功，也没有回到安装步骤。

真实下载与故障验收使用 r1 的同一 `complete-missing-dark` 状态，所有文件均位于隔离夹具 `fixtures/ComfyUI/models`：

1. 确认预览后从官方 Hugging Face 开始下载 Anima 底模，`.part` 从 0 增至 21,517,303 字节。点击暂停后，页面显示「模型下载已暂停」，主按钮为「继续下载」。
2. 点击继续，实测 UI 进度达到 `26.2 / 3988.5 MiB`，随后再次暂停。复核时 `.part` 为 36,758,047 字节（约 35.1 MiB），证明已保留并继续使用下载内容。
3. 在夹具 `text_encoders/qwen_3_06b_base.safetensors` 写入故意异常的内容 `acceptance-invalid-model-preserve-me`。实盘文件为 **36 字节**，SHA-256 为 `BA34CCE0CDFF242026E6DB67E369200FFD7F7B55F8FE9C9924E3DA2A1BEABD67`。
4. 再次继续时，启动器立即提示「已有文件校验异常，已保留原文件……请备份处理后重试」，主按钮切换为「重试下载」，当前步骤仍为 Complete。操作前后异常文件 SHA-256 相同，没有覆盖或删除它。

这证明了部分真实传输、暂停续传与异常文件保护，未验证全部 5.2 GiB 下载完成、完整环境安装或生图。

r3 使用最终候选目录内的 EXE 启动，原生进程 PID 35852，随后完成以下补验：

1. 重启同一 `complete-missing-dark` 状态后自动复扫，页面显示「还有模型需要处理」，没有自动恢复下载；此时 `.part` 仍为 36,758,047 字节，记录保持 `step=4`（Complete）、`installFinished=true`。
2. 重新检查后，预览仅列 **2 项可下载、剩余 4.1 GiB**；异常文本编码器明确提示保留，并提供「打开文件夹处理」。主按钮为「下载可补齐模型」。
3. 明确点击后继续官方来源传输，进度显示在预览上方，无需滚动可见。UIA 树记录 `140.2 / 3,988.5 MiB · 21.0 MiB/s`，截图瞬时值为 `148.8 / 3,988.5 MiB`，两者为不同采样时刻。
4. 点击暂停后只有一处「模型下载已暂停」标题，主按钮为「继续下载」，底部操作固定。此次暂停后的 `.part` 为 **223,018,124 字节**；36 字节异常编码器的 SHA-256 仍为 `BA34CCE0CDFF242026E6DB67E369200FFD7F7B55F8FE9C9924E3DA2A1BEABD67`。
5. r3 浅色扫描失败夹具显示「基础模型检查未完成」及检查目录、Python、日志的简洁提示，主操作为「重新检查模型」，没有误报模型齐备；完整 traceback 仅在日志中。

r3 另验证了首次配置和未完成安装恢复：

1. 浅色欢迎页点击「重新检测」后出现「正在检测」禁用态，完成后恢复检测结果 `NVIDIA GeForce RTX 4060 Laptop GPU, 610.74`。
2. 新建安装的真实空间预检发现 F 盘当前可用 **11.7 GiB**，低于需预留的 **26.5 GiB**；已知剩余下载为 **7.9 GiB**。问题与「更换安装位置」位于安装摘要前，不能开始安装。可用空间为该次采样值，不代表当前磁盘剩余空间。
3. 点击「更换安装位置」返回 Location 后，草稿仍指向隔离目录 `output/launcher-onboarding-style-acceptance-20261022/r3-install-check-only`。`launcher.json` 中生效的 `comfyRoot`、`installRoot` 仍为空，`setupComplete=false`；`onboarding.json` 保持草稿安装路径，`step=1`，未提前覆盖生效配置。
4. 未完成安装夹具显示 Installing 步骤、「等待继续安装」以及「检查并继续」「更改目录或来源」，没有自动安装。本轮只验证恢复入口和等待确认状态。

最终 r3 还以 `configured-light` 状态（PID 47052）打开「停止本程序服务」确认弹窗。系统仍为深色，应用和弹窗均为浅色；取消按钮为唯一蓝色按钮并有焦点环。按 Enter 后弹窗关闭并回到概览，服务状态未变，日志没有停止动作或异常。UIA 的 focused 字段仍滞后报告正文，焦点结论依据截图中的焦点环与 Enter 的实际行为。日志记录 19:32:55 的 Grid 及 19:33:43 的 ContentDialog 均应用了 142 项主题画刷。

以下图片均位于 `output/launcher-onboarding-style-acceptance-20261022/`。第一组来自 r1；文件名中的 `final-` 只是拍摄时使用的名称，实际来源以本表为准。

| r1 图片 / 状态证据 | 实际内容与用途 |
| --- | --- |
| `final-overview-light.png` | 系统深色、应用浅色的概览。 |
| `final-models-light.png`、`final-models-small-light.png` | 应用浅色模型页，以及小窗口下的滚动布局。 |
| `final-logs-small-dark.png` | 深色小窗口日志页。 |
| `final-assistant-small-light.png`、`final-assistant-small-dark.png` | 助手小窗口的深浅色切换与错误文字可读性。 |
| `final-danger-dialog-dark.png`、`final-danger-dialog-tree.txt` | r1 危险确认弹窗；初始正文焦点问题留作修复前记录，不能证明默认取消焦点已通过。 |
| `final-import-small-dark.png` | 已用真正的深色小窗口导入位置页覆盖，作为导入页面证据；先前误放的欢迎页图不再引用。 |
| `intermediate-model-paused-dark.png`、`model-paused-tree.txt` | 真实下载暂停后的完成页、保留文件提示及「继续下载」主按钮；是 r1 进度布局调整前的记录。 |
| `final-model-retry-dark.png`、`model-retry-tree.txt` | 异常模型文件拦截提示、「重试下载」主按钮与保持 Complete 的步骤状态。 |

| 后续修复与最终包证据 | 来源与实际内容 |
| --- | --- |
| `final-danger-cancel-focus-dark.png` | r3 打包前修复后的 Release 构建；通过类型明确的 Button 属性复制保留原生默认按钮样式，取消按钮为唯一蓝色按钮并有焦点环，Enter 实测取消。 |
| `final-theme-popup-dark.png` | r3 打包前修复后的 Release 构建；主题下拉选中指示条实测为蓝色。 |
| `r3-restart-mixed-dark.png`、`r3-restart-mixed-tree.txt` | r3 最终候选重启后重新扫描，保留 Complete 且不自动恢复下载。 |
| `r3-safe-model-preview-dark.png`、`r3-safe-model-preview-tree.txt` | r3 预览仅下载 2 项安全资源，明确保留异常编码器并提供处理入口。 |
| `r3-model-progress-dark.png`、`r3-model-progress-tree.txt` | r3 真实下载进度位于预览上方，文件进度、传输量和速度可直接看到。 |
| `r3-model-paused-dark.png`、`r3-model-paused-tree.txt` | r3 暂停后提示去重、主操作为继续下载，底部操作保留。 |
| `r3-scan-failure-light.png`、`r3-scan-failure-tree.txt` | r3 浅色模型扫描失败页的简洁提示；完整 traceback 留在日志，不显示模型成功状态。 |
| `r3-welcome-light.png`、`r3-welcome-tree.txt` | r3 浅色欢迎页，两种开始方式的说明与重新检测后的硬件结果。 |
| `r3-preflight-space-light.png`、`r3-preflight-space-tree.txt` | r3 真实磁盘空间不足时阻止安装，优先显示问题及更换位置入口。 |
| `r3-install-recovery-dark.png`、`r3-install-recovery-tree.txt` | r3 深色未完成安装恢复页，保留 Installing 步骤，等待重新检查与确认。 |
| `r3-danger-cancel-light.png`、`r3-danger-cancel-tree.txt` | 最终 r3 在系统深色、应用浅色下的危险确认弹窗；取消按钮焦点环与 Enter 取消行为通过。UIA focused 字段有滞后，不能单独用于判定焦点。 |

未实测高 DPI、操作系统高对比模式及关闭透明效果时的表现。

## 候选包与最终记录

最终 r3 的构建与候选检查已执行。以下是所用命令形式；复跑时必须换一个新目录，不能覆盖现有候选：

```powershell
$candidateCommit = (git rev-parse HEAD).Trim()
$candidateVersion = (Get-Content launcher/version.txt -Raw).Trim()
pwsh ./Build-Launcher.ps1 -CandidateDirectory './dist/candidates/launcher-onboarding-style-20261022-r3'
pwsh ./launcher/scripts/Test-LauncherCandidate.ps1 -CandidateDirectory './dist/candidates/launcher-onboarding-style-20261022-r3' -ExpectedCommit $candidateCommit -ExpectedVersion $candidateVersion
```

本轮开发候选省略 `-RequireClean`、`-InstallRootEntry`、`-InstallShortcut` 和签名参数。根目录入口、`.launcher` 及桌面快捷方式不会被替换。`Build-Launcher.ps1` 会重建忽略跟踪的 `launcher/payload.zip`，因此候选工作台负载包含构建时其他已存在的网页修改；以 `sourceDirty`、`sourceChanges` 和 `payloadSha256` 记录事实。

r1 目录 `dist/candidates/launcher-onboarding-style-20261022-r1` 曾通过 516 文件检查，ZIP SHA-256 为 `1a754acc385eefc5ca14352e584b4199afd450162b41c8141d9b669fcac55b55`，仅保留为早期验收产物。r2 对话框错误使其不具备交付条件。**最终只交付 r3**，其信息如下。

| 最终证据 | 记录 |
| --- | --- |
| 修改后构建 / 逻辑测试 | 最新正常 Release 构建通过，9.90 秒、0 警告、0 错误；r3 候选构建成功。完整 Core 回归 `dotnet run -c Release --project launcher/Anima.Launcher.Tests --no-build`：347 checks 通过，夹具目录 `output/launcher-tests/984317b196684fcbaa98c6082d10d126`；定向向导检查为 127 checks。包含无关重复可选节点不阻断基础模型检查的回归。 |
| 包装逻辑回归 | `pwsh -NoProfile -File launcher/scripts/Test-LauncherPackage.ps1` 通过：Unicode 路径与运行文件完整性、ZIP 根目录、来源、版本不匹配、漏文件、篡改检测均通过。夹具为 `%LOCALAPPDATA%/Temp/Anima package test b07e3385062b403591cf8779278a3149`；这是包装测试夹具，不是本轮候选发布包。 |
| 原生界面场景与截图 | r1 深浅色、小窗口、启动失败、部分真实下载及故障拦截；修复后 Release 的默认取消焦点、Enter 取消与蓝色下拉指示条；最终 r3 的重启复扫、混合异常预览、真实下载进度、暂停、扫描失败、硬件重新检测、空间不足预检、未完成安装恢复及浅色危险弹窗的主题和 Enter 取消。截图来源分别列于上文。 |
| 最终候选目录与版本 | `dist/candidates/launcher-onboarding-style-20261022-r3`；版本 `1.1.0`，Windows x64 unsigned；ZIP 名称 `AnimaRandomStudio-win-x64-unsigned.zip`，89,696,481 字节（约 85.5 MiB）。 |
| 最终 EXE SHA-256 | `0F27A067CC35A50EA2E2BC459C9157CD07685D2469FF27B7FD6860C654977E82`；EXE 位于候选目录的 `AnimaRandomStudio/AnimaRandomStudio.exe`。 |
| 最终 ZIP SHA-256 | `ce6ad9558b8ff0ba83eaee5289560d6195a680b9f356cae0f9871616ff99422b`。 |
| 最终候选完整性与来源检查 | PASS：版本 1.1.0、516 文件、ZIP 校验和与构建来源验证通过；来源提交 `eeeb64a62302cd57e9eff74cb3f188e0ef17d2aa`，`sourceDirty=true`。工作台负载 SHA-256 为 `8300e6a4f3b616bf186371ca8b93dc69e421375b462a42d5e51fba0a0f0c546c`。 |
| 未覆盖场景与已知限制 | 已执行部分真实基础模型下载、暂停续传、异常文件拦截及重启复扫；未下载全部 5.2 GiB、未完成整套环境安装，亦未验证服务成功启动或生图；全部补齐成功后的最终扫描未实测。未实测高 DPI、OS 高对比、关闭透明效果；跟随系统模式与完整 Tab 顺序未完整覆盖。 |
| 测试进程清理 | PID 35852、46856、52440、23920、47052 均已停止；最后按本轮候选 EXE 路径筛选的检查结果为 `RemainingTestProcessCount=0`。 |
