# ComfyUI 稳定版轻量更新验收（2026-11-15）

本文记录首次轻量更新实现的验收。后续真实更新暴露的旧工作台可编辑安装信息冲突，其修复与实际环境恢复另见[工作台可编辑安装信息修复](workbench-editable-repair-2026-11-15.md)。

## 实施范围

现有稳定版更新改用轻量恢复点，查询和安装分离。保留当前工作区其他功能的修改，不修改工作台 HTTP API，不增加开发版或历史版本选择。绘世启动器仅作交互参考；静态检查不能证明其内部备份策略，本实现不依赖该推断。

检查更新保存官方稳定标签和完整提交。依赖准备联合解析核心、Manager、工作台与现有节点声明，固定下载版本和哈希，校验缓存并先构建所需 wheel；之后才切换源码、应用离线资源和探测核心。更新入口不运行节点安装脚本，不下载模型，不修复可选加速组件，保留 GPU 包版本。

恢复记录新增格式、策略、原分支／提交、目标提交、专用 Git 引用、依赖清单、配置哈希与事务阶段。新记录不复制完整 Git、Python、源码、模型或 `user/`。源码切换使用非强制 Git 操作，配置恢复拒绝覆盖后续用户修改。依赖失败后按恢复版本显式修复；旧包清单仅作诊断。旧完整备份的恢复逻辑和节点更新的完整备份策略继续保留。

空间预算按实际 wheel 下载／解包、缓存与 Python 目标卷安装量计算，每卷另留 256 MiB；源码和少量配置分别预留空间，不扫描现有 Python 树来计算完整备份量。

## 自动检查

| 范围 | 结果 | 证据或命令 |
| --- | --- | --- |
| 稳定版、轻量恢复、旧完整备份、状态展示、取消与中断 | 98 项通过 | `output/launcher-update-stage-tests/comfy-release-only.log`；`--comfy-release-only` |
| 依赖准备／应用、真实隔离 venv、缓存哈希、空间预算和构建隔离 | 157 项通过 | `output/launcher-tests/75a7544a0f3945409a34e86d4f4c9183/dependency-installation/`；`--dependency-installation-only` |
| 隔离核心探测、进程身份、HTTP／核心节点、取消清理 | 31 项通过（含 6 项通用检查） | `output/launcher-tests/29b29efcf4b2463c959e6a01ab1e3c95/`；`--comfy-update-probe-tests` |
| 普通启动参数与进程复用兼容性 | 66 项通过 | `--launch-arguments-only` |
| 节点更新／完整回退、核心交叉回退及进程／缓存／运行文件兼容性 | 210 项通过 | `output/node-light-recovery-tests/final-stdout-tail.log`；夹具 `output/launcher-tests/3a17c2b2160a47bca37640e7ac6754cb/`；`--node-updates-only` |

各组包含重复的通用检查，不相加宣称去重总数。依赖证据 JSON 记录主体 151 项，最后 6 项构建测试使控制台总数达到 157。节点命令的 210 项也包含 Windows 进程、缓存、界面展示、Forbidden Vision 兼容性和运行文件规划，不全是节点单独用例。该组新增 7 项检查轻量核心恢复与节点完整恢复的顺序和修复互锁。

恢复专项覆盖成功、无需更新、下载失败、源码修改、未跟踪文件冲突、配置并发修改、固定目标重试、独立 venv／便携 Python，以及旧 full 目录交换中断。准备取消不发布恢复点；源码切换与健康检查取消使用独立恢复令牌；连续两次回退中断后仍能从原 Git 引用离线恢复。失败的依赖修复验证不能清除待修复状态。

节点交叉回退回归发现旧测试仍假定核心回退会还原 Python，现已按轻量语义校正。同时补上节点手动回退的恢复状态检查：核心源码虽然已恢复，但依赖待修复时仍不能覆盖 Python；确认核心恢复后才能执行节点原有的完整回退。

## 真实 ComfyUI 核心探测

仅调用新 `ServiceManager.ProbeComfyUpdateAsync`，没有执行安装、更新或普通启动维护。

- 环境：`F:\comfyui`，源码提交 `426f5eea52a7ece1dd599fff4cb6cd1f352ddd24`，ComfyUI 0.43.0，Python 3.11.9，torch 2.13.0+cu130。
- 独立端口 1109，约 19.21 秒通过。核对探测 PID／监听归属、`/system_stats`、核心节点；禁用 Manager、自定义节点与资源数据库扫描，前端、用户、输入、输出和缓存目录均为临时路径。
- 前后 17,457 条快照无差异。`user/`、正式配置及资源绑定检查 SHA-256；input/output/temp/custom_nodes/cache 检查完整路径、长度、修改时间及属性，未对 30 GB 正式输出逐文件内容哈希。
- 正式 8188 服务 PID 13084 与 venv 父 PID 37244 保持运行并响应；探测进程、临时目录全部清理，正式源码 HEAD 和 tracked 状态不变。

证据：`output/launcher-real-comfy-probe-20261115-9c2a1b16/result.json`、`before.json`、`after.json` 和 `isolated-state/logs/ComfyUI-update-probe.log`。

## 界面与构建

界面使用 `output/comfy-update-ui-c09a969b/` 内独立 Git／venv 和独立启动器状态目录。`v0.0.1`、`v0.0.2` 与待修复记录均为 UI 夹具，不是实际发布或更新结果。已检查浅色版本展示、固定目标恢复、发布日期与说明入口，以及缺少隔离参数时在修改前拦截并显示原因；Git HEAD 不变且没有建立恢复记录。深色待修复场景显示修复入口并阻止启动，日志可导航返回。

最终 `-2` 候选在独立 `state-final` 中运行，窄窗口截图为 794×814；恢复说明、修复／日志入口可完整滚动访问，节点维护入口随未完成恢复禁用。日志页返回后仍保留准确的待修复状态。截图与 UI 树保存为 `output/comfy-update-ui-c09a969b/final-dark-narrow.png` 和 `final-dark-narrow-tree.txt`。每个同版本候选使用独立启动器状态，避免复用之前候选登记的活动 EXE。独立预览已关闭。

最终构建命令：

```powershell
.\Build-Launcher.ps1 -CandidateDirectory 'dist\candidates\comfy-light-update-20261115-2'
```

最终构建成功，日志无 warning/error：`output/comfy-light-update-build-final.log`。生成单文件 EXE、安装器、便携 ZIP 和 SHA256SUMS；EXE SHA-256 为 `ef85e84b46d74244116268e0ea57591d1040840e8f1bbdfc9851acae68db6f2f`。前一候选构建曾因独立预览占用 DLL 产生复制重试，退出预览后完成；交付使用重新构建的 `-2` 候选。

候选保留版本号 1.6.2，`build-info.json` 标记构建时工作区 `sourceDirty=true`；该候选在本次源码提交前构建。未发布 GitHub Release，也没有替换日常 EXE、快捷方式和 ComfyUI 环境。

## 验证边界

源码更新／回退事务使用隔离 Git 仓库和故障注入验证，依赖准备／安装使用实际独立 venv 和 wheel，真实 ComfyUI 使用上述独立核心探测验证。未在日常环境执行实际稳定版升级或 Python 改包。核心探测通过不等于所有插件及 GPU 生图链路已完成验证；插件可用性仍由正常启动流程检查。
