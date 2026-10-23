# 依赖说明

图像反推的本地读取适配自 MIT 许可的 [Stable Diffusion Prompt Reader v1.3.5](https://github.com/receyuki/stable-diffusion-prompt-reader/commit/1a499becb0a88fd28ac3e4e09bd8917ce95c9629)，仅包含元数据读取部分，使用现有 Pillow，不安装上游 GUI、Tk 或旧版 Pillow。随源码和应用负载保留来源、许可及本地适配说明。

本地标签识别使用可选的 [ComfyUI-WD-Timm-Tagger](https://github.com/bedovyy/ComfyUI-WD-Timm-Tagger)，在 ComfyUI 环境中运行。节点版本由启动器清单固定；八个预设模型包的来源版本、文件校验和及各自许可保存在 `anima_webui/tagger-models.json`，由网页检测和启动器下载共用。模型按需单独下载，不包含在启动器安装包中。该节点所需 `timm`、`huggingface_hub` 使用现有 ComfyUI 依赖管理，不加入轻量网页服务依赖。模型包中的额外架构文件同样锁定版本并校验，完整性检测本身不导入或执行它们。

1.1.0 新建环境使用清单锁定的 Python 3.11.9、PyTorch 2.11.0 / CUDA 12.8；导入已有环境保留原 GPU 运行时。ComfyUI 和节点固定到提交，自动下载的模型与工具校验大小及 SHA-256。自定义下载源需要用户自行信任。

`python scripts/audit_dependencies.py --output output/dependency-audit.json` 审计托管运行时的锁定依赖。脚本只对 torch 2.11.0 的 `GHSA-rrmf-rvhw-rf47` / `PYSEC-2025-194` 保留一项明确例外：该问题由 `torch.jit.script` 编译带异常裸 `list` / `tuple` 类型注解的 Python 源码触发，工作台输入和固定工作流接口不提供这种编译入口。其他公告或跳过扫描均视为失败。更换节点或运行时时需重新评估此范围；这不表示第三方依赖没有漏洞。

源码运行的依赖范围来自 `pyproject.toml`，与托管运行时的锁定版本分开检查。先在独立虚拟环境安装 `pip-audit`，再运行 `python scripts/audit_dependencies.py --source --output output/source-dependency-audit.json`。该模式解析当前可安装的运行依赖及传递依赖，不应用托管 PyTorch 的例外。两种审计均已接入 CI，报告作为构建附件保存；跳过扫描、扫描不完整或网络失败不算通过。源码审计表示新安装的解析结果，不能替代对用户已安装环境的盘点。

1.1.0 把 aiohttp 下限及托管锁定版本提升至 **3.14.5**，包含 3.14.4 的资源耗尽、初始请求超时及 Secure Cookie 修复，以及后续 WebSocket 兼容修复；参见 [上游更新日志](https://docs.aiohttp.org/en/stable/changes.html) 和 [资源耗尽公告](https://github.com/aio-libs/aiohttp/security/advisories/GHSA-2g87-pp6c-29x5)。Pillow 下限与托管图片处理基线统一为 **12.3**，multidict 保持 **6.9.1**。源码升级需要重新安装依赖；若既有 ComfyUI 环境有冲突，请使用独立工作台虚拟环境，不要忽略依赖冲突。旧版 ZIP 不会因源码清单变更自动更新。

包内 `THIRD-PARTY-NOTICES.txt` 与 `third-party-packages.json` 列出启动器、运行时和构建依赖的版本及许可，并说明并非所有列出的包都被再分发。模型、ComfyUI 和额外节点不在启动器 ZIP 中，使用时遵循各自许可。

1.2.0 将源码图片处理下限和托管 Pillow 升级至 **13.0.0**，包含 [Pillow 13.0.0 安全修复](https://pillow.readthedocs.io/en/stable/releasenotes/13.0.0.html)。同时将托管 Brotli、certifi、GitPython 分别升级至 **1.2.1**、**2026.10.19**、**3.2.1**，修复本次发布审计发现的问题。没有扩大审计例外；既有环境仍需按其依赖约束更新，不能仅凭更新启动器认定原环境依赖已升级。
