# Anima Random Studio 1.3.0

本版优化中国大陆下载、新环境安装与环境手动维护，并统一提示词池、收藏和 AI 提示词处理。

## 下载与启动

- **安装版**：下载 `AnimaRandomStudio-Setup-win-x64.exe`，安装后从开始菜单或桌面快捷方式启动。
- **单文件版**：下载 `AnimaRandomStudio.exe` 直接运行；`AnimaRandomStudio-portable-win-x64.zip` 仅包含同一份 EXE。
- 运行依赖内嵌在 EXE 中；ComfyUI、节点和模型在配置环境时另行下载。GitHub 的 Source code 附件为开发源码。

支持 Windows 10 1809 及以上的 x64 系统；新建环境需要支持 CUDA 12.8 的 NVIDIA 驱动（570.65 或更新版本）。

## 下载与环境维护

- 默认中国大陆来源，模型优先 **ModelScope → hf-mirror → Hugging Face 官方**。逐文件保存平台、仓库、该平台的 revision、路径、大小和 SHA-256；没有核对一致副本的来源直接跳过。
- Python、uv、Git、PyPI、PyTorch 与启动器更新包接入统一来源策略；超时、持续低速或校验失败时切源，保留断点，显示实际来源、速度和原因。官方、自定义来源与已有配置继续可用。
- 新装 ComfyUI 解析官方最新正式 Release，排除草稿和预发布；检查阶段固定完整提交及依赖。暂停、重试和重启继续原计划，启动服务不会自动升级。
- 新装及补装缺失节点解析官方默认分支最新完整提交；已有插件和自定义节点提供手动检查、固定依赖、备份更新与回退入口。子模块使用父节点锁定的提交。
- 本体与节点更新前检查源码、服务状态、独立 Python 和空间，备份源码、完整 Python 与配置，保留模型；失败恢复并支持中断恢复。有本地修改或无法安全更新的节点会跳过并说明原因。
- 导入环境保留现有 GPU 包版本；Manager 版本继续遵循目标 ComfyUI 的兼容要求。节点更新和 ComfyUI 更新相互检查回退顺序，避免恢复不匹配的依赖环境。

## 启动参数与依赖修复

- 设置页新增显存模式、预留显存、智能显存、精度、注意力实现和 Manager 开关，以及高级参数编辑、互斥校验和完整命令预览。
- 启动及状态检测统一比较环境、真实 Python 身份与进程参数；同参数可复用，参数不同提示需要重启，外部进程不会自动终止。
- 新安装和显式修复补齐 `manager_requirements.txt`，新环境默认启用 Manager；已有环境可安装并启用。
- 为已验证的 Python、Torch 与 GPU 组合安装锁定的 `triton-windows`，修复 KJNodes 的 `PatchTritonVAE`。可选加速不支持或失败时说明原因，基础生图仍可使用；运行中的环境禁止改包。
- 修复节点依赖完成记录遗漏、Git 超时遗留锁文件的恢复，以及 Windows 节点和子模块的长路径处理。

## 工作台与文档

- Krea 2 自然语言池补齐名称、说明、收藏分组、自定义分组树和导入冲突处理；旧词库迁移前备份，保留条目 ID 与原正文。
- AI 重写和视觉结果携带可入池的名称、说明与正文；收藏可选择目标池并同时入池，手改或局部文本不会错误沿用旧 AI 元数据。
- 单图与随机台共用对应模型档案的收藏；随机台从池内侧栏管理收藏，单图收藏使用紧凑标签并增加取消确认。保留旧收藏快照，修复分组按钮位置及图片关闭动效。
- README 更新为单图台、随机台、启动器、常用参数的展示顺序，并加入新的启动器截图。

## 升级与验证

1. 等待队列空闲，退出旧启动器，并备份 `%LOCALAPPDATA%/AnimaRandomStudio`；旧源码版另备份项目的 `data`。
2. 1.2.0 起可在设置中检查、下载并重启更新；更早版本需手动下载本版。软件更新不自动升级 ComfyUI 或已有节点。
3. 环境更新先检查并固定计划，再停止服务后执行；模型继续复用，存在本地源码修改时按界面提示处理。

本地 C# 回归 821 项通过，包括 81 项节点更新检查；Python 909 项及 500 个子用例、JavaScript 373 项通过。7 个模型约 26.38 GiB 已从 ModelScope 实际下载并通过独立 SHA-256 校验。隔离环境验证了 Manager、PatchTritonVAE 注册、Triton GPU kernel，以及 ComfyUI 更新和回退。

验收边界：官方 Git/Python 无代理冷下载在现场遇到 TLS 连接失败，完整安装与升级使用了已核对的缓存；节点验证包含最新提交解析和真实环境中的故障/中断恢复，未逐一实装全部上游最新提交。不同网络、GPU 与上游节点组合仍需以实际检查结果为准。

`SHA256SUMS.txt` 覆盖 EXE、便携 ZIP 和安装包；`build-info.json` 记录版本及来源提交。EXE 内嵌文件清单和第三方许可，当前包未签署代码签名。

[完整更新日志](https://github.com/159159pzy-crypto/comfyui-random-image-generation/blob/1.3.0/CHANGELOG.md) · [启动器说明](https://github.com/159159pzy-crypto/comfyui-random-image-generation/blob/1.3.0/launcher/README.md) · [依赖说明](https://github.com/159159pzy-crypto/comfyui-random-image-generation/blob/1.3.0/docs/dependencies.md)
