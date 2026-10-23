# Windows 启动器

## 安装与使用

1.2.0 起，从 [最新 Release](https://github.com/159159pzy-crypto/comfyui-random-image-generation/releases/latest) 下载 `AnimaRandomStudio-Setup-win-x64.exe` 安装，或直接下载 `AnimaRandomStudio.exe` 运行。`AnimaRandomStudio-portable-win-x64.zip` 仅包含同一份 EXE。源码 ZIP 不包含 EXE。运行依赖自动提取到用户缓存，EXE 旁无需 DLL 或资源文件。

安装版默认按用户安装到 `%LOCALAPPDATA%/Programs/AnimaRandomStudio`，也可选择其他位置；创建开始菜单入口，可选桌面快捷方式；卸载保留用户数据、模型和环境。

首次打开且尚未配置环境时，启动器直接进入六步安装向导。已有环境的用户进入「概览」，也可从「安装环境」重新打开向导。1.2.0 包含以下六步流程、模型下载修复与提示词助手供应商配置。

1. **选择方式**：启动器自动检测系统和显卡，也可点击「重新检测」。首次安装选择新建完整环境，准备独立 Python、Git、ComfyUI、必需节点和 Anima 基础模型；需要 Windows 10 1809 或更新版本的 x64 系统，以及支持 CUDA 12.8 的 NVIDIA 驱动（570.65 或更新版本）。已经能运行 ComfyUI 时选择连接已有环境，继续使用现有安装目录。
2. **安装位置**：新建环境选择安装文件夹，页面会显示实际环境路径和所在盘可用空间；工具、环境和模型放在所选安装盘。首次默认官方下载来源，也可切换国内镜像；自定义镜像等选项在「高级下载设置」中。导入时选择含 `main.py` 的 ComfyUI 目录或便携版父目录，自动识别 `.venv`、`venv`、`python_embeded`，其他布局可展开「手动指定 Python」。
3. **检查确认**：点击「检查并继续」，查看安装内容、已知剩余下载量、可复用文件及各盘需预留空间。当前生成档案的基础模型固定包含，可选的 Krea 2、SeedVR2 和八种本地反推模型默认不选。Krea 2、SeedVR2 按功能搭配模型与依赖，共享 VAE 只下载一次；本地反推模型可独立勾选。更改选择后自动重新检查下载量、实际保存位置与空间，Python 依赖包另按需下载。检查通过后点击「开始安装」；已有节点齐全时点击「连接并准备模型」。修改目录、下载来源或安装方式后必须重新检查。
4. **安装环境**：查看工具、环境、节点、所选模型及最终检查的阶段状态。模型下载同时显示按字节计算的整批进度、当前资源进度、下载量及可计算时的速度；当前资源可能是单文件或完整反推模型包。收齐字节后继续显示校验状态，全部校验成功才到 100%；安装依赖等阶段显示进行中。可「暂停」、在本次会话中「继续」或失败后「重试」；暂停后更改目录或来源需重新检查。选择和下载阶段会保存，退出重开后复用已校验资源与断点；环境已安装而模型未完成时只恢复模型阶段。导入分支保留已有节点版本和 GPU 运行时，补齐缺失节点与所选模型。基础模型就绪后，可选模型失败可重试或跳过剩余可选项，稍后再到模型资源页补齐。
5. **提示词助手**：可以点击「启动并配置」，启动或复用当前环境的 ComfyUI，再加载已有供应商或填写名称、协议、Base URL、API Key 和默认文本模型。支持 OpenAI 兼容服务及 Ollama；模型名可直接填写，只有点击「保存服务并获取模型列表」才向供应商请求列表。「保存并继续」保存并回读插件配置，不发送提示词或生成请求。也可跳过，直接进入下一步；跳过本身不会启动服务，稍后可在设置中补配。
6. **开始创作**：完成页重新检查基础模型，模型齐备后点击「打开工作台」，服务启动且连接检查通过后打开工作台；启动失败时留在此页重试。若模型后来被移动或损坏，可在此重新检查并补齐缺失文件；已有异常单文件会保留，需按提示备份处理。缺少模型或检查异常时仍可选择「先打开工作台」，空闲时也可选择「稍后打开」结束向导。

向导底部固定保留操作按钮，长内容可滚动查看；「查看日志」打开详细日志，点击「返回安装向导」回到原步骤。向导草稿、可选模型选择与供应商步骤的完成或跳过状态单独保存在配置目录的 `onboarding.json`，未开始安装的选项不会覆盖当前生效环境。API Key 仅由当前环境的 Prompt Assistant 插件保存，不写入向导草稿或启动器配置。旧版向导记录保持兼容，不会强制重新配置供应商。

启动器的页面、下拉选项、提示条和对话框使用同一套界面样式，在「设置」中可选择浅色、深色或跟随系统。窗口较小时可滚动查看完整内容，键盘 Tab 可依次访问当前步骤的输入与操作。

默认 ComfyUI 端口为 8188，工作台端口为 8190；端口冲突时按提示修改。

- **模型**：Anima 基础模型、Krea 2 Turbo INT8 与配套文本编码器、SeedVR2 3B INT8 与配套 VAE 均可自动下载，来源固定提交、大小与 SHA-256 已写入清单。模型资源页重新扫描会保留完整目录、仍可下载项目的勾选及当前查看项，已完成项目更新状态。LoRA 支持子目录和 `extra_model_paths.yaml`，新增后请扫描并刷新分类。
- **退出**：关闭窗口会最小化到托盘；托盘菜单可选择保留服务退出，或停止本程序启动的服务后退出。

Krea 2 和 SeedVR2 的下载分别来自 [Comfy-Org/Krea-2](https://huggingface.co/Comfy-Org/Krea-2) 与 [Comfy-Org/SeedVR2](https://huggingface.co/Comfy-Org/SeedVR2) 的 ComfyUI 适配文件。模型详情可查看来源、校验值与许可：Krea 2 使用 Krea 2 Community License，SeedVR2 使用 Apache-2.0。

Anima Tools 按其 `js` 目录中的四个数据文件识别，支持 `-main`、`-master` 和自定义目录名。重复安装或数据不完整时会明确提示。

## 升级与数据

默认每 24 小时后台检查正式 Release，可在「设置」中关闭自动检查或手动检查。查看版本说明和下载大小后开始下载，支持暂停、续传和重试；下载不会打断生图。下载完成并等待任务空闲后，点击重启更新，一次更新启动器和内置工作台。

完整更新验证成功后解除旧工作台的固定回退；失败恢复原版本和原配置，保留原固定设置。原来运行的工作台会安全停止并恢复，原来停止的服务保持停止；无法安全停机时拒绝更新，不强制结束任务。工作台的「应用内置新版」与「回退上一版」仍可单独使用。

1.1.0 及更早版本没有联网自更新功能，需要最后一次下载带更新功能的新版本。此后可直接从软件内更新。安装版拒绝降级，安装相同版本可修复；安装或卸载前需先退出启动器，安装程序不会强制结束服务。

便携版、安装版和旧入口共享配置、历史及更新状态，默认目录为 `%LOCALAPPDATA%/AnimaRandomStudio`。移动便携 EXE 不会迁移历史、模型或 ComfyUI。迁移前先等待任务空闲、停止工作台并退出启动器，再备份整个目录；旧源码版的数据在原项目的 `data` 中。启动器不会自动把旧源码版的数据迁入新位置。

## 提示词助手

新向导的第 5 步提供供应商和默认文本模型的精简配置，可随时跳过。完整模型参数、视觉反推与系统提示词管理仍位于设置页。

先启动 ComfyUI，在「设置 → 提示词助手」点击「加载 / 刷新」。在「配置用途」选择「文本重写」或「图像反推」，分别管理 LLM / VLM 模型和对应的系统提示词；两者共用服务地址与 API Key。支持 OpenAI 兼容服务、Ollama、模型参数以及模板的新增、修改、复制、删除。供应商模型列表仅在点击「从供应商获取模型列表」时获取，也可直接填写模型名称；图像反推需选择支持图像输入的视觉模型。

API Key 沿用插件的存储方式，不另写启动器配置。编辑时留空保留原密钥，勾选清除才删除；列表只显示是否已配置。模板沿用插件稳定 ID，保存时读取最新文档并保留其他模板、分类和默认选择。插件当前默认模板需先在插件中更换默认项，才能删除。Anima 与 Krea 2 共用这些配置，保存后在工作台对应的重写台或反推台刷新选项即可使用。

图像反推使用 Prompt Assistant 的 `ImageCaptionNode`、`vlm_models` 和 `vision_prompts`，不依赖重写的纯模板补丁。若运行中的插件缺少兼容视觉节点，更新插件并重启 ComfyUI 后再刷新。修改视觉模型或模板不会改动同名的文本重写配置；删除共用服务会一并删除该服务的两类模型。

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

可将 `templates/workflow_ui_v2.json` 拖进 ComfyUI 检查缺失节点；应用实际提交使用 `templates/workflow_api_v2.json`。节点及版本见 [运行清单](https://github.com/159159pzy-crypto/comfyui-random-image-generation/blob/1.2.0/launcher/manifests/launcher-manifest.json)。

## 本地图像反推模型

向导「检查确认」和「模型」页均提供可选的本地反推模型，推荐 `pixai-tagger-v0.9-timm`，默认不选，勾选后才下载。八个预设是可独立选择的方案，不会一次全部下载。它们共用应用内 `anima_webui/tagger-models.json` 锁定来源提交、文件大小与 SHA-256；PixAI 1.0 的 `timm_arch.py` 也属于完整包。`launcher/scripts/lock_tagger_models.py` 用于维护锁文件，运行后需审阅来源和许可变化。

模型包在 `wd_taggers.anima-packages` 暂存，全部校验通过后才发布到精确的 `wd_taggers/<模型名称>`。暂停、取消和重试保留可续传内容；修复残缺模型时先备份原目录。不会因为其他模型目录里有同名 `config.json` 就认为模型已安装。

运行中的 ComfyUI 通过现有本地接口确认真实模型目录；未启动时探测器沿用 ComfyUI 参数。无法确认目录时不下载。节点作为可选依赖锁定安装，已有安装保持原版本，缺失不阻断生图或视觉 AI。

开发验收可使用启动器相同的下载实现（仅下载指定模型）：

```powershell
dotnet run -c Release --project launcher/Anima.Launcher.Tests -- --tagger-only
dotnet run -c Release --project launcher/Anima.Launcher.Tests -- --tagger-download <ComfyUI目录> <该环境python.exe> pixai-tagger-v0.9-timm
```

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

输出位于新的 `dist/candidates/<构建编号>`：独立 `AnimaRandomStudio.exe`、仅含相同 EXE 的 ZIP、`AnimaRandomStudio-Setup-win-x64.exe`、三份下载文件的 `SHA256SUMS.txt` 及 `build-info.json`。`candidate.json` 仅用于本地定位输出。单文件内部的 `FILES.sha256` 覆盖所有内嵌运行文件（排除清单自身与外层 EXE）；来源资料和第三方许可随 EXE 内嵌。正式发布要求提交已保存、工作树干净。

构建脚本下载锁定的 Inno Setup 6.7.3 到忽略的 `tools` 目录，验证官方 SHA-256 与 Pyrsys B.V. 数字签名后准备编译器。安装程序始终包含与便携版逐字节相同的最终 EXE。

只需本地检查启动器改动时，可先编译与运行向导检查，再生成独立候选包：

```powershell
dotnet build launcher/Anima.Launcher.WinUI/Anima.Launcher.WinUI.csproj -c Release --no-restore
dotnet run -c Release --project launcher/Anima.Launcher.Tests -- --onboarding-only
pwsh ./Build-Launcher.ps1 -CandidateDirectory './dist/candidates/launcher-ui-review'
pwsh ./launcher/scripts/Test-LauncherCandidate.ps1 -CandidateDirectory './dist/candidates/launcher-ui-review'
pwsh ./launcher/scripts/Test-LauncherInstaller.ps1 -CandidateDirectory './dist/candidates/launcher-ui-review'
```

候选目录必须不存在；重复构建时换一个新目录。省略 `-InstallRootEntry` 和 `-InstallShortcut` 时，构建不会替换根目录启动入口或桌面快捷方式。开发中的候选包可以包含未提交改动，`build-info.json` 会如实标记 `sourceDirty`，不等同于正式发布包。打包会重新生成 `launcher/payload.zip`，其中包含构建时当前工作树的工作台文件。

原生界面验收使用单独的 `ANIMA_LAUNCHER_HOME` 目录，并给候选 EXE 传入 `--show`，避免复用日常配置或自动启动服务。编译和向导逻辑检查不能代替浅色、深色、小窗口及原生弹窗的实际检查。

需要源码目录的双击入口时，可运行 `pwsh ./Build-Launcher.ps1 -InstallRootEntry`；部署到根目录的 EXE 与发布单文件一致，不依赖同级 `.launcher`；替换前旧入口备份在 `.launcher` 中。普通构建不会修改根入口或快捷方式。

包内附带第三方许可和依赖清单；ComfyUI、节点与模型单独下载。已知依赖限制见 [依赖说明](https://github.com/159159pzy-crypto/comfyui-random-image-generation/blob/1.2.0/docs/dependencies.md)。
