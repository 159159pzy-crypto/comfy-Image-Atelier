param(
    [string]$Configuration = 'Release',
    [string]$CertificateThumbprint = '',
    [switch]$InstallShortcut,
    [switch]$InstallRootEntry,
    [switch]$RequireClean,
    [string]$CandidateDirectory = ''
)
$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
. (Join-Path $repo 'launcher\scripts\Package-Launcher.ps1')
$sdk = (Get-Command dotnet -ErrorAction Stop).Source
$version = [System.IO.File]::ReadAllText((Join-Path $repo 'launcher\version.txt')).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'launcher/version.txt must contain a semantic release version.' }
$sourceInfo = Get-LauncherSourceInfo $repo
$head = $sourceInfo.sourceCommit
$changes = $sourceInfo.sourceChanges
$sourceDirty = $sourceInfo.sourceDirty
$sourceKind = $sourceInfo.sourceKind
if ($RequireClean -and ($sourceKind -ne 'git-checkout' -or $sourceDirty)) { throw 'Release builds require a clean Git checkout.' }
if (-not $CandidateDirectory) { $CandidateDirectory = Join-Path $repo ('dist\candidates\' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)) }
$candidate = [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetFullPath($CandidateDirectory, (Get-Location).Path))
if (Test-Path -LiteralPath $candidate) { throw "Use a new candidate directory; existing output is preserved: $candidate" }
[System.IO.Directory]::CreateDirectory($candidate) | Out-Null
$staging = Join-Path $candidate '.publish-staging'
$output = $candidate
$content = Join-Path $candidate '.bundle-content'
[System.IO.Directory]::CreateDirectory($content) | Out-Null
[System.IO.Directory]::CreateDirectory($staging) | Out-Null
$profileExport = Join-Path $candidate 'model-profiles.json'
$python = (Get-Command python -ErrorAction Stop).Source
& $python (Join-Path $repo 'launcher\scripts\export_model_profiles.py') --output $profileExport
if ($LASTEXITCODE -ne 0) { throw 'Cannot export model profiles from the WebUI contract.' }
$payload = Join-Path $repo 'launcher\payload.zip'
Add-Type -AssemblyName System.IO.Compression
$stream = [System.IO.File]::Create($payload)
$archive = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    $files = @('run.py', 'LICENSE', 'README.md', 'pyproject.toml', 'launcher/version.txt',
        'anima_webui/_vendor/sd_prompt_reader/LICENSE', 'anima_webui/_vendor/sd_prompt_reader/UPSTREAM.txt')
    if ($sourceKind -eq 'git-checkout') {
        $files += @(& git -C $repo -c core.quotePath=false ls-files --cached --others --exclude-standard -- anima_webui static templates) |
            Where-Object { $_ -match '\.(py|json|js|css|html)$' }
        if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate application files.' }
    } else {
        foreach ($folder in @('anima_webui', 'static', 'templates')) {
            $files += Get-ChildItem -LiteralPath (Join-Path $repo $folder) -File -Recurse |
                Where-Object { $_.Extension -in '.py', '.json', '.js', '.css', '.html' -and $_.FullName -notmatch '__pycache__' } |
                ForEach-Object { $_.FullName.Substring($repo.Length + 1) }
        }
    }
    foreach ($file in $files | Sort-Object -Unique) {
        $entry = $archive.CreateEntry($file.Replace('\', '/'), [System.IO.Compression.CompressionLevel]::Optimal)
        $target = $entry.Open()
        $source = [System.IO.File]::OpenRead((Join-Path $repo $file))
        try { $source.CopyTo($target) } finally { $source.Dispose(); $target.Dispose() }
    }
} finally { $archive.Dispose(); $stream.Dispose() }
$payloadHash = (Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash.ToLowerInvariant()
$buildInfo = [ordered]@{
    sourceCommit = $head
    sourceKind = $sourceKind
    sourceDirty = $sourceDirty
    sourceChanges = $changes
    version = $version
    packageFormat = 'single-exe-v1'
    payloadSha256 = $payloadHash
    builtAtUtc = [DateTime]::UtcNow.ToString('o')
    configuration = $Configuration
    runtimeIdentifier = 'win-x64'
    signed = [bool]$CertificateThumbprint
}
[System.IO.File]::WriteAllText((Join-Path $content 'build-info.json'), ($buildInfo | ConvertTo-Json -Depth 5) + "`n", [System.Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath (Join-Path $content 'build-info.json') -Destination (Join-Path $candidate 'build-info.json')
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination (Join-Path $content 'LICENSE')
Copy-Item -LiteralPath (Join-Path $repo 'launcher\README.md') -Destination (Join-Path $content 'README.md')
& (Join-Path $repo 'launcher\scripts\Prepare-DownloadTools.ps1') -BundleContentDirectory $content
if ($LASTEXITCODE -ne 0) { throw 'Cannot verify embedded download tools.' }
& $sdk restore (Join-Path $repo 'launcher\Anima.Launcher.WinUI\Anima.Launcher.WinUI.csproj') -r win-x64 --nologo
if ($LASTEXITCODE -ne 0) { throw 'Launcher restore failed.' }
& $python (Join-Path $repo 'launcher\scripts\export_notices.py') --assets (Join-Path $repo 'launcher\Anima.Launcher.WinUI\obj\project.assets.json') --output $content
if ($LASTEXITCODE -ne 0) { throw 'Cannot collect third-party license notices.' }
& $sdk publish (Join-Path $repo 'launcher\Anima.Launcher.WinUI\Anima.Launcher.WinUI.csproj') -c $Configuration -r win-x64 -o $staging --no-restore --nologo "-p:Version=$version" "-p:ModelProfilesPath=$profileExport" "-p:AnimaBundleContentDir=$content" "-p:AnimaPython=$python" '-p:ContinuousIntegrationBuild=true' '-p:DebugType=None' '-p:DebugSymbols=false' "-p:PathMap=$repo=/_/AnimaRandomStudio"
if ($LASTEXITCODE -ne 0) { throw 'Launcher publish failed; the incomplete candidate is retained for diagnosis.' }
$published = @(Get-LauncherPackageFiles $staging)
if ($published.Count -ne 1 -or $published[0].Relative -ne 'AnimaRandomStudio.exe') { throw 'Single-file publish must produce only AnimaRandomStudio.exe.' }
$exe = Join-Path $candidate 'AnimaRandomStudio.exe'
Copy-Item -LiteralPath $published[0].FullName -Destination $exe
if ($CertificateThumbprint) {
    $certificate = Get-Item "Cert:\CurrentUser\My\$CertificateThumbprint"
    $signature = Set-AuthenticodeSignature -FilePath $exe -Certificate $certificate -TimestampServer 'http://timestamp.digicert.com'
    if ($signature.Status -ne 'Valid') { throw "Signing failed: $($signature.StatusMessage)" }
}
$zipPath = Join-Path $candidate 'AnimaRandomStudio-portable-win-x64.zip'
$package = New-LauncherPackage -ExecutablePath $exe -ZipPath $zipPath
& (Join-Path $repo 'launcher\scripts\Build-LauncherInstaller.ps1') -SourceExe $exe -Version $version -OutputDirectory $candidate
$setup = Join-Path $candidate 'AnimaRandomStudio-Setup-win-x64.exe'
if ($CertificateThumbprint) {
    $signature = Set-AuthenticodeSignature -FilePath $setup -Certificate $certificate -TimestampServer 'http://timestamp.digicert.com'
    if ($signature.Status -ne 'Valid') { throw "Setup signing failed: $($signature.StatusMessage)" }
}
$sumLines = @($exe, $zipPath, $setup) | ForEach-Object { '{0}  {1}' -f (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant(), [System.IO.Path]::GetFileName($_) }
[System.IO.File]::WriteAllText((Join-Path $candidate 'SHA256SUMS.txt'), ($sumLines -join "`n") + "`n", [System.Text.UTF8Encoding]::new($false))
$result = [ordered]@{ candidateDirectory = $candidate; launcher = $exe; archive = $zipPath; setup = $setup; sha256 = $package.Sha256; fileCount = 1; version = $version; sourceCommit = $head; sourceKind = $sourceKind; sourceDirty = $buildInfo.sourceDirty; payloadSha256 = $payloadHash }
[System.IO.File]::WriteAllText((Join-Path $candidate 'candidate.json'), ($result | ConvertTo-Json) + "`n", [System.Text.UTF8Encoding]::new($false))
& (Join-Path $repo 'launcher\scripts\Test-LauncherCandidate.ps1') -CandidateDirectory $candidate -ExpectedVersion $version
if ($InstallRootEntry) {
    & (Join-Path $repo 'launcher\scripts\Install-LauncherEntry.ps1') -CandidateDirectory $candidate -TargetDirectory $repo
}
if ($InstallShortcut) {
    $shortcutExe = if ($InstallRootEntry) { Join-Path $repo 'AnimaRandomStudio.exe' } else { $exe }
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Desktop')) 'Anima Random Studio.lnk'))
    $link.TargetPath = $shortcutExe
    $link.WorkingDirectory = Split-Path $shortcutExe -Parent
    $link.IconLocation = "$shortcutExe,0"
    $link.Save()
}
$result | ConvertTo-Json
