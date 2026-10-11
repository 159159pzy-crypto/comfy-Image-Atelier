# Anima Random Studio

基于 [ComfyUI](https://github.com/comfyanonymous/ComfyUI) 和 [Anima Tools](https://github.com/nregret/Comfyui-Anima-Tools) 的本地随机生图工作台。它把提示词池、批量生成、LoRA、模型与修复、收藏树、风格预设和历史记录集中在一个轻量 WebUI 中，并默认只连接本机服务。

## 界面预览

![Anima Random Studio v7 工作台界面](docs/screenshots/anima-studio-v7.png)

## 项目适合谁

- 已经在本机运行 ComfyUI，希望批量抽取 Anima 提示词并生成图片。
- 希望把常用 LoRA、画师、尺寸和修复开关保存成可复用风格。
- 需要在不直接暴露 ComfyUI 的情况下，通过一个本机 WebUI 管理生成任务。
- 想保留 ComfyUI 原生工作流，同时获得更清晰的随机池和收藏管理。

项目不打包任何模型、LoRA、检测器或第三方节点。Windows 用户可使用安装启动器导入已有 ComfyUI，或自动创建 NVIDIA 环境并按需下载模型。

## Windows 安装启动器

双击 `AnimaRandomStudio.exe`，按“选择 ComfyUI 目录 → 检查或安装环境 → 勾选缺失模型 → 启动工作台”完成首次配置。之后双击自动检查并启动服务，托盘提供打开工作台、启动/停止、检查资源、设置与日志。已有 `models/clip`、`models/unet` 和 `extra_model_paths.yaml` 中的资源会被识别，模型必须勾选才下载。

源码构建运行 `pwsh ./Build-Launcher.ps1`，输出位于 `dist/launcher/`；新版基于 **WinUI 3**，以自包含文件夹分发（`AnimaRandomStudio.exe` + 依赖 DLL，内置 .NET / Windows App Runtime / WebUI），无需为启动器单独安装运行时。详见 [启动器使用、构建与发布说明](launcher/README.md)。首次发布包仍需可信 Authenticode 签名。

## 主要功能

### 生成工作台

- 批量生成、批次队列(运行中再开批次自动排队接续)、停止任务和生成历史。
- 单张采样进度与实时预览(经由 ComfyUI websocket,断连不影响批次本身)。
- 历史详情支持可靠回放与三类变体：按原双种子重跑、冻结最终提示词仅更换图像种子、按角色/服装/姿势/背景/表情选择性重抽，以及按原配置全部再抽。同提示词变体仍是文生图，不是 Img2Img。
- 公共模型选择器只有 **Anima** 与 **Krea 2** 两个档案。切换档案会一起切换 UNet、CLIP、VAE、编码类型、采样默认值、SeedVR2 默认资源和数据命名空间。
- Anima 保留角色、服装、姿势、背景、表情五类确定性随机池；`prompt_seed` 控制池抽取，`sample_seed` 控制 ComfyUI 噪声。Krea 2 使用独立的自然语言池，初始为空，可导入 JSON 或 CSV 后再抽取。
- 每个 Anima 池支持随机、固定、关闭、搜索、分类/特征筛选、手动选择和排除项。LoRA、触发词、风格预设和池数据均按模型档案隔离；升级时旧全局数据迁移到 Anima 并先备份。
- 女性人数、男性人数、每类抽取数量、宽高、步数、CFG、Sampler、Scheduler、正负提示词和画师均可独立设置。
- 全部提示词字段支持安全的 Anima Tag 自动规范；支持本地“原词 → 规范词”替换规则，并按正向、负向、LoRA 三种范围启停。

### 模型、LoRA 与修复

- 默认 Anima 绑定 `anima-base-v1.0.safetensors`、`qwen_3_06b_base.safetensors`、`qwen_image_vae.safetensors` 和 `stable_diffusion` 编码类型；Krea 2 绑定 `krea2_turbo_int8_convrot.safetensors`、`qwen3vl_4b_fp8_scaled.safetensors`、`qwen_image_vae.safetensors` 和 `krea2` 编码类型。
- LoRA 支持 `models/loras` 下的任意安全子目录；选择器按当前模型档案读取清单、预览、触发词和覆盖值，不会把另一档案的配置带入当前提交。
- 高清链按顺序独立旁路：底图 → 潜空间放大 → SeedVR2 修复放大 → N 卡放大 → ForbiddenVision 脸部细化。旧 `UpscaleModelLoader` / `ImageUpscaleWithModel` 链、CFGZeroStar 和四类独立 ADetailer 不再进入 v2 提交。
- 底图模式严格二选一：普通底图或底图－双采样器；默认值由模型档案提供。关闭的修复阶段会清除提交中的孤立节点和连接。
- 批次启动前校验当前档案的 UNet、CLIP、VAE、采样器、启用的 LoRA、SeedVR2 资源及可选处理节点；N 卡放大和脸部细化预检检查节点/插件可用性，不承诺独立模型文件扫描。
- `GET /api/resources` 同时返回两个档案的绑定资源、节点状态和可选插件状态，便于启动器与前端显示可用性。

### 收藏与提示词分组

- 内置条目和自定义条目可以分别收藏；画师以每个 `@name` 为独立收藏项。
- 自定义提示词分组使用可折叠树：每个导入文件成为顶层大项，文件内 `groups` 成为子分组；手动分组保持顶层叶子。
- 点击导入大项会聚合筛选全部后代条目并去重；同名文件再次导入时复用原大项，无 `groups` 的条目进入“未分组”。
- 收藏分组是 Finder 式任意层级树。顶层分组可手动创建，子分组只能从当前分类的非空自定义分组一次性快照导入。
- 父收藏组会聚合所有后代条目并自动去重；同一个自定义分组可以导入到不同父组，但不会持续同步。
- 删除收藏组默认只删除分组结构并保留条目；被删分组失去最后归属的条目会转入“我的收藏”。
- 只有非顶层、非系统、非空叶子收藏组可以选择“同时删除条目”；共享条目只解除当前分组关系。
- 删除自定义分组提供三种方式：默认仅解绑；“同时删除独占内容”保留共享条目；“彻底删除全部关联内容”会连同完整子树及共享内容永久删除。
- 每个词条池可将“本次选择”批量收藏到一个或多个收藏分组；跨页选择和“全选词库”状态都会完整保存，已有收藏只追加分组并保留备注。
- 所有破坏性操作使用专用确认面板，不使用浏览器原生 `confirm()`。

### 风格预设与界面

- 风格预设按模型档案保存 UNet、CLIP、VAE 绑定、LoRA、尺寸、步数、CFG、底图模式、潜空间 / SeedVR2 / N 卡 / 脸部细化开关、高级提示词和画师。
- 支持创建、覆盖、重命名、删除和星标收藏；列表按收藏优先、最近更新排序。
- 应用预设会整体替换上述字段，之后可以继续逐项调整，不会自动回写预设。
- 预设不包含批次数量、人物数量或五类提示词池选择。
- 浅色/深色主题可手动切换并记忆；布局使用系统字体、紧凑分组、清晰分隔和克制的 180–220ms 动效。
- 支持 `prefers-reduced-motion`、`prefers-reduced-transparency`、`prefers-contrast` 和键盘焦点。

## 环境要求

| 项目 | 要求 |
| --- | --- |
| 操作系统 | Windows 10/11（启动批处理使用 Windows 路径） |
| Python | 3.11 或更高版本 |
| ComfyUI | 可正常运行的本机实例，默认 `http://127.0.0.1:8188` |
| WebUI | 默认监听 `http://127.0.0.1:8190` |
| Python 包 | `aiohttp`；开发/测试再安装 `pytest` |
| 浏览器 | Chromium、Edge 或 Chrome；移动端可使用响应式视口 |
| GPU | 由 ComfyUI 的 Anima 工作流决定；建议使用支持该模型的 NVIDIA GPU |

## ComfyUI 自定义节点

建议通过 ComfyUI Manager 安装并重启 ComfyUI。启动器清单会锁定 v2 提交所需的第三方包；下表列出模板实际使用的节点来源：

| 节点包 | 用途 |
| --- | --- |
| [Comfyui-Anima-Tools](https://github.com/nregret/Comfyui-Anima-Tools) | Anima 提示词池、画师/条目收藏、`AnimaPromptComposer` 和 `AnimaPromptPlusClipEncode` |
| [rgthree-comfy](https://github.com/rgthree/rgthree-comfy) | Power LoRA Loader、Seed 和分组控制 |
| [ComfyUI-Aaalice-Nodes](https://github.com/Aaalice233/ComfyUI-Aaalice-Nodes) | `PromptSelector`、`QuickGroupManager`、`ResolutionPreset` |
| [ComfyUI-Prompt-Assistant](https://github.com/yawiii/ComfyUI-Prompt-Assistant) | `PromptExpand` 提示词后处理 |
| [KreaSeedVarianceEnhancer](https://github.com/harukimix/KreaSeedVarianceEnhancer) | Krea 2 自然语言条件随机增强 |
| [ComfyUI-KJNodes](https://github.com/kijai/ComfyUI-KJNodes) | `ModelPassThrough`、`VRAM_Debug`、图像/潜空间辅助节点 |
| [ComfyUI-Lora-Manager](https://github.com/willmiao/ComfyUI-Lora-Manager) | 两组 LoRA、触发词和元数据节点 |
| [ComfyUI-Impact-Pack](https://github.com/ltdrdata/ComfyUI-Impact-Pack) / Subpack | 可选检测与图像处理节点 |
| ComfyUI 内置 SeedVR2 | `SeedVR2Preprocess`、`SeedVR2Conditioning`、`SeedVR2PostProcessing` |
| [ComfyUI-NVIDIA-RTX-VSR-Pro](https://github.com/whmc76/ComfyUI-NVIDIA-RTX-VSR-Pro) | `RTXVideoSuperResolution` N 卡放大（可选） |
| [ComfyUI-Forbidden-Vision](https://github.com/luxdelux7/ComfyUI-Forbidden-Vision) | `ForbiddenVisionFaceProcessorIntegrated` 脸部细化（可选） |

ComfyUI 核心节点（包括逻辑开关、采样、VAE 和 `SaveImage`）以及 SeedVR2 节点随 ComfyUI 提供；N 卡放大和脸部细化需要另行安装对应插件。开启可选阶段前，先在 `/api/resources` 确认节点和插件状态；这两个阶段的预检不虚构独立放大模型或脸部检测模型文件。v2 提交不包含 CFGZeroStar、旧 `UpscaleModelLoader` / `ImageUpscaleWithModel` 或独立 ADetailer。

## 模型与文件放置

模型档案由 `anima_webui/model_profiles.py` 定义，文件名必须和 ComfyUI `/object_info` 返回的名称一致。路径均相对于 ComfyUI 的 `models` 目录：

| 模型档案 | 类型 | 文件名 | 放置目录 | 是否需要 |
| --- | --- | --- | --- | --- |
| Anima | UNet | `anima-base-v1.0.safetensors` | `diffusion_models/` 或 `unet/` | Anima 必需 |
| Anima / Krea 2 | CLIP | `qwen_3_06b_base.safetensors` / `qwen3vl_4b_fp8_scaled.safetensors` | `text_encoders/` 或 `clip/` | 对应档案必需 |
| Anima / Krea 2 | VAE | `qwen_image_vae.safetensors` | `vae/` | 两个档案共用 |
| SeedVR2 | 修复模型 | `seedvr2_3b_int8_convrot.safetensors` | SeedVR2 节点要求的模型目录 | 启用 SeedVR2 时必需 |
| SeedVR2 | VAE | `ema_vae_fp16.safetensors` | SeedVR2 节点要求的 VAE 目录 | 启用 SeedVR2 时必需 |
| N 卡放大 | 节点与 GPU 能力 | `RTXVideoSuperResolution` / `NvidiaUpscale` | 节点包配置目录 | 启用 N 卡放大时必需；不使用独立放大模型文件 |

LoRA 放在 `F:\comfyui\models\loras\`，可以继续分目录，例如：

```text
F:\comfyui\models\loras\artist-a\style.safetensors
F:\comfyui\models\loras\character\blue_archive\kazusa.safetensors
```

重启 ComfyUI 或刷新其模型列表后，WebUI 才能读取新资源。启动器和 `/api/resources` 会按档案分别报告 UNet、CLIP、VAE、SeedVR2、节点及插件状态；缺失资源不会静默替换。

## 安装与启动

### 1. 启动 ComfyUI

先让 ComfyUI 正常运行，并确认浏览器可以打开：

```text
http://127.0.0.1:8188
```

先将附件基线 `D:\loud\workflow (1).json` 导入 ComfyUI，确认节点包已加载；仓库保留该前端工作流基线，并使用 `templates/workflow_api_v2.json` 作为 API Format 提交模板。仓库根目录的 `AnimaBasicV7-Random-WebUI.json` 仍用于旧版可视化工作流兼容，不会自动覆盖你的 ComfyUI 工作流。

### 2. 获取仓库

```powershell
git clone https://github.com/159159pzy-crypto/comfyui-random-image-generation.git
cd comfyui-random-image-generation
```

### 3. 安装运行依赖

如果复用已有 ComfyUI 虚拟环境：

```powershell
F:\comfyui\.venv\Scripts\python.exe -m pip install aiohttp
```

如果使用独立环境：

```powershell
py -3.11 -m venv .venv
.\.venv\Scripts\python.exe -m pip install --upgrade pip
.\.venv\Scripts\python.exe -m pip install aiohttp
```

### 4. 启动 Anima Random Studio

在默认安装位置 `F:\comfyuishengtu`，可以双击：

```text
启动Anima随机生图WebUI.bat
```

其他目录请修改批处理中的 `ANIMA_PYTHON`，或直接运行：

```powershell
python run.py
```

启动后访问：

```text
http://127.0.0.1:8190
```

常用参数：

| 参数 | 默认值 | 说明 |
| --- | --- | --- |
| `--host` | `127.0.0.1` | WebUI 监听地址；默认不暴露到局域网 |
| `--port` | `8190` | WebUI 端口 |
| `--comfy-url` | `http://127.0.0.1:8188` | ComfyUI 地址，只接受本机 HTTP |
| `--anima-tools-dir` | 自动探测 | 手动指定 `Comfyui-Anima-Tools` 目录 |
| `--no-browser` | 关闭 | 不自动打开浏览器 |

例如：

```powershell
python run.py --comfy-url http://127.0.0.1:8188 --port 8193 --no-browser
```

### 5. 首次检查

打开 WebUI 后依次确认：

1. `/api/resources` 返回 Anima 与 Krea 2 两个档案，并确认当前档案的 UNet、CLIP、VAE 名称可用。
2. 在模型选择器切换 Anima / Krea 2，检查绑定资源、编码类型、默认底图模式和 SeedVR2 资源随档案改变。
3. Anima 先关闭所有可选修复生成一张最小批次；再按潜空间、SeedVR2、N 卡、脸部细化顺序逐项开启。
4. 切换 Krea 2 后先导入自然语言 JSON/CSV 词池，再启用随机抽取；空池会在提交前明确提示。
5. 普通底图与底图－双采样器只能二选一；资源或插件缺失时应先修复环境，不要手动改 API payload 绕过检查。

## 使用说明

### 提示词池与批量导入

五个池的内部名称为 `character`、`clothing`、`pose`、`background`、`expression`。自定义条目至少需要名称和提示词；角色池还支持性别、头发、眼睛和作品字段。

JSON/CSV 批量导入只写入当前池。选择文件后，“导入大项名称”默认取文件名且可修改；导入前会校验格式、跨池数据、重名、目标叶子分组和树节点上限，不会留下半成功数据。

首次读取旧版 `data/custom_prompts.json` 时会升级为 v4：原有平铺分组进入各池的“历史自定义分组”，原有未分组条目进入其中的“未分组”。升级前会保留一次 `.pre-v4.bak` 备份。

### 收藏树

收藏树和自定义分组是两套不同数据：

- 自定义分组：导入大项与叶子分组组成的树；父项聚合后代，条目只保存叶子分组 ID，并可同时属于多个叶子。
- 收藏分组：用于收藏视图的任意层级树；“导入子分组”只复制当前条目快照，之后两边互不自动同步。

删除时请看确认面板统计：分组数、保留/转移条目数、独占条目数和共享条目数。删除任意自定义分组都会处理完整子树：`keep` 仅解绑，`exclusive` 删除子树独占内容，`all` 会永久删除子树关联的全部内容，即使内容同时属于其他分组。系统默认分组“我的收藏”不能删除或重命名。两棵树都支持展开箭头、层级计数、路径提示以及键盘方向键导航。

### 画师

输入 `anmi, rella`、`@anmi, @rella` 或粘贴多行内容后，WebUI 会统一保存和恢复为：

```text
@anmi, @rella
```

每位画师单独收藏、取消收藏和追加到提示词；昵称只作为辅助说明，不会替代最终提示词中的 `@name`。

### Anima Tag 规范与替换

质量词、固定画师、五类固定提示词、额外提示词、负面词和自定义提示词会在编辑失焦时调用同源规范接口。内置规则遵循 Anima 的安全 Tag 约定：普通 tag 使用小写和空格，`score_*` 保留下划线，独立年份写为 `year YYYY`，画师使用 `@artist`。自然语言句子、未知词、`;d` 和括号权重不会被强制拆改，也不会重新排列提示词区段。

“高级提示词”中的“管理规范与替换”可以停用内置规则或创建本地精确替换规则。自定义规则不支持正则、空替换或级联执行；LoRA 范围默认不改变触发词大小写和下划线，只有显式的 LoRA 自定义规则才会替换精确触发词。生成前浏览器会统一检查一次，服务端在批次校验前再次兜底，因此直接调用 API 也会得到相同结果。

### 风格预设

在“风格预设”中输入名称后保存。名称为空、重复或服务端写入失败时，表单会显示内联错误；成功后立即出现在列表中。预设文件只保存用户自建内容，采用本地原子写入：

```text
data/style_presets.json
```

## 数据与项目结构

```text
anima_webui/       Python 后端、ComfyUI 客户端、工作流和持久化
static/            WebUI HTML、CSS、JavaScript
templates/         workflow_api_v2.json、兼容 API/UI 模板
sources/           原始工作流副本
tests/             Python 测试
run.py             启动入口
data/              本地运行时数据（默认不提交）
output/            生成图片（默认不提交）
```

主要运行时文件：

| 路径 | 内容 |
| --- | --- |
| `data/history.sqlite3` | 批次、图片、种子、提示词和工作流元数据 |
| `data/custom_prompts.json` | v4 自定义条目、导入大项和树形分组 |
| `data/style_presets.json` | 按模型档案保存的用户自建风格预设（升级前自动备份） |
| `data/lora_trigger_overrides.json` | 按模型档案和 LoRA 文件保存的有效触发词覆盖，旧平面文件迁移到 Anima 前自动备份 |
| `data/prompt_pools.json` | Krea 2 自然语言池及其他档案的隔离数据 |
| `data/prompt_replacements.json` | 内置规范规则停用状态和用户自定义精确替换规则 |
| `output/AnimaRandom/<日期>/<批次>/` | 生成图片 |
| `templates/workflow_api_v2.json` | 以附件工作流导出的 API Format 基线，提交前由渲染器按档案和开关绑定 |
| `templates/workflow_api.json` | 旧 Anima API 兼容模板，供历史回放和升级路径使用 |
| `templates/workflow_ui.json` | 旧可视化工作流兼容模板；附件前端基线另保存于 `D:\loud\workflow (1).json` |
| `AnimaBasicV7-Random-WebUI.json` | 旧版可视化工作流兼容文件 |

收藏条目、收藏画师和收藏分组由 Anima Tools 的本地接口保存；不会写入本项目的 Git 工作区。`data/`、`output/`、缓存和日志默认由 `.gitignore` 排除。

## API 入口

WebUI 前端使用以下本地接口：

| 方法 | 路径 | 作用 |
| --- | --- | --- |
| `GET` | `/api/resources` | 当前清单及 Anima / Krea 2 的模型、CLIP、VAE、SeedVR2、节点和插件状态 |
| `GET` | `/api/loras?model_profile=anima|krea2` | 按模型档案过滤 LoRA 清单、预览地址及触发词元数据 |
| `GET` | `/api/prompt-pools?model_profile=krea2` | 读取模型隔离的自然语言池 |
| `POST` | `/api/prompt-pools/import` | 以 JSON/CSV 导入 Krea 2 或指定档案的自然语言池（`replace` / `append`） |
| `GET` | `/api/loras/preview?filename=...` | 代理已入库 LoRA 的本地预览资源 |
| `PUT/DELETE` | `/api/loras/triggers` | 保存全局有效触发词覆盖，或按 `filename` 查询参数恢复管理器原值 |
| `GET/POST` | `/api/prompt-rules` | 列出规则或创建自定义精确替换规则 |
| `PUT/DELETE` | `/api/prompt-rules/{id}` | 启停内置规则，或更新/删除自定义规则 |
| `POST` | `/api/prompts/normalize` | 按字段和托管 LoRA 词返回规范结果及变更摘要 |
| `GET/POST` | `/api/style-presets` | 列出或创建风格预设 |
| `PUT/DELETE` | `/api/style-presets/{id}` | 更新或删除预设 |
| `GET` | `/api/favorites/{section}` | 收藏条目、树分组及聚合计数 |
| `POST` | `/api/favorites/{section}/selection` | 将当前池的完整选择原子追加到一个或多个收藏分组 |
| `POST` | `/api/favorites/{section}/groups/{parent_id}/children/import` | 从自定义分组导入收藏子分组快照 |
| `DELETE` | `/api/favorites/{section}/groups/{group_id}?deleteItems=false` | 删除收藏分组；默认保留条目 |
| `GET` | `/api/custom-groups/{section}` | 自定义分组树、聚合计数及独占条目统计 |
| `POST` | `/api/custom-prompts/import` | 按 `bundleName` 原子导入或复用导入大项 |
| `DELETE` | `/api/custom-groups/{section}/{group_id}?deleteMode=keep` | 删除自定义分组子树；支持 `keep`、`exclusive`、`all`，并兼容旧 `deleteItems` 参数 |
| `POST` | `/api/batches` | 校验资源并启动批次;运行中则加入队列;可携带 `seeds` 固定种子复现 |
| `GET` | `/api/history/{image_id}/regeneration-options` | 检查历史回放能力、缺失资源与各提示词维度是否可重抽 |
| `POST` | `/api/history/{image_id}/regenerate` | 以 `replay`、`prompt_variant`、`content_redraw` 或 `settings_reroll` 模式再生成 |
| `GET` | `/api/batches/current/preview` | 当前生成的实时预览帧(无帧时 204) |
| `DELETE` | `/api/batches/queue/{queue_id}` | 移出排队中的批次 |
| `POST` | `/api/batches/{batch_id}/stop?clearQueue=true` | 停止当前批次,默认同时清空队列 |
| `GET` | `/api/history` | 分页读取历史记录 |

这些接口默认只绑定本机，不是面向公网的远程服务 API。

## 常见问题

### 模型下拉框为空或显示离线

先确认 ComfyUI 正在运行，并直接打开 `http://127.0.0.1:8188/object_info`。检查对应 Loader 是否存在，以及模型是否放在正确目录；移动模型后重启 ComfyUI 再刷新 WebUI。

### 提交时报“主模型不存在”或修复资源不可用

这是批次启动前的真实资源校验。请在 `/api/resources` 或 ComfyUI `/object_info` 中确认当前档案的 UNet、CLIP、VAE、SeedVR2 模型和节点完全可用，包含子目录、大小写和扩展名；N 卡放大与脸部细化检查插件/节点状态；WebUI 不会自动替换模型。

### LoRA 看不到或数量不对

确认文件位于 `models/loras`，并检查 `/object_info` 的 `LoraLoader` 列表。预览图和触发词来自 ComfyUI-Lora-Manager 已扫描的本地缓存；没有元数据时仍可选择和生成，只显示占位图且不会自动填写触发词。子目录使用 `/` 作为规范化分隔符；同名 LoRA 不会按 basename 猜测，旧配置需要重新选择明确路径。

### SeedVR2、N 卡放大或脸部细化执行失败

先确认 `/api/resources` 中对应插件节点为可用状态，并安装该档案要求的模型。建议先关闭全部可选修复生成一张图片，再按潜空间、SeedVR2、N 卡、脸部细化顺序逐项开启；关闭阶段会从 API payload 旁路并清除连接。

### Krea 2 提示词池为空

Krea 2 不复用 Anima 的五类 Tag 池。打开 Krea 2 面板，导入 JSON 字符串数组、`{"items": [...]}` 或每行一条的 CSV；导入成功后再启用随机抽取。`prompt_seed` 相同会选择相同自然语言条目。

### WebUI 能打开但 Anima 提示池为空

确认 `Comfyui-Anima-Tools` 路径可被探测。默认探测位置包括项目同级 `comfyui/custom_nodes/Comfyui-Anima-Tools` 和 `F:/comfyui/custom_nodes/Comfyui-Anima-Tools`；其他位置使用 `--anima-tools-dir`。

### 浏览器显示旧界面

停止并重新启动 WebUI，然后使用全新浏览器会话或硬刷新。静态资源带版本参数，启动时也会检查关键 DOM；初始化失败会显示错误面板而不是静默失效。

## 开发与测试

安装开发依赖并运行：

```powershell
python -m pip install aiohttp pytest
python -m pytest -q
node --check static\app.js
python -m compileall -q anima_webui tests run.py
Get-Content templates\workflow_api.json | ConvertFrom-Json | Out-Null
Get-Content templates\workflow_api_v2.json | ConvertFrom-Json | Out-Null
Get-Content templates\workflow_ui.json | ConvertFrom-Json | Out-Null
git diff --check
```

工作流源文件变化后，先重新生成兼容模板，再重启 WebUI。附件 v2 前端工作流保存在外部路径时，使用 ComfyUI 的 API Format 导出更新 `templates/workflow_api_v2.json`，同时保留原始前端 JSON 作为可视化基线：

```powershell
python generate_templates.py
# 之后在 ComfyUI 导出 D:\loud\workflow (1).json 的 API Format，覆盖 templates/workflow_api_v2.json
```

测试覆盖资源枚举、LoRA 子目录和路径安全、动态修复链、画师 `@` 规范化、风格预设、收藏树、分组快照导入、安全删除、自定义项、历史记录和批量导入。

## 安全边界与许可证

- 默认只监听 `127.0.0.1`、`localhost` 或 `::1`，不会直接暴露到局域网。
- `--comfy-url` 只接受本机 HTTP 地址，不接受远程主机、凭据、查询参数或片段。
- LoRA 路径拒绝绝对路径和 `..` 路径穿越；工作流资源使用 ComfyUI 返回的实时清单。
- 运行时历史、收藏、预设和生成图片默认不进入 Git；公开仓库前请再次检查是否误提交模型、图片或本机配置。
- 本仓库当前未附带 `LICENSE`。模型、LoRA、检测器和第三方节点遵循各自授权条款；公开分发前请根据实际资源授权补充项目许可证。
