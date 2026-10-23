# Pure packaging helpers; dot-source from the build and its offline regression test.
Set-StrictMode -Version Latest

function Get-LauncherSourceInfo([string]$Repository) {
    $result = [ordered]@{ sourceCommit = 'unknown'; sourceKind = 'source-archive'; sourceDirty = $null; sourceChanges = @() }
    # Test this directory itself, so a source ZIP unpacked inside another Git repo
    # cannot accidentally claim the containing repository's commit.
    if (Test-Path -LiteralPath (Join-Path $Repository '.git')) {
        $git = Get-Command git -ErrorAction SilentlyContinue
        if ($git) {
            $head = (& $git.Source -C $Repository rev-parse HEAD).Trim()
            if ($LASTEXITCODE -ne 0) { throw 'Cannot identify the source commit.' }
            $changes = @(& $git.Source -C $Repository status --porcelain=v1 --untracked-files=normal)
            if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect the source working tree.' }
            $result.sourceCommit = $head
            $result.sourceKind = 'git-checkout'
            $result.sourceDirty = $changes.Count -gt 0
            $result.sourceChanges = $changes
        }
    }
    return [pscustomobject]$result
}

function Get-LauncherPackageFiles([string]$Directory) {
    $root = [System.IO.Path]::GetFullPath($Directory).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    foreach ($folder in @(Get-ChildItem -LiteralPath $root -Directory -Recurse -Force)) {
        if ($folder.Attributes -band [System.IO.FileAttributes]::ReparsePoint) { throw "Packaging does not follow directory links: $($folder.FullName)" }
    }
    $files = @(Get-ChildItem -LiteralPath $root -File -Recurse -Force | Sort-Object FullName)
    foreach ($file in $files) {
        if ($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) { throw "Packaging does not follow file links: $($file.FullName)" }
        $relative = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
        if ($relative -match '[\r\n]' -or $relative.Split('/') -contains '..') { throw "Invalid package path: $relative" }
        [pscustomobject]@{ FullName = $file.FullName; Relative = $relative }
    }
}

function Get-LauncherStreamHash([System.IO.Stream]$Stream) {
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try { return ([System.BitConverter]::ToString($algorithm.ComputeHash($Stream))).Replace('-', '').ToLowerInvariant() }
    finally { $algorithm.Dispose() }
}

function Test-LauncherPackageIntegrity {
    param([Parameter(Mandatory)][string]$ZipPath, [Parameter(Mandatory)][string]$ExecutablePath)
    Add-Type -AssemblyName System.IO.Compression
    $stream = [System.IO.File]::OpenRead($ZipPath)
    $zip = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Read)
    try {
        if ($zip.Entries.Count -ne 1 -or $zip.Entries[0].FullName -cne 'AnimaRandomStudio.exe') { throw 'Portable ZIP must contain only the root AnimaRandomStudio.exe.' }
        $entry = $zip.Entries[0]
        if ((($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) { throw 'Portable EXE cannot be a symbolic link.' }
        $entryStream = $entry.Open()
        try { $actual = Get-LauncherStreamHash $entryStream } finally { $entryStream.Dispose() }
        if ($actual -ne (Get-FileHash -LiteralPath $ExecutablePath -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'Portable ZIP EXE differs from the standalone EXE.' }
        return 1
    } finally { $zip.Dispose(); $stream.Dispose() }
}

function New-LauncherPackage {
    param([Parameter(Mandatory)][string]$ExecutablePath, [Parameter(Mandatory)][string]$ZipPath)
    if (Test-Path -LiteralPath $ZipPath) { throw "Candidate archive already exists: $ZipPath" }
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $stream = [System.IO.File]::Open($ZipPath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write)
    $zip = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create)
    try { [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $ExecutablePath, 'AnimaRandomStudio.exe', [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null }
    finally { $zip.Dispose(); $stream.Dispose() }
    $count = Test-LauncherPackageIntegrity -ZipPath $ZipPath -ExecutablePath $ExecutablePath
    return [pscustomobject]@{ ZipPath = $ZipPath; Sha256 = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant(); FileCount = $count }
}
