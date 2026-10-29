param(
    [Parameter(Mandatory)][string]$ExecutablePath,
    [string]$ShortcutDirectory = [Environment]::GetFolderPath('Desktop'),
    [string[]]$KnownEntryPaths = @()
)
$ErrorActionPreference = 'Stop'
$target = [System.IO.Path]::GetFullPath($ExecutablePath)
if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { throw 'Launcher shortcut requires an existing executable.' }
$directory = [System.IO.Path]::GetFullPath($ShortcutDirectory)
if (-not (Test-Path -LiteralPath $directory -PathType Container)) { throw 'Shortcut directory does not exist.' }
$known = @($KnownEntryPaths) + $target
function Test-OwnedTarget([string]$Path) {
    if (-not $Path -or -not [System.IO.Path]::IsPathFullyQualified($Path)) { return $false }
    try {
        $resolved = [System.IO.Path]::GetFullPath($Path)
        foreach ($entry in $known) {
            if ($entry -and [System.IO.Path]::IsPathFullyQualified($entry) -and $resolved.Equals([System.IO.Path]::GetFullPath($entry), [StringComparison]::OrdinalIgnoreCase)) { return $true }
        }
    } catch { return $false }
    return $false
}
$shell = New-Object -ComObject WScript.Shell
$shortcut = Join-Path $directory 'Comfy Image Atelier.lnk'
$link = $shell.CreateShortcut($shortcut)
if ((Test-Path -LiteralPath $shortcut) -and -not (Test-OwnedTarget $link.TargetPath)) { throw 'The new shortcut name is already used by another program.' }
$link.TargetPath = $target
$link.WorkingDirectory = Split-Path $target -Parent
$link.IconLocation = "$target,0"
$link.Save()
$legacyPath = Join-Path $directory 'Anima Random Studio.lnk'
if (Test-Path -LiteralPath $legacyPath) {
    $legacy = $shell.CreateShortcut($legacyPath)
    if ((Test-OwnedTarget $legacy.TargetPath) -and [string]::IsNullOrWhiteSpace($legacy.Arguments)) {
        Remove-Item -LiteralPath $legacyPath
    }
}
