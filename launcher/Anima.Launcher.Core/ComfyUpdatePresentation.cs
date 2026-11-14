namespace Anima.Launcher.Core;

/// <summary>The durable recovery result, shared by navigation, restart and task interruption.</summary>
public sealed record ComfyUpdatePresentation(string Message, bool RequiresRecovery, bool CanRestore,
    bool NeedsDependencyRepair, bool CoreVerified)
{
    public static ComfyUpdatePresentation From(ComfyUpdateBackup? record)
    {
        if (record is null) return new("手动检查最新稳定版；检查不会安装依赖或中断正在运行的服务。", false, false, false, false);
        var legacy = record.FormatVersion == 0;
        if (legacy && record.RecoveryStrategy != "full" || !legacy && (record.FormatVersion != 1 || record.RecoveryStrategy != "light"))
            return new("无法识别恢复记录版本，请保留记录并查看日志。", true, false, false, false);
        return record.Status switch
        {
            "complete" => new(legacy ? "上次更新保留了完整备份，可以从旧备份恢复。"
                : "ComfyUI 核心启动验证通过。可启动工作台，或恢复上次版本；插件将在正常启动时检查。", false, true, false, legacy || record.CoreVerified),
            "needs-repair" => new("源码已恢复，依赖待修复。点击「修复更新依赖」后再启动；不会自动联网修复。", true, false, true, false),
            "rollback-failed" => new("源码或配置恢复未完成。恢复记录已保留，请查看日志、处理文件冲突后重试恢复。", true, true, false, false),
            "updating" or "restoring" => new("上次更新或恢复中断。请先恢复上次版本，完成检查后再更新或启动。", true, true, false, false),
            "rolled-back" => new(legacy ? "已从旧完整备份恢复。" : record.CoreVerified ? "源码与必要配置已恢复，ComfyUI 核心启动检查通过。"
                : "源码与必要配置已恢复；未改动 Python 依赖，尚未进行核心启动检查。", false, false, false, legacy || record.CoreVerified),
            _ => new("无法识别更新恢复状态，请查看日志并保留恢复记录。", true, false, false, false),
        };
    }
}
