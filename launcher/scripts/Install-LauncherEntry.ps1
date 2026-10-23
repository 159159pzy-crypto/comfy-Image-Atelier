#requires -Version 7.0
param([Parameter(Mandatory)][string]$CandidateDirectory, [string]$TargetDirectory = (Join-Path $PSScriptRoot '..\..'))
$ErrorActionPreference = 'Stop'
$candidate = [System.IO.Path]::GetFullPath($CandidateDirectory)
$target = [System.IO.Path]::GetFullPath($TargetDirectory)
if (-not (Test-Path -LiteralPath $target -PathType Container)) { throw 'The target project directory must already exist.' }
& (Join-Path $PSScriptRoot 'Test-LauncherCandidate.ps1') -CandidateDirectory $candidate
$source = Join-Path $candidate 'AnimaRandomStudio.exe'
$entry = Join-Path $target 'AnimaRandomStudio.exe'
$temporary = Join-Path $target ('.anima-entry-' + [Guid]::NewGuid().ToString('N') + '.tmp')
try {
    Copy-Item -LiteralPath $source -Destination $temporary
    if (Test-Path -LiteralPath $entry) {
        $backupRoot = Join-Path $target '.launcher'
        [System.IO.Directory]::CreateDirectory($backupRoot) | Out-Null
        Copy-Item -LiteralPath $entry -Destination (Join-Path $backupRoot ('entry-backup-' + [Guid]::NewGuid().ToString('N') + '.exe'))
    }
    [System.IO.File]::Move($temporary, $entry, $true)
} finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
[ordered]@{ entry = $entry; sha256 = (Get-FileHash -LiteralPath $entry -Algorithm SHA256).Hash.ToLowerInvariant(); packageFormat = 'single-exe-v1' } | ConvertTo-Json
