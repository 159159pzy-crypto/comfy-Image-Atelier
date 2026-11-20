param(
    [Parameter(Mandatory)][int]$LauncherProcessId,
    [Parameter(Mandatory)][string]$CandidateDirectory,
    [Parameter(Mandatory)][string]$ComfyRoot,
    [Parameter(Mandatory)][string]$Python,
    [Parameter(Mandatory)][int]$ComfyPort,
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [int]$WorkbenchPort,
    [switch]$VerifyExternalTree,
    [string]$RestartedStateDirectory,
    [switch]$ExitWithOwnedServices,
    [switch]$FromCompletion
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$expected = [IO.Path]::GetFullPath((Join-Path $CandidateDirectory 'AnimaRandomStudio.exe'))
$process = Get-Process -Id $LauncherProcessId -ErrorAction SilentlyContinue
$activeCopy = $null
if ($RestartedStateDirectory) {
    $updateRoot = (Get-Item -LiteralPath (Join-Path $RestartedStateDirectory 'launcher-updates')).FullName
    $record = (Get-Content -Raw -LiteralPath (Join-Path $updateRoot 'state.json') | ConvertFrom-Json).active
    $activeCopy = [IO.Path]::GetFullPath($record.executable)
    if (!$activeCopy.StartsWith($updateRoot + '\',[StringComparison]::OrdinalIgnoreCase) -or
        (Get-FileHash -LiteralPath $expected).Hash -ne $record.sha256 -or
        (Get-FileHash -LiteralPath $activeCopy).Hash -ne $record.sha256) {
        throw 'Installed launcher differs from the specified candidate.'
    }
}
if ($process -and $process.Path -ne $expected -and $process.Path -ne $activeCopy) { throw 'Unexpected candidate process.' }
$null = [IO.Directory]::CreateDirectory($EvidenceDirectory)
$trace = Join-Path $EvidenceDirectory 'wizard-actions.log'
$desc = [Windows.Automation.TreeScope]::Descendants
$windowDeadline = [DateTime]::UtcNow.AddSeconds(45)
do {
    if ($process) { $process.Refresh() }
    if (!$process -or $process.HasExited) {
        if (!$activeCopy) { throw 'Candidate exited before opening its window.' }
        $process = Get-Process -Name 'AnimaRandomStudio' -ErrorAction SilentlyContinue |
            Where-Object Path -eq $activeCopy | Select-Object -First 1
        if (!$process) { Start-Sleep -Milliseconds 250; continue }
        $LauncherProcessId = $process.Id
    }
    $windowHandle = $process.MainWindowHandle
    if ($windowHandle -ne [IntPtr]::Zero) {
        $root = [Windows.Automation.AutomationElement]::FromHandle($windowHandle)
        if ($root -and $root.Current.ProcessId -eq $LauncherProcessId) { break }
    }
    Start-Sleep -Milliseconds 250
} while ([DateTime]::UtcNow -lt $windowDeadline)
if (!$root -or $root.Current.ProcessId -ne $LauncherProcessId) { throw 'Candidate window is unavailable.' }
function Record([string]$line) {
    Add-Content -LiteralPath $trace -Encoding utf8 -Value ("{0:o} {1}" -f [DateTimeOffset]::Now,$line)
}
function FindControl([string]$property,[string]$value) {
    $field = if ($property -eq 'Name') { [Windows.Automation.AutomationElement]::NameProperty }
        else { [Windows.Automation.AutomationElement]::AutomationIdProperty }
    $condition = [Windows.Automation.PropertyCondition]::new($field,$value)
    return $root.FindFirst($desc,$condition)
}
function Control([string]$id,[string]$name='') {
    $element = FindControl 'ID' $id
    if (!$element -and $name) { $element = FindControl 'Name' $name }
    return $element
}
function WaitControl([string]$id,[string]$namePattern,[int]$seconds,[string]$fallback='') {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $seconds) {
        if ($process.HasExited) { throw 'Candidate exited during onboarding.' }
        $errorText = Control 'SetupError'
        if ($errorText -and $errorText.Current.Name) { throw $errorText.Current.Name }
        $element = Control $id $fallback
        if ($element -and $element.Current.IsEnabled -and $element.Current.Name -match $namePattern) { return $element }
        Start-Sleep -Milliseconds 350
    }
    throw "Timed out waiting for $id ($namePattern)"
}
function InvokeControl($element) {
    Record ("invoke " + $element.Current.AutomationId + " " + $element.Current.Name)
    $element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function SetField([string]$id,[string]$value) {
    $field = WaitControl $id '.*' 10
    $field.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($value)
    Record ("set " + $id + " " + $value)
}
function GetTreeDigest {
    $base = (Get-Item -LiteralPath $ComfyRoot).FullName
    $manifest = foreach ($entry in (Get-ChildItem -LiteralPath $base -Recurse -Force | Sort-Object FullName)) {
        $rel = $entry.FullName.Substring($base.Length)
        if ($entry.PSIsContainer) { "D $rel" }
        else {
            $stream = [IO.File]::Open($entry.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read,
                ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
            try { "F $rel $((Get-FileHash -Algorithm SHA256 -InputStream $stream).Hash)" }
            finally { $stream.Dispose() }
        }
    }
    return $manifest
}
if ($ExitWithOwnedServices) {
    $more = WaitControl 'MoreActions' '更多操作' 30 '更多操作'
    InvokeControl $more
    $name = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'停止服务并退出')
    $pidFilter = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$LauncherProcessId)
    $filter = [Windows.Automation.AndCondition]::new($pidFilter,$name)
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $exitAction = [Windows.Automation.AutomationElement]::RootElement.FindFirst($desc,$filter)
        if ($exitAction) { break }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    if (!$exitAction) { throw 'The candidate exit action did not appear.' }
    InvokeControl $exitAction
    if (!$process.WaitForExit(45000)) { throw 'Candidate did not finish exiting.' }
    $null = Invoke-RestMethod "http://127.0.0.1:$ComfyPort/system_stats" -TimeoutSec 5
    Record 'launcher exited via its menu and external ComfyUI remained online'
    return
}
if ($RestartedStateDirectory) {
    if (!$WorkbenchPort) { throw 'Restart verification requires the workbench port.' }
    $action = WaitControl 'ConfigureButton' '管理安装环境' 60 '管理安装环境'
    $config = Get-Content -Raw -LiteralPath (Join-Path $RestartedStateDirectory 'launcher.json') | ConvertFrom-Json
    $saved = Get-Content -Raw -LiteralPath (Join-Path $RestartedStateDirectory 'onboarding.json') | ConvertFrom-Json
    $proof = Get-Content -Raw -LiteralPath (Join-Path $EvidenceDirectory 'first-image.json') | ConvertFrom-Json
    $web = "http://127.0.0.1:$WorkbenchPort"
    try { $health = Invoke-RestMethod "$web/api/launcher-health" -TimeoutSec 2 } catch { $health = $null }
    if (!$health) {
        InvokeControl (WaitControl 'StartAllButton' '启动并打开工作台|打开创作工作台' 30 '启动并打开工作台')
        $readyUntil = [DateTime]::UtcNow.AddMinutes(2)
        do {
            Start-Sleep -Milliseconds 500
            try { $health = Invoke-RestMethod "$web/api/launcher-health" -TimeoutSec 2 } catch { $health = $null }
        } while (!$health -and [DateTime]::UtcNow -lt $readyUntil)
    }
    if (!$health -or $health.python_executable -ne $proof.health.python_executable -or
        $health.data_dir -ne $proof.health.data_dir -or $health.comfy_url -ne $proof.health.comfy_url) {
        throw 'Workbench identity changed after restart.'
    }
    $batch = Invoke-RestMethod "$web/api/batches/$($proof.batch.id)"
    if (!$config.setupComplete -or $config.externalEnvironmentMode -ne 'readOnly' -or
        !$saved.installFinished -or $saved.step -ne 5 -or $config.comfyPort -ne $ComfyPort -or
        $config.webPort -ne $WorkbenchPort -or $batch.status -ne 'completed' -or
        $batch.image_ids[0] -ne $proof.imageId) { throw 'Completed configuration or batch identity changed on restart.' }
    $image = (Invoke-WebRequest "$web/api/images/$($proof.imageId)").RawContentStream
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash = [BitConverter]::ToString($sha.ComputeHash($image)).Replace('-','') }
    finally { $image.Dispose(); $sha.Dispose() }
    if ($hash -ne $proof.pngSHA256) { throw 'Image bytes changed on restart.' }
    [ordered]@{ pid=$LauncherProcessId; overviewAction=$action.Current.Name; setupComplete=$config.setupComplete
        finishedStep=$saved.step; externalEnvironmentMode=$config.externalEnvironmentMode; batch=$batch
        imageSHA256=$hash; python=$health.python_executable } | ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8 -LiteralPath (Join-Path $EvidenceDirectory 'restart.json')
    Record 'restart verified overview, completed configuration, persisted batch and original PNG bytes'
    return
}
if ($VerifyExternalTree) {
    if ($FromCompletion) { throw 'Tree verification requires starting before import.' }
    $beforeImport = GetTreeDigest
}
if (!$FromCompletion) {
    InvokeControl (WaitControl 'SetupSecondary' '我已有 ComfyUI' 30)
    SetField 'SetupRoot' $ComfyRoot
    SetField 'SetupImportPort' ([string]$ComfyPort)
    $repair = WaitControl 'SetupAllowRepair' '允许补齐' 10
    if ($repair.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne 'Off') {
        throw 'Acceptance requires read-only import.'
    }
    $expander = WaitControl 'SetupPythonSettings' '手动指定 Python' 10 '手动指定 Python'
    $expand = $expander.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)
    if ($expand.Current.ExpandCollapseState -ne 'Expanded') { $expand.Expand() }
    SetField 'SetupPython' $Python
    InvokeControl (WaitControl 'SetupPrimary' '检查并继续' 10)
    InvokeControl (WaitControl 'SetupPrimary' '连接|开始安装|准备工作台' 120)
}
$until = [DateTime]::UtcNow.AddMinutes(15)
do {
    $errorText = Control 'SetupError'
    if (!$FromCompletion -and $errorText -and $errorText.Current.Name) { throw $errorText.Current.Name }
    $later = Control 'SetupSecondary'
    if ($later -and $later.Current.Name -match '稍后配置|跳过助手') { InvokeControl $later }
    $smoke = Control 'SetupSmoke' '试生成一张 512 × 512 图片'
    if ($smoke -and $smoke.Current.IsEnabled) { break }
    Start-Sleep -Milliseconds 500
} while ([DateTime]::UtcNow -lt $until)
if (!$smoke -or !$smoke.Current.IsEnabled) { throw 'Manual generation action did not become available.' }
if ($VerifyExternalTree) {
    $changes = Compare-Object $beforeImport (GetTreeDigest)
    if ($changes) {
        $changes | Out-File (Join-Path $EvidenceDirectory 'external-tree-diff.txt')
        throw 'External tree changed before generation; see external-tree-diff.txt.'
    }
    Record ("read-only import preserved paths and SHA-256 of all " + $beforeImport.Count + " entries")
}
if ($WorkbenchPort) {
    $web = "http://127.0.0.1:$WorkbenchPort"
    $health = Invoke-RestMethod "$web/api/launcher-health"
    if ($health.comfy_url.TrimEnd('/') -ne "http://127.0.0.1:$ComfyPort") { throw 'Unexpected ComfyUI binding.' }
    $previous = @( (Invoke-RestMethod "$web/api/history").items | ForEach-Object { $_.id } )
}
InvokeControl $smoke
$result = WaitControl 'SetupSmokeResult' '已生成|测试图已完成|生成成功|完成.*512' 600 '试生成成功，图片已保存到工作台历史记录。'
Record $result.Current.Name
if ($WorkbenchPort) {
    $added = @((Invoke-RestMethod "$web/api/history").items | Where-Object { $_.id -notin $previous })
    if ($added.Count -ne 1) { throw 'Expected exactly one new history record.' }
    $batch = Invoke-RestMethod "$web/api/batches/$($added[0].batch_id)"
    if ($batch.status -ne 'completed' -or $batch.total -ne 1 -or $batch.image_ids[0] -ne $added[0].id) {
        throw 'Batch and history artifact do not agree.'
    }
    $imagePath = Join-Path $EvidenceDirectory 'first-image.png'
    Invoke-WebRequest "$web/api/images/$($added[0].id)" -OutFile $imagePath
    Add-Type -AssemblyName System.Drawing
    $bitmap = [Drawing.Image]::FromFile($imagePath)
    try {
        if ($bitmap.Width -ne 512 -or $bitmap.Height -ne 512 -or
            $bitmap.RawFormat.Guid -ne [Drawing.Imaging.ImageFormat]::Png.Guid) { throw 'Unexpected output format or resolution.' }
    } finally { $bitmap.Dispose() }
    [ordered]@{ health=$health; batch=$batch; imageId=$added[0].id; filename=$added[0].filename
        pngSHA256=(Get-FileHash -Algorithm SHA256 -LiteralPath $imagePath).Hash; width=512; height=512
        externalTreeEntriesChecked=if ($VerifyExternalTree) { $beforeImport.Count } else { 0 }
    } | ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8 -LiteralPath (Join-Path $EvidenceDirectory 'first-image.json')
}
InvokeControl (WaitControl 'SetupPrimary' '打开工作台|启动工作台' 30)
Record 'requested workbench opening'
