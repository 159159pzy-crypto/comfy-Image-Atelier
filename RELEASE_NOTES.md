# Comfy Image Atelier 1.5.0

本版为 **正式版**。项目由 Anima Random Studio 更名为 **Comfy Image Atelier**，后续代码、版本下载和问题反馈统一迁移至 [新仓库](https://github.com/159159pzy-crypto/comfy-Image-Atelier)。

旧仓库版本的启动器仍使用旧更新地址，**请手动下载安装本版一次**，之后可从应用内获取新仓库的正式版更新。历史代码和安装包继续保留在 [旧仓库](https://github.com/159159pzy-crypto/comfyui-random-image-generation/releases)。

## 下载与启动

- **安装版**：下载 `AnimaRandomStudio-Setup-win-x64.exe`，覆盖已有安装或按当前用户安装，从开始菜单或桌面的 **Comfy Image Atelier** 入口启动。
- **单文件版**：下载 `AnimaRandomStudio.exe` 直接运行；`AnimaRandomStudio-portable-win-x64.zip` 仅包含同一份 EXE。
- 为兼容旧安装、更新协议和现有数据，程序文件名、安装标识及数据目录保持不变。
- 运行依赖内嵌在 EXE 中；ComfyUI、节点与模型在配置环境时另行下载。GitHub 的 Source code 附件为开发源码。

支持 Windows 10 1809 及以上的 x64 系统；新建环境需要支持 CUDA 12.8 的 NVIDIA 驱动（570.65 或更新版本）。

## 更名与仓库迁移

- 网页标题、品牌文字、启动器窗口、托盘、安装器、快捷方式及程序属性统一使用 **Comfy Image Atelier**，沿用原有图标。
- 启动器从新仓库检查正式版，仍严格核对发行包来源、大小及 SHA-256；旧仓库的更新缓存不会影响新仓库检查。
- 继续识别旧版安装记录和数据。Anima 模型、Anima Tools 及图片历史格式保留原名。
- 将环境清单中的 hydra-core 升级至 1.3.8，修复依赖审计发现的问题；已有 ComfyUI 环境不会被自动升级。详见 [依赖说明](docs/dependencies.md)。

## 重新实现作品收藏

- 卡片和作品详情中的星星打开收藏夹菜单，点选即保存；单图与随机作品共享收藏，同一作品可加入多个夹。
- 主页「选择」支持跨页、跨来源逐张挑选，将作品统一收入多个目标夹，或从当前夹批量移出；不提供全选，失败保留操作上下文。
- 收藏夹管理支持新建、重命名、拖动及键盘排序和确认删除；删除收藏夹仅解除归属，保留原图，不自动归入其他夹。
- 升级保留已有收藏夹和记录，数据库结构迁移前备份。新收藏按最近收藏时间排序，重复加入不置顶。
- 收藏与选择反馈支持明暗主题、窄屏、键盘和减少动态效果设置；生成完成不打断正在进行的选择。

## 作品查看与数据维护

- 保留历史来源筛选、分页、详情、大图预览、载入设置、复现及变体；移除旧版独立的「整理作品」窗口。
- 单张删除使用独立确认界面，默认只删记录，也可永久删除原图及记录；继续保护其他作品引用的原文件并提供失败重试。
- 移除导入图片手动清理、后台自动清理及相关设置；图生图与反推继续使用已有图片存储，现有图片、历史及旧数据库表保持保留。

## 从旧版升级

1. 等待生成队列空闲，停止工作台并退出旧启动器，备份整个 `%LOCALAPPDATA%/AnimaRandomStudio`；源码版另备份项目的 `data`。ComfyUI 原图、模型和插件配置按需单独备份。
2. 从本页下载 1.5.0 安装包覆盖升级，或运行新版单文件 EXE。继续使用原数据目录，无需重新配置已有环境；软件升级不会自动更新 ComfyUI 或已有节点。
3. 打开 **Comfy Image Atelier**，启动工作台并刷新网页，使数据库迁移、接口和界面同时生效。
4. 后续在启动器设置中检查新仓库的正式版更新。

源码版的 `data` 不会自动迁入启动器目录。安装版、便携版共用 `%LOCALAPPDATA%/AnimaRandomStudio`，移动 EXE 不会迁移模型、环境或历史。

## 验证范围

本次发布沿用现有 CI，验证范围包括自动化质量检查、更新来源与缓存隔离回归、品牌显示和简单启动检查，以及 EXE、ZIP、安装包、来源提交与校验文件的一致性。安装验证使用隔离测试目录。

本次未额外开展真实环境从零安装、完整 GPU 生图及升级回退实机验收。自动化回归和启动检查不代表这些场景已经全部验证。

`SHA256SUMS.txt` 覆盖 EXE、便携 ZIP 和安装包；`build-info.json` 记录版本及来源提交。EXE 内嵌文件清单和第三方许可，当前包未签署代码签名。

[完整更新日志](https://github.com/159159pzy-crypto/comfy-Image-Atelier/blob/1.5.0/CHANGELOG.md) · [启动器说明](https://github.com/159159pzy-crypto/comfy-Image-Atelier/blob/1.5.0/launcher/README.md) · [依赖说明](https://github.com/159159pzy-crypto/comfy-Image-Atelier/blob/1.5.0/docs/dependencies.md)
