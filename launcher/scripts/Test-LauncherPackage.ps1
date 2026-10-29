$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Package-Launcher.ps1')
$work = Join-Path ([System.IO.Path]::GetTempPath()) ('Anima package test 中文 ' + [Guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($work) | Out-Null
$exe = Join-Path $work 'AnimaRandomStudio.exe'
[System.IO.File]::WriteAllText($exe, 'single executable fixture')
$sourceInfo = Get-LauncherSourceInfo $work
if ($sourceInfo.sourceCommit -ne 'unknown' -or $sourceInfo.sourceKind -ne 'source-archive' -or $null -ne $sourceInfo.sourceDirty) { throw 'Source archive provenance is wrong.' }
$package = New-LauncherPackage -ExecutablePath $exe -ZipPath (Join-Path $work 'portable.zip')
if ($package.FileCount -ne 1) { throw 'Portable ZIP has extra files.' }
Write-Output 'PASS: portable ZIP contains the identical standalone EXE and no other files'
function Expect-Rejected([scriptblock]$Action, [string]$Name) {
    $failed = $false
    try { & $Action | Out-Null } catch { $failed = $true }
    if (-not $failed) { throw "Expected rejection: $Name" }
    Write-Output "PASS: rejected $Name"
}
[System.IO.File]::WriteAllText($exe, 'different executable')
Expect-Rejected { Test-LauncherPackageIntegrity -ZipPath $package.ZipPath -ExecutablePath $exe } 'standalone/ZIP EXE mismatch'
[System.IO.File]::WriteAllText($exe, 'single executable fixture')
$stream = [System.IO.File]::Open($package.ZipPath, [System.IO.FileMode]::Open)
$zip = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Update)
try { $zip.CreateEntry('unexpected.dll') | Out-Null } finally { $zip.Dispose(); $stream.Dispose() }
Expect-Rejected { Test-LauncherPackageIntegrity -ZipPath $package.ZipPath -ExecutablePath $exe } 'extra loose runtime file'
foreach ($name in @('../AnimaRandomStudio.exe', 'nested/AnimaRandomStudio.exe', 'ANIMARANDOMSTUDIO.EXE')) {
    $bad = Join-Path $work ([Guid]::NewGuid().ToString('N') + '.zip')
    $stream = [System.IO.File]::Create($bad)
    $zip = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create)
    try { [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $exe, $name) | Out-Null } finally { $zip.Dispose(); $stream.Dispose() }
    Expect-Rejected { Test-LauncherPackageIntegrity -ZipPath $bad -ExecutablePath $exe } "invalid entry $name"
}
Assert-LauncherSlimBundle @([pscustomobject]@{ path = 'Microsoft.Windows.AI.MachineLearning.dll' })
foreach ($excludedName in @('DirectML.dll', 'ONNXRUNTIME.DLL', 'nested/onnxruntime_providers_shared.dll')) {
    Expect-Rejected { Assert-LauncherSlimBundle @([pscustomobject]@{ path = $excludedName }) } "unused inference payload $excludedName"
}

$candidate = Join-Path $work 'candidate'
[System.IO.Directory]::CreateDirectory($candidate) | Out-Null
foreach ($name in @('AnimaRandomStudio.exe', 'AnimaRandomStudio-portable-win-x64.zip', 'AnimaRandomStudio-Setup-win-x64.exe', 'FILES.sha256', 'package-info.json')) {
    [System.IO.File]::WriteAllText((Join-Path $candidate $name), 'report fixture')
}
$sizeFixture = [pscustomobject]@{
    version = '1.5.0'
    buildInfo = [pscustomobject]@{ sourceCommit = 'fixture' }
    files = @([pscustomobject]@{ path = 'runtime.dll'; size = 123; sha256 = 'fixture' })
}
Write-LauncherPackageSizeReport -CandidateDirectory $candidate -Package $sizeFixture
$sizeReport = Get-Content -LiteralPath (Join-Path $candidate 'package-size-report.json') -Raw | ConvertFrom-Json
if ($sizeReport.assets.Count -ne 3 -or $sizeReport.embeddedFileCount -ne 2 -or $sizeReport.extractedBytes -ne 137) { throw 'Package size report lost an asset or omitted its inventory bytes.' }
if ($sizeReport.assets[0].sha256 -ne (Get-FileHash -LiteralPath (Join-Path $candidate 'AnimaRandomStudio.exe') -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'Package size report did not hash the final asset.' }
Write-Output 'PASS: size report records final assets and extracted bytes including the inventory'

foreach ($name in @('.publish-staging', '.bundle-content', ('.verification-' + [Guid]::NewGuid().ToString('N')))) {
    $temporary = Join-Path $candidate $name
    [System.IO.Directory]::CreateDirectory((Join-Path $temporary 'nested')) | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $temporary 'nested\payload'), 'temporary fixture')
    Remove-LauncherCandidateTemporaryDirectory -CandidateDirectory $candidate -Path $temporary
    if (Test-Path -LiteralPath $temporary) { throw 'Successful candidate cleanup left temporary contents.' }
}
foreach ($path in @($candidate, (Join-Path $work '.publish-staging'), (Join-Path $candidate '.preview-state'), (Join-Path $candidate '.verification-not-owned'))) {
    Expect-Rejected { Remove-LauncherCandidateTemporaryDirectory -CandidateDirectory $candidate -Path $path } "unsafe candidate cleanup $path"
}
foreach ($name in @('AnimaRandomStudio.exe', 'FILES.sha256', 'package-info.json', 'package-size-report.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $candidate $name))) { throw "Candidate cleanup removed retained evidence: $name" }
}
if ($IsWindows) {
    $linked = Join-Path $candidate '.bundle-content'
    $outside = Join-Path $work 'outside-candidate'
    [System.IO.Directory]::CreateDirectory($linked) | Out-Null
    [System.IO.Directory]::CreateDirectory($outside) | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $outside 'keep.txt'), 'preserved')
    New-Item -ItemType Junction -Path (Join-Path $linked 'linked-cache') -Target $outside | Out-Null
    Expect-Rejected { Remove-LauncherCandidateTemporaryDirectory -CandidateDirectory $candidate -Path $linked } 'junction inside candidate temporary directory'
    if (-not (Test-Path -LiteralPath (Join-Path $outside 'keep.txt'))) { throw 'Cleanup followed a junction.' }
}
Write-Output 'PASS: cleanup removes only owned temporary trees and preserves package reports'
Write-Output "Fixtures retained in $work"
