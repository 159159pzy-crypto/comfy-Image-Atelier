# Windows 启动器

## 安装与使用

从 [1.0.1 Release](https://github.com/159159pzy-crypto/comfyui-random-image-generation/releases/tag/1.0.1) 下载 `AnimaRandomStudio-win-x64-unsigned.zip`，完整解压后运行 `AnimaRandomStudio/AnimaRandomStudio.exe`。源码 ZIP 不包含 EXE。

- **新建环境**：选择安装位置，检查安装内容，按提示安装。启动器准备独立 Python、Git、ComfyUI、必需节点和 Anima 基础模型；大文件放在所选安装盘。需要 Windows 10/11 x64 和支持 CUDA 12.8 的 NVIDIA 驱动（570.65 或更新版本）。
- **导入已有环境**：选择含 `main.py` 的 ComfyUI 目录或便携版父目录。支持 `.venv`、`venv`、`python_embeded`；其他布局可手动指定 Python。已有节点和 GPU 运行时保留。
- **开始生成**：完成配置后回到「概览」，点击「启动工作台」。默认 ComfyUI 端口为 8188，工作台端口为 8190；端口冲突时按提示修改。
- **模型**：Anima 基础模型可自动下载；Krea 2、SeedVR2 等按模型资源页提示导入。LoRA 支持子目录和 `extra_model_paths.yaml`，新增后请扫描并刷新分类。
- **下载失败**：可暂停、继续或重试，已校验资源会复用。模型校验不符时先处理已有文件，再重试。
- **退出**：关闭窗口会最小化到托盘；托盘菜单可选择保留服务退出，或停止本程序启动的服务后退出。

Anima Tools 按其 `js` 目录中的四个数据文件识别，支持 `-main`、`-master` 和自定义目录名。重复安装或数据不完整时会明确提示。

## 升级与数据

新版 ZIP 解压到新目录，退出旧启动器后运行新 EXE，再在设置中选择「应用内置新版」。保留完整目录；仅替换 EXE 会缺少运行资源。需要时可在空闲时回退上一工作台版本。

配置、日志和用户数据默认保存在 `%LOCALAPPDATA%/AnimaRandomStudio`。迁移前备份整个目录；旧源码版的数据在原项目的 `data` 中。启动器不会自动把旧源码版的数据迁入新位置。

## 源码运行

适合已经安装并能运行 ComfyUI 的开发者，需要 Python 3.11+。先启动 ComfyUI，在本仓库根目录运行：

```powershell
python -m pip install -e .
python run.py
```

浏览器访问 `http://127.0.0.1:8190`。如提示找不到词库，在启动命令后添加 `--anima-tools-dir '<实际的 Anima Tools 节点目录>'`；ComfyUI 使用其他端口时添加 `--comfy-url http://127.0.0.1:<实际端口>`。路径占位符需替换成自己的目录。

可将 `templates/workflow_ui_v2.json` 拖进 ComfyUI 检查缺失节点；应用实际提交使用 `templates/workflow_api_v2.json`。节点及版本见 [运行清单](https://github.com/159159pzy-crypto/comfyui-random-image-generation/blob/1.0.1/launcher/manifests/launcher-manifest.json)。

## 开发与构建

需要 .NET 8 SDK、Python 3.11+、Node.js 和 PowerShell 7，在仓库根目录运行：

```powershell
python -m pip install -e '.[dev]'
python scripts/check_release.py
dotnet run -c Release --project launcher/Anima.Launcher.Tests
pwsh ./launcher/scripts/Test-LauncherPackage.ps1
pwsh ./Build-Launcher.ps1
```

输出位于新的 `dist/candidates/<构建编号>` 目录：完整 ZIP、`SHA256SUMS.txt` 和 `build-info.json`。`candidate.json` 仅用于本地定位构建输出，不作为 Release 附件。包内 `FILES.sha256` 覆盖全部其他文件；构建来源、版本和应用负载哈希写入 `build-info.json`。正式发布要求提交已保存、工作树干净。

需要源码目录的双击入口时，可运行 `pwsh ./Build-Launcher.ps1 -InstallRootEntry`；生成的根目录 EXE 依赖同级 `.launcher`，两者均不进入 Git。普通用户使用上面的完整 ZIP 即可。

包内附带第三方许可和依赖清单；ComfyUI、节点与模型单独下载。已知依赖限制见 [依赖说明](https://github.com/159159pzy-crypto/comfyui-random-image-generation/blob/1.0.1/docs/dependencies.md)。
