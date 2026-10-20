# 参与开发

Anima Random Studio 是每位用户在自己电脑上运行的 ComfyUI 工作台。欢迎提交可复现的错误报告、文档改进和范围明确的修复。支持范围和安全问题报告方式见 [安全说明](SECURITY.md)。

## 准备独立环境

从 Git 克隆仓库后，在仓库根目录创建虚拟环境，避免改动已有 ComfyUI 的 Python 依赖：

```powershell
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -e '.[dev]'
.\.venv\Scripts\python.exe scripts/check_release.py
```

Linux / macOS 使用 `.venv/bin/python`。质量检查还需要 Node.js 22 或更新版本；普通后端开发不需要安装 .NET、GPU 模型或启动 ComfyUI。CI 在 Windows / Linux、Python 3.11 / 3.13 上运行后端与前端回归。Python 3.11 是启动器托管版本；其他版本的兼容性不等同于第三方节点兼容性。

`check_release.py` 检查版本、模板、公开文件、模型档案导出、Python 静态检查、所有前端脚本语法、Python / Node 回归及 Prompt Assistant 补丁夹具。公开文件检查覆盖 Git 已跟踪及尚未提交的非忽略文件，可在暂存前运行；发布时新文件仍需纳入提交，新公开 Markdown 还要明确加入 `scripts/check_release_content.py` 白名单。测试使用临时目录和 ComfyUI 替身，不会运行真实推理。

针对单个问题可先运行：

```powershell
.\.venv\Scripts\python.exe -m pytest -q tests/test_server.py
node --test tests/frontend_state.test.cjs
```

真实联调的启动参数和 Windows 打包步骤见 [启动器开发说明](launcher/README.md)。联调使用独立的 `--data-dir`，不要拿唯一一份作品库测试迁移、删除或恢复。

## 提交修复

1. 写清触发条件、预期行为和实际行为；异步问题优先用事件控制时序复现，不依赖不稳定的长时间休眠。
2. 给真实错误补回归测试。涉及历史记录、旧设置或收藏，验证旧数据仍可读，失败时不会覆盖原文件。
3. 保留模型档案隔离与历史复现语义：复现使用保存的最终提示词和原种子，同提示词变体只换图像种子；不得静默换模型。
4. 后端接口有变化时检查两个工作台的调用，更新用户说明与 `CHANGELOG.md` 的“未发布”部分。
5. 提交前运行完整质量检查。PR 写明通过的检查和未验证的真实 GPU / 浏览器场景，避免把替身测试当作实机证明。

## 长期维护与发布

- 常规 PR 通过与发布相同的质量门槛。依赖更新先提交 PR，再跑测试；Dependabot 不会替代对节点、模型或运行清单的人工审查。
- 源码运行依赖及其传递依赖、启动器锁定运行时分别审计。审计失败、网络失败或扫描不完整都不能视为通过。上游限制记录在 [依赖说明](docs/dependencies.md)。
- 版本来自 `launcher/version.txt`；更新版本时同步发布清单、Windows manifest 和发行说明，检查脚本会阻止不一致。
- Windows 发布从干净提交构建，验证最终 ZIP、版本、提交来源和校验和。稳定版发布前完成安装、启动、生成、退出及升级回退实机验收；测试版须明确标注 beta，并在发行说明中列明验证范围和未覆盖场景，不能把自动化回归等同于完整实机验收。
- 升级前停机并备份整个用户数据目录；SQLite 文件可能带有 WAL/SHM 文件，运行中不要仅复制主数据库作为备份。ComfyUI 的输出目录和插件配置需要分别备份。
- 一个数据目录同时只运行一个工作台进程。当前 JSON 存储的写锁只覆盖进程内操作，不提供多实例共享写入或多用户隔离。
