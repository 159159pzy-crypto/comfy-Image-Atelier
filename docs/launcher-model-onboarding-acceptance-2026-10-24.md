# 启动器模型下载与新手导向验收记录

日期：2026-10-24（Asia/Shanghai）。本文记录自动化与原生 WinUI 验收证据。最终交付为本地 r3 候选包；未进行 GitHub 发布，未替换日常启动入口或桌面快捷方式。

## 已验证结果

- 模型资源列表重新扫描后仍显示全部 15 项；可下载项目的勾选在重扫后保留。
- 新手导向的“检查确认”包含必需基础模型和默认未勾选的可选模型，选择后重新计算下载量与磁盘预留。导入后的下载使用同一批所选资源，显示整批及当前文件进度，可以暂停并在重启后继续。
- 提示词助手作为安装后的独立步骤，可以保存供应商及默认文本模型，也可以跳过。保存忙碌时禁用继续与跳过，保存结果经回读核验；跳过没有自动请求供应商。
- 原先需要手动导入的 Krea 2、SeedVR2 共 4 项已补齐固定来源、大小、SHA-256 与许可链接。

## 自动化与构建证据

最终完整回归为 **525 项通过**，日志：[launcher-regression-20261024-r3.log](/F:/comfyuishengtu/output/launcher-regression-20261024-r3.log)。其中包含 onboarding 182 项、提示词助手 52 项；这些子集不与总数重复相加。后增加的检查覆盖未选可选模型的状态与目录预览，并验证其不改变所选下载量或磁盘预算。

最终 [r3 构建日志](/F:/comfyuishengtu/output/launcher-build-20261024-r3.log) 成功，单文件、只含同一 EXE 的 ZIP、安装包及内嵌文件清单检查均通过。构建版本为 `1.2.0`，提交基线 `07c86e781ca3c667c950724172f133b9b715b33c`，`sourceDirty=true`，内嵌工作台取自构建时当前工作区。现有其他修改保留；本次不提交或发布。

| 交付文件 | SHA-256 |
| --- | --- |
| [AnimaRandomStudio.exe](/F:/comfyuishengtu/dist/candidates/onboarding-models-20261024-r3/AnimaRandomStudio.exe) | `14ba73da0d9fbcf812b5222b3407f295724b80b41af5dc41705fd821ba33a5f1` |
| [便携 ZIP](/F:/comfyuishengtu/dist/candidates/onboarding-models-20261024-r3/AnimaRandomStudio-portable-win-x64.zip) | `38682cf5933ead0342937ea5fd010efbec3c47e437815a466338c54723c448b2` |
| [安装包](/F:/comfyuishengtu/dist/candidates/onboarding-models-20261024-r3/AnimaRandomStudio-Setup-win-x64.exe) | `d878f9ec0545efd06cbdd3301617b81c8a8d4184a54fbbce71789e4acd93e794` |

完整来源与构建时间见 [build-info.json](/F:/comfyuishengtu/dist/candidates/onboarding-models-20261024-r3/build-info.json)。

模型列表专项回归覆盖可观察分组的完整发布、连续重扫、勾选恢复、下载完成或异常文件取消选择、刷新失败保留旧快照，以及 SeedVR2 VAE 别名识别。下载与导向回归还覆盖计划持久化、来源和目录变化、剩余空间估算、批次及文件进度、取消/暂停与恢复。提示词助手回归覆盖服务与模型回读、默认 LLM/VLM 隔离、空密钥保留、明确清除、部分成功后的重试，以及避免自动请求远端模型。

本地 HTTP 夹具另外通过真实 `PromptAssistantClient` 的 8 项 CRUD 检查，以及实际 `SetDefaultModel` HTTP 调用、默认 LLM/VLM 隔离、无自动远端模型读取、无生成调用和测试密钥不落盘检查。夹具与复现方式见 [README.md](/F:/comfyuishengtu/output/launcher-ui-fixture-20261024/README.md)。这些检查不等同于真实供应商可用性验证。

## 原生 WinUI 验收

交互使用原生启动器窗口。模型真实扫描场景指向已有 `F:\comfyui`，启动器状态放在独立 `ANIMA_LAUNCHER_HOME`；导向、供应商写入与实际下载场景使用 `output/launcher-ui-fixture-20261024/fake-comfy`，没有把验收下载写进真实模型目录。

| 场景 | 已观察结果 | 本地证据 |
| --- | --- | --- |
| 模型列表重新扫描 | r1 在真实模型环境连续扫描两次，7 个单文件资源与 8 个 Tagger 包共 15 项保持可见。r2 在假环境勾选 Qwen 文本编码器后再次扫描，勾选保持。 | [真实扫描树](/F:/comfyuishengtu/output/launcher-ui-fixture-20261024/models-rescanned.txt)、[r2 勾选截图](/F:/comfyuishengtu/output/launcher-ui-fixture-20261024/r2-selection-rescan.jpg)、[r2 勾选树](/F:/comfyuishengtu/output/launcher-ui-fixture-20261024/r2-selection-rescan.txt) |
| 检查确认与模型选择 | 基础模型预计下载约 5.2 GiB。勾选 SeedVR2 后两个模型 ID 写入向导记录，预计下载变为约 8.9 GiB、磁盘预留约 9.4 GiB。Krea 2 使用共享 Qwen VAE。 | [选择截图](/F:/comfyuishengtu/output/launcher-ui-fixture-20261024/review-selected.jpg)、[检查确认树](/F:/comfyuishengtu/output/launcher-ui-fixture-20261024/review-selected.txt) |
| 本地反推选项 | 展开后展示全部 8 个 Tagger 包，均默认未选；PixAI 0.9 标注推荐，没有把推荐等同于自动勾选。 | [Tagger 选项树](/F:/comfyuishengtu/output/launcher-ui-fixture-20261024/review-taggers.txt) |
| 真实下载进度、暂停与重启恢复 | 导入后进入包含 3 个基础模型及 2 个 SeedVR2 资源的 5 项批次。实际从官方来源下载 Anima 文件的一部分；页面同时显示 `0/5`、整批字节与速度、当前文件字节与速度。约 3.3 MiB 时暂停；r2 重启后恢复同一批 5 项并继续，验收停止时 `.part` 为 **10,431,096 字节**。没有完成整份权重下载或最终哈希验证。 | [进度截图](/F:/comfyuishengtu/output/launcher-ui-fixture-20261024/download-progress.jpg)、[进度树](/F:/comfyuishengtu/output/launcher-ui-fixture-20261024/download-progress.txt)、[暂停树](/F:/comfyuishengtu/output/launcher-ui-fixture-20261024/download-paused.txt)、[重启恢复树](/F:/comfyuishengtu/output/launcher-ui-fixture-20261024/r2-restored.txt)、[继续树](/F:/comfyuishengtu/output/launcher-ui-fixture-20261024/r2-resume.txt) |
| 供应商保存与回读 | 假 ComfyUI HTTP 服务与原生界面配合验收。第一次默认模型回读不一致被启动器拦下；修正并重启夹具后，在原窗口重试，复用 `existing` 服务 ID 成功，向导记录为 `assistantStatus=2`、`step=4`。默认 LLM 为 true，原 VLM 默认仍为 true；留空密钥保留已配置状态，没有新增服务。 | [保存忙碌树](/F:/comfyuishengtu/output/launcher-ui-fixture-20261024/assistant-save-busy.txt)、[重试完成树](/F:/comfyuishengtu/output/launcher-ui-fixture-20261024/assistant-save-retry.txt)、[夹具请求与状态](/F:/comfyuishengtu/output/launcher-ui-fixture-20261024/fixture-state.json) |
| 保存期间的操作约束 | 夹具延迟响应时，“保存并继续”和“稍后配置”均禁用，显示正在保存或读取配置。 | [忙碌状态树](/F:/comfyuishengtu/output/launcher-ui-fixture-20261024/assistant-save-busy.txt) |
| 跳过与较小深色窗口 | 在约 849×813 的深色窗口执行跳过，进入完成步骤；记录为 `assistantStatus=3`、`step=4`。夹具请求数从 29 到 29，跳过本身没有新增请求。 | [深色窗口截图](/F:/comfyuishengtu/output/launcher-ui-fixture-20261024/assistant-dark-small.jpg)、[跳过后树](/F:/comfyuishengtu/output/launcher-ui-fixture-20261024/assistant-skipped.txt)、[跳过前计数](/F:/comfyuishengtu/output/launcher-ui-fixture-20261024/skip-before.json) |

供应商验收期间，夹具统计的 `remote_model_reads=0`、`generation_requests=0`。API Key 只用“已配置”布尔状态模拟，夹具不保存测试密钥内容。真实启动器配置哈希检查保持不变。

夹具本身在验收中补齐了 Anima Tools 的 4 个数据文件，并核验 11 个节点的发现/安装计划全部指向隔离目录。供应商夹具加强了默认模型端点的字段与目标校验，并增加真实客户端 `SetDefaultModel` 自测。夹具修改只发生在 `output`；供应商保存失败及后续修复不能被记为真实供应商服务故障。

## 四项下载来源核验

下列来源已核验 Hugging Face API 的文件元数据及最终 CDN HEAD：均返回 HTTP 200，Content-Length 与清单大小一致。权重下载链接固定到提交 revision；本轮未完整下载这些 4 项权重，因此不声称完成本地文件 SHA-256 复算。

| 资源 ID | 精确字节数 | SHA-256 | 固定下载地址 |
| --- | ---: | --- | --- |
| `krea2-turbo` | 13,492,686,496 | `8e4eeda70dd5037ab1ba2bef6b417f9f901e26093117cf397f741fc1fdaaf3f1` | [Krea 2 Turbo INT8](https://huggingface.co/Comfy-Org/Krea-2/resolve/1171f4dc7e325bf3734586942e737872506c73e1/diffusion_models/krea2_turbo_int8_convrot.safetensors) |
| `qwen3vl-krea2` | 5,242,467,968 | `54bd5144df0bbc25dd6ccadfcb826b521445a1b06ae5a42570bdd2974ca87094` | [Qwen3 VL 4B FP8](https://huggingface.co/Comfy-Org/Krea-2/resolve/1171f4dc7e325bf3734586942e737872506c73e1/text_encoders/qwen3vl_4b_fp8_scaled.safetensors) |
| `seedvr2-model` | 3,458,259,704 | `c3dec8bcc5916843a8a858572970597462e1f2dc598d6dfd818f6cd40f53a157` | [SeedVR2 3B INT8](https://huggingface.co/Comfy-Org/SeedVR2/resolve/df48879708206a403d2a61acd55578c2e80fd233/diffusion_models/seedvr2_3b_int8_convrot.safetensors) |
| `seedvr2-vae` | 501,324,814 | `20678548f420d98d26f11442d3528f8b8c94e57ee046ef93dbb7633da8612ca1` | [SeedVR2 EMA VAE FP16](https://huggingface.co/Comfy-Org/SeedVR2/resolve/df48879708206a403d2a61acd55578c2e80fd233/vae/ema_vae_fp16.safetensors) |

Krea 两项保留 [Krea 2 Community License](https://cdn.jsdelivr.net/gh/krea-ai/krea-2@db3984fbc6e13b34c0064990fc2d95ac64d00058/assets/hf_samples/LICENSE.pdf) 链接；SeedVR2 两项标注 Apache-2.0，并链接[作者固定版本模型卡](https://huggingface.co/ByteDance-Seed/SeedVR2-3B/blob/37255ff8cccfb01071b87f635a5948ca8d53117c/README.md)。SeedVR2 VAE 的官方另名 `seedvr2_ema_vae_fp16.safetensors` 与清单名称具有相同大小和 SHA-256，已作为别名加入，避免已有文件被重复下载。

## 最终包复核

r3 已实际启动并恢复上次的模型下载阶段。环境工具、节点和依赖显示“环境已就绪 / 已完成”，提示“环境已安装，所选模型尚未准备完成”，继续按钮可用，两个 SeedVR2 选择仍保留。见 [r3 恢复截图](/F:/comfyuishengtu/output/launcher-ui-fixture-20261024/r3-restored.jpg) 与 [界面树](/F:/comfyuishengtu/output/launcher-ui-fixture-20261024/r3-restored.txt)。

返回目录页并重新检查后，r3 中未勾选的 Krea 2 两项也显示实际“缺失”状态，所选下载仍为约 8.9 GiB，按钮为“连接并准备模型”。见 [r3 确认页树](/F:/comfyuishengtu/output/launcher-ui-fixture-20261024/r3-review-preview.txt)。

r3 供应商表单加载后，对夹具注入一次 `/system_stats` 失败，保存入口回到“启动并配置”，并明确提示连接中断、表单已保留。重新连接后，原服务 ID、名称和默认文本模型仍保留；再次保存成功，进入完成页，`assistantStatus=2`、`step=4`，LLM/VLM 默认各自保留，远端查询和生成计数仍为 0。见 [断线恢复树](/F:/comfyuishengtu/output/launcher-ui-fixture-20261024/r3-assistant-disconnected.txt) 与 [最终保存完成树](/F:/comfyuishengtu/output/launcher-ui-fixture-20261024/r3-assistant-saved.txt)。

## 验收边界

- 本轮没有全量下载 Anima、Krea 2、SeedVR2 或 Tagger 大模型，也没有完成这些下载文件的最终本地哈希核验。实际网络下载证据限于隔离目录中的 Anima 部分文件、暂停和重启续传。
- 没有重新执行全新 GPU 环境的完整 ComfyUI/Python/CUDA/PyTorch 安装；本轮原生安装流程使用隔离的已有环境导入场景。
- 没有向真实或付费供应商发送请求，未验证其认证、模型列表、计费或生成能力。供应商证据来自真实客户端及原生 UI 对本地假服务的调用。
- 较小深色窗口只验证了上述尺寸和操作，不能代表所有 DPI、分辨率或系统辅助功能组合。
- r1/r2 截图记录对应构建的完整交互验收；r3 的检查范围另见“最终包复核”，没有将早期截图当作最终二进制截图。
