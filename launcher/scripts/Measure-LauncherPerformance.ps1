param(
    [Parameter(Mandatory)][string]$ExecutablePath,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateRange(0, 20)][int]$ColdRuns = 3,
    [ValidateRange(0, 20)][int]$WarmRuns = 5,
    [ValidateRange(0, 600)][int]$ObserveSeconds = 60,
    [ValidateRange(10, 300)][int]$StartupTimeoutSeconds = 90
)
$ErrorActionPreference = 'Stop'
$executable = [System.IO.Path]::GetFullPath($ExecutablePath)
$output = [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetFullPath($OutputDirectory))
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Launcher not found: $executable" }
if ($ColdRuns + $WarmRuns -eq 0) { throw 'At least one startup sample is required.' }
if (Test-Path -LiteralPath $output) { throw "Use a new measurement directory; existing evidence is preserved: $output" }
[System.IO.Directory]::CreateDirectory($output) | Out-Null

if (-not ('LauncherPerformanceNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class LauncherPerformanceNative {
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError=true)]
    public static extern IntPtr SendMessageTimeoutW(IntPtr window, uint message, UIntPtr wParam,
        IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
}
'@
}

$samples = [System.Collections.Generic.List[object]]::new()
$idleSamples = [System.Collections.Generic.List[object]]::new()
$reportPath = Join-Path $output 'performance.json'
$report = [ordered]@{
    schemaVersion = 1
    executable = $executable
    executableSha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant()
    executableBytes = (Get-Item -LiteralPath $executable).Length
    startedAtUtc = [DateTime]::UtcNow.ToString('o')
    scenario = 'Isolated first-run onboarding; no environment or downloads; automatic service startup and update checks disabled.'
    readiness = 'Process start to a newly appended native-window-ready log, visible main HWND, input-idle signal, and successful WM_NULL response. This approximates first interaction, not rendered-frame latency.'
    coldDefinition = 'Fresh application state and empty .NET extraction directory for every sample. OS filesystem caches are not flushed.'
    warmDefinition = 'Every sample has its own state/cache, initialized by one untimed GUI launch; measure a repeat launch of its registered active executable (identical bytes).'
    samplingIntervalMs = 50
    machine = [ordered]@{ os = [Environment]::OSVersion.VersionString; logicalProcessors = [Environment]::ProcessorCount; powershell = $PSVersionTable.PSVersion.ToString() }
    samples = @()
    idleSamples = @()
    summary = $null
}

function Save-Report {
    $report.samples = @($samples.ToArray())
    $report.idleSamples = @($idleSamples.ToArray())
    [System.IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 10) + "`n", [System.Text.UTF8Encoding]::new($false))
}
function New-IsolatedState([string]$directory) {
    $state = Join-Path $directory 'state'
    [System.IO.Directory]::CreateDirectory($state) | Out-Null
    $ports = @()
    $listeners = @()
    try {
        1..2 | ForEach-Object {
            $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
            $listener.Start(); $listeners += $listener; $ports += $listener.LocalEndpoint.Port
        }
    } finally { foreach ($listener in $listeners) { $listener.Stop() } }
    $configuration = [ordered]@{
        schemaVersion = 1; startOnLaunch = $false; autoCheckUpdates = $false
        comfyPort = $ports[0]; webPort = $ports[1]; comfyRoot = ''; setupComplete = $false
        installRoot = (Join-Path $directory 'environment'); runtimeRoot = (Join-Path $directory 'runtime')
        dataDir = (Join-Path $state 'data')
    }
    [System.IO.File]::WriteAllText((Join-Path $state 'launcher.json'), ($configuration | ConvertTo-Json) + "`n", [System.Text.UTF8Encoding]::new($false))
    return $state
}
function Read-NewStartupLog([string]$state, [hashtable]$offsets) {
    $logs = Join-Path $state 'logs'
    if (-not (Test-Path -LiteralPath $logs)) { return '' }
    $text = [System.Text.StringBuilder]::new()
    foreach ($file in Get-ChildItem -LiteralPath $logs -File -Filter 'launcher-*.log') {
        $offset = if ($offsets.ContainsKey($file.FullName)) { [long]$offsets[$file.FullName] } else { 0L }
        $stream = [System.IO.File]::Open($file.FullName, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        try {
            if ($stream.Length -lt $offset) { $offset = 0 }
            [void]$stream.Seek($offset, [System.IO.SeekOrigin]::Begin)
            $reader = [System.IO.StreamReader]::new($stream, [System.Text.Encoding]::UTF8, $true, 1024, $true)
            try { [void]$text.Append($reader.ReadToEnd()) } finally { $reader.Dispose() }
        } finally { $stream.Dispose() }
    }
    return $text.ToString()
}
function Start-MeasuredLauncher([string]$path, [string]$state, [string]$bundle) {
    $offsets = @{}
    $logs = Join-Path $state 'logs'
    if (Test-Path -LiteralPath $logs) {
        foreach ($file in Get-ChildItem -LiteralPath $logs -File -Filter 'launcher-*.log') { $offsets[$file.FullName] = $file.Length }
    }
    $info = [System.Diagnostics.ProcessStartInfo]::new($path)
    $info.UseShellExecute = $false
    $info.WorkingDirectory = [System.IO.Path]::GetDirectoryName($path)
    $info.ArgumentList.Add('--show')
    $info.Environment['ANIMA_LAUNCHER_HOME'] = $state
    $info.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = $bundle
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $process = [System.Diagnostics.Process]::Start($info)
    $ready = $false
    try {
        while ($clock.Elapsed.TotalSeconds -lt $StartupTimeoutSeconds) {
            $process.Refresh()
            if ($process.HasExited) { throw "Measured launcher exited before readiness (PID $($process.Id), code $($process.ExitCode)); inspect $logs" }
            $window = $process.MainWindowHandle
            $log = Read-NewStartupLog $state $offsets
            if ($window -ne [IntPtr]::Zero -and $log.Contains('原生窗口就绪') -and [LauncherPerformanceNative]::IsWindowVisible($window)) {
                $inputIdle = $false
                try { $inputIdle = $process.WaitForInputIdle(0) } catch { }
                $response = [UIntPtr]::Zero
                if ($inputIdle -and [LauncherPerformanceNative]::SendMessageTimeoutW($window, 0, [UIntPtr]::Zero, [IntPtr]::Zero, 2, 250, [ref]$response) -ne [IntPtr]::Zero) {
                    $clock.Stop(); $ready = $true
                    return [pscustomobject]@{ Process = $process; ReadyMs = [Math]::Round($clock.Elapsed.TotalMilliseconds, 2); Log = $log; Window = $window }
                }
            }
            Start-Sleep -Milliseconds 50
        }
        throw "Launcher readiness timed out after $StartupTimeoutSeconds seconds; inspect $logs"
    } finally {
        if (-not $ready) { Stop-MeasuredLauncher $process }
    }
}
function Stop-MeasuredLauncher([System.Diagnostics.Process]$process) {
    try {
        # Only the Process object returned by this script is stopped, never a name-wide match.
        if (-not $process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(10000) }
    } finally { $process.Dispose() }
}
function Measure-VisibleIdle($started, [string]$sampleName) {
    if ($ObserveSeconds -eq 0) { return }
    Write-Host "Observing visible idle for $ObserveSeconds seconds ($sampleName)."
    # Let first activation/system detection finish before measuring steady state.
    Start-Sleep -Seconds 5
    $process = $started.Process
    $process.Refresh()
    $previousCpu = $process.TotalProcessorTime.TotalMilliseconds
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $previousElapsed = 0.0
    while ($clock.Elapsed.TotalSeconds -lt $ObserveSeconds) {
        Start-Sleep -Milliseconds 1000
        $process.Refresh()
        if ($process.HasExited) { throw 'Measured launcher exited during idle observation.' }
        $elapsed = $clock.Elapsed.TotalSeconds
        $cpu = $process.TotalProcessorTime.TotalMilliseconds
        $idleSamples.Add([pscustomobject]@{
            sample = $sampleName; elapsedSeconds = [Math]::Round($elapsed, 3)
            intervalSeconds = [Math]::Round($elapsed - $previousElapsed, 3)
            cpuMilliseconds = [Math]::Round($cpu - $previousCpu, 3)
            cpuOneCorePercent = [Math]::Round(($cpu - $previousCpu) / (10 * ($elapsed - $previousElapsed)), 3)
            workingSetBytes = $process.WorkingSet64; privateBytes = $process.PrivateMemorySize64
            handles = $process.HandleCount; threads = $process.Threads.Count
            visible = [LauncherPerformanceNative]::IsWindowVisible($started.Window)
            foreground = [LauncherPerformanceNative]::GetForegroundWindow() -eq $started.Window
        })
        $previousCpu = $cpu; $previousElapsed = $elapsed
        if ($idleSamples.Count % 10 -eq 0) { Save-Report; Write-Host "Idle samples: $($idleSamples.Count) / approximately $ObserveSeconds" }
    }
}
function Median($values) {
    $ordered = @($values | Sort-Object)
    if ($ordered.Count -eq 0) { return $null }
    $middle = [int][Math]::Floor($ordered.Count / 2)
    if ($ordered.Count % 2) { return [double]$ordered[$middle] }
    return ([double]$ordered[$middle - 1] + [double]$ordered[$middle]) / 2
}

Save-Report
try {
    $schedule = @()
    for ($i = 1; $i -le $ColdRuns; $i++) { $schedule += [pscustomobject]@{ Name = ('cold-{0:D2}' -f $i); Warm = $false } }
    for ($i = 1; $i -le $WarmRuns; $i++) { $schedule += [pscustomobject]@{ Name = ('warm-{0:D2}' -f $i); Warm = $true } }
    for ($index = 0; $index -lt $schedule.Count; $index++) {
        $run = $schedule[$index]
        $directory = Join-Path $output $run.Name
        $state = New-IsolatedState $directory
        $bundle = Join-Path $directory 'bundle'
        $launchPath = $executable
        $warmupMs = $null
        if ($run.Warm) {
            $warmup = Start-MeasuredLauncher $executable $state $bundle
            $warmupMs = $warmup.ReadyMs
            Stop-MeasuredLauncher $warmup.Process
            $updateState = Get-Content -LiteralPath (Join-Path $state 'launcher-updates\state.json') -Raw | ConvertFrom-Json
            $launchPath = [System.IO.Path]::GetFullPath($updateState.active.executable)
            if (-not $launchPath.StartsWith($state + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Warm launch entry escaped its isolated state.' }
            if ((Get-FileHash -LiteralPath $launchPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $report.executableSha256) { throw 'Warm launch entry differs from the supplied binary.' }
        }
        $started = Start-MeasuredLauncher $launchPath $state $bundle
        try {
            $started.Process.Refresh()
            $samples.Add([pscustomobject]@{
                name = $run.Name; kind = $(if ($run.Warm) { 'warm' } else { 'cold' }); readyMilliseconds = $started.ReadyMs
                warmupMilliseconds = $warmupMs; executable = $launchPath; stateDirectory = $state; bundleDirectory = $bundle
                processId = $started.Process.Id; workingSetBytesAtReady = $started.Process.WorkingSet64
                privateBytesAtReady = $started.Process.PrivateMemorySize64; handlesAtReady = $started.Process.HandleCount
                startupLog = $started.Log
            })
            Save-Report
            Write-Host "$($run.Name): $($started.ReadyMs) ms to approximate first interaction."
            if ($index -eq $schedule.Count - 1) { Measure-VisibleIdle $started $run.Name }
        } finally { Stop-MeasuredLauncher $started.Process }
    }
    $idleElapsed = ($idleSamples | Measure-Object intervalSeconds -Sum).Sum
    $idleCpu = ($idleSamples | Measure-Object cpuMilliseconds -Sum).Sum
    $report.summary = [ordered]@{
        coldMedianMilliseconds = Median @($samples | Where-Object kind -eq 'cold' | ForEach-Object readyMilliseconds)
        warmMedianMilliseconds = Median @($samples | Where-Object kind -eq 'warm' | ForEach-Object readyMilliseconds)
        idleSeconds = $idleElapsed; idleCpuMilliseconds = $idleCpu
        idleCpuOneCorePercent = $(if ($idleElapsed -gt 0) { [Math]::Round($idleCpu / (10 * $idleElapsed), 4) } else { $null })
        idleWorkingSetMedianBytes = Median @($idleSamples | ForEach-Object workingSetBytes)
        idlePrivateMedianBytes = Median @($idleSamples | ForEach-Object privateBytes)
        idleForegroundSamples = @($idleSamples | Where-Object foreground).Count
        idleVisibleSamples = @($idleSamples | Where-Object visible).Count
        idleSampleCount = $idleSamples.Count
    }
    $report.completedAtUtc = [DateTime]::UtcNow.ToString('o')
} catch {
    $report.failure = $_.Exception.Message
    throw
} finally { Save-Report }
$report.summary | ConvertTo-Json
Write-Output "Performance evidence: $reportPath"
