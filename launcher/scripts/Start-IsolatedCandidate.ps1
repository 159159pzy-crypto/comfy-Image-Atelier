param([string]$CandidateDirectory = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
$candidate = [System.IO.Path]::GetFullPath($CandidateDirectory)
$executable = Join-Path $candidate 'AnimaRandomStudio.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Candidate executable not found: $executable" }
$state = Join-Path $candidate '.preview-state'
[System.IO.Directory]::CreateDirectory($state) | Out-Null
$configuration = Join-Path $state 'launcher.json'
if (-not (Test-Path -LiteralPath $configuration)) {
    $initial = [ordered]@{
        schemaVersion = 1
        startOnLaunch = $false
        autoCheckUpdates = $false
        comfyPort = 19992
        webPort = 19994
        dataDir = (Join-Path $state 'data')
    }
    $stream = [System.IO.File]::Open($configuration, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    try {
        $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes(($initial | ConvertTo-Json) + "`n")
        $stream.Write($bytes, 0, $bytes.Length)
    } finally { $stream.Dispose() }
}
$launch = [System.Diagnostics.ProcessStartInfo]::new($executable)
$launch.UseShellExecute = $false
$launch.WorkingDirectory = $candidate
$launch.ArgumentList.Add('--show')
$launch.Environment['ANIMA_LAUNCHER_HOME'] = $state
$launch.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = Join-Path $candidate '.preview-bundle'
[System.Diagnostics.Process]::Start($launch) | Out-Null
Write-Output "Isolated candidate started. Configuration: $state"
