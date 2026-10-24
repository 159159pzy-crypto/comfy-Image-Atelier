# ModelScope 优先与环境维护验收

验收日期：2026-10-24。代码基线为 `6286312d9b7ba01a9c236f56214c121e8f03b11b`，候选包包含本次未提交修改。未发布 GitHub Release。

## 隔离范围与网络条件

全部安装、模型下载、更新与回退使用 `<隔离验收目录>`，ComfyUI 端口为 `18888`，工作台端口为 `18990`。日常 `<日常 ComfyUI>` 没有用于安装或改包。

验收进程设置 `ANIMA_LAUNCHER_DIRECT_NETWORK=1`，禁用 HTTP/SOCKS 环境代理、系统 HTTP 代理及 GitHub 转发下载地址；国内原生镜像仍可使用。这一开关不能绕过系统 TUN。现场另外通过 Mihomo 本地控制器检查实际连接：ModelScope API 与下载 CDN 均命中 `DIRECT`，不是远程代理出站。先前 GitHub 工具下载命中了远程代理，其结果只计入文件校验，不计入无代理网络验收。

网络证据保存在验收目录的 `mihomo-inspection.json`、`mihomo-model-seed-connection.json`。日志没有记录控制器密钥或带签名的 CDN 下载地址。

## 模型下载

Anima、Krea 2、SeedVR2 共 7 个清单文件已实际下载，总计 **28,322,898,652 字节**。生产下载器的最终大小/SHA-256 校验与独立本地 SHA-256 复算均通过，实际来源均为 ModelScope。使用各平台自己的固定提交，未替换量化版本。

| 文件 | 字节数 | 实测平均速度 |
| --- | ---: | ---: |
| Qwen 文本编码器 | 1,192,135,096 | 7.49 MiB/s |
| Qwen VAE | 253,806,246 | 11.58 MiB/s |
| Anima v1 | 4,182,218,328 | 8.89 MiB/s |
| Qwen3 VL 4B FP8 | 5,242,467,968 | 15.03 MiB/s |
| Krea 2 Turbo INT8 | 13,492,686,496 | 约 15.42 MiB/s |
| SeedVR2 3B INT8 | 3,458,259,704 | 约 15.65 MiB/s |
| SeedVR2 EMA VAE FP16 | 501,324,814 | 20.32 MiB/s |

速度为单次现场传输均值，不包含最终哈希复算时间，也不是其他网络的速度保证。Krea 与 SeedVR2 模型的均值按日志中开始校验时的累计秒数计算，计时精度为 0.1 秒。完整逐项报告为 `model-download-direct/final-acceptance.json`。7 个文件通过同盘硬链接放入隔离 ComfyUI 的相应模型目录，避免重复占用 28 GB 空间。

## 安装与运行

隔离新安装解析到官方稳定版 **v0.42.1**，完整提交为 `63be2f9b82ad0dac88a7ceaa28a9cf3154125a18`。Python 为 3.11.9，Torch 为 2.11.0+cu128，显卡为 RTX 4060 Laptop。

新安装日志确认 `pip check` 无损坏依赖、CUDA 可用。实际启动使用 `--enable-manager`，Manager 版本端点返回 **V4.2.2**，必需节点及 `PatchTritonVAE` 已注册。`triton-windows==3.6.0.post26` 的真实 GPU 向量加法 kernel 通过。可选加速不支持或安装失败时只报告具体原因，不阻断基础环境。

主要运行证据位于 `ComfyUI.launcher-state/install.log` 与 `ComfyUI.launcher-state/live-update-acceptance.log`。

实际 ComfyUI 更新事务从 v0.42.0（`355cd6b14c92c19ac3188fe528e47a3f21ef5178`）开始：更新后注入健康检查故障并自动回退、再次成功更新、手动回退、最终重新更新到 v0.42.1 均通过。逐项核对 Python 已安装包版本、用户配置与 Manager 数据库、根目录配置及注入故障文件的恢复；模型大小和 NTFS 文件身份保持不变。最终验收服务停止。证据为 `stage-old-local-cache.out.log`、`exercise-local-cache.out.log` 和上述事务日志。

官方 GitHub 直连 fetch 在现场超时，因此上述事务使用日常仓库的**只读、已核对完整 SHA 的本机 Git 对象缓存**，没有修改日常环境，不能计为官方网络冷启动成功。SAM2 固定提交改为带包名的 VCS 依赖；清空 Git PATH、使用 `--no-index` 的独立检查确认可以复用相同提交的缓存 wheel。

## 最新插件与自定义节点

全新目标 `ComfyUI-latest-preview` 的真实检查通过生产代码解析了 11 个官方节点的默认分支最新完整提交，并持久化到安装计划。相对原清单有 5 个新提交：AnimaTools、rgthree、EasyUse、KJNodes、LoraManager。Impact Pack 的默认分支大小写 `Main` 被保留。证据为 `latest-preview.log`、`ComfyUI-latest-preview.launcher-state/frozen-node-versions.json`。这是最新版本解析与固定计划验收；原已安装环境续装仍遵循其原先固定的节点版本，不会悄悄升级。

环境页新增节点检查、固定依赖、备份更新和回退入口，展示当前/目标提交及跳过原因。节点更新预检全部目标 requirements，保留当前 ComfyUI 和 GPU 包版本，备份节点完整目录、独立 Python 与配置。子模块跟随父提交的 gitlink；本地修改、目录链接及不安全来源被阻止。已支持清单内节点的安装脚本继续由 Installer 执行，未知用户节点的任意 `install.py` 不自动执行。Manager 版本遵循目标 ComfyUI 的 `manager_requirements.txt`。

节点事务的本地测试覆盖准备阶段不改现有源码、安装/健康检查失败恢复、用户未跟踪文件保留、GPU 包漂移回退，以及 ComfyUI 与节点按相反顺序回退时的阻止。新增用例专门覆盖健康启动时修改 GPU 包或节点提交后的回退，以及超过 260 字符的跟踪文件路径。全量回归暴露的 Git 长路径遗漏已修复：节点 clone、checkout、fetch、状态检查及子模块操作统一使用进程级 `core.longpaths=true`，不修改全局 Git 配置。

真实节点事务在上述隔离 ComfyUI 中加入一个仅用于验收的本地 Git 节点，实际调用完整依赖解析、Installer、6.4 GiB Python 备份及 ComfyUI 启动。revision 1、revision 2 均成功注册，Manager、15 个必需节点与 Triton kernel 通过；随后注入故障，自动完整回退通过。第二轮为遵守 DIRECT 窗口期限在 20:15:14 主动停止，之后使用纯本地恢复入口完成中断恢复，核对全部 162 个包记录、节点目录字节、用户配置、ComfyUI SHA 和 7 个模型身份，最终服务停止，验收节点已禁用。

证据为 `node-fixture-live.out.log`、`node-fixture-local-restore.out.log`、`node-fixture-summary.json`。这证明生产节点事务在真实 Python 环境中的安装、健康检查与失败/中断恢复；没有宣称 5 个有新提交的上游节点已全部实际升级。节点成功提交后的手动回退由本地事务测试覆盖，真实第二轮没有完成该环节。ComfyUI 本体的真实成功更新及手动回退则已完成。

## 无代理工具下载边界

经用户授权，19:16:50 起临时为 33 个隔离验收进程添加精确进程路径 DIRECT 规则，原 TUN 与模式保持不变。GitHub 探测确认 DIRECT。实际冷下载 uv 0.12.16 成功：17,818,061 字节，318.407 秒，约 0.053 MiB/s，SHA-256 与可执行版本检查通过。MinGit 两轮与 Python 两轮均在开始传输前发生 TLS 握手/连接 EOF；没有偷偷改走代理。这两项冷下载未通过，不能把使用已有工具缓存的完整安装称为纯冷下载成功。详细报告为 `bootstrap-verified/final-acceptance.json`。

20:15:38 已主动结束临时窗口并恢复原 522 条规则，模式、TUN、选择器及禁用状态均与修改前一致，原配置文件字节未变。owner 与 watchdog 已退出，含凭据的三个临时备份已删除。独立复核见 `network-window/restored.json`、`network-window/restoration-cleanup.json`。后续只进行本地测试、恢复及离线构建。

## 自动化与界面

最终完整 C# 回归为 **821 项通过**，按相同 r6 产品代码分段执行：已完成的前五组 215 项，修正新增长路径测试的 CRLF 断言后，从完整节点组续跑至程序末尾的 606 项，退出码 0。节点更新组的 **81 项**全部通过，包含 300 字符源码路径、子模块固定提交、健康启动后依赖漂移检测、手动回退和中断恢复。测试使用本地 Git/HTTP fixtures 与离线构建，未改全局 Git 配置。

汇总为 `r6-full-suite-result.json`；前段日志 `r6-completed-prefix.log` 精确保留原运行中完成的五组，后段为 `r6-resume-suite.log`。原失败日志 `r6-full-suite.log` 也保留供审计。覆盖来源顺序、平台 revision、错误哈希、限流、跨源断点与凭据隔离、包内文件完整性、稳定版本及手动更新计划持久化、联合依赖解析、进程参数等价/差异、真实 Python/虚拟环境身份、外部进程保护，以及完整 Python/源码/配置回退。

Python 回归 909 项及 500 个子用例通过；发布检查中的 JavaScript 回归 373 项通过，补丁检查 7 项通过；Ruff、Python 编译、前端语法及模型清单一致性检查通过。

原生 WinUI 已检查浅色、深色、846×813 窄窗口与 1146×643 矮窗口。验证常用/高级启动参数、完整命令预览、保留参数错误提示、保存后的配置回读，以及环境维护页的稳定版检查。修复维护页导航遗漏及 `WrapGrid` 布局异常，滚动后保存控件可访问。

原生界面复核证据位于 `ui-final/`：`environment-r3.jpg`、`arguments-r3.jpg`、`preview-r3.jpg`，以及新增节点功能的 `nodes-light-r4.jpg`、`nodes-dark-narrow-r4.jpg` 和 `nodes-final-r5.jpg`。新增面板在 838×813 深色窄窗口中仍能显示按钮、版本和状态。计划恢复界面使用真实生产事务计划的隔离副本，并将依赖状态设为待固定；没有通过这个界面副本执行更新。r4、r5、r6 的界面源码一致；r5 增加健康启动后的二次完整性核验，r6 补齐节点 Git 长路径支持。

## 本地候选包

最终交付为 `candidate-modelscope-r6`，单文件 EXE、仅含同一 EXE 的 ZIP、Setup 校验和及内嵌文件清单验证通过。版本为 `1.2.0`，`sourceDirty=true`，未安装到日常入口。使用本机 NuGet 缓存离线构建。

| 文件 | SHA-256 |
| --- | --- |
| `AnimaRandomStudio.exe` | `ebd1554486c383d68efb797f3fb4c325ffc090b295ac6479dedf39397b17049f` |
| `AnimaRandomStudio-portable-win-x64.zip` | `93be4da503202e080b6dd33b9e219846dfe4aa47d1ca7ae64ce5c33cf8228ff8` |
| `AnimaRandomStudio-Setup-win-x64.exe` | `b0b5a5d469f0cf2933cd44b27b0d13935036ab9cb17a1954b119faf920dbe5e6` |

同一打包流程的 r2 已完成独立 AppId 的实际安装/卸载验收，9 项全部通过：安装 EXE 字节一致、运行锁、待更新状态、降级阻止、同版修复、卸载后用户数据保留。日志为 `installer-r2-acceptance.log`。r6 另外完成包校验和实际启动；安装器流程此后未修改。

