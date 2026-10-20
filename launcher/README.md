# Windows 启动器

## 安装与使用

从 [1.1.0 Release](https://github.com/159159pzy-crypto/comfyui-random-image-generation/releases/tag/1.1.0) 下载 `AnimaRandomStudio-win-x64-unsigned.zip`，完整解压到新文件夹后，直接运行根目录的 `AnimaRandomStudio.exe`。源码 ZIP 不包含 EXE。

首次打开且尚未配置环境时，启动器直接进入五步安装向导。已有环境的用户进入「概览」，也可从「安装环境」重新打开向导。以下说明对应 1.1.0。

1. **选择方式**：启动器自动检测系统和显卡。选择「安装完整环境」准备独立 Python、Git、ComfyUI、必需节点和 Anima 基础模型，需要 Windows 10 1809 或更新版本的 x64 系统，以及支持 CUDA 12.8 的 NVIDIA 驱动（570.65 或更新版本）。已有可用环境时选择「我已有 ComfyUI」。
2. **安装位置**：新建环境选择安装文件夹，页面会显示实际环境路径和所在盘可用空间；工具、环境和模型放在所选安装盘。首次默认官方下载来源，也可切换国内镜像；自定义镜像等选项在「高级下载设置」中。导入时选择含 `main.py` 的 ComfyUI 目录或便携版父目录，自动识别 `.venv`、`venv`、`python_embeded`，其他布局可展开「手动指定 Python」。
3. **检查确认**：点击「检查并继续」，查看安装内容、已知剩余下载量、可复用文件及各盘需预留空间。Python 依赖包另按需下载。处理检查结果中的问题后重新检查，通过后点击「开始安装」；已有节点齐全时可直接「完成导入」。修改目录、下载来源或安装方式后必须重新检查。
4. **安装环境**：查看工具、环境、节点、基础模型及最终检查的阶段状态。下载进度显示当前文件，安装依赖等阶段显示进行中。可「暂停」、在本次会话中「继续」或失败后「重试」；暂停后更改目录或来源需重新检查。退出并重新运行启动器后，点击「检查并继续」，确认后续装，已校验资源会复用。导入分支保留已有节点版本和 GPU 运行时，补齐缺失节点；模型校验不符时按提示处理已有文件再重试。
5. **开始创作**：安装完成后点击「开始创作」，服务启动且连接检查通过后打开工作台；启动失败时留在此页「重试启动」。也可选择「稍后打开」结束向导，之后从「概览」点击「启动工作台」。导入环境缺少模型时，完成页列出待补齐内容并提供「前往模型资源」入口。

向导底部固定保留操作按钮，长内容可滚动查看；「查看日志」打开详细日志，点击「返回安装向导」回到原步骤。向导草稿单独保存在配置目录的 `onboarding.json`，未开始安装的选项不会覆盖当前生效环境。

默认 ComfyUI 端口为 8188，工作台端口为 8190；端口冲突时按提示修改。

- **模型**：Anima 基础模型可自动下载；Krea 2、SeedVR2 等按模型资源页提示导入。LoRA 支持子目录和 `extra_model_paths.yaml`，新增后请扫描并刷新分类。
- **退出**：关闭窗口会最小化到托盘；托盘菜单可选择保留服务退出，或停止本程序启动的服务后退出。

Anima Tools 按其 `js` 目录中的四个数据文件识别，支持 `-main`、`-master` 和自定义目录名。重复安装或数据不完整时会明确提示。

## 升级与数据

新版 ZIP 解压到新目录，退出旧启动器后运行新 EXE，再在设置中选择「应用内置新版」。保留完整目录；仅替换 EXE 会缺少运行资源。需要时可在空闲时回退上一工作台版本。

配置、日志和用户数据默认保存在 `%LOCALAPPDATA%/AnimaRandomStudio`。迁移前先等待任务空闲、停止工作台并退出启动器，再备份整个目录；旧源码版的数据在原项目的 `data` 中。启动器不会自动把旧源码版的数据迁入新位置。

## 提示词助手

先启动 ComfyUI，在「设置 → 提示词助手」点击「加载 / 刷新」。这里直接管理当前环境的 Prompt Assistant 服务、LLM 模型和系统提示词：支持 OpenAI 兼容服务、Ollama、模型参数以及模板的新增、修改、复制、删除。供应商模型列表仅在点击「从供应商获取模型列表」时获取，也可直接填写模型名称。

API Key 沿用插件的存储方式，不另写启动器配置。编辑时留空保留原密钥，勾选清除才删除；列表只显示是否已配置。模板沿用插件稳定 ID，保存时读取最新文档并保留其他模板、分类和默认选择。插件当前默认模板需先在插件中更换默认项，才能删除。Anima 与 Krea 2 共用这些配置，保存后在工作台重写区刷新选项即可使用。

工作台纯模板重写需要 PromptExpand 的 `template_only` 兼容输入。安装/修复和启动器启动已停止的 ComfyUI 时，会验证并应用兼容补丁；已运行的外部 ComfyUI 只提示重新启动，不会被自动停止。补丁仅支持清单锁定提交 `e0587e6d50939ae05e92922f1ed2fce008c7f622` 的已验证源码，保留原文件到插件目录 `.anima-template-only-v1`，未知或用户修改过的版本会被拒绝。旧工作流省略此输入时行为保持原样；开启后只发送所选系统提示词及用户输入，不附加语言或思考过程指令。

## 源码运行

适合已经安装并能运行 ComfyUI 的开发者，需要 Python 3.11+。先启动 ComfyUI，在本仓库根目录运行：

```powershell
python -m pip install -e .
python run.py
```

浏览器访问 `http://127.0.0.1:8190`。如提示找不到词库，在启动命令后添加 `--anima-tools-dir '<实际的 Anima Tools 节点目录>'`；ComfyUI 使用其他端口时添加 `--comfy-url http://127.0.0.1:<实际端口>`。路径占位符需替换成自己的目录。

源码版可在停止 ComfyUI 后单独应用同一补丁，然后重新启动 ComfyUI：

```powershell
python launcher/scripts/patch_prompt_assistant.py --root '<Prompt Assistant 插件目录>' --check
python launcher/scripts/patch_prompt_assistant.py --root '<Prompt Assistant 插件目录>' --apply
```

`--check` 不修改文件：返回码 0 表示已安装，2 表示已识别但待安装，1 表示不支持或文件异常。补丁脚本内嵌在启动器 Core 程序集中，发布包不依赖源码目录。

可将 `templates/workflow_ui_v2.json` 拖进 ComfyUI 检查缺失节点；应用实际提交使用 `templates/workflow_api_v2.json`。节点及版本见 [运行清单](https://github.com/159159pzy-crypto/comfyui-random-image-generation/blob/1.1.0/launcher/manifests/launcher-manifest.json)。

## 开发与构建

需要 .NET 8 SDK、Python 3.11+、Node.js 和 PowerShell 7，在仓库根目录运行：

```powershell
python -m pip install -e '.[dev]'
python scripts/check_release.py
dotnet run -c Release --project launcher/Anima.Launcher.Tests
python launcher/scripts/test_prompt_assistant_patch.py
pwsh ./launcher/scripts/Test-LauncherPackage.ps1
pwsh ./Build-Launcher.ps1
```

输出位于新的 `dist/candidates/<构建编号>` 目录：完整 ZIP、`SHA256SUMS.txt` 和 `build-info.json`。`candidate.json` 仅用于本地定位构建输出，不作为 Release 附件。包内 `FILES.sha256` 覆盖全部其他文件；构建来源、版本和应用负载哈希写入 `build-info.json`。正式发布要求提交已保存、工作树干净。

需要源码目录的双击入口时，可运行 `pwsh ./Build-Launcher.ps1 -InstallRootEntry`；生成的根目录 EXE 依赖同级 `.launcher`，两者均不进入 Git。普通用户使用上面的完整 ZIP 即可。

包内附带第三方许可和依赖清单；ComfyUI、节点与模型单独下载。已知依赖限制见 [依赖说明](https://github.com/159159pzy-crypto/comfyui-random-image-generation/blob/1.1.0/docs/dependencies.md)。
