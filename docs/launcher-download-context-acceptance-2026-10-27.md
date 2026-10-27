# 启动器下载入口与原页面操作验收（2026-10-27）

## 交付状态

本轮将下载管理融入模型、安装与软件更新页面，完整下载列表作为二级页面保留。现有下载身份、校验、断点、模型包发布和安装协调器继续负责实际任务。顶栏不再单列“下载”，模型页启动或重试下载时留在原页。

本轮 **471 项定向检查通过**，WinUI Release 候选包及 EXE、ZIP、安装包完整性检查通过。原生验收发现的滚动恢复与退出时探测进程清理问题均已修复，并在最终 EXE 中复验。

| 最终产物身份 | 结果 |
| --- | --- |
| 候选可执行文件 | `dist/candidates/download-context-final-20261027/AnimaRandomStudio.exe` |
| 构建配置、版本与构建结果 | Release / win-x64 / 1.3.1；单 EXE、便携 ZIP 与安装包均通过校验，655 个内嵌文件已验证；[构建日志](launcher-download-context-evidence-2026-10-27/release-build-final.log) |
| EXE SHA-256 | `2bce9522bb4f2f94358a633274eff2c4eca3f64f1245cf891542ec0a07c2e680` |
| 源码状态 | 构建时基于 `090af0d91d91780ceb3e7d0985eebec89eb212d2` 的未提交工作区；`sourceDirty=true`，未发布 GitHub |
| 最终补充检查数与日志 | 下载任务组由 183 增至 188，五组共 471；[最终补充测试日志](launcher-download-context-evidence-2026-10-27/coverage-gaps-retry.log) |

## 功能与兼容行为

- **模型资源**：模型行展示当前进度、速度、错误及暂停／继续／重试。单模型操作只处理该资源；模型包按完整包恢复，不能用某个文件完成代替整包完成。旧多模型批次可拆为各模型的恢复入口，保留既有文件身份和固定来源。
- **完整下载列表**：模型页“查看全部下载”、行详情、安装／更新详情及全局页脚更多菜单均可进入。详情按来源定位相关任务，并提供返回原页面入口；完整列表继续提供换源、取消、清理、兼容模式等恢复操作。
- **环境与向导**：环境页显示当前环境的安装批次状态和恢复操作；暂停安装会停止完整协调器。向导查看详情后返回原步骤。已传完文件不代表安装、校验或模型包发布已经完成。
- **软件更新**：设置页恢复持久任务的进度、原版本及错误，并可继续原任务。恢复不会覆盖已经准备好的更新候选；用户检查得到更高新版本或同版本的新哈希时，也不会被旧暂停任务覆盖。
- **重启与取消**：重新打开启动器只恢复显示，等待用户继续。取消保留文件；删除未完成文件仍是单独操作。旧记录和不同环境的任务仍可从完整列表访问。

## 自动检查证据

以下五组分别通过，共 **471 checks**。日志均位于 `output/launcher-tests/0c55325f37cd4130b5c29aff2937a2a1`，不是整套项目测试全量通过的声明。

| 检查组 | 结果 | 日志 |
| --- | ---: | --- |
| 下载任务、模型展示与上下文恢复 | 188 | [coverage-gaps-retry.log](launcher-download-context-evidence-2026-10-27/coverage-gaps-retry.log) |
| 模型包 | 39 | [model-packages-final.log](launcher-download-context-evidence-2026-10-27/model-packages-final.log) |
| 环境事务与并发锁 | 6 | [environment-lock-final.log](launcher-download-context-evidence-2026-10-27/environment-lock-final.log) |
| 安装向导 | 155 | [onboarding-final.log](launcher-download-context-evidence-2026-10-27/onboarding-final.log) |
| 启动器更新 | 83 | [updates-final.log](launcher-download-context-evidence-2026-10-27/updates-final.log) |

补充测试验证磁盘预算不足时持久记录为 Failed、可重试、未发网络请求或写入模型数据；预算恢复后沿用同一 owner 成功。迁移测试覆盖提交失败、残缺临时文件、新服务重启、重新拆分后任务与断点不丢失及不重复；故障通过注入模拟，没有实际杀进程。

首次补跑的 [coverage-gaps-final.log](launcher-download-context-evidence-2026-10-27/coverage-gaps-final.log) 在原有 `TaskServiceTests.cs:179` 超时：该测试的持久错误为 `Access to the path is denied`，尚未执行到新增测试。未改代码再次执行全部 188 项通过；具体 OS 拒绝原因未复现，两次日志均保留。

## 原生窗口验收

本轮使用独立 `ANIMA_LAUNCHER_HOME`。最终窗口对应 `output/launcher-download-context-ui-20261027/state-final`；安装和软件恢复的早期截图来自 `state-r2`。自动启动与自动检查更新关闭，显式下载使用 `http://127.0.0.1:9` 本地拒绝代理。夹具只提供真实 Python 可读取的最小路径清单，不是可运行的 ComfyUI。

| 场景 | 已观察结果 | 证据 |
| --- | --- | --- |
| 模型原页操作 | 点击重试仍停留模型资源页；Qwen 下载可暂停。旧多模型批次中的 Anima 重试只运行对应子任务，随后可暂停。 | [浅色模型页](screenshots/download-context-2026-10-27/models-light.jpg)、隔离状态 `state-final/download-tasks.json` |
| 重启恢复 | 重启同一独立状态后相关模型仍显示继续入口，没有自动启动下载。 | 隔离状态 `state-final` 与本轮原生交互记录 |
| 模型详情及选择 | 从模型进入详情能看到相关任务，返回模型页后选择保留。 | [模型详情](screenshots/download-context-2026-10-27/model-details-dark.jpg)、[返回选择](screenshots/download-context-2026-10-27/model-return-selection.jpg) |
| 全部任务与空列表 | 完整任务列表可从二级入口访问；没有任务时仍能从页脚更多菜单打开空列表。 | [全部下载](screenshots/download-context-2026-10-27/all-downloads-light.jpg)、[空列表](screenshots/download-context-2026-10-27/empty-downloads-light.jpg) |
| 安装与更新恢复 | `state-r2` 中原页面恢复约 25% 的夹具进度，并展示继续及详情入口。数字是持久展示数据。 | [安装恢复](screenshots/download-context-2026-10-27/installation-restored.jpg)、[更新恢复](screenshots/download-context-2026-10-27/software-restored.jpg) |
| 向导往返 | 从 Location 步骤进入下载详情，返回后仍为 Location，表单内容相同。 | [向导详情](screenshots/download-context-2026-10-27/wizard-download-details.jpg)、[返回位置页](screenshots/download-context-2026-10-27/wizard-return-location.jpg) |
| 深浅色、小窗口与长错误 | 浅色页面和约 748×814 的深色长错误场景已查看；Tab 键操作显示可见焦点。 | [小窗口长错误](screenshots/download-context-2026-10-27/models-narrow-long-error.jpg)、[键盘焦点](screenshots/download-context-2026-10-27/wizard-keyboard-focus.jpg) |
| 返回滚动位置 | 最终候选复测通过：页面滚动至 Qwen 与 Anima 行，勾选 Qwen，进入详情后返回；两行保持原位置，Qwen 仍勾选。 | [进入详情前](screenshots/download-context-2026-10-27/final-before-details.jpg)、[返回详情后](screenshots/download-context-2026-10-27/final-after-details.jpg) |
| 扫描期间退出 | 最终候选在隔离路径模块注入 25 秒延迟。扫描中读取到两个 Python 进程（41832、32100）；10 秒内通过页脚退出，启动器及两个探测进程均已退出。 | [退出前进程](launcher-download-context-evidence-2026-10-27/probe-before-exit.json)、[退出后进程](launcher-download-context-evidence-2026-10-27/probe-after-exit.json) |

## 验证界限与复现

本轮没有下载真实模型字节，没有完成整包权重校验、真实环境安装、更新应用或 GPU 生图。夹具中的 `Received` 字节用于展示恢复进度，不代表磁盘已有可续传模型；本地拒绝代理下的运行／失败／暂停验证也不代表外网下载速度或来源可用性。已有核心测试覆盖下载与发布规则，不能替代这些真实端到端场景。

使用本轮编译的测试程序生成一个新的空状态目录：

```powershell
dotnet run -c Debug --project launcher/Anima.Launcher.Tests --no-build -- --seed-download-ui <新的空状态目录>
# 加 --long-error 生成长错误展示场景。
```

该入口生成单模型、旧多模型批次、模型包、软件更新与合法固定安装计划五类暂停记录，`fixture-index.json` 保存环境路径、真实 Python、模型身份和 owner ID。启动候选进程时仅为该子进程设置 `ANIMA_LAUNCHER_HOME`，并使用 `--show`。早期 `state` 使用旧夹具，不能作为本轮扩展场景；`state-r2` 适用于已记录的恢复展示，最终交互以 `state-final` 为准。

隔离验收结束时，测试窗口和探测进程均已退出。随后按用户要求，通过“退出（保留服务）”正常关闭旧启动器，备份原文件后替换仓库根目录入口并重新启动。新入口 SHA-256 与本表候选一致；原文件备份 SHA-256 为 `94a30bb46435a89ee0475637b7bb96e9641e978c733f0e66af9ad654e993e0fe`。原生窗口已确认顶栏没有“下载”，ComfyUI 与 Studio 服务仍显示已就绪；直接请求 8188/system_stats 与 8190/ 均返回 HTTP 200。[替换校验日志](launcher-download-context-evidence-2026-10-27/install-entry.log)。本记录不表示已发布到 GitHub。
