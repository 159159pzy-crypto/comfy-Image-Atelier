$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Package-Launcher.ps1')
$work = Join-Path ([System.IO.Path]::GetTempPath()) ('Anima package test ' + [Guid]::NewGuid().ToString('N'))
$publish = Join-Path $work 'publish'
[System.IO.Directory]::CreateDirectory((Join-Path $publish '资源 with spaces')) | Out-Null
$sourceInfo = Get-LauncherSourceInfo $publish
if ($sourceInfo.sourceCommit -ne 'unknown' -or $sourceInfo.sourceKind -ne 'source-archive' -or $null -ne $sourceInfo.sourceDirty) { throw 'Source ZIP incorrectly claims a known Git commit or clean tree.' }
Write-Output 'PASS: a source ZIP without .git has explicit unknown provenance'
$fixtureNames = @('AnimaRandomStudio.exe', 'AnimaRandomStudio.dll', 'Anima.Launcher.Core.dll', 'AnimaRandomStudio.deps.json', 'AnimaRandomStudio.runtimeconfig.json', 'AnimaRandomStudio.pri', 'Microsoft.ui.xaml.dll', 'coreclr.dll', 'README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.txt', 'third-party-packages.json', '资源 with spaces/dependency.dll')
foreach ($name in $fixtureNames) {
    [System.IO.File]::WriteAllText((Join-Path $publish $name), "fixture $name")
}
$info = @{ sourceCommit = ('a' * 40); sourceKind = 'git-checkout'; sourceDirty = $false; version = '0.4.1' } | ConvertTo-Json
[System.IO.File]::WriteAllText((Join-Path $publish 'build-info.json'), $info)
[System.IO.File]::WriteAllText((Join-Path $work 'build-info.json'), $info)
$package = New-LauncherPackage -PublishDirectory $publish -ZipPath (Join-Path $work 'AnimaRandomStudio-win-x64-unsigned.zip')
if ($package.FileCount -ne $fixtureNames.Count + 2) { throw 'Package omitted a file.' }
Write-Output 'PASS: all runtime files and Unicode subdirectories are packaged and hashed'
$rootCheck = [System.IO.Compression.ZipFile]::OpenRead($package.ZipPath)
try {
    if ($null -eq $rootCheck.GetEntry('AnimaRandomStudio.exe') -or $null -ne $rootCheck.GetEntry('AnimaRandomStudio/AnimaRandomStudio.exe')) { throw 'Launcher EXE must be directly at the ZIP root.' }
} finally { $rootCheck.Dispose() }
Write-Output 'PASS: launcher EXE is directly at the ZIP root'
[System.IO.File]::WriteAllText((Join-Path $work 'SHA256SUMS.txt'), "$($package.Sha256)  AnimaRandomStudio-win-x64-unsigned.zip`n")
& (Join-Path $PSScriptRoot 'Test-LauncherCandidate.ps1') -CandidateDirectory $work -ExpectedCommit ('a' * 40) -ExpectedVersion '0.4.1' -RequireClean
$failed = $false
try { & (Join-Path $PSScriptRoot 'Test-LauncherCandidate.ps1') -CandidateDirectory $work -ExpectedVersion '9.9.9' } catch { $failed = $true }
if (-not $failed) { throw 'A wrong release tag version was not detected.' }
Write-Output 'PASS: release verification rejects a tag/version mismatch'
[System.IO.File]::WriteAllText((Join-Path $publish 'new-runtime.dll'), 'later publish output')
$failed = $false
try { Test-LauncherPackageIntegrity -ZipPath $package.ZipPath -PublishDirectory $publish | Out-Null } catch { $failed = $true }
if (-not $failed) { throw 'A missing runtime file was not detected.' }
Write-Output 'PASS: verification rejects omitted publish files'
$stream = [System.IO.File]::Open($package.ZipPath, [System.IO.FileMode]::Open)
$zip = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Update)
try {
    $entry = $zip.GetEntry('AnimaRandomStudio.dll')
    $entry.Delete()
    $writer = [System.IO.StreamWriter]::new($zip.CreateEntry('AnimaRandomStudio.dll').Open())
    try { $writer.Write('tampered') } finally { $writer.Dispose() }
} finally { $zip.Dispose(); $stream.Dispose() }
$failed = $false
try { Test-LauncherPackageIntegrity -ZipPath $package.ZipPath | Out-Null } catch { $failed = $true }
if (-not $failed) { throw 'Tampered package bytes were not detected.' }
Write-Output 'PASS: verification rejects tampered archive contents'
Write-Output "Fixtures retained in $work"
