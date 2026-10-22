using Anima.Launcher.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Text;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Anima.Launcher.Pages;

public sealed partial class SetupPage : LauncherPage
{
    private bool loading = true;
    private bool active;
    private bool pythonExplicit;
    private bool pauseRequested;
    private string localError = "";
    private string displayedError = "";
    private SetupStep? displayedStep;
    private string stageSignature = "";
    private object? renderedModelReview;
    private object? renderedPreflight;

    public SetupPage()
    {
        InitializeComponent();
        SourceBox.ItemsSource = Program.Sources.Profiles;
        SourceBox.DisplayMemberPath = nameof(SourceProfile.Name);
        ActualThemeChanged += (_, _) =>
        {
            stageSignature = "";
            renderedModelReview = null;
            if (active) Refresh();
        };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        active = true;
        State.SetupChanged += OnChanged;
        State.BusyChanged += OnBusy;
        State.TaskProgressChanged += OnProgress;
        State.ProgressChanged += OnPercent;
        LoadFields();
        Refresh();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        active = false;
        State.SetupChanged -= OnChanged;
        State.BusyChanged -= OnBusy;
        State.TaskProgressChanged -= OnProgress;
        State.ProgressChanged -= OnPercent;
        base.OnNavigatedFrom(e);
    }

    private void OnChanged() => DispatcherQueue.TryEnqueue(() => { if (active) Refresh(); });
    private void OnBusy(bool _) => OnChanged();
    private void OnProgress(string _) => OnChanged();
    private void OnPercent(int _) => OnChanged();
    private static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    // Initial navigation runs before App.Window is assigned. Resolve brushes
    // from this page rather than depending on the not-yet-published shell.
    private Brush PageBrush(string key)
    {
        var theme = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast ? "HighContrast"
            : ActualTheme == ElementTheme.Light ? "Light" : "Default";
        return (Brush)((ResourceDictionary)Application.Current.Resources.ThemeDictionaries[theme])[key];
    }

    private void LoadFields()
    {
        loading = true;
        try
        {
            var draft = State.Setup.Draft;
            RootBox.Text = State.Setup.Fresh && !State.SetupLegacyDirectRoot ? draft.InstallRoot : draft.ComfyRoot;
            PythonBox.Text = draft.Python;
            pythonExplicit = draft.PythonExplicit;
            SourceBox.SelectedItem = Program.Sources.Profiles.FirstOrDefault(p => p.Id == draft.SourceId)
                ?? Program.Sources.Profiles.FirstOrDefault(p => p.Id == "official")
                ?? Program.Sources.Profiles[0];
            GitMirrorBox.Text = draft.GitMirror;
            ModelMirrorBox.Text = draft.ModelMirror;
            PipBox.Text = draft.PipIndex;
            RepositoryBox.Text = draft.CustomRepository;
        }
        finally { loading = false; }
        UpdateLocation();
    }

    // Only an actual editor change writes the separate draft. Progress events
    // never refill the text fields, so typing and selection remain uninterrupted.
    private void SaveDraft()
    {
        if (loading || State.Busy) return;
        localError = "";
        var draft = State.Setup.Draft.Clone();
        if (State.Setup.Fresh && !State.SetupLegacyDirectRoot) draft.InstallRoot = RootBox.Text;
        else draft.ComfyRoot = RootBox.Text;
        draft.Python = PythonBox.Text;
        draft.PythonExplicit = pythonExplicit;
        draft.SourceId = (SourceBox.SelectedItem as SourceProfile)?.Id ?? "official";
        draft.GitMirror = GitMirrorBox.Text;
        draft.ModelMirror = ModelMirrorBox.Text;
        draft.PipIndex = PipBox.Text;
        draft.CustomRepository = RepositoryBox.Text;
        try { State.SaveSetupDraft(draft); }
        catch (Exception ex) { ShowLocalError(ex); }
        UpdateLocation();
        Refresh();
    }

    private void Refresh()
    {
        var step = State.Setup.Step;
        if (!State.Busy) pauseRequested = false;
        if (displayedStep != step)
        {
            displayedStep = step;
            if (step == SetupStep.Location) LoadFields();
            BodyScroll.ChangeView(null, 0, null, true);
        }
        WelcomePanel.Visibility = Visible(step == SetupStep.Welcome);
        LocationPanel.Visibility = Visible(step == SetupStep.Location);
        ReviewPanel.Visibility = Visible(step == SetupStep.Review);
        InstallingPanel.Visibility = Visible(step == SetupStep.Installing);
        CompletePanel.Visibility = Visible(step == SetupStep.Complete);
        UpdateStepLabels(step);
        ErrorBar.Message = string.IsNullOrEmpty(localError) ? State.SetupError : localError;
        if (localError.Length == 0 && step == SetupStep.Review && State.SetupPreflight is { CanInstall: false })
            ErrorBar.Message = "检查未通过，请查看下方检查结果并处理后重试。";
        if (localError.Length == 0 && step == SetupStep.Complete && State.SetupModelsReadiness == SetupModelReadiness.ScanFailed
            && State.SetupCompletionOperation != SetupCompletionOperation.StartWorkbench && ErrorBar.Message.Length > 0)
            ErrorBar.Message = "基础模型检查未完成。请确认已连接的 ComfyUI 目录和 Python 可用，再重新检查；详细原因见日志。";
        ErrorBar.IsOpen = !string.IsNullOrWhiteSpace(ErrorBar.Message);
        if (ErrorBar.IsOpen && displayedError != ErrorBar.Message) BodyScroll.ChangeView(null, 0, null, true);
        displayedError = ErrorBar.Message;
        NoticeBar.Message = State.SetupNotice;
        NoticeBar.IsOpen = !string.IsNullOrWhiteSpace(State.SetupNotice)
            && step != SetupStep.Review
            && step != SetupStep.Complete;
        LocationFields.IsEnabled = !State.Busy;
        BackButton.IsEnabled = !State.Busy;
        SecondaryButton.IsEnabled = !State.Busy;
        DetectAgainButton.IsEnabled = !State.Busy;
        DetectAgainButton.Content = State.Busy ? "正在检测…" : "重新检测";
        RecheckModelsButton.IsEnabled = !State.Busy;
        ModelReviewHost.IsEnabled = !State.Busy;
        IssueActionsHost.IsEnabled = !State.Busy;
        PrimaryButton.IsEnabled = !State.Busy;
        SecondaryButton.Visibility = Visibility.Collapsed;
        BackButton.Visibility = Visibility.Visible;
        BackButton.Content = step == SetupStep.Welcome ? "稍后设置" : step == SetupStep.Complete ? "稍后打开" : "上一步";
        CheckProgress.Visibility = Visible(State.Busy && step == SetupStep.Review);

        switch (step)
        {
            case SetupStep.Welcome:
                SystemSummary.Text = State.SetupSystemSummary;
                SystemAdvice.Text = State.SetupSystemSupported switch
                {
                    true => "可以在这台电脑上安装完整环境。",
                    false => "暂不支持全新安装。可导入已经可用的 ComfyUI 环境。",
                    _ => "检测完成后即可继续；已有环境也可以直接导入。"
                };
                PrimaryButton.Content = "安装完整环境";
                PrimaryButton.IsEnabled = !State.Busy && State.SetupSystemSupported == true;
                SecondaryButton.Content = "我已有 ComfyUI";
                SecondaryButton.Visibility = Visibility.Visible;
                FooterHint.Text = "下一步选择位置与下载来源。";
                break;
            case SetupStep.Location:
                PrimaryButton.Content = State.Busy ? "正在检查…" : "检查并继续";
                PrimaryButton.IsEnabled = !State.Busy && !string.IsNullOrWhiteSpace(RootBox.Text);
                FooterHint.Text = "检查安装内容和磁盘空间后，再开始安装。";
                UpdateDetectedPython();
                break;
            case SetupStep.Review:
                UpdateReview();
                PrimaryButton.Content = State.Busy ? "正在检查…" : State.SetupCanInstall
                    ? (!State.Setup.Fresh && !State.SetupImportNeedsRepair ? "完成导入"
                        : State.Setup.InstallStarted ? "继续安装" : "开始安装") : "重新检查";
                FooterHint.Text = State.SetupCanInstall
                    ? State.Setup.Fresh ? "确认后再开始下载与安装。" : "确认后连接现有环境，补齐缺少的节点。"
                    : "处理上方问题后重新检查。";
                break;
            case SetupStep.Installing:
                UpdateInstallation();
                PrimaryButton.Content = State.Busy ? (pauseRequested ? "正在暂停…" : "暂停")
                    : State.SetupCanResume ? (string.IsNullOrEmpty(State.SetupError) ? "继续" : "重试") : "检查并继续";
                PrimaryButton.IsEnabled = !pauseRequested;
                SecondaryButton.Content = "更改目录或来源";
                SecondaryButton.Visibility = Visible(!State.Busy);
                FooterHint.Text = State.Busy ? "可暂停安装；已完成的文件和步骤会保留。"
                    : State.SetupCanResume ? "继续使用本次任务的安装选项。" : "重新检查后继续，已完成内容会复用。";
                break;
            case SetupStep.Complete:
                UpdateCompletion();
                break;
        }
    }

    private void UpdateStepLabels(SetupStep step)
    {
        var labels = new[] { StepWelcome, StepLocation, StepReview, StepInstalling, StepComplete };
        var titles = new[] { "选择方式", "安装位置", "检查确认", "安装环境", "开始创作" };
        var current = step switch
        {
            SetupStep.Welcome => 0, SetupStep.Location => 1, SetupStep.Review => 2,
            SetupStep.Installing => 3, _ => 4
        };
        for (var i = 0; i < labels.Length; i++)
        {
            labels[i].Text = (i < current ? "✓  " : $"{i + 1}  ") + titles[i];
            labels[i].FontWeight = i == current ? FontWeights.SemiBold : FontWeights.Normal;
            labels[i].Opacity = i == current ? 1 : .65;
            AutomationProperties.SetName(labels[i], titles[i] + (i < current ? "，已完成" : i == current ? "，当前步骤" : "，待进行"));
        }
    }

    private void UpdateLocation()
    {
        var fresh = State.Setup.Fresh;
        var directRoot = State.SetupLegacyDirectRoot;
        LocationTitle.Text = directRoot ? "继续准备原来的环境。" : fresh ? "给创作环境选一个位置。" : "连接你已有的 ComfyUI。";
        LocationDescription.Text = directRoot ? "这是上次尚未完成的安装。沿用原来的 ComfyUI 目录和工具目录，继续准备剩余内容。"
            : fresh ? "选择空间充足的磁盘。工具、环境和模型会放在同一个安装文件夹下。"
            : "选择包含 main.py 的 ComfyUI 文件夹，或便携版的上级目录。";
        RootLabel.Text = fresh && !directRoot ? "安装文件夹" : "ComfyUI 文件夹";
        RootBox.PlaceholderText = fresh && !directRoot ? @"例如 D:\AnimaStudio" : @"例如 D:\ComfyUI";
        ImportPythonPanel.Visibility = Visible(!fresh);
        ModelSummary.Text = fresh ? "将准备 Anima 基础模型：底模、文本编码器和 VAE。"
            : "复用现有模型和节点；导入完成后，会列出仍需补齐的模型。";
        UpdateDetectedPython();
        try
        {
            var raw = RootBox.Text.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(raw)) throw new ArgumentException();
            var path = Path.GetFullPath(raw);
            var comfy = fresh && !directRoot ? Path.Combine(path, "ComfyUI") : path;
            if (!fresh)
            {
                try { comfy = Inventory.NormalizeRoot(path); }
                catch { /* Show the selected path until preflight can explain the issue. */ }
            }
            ResolvedRoot.Text = fresh
                ? $"环境：{comfy}\n工具与缓存：{(directRoot ? InstallationPlanner.ResolveRuntimeRoot(State.Setup.Draft, Program.StateDir) : Path.Combine(path, "runtime"))}"
                : $"环境：{comfy}";
            var drive = new DriveInfo(Path.GetPathRoot(comfy)!);
            DiskSummary.Text = $"{drive.Name} 可用 {Bytes(drive.AvailableFreeSpace)}";
        }
        catch
        {
            ResolvedRoot.Text = string.IsNullOrWhiteSpace(RootBox.Text) ? "选择文件夹后，这里会显示实际环境位置。" : "请输入完整的本地文件夹路径。";
            DiskSummary.Text = "磁盘空间将在检查时确认。";
        }
    }

    private void UpdateDetectedPython()
    {
        var python = State.Setup.Draft.Python;
        if (!State.Setup.Fresh && !string.IsNullOrWhiteSpace(RootBox.Text))
        {
            try
            {
                var root = Inventory.NormalizeRoot(RootBox.Text.Trim().Trim('"'));
                python = Inventory.FindPython(root, pythonExplicit ? PythonBox.Text : "");
                DetectedPython.Text = "Python：" + python;
                return;
            }
            catch
            {
                DetectedPython.Text = "尚未找到可用 Python。请确认目录，或展开下方选项手动指定。";
                return;
            }
        }
        DetectedPython.Text = string.IsNullOrWhiteSpace(python) ? "Python：选择目录后自动识别。" : "Python：" + python;
    }

    private void UpdateReview()
    {
        var plan = State.SetupPlan;
        var preflight = State.SetupPreflight;
        ReviewSummary.Text = State.Busy ? "正在检查环境、可复用文件和各磁盘空间…"
            : State.SetupCanInstall ? "检查通过。以下是这次安装会准备的内容。" : "还不能开始安装，请处理检查结果中的问题。";
        ReviewMode.Text = State.Setup.Fresh ? "安装完整环境" : "连接已有 ComfyUI";
        ReviewLocation.Text = "位置：" + (plan?.ComfyRoot ?? State.Setup.Draft.ComfyRoot);
        PlanContents.Text = plan is null ? "完成检查后显示安装内容。"
            : string.Join("\n", plan.Stages.Select(stage => "• " + stage)) + $"\n\n环境：{plan.ComfyRoot}\nPython：{plan.CreateConfig().Python}\n工具与缓存：{plan.RuntimeRoot}";
        DownloadEstimate.Text = plan is null ? "等待计算下载量" : $"已知剩余下载 {Bytes(plan.EstimatedDownloadBytes)}";
        if (plan is not null)
        {
            var existing = plan.Downloads.Count(download => File.Exists(download.Destination));
            ReuseSummary.Text = existing > 0
                ? $"发现 {existing} 个已有下载文件；检查会验证文件，安装时复用有效内容。已计入续传和可复用空间。"
                : "已完成的下载和安装步骤会在继续时复用。空间估算已扣除可复用内容。";
        }
        else ReuseSummary.Text = "";
        VolumeSummary.Text = preflight is null ? "等待检查各目标磁盘。"
            : string.Join("\n", preflight.Volumes.Select(volume => $"{volume.Volume}  需预留 {Bytes(volume.RequiredBytes)} · 可用 {(volume.AvailableBytes is { } available ? Bytes(available) : "无法读取")}"));
        IssuesPanel.Visibility = Visible(preflight is not null && preflight.Issues.Length > 0);
        IssuesSummary.Text = preflight is null ? "" : string.Join("\n\n",
            preflight.Issues.Select(issue => (issue.Blocking ? "需处理：" : "提示：") + issue.Message));
        UpdateIssueActions();
    }

    private void UpdateInstallation()
    {
        InstallingTitle.Text = State.Busy ? "正在准备创作环境。" : string.IsNullOrEmpty(State.SetupError) ? "准备好后，继续安装。" : "安装遇到了问题。";
        InstallationHelp.Text = State.Busy ? "首次准备需要一些时间，可以在下方查看每个阶段的状态。"
            : "已完成的内容会保留。可以继续，也可以更改下载来源后重新检查。";
        CurrentOperation.Text = string.IsNullOrWhiteSpace(State.SetupStatusText) ? "等待继续安装" : State.SetupStatusText;
        TransferDetails.Text = State.ProgressText;
        FileProgress.IsIndeterminate = State.Busy && State.SetupDownloadPercent < 0;
        FileProgress.Value = Math.Max(0, State.SetupDownloadPercent);
        var signature = string.Join("\n", State.SetupStages.Select(stage => $"{stage.StageId}|{stage.Status}|{stage.Detail}"));
        if (signature == stageSignature) return;
        stageSignature = signature;
        StageList.ItemsSource = State.SetupStages.Select(stage => new SetupStageRow(
            StageTitle(stage.StageId), stage.Detail, StageState(stage.Status), StageMarker(stage.Status),
            PageBrush(stage.Status switch
            {
                SetupStageStatus.Complete => "StudioSuccessBrush",
                SetupStageStatus.Failed => "StudioDangerBrush",
                SetupStageStatus.Paused => "StudioWarningBrush",
                SetupStageStatus.Running => "StudioAccentBrush",
                _ => "StudioSecondaryBrush"
            }))).ToArray();
    }

    private SetupCompletionPresentation CompletionPresentation() => SetupCompletionPresentation.Resolve(
        State.SetupModelsReadiness, State.SetupModelReviewVisible, State.SetupCanDownloadModels,
        State.SetupCanResumeModels,
        State.SetupCompletionOperation == SetupCompletionOperation.StartWorkbench && State.SetupError.Length > 0,
        State.SetupError.Length > 0);

    private void UpdateCompletion()
    {
        var presentation = CompletionPresentation();
        CompletionTitle.Text = presentation.Title;
        CompletionHelp.Text = presentation.Description;
        CompletionSummary.Text = (State.Setup.Fresh ? "独立创作环境已安装。" : "现有 ComfyUI 已连接。")
            + "\n" + State.Config.ComfyRoot;
        var ready = State.SetupModelsReadiness == SetupModelReadiness.Ready;
        var attention = State.SetupModelsReadiness is SetupModelReadiness.NeedsAttention or SetupModelReadiness.ScanFailed;
        CompletionIcon.Glyph = ready ? "\uE73E" : attention ? "\uE7BA" : "\uE946";
        CompletionIcon.Foreground = PageBrush(ready ? "StudioSuccessBrush" : attention ? "StudioWarningBrush" : "StudioAccentBrush");
        MissingModelsPanel.Visibility = Visible(State.SetupMissingModels.Count > 0 && !State.SetupModelReviewVisible);
        MissingModelsText.Text = string.Join("\n", State.SetupMissingModels.Select(name => "• " + name));
        ModelReviewPanel.Visibility = Visible(State.SetupModelReviewVisible && State.SetupModelReview is not null);
        if (State.SetupModelReview is { } review && !ReferenceEquals(review, renderedModelReview))
        {
            renderedModelReview = review;
            ModelDownloadEstimate.Text = $"可下载 {review.DownloadableItems.Length} 项 · 已知剩余下载 {Bytes(review.RemainingBytes)}";
            ModelReviewItems.Children.Clear();
            foreach (var item in review.Items)
            {
                var row = new StackPanel { Spacing = 6 };
                row.Children.Add(new TextBlock
                {
                    Text = item.Name + " · " + item.Status.State,
                    TextWrapping = TextWrapping.Wrap,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = PageBrush("StudioTextBrush")
                });
                row.Children.Add(BodyText("位置：" + item.Location));
                row.Children.Add(BodyText("来源：" + item.Source));
                if (!string.IsNullOrWhiteSpace(item.Issue)) row.Children.Add(BodyText(item.Issue));
                if (!string.IsNullOrWhiteSpace(item.OpenDirectory) && !string.IsNullOrWhiteSpace(item.Issue))
                {
                    var folder = item.OpenDirectory;
                    var open = new Button { Content = "打开文件夹处理", HorizontalAlignment = HorizontalAlignment.Left };
                    open.Click += (_, _) => RunUi(() =>
                    {
                        var existing = folder;
                        while (!Directory.Exists(existing)) existing = Path.GetDirectoryName(existing)
                            ?? throw new DirectoryNotFoundException("未找到可打开的目录，请检查模型位置。");
                        Program.Open(existing);
                    });
                    row.Children.Add(open);
                }
                ModelReviewItems.Children.Add(row);
            }
        }
        var downloading = State.Busy && State.SetupCompletionOperation == SetupCompletionOperation.DownloadModels;
        CompletionProgressPanel.Visibility = Visible(State.Busy);
        CompletionProgress.IsIndeterminate = !downloading || State.SetupDownloadPercent < 0;
        CompletionProgress.Value = Math.Max(0, State.SetupDownloadPercent);
        CompletionOperationText.Text = State.SetupStatusText;
        CompletionTransfer.Text = downloading ? State.ProgressText : "";
        PrimaryButton.Content = State.Busy
            ? downloading ? pauseRequested ? "正在暂停…" : "暂停" : State.SetupCompletionOperation == SetupCompletionOperation.StartWorkbench ? "正在启动…" : "正在检查…"
            : presentation.PrimaryLabel;
        PrimaryButton.IsEnabled = downloading ? !pauseRequested : !State.Busy && presentation.PrimaryAction != SetupCompletionAction.None;
        SecondaryButton.Content = "先打开工作台";
        SecondaryButton.Visibility = Visible(presentation.AllowOpenAnyway);
        FooterHint.Text = State.Busy ? State.SetupStatusText
            : State.SetupCompletionOperation == SetupCompletionOperation.StartWorkbench && State.SetupError.Length > 0
                ? "启动未完成。查看日志并处理问题后，可以重试。"
                : State.SetupCanResumeModels ? "已完成的下载会保留，可以继续下载或稍后再开始。"
                : State.SetupModelsReadiness == SetupModelReadiness.ScanFailed ? "检查未完成。可以查看日志、重新检查或先打开工作台。"
                : ready ? "服务连接检查通过后打开工作台。" : "推荐先准备基础模型；已有其他模型时，也可先打开工作台。";
    }

    private static TextBlock BodyText(string text) => new()
    {
        Text = text,
        Style = (Style)Application.Current.Resources["StudioBodyStyle"],
        IsTextSelectionEnabled = true
    };

    private void UpdateIssueActions()
    {
        var preflight = State.SetupPreflight;
        if (preflight is not null && ReferenceEquals(preflight, renderedPreflight)) return;
        renderedPreflight = preflight;
        IssueActions.Children.Clear();
        if (preflight is null)
        {
            // Path/Python validation can fail before a plan is available.
            if (State.Busy || string.IsNullOrWhiteSpace(State.SetupError)) return;
            IssuesPanel.Visibility = Visibility.Visible;
            AddIssueAction("检查目录与 Python", () => EditLocation(!State.Setup.Fresh));
            return;
        }
        var codes = preflight.Issues.Select(issue => issue.Code).ToHashSet();
        if (codes.Overlaps(["target_not_empty", "target_access", "directory_access", "volume_unavailable", "disk_space"]))
            AddIssueAction("更换安装位置", () => EditLocation(false));
        if (codes.Overlaps(["python", "import_probe"]))
            AddIssueAction("检查目录与 Python", () => EditLocation(true));
        if (codes.Overlaps(["system", "nvidia_missing", "nvidia_driver", "nvidia_probe", "target_not_empty"]))
            AddIssueAction("连接已有 ComfyUI", () => State.SetSetupMode(false));
        if (codes.Overlaps(["nvidia_missing", "nvidia_driver", "nvidia_probe"]))
            AddIssueAction("返回重新检测显卡", () => State.MoveSetupTo(SetupStep.Welcome));
        if (codes.Contains("cache_invalid"))
            AddIssueAction("查看日志与文件位置", Shell.ShowSetupLogs);
    }

    private void AddIssueAction(string text, Action action)
    {
        var button = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += (_, _) => { if (!State.Busy) RunUi(action); };
        IssueActions.Children.Add(button);
    }

    private void EditLocation(bool python)
    {
        State.MoveSetupTo(SetupStep.Location);
        Refresh();
        if (python)
        {
            PythonSettings.IsExpanded = true;
            PythonBox.Focus(FocusState.Programmatic);
        }
        else RootBox.Focus(FocusState.Programmatic);
    }

    private void WelcomeChoices_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 620;
        Grid.SetColumn(ImportChoice, narrow ? 0 : 1);
        Grid.SetRow(ImportChoice, narrow ? 1 : 0);
        Grid.SetColumnSpan(ImportChoice, narrow ? 2 : 1);
        Grid.SetColumnSpan((FrameworkElement)WelcomeChoices.Children[0], narrow ? 2 : 1);
    }

    private async void DetectAgain_Click(object sender, RoutedEventArgs e)
    {
        if (State.Busy) return;
        localError = "";
        try { await State.RefreshSetupSystemAsync(); }
        catch (Exception ex) { ShowLocalError(ex); }
    }

    private async void RecheckModels_Click(object sender, RoutedEventArgs e)
    {
        if (State.Busy) return;
        localError = "";
        try { await State.ReviewSetupModelsAsync(); }
        catch (Exception ex) { ShowLocalError(ex); }
    }

    private static string StageTitle(string id) => id switch
    {
        "tools" => "准备安装工具", "comfy" => "准备 ComfyUI", "python" => "准备独立运行环境",
        "torch" => "准备显卡运行环境", "dependencies" => "安装运行所需组件", "nodes" => "准备必要节点",
        "validation" => "检查运行组件", "models" => "准备基础模型", "verify" => "最终检查", _ => id
    };
    private static string StageState(SetupStageStatus status) => status switch
    {
        SetupStageStatus.Running => "进行中", SetupStageStatus.Complete => "已完成",
        SetupStageStatus.Paused => "已暂停", SetupStageStatus.Failed => "待重试", _ => "等待"
    };
    private static string StageMarker(SetupStageStatus status) => status switch
    {
        SetupStageStatus.Running => "●", SetupStageStatus.Complete => "✓",
        SetupStageStatus.Paused => "Ⅱ", SetupStageStatus.Failed => "!", _ => "○"
    };
    private static string Bytes(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / 1073741824d:N1} GiB" : $"{bytes / 1048576d:N1} MiB";

    private void Root_Changed(object sender, TextChangedEventArgs e)
    {
        if (loading) return;
        if (!pythonExplicit)
        {
            loading = true;
            PythonBox.Text = "";
            loading = false;
        }
        SaveDraft();
    }

    private void Python_Changed(object sender, TextChangedEventArgs e)
    {
        if (loading) return;
        pythonExplicit = !string.IsNullOrWhiteSpace(PythonBox.Text);
        SaveDraft();
    }

    private void Input_Changed(object sender, TextChangedEventArgs e) => SaveDraft();
    private void Source_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loading) return;
        if (SourceBox.SelectedItem is SourceProfile profile && profile.Id != "custom")
        {
            loading = true;
            GitMirrorBox.Text = profile.GitTemplate;
            ModelMirrorBox.Text = profile.HuggingFaceBase;
            PipBox.Text = profile.PipIndex;
            loading = false;
        }
        AdvancedSettings.IsExpanded = (SourceBox.SelectedItem as SourceProfile)?.Id == "custom";
        SaveDraft();
    }

    private async void PickRoot_Click(object sender, RoutedEventArgs e)
    {
        if (State.Busy) return;
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, Win32Interop.GetWindowFromWindowId(Shell.AppWindow.Id));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null && !State.Busy && active) RootBox.Text = folder.Path;
        }
        catch (Exception ex) { ShowLocalError(ex); }
    }

    private async void PickPython_Click(object sender, RoutedEventArgs e)
    {
        if (State.Busy) return;
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add(".exe");
            InitializeWithWindow.Initialize(picker, Win32Interop.GetWindowFromWindowId(Shell.AppWindow.Id));
            var file = await picker.PickSingleFileAsync();
            if (file is not null && !State.Busy && active) PythonBox.Text = file.Path;
        }
        catch (Exception ex) { ShowLocalError(ex); }
    }

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        localError = "";
        if (State.Busy)
        {
            if (State.Setup.Step == SetupStep.Installing ||
                State.Setup.Step == SetupStep.Complete && State.SetupCompletionOperation == SetupCompletionOperation.DownloadModels)
            {
                pauseRequested = true;
                State.PauseTask();
                Refresh();
            }
            return;
        }
        try
        {
            localError = "";
            switch (State.Setup.Step)
            {
                case SetupStep.Welcome:
                    if (State.SetupSystemSupported != true) return;
                    State.SetSetupMode(true);
                    break;
                case SetupStep.Location:
                    SaveDraft();
                    if (localError.Length == 0) await State.CheckSetupAsync();
                    break;
                case SetupStep.Review:
                    if (State.SetupCanInstall) await State.InstallSetupAsync();
                    else await State.CheckSetupAsync();
                    break;
                case SetupStep.Installing:
                    if (State.SetupCanResume) await State.ResumeSetupAsync();
                    else await State.CheckSetupAsync();
                    break;
                case SetupStep.Complete:
                    switch (CompletionPresentation().PrimaryAction)
                    {
                        case SetupCompletionAction.ReviewModels: await State.ReviewSetupModelsAsync(); break;
                        case SetupCompletionAction.DownloadModels: await State.DownloadSetupModelsAsync(); break;
                        case SetupCompletionAction.ResumeModels: await State.ResumeSetupModelsAsync(); break;
                        case SetupCompletionAction.StartWorkbench: await State.StartSetupWorkbenchAsync(); break;
                    }
                    break;
            }
        }
        catch (Exception ex) { ShowLocalError(ex); }
        if (active) Refresh();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (State.Busy) return;
        RunUi(() =>
        {
            switch (State.Setup.Step)
            {
                case SetupStep.Welcome: Shell.CancelSetup(); break;
                case SetupStep.Location: State.MoveSetupTo(SetupStep.Welcome); break;
                case SetupStep.Review:
                case SetupStep.Installing: State.MoveSetupTo(SetupStep.Location); break;
                case SetupStep.Complete: State.FinishSetup(); break;
            }
        });
    }

    private async void Secondary_Click(object sender, RoutedEventArgs e)
    {
        if (State.Busy) return;
        if (State.Setup.Step == SetupStep.Complete)
        {
            localError = "";
            try { await State.StartSetupWorkbenchAsync(); }
            catch (Exception ex) { ShowLocalError(ex); }
            return;
        }
        RunUi(() =>
        {
            switch (State.Setup.Step)
            {
                case SetupStep.Welcome:
                    State.SetSetupMode(false);
                    break;
                case SetupStep.Installing: State.MoveSetupTo(SetupStep.Location); break;
            }
        });
    }

    private void Logs_Click(object sender, RoutedEventArgs e) => RunUi(Shell.ShowSetupLogs);
    private void RunUi(Action action)
    {
        localError = "";
        try { action(); }
        catch (Exception ex) { ShowLocalError(ex); }
    }
    private void ShowLocalError(Exception ex)
    {
        localError = ex.Message;
        State.AppendLog(ex.Message);
        ErrorBar.Message = localError;
        ErrorBar.IsOpen = true;
    }
}

public sealed record SetupStageRow(string Title, string Detail, string State, string Marker, Brush Brush);
