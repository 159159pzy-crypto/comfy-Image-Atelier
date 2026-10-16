# Windows 安装启动器

Windows 10/11 x64，**WinUI 3（Windows App SDK，C#）**，非打包（unpackaged）+ 自包含文件夹分发。用户无需安装 .NET 或 Windows App Runtime，也不需要从源码启动 WebUI。模型、ComfyUI 和第三方节点按需从各自来源下载，不随启动器再分发。v2 工作台支持 Anima 与 Krea 2 两个模型档案；档案资源、SeedVR2、N 卡放大和 Forbidden Vision 脸部细化插件状态由 WebUI `/api/resources` 统一检查。

> 完整包为 `AnimaRandomStudio-win-x64-unsigned.zip`（或签名版 `signed.zip`）。先解压整个 ZIP，再运行 `AnimaRandomStudio/AnimaRandomStudio.exe`。WinUI 3 使用自包含文件夹分发，EXE、DLL、运行时和界面资源必须放在一起。GitHub 的 Source code ZIP 是源码，不含可执行启动器。

## 使用

1. 双击 `AnimaRandomStudio.exe`，首次打开选择导入已有 ComfyUI 或新建环境。
   - 默认进入「服务」主页，可独立查看并控制 ComfyUI 与工作台的启动/停止（外部实例只检测不停止），也可一键全部启动/停止。
2. 导入可选择包含 `main.py` 的目录，或便携版上级目录。自动识别 `.venv`、`venv`、`python_embeded`；其他环境手动选择 `python.exe`。点击“检查 / 导入目录”。
3. 新建请选择空目录、来源，点击“安装环境 / 补齐缺失节点”。新安装仅支持 NVIDIA，需已安装支持 CUDA 12.8 的 NVIDIA 驱动；AMD/Intel 用户导入已经可用的 ComfyUI。安装过程记录阶段，失败后可在同一目录重试。
4. 模型页面按 Anima / Krea 2 基础生成和 SeedVR2 资源分组。检查来源、大小、目标、SHA-256 和授权后勾选下载。当前清单中没有可验证来源的 v2 模型只做本地检测，需从模型作者合法来源手动安装；不会用未知 URL 自动下载或替换文件。v2 所需的 AAAlice Nodes、Prompt Assistant、Krea seed enhancer 等节点会按锁定提交安装；N 卡放大和 Forbidden Vision 脸部细化属于可选插件，需从其来源另行安装。
5. 点击“启动并打开工作台”。检查 ComfyUI `/system_stats`、`/object_info`、必需节点和 WebUI `/api/status` 后打开浏览器。以后双击自动启动并驻留托盘。
6. 如需验证生成，主动点击“生成一张测试图”：先使用 Anima 普通底图、关闭全部可选修复，生成一张最小批次；再在工作台切换双采样底图、Krea 2 和各修复阶段。存在其他生成任务时拒绝提交。

安装建议在环境盘预留至少 20 GiB；模型另需约 6 GiB。安装工具、Python 和下载缓存还会占用启动器状态目录所在盘。模型下载前按目标磁盘核算剩余空间；下载完成验证大小和 SHA-256 后才原子发布。暂停保留 `.part`，重试或更换镜像继续；取消只删除所选下载的临时文件。已存在但校验不符的文件不会覆盖，请先手动备份。安装记录最后一个阶段，中断后重试会自动续跑幂等步骤；克隆残留的 `.anima-installing` 临时目录也会被识别，完成的克隆直接接管、未完成的继续拉取，无需手动清理。

## v2 工作流与模型档案

工作台以仓库 `templates/workflow_ui_v2.json` 为可视化基线，使用 `templates/workflow_api_v2.json` 的 API Format 版本提交。启动器不会覆盖用户的原始 ComfyUI 工作流；升级时请保留自己的工作流供回滚和核对。

- **Anima**：`anima-base-v1.0.safetensors` + `qwen_3_06b_base.safetensors` + `qwen_image_vae.safetensors`，编码类型 `stable_diffusion`，默认底图－双采样器。
- **Krea 2**：`krea2_turbo_int8_convrot.safetensors` + `qwen3vl_4b_fp8_scaled.safetensors` + `qwen_image_vae.safetensors`，编码类型 `krea2`，默认普通底图。
- SeedVR2 默认资源为 `seedvr2_3b_int8_convrot.safetensors` 与 `ema_vae_fp16.safetensors`；N 卡与脸部细化只检查插件/节点状态，不包含在模型下载清单中。
- 修复阶段按潜空间 → SeedVR2 → N 卡 → Forbidden Vision 脸部细化串联；关闭阶段会旁路，不会提交孤立节点。旧 CFGZeroStar、旧高清模型链和独立 ADetailer 不属于 v2 工作流。

## 模型路径与来源

`manifests/model-manifest.json` 是唯一自动下载清单。条目只有 HTTPS URL、确定大小和完整 SHA-256 同时存在才可自动安装。不按文件名猜地址。v2 的 Anima、Krea 2 和 SeedVR2 条目在来源未锁定时标为仅检测 / 手动导入；启动器不会猜测下载地址。LoRA 只扫描本地文件，具体选择留在工作台。

通过 ComfyUI 自己的 `folder_paths` 与 `load_extra_path_config` 解析 `extra_model_paths.yaml`，支持 `models/clip`、`models/unet`、安全子目录及 Impact 的 `ultralytics_bbox` / `ultralytics_segm`。新下载遵循配置的默认搜索目录。启动时从 `/object_info` 取得实际模型相对名称，映射到工作流；同名歧义会报错。启动后新增模型可能需要重启 ComfyUI。

Anima Tools 以 `js/` 下四个文件识别：`character_data.js`、`clothing_data.js`、`pose_data.js`、`background_data.js`。扫描、安装和启动使用同一个目录发现结果，支持标准目录、`-main` / `-master` 和自定义目录名；已有 ZIP 安装会保留，不会再克隆一份。可通过 `ANIMA_TOOLS_DIR` 指定目录，兼容 `COMFYUI_ANIMA_TOOLS`；无效的显式路径、多个有效候选，以及标准名称或含任意一个数据文件但内容不齐的目录都会报错。无标志数据文件的其他目录不算 Anima Tools；自动发现跳过隐藏、`.disabled` 和安装暂存目录。

提供官方、HF/Python 国内镜像、GitHub 镜像组合、自定义来源。Git 镜像可填 HTTPS 前缀或 `{url}` 模板；自定义 ComfyUI 仓库也必须包含清单锁定提交。镜像只作用于本程序子进程，不修改系统 Git/pip 配置。镜像可用性取决于提供方。PyTorch 使用官方固定 Windows CUDA wheel，UV 管理固定 Python 版本。

信任边界：ComfyUI/节点锁定到 40 位提交并在克隆后复核，模型与工具下载锁定 SHA-256；Python 包只锁定版本号、不逐包锁定哈希。自定义 Python 包索引等价于信任该索引提供的全部 wheel——请只填写可信源。

若官方模型站需要令牌，在设置里保存至 Windows Credential Manager；`launcher.json` 不存密码。令牌只发送给对应官方 Civitai/Hugging Face 主机，不传给镜像或重定向 CDN。不要将凭据写入自定义 URL。源码 Git/pip 暂不支持需要交互认证的私有仓库或私有包源。

## 服务、状态与升级

- 默认 ComfyUI `127.0.0.1:8188`，工作台 `127.0.0.1:8190`。冲突时提示选择自动分配空闲端口或手动修改，并同步保存 WebUI 的 ComfyUI 地址。
- 只停止本程序创建、PID/启动时间/可执行路径一致的进程。退出并保留服务后，再次运行能恢复进程所有权。外部 ComfyUI 始终不归启动器停止。
- 外部实例仅在 `/system_stats` 明确报告同一绝对 `main.py` 时复用；无法确认目录时按端口冲突处理。现有源码 WebUI 不具备启动器身份信息时也会提示换端口。
- 关闭窗口最小化到托盘；“退出（保留服务）”保持服务；“停止服务并退出”停止拥有的服务。设置页可创建桌面快捷方式。
- 配置：`%LOCALAPPDATA%\AnimaRandomStudio\launcher.json`；日志：`logs\`；独立用户数据：`data\`；应用版本：`versions\`。高级验收可用 `ANIMA_LAUNCHER_HOME` 指向隔离状态目录。
- 不自动拉取最新 ComfyUI 或节点。导入已有节点版本会保留，并在日志标注版本差异；启动时再验证必需类是否实际加载。正在运行的 ComfyUI 端口会阻止依赖安装。
- 升级时将完整新版 ZIP 解压到新目录，退出旧启动器后运行新目录中的 EXE，再从设置“应用内置新版”。不要只替换 EXE，工作台资源嵌在随包程序集内。健康检查失败会恢复旧版本指针，旧资源保留；停止工作台后可“回退上一工作台版本”。用户数据与应用代码分离。此功能应用本地内置工作台，不会自动从 GitHub 下载更新。
- 要更换锁定的 ComfyUI/节点版本，使用新版清单在**另一空目录新建环境**，验证后切换目录；旧环境保留用于回退。不在用户现有节点目录强行 checkout/reset。模型可通过 `extra_model_paths.yaml` 共用，避免重复下载。

## 开发和构建

需要 .NET 8 SDK 和 PowerShell 7；最终用户不需要 SDK。UI 工程为 `launcher/Anima.Launcher.WinUI`（WinUI 3，非打包、`WindowsAppSDKSelfContained`）。

```powershell
dotnet run -c Release --project launcher/Anima.Launcher.Tests
python -m pytest -q
pwsh ./launcher/scripts/Test-LauncherPackage.ps1
pwsh ./Build-Launcher.ps1
```

构建脚本优先使用本机 `%LOCALAPPDATA%\Codex\dotnet-sdk-8`，否则使用 PATH 中的 SDK。默认在全新的 `dist/candidates/<构建编号>/` 中生成候选，不覆盖已有 `dist/launcher`；也可用 `-CandidateDirectory '<新的候选目录>'` 指定一个尚不存在的目录。输出完整 ZIP、ZIP 外的 `SHA256SUMS.txt` 和列出路径的 `candidate.json`。

ZIP 内的 `FILES.sha256` 覆盖全部其他文件。`build-info.json` 记录来源提交、工作树是否含未提交修改、变更文件列表和 WebUI payload SHA-256。打包后逐项比对 ZIP 与完整 publish 输出；遗漏 DLL、运行时或子目录文件会使构建失败。WebUI payload 只包含源码、静态文件、模板与许可证，不包含用户数据、密钥、模型或 `.venv`。构建使用当前工作树，未提交内容会明确标记。

```powershell
# 替换两个路径占位符；从完整解压目录执行只读导入诊断
& '<解压目录>\AnimaRandomStudio\AnimaRandomStudio.exe' --diagnose '<实际ComfyUI目录>' --report './diagnosis.json'
# 已有环境的真实生成验收；会实际生成一张图片
dotnet run -c Release --project launcher/Anima.Launcher.Tests -- --live '<实际ComfyUI目录>' --generate
# 新建环境验收会下载 Python / CUDA wheel / 节点及依赖，请选新的空目录
dotnet run --project launcher/Anima.Launcher.Tests -- --install-new '<新的空目录>'
```

不在普通 CI 中运行 GPU 或大模型下载验收。Windows CI 运行核心测试、Python 测试、打包校验测试，并上传完整候选 ZIP 与校验文件；它不会创建 GitHub Release。清单维护者必须在改动提交或依赖版本后重新执行真实安装和生成。

交付前应从 ZIP 开始验收：核对外部 SHA-256，解压到新目录，核对 `FILES.sha256`，使用独立 `ANIMA_LAUNCHER_HOME` 执行 `--diagnose`，再确认 WinUI 窗口和工作台健康接口。`--diagnose` 成功不代表窗口资源已验证。已有启动器运行时，固定单实例锁会唤醒旧窗口，因此必须核对进程可执行路径；不要把旧窗口当成新候选启动成功。

## 发布

正式宣发前使用可信的代码签名证书。示例：

```powershell
pwsh ./Build-Launcher.ps1 -CertificateThumbprint '<CurrentUser/My 代码签名证书指纹>'
```

签名完成后才生成 SHA-256。没有证书时生成的是**未签名候选包**；不应声称已完成 Authenticode。发布方必须逐项复核模型作者许可与再分发条款；哈希证明文件身份，不代表授予模型使用或分发许可。第三方链接和版本见三份清单。
