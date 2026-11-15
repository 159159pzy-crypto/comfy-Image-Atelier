namespace Anima.Launcher.Core;

public enum SetupStep { Welcome, Location, Review, Installing, Complete, Finished, Assistant = 6 }
public enum AssistantSetupStatus { Legacy = 0, Pending, Configured, Skipped }
public enum SetupStageStatus { Pending, Running, Complete, Paused, Failed }
public sealed record SetupProgress(string StageId, string Detail, SetupStageStatus Status);
public sealed record SetupRestoreDecision(SetupStep? Step, bool RequiresSupplement = false, bool RequiresRepairReview = false);

/// <summary>A draft is separate from the active configuration until installation starts.</summary>
public sealed class SetupRecord
{
    public int SchemaVersion { get; set; } = 1;
    public SetupStep Step { get; set; }
    public bool Fresh { get; set; } = true;
    public LauncherConfig Draft { get; set; } = new();
    public bool InstallStarted { get; set; }
    public bool InstallFinished { get; set; }
    public bool ModelsPending { get; set; }
    public bool RuntimeVerified { get; set; }
    public SetupRuntimeIssue[] ConfigurationIssues { get; set; } = [];
    public bool SelectionNeedsReview { get; set; }
    public string[] OptionalModelIds { get; set; } = [];
    public string[] SkippedOptionalModelIds { get; set; } = [];
    public string[] FailedFeatureIds { get; set; } = [];
    public InstallationPlan? ReviewedPlan { get; set; }
    public InstallationPlan? SupplementalPlan { get; set; }
    public AssistantSetupStatus AssistantStatus { get; set; }
    public bool? AssistantAvailable { get; set; }
    // Old installations selected the ComfyUI folder itself. Keep that identity
    // when the user temporarily clears the path while editing a recovery draft.
    public bool DirectComfyRoot { get; set; }

    public void MarkRuntimeCheckStale() => RuntimeVerified = false;

    public void SetInstallationMode(bool fresh)
    {
        ResetAssistantForTargetChange(Draft, fresh);
        Fresh = fresh;
    }

    public void UpdateDraft(LauncherConfig draft)
    {
        // Opening the location page also saves its fields. Only a real value change
        // invalidates a previously confirmed owner; checking a new plan is not confirmation.
        if (System.Text.Json.JsonSerializer.Serialize(Draft, JsonFile.Options)
            != System.Text.Json.JsonSerializer.Serialize(draft, JsonFile.Options))
        {
            SelectionNeedsReview = true;
            MarkRuntimeCheckStale();
        }
        ResetAssistantForTargetChange(draft, Fresh);
        Draft = draft.Clone();
    }

    private void ResetAssistantForTargetChange(LauncherConfig draft, bool fresh)
    {
        if (TargetKey(Draft, Fresh).Equals(TargetKey(draft, fresh), StringComparison.OrdinalIgnoreCase)) return;
        AssistantStatus = AssistantSetupStatus.Pending;
        AssistantAvailable = null;
    }

    private static string TargetKey(LauncherConfig draft, bool fresh)
    {
        var root = fresh && !string.IsNullOrWhiteSpace(draft.InstallRoot)
            ? Path.Combine(draft.InstallRoot.Trim().Trim('"'), "ComfyUI") : draft.ComfyRoot.Trim().Trim('"');
        if (root.Length == 0) return "";
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException)
        { return root.TrimEnd('\\', '/'); } // An incomplete draft cannot retain another target's service receipt.
    }

    public void RecordRuntimeCheck(SetupRuntimeReadinessReport report)
    {
        if (!report.RequiredReady) throw new InvalidOperationException("运行检查尚未通过，不能替换已保存的资源检查结果。");
        ConfigurationIssues = report.ConfigurationIssues.Distinct().ToArray();
        RuntimeVerified = true;
    }
}

public sealed record SetupAssistantPresentation(string Status, string Description, bool Configured = false)
{
    public static SetupAssistantPresentation Resolve(SetupRecord setup) => setup.AssistantAvailable == false
        ? new("未安装", "提示词助手未安装或未加载。需要时可到「模型资源」准备此功能，再到「设置」配置服务。")
        : setup.AssistantStatus switch
        {
            AssistantSetupStatus.Configured => new("已配置", "提示词助手已配置，可在工作台使用；服务设置仍可随时修改。", true),
            AssistantSetupStatus.Skipped => new("稍后配置", "已跳过提示词助手配置，不影响基础生图。需要时可到「设置」继续配置。"),
            AssistantSetupStatus.Legacy => new("沿用已有设置", "保留已有提示词助手设置，可在「设置」中查看或修改。"),
            _ => new("可选", "提示词助手为可选功能，不影响基础生图。"),
        };
}

public sealed record SetupDependencyReviewPresentation(string Summary, string Details)
{
    public static SetupDependencyReviewPresentation Create(IEnumerable<string> issues)
    {
        var details = issues.Where(issue => !string.IsNullOrWhiteSpace(issue)).ToArray();
        return new(string.Join("\n", details.Select(Summarize).Distinct().Select(issue => "• " + issue)),
            string.Join("\n\n", details.Select(issue => "• " + issue)));
    }

    public static SetupDependencyReviewPresentation ForPreflight(IEnumerable<PreflightIssue> issues)
    {
        var rows = issues.ToArray();
        return new(string.Join("\n\n", rows.Select(issue => (issue.Blocking ? "需处理：" : "提示：") + SummarizePreflight(issue))),
            string.Join("\n\n", rows.Select(issue => (issue.Blocking ? "需处理：" : "提示：") + issue.Message)));
    }

    private static string SummarizePreflight(PreflightIssue issue)
    {
        // This specific safety boundary appends arbitrary Python exceptions and a full
        // preparation list. Keep its two recovery choices clear; the exact list belongs in details.
        if (issue.Code == "import_probe" && issue.Message.Contains(
            "此导入环境需要补齐节点、依赖或安装准备。请选择 ComfyUI 目录内的独立", StringComparison.Ordinal))
            return "此导入环境需要补齐插件或依赖。请选择 ComfyUI 目录内的独立 .venv、venv 或独立便携 Python；也可以先在原环境中手动补齐，再重新检查。缺项与检查原因见「查看完整检查结果与路径」。";
        return Summarize(issue.Message);
    }

    private static string Summarize(string issue)
    {
        var summary = issue.TrimEnd();
        // These producers append exactly one full-width parenthesized source.
        // Balance it so parentheses inside a directory do not corrupt the package message.
        if ((summary.StartsWith("依赖需要补齐：", StringComparison.Ordinal)
            || summary.StartsWith("依赖尚未验证：", StringComparison.Ordinal)
            || summary.StartsWith("需要安装插件：", StringComparison.Ordinal)) && summary.EndsWith('）'))
        {
            var depth = 0;
            for (var index = summary.Length - 1; index >= 0; index--)
            {
                if (summary[index] == '）') depth++;
                else if (summary[index] == '（' && --depth == 0)
                {
                    summary = summary[..index].TrimEnd();
                    break;
                }
            }
        }
        summary = FoldPathSources(summary);
        foreach (var prefix in new[] { "依赖尚未验证：无法读取依赖文件：", "依赖尚未验证：无法离线解析依赖：",
                     "Forbidden Vision 下载管理需要准备：", "NVIDIA 运行库检查未通过：" })
            if (summary.StartsWith(prefix, StringComparison.Ordinal)) return prefix.TrimEnd('：') + "，具体原因见详情。";
        return summary;
    }

    private static string FoldPathSources(string message)
    {
        // Import-blocking diagnostics can contain several joined preparation issues.
        // Fold only recognizable path sources; keep the decision guidance and package text intact.
        var result = new System.Text.StringBuilder();
        for (var index = 0; index < message.Length; index++)
        {
            if (message[index] == '（')
            {
                var depth = 1;
                var end = index + 1;
                for (; end < message.Length && depth > 0; end++)
                    if (message[end] == '（') depth++;
                    else if (message[end] == '）') depth--;
                if (depth == 0)
                {
                    var source = message[(index + 1)..(end - 1)];
                    if (source.Contains(":\\", StringComparison.Ordinal) || source.Contains(":/", StringComparison.Ordinal)
                        || source.StartsWith(@"\\", StringComparison.Ordinal) || source.StartsWith('/'))
                    {
                        index = end - 1;
                        continue;
                    }
                }
            }
            result.Append(message[index]);
        }
        return result.ToString();
    }
}

public static class SetupRouting
{
    public static SetupStep AfterModels(SetupRecord saved) =>
        saved.AssistantStatus == AssistantSetupStatus.Pending && saved.AssistantAvailable != false
            ? SetupStep.Assistant : SetupStep.Complete;
    public static bool OpenWizardFromEnvironmentPage(LauncherConfig current, bool setupActive) =>
        setupActive || string.IsNullOrWhiteSpace(current.ComfyRoot) || current.InstallPending;
    /// <summary>
    /// Null means the regular overview. SetupComplete deliberately does not participate:
    /// it records a successful workbench launch, not whether an environment is installed.
    /// </summary>
    public static SetupStep? Resolve(LauncherConfig current, SetupRecord? saved)
    {
        if (saved is { SchemaVersion: 1, Draft: not null, SelectionNeedsReview: true }
            && (string.IsNullOrWhiteSpace(current.ComfyRoot) || TargetsMatch(current, saved.Draft, saved.Fresh)))
            return SetupStep.Location;
        if (current.InstallPending) return SetupStep.Installing;
        var configured = !string.IsNullOrWhiteSpace(current.ComfyRoot);
        if (saved is null || saved.SchemaVersion != 1 || saved.Draft is null || !Enum.IsDefined(saved.Step))
            return configured ? null : SetupStep.Welcome;
        if (configured && !TargetsMatch(current, saved.Draft, saved.Fresh)) return null;
        if (saved.Step == SetupStep.Finished) return configured ? null : SetupStep.Welcome;
        if (saved.InstallFinished)
        {
            // A completion receipt from a different, removed or incomplete environment
            // must never enable a misleading "start creating" screen.
            if (!configured || !TargetsMatch(current, saved.Draft, saved.Fresh) || !HasLaunchFiles(current))
                return SetupStep.Location;
            if (saved.ModelsPending) return SetupStep.Installing;
            return AfterModels(saved);
        }
        if (saved.InstallStarted) return SetupStep.Installing;
        return saved.Step switch
        {
            SetupStep.Location or SetupStep.Review or SetupStep.Installing or SetupStep.Complete or SetupStep.Assistant => SetupStep.Location,
            _ => SetupStep.Welcome
        };
    }

    /// <summary>Unfinished completion screens must prepare requirements introduced after their frozen plan.</summary>
    public static SetupRestoreDecision ResolveForRuntime(LauncherConfig current, SetupRecord? saved,
        RuntimeManifest runtime, ModelManifest models, ModelProfilesManifest profiles)
    {
        var step = Resolve(current, saved);
        // An ended wizard remains ended. Its explicit installation/repair entry point
        // creates a new reviewed plan instead of reopening onboarding on every upgrade.
        if (step is not (SetupStep.Complete or SetupStep.Assistant)) return new(step);
        var plan = saved!.ReviewedPlan;
        if (plan is null || !TargetsMatch(current, new LauncherConfig { ComfyRoot = plan.ComfyRoot }, false)
            || plan.RuntimeArtifactsIdentity.Length > 0 && plan.RuntimeArtifactsIdentity != RuntimeArtifactPlanning.Identity)
            return new(SetupStep.Location, RequiresRepairReview: true);
        return NeedsSupplement(plan, runtime, models, profiles) ? new(SetupStep.Installing, RequiresSupplement: true) : new(step);
    }

    public static bool NeedsSupplement(InstallationPlan plan, RuntimeManifest runtime,
        ModelManifest models, ModelProfilesManifest profiles) => false;

    /// <summary>Compare a normalized installation target, including an unexpanded fresh-install draft.</summary>
    public static bool TargetsMatch(LauncherConfig current, LauncherConfig draft, bool fresh)
    {
        try
        {
            var target = fresh && !string.IsNullOrWhiteSpace(draft.InstallRoot)
                ? Path.Combine(draft.InstallRoot.Trim().Trim('"'), "ComfyUI") : draft.ComfyRoot;
            if (string.IsNullOrWhiteSpace(current.ComfyRoot) || string.IsNullOrWhiteSpace(target)) return false;
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current.ComfyRoot.Trim().Trim('"')))
                .Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.Trim().Trim('"'))), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    private static bool HasLaunchFiles(LauncherConfig config) =>
        File.Exists(Path.Combine(config.ComfyRoot, "main.py")) && File.Exists(config.Python);
}

/// <summary>Only the most recent check of the current draft can authorize installation.</summary>
public sealed class SetupReviewGate
{
    private readonly object sync = new();
    private int revision;
    private bool canInstall;
    private bool checking;
    public bool CanInstall { get { lock (sync) return canInstall; } }

    public int Begin()
    {
        lock (sync)
        {
            canInstall = false;
            checking = true;
            return ++revision;
        }
    }

    public void Invalidate()
    {
        lock (sync)
        {
            revision++;
            canInstall = false;
            checking = false;
        }
    }

    /// <returns>True only when this result belongs to the latest unmodified draft.</returns>
    public bool Complete(int checkedRevision, bool passed)
    {
        lock (sync)
        {
            if (!checking || checkedRevision != revision) return false;
            checking = false;
            canInstall = passed;
            return true;
        }
    }
}
