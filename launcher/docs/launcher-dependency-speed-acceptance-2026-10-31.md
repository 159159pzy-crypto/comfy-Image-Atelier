# 启动器 ComfyUI 依赖安装加速验收记录（2026-10-30～31）

本次依赖加速实现及约定范围内的验收已完成。**最终 e 候选的固定图长目录真实对比通过：冷依赖缓存 779.81 → 268.10 秒（2.91 倍），热依赖缓存 157.35 → 59.36 秒（2.65 倍），两组最终均为同名同版本的 162 个包。** 真实下载暂停/进程重启恢复保留三份部分文件共 17 MiB，原任务及 SHA 身份续传完成。旧版暂停重试对照、e 隔离安装器、历史失败现场接续、原完整环境功能复核和原生 e 窗口两轮继续/暂停/正常退出重开均通过。d 的较早中间成绩另行保留，不与 e 混用。这里不是全新 Windows 的完整冷安装成绩，不能外推整机安装提速。

本次修改和验收均在本地进行，不推送 GitHub，不创建 Release，不发布正式下载包。下文的候选文件是本地验证产物。

## 范围与证据位置

- 仓库：`F:\comfyuishengtu`；下文 `output/...`、`launcher/...` 均相对于此根目录。
- 真实验收独立目录：`E:\CodexDependencyTests\20261030-acceptance-9f27c8`，下文简称 `SUITE`。
- 测试入口：`launcher/Anima.Launcher.Tests/Program.cs`；真实安装入口：`launcher/Anima.Launcher.Tests/DependencyInstallAcceptance.cs`。
- 真实验收使用本机 Windows 上的新建空环境、独立 Python、独立运行时及缓存。**这不是重新安装的全新 Windows，也没有取得用户另一台新电脑的运行日志。** 本机系统代理、磁盘、驱动和网络条件仍可能与用户机器不同。
- 测试没有把现有 `F:\comfyui` 作为安装目标；该环境的 requirements 仅用于小流量来源探测。
- 自动选源只取本次 requirements 首个可识别顶层包，在每源 8 秒/2 MiB 预算内预检 wheel 可达性、独立 metadata 和 64 KiB 实际吞吐，是代表性预检，不证明全部 144 包或所有 ABI 都齐全/快速。完整依赖图由后续联合解析与缺包 PyPI 回退确认；固定文件随后再按文件名、SHA-256 和大小独立匹配来源。

## 真实空环境验收的固定输入

固定计划保存在 `SUITE\frozen-plan.json` 和 `SUITE\source-manifest.json`，每个实际运行的 `state/plan.json` 记录对应配置；不在文档复制代理凭据或整份用户配置。

| 输入 | 固定值 |
| --- | --- |
| ComfyUI | `v0.43.0` / `426f5eea52a7ece1dd599fff4cb6cd1f352ddd24` |
| Python | `3.11.9` |
| PyTorch | `2.11.0+cu128` |
| torchvision | `0.26.0+cu128` |
| torchaudio | `2.11.0+cu128` |
| SAM2 | `2b90b9f5ceec907a1c18123530e92e794ad901a4` |
| 节点 | 11 个固定提交，完整值以冻结计划的 `nodeVersions` 为准 |

`SUITE\current\state\gpu-seed-packages.json` 已记录 GPU 阶段后的真实 Python 包清单，`SUITE\current\gpu-seed-venv` 为随后固定图对比保留的环境种子。

## 最终 e 的固定图成绩

`SUITE\fixed-current-e-windows-long-root\state\fixed-cold-metrics.json` 与 `fixed-hot-metrics.json` 均为 `passed`。此次使用更长的 label，短目录源码构建已实际覆盖旧 SAM2 路径问题。分别读取旧 baseline 和 e 的 mode-specific `fixed-cold-packages.json`、`fixed-hot-packages.json`，各 162 条规范化 `name==version` 的逐项差异均为 0。

| 最终固定图指标 | 旧版 baseline | 最终 e | 旧/新依赖用时比 |
| --- | ---: | ---: | ---: |
| 冷依赖缓存 | 779.8119133 秒 | 268.0995723 秒 | 2.9086652642 倍 |
| 冷组 ComfyUI 启动（单列） | 37.5868049 秒 | 31.3300078 秒 | 不并入依赖比值 |
| 冷组 harness 总用时 | 860.1482818 秒 | 339.9041445 秒 | 不用于宣称整机提速 |
| 冷组计时外种子准备 | 19.102482 秒 | 20.0273848 秒 | 不计入依赖阶段 |
| 热依赖缓存 | 157.3521988 秒 | 59.3644936 秒 | 2.6506113210 倍 |
| 热组 ComfyUI 启动（单列） | 37.6345968 秒 | 31.8950835 秒 | 不并入依赖比值 |
| 热组 harness 总用时 | 219.2691297 秒 | 116.4197265 秒 | 不用于宣称整机提速 |
| 各组最终包名/版本条目 | 162 | 162 | 同名同版本 |

冷组依赖用时减少 **65.6199697%**，热组减少 **62.2728541%**。e 日志分别为 `fixed-current-e-windows-long-root/state/fixed-cold-20261030-171319.log` 和 `fixed-hot-20261030-171930.log`。新旧使用同一冻结图、同一绝对 Python 路径恢复后的 GPU 种子、官方 PyPI/Git 和相同系统代理，真实计时串行进行；共同首次联合解析 295.5045331 秒及 GPU bootstrap 不在该固定图比较内。hot 重置 GPU 种子后重新安装，保留本组缓存，不是已安装环境的空操作。

| 最终 e 阶段（秒） | cold | hot |
| --- | ---: | ---: |
| 入口开销 | 0.017 | 0.016 |
| resolving（固定图环境检查） | 0.191 | 0.163 |
| preparing | 47.781 | 0.866 |
| downloading（含缓存核验） | 124.012 | 3.374 |
| building（含源码获取/构建准备/缓存检查） | 42.383 | 2.300 |
| 安装前 verifying | 0.331 | 0.418 |
| installing | 30.498 | 25.942 |
| 最终 verifying | 22.887 | 26.286 |

阶段表由 metrics 的 phase 切换及依赖阶段结束时间相减；显示值经过四舍五入。cold 日志第 12 行的独立下载计时为 **143 文件、123.99 秒**，第 452–600 行记录 **144 包本地安装、30.50 秒**。hot 第 12 行为 143 文件缓存处理 3.35 秒，第 307 行为本地安装 25.94 秒。阶段切换时间包含少量边界开销，故与内部秒表显示不完全相同；hot 的 downloading/building 不能解释为发生了同等时长的远程下载或编译。

e cold 日志第 14–414 行记录固定 SAM2 远程 fetch 和原缓存 checkout，第 415 行明确为“不联网”的短目录本地检出，第 420–424 行构建成功。因此额外本地检出没有冒充第二次远程下载，长目录 fresh cold 的实际构建已成功。测试 harness Core SHA-256 为 `2814a64221a369912f4f1a53119f6d9f869075ea25bea52b1b25a4ec9ee2253d`，与候选 e 对应同一源码；它与最终包内 Core 是不同构建产物，不能代替 EXE/Setup 原生验收。

e cold 的托管 HTTP 响应体计数为 **1,012,838,069 字节**，固定下载文件有效内容为 **1,010,718,100 字节**；e hot 的托管计数为 0。该计数不包含来源 metadata 或 pip/Git/uv 子进程流量，旧版的 0 同样是“未观测”，不得用这些值计算全网络字节节省。暂停恢复结果另列，不由 cold/hot 成功推定。

### 源码获取次数与重复传输字节的可观测范围

逐项检查这四份已完成日志，SAM2 的显式远程获取与本地操作如下。`Cloning ...` 描述和随后的 `Running command git clone ...` 是同一次 clone，不重复计为两次；初次共享解析之前的源码准备不在 fixed 组内。

| 运行 | 日志记录的 SAM2 远程获取 | 本地源码/构建缓存行为 | 直接证据 |
| --- | ---: | --- | --- |
| 旧版 cold | 1 次 clone | pip 临时源码目录构建 | baseline cold 第 12–13 行 |
| 旧版 hot | 0 次显式 clone/fetch | 直接使用缓存 SAM2 wheel | baseline hot 第 12 行 `Using cached sam_2...` |
| e cold | 1 次固定提交 fetch | 1 次原缓存 checkout、1 次不联网短目录检出，再构建 wheel | e cold 第 14、412–415、420–424 行 |
| e hot | 未出现远程 fetch 或短目录检出 | 固定构建缓存复用；building 阶段 2.300 秒，未打印实际 wheel 构建块 | e hot 第 13–14 行；之后安装使用缓存 wheel |

因此，本次 fixed 对比**不能宣称旧 cold 多次 clone、e cold 只一次**：这两组日志都只记录一次显式远程获取。改进所避免的重复取源码问题来自原完整解析/安装路径及历史证据；本次共同初始解析已排除。Git 输出的压缩大小经过取整，也没有包级网络计数器，不能据此计算精确 Git 下载或重复字节。

| 字节指标 | 旧版 cold/hot | e cold | e hot |
| --- | --- | ---: | ---: |
| 托管 artifact 响应体实际读取 | 未观测旧 pip/Git 下载；指标的 0 不代表没有流量 | 1,012,838,069 B | 0 B（仅此托管计数器范围） |
| 143 个固定文件各保留一份的有效内容 | 固定输入相同，旧子进程传输量未观测 | 1,010,718,100 B | 复用同一批有效文件 |
| 高于一次完整固定文件内容的额外托管响应体 | 不可计算 | **2,119,969 B，约有效内容的 0.210%** | 未新读托管 artifact 正文 |
| 可以精确归因到某文件/某 Range 的重复下载字节 | 未观测 | 未保存逐请求区间与响应体计数，不能精确拆分 | 托管 artifact 正文范围为 0 B；不外推全部网络 |
| 来源 metadata、pip/uv 构建依赖、Git 子进程及协议开销 | 未观测 | 未观测 | 未观测 |

2,119,969 B 是 `managedHttpBodyBytes - 143 个固定文件有效大小之和` 的精确差值，只能称为额外托管响应体，**不能全部标成重复 wheel 内容**。它可能包含 Range 支持探测、失败/取消前已读内容或重传/兼容传输；现有证据不足以逐项分配。不能把这个差值用于计算旧新全网络节省。`CountingHandler/CountingStream` 在 harness 中只累计经 `Downloads` 返回流的 `Read/ReadAsync` 字节，不计 HTTP 头、TLS、单独的来源 metadata 客户端和子进程网络；e cold 日志第 762 行仍明确出现 pip 下载 47.4 MB 的 Triton，第 425–427 行有源码包构建依赖准备，这些都不在托管计数器内。

轻量核对当前任务数据库为 143 个独立 SHA/大小身份、全部完成，总有效大小与 cold metrics 的 `observedDependencyReceived` 相同。数据库已由 hot 更新，所有 `attempts=2` 代表 cold 与 hot 两轮任务调用，不能说每个文件下载了两遍；hot 的正文计数正好为 0。该 label 没有保存 cold 逐任务事件历史或单独 `cold-transfer-observation.json`，因此没有据后来的任务状态重建当时重试次数或 ETag 事件。

## 最终 e 的真实暂停与进程重启恢复

`SUITE\fixed-e-resume\state` 使用新的独立依赖缓存。`fixed-pause-metrics.json` 为 `status=paused`、`realTransferPause=true`；`fixed-resume-metrics.json` 为 `status=passed`、`sameFilesResumed=true`。两轮 owner 均为 `3339f8dcb326433a8ed5dc125c21da92`，暂停后的三份不完整文件在恢复前仍保持相同任务 ID、目标路径、大小、SHA-256 和保留字节，恢复后同一身份全部完成。

| 真实暂停时的文件 | 保留字节 | 固定文件总字节 | 原任务 ID |
| --- | ---: | ---: | --- |
| comfyui-frontend-package 1.57.4 | 8,388,608 | 24,596,990 | `c2cee855fcfa44e78674bb0a94b03a09` |
| comfyui-embedded-docs 0.5.15 | 5,242,880 | 24,087,745 | `a04b9c4c65f24b63bf3309783aede169` |
| comfy-kitchen 0.2.40 | 4,194,304 | 64,014,503 | `da0768e3f705466fa18deeeaf11f1f71` |
| 合计不完整文件的有效保留内容 | **17,825,792（17 MiB）** | — | 三份原任务身份保持 |

| 指标 | 实测值 |
| --- | ---: |
| 暂停前依赖阶段 | 45.4161185 秒 |
| 重启后恢复依赖阶段 | 202.1912983 秒 |
| 两段依赖计时之和（不含停机等待） | 247.6074168 秒 |
| 恢复后 ComfyUI 启动（单列） | 33.3417181 秒 |
| 恢复下载阶段 | 143 文件，100.32 秒 |
| 恢复后的本地安装 | 144 包，31.16 秒 |
| 暂停前/恢复托管响应体 | 20,737,516 / 992,013,441 B |

日志分别为 `fixed-pause-20261030-172156.log` 与 `fixed-resume-20261030-172319.log`。恢复日志第 12 行完成下载，第 420–429 行短目录 SAM2 构建成功，第 605 行完成本地安装，第 774–778 行通过 Triton GPU kernel、`pip check` 和启动验收。`fixed-resume-packages.json` 与旧 cold 的 162 项包名/版本逐项差异为 0。这里证明了部分文件恢复到完整可用安装，不仅是暂停按钮生效或重新开始下载。

暂停加恢复的托管响应体合计 1,012,750,957 B，比该批固定有效内容多 2,032,857 B；仍只能作为额外托管响应体，不能精确拆分为重试、Range 探测或文件重叠字节。此前已完成的小文件不包含在上述三份 partial 的 17 MiB 中。此样本与旧版的实际暂停位置不同，**不计算暂停恢复速度倍数**，也不把两段计时之和冒充连续冷安装时间。旧版正文暂停/重试对照与 d 原失败现场恢复另记，不能沿用本轮通过状态。

### 旧版真实正文暂停：临时内容未形成持久断点

`SUITE\fixed-baseline-resume-v2\state\fixed-pause-metrics.json` 记录 `status=paused`、依赖阶段 **244.5563435 秒**、`legacyPayloadPause=true`。这次不是在解析阶段按定时器猜测暂停：`fixed-pause-20261030-172804.log` 第 323–324 行先记录下载 frontend wheel，再观察实际临时文件已达 **16,515,072 / 24,596,990 字节**，随后取消。

取消后 `legacyPartialFilesRemaining=[]`、`retainedAfter=[]`、`realTransferPause=false`：pip 清理了该临时文件，没有可供启动器恢复的部分文件。日志第 325 行的通用 `checkpoint retained` 文案不证明旧 pip 留下了断点，以上述专用观测字段为准。16,515,072 B 是取消前实际写入临时文件的内容量，不能直接等同于精确网络流量或已发生的重传字节。

验收方核对该轮专属进程树全部退出后，在**同一 label、保留同组缓存**下启动旧版 retry，现已完成。`fixed-resume-metrics.json` 为 `status=passed`、`sameFilesResumed=false`、`resumeKind="legacy pip retry; no partial package checkpoint proof"`。依赖阶段 **382.320022 秒**，ComfyUI 启动 **37.5982464 秒**；该轮 `fixed-resume-packages.json` 与 e 恢复清单各 162 项、差异为 0。它仍是旧 pip 重试，**不称为断点续传**。

| 暂停/重试对照（秒） | 旧版 baseline-resume-v2 | 最终 e-resume |
| --- | ---: | ---: |
| 暂停前依赖阶段 | 244.5563435 | 45.4161185 |
| 进程重启后的依赖阶段 | 382.3200220 | 202.1912983 |
| 两段依赖之和，不含停机等待 | 626.8763655 | 247.6074168 |
| 重启后 ComfyUI 启动，单列 | 37.5982464 | 33.3417181 |
| 不完整文件保留 | 0 B（临时文件被清理） | 17,825,792 B |

两组都完成了整个依赖安装，但旧版暂停时观察的是单份 frontend 临时内容，e 保留的是三份不同进度的文件，**不计算严格暂停恢复速度倍数**，也不将累计时间当作连续 cold 成绩。

旧版这次可确认一个具体文件的重复下载内容下界。retry 日志 `fixed-resume-20261030-173251.log` 第 324–325 行再次下载完整 frontend wheel，显示 `24.6/24.6 MB`。只读核查该轮隔离 pip HTTP 缓存中唯一 24,596,990 B 响应体，SHA-256 为 `2c4472d5943ac18f9098af3565e319c6ffcc1eca5d873da5d62adc410f8f43bd`，与冻结文件身份一致；缓存写入时间也在本轮 retry 的 01:33:37。结合暂停前已写入 16,515,072 B、该临时文件随后删除的证据，**该固定文件至少有 16,515,072 B 内容在重试中重新下载**。证据摘要为 `output/legacy-retry-bytes-audit-20261031/summary.json`。这是已确认单文件的内容下界，不是全部 pip/Git 网络流量，不包含协议开销，也不能推广为所有包的精确重复字节。

### d 的构建失败现场由 e 接续完成（仅功能验收）

在保留旧 `fixed-current-v2-resume` 现场的前提下，e 的 `fixed-recover` 已通过。证据为 `SUITE\fixed-current-v2-resume\state\fixed-recover-metrics.json` 及 `fixed-recover-20261030-174107.log`。同一 owner `21a181111c8246228965a54d457a1731` 保留原三个任务的 ID、目标路径、文件大小与 SHA；`samePausedFilesCompleted=true`，表明旧暂停涉及的文件身份最终都完成。

该轮开始时文件传输早已完成，旧失败发生在后续 SAM2 长路径构建，因此 `sameFilesResumed=false`、`retainedBefore=[]` 是正确边界：**不是又一次真实 partial 下载续传**。本轮托管 artifact 正文字节为 0，短目录检出与 SAM2 wheel 构建成功，随后安装、Triton GPU kernel、`pip check` 和 ComfyUI 启动通过。原 `fixed-resume-metrics.json` 仍为 `failed`，没有把旧失败改写为成功。

此轮与独立安装器/CLI 验收并行，仅证明 e 能接续真实历史失败现场；不把它的运行时间或 0 托管字节填入冷、热或断点性能成绩。

### 最初完整隔离环境的最终 e 功能复核

最初 `current` 的完整环境恢复后，用 e 对应实现再次执行 full `resume`，`SUITE\current\state\resume-20261030-174433.metrics.json` 为 `passed`，当前 `resume-metrics.json` 指向同一轮。该轮 `managedHttpBodyBytes=0`、`modelFiles=[]`，`final-packages.json` 与 e cold 的 162 项包名/版本逐项一致。

日志 `resume-20261030-174433.log` 第 392–402 行记录 11 个固定节点验证，第 404 行通过 RTX 4060 Laptop GPU 上的 Triton kernel，第 410 行通过 `pip check`，第 411–413 行记录实际 ComfyUI 启动、所有拥有服务停止和最终 passed。full harness 同时检查 `/system_stats` 根目录身份及要求的 `/object_info` 类。该轮预热了最终构建缓存身份，之后交给原生 e UI 验收；它与其他独立功能验收并行，**不列作性能成绩或完整 cold**。

## 原生 e 窗口验收（已通过）

使用实际 `dist/candidates/dependency-speed-20261031-e/AnimaRandomStudio.exe`，在已通过 full 功能复核的隔离环境上操作。最终 `output/dependency-native-qa/e-native-acceptance.json` 为 `status=passed`，候选 SHA 与最终产物一致。各步骤证据位于同目录 `e-*.json`，只保存可访问性树/状态与计划身份，不在 JSON 内嵌截图 payload：

| 已验证行为 | 证据与边界 |
| --- | --- |
| 初始等待用户继续，点击普通 UI 按钮才运行 | `e-initial-waiting.json`、`e-resume-clicked.json`、`e-started.json` |
| 真实依赖核验显示明确阶段及 elapsed，不伪造安装百分比 | `e-dependency-running.json` 显示“正在核对已完成依赖的文件完整性”、忙碌进度栏、已用时 10 秒；此次观察的是核验阶段，不冒充冷下载速度实测 |
| 暂停成功，UI 回到可继续状态 | `e-pause-requested.json`、`e-paused.json` 显示“依赖处理已暂停”、已用时 19 秒；验收方同时检查 E 盘隔离安装子进程均已退出 |
| 日志入口可以打开实际日志面板 | `e-logs-opened.json` 包含打开日志文件夹和复制日志操作 |
| 通过普通 UI 暂离向导、更多操作正常退出，重开不自动运行 | `e-exit-menu.json`、`e-reopened-waiting.json` 显示原固定任务等待继续 |
| 暂停/退出未漂移已审核计划 | `e-exit-plan-identity.json` 的 `reviewedPlanUnchanged=true`，144 包，ComfyUI 提交不变，reviewedPlan JSON 逐字相同，SHA-256 `d313cc631545b66c39c332dcdbe82a4dc15a71f6bbf86e9e94c8cadd3eca1061` |
| 重开后主动继续同一计划，第二次真实核验期间暂停 | `e-resumed-dependency-running.json` 显示相同文件完整性核验、忙碌进度、已用时 15 秒；`e-resumed-paused.json` 显示已暂停、已用时 24 秒 |
| 第二次暂停后无残留安装进程/服务，正常退出 | `e-paused-process-evidence.json` 的 `writers=[]`、`ports=[]`；`e-final-exit.json` 的 `candidateWindows=[]`；最终汇总 `candidateAndEnvironmentProcesses=[]`、`listeningServicePorts=[]` |

两轮都在真实依赖文件完整性核验期间暂停，随后通过普通 UI 正常退出。向导页面没有“更多操作”菜单，因此使用 UI 返回欢迎页并选择稍后设置，再从主窗口“更多操作”退出；此操作后已审核计划保持不变。重开只呈现“等待继续安装”，不会自动启动任务；再次主动继续后进入同一计划的核验，第二次暂停成功。

最终只有一个 owner `a81cd750919d4da6807ce1d5c271742f`，状态为 `Paused`，便于用户继续原计划。原 resolved snapshot 与 144 包 reviewedPlan 均未变化；环境仍为相同的 162 项包/METADATA 和三项 GPU 版本：torch `2.11.0+cu128`、torchvision `0.26.0+cu128`、torchaudio `2.11.0+cu128`。最终没有候选或 E 盘环境子进程、没有 19992/19994 服务监听，`modelFiles=[]`。

**原生验收范围是热缓存依赖核验与恢复 UI**：没有声称在原生窗口观察到 cold 下载速度，没有走完模型选择/下载的整个向导，也没有重装 Windows。真实 cold 文件下载、下载中 partial 续传与完整依赖安装由前述后端真实安装验收覆盖；两类证据分开陈述。

## 验收结论、未覆盖范围与比较要求

按实际覆盖范围记录结果，不将未开展场景补成通过：

| 验收项 | 旧版 | 新版 | 证据/范围 |
| --- | --- | --- | --- |
| 不经中断的完整新建空环境冷安装及全新 Windows 首装 | 本次没有完整样本 | 本次没有不经中断的完整样本，也没有重装 Windows | 原空环境遇到真实故障后恢复成功，不填入 cold 成绩；固定图 cold 另列 |
| 原空环境计划从下载断点恢复至必需节点可用 | 未测 | 早期恢复已通过；最终 e 原完整环境复核亦通过，162 包与 e cold 一致 | 早期 `resume-20261030-152901.metrics.json`；最终 `resume-20261030-174433.metrics.json`，不将功能复核填入 cold |
| 固定依赖图、空依赖缓存 | 已通过，779.81 秒 | 最终 e 长目录已通过，268.10 秒，约 2.91 倍 | e 与 baseline 各自 cold metrics 和 162 项同版本清单；d 中间成绩另列 |
| 固定依赖图、热依赖缓存 | 已通过，157.35 秒 | 最终 e 已通过，59.36 秒，约 2.65 倍 | e `fixed-hot` 恢复 GPU 种子，保留本组缓存，162 项同版本 |
| 下载中暂停及进程重启后恢复 | 正文暂停与同 label 缓存重试已完成，依赖两段 626.88 秒；至少重下 frontend 内容 16,515,072 B，非断点续传 | 最终 e 已通过：三份 partial 共 17 MiB，保持任务/路径/大小/SHA，依赖两段 247.61 秒 | 两 label 的 `fixed-{pause,resume}-metrics.json` 和各自 162 项同版本清单；停点不同，不宣称严格倍率 |
| d 长路径构建失败现场接续 | 不适用 | e `fixed-recover` 功能已通过，原失败指标保持；历史三个部分任务身份最终完成 | `fixed-current-v2-resume/state/fixed-recover-metrics.json`，与其他验收并行，不列性能 |
| `pip check`、GPU 版本及真实启动 `/system_stats`、必需 `/object_info` | 固定图 cold 的 pip/GPU/启动验证已通过 | e 固定图 cold/hot 和最终 full 功能复核通过 | fixed harness 校验启动根目录并保存 object_info；完整必需类断言由最终 full resume 验证，二者不混淆 |
| 最终源码重新打包并核对候选 EXE/ZIP/Setup | 不适用 | e 构建、实物 hash、隔离安装器和原生热缓存核验/恢复 UI 全部通过 | `output/dependency-candidate-build-e.log`、`output/dependency-final-artifacts-verified.json`、`output/dependency-final-installer-e.log`、`output/dependency-native-qa/e-native-acceptance.json`；安装器独立 UUID AppID |

固定图比较要求：

- 新旧实现使用同一份已解析依赖快照、相同 ComfyUI/节点提交、相同 Python 绝对路径和恢复后的 GPU 种子。旧 DLL 通过同一 harness 入口运行；固定图组排除完整图首次解析时间，需与 full cold 单独报告。
- 两组统一官方 PyPI、官方 Git，以及原环境相同的系统/配置代理；固定图机制比较不混入自动镜像优选差异。自动选源另由真实 full 运行和来源探测记录评价。
- 每个 label 使用独立 runtime/cache；只复用 manifest 锁定的启动工具。GPU/Python 启动准备、种子复制时间单列；不得把复用 GPU 启动包的组称为所有文件完全冷缓存。
- hot 每次将同一 Python 路径恢复到 GPU 种子后安装，同时保留该组自己的缓存，避免仅测到“所有包已安装，无事可做”。共享该 Python 路径的 fixed 运行必须串行。
- 计时必须包含同样的依赖校验边界：旧版单独的“安装并验证 Manager 依赖”应与旧版“安装 ComfyUI 依赖”相加，以匹配新版阶段内验证。启动时间单列。
- 比值仅在两组均成功且最终规范化包名/版本清单一致时计算。将清单绑定具体运行和 metrics，避免后来的 hot 结果覆盖 cold 对比依据。
- 暂停恢复只有在状态确为 paused、有部分下载检查点/保留字节且恢复时任务与 SHA 身份一致时才算通过。定时器触发、出现过下载进度或新跑一次成功，都不足以单独证明断点恢复。
- 对字节统计标明可观测范围；没有计数器覆盖的子进程流量写“不可得”，不能写 0。

真实入口接受 `prepare/cold/hot/pause/resume/fixed-cold/fixed-hot/fixed-pause/fixed-resume`。它会安装、移动和恢复标记目录内的 Python 环境，不应作为普通单元测试启动，也不应对用户现有环境运行。

## 已完成测试

表中是各次实际运行打印的通过数，不相加。多数完整测试入口包含共同的 6 项启动器品牌兼容检查，独立来源测试入口不包含它们。测试发生在分阶段实现期间；后续增量需要对应定向回归，不能把下表视为同一最终二进制的单次全套结果。

| 测试 | 结果 | 证据 |
| --- | --- | --- |
| Core 定向构建 | 0 警告、0 错误 | `output/dependency-final-build.log` |
| 最终测试 CLI 构建与入口守卫 | 0 警告、0 错误；未知 flag 及时拒绝，避免误入整套安装测试 | `output/dependency-final-cli-build.log`、`output/dependency-final-cli-tests/unknown-flag-evidence.json` |
| Core 回归 | 155 项通过 | `output/dependency-final-core.log` |
| 固定依赖安装与恢复 | 最终 136 项通过（130 项安装/源码检查 + 6 项共用检查）；包含真实隔离 venv 中离线 uv/pip 安装、固定版本、来源记录、GPU/外部改动保护、27 项构建环境缓存身份守卫、完整收据热重试、共享文件定向修复、RECORD 越界硬拒绝及短目录源码构建 | `output/launcher-tests/a9329a85f5c14131b63fa44268de3327/dependency-installation/{evidence.json,execution.log,hot-retry-evidence.json}`；同 fixture 的 `source-build/evidence.json`；较早收据专项为 `d4da5d2c8e514c1391c54c6ab3696e9a` |
| 短目录源码构建最终复核 | 32 项通过（26 项源码 + 6 项共用检查）；深路径失败复现/短路径成功、精确 Git 提交、本地子模块、tracked build 保留、reparse 拒绝、取消后进程与临时目录清理；在最终日志回调异常清理增量后重跑 | `output/launcher-tests/b5a6c3a8168048e38fe4956a70465515/source-build/{evidence.json,execution.log}` |
| 依赖文件调度与恢复 | 32 项通过；普通 2/依赖 4 调度、身份保持、异步换源竞争、取消、真实 HTTP Range 续传、`.tar.gz` 持久身份；ETag 修复后重跑通过 | `output/dependency-representation-transfer-regression.log`；`output/launcher-tests/a8dc2a99d03f45658bb25e7dfc5a4838/`；此前 `output/dependency-transfer-archive-review.log` |
| 依赖来源独立测试 | 最终 29 项通过；含 144 包共享坏源冷却、显式换源同批探测合并、代理策略隔离、精确 hash/文件名/大小、源码压缩包、全局 8 连接及直连/自定义代理的无额外响应体 drain 策略 | `output/dependency-source-tests-20261030/results-final.log`；此前 27 项为 `results-health.log` |
| 依赖来源完整入口复核 | 30 项通过（24 项来源测试 + 6 项共用检查） | 当次控制台结果；`output/launcher-tests/fae20b7109e4476694f02969b35126c9/` |
| 依赖进度展示与状态 | 45 项通过（39 项进度测试 + 6 项共用检查） | `--dependency-progress-only` 当次控制台结果；`output/launcher-tests/da3008e47c6a4e97a9adce8ef963059f/`；测试源码 `launcher/Anima.Launcher.Tests/DependencyProgressTests.cs` |
| Git 来源偏好 | 28 项通过 | `output/dependency-final-preferences.log`；`output/launcher-tests/4e3a300a341240628133cfa56f156b7f/` |
| 运行时依赖回归 | 最终 75 项通过；含 Git 低速失败分类、禁止 GCM 交互等待及命令环境清理；最终日志回调异常清理增量后重跑 | `output/source-build-final-v2` 构建的 `--runtime-dependencies-only` 当次控制台结果，未单独保存最终日志；此前 74 项日志为 `output/dependency-final-runtime.log` |
| Git 传输空闲保护 | 14 项通过（8 项传输测试 + 6 项共用检查）；覆盖静默增长、松散对象、零字节停滞、首连接等待、用户取消、子任务清理及既有锁 | `output/dependency-final-git-transfer.log`；`output/launcher-tests/9ca9671626dc4fac9a8323361fcab14e/` |
| 命令进程组生命周期 | 27 项通过（21 项专用 + 6 项共用检查）；包括立即退出父进程、孙进程、继承管道、成功/失败/取消/超时清理、native 参数转义、UTF-8、PATH、nested Job、日志回调异常及显式保留后台服务 | `output/command-lifetime-regression.log`；`output/launcher-tests/9f560b843842423fbd173d70026f2190/command lifetime 中文/evidence.json`；构建 `output/command-lifetime-build.log` 为 0 警告/错误 |
| 命令修复后的服务与原生进程回归 | 软件更新服务 14 项、原生进程 23 项、Git 传输 14 项分别通过；正常服务启动/切换及失败保留合同保持 | `output/command-lifetime-{service-regression,native-regression,git-regression}.log` |
| 资源校验与网络回归 | 63 项通过 | `output/dependency-final-artifact-network.log` |
| 下载来源回归 | 37 项通过；ETag 修复后重跑通过 | `output/dependency-representation-source-regression.log`；此前 `output/dependency-transfer-source-regression.log` |
| 强 ETag 变化的安全兼容下载 | 18 项通过（12 项专用 + 6 项共用检查）；真实 aria2/本机 HTTP、最后来源单次完整 GET、非末源先换源、错误内容拒绝、取消保留、额外空间预算、成功后清理断点 | `output/dependency-representation-regression.log`；`output/launcher-tests/61ce59d6057c42679761c91f62c4d8af/download-representation/evidence.json` |
| 下载任务服务回归 | 194 项通过；当次控制台结果，保留 fixture | `output/launcher-tests/415e8d65a4074fa4ace340d939e627f2/` |
| 下载引擎回归 | 48 项通过；ETag 修复后重跑，仍强制拒绝不同强 ETag，原 416 兼容成功后清理断点 | `output/dependency-representation-engine-regression.log`；`output/launcher-tests/d21521bd79fc4a6ba29cae008c639684/` |
| 新手向导回归 | 161 项通过 | `output/dependency-integration-onboarding.log` |
| ComfyUI 发布版本与恢复 | 44 项通过；Git 传输空闲保护加入后再次验证浅锁保护、取消等待、完整回滚及重启恢复 | `output/dependency-git-transfer-lock-regression.log`；此前 `output/dependency-integration-comfy-release.log` |
| 节点更新与回滚 | 181 项通过 | `output/dependency-integration-node-updates.log` |
| e 候选隔离安装器 | 10 项通过；原 EXE 逐字节一致、运行中覆盖/降级/待更新拒绝、同版本修复、确认旧快捷方式迁移、卸载保留数据 | `output/dependency-final-installer-e.log`；`E:\CodexDependencyTests\20261031-installer-e-5d986a`，采用相同候选 EXE/安装脚本、独立 UUID AppID |
| 原生 e 窗口热缓存核验/恢复 | 最终 passed；两次真实核验期间暂停、日志、正常退出重开、同计划主动继续，最终无子进程/服务监听且计划和 GPU 不变 | `output/dependency-native-qa/e-native-acceptance.json` 及同目录分步骤 JSON；不是 cold 速度 UI 或完整模型向导验收 |

用于上述构建环境身份补全的独立命令：

```powershell
dotnet build launcher\Anima.Launcher.Tests\Anima.Launcher.Tests.csproj -c Release --artifacts-path output\dependency-transfer-build -o output\dependency-transfer-tests --nologo
.\output\dependency-transfer-tests\Anima.Launcher.Tests.exe --dependency-installation-only
```

构建结果缓存将影响产物的显式/继承环境参数纳入稳定 hash，显式值覆盖继承值，包括 CUDA、编译器、CMake、编译/链接参数。实际构建复用同一份参数快照；缓存路径只保存摘要，不输出参数原值。

另一个实际热重试问题是恢复正确的索引来源记录后，uv 把缓存 wheel 的本地 URL 当作不同来源，重复卸载/安装 3 个已经完整的索引包。修前证据为 `output/launcher-tests/a5aac3c82bdb42bb9be98c067ba8f9d2/dependency-installation/hot-retry-evidence.json`。现于来源修复、环境核验和 `pip check` 成功后写入安装收据；重试读取实际文件内容、核对 RECORD 与来源身份后跳过完整包，只修复损坏包。修后同计划重试的文件时间保持不变，日志显示 6 包复用、0 包安装，见上表最新 `hot-retry-evidence.json`。对 OpenCV 类合法重叠文件，仅接受当前固定批次已声明的共享路径，并将实际文件 SHA 和所有 owner 的声明共同冻结；篡改共享文件会使相关 owner 一起进入修复。不能仅靠版本、文件大小或 mtime 跳过内容校验。

收据性能单独在同一真实 E 盘环境的 144 包上测量：核验由 53.809 秒降至 14.773 秒，优化后的写入核验为 14.850 秒，前后有效包列表完全相同，重写收据与原收据逐字段相等。证据为 `output/receipt-profile/summary.json`。优化复用单次调用中的父目录、RECORD 和路径计算，以最多 4 个工作线程重新读取文件内容，不用文件大小或 mtime 缓存跳过 hash。这个局部成绩不是整轮安装速度。最终测试还验证越界 RECORD 会直接拒绝，不调用安装器修复且外部文件不变；真实 symlink 测试因当前 Windows 权限不足标记 unavailable（最新 `execution.log` 第 102 行），不计作通过。

## 已完成的受控 HTTP 基准

入口：`--dependency-transfer-performance-only`。源为本机受控服务器，每条响应流限速 2 MiB/s；每组 4 个 16 MiB 文件，共 64 MiB。并发组使用实际内置 aria2，经安全 loopback bridge 访问受控源；两组全部文件完成 SHA-256 校验。

| 项目 | 串行 HTTP | 依赖并发传输 |
| --- | ---: | ---: |
| 用时 | 39.0166879 秒 | 9.5917676 秒 |
| 对外响应流峰值 | 1 | 8 |
| Range 请求数 | 0 | 20 |
| 本组有效文件字节 | 67,108,864 | 67,108,864 |

受控加速比：**4.0677265679 倍，约 39.02 → 9.59 秒（4.07 倍）**。该结果仅证明在这组连接限速条件下并发/分段机制有效，不代表互联网真实包下载速度，也不代表解析、构建、本地安装、GPU 初始化或 ComfyUI 启动的端到端加速比。

原始指标：`output/launcher-tests/d532548b4b364ecaadb9b93e5074f662/dependency-transfer-performance-fd1af5185557468a84d7e6f9f15cdcf4/performance.json`。

强 ETag 兼容修复后的同条件回归再次通过：串行 42.870914 秒、并发 9.6080763 秒（4.461966 倍），两组仍为 67,108,864 字节，峰值分别为 1 / 8，分段请求 0 / 20。入口报告 16 项通过，日志为 `output/dependency-representation-performance.log`，fixture 为 `output/launcher-tests/fa4296e0676c4bfca0ff1376c16c3269/`。保留前一次原始数字，不把两次不同运行拼成同一比值。

## 已完成的真实来源小流量探测

配置为 `SourceId=china`、`NetworkMode=system`，读取真实 `F:\comfyui\requirements.txt`，没有安装包。小样本吞吐只用于当次来源排序，不能外推整批吞吐。

最初用三个包共享 8 秒探测预算时，USTC 完成 3/3 但没有独立 metadata，官方只来得及完成 1/3 且有 metadata；完成数优先会错误选 USTC。记录在 `output/dependency-source-tests-20261030/live-probe-system-before.log`，用时 8.1303366 秒。未完成的探测不能当作源缺包。

改为单个真实顶层 Frontend 哨兵后，用时 7.5941696 秒：

| 来源 | 必要包探测 | 独立 metadata | 采样吞吐 |
| --- | --- | --- | ---: |
| USTC | 1/1 | 0/1 | 63.8 KiB/s |
| TUNA | 1/1 | 0/1 | 1030.5 KiB/s |
| 官方 PyPI | 1/1 | 1/1 | 59.4 KiB/s |

当次解析顺序为官方 → TUNA → USTC；固定文件的下载来源另按已验证文件身份和采样吞吐选择，可优先 TUNA。解析 metadata 能力和文件吞吐分别排序。证据：`output/dependency-source-tests-20261030/live-probe-system-after.log`。

镜像缺包的真实证据是前述 10 月 26 日日志；10 月 30 日这次单哨兵探测的三个来源均找到该包，不能写成当日全部镜像缺包。

最终来源探测连接池使用 `SocketsHttpHandler.MaxResponseDrainSize=0`：有界探测停止或拒绝过大响应后，释放响应不再为连接复用额外读取剩余响应体。代理、系统/PAC 和重定向选项继续来自统一网络策略；普通文件下载 handler 不变。直连与自定义代理的两项新增守卫包含于上表最终 29 项来源测试。这些探测边界验证与前述最终固定图性能成绩分别记录，不能互相代替。

## 本地候选与发布状态

早期本地候选 `dist/candidates/dependency-speed-20261030-a` 的单 EXE、仅含同一 EXE 的 ZIP、Setup 及校验已通过，日志为 `output/dependency-candidate-build-a.log`。它来自有未提交改动的本地源码，且早于后续真实 Git 阻断修复、下载速度 UI 和构建参数缓存身份补全；它是中间候选，不能将其称为最终已验收安装包。

c 候选 `dist/candidates/dependency-speed-20261030-c` 的构建和包校验曾通过（`output/dependency-candidate-build-c.log`），但不包含随后真实冷测发现的强 ETag 兼容修复，已由 d 候选替代。

中间候选 `dist/candidates/dependency-speed-20261031-d` 对应前述已完成冷、热成绩。`output/dependency-candidate-build-d.log` 记录构建与包校验成功。下列保留 d 的独立身份，不与后续 e 混用；数值逐文件核对其 `SHA256SUMS.txt`、`candidate.json`、`build-info.json` 和 `package-info.json`，构建 verifier 已校验实物。文档复核仅读取小文件及文件长度，未重复执行大文件 hash：

| 文件 | 字节 | SHA-256 |
| --- | ---: | --- |
| `AnimaRandomStudio.exe` | 125,881,909 | `b346499e4d54718a5d9cd85e3fd9ec66b1b8ee03490c76dc4e5d01b2d318e02d` |
| `AnimaRandomStudio-portable-win-x64.zip` | 119,393,104 | `2f5e0f49e62c84345dabbf5cca987ef3f9a9cf9a16ac53861a0dfacbb488c6d2` |
| `AnimaRandomStudio-Setup-win-x64.exe` | 119,281,523 | `f7ac1d7267a83d041e6d32bdcedd9e91d2052339d427d1f37ce8eee7d4466afa` |

`candidate.json` 的通用 `sha256` 字段指 portable ZIP，不能把它标成 EXE hash。候选版本标记为 `1.5.0`、`single-exe-v1`、Release/win-x64，构建时间 `2026-10-30T16:23:39.2860956Z`（本地 10 月 31 日），源码基点 `a06635fb5b42088b63101f36fa689dc30c1700b3`，`sourceDirty=true`，因此该提交号不足以单独重现候选内容。构建清单记录未提交变更，payload SHA-256 为 `f5b41d2f049bc1fb5a7e14566560a31f22227193f55047756f8c20c5b51bcf31`。`package-info.json` 列出 652 个嵌入文件，其中 Core SHA-256 为 `21ae11c07f6b990058bea4d970f26c752be92ef138b540710a2796497926cda7`。该候选未签名，属于本地验证产物，不代表重新发布 1.5.0。

d 已包含强 ETag 兼容修复和最新网络预算说明，但不包含后续源码短目录构建与命令 Job 清理修复。它的冷、热中间成绩保留，不能代替最终 e 的复测。

当前验收候选为 **`dist/candidates/dependency-speed-20261031-e`**。`output/dependency-candidate-build-e.log` 第 78 行确认 `standalone EXE, EXE-only ZIP, Setup checksums and embedded files verified`。本次只读取 e 的小型清单、包信息与文件长度，以免影响正在进行的正式基准：

| e 候选文件 | 字节 | SHA-256 |
| --- | ---: | --- |
| `AnimaRandomStudio.exe` | 125,903,417 | `6e0c44f59a7ee17ecc3d196420aff37241d866524ccc50c16ec2abd1e4f082fb` |
| `AnimaRandomStudio-portable-win-x64.zip` | 119,402,156 | `e5271da53e5ec0350f27c283e03e4f71ecfc1a04024aa939d418038586601cc3` |
| `AnimaRandomStudio-Setup-win-x64.exe` | 119,288,149 | `5ffb7721f636f0a7059b99f1f47d256ff7efc2bab37c01ed790c2b0ea664b868` |

e 的构建时间为 `2026-10-30T17:11:40.3640023Z`（本地 10 月 31 日），版本/格式仍为 `1.5.0`、`single-exe-v1`、Release/win-x64、未签名；源码基点仍为 `a06635fb5b42088b63101f36fa689dc30c1700b3` 且 `sourceDirty=true`。payload SHA-256 为 `032b26e9a8ea1c2f67117daaa95afdf931574598297e285bed789a6927770f69`；652 个嵌入文件中 `Anima.Launcher.Core.dll` 为 21,463,552 字节、SHA-256 `19e72e6f67f53162825868a05508585d5d5faab1c079504a2da0782e7fb19dec`。`candidate.json.sha256` 仍只指 portable ZIP，EXE、ZIP、Setup 三种身份分别记录。

正式性能阶段结束后，三份最终实物再次逐文件 hash 核验通过，`output/dependency-final-artifacts-verified.json` 对上述 EXE、ZIP、Setup 分别记录同样的字节数和 SHA-256，三项 `verified=true`。本次文档收尾仅读取该结果文件，没有与基准争用大文件读取。

e 已包含短目录固定源码构建、`source-staging-v2` 缓存身份和创建时 Job 绑定的命令子进程清理。正式冷、热使用 `current-e-windows-long-root` label，均已通过，结果见前文最终表；真实恢复、隔离安装器和原生热缓存核验/恢复 UI 也已通过。本轮不 commit、不 push，不创建 GitHub Release。

e 隔离安装器验收的 10 项检查已通过，日志为 `output/dependency-final-installer-e.log`，fixture 为 `E:\CodexDependencyTests\20261031-installer-e-5d986a`。使用**相同 e 候选 EXE 与相同安装脚本，以独立 UUID AppID 重建隔离安装器**；不是直接拿正式 AppID 修改本机安装。验证覆盖首装状态探测、已有新版 portable/待更新保护、每用户安装后的 EXE 与原候选逐字节一致、运行中阻止覆盖、安装/卸载期间待更新保护、拒绝降级、同版本修复、仅迁移确认属于旧程序的快捷方式，以及卸载移除程序/注册而保留共享用户数据。原生 e 窗口验收另有前述独立证据，二者不互相替代。
## 附录 A：已确认的旧路径耗时因素

历史记录仅用于定位，不作为本次新旧性能比值的分母。

| 证据 | 已观察到的现象 |
| --- | --- |
| `%LOCALAPPDATA%\AnimaRandomStudio\logs\launcher-20261027.log`，276–547 行 | 已有环境的一次依赖处理从 18:07:23 到 18:17:50，约 10 分 27 秒；不是空缓存新装。SAM2 在解析与安装中重复取源码；较大 wheel 传输约 369–378 kB/s。 |
| `output/download-acceptance/cold-d4e4ff3cf2344ecf9eca2725b99ef7b7/state/environment-install.log`，386–387、557–558 行 | 当时 USTC 缺少 `comfyui-workflow-templates-media-assets-02==0.1.17`，前面的下载和元数据工作不能让这轮解析完成。该旧测试运行时已不作为可用环境，仅保留日志证据。 |
| 同目录 `source-switch.json` | 旧验收记录过 USTC 缺包、TUNA HTTP 403 后显式改为官方来源。该历史故障不表示这些源现在或对所有包都不可用。 |
| 同日志 2161–2163 行 | 缓存命中的重试仍在 `Installing collected packages` 到成功之间用时约 181 秒，期间缺少细分输出。下载结束不等于本地安装完成。 |
| `output/dependency-delay-audit-20261030/source-retry-result.json` | 纯本地假动作复现旧逻辑：解析成功后正式安装重新从失败的首源开始，缺少 PyPI 包时还重复组合 Git 来源。该文件明确是无网络、无 Python 的逻辑复现，不能当真实下载计时。 |

当前实现把完整解析结果固定为包名、版本、文件名、SHA-256/提交身份；已知身份的文件使用共享下载服务；本地安装不再次求解整个依赖图。依赖最多同时传输 4 个文件，16 MiB 起最多 4 段、最小分段 4 MiB；普通资源仍为 2 文件，托管 HTTP、元数据探测和 aria2 bridge 共享 8 条外部连接预算。pip/Git 子进程本身不由该 HTTP 信号量计数。

## 附录 B：真实阻断、修复与早期恢复证据

### 已遇到并修复的真实阻断

1. 初次 cold 运行在 Impact Pack Git 获取处失败。`Operation too slow. Less than 1024 bytes/sec transferred the last 30 seconds` 被旧分类识别为 `Unknown`，没有进入来源回退。证据：`SUITE\current\state\cold-20261030-143825.log`，140–154 行。该次总运行约 481.35 秒，`dependencySeconds=0`，所以不是一次依赖安装耗时样本。现已把 `operation too slow` 归为 Transport，并覆盖字符串回退判断。
2. 随后 resume 遇到 Git Credential Manager 自动识别及凭据等待；已有 `GIT_TERMINAL_PROMPT=0` 仍在约 22:49:11 到 22:52:26 之间等待，最后以 `AuthenticationRequired` 结束。证据：`SUITE\current\state\resume-20261030-144839.log`，41–61 行及 `resume-before-gcm-fix.metrics.json`。现于子进程入口强制 `GCM_INTERACTIVE=never`，并只允许中国内置公共 Git 镜像的鉴权/403 故障回退到官方来源；不把自定义私有仓库或 PyPI 鉴权失败泛化为可回退。

两项修复之后继续使用原固定输入及已有断点，运行日志为 `SUITE\current\state\resume-20261030-145713.log`。其过程不是第二次真正冷启动；后续完整冷测须使用新的独立 label。

该次继续运行还暴露了 Git 自身低速选项未覆盖的长等待：SAM2 从 23:01:43.923 开始取源码，到 23:06:23.313 才打印 fetch 完成，约 279.39 秒（同日志 100–104 行）。期间观察到 `index-pack` 的临时 pack 长时间为 0 字节，随后自然完成；不能把它表述为永久死锁。新增 `GitFetchRecovery.RunTransfer` 按 `.git/objects` 下 pack/松散对象文件的实际新增字节监控空闲，默认 90 秒、每秒采样，初始连接等待同样有界。静止旧文件、仅 mtime 变化或日志输出均不算对象传输进度；静默但持续增长的文件允许继续。超时先取消并等待 fetch 子任务清理结束，再抛出可供换源处理的 TimeoutException；监控任务在 finally 取消并等待，原浅克隆锁保护合同保持不变。Installer 仓库获取与 DependencySources 已接入该入口并启用 Git `--progress`；进度日志用于展示，不用于决定空闲超时。集成后的 14 项定向检查通过，证据见上表。

初次 cold 的 `managedHttpBodyBytes=2,789,377,346` 是当次托管 HTTP 实际读取响应体的计数，包含该阶段的启动资源/GPU 下载，**不是全部网络流量**。pip、Git、uv 子进程网络字节未被该计数器观测，不得记为 0 或据此宣称省掉了这些下载。

同次 resume 后来在 `polars-runtime-32==2.1.0` 的范围支持请求失败，任务 `a060c35de76c46f790a69656a3043eaa` 保存 45,537,657 / 53,926,265 字节，文件 SHA-256 为 `f2fe32f48af2395cd6ec52a433a03970a69aab1dbb66a613729fd549ce92c904`。持久任务最后来源为 TUNA，aria2 检查点原来源为 USTC。现有下载代码已经对非末位来源的 HTTP 403 尝试后续记录来源；进程内健康排序没有完整持久化，不能由这一次最终错误推断“没有尝试换源”。

随后按该任务相同 `NetworkMode=system`、相同安全 HTTP 客户端，对三个已记录 URL 顺序发出有界 `Range: bytes=0-0`，TUNA、USTC、官方 `files.pythonhosted.org` 均返回 `206`、`bytes 0-0/53926265`，各实际只读 1 字节，用时分别 0.327、0.256、2.605 秒。证据：`output/dependency-live-range-probe-20261030/results.log`。该次复核没有改动实际任务/断点，也没有扩大 403 或鉴权回退规则；原固定计划与断点随后恢复成功。这个新探测顺序不冒充此前失败进程的实际尝试顺序。

### 已完成的真实恢复安装

`SUITE\current\state\resume-20261030-152901.metrics.json` 的状态为 `passed`，对应 `resume-20261030-152901.log`。这是保留的历史按次指标，当前通用 `resume-metrics.json` 后来已被最终 e 功能复核更新，不能用它指代本轮。该早期运行复用此前已完成文件、GPU 环境和部分下载，**不是完整 cold 样本，也不能拿它与旧版完整新装计算提速比**。

| 指标 | 实测值 |
| --- | ---: |
| 本轮恢复总时长 | 348.6407132 秒 |
| ComfyUI 依赖阶段 | 217.0821109 秒 |
| 其中 144 个包本地 uv 安装 | 51.39 秒 |
| 首次 ComfyUI 启动 | 41.865931 秒 |
| 本轮托管 HTTP 响应体字节 | 33,287,516 |

日志 91–93、238 行记录 144 包准备及安装；241、426 行 `pip check` 成功；387–408 行完成 11 个节点验证；421 行记录 `Triton GPU kernel verified`，设备为 NVIDIA GeForce RTX 4060 Laptop GPU。harness 随后成功验证 `/system_stats` 根目录及必需 `/object_info` 类；原始响应保存在同一 state 目录的 `system-stats.json`、`object-info.json`，最终包清单为 `final-packages.json`。本轮 `modelFiles=[]`，没有下载模型。安装收据按包核验 RECORD 文件内容，直接来源恢复保留 PEP 610 身份。上述 HTTP 字节仍不覆盖 pip/Git/uv 子进程，不能将它解释为本轮全部流量。

### 固定图冷测暴露的官方来源 ETag 分裂

随后正式 `fixed-current` 冷测在 `2026-10-31T00:13:49+08:00` 失败，依赖阶段 174.92 秒，不能作为成功的新冷测成绩。日志为 `SUITE\fixed-current\state\fixed-cold-20261030-161025.log`；任务 `dbaa8b5334464207b386676301991920` 的 `polars-runtime-32==2.1.0` 保留约 1,392,640 / 53,926,265 字节，该轮仅记录官方 `files.pythonhosted.org` 来源。桥接层检测到并发 Range 的强 ETag 不同而停止，原断点保持。

只读复核使用相同网络策略，发出两轮共 10 个单字节 Range 请求，总计只读取 10 个响应体字节，禁用额外响应体 drain。0 和 1 MiB 位置返回强 ETag `"ae7aa6eb0f7c446305196f02029d94dd-7"`，13 MiB、26 MiB 与末字节返回 `"b77f6ab8973ed65ab7b8b210a1a3fc43"`；第二轮携带第一种 `If-Range` 仍返回同样分裂的 `206`，全部总长一致。证据为 `output/dependency-etag-probe-20261031/results.log`。因此这不是弱标签或本地引号格式误判；只能确认源/代理缓存返回不同强验证标识，单字节探测不能证明完整内容相同。

修复保留强 ETag 一致性检查，以专用异常标记这种故障；非末位来源继续原有换源，只有最后来源尝试一次独立无 Range 完整 GET。完整下载不继承 aria2 的稀疏前缀或 If-Range，另外预留整份空间，通过原定大小和 SHA-256 后才发布并清理旧断点；失败或取消保留原 aria2 文件、控制文件和身份记录。泛化内容错误、403 和私有鉴权规则不因本修复获得新回退。上述行为已通过表中的 18 项回归。

真实同一文件的修复验证随后通过。证据目录为 `SUITE\fixed-current\state\etag-single-file-20261030-162353`，保留原任务、partial、控制文件及身份记录备份。`progress.jsonl` 记录再次触发 `source_representation_changed` 后使用独立 HttpClient 完整 GET；`result.json` 记录原文件身份仍为 53,926,265 字节、SHA-256 `f2fe32f48af2395cd6ec52a433a03970a69aab1dbb66a613729fd549ce92c904`，`verified=true`、`databaseUnchanged=true`。原任务数据库及 owner 失败状态未被此单文件验证改写。这轮约 24.2616 秒与候选 d 构建并行，仅证明真实故障修复，不作为性能样本；新的正式冷测另用 `current-v2` 独立 label，结果见附录 C；最终 e 见正文。

### 后续暂停恢复暴露的源码构建长路径

`SUITE\fixed-current-v2-resume\state\fixed-pause-metrics.json` 记录 `status=paused`、`realTransferPause=true`，同一 owner 留下 3 个非空且未完成文件检查点，保留字节分别为 9,437,184、3,145,728、343,044。重启继续后，`resume-transfer-observation.json` 记录 143 个下载全部完成，总有效内容 1,010,718,100 字节；但整个 `fixed-resume-metrics.json` 为 **failed**、`sameFilesResumed=false`，不能宣称完整恢复安装已通过。

后续失败发生在 SAM2 wheel 构建：`fixed-resume-20261030-163607.log` 第 418 行仍直接在深层 dependency-sources 缓存构建，第 620 行报 `could not create ... sam2.1_hiera_b+_MOSE_finetune.yaml: No such file or directory`。同一构建目标字符串在此 label 下长 266 字符（包含日志中的 `wheel\.\`），此前 cold 对应路径为 259 字符；原源码文件存在，故不能把它当作缺少下载文件。

补充的实现把 Git 源码在已有短 TEMP 中做精确提交的本地独立 checkout，保留 `.git`、可用 refs、合法 tracked build/dist 和本地已固定子模块；不带入旧 untracked 构建残留，不原位修改源码缓存。源码目录完整安全复制，archive 仍由短 TEMP 解包；`source-staging-v2` 纳入构建缓存身份，防止复用旧原位构建产物。真实 SAM2 单独短目录 wheel 构建已通过，最终 Job 正常路径用时 11.2536978 秒，669 个原源码文件及 GPU 环境清单不变，证据为 `output/source-build-probe-final/{evidence.json,execution.log}`。最终 e 长目录 cold/hot 和对原失败现场的 `fixed-recover` 功能验收均已另外通过，详见正文；单独 wheel 构建不作为整轮性能成绩。

另对旧 retry 的 SAM2 wheel（177,862 B）与 e cold wheel（176,760 B）作只读逐成员审计，两者均为 65 个相同路径，无增删；7 个成员逐字节相同，57 个成员只有 CRLF/LF 差异，另一个差异为随文件内容变化的 RECORD。各自 RECORD 的全部 hash/大小均有效，除 RECORD 外规范化换行后的内容完全一致，没有因为短目录检出遗漏包文件。证据：`output/sam2-wheel-content-audit-20261031/summary.json`；这是内容检查，不要求不同构建的 ZIP 字节或时间戳相同。

短目录取消测试又复现了进程清理缺陷：pip/backend 父进程已退出后，仅调用父 PID 的 `Kill(true)` 仍可能留下 Python 子孙进程。旧 fixture `output/launcher-tests/36b37cd343d1467784b8345c4ee9dd2b/source-build/build-child-pids.json` 的记录与进程身份检查证实该问题，测试进程已按精确身份清理。

现用独立 `CommandProcessLifetime` 在 Windows 创建进程时通过 `PROC_THREAD_ATTRIBUTE_JOB_LIST` 加入专用 Job；取消、超时、失败及普通命令成功返回时等待所有成员退出，避免父 PID 消失或继承 stdout 句柄造成遗漏。服务 bootstrap 仅在正常成功时显式保留后台进程；`terminateOnFailure=false` 继续原有保留行为，不绑定 kill-on-close Job。原源码取消 fixture 已通过：`output/launcher-tests/75d3b3ba2e394fdd8ca6adf10dcb1a9e/source-build/evidence.json`，32 项含共同检查，持有 backend/child 句柄确认安装返回时已退出且短目录回收；专用命令与服务回归见上表。原生 API 参数合同参考微软 [UpdateProcThreadAttribute](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute) 与 [CreateProcessW](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessw)。这些定向通过不代替最终 e 完整恢复与安装器验收。

## 附录 C：中间 d 的固定图成绩

旧版 `SUITE\fixed-baseline\state\fixed-cold-metrics.json` 与中间 d 对应新版 `SUITE\fixed-current-v2\state\fixed-cold-metrics.json` 均为 `passed`。各自的 `fixed-cold-packages.json` 都有 162 个规范化包名/版本条目，逐项比较无差异。新旧各用独立空依赖缓存、同一冻结依赖图、同一绝对 Python 路径恢复后的 GPU 种子、官方 PyPI/Git 和相同代理；运行串行，新样本在 d 构建完成后开始。以下仅保留真实中间成绩，最终 e 结果以正文为准。

| 正式冷缓存指标 | 旧版 baseline | 新版 current-v2 |
| --- | ---: | ---: |
| 依赖阶段（含旧版独立 Manager 阶段） | 779.8119133 秒 | 255.2093367 秒 |
| ComfyUI 启动（单列） | 37.5868049 秒 | 29.3172815 秒 |
| harness 总用时（含其余校验与启动） | 860.1482818 秒 | 324.3295873 秒 |
| 排除在计时外的种子等准备时间 | 19.102482 秒 | 17.0458599 秒 |
| 最终包名/版本条目 | 162 | 162 |

依赖阶段比值为 **3.0555775246 倍**，用时减少 **67.2729626%**，即约 13 分钟降至 4 分 15 秒。日志分别是 `fixed-baseline/state/fixed-cold-20261030-155525.log` 与 `fixed-current-v2/state/fixed-cold-20261030-162445.log`。旧版分母是 ComfyUI 775.9673061 秒加 Manager 3.8446072 秒，避免遗漏旧版额外验证阶段。

新版准备来源约 45.47 秒；143 个固定文件的下载阶段为 118.44 秒、有效内容 1,010,718,100 字节，本地安装为 27.76 秒。阶段证据在新版日志第 10–12、604 行及 metrics 的 phase 时间。`cold-transfer-observation.json` 记录 143 项已完成，固定包清单包含 polars-runtime-32；当时观察到该包使用 HttpClient，但后续 hot 已复用任务数据库，未保留独立的 cold 逐包 engine 快照。harness 只保存阶段切换，没有持久化该包的 ETag 触发事件；具体 ETag 变化及完整 GET 的直接证据仍来自上一节单文件验证，不能冒充本轮 cold 日志。新版托管 HTTP 实际响应体计数为 1,013,438,893 字节，含此计数器可见的额外传输；它不覆盖来源 metadata 或 pip/Git/uv 子进程。旧版计数器记录 `0` 是因为下载由子进程完成、该计数器看不到，**不是旧版没有网络传输**，两数不得用来计算总带宽节省。

固定图两组都排除生成共同依赖图的首次联合 dry-run，GPU bootstrap 也单列；旧安装过程自身的重复 metadata/安装处理仍计入其依赖阶段。这是相同固定输入的安装机制对比，不是从空 Windows 开始的总时长。测试 harness 使用旧 Core `f05087c1b59d2d5047f6d3c5d1b9e9cb96bec5e5fa26989df1e102d4bd2f5d2e` 与新版 Core `7fbb76b95fe21fd42ed91455ba3aa3cfc0d9f7142ea9ddb7a60baf96fa0ba832`；新版与 d 对应源码一致，但测试构建和最终包内 Core 是不同产物，不能代替最终 EXE 的原生验收。失败的旧 `fixed-current` 样本及单文件修复计时不混入本表。

同两个 label 的 `fixed-hot-metrics.json` 也均为 `passed`，各自 `fixed-hot-packages.json` 的 162 项完全一致。hot 是恢复同一 GPU 种子后保留各自缓存重新安装，不是所有依赖已装好的空操作：

| 中间 d 热缓存指标 | 旧版 baseline | 新版 current-v2 |
| --- | ---: | ---: |
| 依赖阶段 | 157.3521988 秒 | 51.7570539 秒 |
| ComfyUI 启动（单列） | 37.6345968 秒 | 29.7580914 秒 |
| harness 总用时 | 219.2691297 秒 | 104.6689489 秒 |

中间 d 热缓存依赖比值为 **3.0402077967 倍**，减少 **67.1075115%** 用时。日志分别为 `fixed-baseline/state/fixed-hot-20261030-164601.log` 与 `fixed-current-v2/state/fixed-hot-20261030-163048.log`。两边托管响应体计数均为 0，只代表该计数器本轮未读到文件响应体，不证明 pip/Git 子进程绝对没有网络活动。

### 中间 d 的阶段拆分与边界

只读提取脚本及逐区间原始时间、日志行号保存在 `output/dependency-phase-analysis-20261031/{extract.ps1,d-phase-breakdown.json}`。新版按 metrics 的 phase 切换时间相减；旧版按实际 pip 日志边界划分混合窗口。两种粒度不能直接逐行当作相同工作，也不能将日志窗口解释为纯网络传输时间。

| 新版 d 阶段（秒） | cold | hot | 计量边界 |
| --- | ---: | ---: | --- |
| 入口开销 | 0.015 | 0.017 | 依赖阶段开始至首个 phase |
| resolving | 0.142 | 0.145 | 固定图已有环境守卫，不是首次联合求解 |
| preparing | 45.472 | 0.927 | 固定文件来源与准备 |
| downloading | 118.452 | 2.893 | cold 文件传输；hot 包含缓存核验/复用，不能称 2.893 秒网络下载 |
| building | 41.472 | 2.047 | 包含源码获取、构建准备和构建/缓存检查；hot 没有证明重新编译 |
| 安装前 verifying | 0.304 | 0.355 | 本地输入检查 |
| installing | 27.760 | 22.585 | 固定本地 wheel 安装 |
| 最终 verifying | 21.592 | 22.788 | 至依赖阶段结束，含来源、文件收据与环境核验 |
| 完整依赖阶段 | 255.209 | 51.757 | 与各自 metrics 一致；表中显示值经四舍五入 |

| 旧版日志可分辨窗口（秒） | cold | hot | 限制 |
| --- | ---: | ---: | --- |
| 阶段开始至普通 wheel 正文/缓存批次 | 300.174 | 67.800 | 混合 metadata、解析、Git 和 backend 工作，纯 preparing/解析时间不可得 |
| 普通 wheel 正文或缓存处理窗口 | 401.464 | 3.010 | stdout 边界，不是准确网络字节或纯传输时间 |
| 明确 `Building wheels` 窗口 | 3.493 | 不可得 | hot 没有该日志块，不能据此填 0 秒 |
| `Installing collected packages` 至成功 | 69.987 | 80.657 | pip 安装处理 |
| 成功输出至 Manager 阶段 | 0.845 | 0.686 | 阶段转换 |
| 独立 Manager 阶段 | 3.845 | 5.195 | 已计入旧版依赖总时长；纯校验时间不可再细分 |

共同首次联合 dry-run 位于 `current/state/resume-20261030-145713.log` 第 109–419 行，用时 **295.5045331 秒**，不计入上述任一 fixed 组。此前 SAM2 远程获取约 279.39 秒也不属于该 dry-run 区间。旧 cold 的日志可见 Git 远程 clone 窗口约 23.839 秒，已包含在其混合准备窗口中；新版 d cold 从固定源码准备到远程 fetch 完成约 15.205 秒，之后本地 checkout 约 0.625 秒，均包含于 building。最终 e 新增的短目录本地 clone/checkout 只复制已经固定的本地 Git 对象，不能表述为又一次远程下载；e 的实际阶段数字已列于正文。


## 验收现场与后续清理

验收结束时仅清理本次基线切换产生的 11 个 `current/retired-*` 虚拟环境副本，逐项验证绝对路径、任务标记、无重解析点与无运行引用。逻辑文件合计 65.439 GiB；E 盘空闲实测由 28.766 GiB 增至 95.522 GiB。当时完整 `current/ComfyUI/.venv`、GPU seed、源码、各组 runtime/cache、state/log/metrics 等 27 个保留根均复核存在。原证据位置为 `output/dependency-retired-venv-cleanup-20261031.json`。

最终原生候选已通过正常 UI 菜单退出，测试服务与安装子进程均已停止；当时单独 QA 配置保留暂停任务供复测。正常分发的单 EXE 不依赖这些 QA 配置或源码目录。候选目录附带与仓库归档一致的 `ACCEPTANCE.md`，正式文件校验值以 `SHA256SUMS.txt` 为准。

交付使用干净目录 `F:\comfyuishengtu\dist\candidates\dependency-speed-20261031-final`。其中单 EXE、便携 ZIP、Setup 与已验收 e 产物逐字节相同，三个 SHA-256 再次核对通过；仅复制交付文件，未带入 e 的测试专用代理、暂停配置或运行时解包目录。e 目录当时保留原验收现场。原复制证明位置为 `output/dependency-final-delivery-proof.json`；`candidate.json` 的路径已更新到交付目录，包本体没有重新构建或改动。

### 2026-10-31 后续清理与提交检查

按维护者后续要求，已清理本项目的临时测试、构建、解包和验证产物，包括 `output` 中确认的测试目录、a–e 等中间候选、`bin/obj`、浏览器和 Python 测试缓存，以及 E 盘的两个专用验收根 `20261030-acceptance-9f27c8`、`20261031-installer-e-5d986a`。删除前确认绝对路径、归属和无进程引用，移除链接本身后再清理目录，未穿越重解析点。F 盘两轮清理实测释放约 45.51 GiB，E 盘释放约 32.65 GiB，合计约 78.15 GiB。

保留源码及正式回归测试、最终交付目录、历史发行安装包、项目数据、日常入口和开发环境，也保留独特图标素材。关键 metrics、包清单、原生验收摘要、交付哈希和本轮质量检查日志保存在本地忽略目录 `.codex-local/cleanup-20261031`。本文上述 `output`、`SUITE` 及 e 候选位置属于历史取证路径，完整测试现场已经清理，不再表示这些文件仍然可访问。最终 EXE、便携 ZIP、Setup 的 SHA-256 在清理后再次确认不变。

提交前完整执行 `scripts/check_release.py` 并通过：Python 1386 项及 604 个子测试、Node 533 项、Prompt Assistant 补丁 7 项；版本、公开文件、模型档案、静态检查及 JavaScript 语法全部通过。补齐本报告和既有 slimming 验收报告的 `export-ignore`，验收记录保留在 Git 中，按既有规则排除出源码发行归档。
