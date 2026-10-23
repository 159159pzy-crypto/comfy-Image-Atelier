param(
    [Parameter(Mandatory)][string]$CandidateDirectory,
    [string]$ExpectedCommit = '',
    [string]$ExpectedVersion = '',
    [switch]$RequireClean
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Package-Launcher.ps1')
$candidate = [System.IO.Path]::GetFullPath($CandidateDirectory)
$lines = [System.IO.File]::ReadAllLines((Join-Path $candidate 'SHA256SUMS.txt'))
if ($lines.Count -ne 3) { throw 'Candidate checksums must cover EXE, portable ZIP and Setup.' }
$hashes = @{}
foreach ($line in $lines) {
    if ($line -notmatch '^([a-f0-9]{64})  (AnimaRandomStudio(?:\.exe|-Setup-win-x64\.exe|-portable-win-x64\.zip))$') { throw 'Invalid candidate SHA256SUMS.txt.' }
    if ($hashes.ContainsKey($Matches[2])) { throw 'Duplicate candidate checksum filename.' }
    $hashes[$Matches[2]] = $Matches[1]
}
$archiveNames = @($hashes.Keys | Where-Object { $_ -like '*.zip' })
if (-not $hashes.ContainsKey('AnimaRandomStudio.exe') -or -not $hashes.ContainsKey('AnimaRandomStudio-Setup-win-x64.exe') -or $archiveNames.Count -ne 1) { throw 'Incomplete candidate checksum asset list.' }
foreach ($name in $hashes.Keys) {
    if ((Get-FileHash -LiteralPath (Join-Path $candidate $name) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hashes[$name]) { throw "Candidate hash does not match SHA256SUMS.txt: $name" }
}
$exe = Join-Path $candidate 'AnimaRandomStudio.exe'
Test-LauncherPackageIntegrity -ZipPath (Join-Path $candidate $archiveNames[0]) -ExecutablePath $exe | Out-Null
$infoText = [System.IO.File]::ReadAllText((Join-Path $candidate 'build-info.json'))
$info = $infoText | ConvertFrom-Json
if ($info.packageFormat -ne 'single-exe-v1' -or $info.runtimeIdentifier -ne 'win-x64') { throw 'Unsupported candidate package format or architecture.' }
if ($ExpectedCommit -and $info.sourceCommit -ne $ExpectedCommit) { throw 'Candidate commit does not match the release tag.' }
if ($ExpectedVersion -and $info.version -ne $ExpectedVersion) { throw 'Candidate version does not match the release tag.' }
if ($RequireClean -and ($info.sourceKind -ne 'git-checkout' -or $info.sourceDirty -ne $false)) { throw 'Release requires a known, clean Git checkout.' }
if ($info.version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid candidate version.' }
$verify = Join-Path $candidate ('.verification-' + [Guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($verify) | Out-Null
$report = Join-Path $verify 'package-info.json'
$previousHome = $env:ANIMA_LAUNCHER_HOME
$previousExtract = $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR
try {
    $env:ANIMA_LAUNCHER_HOME = Join-Path $verify 'state'
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $verify 'bundle'
    $process = Start-Process -FilePath $exe -ArgumentList @('--package-info', '--report', ('"' + $report + '"')) -PassThru -WindowStyle Hidden
    if (-not $process.WaitForExit(120000)) { $process.Kill(); throw 'Candidate package-info timed out.' }
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $report)) { throw "Candidate package-info failed: exit $($process.ExitCode)." }
    $package = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    if ($package.protocolVersion -ne 1 -or $package.version -ne $info.version -or $package.packageFormat -ne 'single-exe-v1') { throw 'Executable package metadata differs from its release metadata.' }
    if (($package.buildInfo | ConvertTo-Json -Depth 20 -Compress) -cne ($info | ConvertTo-Json -Depth 20 -Compress)) { throw 'External and embedded build-info.json differ.' }
    foreach ($required in @('AnimaRandomStudio.dll', 'Anima.Launcher.Core.dll', 'AnimaRandomStudio.deps.json', 'AnimaRandomStudio.runtimeconfig.json', 'AnimaRandomStudio.pri', 'Microsoft.UI.Xaml.dll', 'System.Private.CoreLib.dll', 'README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.txt', 'third-party-packages.json', 'build-info.json')) {
        if ($package.files.path -notcontains $required) { throw "Required embedded file missing: $required" }
    }
} finally {
    $env:ANIMA_LAUNCHER_HOME = $previousHome
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $previousExtract
}
Write-Output "PASS: candidate $($info.version); standalone EXE, EXE-only ZIP, Setup checksums and embedded files verified."
