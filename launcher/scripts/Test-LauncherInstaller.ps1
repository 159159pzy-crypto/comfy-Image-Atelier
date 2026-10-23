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
$script:installerRun = 0
function Run-Installer([string]$Executable, [string[]]$Arguments, [bool]$ExpectFailure = $false, [string]$ExpectedMessage = '') {
    $script:installerRun++
    $logPath = Join-Path $root ('installer-' + $script:installerRun + '.log')
    $Arguments = @($Arguments | Where-Object { $_ -notlike '/LOG=*' }) + ('/LOG="' + $logPath + '"')
    $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -PassThru -WindowStyle Hidden
    if (-not $process.WaitForExit(90000)) { $process.Kill(); throw 'Isolated installer timed out.' }
    if ($ExpectFailure -and $process.ExitCode -eq 0) { throw 'Installer unexpectedly accepted a blocked operation.' }
    if ($ExpectFailure -and $ExpectedMessage -and
        (-not (Test-Path -LiteralPath $logPath) -or -not ([System.IO.File]::ReadAllText($logPath).Contains($ExpectedMessage)))) {
        if (Test-Path -LiteralPath $logPath) { Get-Content -LiteralPath $logPath -Tail 60 | Write-Output }
        throw "Installer failed without the expected guard message: $ExpectedMessage"
    }
    if (-not $ExpectFailure -and $process.ExitCode -ne 0) {
        if (Test-Path -LiteralPath $logPath) { Get-Content -LiteralPath $logPath -Tail 60 | Write-Output }
        throw "Isolated installer failed: $($process.ExitCode); log: $logPath"
    }
}
try {
    $env:ANIMA_LAUNCHER_HOME = $state
    # Hosted checkout paths can make native bundle members exceed MAX_PATH.
    # Keep the state/installation paths under test, but use a short extraction cache.
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path ([System.IO.Path]::GetTempPath()) ('anima-installer-' + [Guid]::NewGuid().ToString('N'))
    [System.IO.Directory]::CreateDirectory($state) | Out-Null
    $sentinel = Join-Path $state 'user-data-preserved.txt'
    [System.IO.File]::WriteAllText($sentinel, 'preserve user data')
    $probeReport = Join-Path $root 'installation-status.json'
    $probeErrors = Join-Path $root 'installation-status-stderr.log'
    $probe = Start-Process -FilePath $source -ArgumentList @('--installation-status', '--report', ('"' + $probeReport + '"')) -PassThru -WindowStyle Hidden -RedirectStandardError $probeErrors
    if (-not $probe.WaitForExit(90000)) { $probe.Kill(); throw 'Installation status preflight timed out.' }
    if ($probe.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $probeReport)) {
        if (Test-Path -LiteralPath $probeErrors) { Get-Content -LiteralPath $probeErrors | Write-Output }
        throw "Installation status preflight failed: $($probe.ExitCode)"
    }
    $probeStatus = Get-Content -LiteralPath $probeReport -Raw | ConvertFrom-Json
    if ($probeStatus.protocolVersion -ne 1 -or $probeStatus.stateDirectory -ne $state -or
        $probeStatus.effectiveVersion -ne $info.version -or $probeStatus.canInstall -ne $true -or
        $probeStatus.running -ne $false -or $probeStatus.updateInProgress -ne $false) {
        throw 'Installation status preflight did not confirm the isolated clean state.'
    }
    Write-Output 'PASS: installation status probe starts and confirms the isolated clean state'
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOICONS', ('/DIR="' + $installed + '"'), ('/LOG="' + (Join-Path $root 'install.log') + '"'))
    $updateRoot = Join-Path $state 'launcher-updates'
    [System.IO.Directory]::CreateDirectory($updateRoot) | Out-Null
    $updateState = Join-Path $updateRoot 'state.json'
    $portableState = [ordered]@{ protocolVersion = 1; active = @{ version = '9.0.0'; executable = $source; size = (Get-Item -LiteralPath $source).Length; sha256 = (Get-FileHash -LiteralPath $source).Hash.ToLowerInvariant() } }
    [System.IO.File]::WriteAllText($updateState, ($portableState | ConvertTo-Json -Depth 5))
    try { Run-Installer $setup $arguments $true 'A newer launcher is already active. Downgrades are not supported.' } finally { Remove-Item -LiteralPath $updateState }
    if (Test-Path -LiteralPath (Join-Path $installed 'AnimaRandomStudio.exe')) { throw 'First-install downgrade guard wrote program files.' }
    Write-Output 'PASS: first Setup refuses a newer active portable launcher in shared state'
    [System.IO.File]::WriteAllText($updateState, ('{"protocolVersion":1,"pendingTransactionId":"' + [Guid]::NewGuid().ToString('N') + '"}'))
    try { Run-Installer $setup $arguments $true 'Close Anima Random Studio and wait for its update to finish before installing.' } finally { Remove-Item -LiteralPath $updateState }
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
