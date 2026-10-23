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
Write-Output "Fixtures retained in $work"
