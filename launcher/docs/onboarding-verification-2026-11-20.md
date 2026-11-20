# 首次安装与接入问题核对表（2026-11-20）

本次核对的基线为 1.7.0、提交 `602e74f`。下表覆盖报告的 65 个编号和 9 个验收场景。「已修」表示已改动当前工作区代码；「原有保护」与「部分成立」说明报告中的现象需要限定条件。实测范围单独列于末节。

## 环境版本、第三方节点和依赖

| 编号 | 结论 | 行为与证据 |
| --- | --- | --- |
| A-01 | 已修 | 新装脸部细化使用清单内已验证的 Forbidden Vision 提交；已有节点继续按来源身份和补丁契约检查。见 `InstallationPlanner.ResolveInstallVersionsAsync` 和 `ComfyReleaseUpdateTests`。 |
| A-02 | 条件成立，已修恢复 | 增加 httpcore 本身不必然出错。中断留下的计划外包须在重新检查中核对并确认；保护原包、GPU 与解释器身份。见 `InstallationDependencyRecoveryTests`。 |
| A-03 | 已增加接入方式 | 可复用整合包自行启动的 ComfyUI，工作台 Python 安装在独立位置；需要补齐插件时显式选择修复。第三方整合包的完整版本矩阵仍待实机验收。 |
| A-04 | 部分成立，已修 | 只读连接从实际服务的设备信息区分 CUDA、ROCm、XPU、CPU。原整合包自行启动时沿用其环境变量。各计算后端的真实推理见 C-03。 |
| A-05 | 部分成立，已修 | 原先已有 Git 和官方跳转回退。当前 HTTP 与 Git 读取向导的代理快照；固定提交的依赖支持官方 API 回退和缓存。首次无法读取官方元数据时可采用内嵌清单固定的提交、依赖。见 `ComfyReleaseUpdateTests`。 |
| A-06 | 已修外部环境隔离及诊断 | 外部环境使用单独的工作台 Python。托管解析仍保护依赖契约；冲突输出保留 pip 原因和节点约束来源。见 `ExternalWorkbench` 和 `Installer`。 |
| A-07 | 部分成立 | Manager 仍可按配置启用。已有源码改动继续受保护，安装报告目录和提交，并引导检查或接入已有环境。见 `Installer` 的仓库身份检查。 |
| A-08 | 保留约束，改善诊断 | 托管节点安装脚本仍遵守已确认的包与 GPU 版本，冲突提示节点、约束文件和 pip 诊断。未开放第三方脚本静默改写冻结版本。 |
| A-09 | 已提供只读路径 | 默认连接已有环境使用独立工作台和已有服务。明确允许修复时，先为独立 Python、所选节点及配置建立可恢复备份，再写入目标。恢复说明列明原目录和备份清单。见 `ExternalRepairBackup` 与 `InstallationDependencyRecoveryTests`。 |
| A-10 | 已修诊断 | 同时发现多个候选或发现修改过的补丁源码时，显示具体冲突位置。保留用户副本，要求明确处理。见 `ForbiddenVisionCompatibility`。 |
| A-11 | 已修接入检查 | 只读路径核实运行服务的身份、设备和加载的类；新式修复导入对现有核心模块执行实际 import。历史环境的轻量检查仍保留。见 `SetupImportReview`。 |

## 启动、网络与恢复

| 编号 | 结论 | 行为与证据 |
| --- | --- | --- |
| G-01 | 已修 | 读取代理快照、创建窗口与更新恢复不再因 PAC 中断。托管安装在检查页要求选择明确网络模式，保留系统代理策略。见 `NetworkPolicySnapshot` 和 `LauncherUpdates`。 |
| G-02 | 已修 | 向导高级设置可选择网络模式和 HTTP 代理；草稿、元数据检查、已确认安装和下载使用选定配置。下载页更换设置会使相关未确认结果失效。 |
| G-03 | 已修 | 完成页可返回概览稍后处理；单独保存暂停状态及原环境，之后可以重新进入。见 `SetupRouting` 和 `LauncherState.SuspendSetup`。 |
| G-04 | 已修 | 完成页可停止本轮新启动的服务，按进程身份区分原有、外部和本轮进程。见 `LauncherState.SetupReadiness`。 |
| G-05 | 已修 | 返回编辑、仅访问未改动的草稿、真正切换环境分别处理；恢复优先考虑待确认选择。见 `SaveSetupDraft` 和 `SetupRouting.Resolve`。 |
| G-06 | 已修 | 完成页保存可选失败状态，在当前步骤重试或跳过；重启可恢复原位置。见 `OptionalReviewAtCompletion`。 |
| G-07 | 已修 | 同一次快速连击不会把刚启动的安装或下载立即暂停。见 `SetupPage.Primary_Click`。 |
| G-08 | 已修 | 检查时可主动取消，取消令牌传入网络与检查工作，回到可编辑或重新检查状态。 |
| G-09 | 已修 | 模型列表与自动运行检查复用当前会话的文件校验记录。清单哈希、路径、大小、创建或修改时间变化会使记录失效。下载验收及显式完整性检查读取文件并核对 SHA-256。见 ModelPackageTests。 |
| G-10 | 已修 | 启动探测状态目录写入，日志写入失败降级到临时目录；原始异常仍能显示。见启动器 `Program.Log` 与 `Main`。 |
| G-11 | 已修 | 普通启动竞争更新互斥锁时，在短时间内提示更新进行中；更新协调器继续运行。见 `RouteInstalledVersion`。 |
| G-12 | 部分处理 | 本地现有发布 EXE 的 Authenticode 状态为 NotSigned；文档说明官方来源、SHA-256 和保护历史记录，权限失败提供排查提示。证书签名、SmartScreen 信誉和杀软实机隔离尚未验证。 |
| G-13 | 部分成立 | 原代码已在服务实际启动并通过就绪检查后记录成功。浏览器关联异常单独处理，保存服务已就绪状态。见 S6-01。 |
| G-14 | 已修 | 位置字段变动先更新内存，延迟写入；切页和退出前刷新。未改变草稿时保留确认结果。 |
| G-15 | 已修恢复提示 | 未开始安装的检查授权在重启后仍须重新检查，恢复时说明原因。见 `InitializeSetup`。 |
| G-16 | 已修 | 外层等待覆盖取消、保存与子任务排空。超过 20 秒后可再次选择退出并确认立即退出；保存工作在后台继续。见 `MainWindow.Exit`。 |
| G-17 | 已补文档 | 说明单文件 EXE 的用户临时目录要求及启动前配置的 `DOTNET_BUNDLE_EXTRACT_BASE_DIR`。见启动器 README。 |
| G-18 | 原报告范围过宽 | 已有 Core 路由和模型检查自动化，本次补充恢复、配置、资源修复等回归。完整 WinUI 按钮组合和真实设备覆盖见 C 区。 |

## 六步向导

| 编号 | 结论 | 行为与证据 |
| --- | --- | --- |
| S1-01 | 支持边界，改善入口 | NVIDIA 托管新装保留最低驱动条件。其他显卡可查看官方安装指南并连接已运行环境。 |
| S1-02 | 部分成立 | 发现失败会区分 nvidia-smi 超时与检测错误并可重试；仍需通过硬件检查才可托管新建。 |
| S2-01 | 已修 | 同时有 main.py 和 folder_paths.py 的目录在规划阶段被拦截，可携带所选路径进入导入。普通安装父目录及旧直接根续装保留。见 `OnboardingTests`。 |
| S2-02 | 已修 | 显式解释器不存在时报告该路径，自动发现仍寻找 .venv、venv 和便携版解释器。见 `Inventory.FindPython`。 |
| S2-03 | 已修 | 主窗口首次激活时检查保存的 ComfyUI 和运行时盘符，离线时显示具体盘符及重新连接／改选位置操作提示。位置页同时处理离线盘和 UNC。已模拟缺失盘符，物理拔盘未实测。 |
| S2-04 | 已修 | 检查外部工具实际使用的路径，pip 使用短工作目录，并从同盘短路径读取经过哈希核对的长路径 wheel。带空格和长缓存路径的真实安装见 `DependencyInstallationTests`。 |
| S2-05 | 已修 | 区分可解析配置中的非法字段与无法解析的整份文件，先备份，尽量保留有效环境绑定，在窗口显示备份路径和恢复情况。 |
| S2-06 | 已修 | 本机使用完整 UNC 根构造 DriveInfo 可复现 ArgumentException。现单独说明网络共享及映射盘路径，原始输入保留。 |
| S2-07 | 已补说明 | 界面列出第三方镜像和 Python 索引，提供官方来源选项。保留已有用户首选来源。 |
| S3-01 | 原有保护 | preflight 为空且规划失败时，`UpdateIssueActions` 已显式恢复面板并添加「检查目录与 Python」；错误栏保留原始原因。 |
| S3-02 | 已修 | Review 显示检查结论，包括已固定的版本与接下来的复用行为。 |
| S3-03 | 已修 | 选中 VSR／DLSS 时，运行库探测未识别受支持设备，会在检查页给出可取消选装的提示。 |
| S3-04 | 已修 | 要修改的环境在确认前检查服务、端口和解释器占用，提示检查队列后关闭；安装时再检查以防状态变化。只读接入可复用外部服务。 |
| S3-05 | 原有保护 | 已有「实际路径待确认」条目，先按环境盘估算，模型准备阶段重新解析并按实际卷检查可用空间。见 `InstallationPlanner.PreflightAsync` 和 `CheckModelSpaceAsync`。 |
| S3-06 | 已修 | 普通环境的计划和安装页共用阶段定义，包含环境依赖步骤；只读接入单独列出其实际动作。 |
| S3-07 | 已修 | 预检返回核验后的计划，确认页据其区分可复用内容与需重下的损坏暂存文件。有异常时显示待确认。 |
| S4-01 | 已修 | 使用安装检查点比对中断后的新增包，在新 review 经确认后采用相同现场；发生版本替换或 GPU 变化时要求处理。见 `InstallationDependencyRecovery`。 |
| S4-02 | 已修 | 安装任务身份排除磁盘瞬时计数，注册和恢复会核对原有 owner，防止多次继续重复执行同一计划。见 `DownloadTaskService`。 |
| S4-03 | 已修 | 区分连接失败、需要重新确认的计划和需要处理的本地修改；确定性错误不会无限作为同一操作继续重试。 |
| S4-04 | 已修重试路径 | 确认、恢复时将来源和代理绑定到本次计划；下载页支持显式按新来源继续。见 `DownloadOwnerRecovery` 与环境维护入口。 |
| S4-05 | 已修 | 解析或安装依赖失败时，阶段标题显示经脱敏的真实错误，取消操作显示暂停。见 `DependencyProgressTests`。 |
| S4-06 | 已修 | 明确可选失败可以是功能或模型，展现运行库等可选项，并可跳过。 |
| S4-07 | 已修新租约 | 清理超过 24 小时且拥有有效标识、活动租约已释放的同环境临时目录。无法证明所有者、运行中及链接目录会保留。 |
| S5-01 | 已修 | 保存服务与工作台可用状态分开。依据同环境工作台的重写能力检查判断就绪，缺模板可添加。 |
| S5-02 | 已加入口 | 向导可进入助手准备及设置。只读外部连接需由环境所有者准备相关节点或显式选择允许补齐。 |
| S5-03 | 已改诊断 | 根据节点目录与运行时可用性说明已安装未加载或缺失。重写与图像反推能力分别检查。 |
| S5-04 | 已修 | 前往日志、下载再返回保留本次助手表单；操作不把密钥写入向导草稿。 |
| S5-05 | 已修 | 历史记录缺少检测结果时显示「未检查」，避免宣称已配置。 |
| S6-01 | 已修 | 服务就绪与浏览器启动分别处理，显示本地服务地址并可复制。 |
| S6-02 | 已加入口 | 用户可主动提交测试图，按返回的批次 ID 查看状态，工作台队列忙时跳过此次提交。实际 GPU 覆盖应区分用户流程和独立工作台调用。 |
| S6-03 | 已改诊断 | 进程准备超时显示具体服务日志路径；运行检查保留阶段和诊断。未逐个构造全部底层 IO 异常。 |

## 工程与验收

| 编号 | 结论 | 行为与证据 |
| --- | --- | --- |
| D-01 | 已清理 | 删除恒为 false 的自动补齐路由与不可达动作。升级仍可进入明确的修复检查流程。 |
| D-02 | 文档差异成立 | 旧日期记录描述当时验收的按钮，当前行为以六步向导、完成页的返回概览和运行检查为准。历史通过范围见下述记录。 |
| D-03 | 原有保护 | `ArtifactCache.PathFor` 已按 SHA-256 和原始文件名存放归档，旧缓存仅在内容或断点身份一致时采用。 |
| D-04 | 已修 | 比较内嵌资源哈希及 payload.zip 成员。普通启动可补回缺项或原子替换损坏项，保留原字节备份；缓存维护中的恢复继续走原有保护。见 `BundledResourceRepair`、`OnboardingTests`。 |
| D-05 | 部分覆盖 | 新增具体的依赖检查点恢复、任务身份、配置恢复与完成流程断言。机器整段断电、所有按钮序列和所有真实下载组合尚未验证。 |
| C-01 | 待实机 | 不以默认 DPI 窗口代替多 DPI、高对比度和关闭透明效果的组合验证。 |
| C-02 | 待实机 | 空安装目录可模拟首次安装，纯净 Windows 和缺少 VC++ 的真实机器仍需独立验收。 |
| C-03 | RTX 4060 Laptop 实机通过 | RTX 4060 Laptop 通过 CUDA 生成批次 `c0922191f671`。ROCm、XPU、CPU 的后端识别已有测试，RTX 30/50、AMD 和 Intel 的设备推理仍待实机。 |
| C-04 | 复用模型的首图通过 | 隔离向导复用已有模型，取得 243,850 字节的 PNG。提交前对比外部环境 3,441 个目录项和全部文件的 SHA-256。模型冷下载、中断和磁盘异常由夹具覆盖，1.5.1 的隔离冷装结果见原记录。 |
| C-05 | 本版实机通过 | 完成页打开 Edge 并连接 19997，工作台返回 HTTP 200。通过菜单停止自有服务并退出，19992 的外部 ComfyUI 仍可访问。从概览再次启动后，配置、批次和 PNG 均与退出前一致。 |
| C-06 | 待实机 | 选装 Krea 2 及全部本地反推模型未逐项完成真实下载与推理。 |
| C-07 | 部分覆盖 | 隔离 Windows 注册表实测 PAC 下初始化、诊断、直连和自定义代理。UNC 已复现；真实企业 PAC 网络、移动盘物理拔插与安全软件隔离仍待实机。 |
| C-08 | 待实机 | 尚需按实际 WinUI 页面逐步核验跟随系统主题、全程 Tab 顺序和 Esc／Enter。 |
| C-09 | 部分覆盖 | 状态写入探测与日志降级已有代码；.NET 首次解包前 TEMP 受限、真实磁盘写满等仍应独立验收。 |

## 编号证据索引

表中注明是可执行测试、历史实测，还是本次核对的实现。仅有实现或历史记录的场景，其实机范围以上表结论为准。

| 编号 | 复查入口 |
| --- | --- |
| A-01、A-05 | [安装时固定节点提交](../Anima.Launcher.Core/InstallationPlan.cs#L203) · [固定提交、离线依赖与网络回退测试](../Anima.Launcher.Tests/ComfyReleaseUpdateTests.cs) |
| A-02、A-08 | [节点安装器改动依赖后强杀、异常与恢复测试](../Anima.Launcher.Tests/InstallationDependencyRecoveryTests.cs#L23) · [托管依赖约束与冲突](../Anima.Launcher.Core/Installer.cs) |
| A-03、A-04、A-06、A-09、A-11 | [运行中外部实例、设备类型、只读边界测试](../Anima.Launcher.Tests/ExternalConnectionTests.cs) · [独立工作台准备](../Anima.Launcher.Core/ExternalWorkbench.cs) · [显式修复备份测试](../Anima.Launcher.Tests/InstallationDependencyRecoveryTests.cs#L89) · [导入解释器检查](../Anima.Launcher.Tests/SetupImportTests.cs) |
| A-07、A-10 | [受管仓库修改提示](../Anima.Launcher.Core/Installer.cs#L184) · [已有节点、补丁兼容及冲突测试](../Anima.Launcher.Tests/ForbiddenVisionCompatibilityTests.cs) |
| G-01、G-02 | [Windows 隔离注册表启动与实际 HTTP 请求](../Anima.Launcher.Tests/PacStartupTests.cs) · [PAC 和网络设置快照](../Anima.Launcher.Core/NetworkPolicy.cs) · [安装来源设置测试](../Anima.Launcher.Tests/InstallationSourcePreferenceTests.cs) · [依赖下载网络测试](../Anima.Launcher.Tests/ArtifactNetworkTests.cs) |
| G-03、G-05、G-06、G-13、G-15、D-01 | [变更草稿、可选失败及恢复路由测试](../Anima.Launcher.Tests/OnboardingTests.cs#L110) · [浏览器失败后落盘，重开概览并恢复完成页](../Anima.Launcher.Tests/OnboardingTests.cs#L319) · [恢复时的提示](../Anima.Launcher.WinUI/Shared/LauncherState.Setup.cs#L37) |
| G-04 | [记录向导启动的服务](../Anima.Launcher.WinUI/Shared/LauncherState.SetupReadiness.cs) · [停止时核对拥有的进程身份](../Anima.Launcher.Core/Processes.cs#L633) |
| G-07、G-08、G-14 | [开始、暂停连击与返回操作](../Anima.Launcher.WinUI/Pages/SetupPage.xaml.cs#L850) · [延迟写盘和退出刷新](../Anima.Launcher.WinUI/Shared/LauncherState.Setup.cs#L149) · [审查版本与可取消检查](../Anima.Launcher.Core/Onboarding.cs#L261) |
| G-09 | [完成页自动检查和显式复核](../Anima.Launcher.WinUI/Shared/LauncherState.SetupModels.cs#L84) · [单文件与模型包校验缓存测试](../Anima.Launcher.Tests/ModelPackageTests.cs) · [模型就绪与下载前预览测试](../Anima.Launcher.Tests/SetupCompletionTests.cs) |
| G-10、G-11、G-12、G-16、G-17 | [启动目录检查及诊断](../Anima.Launcher.WinUI/Program.cs#L319) · [日志降级](../Anima.Launcher.WinUI/Program.cs#L247) · [版本协调](../Anima.Launcher.WinUI/Program.cs#L137) · [退出等待及用户选择](../Anima.Launcher.WinUI/MainWindow.xaml.cs#L380) · [TEMP、签名与安全历史说明](../README.md#L9) |
| G-18、D-05 | [安装阶段暂停五次、恢复同一任务并验证最终文件](../Anima.Launcher.Tests/InstallationPauseTests.cs) · [实际子进程异常退出后重读依赖检查点](../Anima.Launcher.Tests/InstallationDependencyRecoveryTests.cs) · [完成页恢复](../Anima.Launcher.Tests/OnboardingTests.cs#L319) |
| S1-01、S1-02 | [欢迎页硬件检测与官方指南入口](../Anima.Launcher.WinUI/Pages/SetupPage.xaml.cs#L225) · [支持判定与具体诊断](../Anima.Launcher.WinUI/Shared/LauncherState.Setup.cs#L643) |
| S2-01、S2-02、S2-03、S2-05、S2-06 | [已存在的 ComfyUI、手动 Python、离线盘、UNC 与逐字段配置恢复测试](../Anima.Launcher.Tests/OnboardingTests.cs#L28) |
| S2-04、S2-07 | [长路径预检](../Anima.Launcher.Core/InstallationPlan.cs#L656) · [来源和路径编辑界面](../Anima.Launcher.WinUI/Pages/SetupPage.xaml) |
| S3-01、S3-02、S3-07 | [检查结果、复用与重下摘要](../Anima.Launcher.WinUI/Pages/SetupPage.xaml.cs#L389) · [检查失败时整改按钮](../Anima.Launcher.WinUI/Pages/SetupPage.xaml.cs#L677) |
| S3-03、S3-04、S3-05、S3-06 | [阶段与额外模型路径规划](../Anima.Launcher.Core/InstallationPlan.cs#L306) · [运行端口、硬件和实际卷预检](../Anima.Launcher.Core/InstallationPlan.cs#L647) · [运行库条件规划测试](../Anima.Launcher.Tests/RuntimeArtifactPlanningTests.cs) |
| S4-01、S4-02、S4-04 | [依赖检查点异常与重试](../Anima.Launcher.Tests/InstallationDependencyRecoveryTests.cs) · [同一计划真实暂停、续传及文件发布](../Anima.Launcher.Tests/InstallationPauseTests.cs) · [恢复时来源与确认计划的测试](../Anima.Launcher.Tests/DownloadOwnerRecoveryTests.cs) |
| S4-03、S4-05、S4-06、S4-07 | [安装失败分类与恢复](../Anima.Launcher.WinUI/Shared/LauncherState.cs#L300) · [依赖进度与失败详情测试](../Anima.Launcher.Tests/DependencyProgressTests.cs) · [跳过可选项](../Anima.Launcher.WinUI/Shared/LauncherState.SetupExtras.cs#L96) · [临时目录与租约处理](../Anima.Launcher.Core/InstallationTemporaryDirectory.cs) |
| S5-01、S5-02、S5-03、S5-04、S5-05 | [助手能力、保存与修复](../Anima.Launcher.WinUI/Shared/LauncherState.SetupExtras.cs#L113) · [页面离开时的会话处理](../Anima.Launcher.WinUI/Pages/SetupPage.xaml.cs#L75) · [历史状态与只读助手步进测试](../Anima.Launcher.Tests/OnboardingTests.cs#L90) · [助手接口测试](../../tests/test_prompt_assistant.py) |
| S6-01、S6-02、S6-03 | [浏览器打开失败回归](../Anima.Launcher.Tests/OnboardingTests.cs#L319) · [试生成批次、忙碌及模型配置测试](../../tests/test_server.py#L1036) · [离页只取消轮询测试](../Anima.Launcher.Tests/SetupCompletionTests.cs) · [服务就绪和缺项测试](../Anima.Launcher.Tests/InstallationReadinessTests.cs) |
| D-02、D-03、D-04 | [当前向导说明](../README.md) · [旧版完成页实测记录](../../docs/launcher-onboarding-style-acceptance-2026-10-22.md) · [缓存按 SHA 定位](../Anima.Launcher.Core/ArtifactCache.cs#L15) · [资源被改写后的修复测试](../Anima.Launcher.Tests/OnboardingTests.cs#L61) |
| C-01、C-08 | [历史高 DPI、高对比度、主题及键盘实测范围](../../docs/launcher-onboarding-style-acceptance-2026-10-22.md#L101) |
| C-02、C-03、C-04、C-05、C-06 | [本轮 WinUI、RTX 首图与重启记录](onboarding-evidence-2026-11-20/acceptance.json) · [历史设备、冷装、原生首次打开工作台、真实出图与未覆盖分支](../../docs/onboarding-1.5.1-validation.md#L32) |
| C-07、C-09 | [代理与下载来源测试](../Anima.Launcher.Tests/ArtifactNetworkTests.cs) · [受限磁盘与预留测试](../Anima.Launcher.Tests/DownloadDiskSpaceTests.cs) · [启动前 TEMP 受限场景说明](../README.md#L9) |

**复现恢复专项**　在仓库根目录以 `dotnet run --project launcher/Anima.Launcher.Tests -c Release -- --onboarding-only` 执行浏览器异常、落盘恢复和离页停止轮询的回归。另运行 `--onboarding-five-pauses` 需要 Windows x64、通过托管新装检查的 NVIDIA 驱动和 PATH 中的 Git。该专项每轮重新打开任务库，并通过真实 `Installer.Install` 执行工具阶段，使用固定的本地 HTTP 响应夹具；第五次暂停后续传并校验发布工具归档，随后在克隆仓库前结束。

## 本轮验证

- Windows 启动器的 .NET 默认回归套件通过 2,017 项。最终源码增加了数据目录和目录联接的只读边界，随后重新运行向导专项，并重新打包完成下述首图与重启实测。
- 隔离的 Release 构建通过，向导 396 项、模型文件与模型包 48 项、ComfyUI 发布与恢复 104 项、节点发布识别 30 项、节点维护 213 项通过。显式完整性测试能发现大小、时间戳均保持原值的文件损坏。只读接入测试还覆盖旧工作台数据目录位于外部环境内的情形，包括穿过 Windows 目录联接访问同一位置。
- 从 WinUI Release 输出的 Core DLL 读取嵌入资源 `Anima.Launcher.Core.comfy-pinned-requirements.json`，其 SHA-256 为 `D07BD9657FA5C3F5851A8F916B5E2030C7614EF6FF75F558411794E73AD93B47`，与仓库源文件一致；程序集包含 `BundledResourceRepair`。修复以受信内嵌资源和 payload.zip 内容作为基准。
- 历史原生向导、冷装及实际生成的设备和版本均记录在历史验收中。[1.5.1 隔离验收](../../docs/onboarding-1.5.1-validation.md) · [2026-10-22 WinUI 样式验收](../../docs/launcher-onboarding-style-acceptance-2026-10-22.md)

### RTX 4060 Laptop 首图和重启

交付候选包为工作区构建的 1.7.0，文件位于隔离验收根目录下的 `accepted/AnimaRandomStudio.exe`，SHA-256 为 `db1da0ed8cc28ea5f6a60bddc2dcec3b6f7da06f6c461fc0335bd54abc83c8b6`。本轮使用全新的 `state-accepted` 和 `studio-accepted`。工作台监听 19997，使用独立 Python；外部 ComfyUI 监听 19992，使用原先指定的解释器，将图像保存到隔离输出目录。[候选包及运行证据](onboarding-evidence-2026-11-20/acceptance.json)

- Windows 原生向导选中已有环境、保持只读，在助手页选择「稍后配置」。从导入前到完成页提交试生成之前，3,441 个目录项路径一致，全部文件 SHA-256 一致。先前一轮故意缺少 KJNodes 和 LoRA Manager，试生成显示缺失节点名，补齐隔离夹具后重新试生成成功。
- Anima 档案复用了本机已有的基础模型、Qwen 编码器和 VAE。完成页「试生成一张」提交 4 步、512 × 512 的单图，批次 `c0922191f671` 返回 completed、1/1。ComfyUI 输出文件、工作台图片接口与重启后的接口响应逐字节一致。PNG 为 243,850 字节，SHA-256 为 `caeca1e54615f0ebe21ab2bb63dfbed009e33bb31ec2f7083d6ef4e7b5b93e88`，已完整解码。[真实首图](onboarding-evidence-2026-11-20/first-image.png)
- 从完成页打开工作台后，检测到 Edge 连接 19997，首页返回 HTTP 200。通过「停止服务并退出」结束启动器及自有工作台，确认外部 ComfyUI 仍在线。再次打开 `accepted` 下的 EXE 后，启动器转到同一状态目录内哈希一致的已登记副本。从概览启动工作台，已完成状态、独立解释器、批次 ID 和图像内容均通过检查。验收结束后两个隔离端口均已释放。[复测脚本](../scripts/Test-OnboardingExistingEnvironment.ps1)
- 基于真实 pip 的依赖安装专项 157 项通过。Windows PAC 专项 10 项通过，独立子进程在隔离 HKCU 中读取实际 AutoConfigURL，确认安装前拦截、两种替代网络设置与父进程代理状态。连续五次暂停专项中每次重开均复用同一安装任务，最终通过归档 SHA-256 并完成工具阶段。
- 源码发布内容检查覆盖 613 个文件，检查时排除了工作区原有的本机对比素材目录 `artifacts/comparisons/`。构建脚本核验 EXE、便携 ZIP 和安装包的 SHA-256。此候选为未签名的本机构建。
- Python 全量回归为 2,361 项通过、63 项跳过，另有 826 个子测试通过。缺少的专用硬件、完整冷下载、全新 Windows、高 DPI 及发布签名范围见 C-01 至 C-09 和 G-12。
