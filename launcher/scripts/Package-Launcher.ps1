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

function Assert-LauncherSlimBundle($Files) {
    foreach ($name in @('DirectML.dll', 'onnxruntime.dll', 'onnxruntime_providers_shared.dll')) {
        if (@($Files | Where-Object { [System.IO.Path]::GetFileName($_.path) -ieq $name }).Count -gt 0) {
            throw "Unused Windows ML runtime must not be bundled: $name"
        }
    }
}

function Write-LauncherPackageSizeReport {
    param([Parameter(Mandatory)][string]$CandidateDirectory, [Parameter(Mandatory)]$Package)
    Assert-LauncherSlimBundle $Package.files
    $assets = foreach ($name in @('AnimaRandomStudio.exe', 'AnimaRandomStudio-portable-win-x64.zip', 'AnimaRandomStudio-Setup-win-x64.exe')) {
        $path = Join-Path $CandidateDirectory $name
        [ordered]@{ path = $name; bytes = (Get-Item -LiteralPath $path).Length; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    $files = @($Package.files)
    $inventoryBytes = (Get-Item -LiteralPath (Join-Path $CandidateDirectory 'FILES.sha256')).Length
    $result = [ordered]@{
        schemaVersion = 1
        version = $Package.version
        sourceCommit = $Package.buildInfo.sourceCommit
        verifiedAtUtc = [DateTime]::UtcNow.ToString('o')
        assets = @($assets)
        embeddedFileCount = $files.Count + 1
        extractedBytes = [long]($files | Measure-Object -Property size -Sum).Sum + $inventoryBytes
        excludedNativeFiles = @('DirectML.dll', 'onnxruntime.dll', 'onnxruntime_providers_shared.dll')
        largestEmbeddedFiles = @($files | Sort-Object -Property size -Descending | Select-Object -First 20 path, size)
        inventory = 'FILES.sha256'
        verification = 'package-info.json'
    }
    [System.IO.File]::WriteAllText((Join-Path $CandidateDirectory 'package-size-report.json'), ($result | ConvertTo-Json -Depth 8) + "`n", [System.Text.UTF8Encoding]::new($false))
}

function Remove-LauncherCandidateTemporaryDirectory {
    param([Parameter(Mandatory)][string]$CandidateDirectory, [Parameter(Mandatory)][string]$Path)
    $root = [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetFullPath($CandidateDirectory))
    $target = [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetFullPath($Path))
    $parent = [System.IO.Path]::GetDirectoryName($target)
    if (-not $parent.Equals($root, [System.StringComparison]::OrdinalIgnoreCase) -or
        [System.IO.Path]::GetFileName($target) -cnotmatch '^\.(publish-staging|bundle-content|verification-[a-f0-9]{32})$') {
        throw "Refusing to clean a path outside this candidate's known temporary directories: $target"
    }
    # Check every ancestor and every descendant before deletion. Enumerate one directory at
    # a time so a junction is rejected before traversal, including links inside hidden folders.
    for ($ancestor = $target; $ancestor; $ancestor = [System.IO.Path]::GetDirectoryName($ancestor)) {
        if (Test-Path -LiteralPath $ancestor) {
            if ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
                throw "Refusing to clean through a filesystem link: $ancestor"
            }
        }
    }
    if (-not (Test-Path -LiteralPath $target -PathType Container)) { return }
    $pending = [System.Collections.Generic.Queue[string]]::new()
    $pending.Enqueue($target)
    while ($pending.Count -gt 0) {
        foreach ($item in Get-ChildItem -LiteralPath $pending.Dequeue() -Force) {
            if ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) { throw "Refusing to clean a filesystem link: $($item.FullName)" }
            if ($item.PSIsContainer) { $pending.Enqueue($item.FullName) }
        }
    }
    Remove-Item -LiteralPath $target -Recurse -Force
}
