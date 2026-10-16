param(
    [string]$Configuration = 'Release',
    [string]$CertificateThumbprint = '',
    [switch]$InstallShortcut,
    [string]$CandidateDirectory = ''
)
$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
. (Join-Path $repo 'launcher\scripts\Package-Launcher.ps1')
$sdk = Join-Path $env:LOCALAPPDATA 'Codex\dotnet-sdk-8\dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = (Get-Command dotnet -ErrorAction Stop).Source }
$head = (& git -C $repo rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify the source commit.' }
$changes = @(& git -C $repo status --porcelain=v1 --untracked-files=normal)
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect the source working tree.' }
if (-not $CandidateDirectory) { $CandidateDirectory = Join-Path $repo ('dist\candidates\' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)) }
$candidate = [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetFullPath($CandidateDirectory, (Get-Location).Path))
if (Test-Path -LiteralPath $candidate) { throw "Use a new candidate directory; existing output is preserved: $candidate" }
[System.IO.Directory]::CreateDirectory($candidate) | Out-Null
$staging = Join-Path $candidate '.publish-staging'
$output = Join-Path $candidate 'AnimaRandomStudio'
[System.IO.Directory]::CreateDirectory($staging) | Out-Null
$payload = Join-Path $repo 'launcher\payload.zip'
Add-Type -AssemblyName System.IO.Compression
$stream = [System.IO.File]::Create($payload)
$archive = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    $files = @('run.py', 'LICENSE', 'pyproject.toml')
    foreach ($folder in @('anima_webui', 'static', 'templates')) {
        $files += Get-ChildItem -LiteralPath (Join-Path $repo $folder) -File -Recurse |
            Where-Object { $_.FullName -notmatch '__pycache__|\.pyc$' } |
            ForEach-Object { $_.FullName.Substring($repo.Length + 1) } # Path.GetRelativePath 需要 .NET Core，PS 5.1 没有
    }
    foreach ($file in $files | Sort-Object) {
        $entry = $archive.CreateEntry($file.Replace('\', '/'), [System.IO.Compression.CompressionLevel]::Optimal)
        $target = $entry.Open()
        $source = [System.IO.File]::OpenRead((Join-Path $repo $file))
        try { $source.CopyTo($target) } finally { $source.Dispose(); $target.Dispose() }
    }
} finally { $archive.Dispose(); $stream.Dispose() }
$payloadHash = (Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash.ToLowerInvariant()
& $sdk publish (Join-Path $repo 'launcher\Anima.Launcher.WinUI\Anima.Launcher.WinUI.csproj') -c $Configuration -r win-x64 -o $staging --nologo
if ($LASTEXITCODE -ne 0) { throw 'Launcher publish failed; the incomplete candidate directory is retained for diagnosis.' }
foreach ($required in @('AnimaRandomStudio.exe', 'AnimaRandomStudio.dll', 'Anima.Launcher.Core.dll', 'AnimaRandomStudio.deps.json', 'AnimaRandomStudio.runtimeconfig.json', 'AnimaRandomStudio.pri', 'Microsoft.UI.Xaml.dll', 'coreclr.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $staging $required) -PathType Leaf)) { throw "Incomplete self-contained publish output: $required" }
}
$exe = Join-Path $staging 'AnimaRandomStudio.exe'
if ($CertificateThumbprint) {
    $certificate = Get-Item "Cert:\CurrentUser\My\$CertificateThumbprint"
    $signature = Set-AuthenticodeSignature -FilePath $exe -Certificate $certificate -TimestampServer 'http://timestamp.digicert.com'
    if ($signature.Status -ne 'Valid') { throw "Signing failed: $($signature.StatusMessage)" }
}
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination (Join-Path $staging 'LICENSE')
Copy-Item -LiteralPath (Join-Path $repo 'launcher\README.md') -Destination (Join-Path $staging 'README.md')
$buildInfo = [ordered]@{
    sourceCommit = $head
    sourceDirty = $changes.Count -gt 0
    sourceChanges = $changes
    payloadSha256 = $payloadHash
    builtAtUtc = [DateTime]::UtcNow.ToString('o')
    configuration = $Configuration
    runtimeIdentifier = 'win-x64'
    signed = [bool]$CertificateThumbprint
}
[System.IO.File]::WriteAllText((Join-Path $staging 'build-info.json'), ($buildInfo | ConvertTo-Json -Depth 5) + "`n", [System.Text.UTF8Encoding]::new($false))
# Move only the two fixed child paths of this newly created candidate. Existing
# dist/launcher and installed launchers are never replaced by a candidate build.
if ([System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($staging)) -ne $candidate -or [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($output)) -ne $candidate) { throw 'Candidate staging path escaped its root.' }
Move-Item -LiteralPath $staging -Destination $output
$suffix = if ($CertificateThumbprint) { 'signed' } else { 'unsigned' }
$zipPath = Join-Path $candidate "AnimaRandomStudio-win-x64-$suffix.zip"
$package = New-LauncherPackage -PublishDirectory $output -ZipPath $zipPath
[System.IO.File]::WriteAllText((Join-Path $candidate 'SHA256SUMS.txt'), "$($package.Sha256)  $([System.IO.Path]::GetFileName($zipPath))`n", [System.Text.UTF8Encoding]::new($false))
$exe = Join-Path $output 'AnimaRandomStudio.exe'
$result = [ordered]@{ candidateDirectory = $candidate; launcher = $exe; archive = $zipPath; sha256 = $package.Sha256; fileCount = $package.FileCount; sourceCommit = $head; sourceDirty = $buildInfo.sourceDirty; payloadSha256 = $payloadHash }
[System.IO.File]::WriteAllText((Join-Path $candidate 'candidate.json'), ($result | ConvertTo-Json) + "`n", [System.Text.UTF8Encoding]::new($false))
if ($InstallShortcut) {
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Desktop')) 'Anima Random Studio.lnk'))
    $link.TargetPath = $exe
    $link.WorkingDirectory = $output
    $link.IconLocation = "$exe,0"
    $link.Save()
}
$result | ConvertTo-Json
