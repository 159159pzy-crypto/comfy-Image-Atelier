> **作者没跑路！！！因为作者是学生，所以更新频率会比较慢，但有issue作者也会及时修复的**

# Anima Random Studio · 1.0.1

基于 [ComfyUI](https://github.com/Comfy-Org/ComfyUI) 的本地生图工具，提供单图和随机批量两个工作台，支持 Anima / Krea 2、LoRA、提示词池、收藏及历史复现。

## 下载与启动（Windows）

1. 打开 [1.0.1 下载页](https://github.com/159159pzy-crypto/comfyui-random-image-generation/releases/tag/1.0.1)，下载 **`AnimaRandomStudio-win-x64-unsigned.zip`**。
2. **完整解压**，进入 `AnimaRandomStudio` 文件夹，双击 **`AnimaRandomStudio.exe`**。请保留同目录的 DLL 和其他文件，不要只复制 EXE。
3. 在「安装环境」选择 **新建环境**，或 **导入已有 ComfyUI**；按界面检查并准备依赖，完成后回到「概览」点击 **启动工作台**。

**不要下载页面底部的 Source code 来找 EXE。** 那两份是开发源码；上面的 `AnimaRandomStudio-win-x64-unsigned.zip` 才是完整启动器。

启动器适用于 Windows 10/11 x64，内置 .NET 与 Windows App Runtime。新建环境面向 NVIDIA 显卡，需要支持 CUDA 12.8 的驱动（570.65 或更新版本）和联网下载。已有其他显卡环境可尝试导入，兼容性取决于 ComfyUI 和所用节点。模型与 ComfyUI 首次配置时单独下载，不在启动器 ZIP 内；Krea 2 和可选修复模型按需添加。

当前包未做代码签名，文件名中的 `unsigned` 即表示未签名。完整性可用附件 `SHA256SUMS.txt` 核对。

## 可以做什么

- **单图生成**：文生图、拖图进入图生图、实时降噪预览、普通 / 双采样、放大与脸部修复。
- **随机批量**：角色、服装、姿势、背景、表情五类池，支持选定候选、排除项、导入和自定义内容；Krea 2 使用独立自然语言池。
- **作品管理**：任务队列、历史详情、原种子复现、同提示词换种子、收藏、画师与风格预设。

![单图工作台](docs/screenshots/anima-studio-single-workbench.png)

## 升级与常见问题

- **升级**：将新版 ZIP 解压到新文件夹，退出旧启动器，再运行新版 EXE；在设置中选择「应用内置新版」。保留原有配置、模型和数据，不要只替换 EXE。使用旧源码版的用户，请先备份原项目的 `data` 目录。
- **提示词池为空**：确认 Anima Tools 已完整安装。`-main`、`-master` 和改名目录均支持，无需为去掉后缀重装；若有重复或缺文件的节点目录，按界面提示处理。
- **模型 / LoRA 不显示**：在 ComfyUI 的 LoRA Manager 中完成扫描和基础模型分类，回工作台刷新分类；添加模型后可能需要重启 ComfyUI。
- **生成失败**：先关闭可选放大 / 修复，检查当前档案的模型和节点，再根据日志处理缺失项。

安装、源码运行和构建见 [启动器说明](launcher/README.md)，版本变化见 [更新日志](CHANGELOG.md)。遇到问题请到 [Issues](https://github.com/159159pzy-crypto/comfyui-random-image-generation/issues) 附上版本、操作步骤和相关错误日志，贴日志前请遮去令牌等私密信息。

代码采用 [MIT License](LICENSE)；模型和第三方节点遵循各自许可。默认服务仅监听本机。
