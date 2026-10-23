param([Parameter(Mandatory)][string]$CandidateDirectory, [string]$TestRoot = '')
$ErrorActionPreference = 'Stop'
$repo = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$candidate = [System.IO.Path]::GetFullPath($CandidateDirectory)
$id = 'AnimaRandomStudio.Test.' + [Guid]::NewGuid().ToString('N')
if (-not $TestRoot) { $TestRoot = Join-Path $repo ('output\launcher-installer-tests\' + $id) }
$root = [System.IO.Path]::GetFullPath($TestRoot)
if (Test-Path -LiteralPath $root) { throw 'Installer test requires a fresh isolated directory.' }
[System.IO.Directory]::CreateDirectory($root) | Out-Null
$pin = (Get-Content -LiteralPath (Join-Path $repo 'launcher\installer\tools.json') -Raw | ConvertFrom-Json).innoSetup
$compiler = Join-Path $repo ('tools\inno-setup-' + $pin.version + '\ISCC.exe')
if ((Get-FileHash -LiteralPath $compiler -Algorithm SHA256).Hash.ToLowerInvariant() -ne $pin.compilerSha256) { throw 'Pinned Inno compiler unavailable; build a candidate first.' }
$source = Join-Path $candidate 'AnimaRandomStudio.exe'
$info = Get-Content -LiteralPath (Join-Path $candidate 'build-info.json') -Raw | ConvertFrom-Json
$state = Join-Path $root 'state'
$suffix = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($state.ToUpperInvariant()))).Substring(0, 12)
$mutex = 'Local\AnimaRandomStudioLauncher-' + $suffix
$output = Join-Path $root 'setup'
& $compiler '/Q' ("/DAppVersion=$($info.version)") ("/DSourceExe=$source") ("/DOutputDirectory=$output") ("/DInstallerAppId=$id") ("/DInstallerMutex=$mutex") (Join-Path $repo 'launcher\installer\AnimaRandomStudio.iss')
if ($LASTEXITCODE -ne 0) { throw 'Isolated installer test build failed.' }
$setup = Join-Path $output 'AnimaRandomStudio-Setup-win-x64.exe'
$installed = Join-Path $root 'installed'
$registryPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\' + $id + '_is1'
$previousHome = $env:ANIMA_LAUNCHER_HOME
$previousExtract = $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR
function Run-Installer([string]$Executable, [string[]]$Arguments, [bool]$ExpectFailure = $false) {
    $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -PassThru -WindowStyle Hidden
    if (-not $process.WaitForExit(90000)) { $process.Kill(); throw 'Isolated installer timed out.' }
    if ($ExpectFailure -and $process.ExitCode -eq 0) { throw 'Installer unexpectedly accepted a blocked operation.' }
    if (-not $ExpectFailure -and $process.ExitCode -ne 0) { throw "Isolated installer failed: $($process.ExitCode)" }
}
try {
    $env:ANIMA_LAUNCHER_HOME = $state
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $root 'bundle'
    [System.IO.Directory]::CreateDirectory($state) | Out-Null
    $sentinel = Join-Path $state 'user-data-preserved.txt'
    [System.IO.File]::WriteAllText($sentinel, 'preserve user data')
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOICONS', ('/DIR="' + $installed + '"'), ('/LOG="' + (Join-Path $root 'install.log') + '"'))
    $updateRoot = Join-Path $state 'launcher-updates'
    [System.IO.Directory]::CreateDirectory($updateRoot) | Out-Null
    $updateState = Join-Path $updateRoot 'state.json'
    $portableState = [ordered]@{ protocolVersion = 1; active = @{ version = '9.0.0'; executable = $source; size = (Get-Item -LiteralPath $source).Length; sha256 = (Get-FileHash -LiteralPath $source).Hash.ToLowerInvariant() } }
    [System.IO.File]::WriteAllText($updateState, ($portableState | ConvertTo-Json -Depth 5))
    try { Run-Installer $setup $arguments $true } finally { Remove-Item -LiteralPath $updateState }
    if (Test-Path -LiteralPath (Join-Path $installed 'AnimaRandomStudio.exe')) { throw 'First-install downgrade guard wrote program files.' }
    Write-Output 'PASS: first Setup refuses a newer active portable launcher in shared state'
    [System.IO.File]::WriteAllText($updateState, ('{"protocolVersion":1,"pendingTransactionId":"' + [Guid]::NewGuid().ToString('N') + '"}'))
    try { Run-Installer $setup $arguments $true } finally { Remove-Item -LiteralPath $updateState }
    Write-Output 'PASS: first Setup refuses a pending portable software update'
    Run-Installer $setup $arguments
    if ((Get-FileHash -LiteralPath (Join-Path $installed 'AnimaRandomStudio.exe')).Hash -ne (Get-FileHash -LiteralPath $source).Hash) { throw 'Installed EXE differs from portable EXE.' }
    if (-not (Test-Path -LiteralPath $registryPath)) { throw 'Per-user uninstall registration is missing.' }
    Write-Output 'PASS: per-user install preserves the exact portable EXE bytes'
    $runningMutex = [System.Threading.Mutex]::new($true, $mutex)
    try { Run-Installer $setup $arguments $true } finally { $runningMutex.ReleaseMutex(); $runningMutex.Dispose() }
    Write-Output 'PASS: installer refuses to overwrite a running launcher'
    [System.IO.File]::WriteAllText($updateState, ('{"protocolVersion":1,"pendingTransactionId":"' + [Guid]::NewGuid().ToString('N') + '"}'))
    try {
        Run-Installer $setup $arguments $true
        Run-Installer (Join-Path $installed 'unins000.exe') @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') $true
    } finally { Remove-Item -LiteralPath $updateState }
    Write-Output 'PASS: installer and uninstaller refuse a pending software update'
    $downgradeOutput = Join-Path $root 'downgrade'
    & $compiler '/Q' '/DAppVersion=0.0.1' ("/DSourceExe=$source") ("/DOutputDirectory=$downgradeOutput") ("/DInstallerAppId=$id") ("/DInstallerMutex=$mutex") (Join-Path $repo 'launcher\installer\AnimaRandomStudio.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Downgrade fixture build failed.' }
    Run-Installer (Join-Path $downgradeOutput 'AnimaRandomStudio-Setup-win-x64.exe') $arguments $true
    Write-Output 'PASS: installer rejects a downgrade'
    Run-Installer $setup $arguments
    Write-Output 'PASS: equal-version repair succeeds'
    Run-Installer (Join-Path $installed 'unins000.exe') @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', ('/LOG="' + (Join-Path $root 'uninstall.log') + '"'))
    if ((Test-Path -LiteralPath (Join-Path $installed 'AnimaRandomStudio.exe')) -or (Test-Path -LiteralPath $registryPath)) { throw 'Uninstall did not remove the test program or its isolated registration.' }
    if ([System.IO.File]::ReadAllText($sentinel) -ne 'preserve user data') { throw 'Uninstall changed shared user data.' }
    Write-Output 'PASS: uninstall removes only program/registration and preserves shared user data'
} finally {
    $env:ANIMA_LAUNCHER_HOME = $previousHome
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $previousExtract
}
Write-Output "Installer acceptance retained in $root"
