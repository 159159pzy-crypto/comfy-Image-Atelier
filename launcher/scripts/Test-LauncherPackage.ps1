$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Package-Launcher.ps1')
$work = Join-Path ([System.IO.Path]::GetTempPath()) ('Anima package test ' + [Guid]::NewGuid().ToString('N'))
$publish = Join-Path $work 'publish'
[System.IO.Directory]::CreateDirectory((Join-Path $publish '资源 with spaces')) | Out-Null
foreach ($name in @('AnimaRandomStudio.exe', 'AnimaRandomStudio.dll', 'AnimaRandomStudio.pri', 'AnimaRandomStudio.runtimeconfig.json', '资源 with spaces/dependency.dll')) {
    [System.IO.File]::WriteAllText((Join-Path $publish $name), "fixture $name")
}
$package = New-LauncherPackage -PublishDirectory $publish -ZipPath (Join-Path $work 'candidate.zip')
if ($package.FileCount -ne 6) { throw 'Package omitted a file.' }
Write-Output 'PASS: all runtime files and Unicode subdirectories are packaged and hashed'
[System.IO.File]::WriteAllText((Join-Path $publish 'new-runtime.dll'), 'later publish output')
$failed = $false
try { Test-LauncherPackageIntegrity -ZipPath $package.ZipPath -PublishDirectory $publish | Out-Null } catch { $failed = $true }
if (-not $failed) { throw 'A missing runtime file was not detected.' }
Write-Output 'PASS: verification rejects omitted publish files'
$stream = [System.IO.File]::Open($package.ZipPath, [System.IO.FileMode]::Open)
$zip = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Update)
try {
    $entry = $zip.GetEntry('AnimaRandomStudio/AnimaRandomStudio.dll')
    $entry.Delete()
    $writer = [System.IO.StreamWriter]::new($zip.CreateEntry('AnimaRandomStudio/AnimaRandomStudio.dll').Open())
    try { $writer.Write('tampered') } finally { $writer.Dispose() }
} finally { $zip.Dispose(); $stream.Dispose() }
$failed = $false
try { Test-LauncherPackageIntegrity -ZipPath $package.ZipPath | Out-Null } catch { $failed = $true }
if (-not $failed) { throw 'Tampered package bytes were not detected.' }
Write-Output 'PASS: verification rejects tampered archive contents'
Write-Output "Fixtures retained in $work"
