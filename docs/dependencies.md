# 依赖说明

1.0.1 新建环境使用清单锁定的 Python 3.11.9、PyTorch 2.11.0 / CUDA 12.8；导入已有环境保留原 GPU 运行时。ComfyUI 和节点固定到提交，自动下载的模型与工具校验大小及 SHA-256。自定义下载源需要用户自行信任。

`python scripts/audit_dependencies.py --output output/dependency-audit.json` 审计托管运行时的锁定依赖。脚本只对 torch 2.11.0 的 `GHSA-rrmf-rvhw-rf47` / `PYSEC-2025-194` 保留一项明确例外：该问题由 `torch.jit.script` 编译带异常裸 `list` / `tuple` 类型注解的 Python 源码触发，工作台输入和固定工作流接口不提供这种编译入口。其他公告或跳过扫描均视为失败。更换节点或运行时时需重新评估此范围；这不表示第三方依赖没有漏洞。

包内 `THIRD-PARTY-NOTICES.txt` 与 `third-party-packages.json` 列出启动器、运行时和构建依赖的版本及许可，并说明并非所有列出的包都被再分发。模型、ComfyUI 和额外节点不在启动器 ZIP 中，使用时遵循各自许可。
