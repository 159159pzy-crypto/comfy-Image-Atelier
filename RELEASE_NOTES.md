# Comfy Image Atelier 1.8.0 — 提示词创作与参考图复用

在单图工作台，可以从一句构想开始扩写，也可以让视觉 AI 描述参考图的构图，再把同一张图带入图生图。已有 ComfyUI 整合包的用户可通过 Windows 启动器只读接入现有服务。

## 提示词与参考图

- **按用途重写。** 可选电影分镜、时装海报、创意插画和场景概念设计。Anima 将英文标签与人物动作、空间关系分段，整理标签和添加 LoRA 触发词时保留描述段。Krea 2 则生成连贯的英文描述。
- **按构图反推。** 视觉 AI 新增「构图保真」，优先描述裁切、机位、主体的相对位置、大小与遮挡。点「用作图生图参考」即可将反推图片带入单图工作台，提示词仍可独立编辑和应用。
- **导入 Wardrobe V6 的 PNG。** 元数据提取优先使用重建正向，缺失时回退到生成正文。对于多分支工作流，提取结果可能只覆盖部分分支。

## 启动器与作品详情

- **接入已有 ComfyUI。** 默认只读连接由原整合包启动的服务，通过独立 Python 准备工作台。选择允许补齐时，可先预览涉及的节点和依赖，启动器在修复前建立备份。
- **从中断处继续配置。** 向导支持选择系统代理、直连或自定义 HTTP 代理。依赖安装中断后可重新核对已有内容并继续，完成页可稍后返回，也可试生成一张 512 × 512 图片并在工作台历史中查看。
- **打开作品看原图。** 作品库和单图工作台会提前准备即将打开的原图，待解码后显示。快速翻图时，详情跟随当前选中的作品。README 增加了八种放大修复流程的同区域细节对比。

## Windows x64 下载与升级

- 日常安装或覆盖升级，推荐下载 `AnimaRandomStudio-Setup-win-x64.exe`。升级前请等待生成队列空闲并退出启动器。
- 下载 `AnimaRandomStudio.exe` 可直接运行，`AnimaRandomStudio-portable-win-x64.zip` 内含同一份 EXE。
- `SHA256SUMS.txt` 列出三个程序附件的 SHA-256，`build-info.json` 记录版本、构建来源和工作台载荷哈希。

标签发布流程包含 Windows 和 Linux 回归测试、Chromium 工作台交互测试、依赖审计、Windows 启动器打包与隔离安装、修复、卸载检查。硬件上的生成结果取决于所选模型、节点和设备。[GitHub Actions](https://github.com/159159pzy-crypto/comfy-Image-Atelier/actions/workflows/launcher.yml)

[使用指南](https://github.com/159159pzy-crypto/comfy-Image-Atelier/blob/1.8.0/docs/guide.md) · [Windows 启动器说明](https://github.com/159159pzy-crypto/comfy-Image-Atelier/blob/1.8.0/launcher/README.md) · [完整更新日志](https://github.com/159159pzy-crypto/comfy-Image-Atelier/blob/1.8.0/CHANGELOG.md)
