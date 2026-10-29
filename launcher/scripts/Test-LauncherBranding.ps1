param([string]$TestRoot = '')
$ErrorActionPreference = 'Stop'
$repo = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (-not $TestRoot) { $TestRoot = Join-Path $repo ('output\launcher-branding-tests\' + [Guid]::NewGuid().ToString('N')) }
$root = [System.IO.Path]::GetFullPath($TestRoot)
if (Test-Path -LiteralPath $root) { throw 'Branding tests require a new isolated directory.' }
[System.IO.Directory]::CreateDirectory($root) | Out-Null
$executable = Join-Path $root 'AnimaRandomStudio.exe'
$foreign = Join-Path $root 'other.exe'
[System.IO.File]::WriteAllText($executable, 'shortcut fixture only')
[System.IO.File]::WriteAllText($foreign, 'unrelated shortcut fixture')
$shell = New-Object -ComObject WScript.Shell
foreach ($case in @('owned', 'foreign', 'custom-arguments', 'new-name-conflict')) {
    $directory = Join-Path $root $case
    [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    $legacyPath = Join-Path $directory 'Anima Random Studio.lnk'
    $newPath = Join-Path $directory 'Comfy Image Atelier.lnk'
    $legacy = $shell.CreateShortcut($legacyPath)
    $legacy.TargetPath = if ($case -eq 'foreign') { $foreign } else { $executable }
    if ($case -eq 'custom-arguments') { $legacy.Arguments = '--custom-user-option' }
    $legacy.Save()
    if ($case -eq 'new-name-conflict') {
        $conflict = $shell.CreateShortcut($newPath)
        $conflict.TargetPath = $foreign
        $conflict.Save()
    }
    $failed = $false
    try { & (Join-Path $PSScriptRoot 'Install-LauncherShortcut.ps1') -ExecutablePath $executable -ShortcutDirectory $directory }
    catch { if ($case -ne 'new-name-conflict') { throw }; $failed = $true }
    if ($case -eq 'new-name-conflict') {
        if (-not $failed -or $shell.CreateShortcut($newPath).TargetPath -ne $foreign) { throw 'Unrelated new-name shortcut was changed.' }
    } else {
        if ($shell.CreateShortcut($newPath).TargetPath -ne $executable) { throw 'New branding shortcut does not point to the requested executable.' }
    }
    if ((Test-Path -LiteralPath $legacyPath) -ne ($case -ne 'owned')) { throw "Unexpected legacy shortcut handling: $case" }
    Write-Output "PASS: shortcut migration $case"
}
Write-Output "Branding shortcut acceptance retained in $root"
