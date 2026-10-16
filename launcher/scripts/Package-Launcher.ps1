# Pure packaging helpers; dot-source from the build and its offline regression test.
Set-StrictMode -Version Latest

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
    param([Parameter(Mandatory)][string]$ZipPath, [string]$PublishDirectory = '')
    Add-Type -AssemblyName System.IO.Compression
    $stream = [System.IO.File]::OpenRead($ZipPath)
    $zip = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Read)
    try {
        $entries = @{}
        foreach ($entry in $zip.Entries) {
            if (-not $entry.FullName.StartsWith('AnimaRandomStudio/', [System.StringComparison]::Ordinal)) { throw "Unexpected archive root: $($entry.FullName)" }
            $relative = $entry.FullName.Substring('AnimaRandomStudio/'.Length)
            if (-not $relative -or $relative -match '[\\:\r\n]' -or @($relative.Split('/') | Where-Object { $_ -in '', '.', '..' }).Count) { throw "Unsafe archive entry: $relative" }
            if ($entries.ContainsKey($relative)) { throw "Duplicate archive entry: $relative" }
            $entries[$relative] = $entry
        }
        if (-not $entries.ContainsKey('FILES.sha256')) { throw 'Package is missing FILES.sha256.' }
        $reader = [System.IO.StreamReader]::new($entries['FILES.sha256'].Open())
        try { $manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $expected = @{}
        foreach ($line in $manifest.Split("`n", [System.StringSplitOptions]::RemoveEmptyEntries)) {
            if ($line -notmatch '^([a-f0-9]{64})  (.+)$') { throw 'Invalid FILES.sha256 entry.' }
            $name = $Matches[2]
            if ($name -eq 'FILES.sha256' -or $expected.ContainsKey($name)) { throw "Invalid checksum path: $name" }
            $expected[$name] = $Matches[1]
        }
        if ($entries.Count -ne $expected.Count + 1) { throw 'Archive file list differs from FILES.sha256.' }
        foreach ($name in $expected.Keys) {
            if (-not $entries.ContainsKey($name)) { throw "Package is missing $name" }
            $entryStream = $entries[$name].Open()
            try { $actual = Get-LauncherStreamHash $entryStream } finally { $entryStream.Dispose() }
            if ($actual -ne $expected[$name]) { throw "Package checksum mismatch: $name" }
        }
        if ($PublishDirectory) {
            $sourceFiles = @(Get-LauncherPackageFiles $PublishDirectory)
            if ($sourceFiles.Count -ne $entries.Count) { throw 'Archive does not contain the complete publish directory.' }
            foreach ($file in $sourceFiles) {
                if (-not $entries.ContainsKey($file.Relative)) { throw "Publish output omitted from archive: $($file.Relative)" }
                $entryStream = $entries[$file.Relative].Open()
                try { $actual = Get-LauncherStreamHash $entryStream } finally { $entryStream.Dispose() }
                if ($actual -ne (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()) { throw "Publish output differs in archive: $($file.Relative)" }
            }
        }
        return $entries.Count
    } finally { $zip.Dispose(); $stream.Dispose() }
}

function New-LauncherPackage {
    param([Parameter(Mandatory)][string]$PublishDirectory, [Parameter(Mandatory)][string]$ZipPath)
    if (Test-Path -LiteralPath $ZipPath) { throw "Candidate archive already exists: $ZipPath" }
    $inventory = Join-Path $PublishDirectory 'FILES.sha256'
    if (Test-Path -LiteralPath $inventory) { throw 'A fresh publish directory is required; FILES.sha256 already exists.' }
    $lines = @(Get-LauncherPackageFiles $PublishDirectory | ForEach-Object {
        '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Relative
    })
    [System.IO.File]::WriteAllText($inventory, ($lines -join "`n") + "`n", [System.Text.UTF8Encoding]::new($false))
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $stream = [System.IO.File]::Open($ZipPath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write)
    $zip = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in @(Get-LauncherPackageFiles $PublishDirectory)) {
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, 'AnimaRandomStudio/' + $file.Relative, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $zip.Dispose(); $stream.Dispose() }
    $count = Test-LauncherPackageIntegrity -ZipPath $ZipPath -PublishDirectory $PublishDirectory
    return [pscustomobject]@{ ZipPath = $ZipPath; Sha256 = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant(); FileCount = $count }
}
