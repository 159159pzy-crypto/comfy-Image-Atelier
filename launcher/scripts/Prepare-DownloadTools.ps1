param(
    [string]$CacheDirectory = '',
    [string]$BundleContentDirectory = ''
)
$ErrorActionPreference = 'Stop'
$repo = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (-not $CacheDirectory) { $CacheDirectory = Join-Path $repo 'tools\download-tools' }
$cache = [System.IO.Path]::GetFullPath($CacheDirectory)
[System.IO.Directory]::CreateDirectory($cache) | Out-Null
$manifestPath = Join-Path $repo 'launcher\manifests\download-tools.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or @($manifest.tools).Count -ne 2) { throw 'Unsupported download-tools manifest.' }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-VerifiedDownload($Spec, [string]$Destination) {
    if ($Spec.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or [long]$Spec.size -le 0 -or $Spec.url -notmatch '^https://') { throw 'Invalid pinned tool artifact.' }
    if ((Test-Path -LiteralPath $Destination -PathType Leaf) -and
        (Get-Item -LiteralPath $Destination).Length -eq [long]$Spec.size -and
        (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash -eq $Spec.sha256) { return $Destination }
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($Destination)) | Out-Null
    $sources = @($Spec.url) + @(Get-OptionalManifestItems $Spec 'fallbackUrls')
    foreach ($source in $sources) {
        if ($source -notmatch '^https://') { throw 'Invalid pinned tool source URL.' }
    }
    foreach ($source in $sources) {
        for ($attempt = 1; $attempt -le 3; $attempt++) {
            $temporary = $Destination + '.download-' + [Guid]::NewGuid().ToString('N')
            try {
                # Keep diagnostics out of the success stream: callers receive
                # only the verified archive path, never a list of log lines.
                Write-Host "Downloading $([System.IO.Path]::GetFileName($Destination)) (attempt $attempt/3): $source"
                Invoke-WebRequest -Uri $source -OutFile $temporary -TimeoutSec 180 -MaximumRedirection 10 -Headers @{ 'User-Agent' = 'AnimaRandomStudio-Build' }
                if ((Get-Item -LiteralPath $temporary).Length -ne [long]$Spec.size -or
                    (Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $Spec.sha256) { throw "Integrity verification failed: $([System.IO.Path]::GetFileName($Destination))" }
                if (Test-Path -LiteralPath $Destination) { Move-Item -LiteralPath $Destination -Destination ($Destination + '.invalid-' + [Guid]::NewGuid().ToString('N')) }
                Move-Item -LiteralPath $temporary -Destination $Destination
                return $Destination
            } catch {
                $lastFailure = $_.Exception.Message
                Write-Warning "Download failed from ${source}: $lastFailure"
            } finally {
                if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
            }
            if ($attempt -lt 3) { Start-Sleep -Seconds 2 }
        }
    }
    throw "All pinned sources failed for $([System.IO.Path]::GetFileName($Destination)): $lastFailure"
}

function Get-OptionalManifestItems($Object, [string]$Name) {
    $property = $Object.PSObject.Properties[$Name]
    if ($null -ne $property) { $property.Value }
}

$supplements = [System.Collections.Generic.List[object]]::new()
foreach ($tool in $manifest.tools) {
    $archiveName = $tool.id + '-' + $tool.sha256.ToLowerInvariant() + '.zip'
    $archive = Get-VerifiedDownload $tool (Join-Path $cache $archiveName)
    $zip = [System.IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $entry = $zip.GetEntry($tool.executable)
        if (-not $entry) { throw "Missing executable in $archiveName" }
        $input = $entry.Open()
        try { $digest = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($input)) } finally { $input.Dispose() }
        if ($digest -ne $tool.executableSha256) { throw "Executable integrity verification failed: $($tool.id)" }
    } finally { $zip.Dispose() }
    foreach ($item in @($tool.sourceArchive) + @(Get-OptionalManifestItems $tool 'sourceDependencies') + @(Get-OptionalManifestItems $tool 'licenseFiles')) {
        if (-not $item) { continue }
        if ([System.IO.Path]::GetFileName($item.fileName) -ne $item.fileName) { throw 'Unsafe supplement filename.' }
        $destination = Join-Path $cache ('source\' + $item.fileName)
        # Existing verified license files from a developer's cache can also be adopted.
        $legacy = Join-Path $cache $item.fileName
        if (-not (Test-Path -LiteralPath $destination) -and (Test-Path -LiteralPath $legacy) -and
            (Get-FileHash -LiteralPath $legacy -Algorithm SHA256).Hash -eq $item.sha256) {
            [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($destination)) | Out-Null
            Copy-Item -LiteralPath $legacy -Destination $destination
        }
        $verified = Get-VerifiedDownload $item $destination
        $supplements.Add([pscustomobject]@{ Path = $verified; Name = ($tool.id + '/' + $item.fileName) })
    }
    Write-Output "Verified $($tool.id) $($tool.version): archive and executable SHA-256"
}

if ($BundleContentDirectory) {
    $bundle = [System.IO.Path]::GetFullPath($BundleContentDirectory)
    [System.IO.Directory]::CreateDirectory($bundle) | Out-Null
    $sourcesZip = Join-Path $bundle 'DOWNLOAD-TOOLS-SOURCES.zip'
    $stream = [System.IO.File]::Open($sourcesZip, [System.IO.FileMode]::Create)
    $package = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create)
    function Add-SourceEntry([string]$Name, [System.IO.Stream]$Source) {
        $entry = $package.CreateEntry($Name, [System.IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
        $output = $entry.Open()
        try { $Source.CopyTo($output) } finally { $output.Dispose() }
    }
    try {
        $input = [System.IO.File]::OpenRead($manifestPath)
        try { Add-SourceEntry 'download-tools.json' $input } finally { $input.Dispose() }
        foreach ($item in $supplements) {
            $input = [System.IO.File]::OpenRead($item.Path)
            try { Add-SourceEntry $item.Name $input } finally { $input.Dispose() }
        }
        $aria = $manifest.tools | Where-Object id -eq 'aria2'
        $zip = [System.IO.Compression.ZipFile]::OpenRead((Join-Path $cache ($aria.id + '-' + $aria.sha256 + '.zip')))
        try {
            foreach ($name in @('COPYING', 'LICENSE.OpenSSL', 'README.mingw', 'AUTHORS', 'NEWS')) {
                $entry = $zip.Entries | Where-Object { $_.Name -eq $name } | Select-Object -First 1
                if (-not $entry) { throw "Missing aria2 notice: $name" }
                $input = $entry.Open()
                try { Add-SourceEntry ('aria2/notices/' + $name) $input } finally { $input.Dispose() }
            }
        } finally { $zip.Dispose() }
    } finally { $package.Dispose(); $stream.Dispose() }
    $notice = @'
Comfy Image Atelier download tools

uv 0.12.16: MIT OR Apache-2.0. The original license texts and pinned source archive are included in DOWNLOAD-TOOLS-SOURCES.zip.
aria2 1.37.0 Windows x64 build1: GPL-2.0-or-later. The upstream executable is unchanged. COPYING, LICENSE.OpenSSL, AUTHORS, README.mingw and NEWS are included.

The upstream Windows README identifies statically linked GMP 6.3.0, Expat 2.5.0, SQLite 3.43.1, zlib 1.3, c-ares 1.19.1 and libssh2 1.11.0. Their exact-version upstream source archives, including their licenses, are included alongside the aria2 source archive. aria2 source contains Dockerfile.mingw, mingw-config, mingw-release and mingw-build-memo with the upstream build instructions. Some dependency archives use xz instead of bz2 compression; their source versions are unchanged.

The source bundle contains download-tools.json with original HTTPS source URLs, exact sizes, archive SHA-256 and executable SHA-256. No executable or source is taken from a third-party launcher installation. The application invokes aria2 as a separate process over a local authenticated bridge.
'@
    [System.IO.File]::WriteAllText((Join-Path $bundle 'DOWNLOAD-TOOLS-NOTICES.txt'), $notice + "`n", [System.Text.UTF8Encoding]::new($false))
    Write-Output "Prepared download tool notices and corresponding source archives: $sourcesZip"
}
