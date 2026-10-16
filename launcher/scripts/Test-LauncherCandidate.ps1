param(
    [Parameter(Mandatory)][string]$CandidateDirectory,
    [string]$ExpectedCommit = '',
    [string]$ExpectedVersion = '',
    [switch]$RequireClean
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Package-Launcher.ps1')
$candidate = [System.IO.Path]::GetFullPath($CandidateDirectory)
$lines = [System.IO.File]::ReadAllLines((Join-Path $candidate 'SHA256SUMS.txt'))
if ($lines.Count -ne 1 -or $lines[0] -notmatch '^([a-f0-9]{64})  (AnimaRandomStudio-win-x64-(?:un)?signed\.zip)$') { throw 'Invalid candidate SHA256SUMS.txt.' }
$expectedHash = $Matches[1]
$archiveName = $Matches[2]
$archive = Join-Path $candidate $archiveName
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedHash) { throw 'Candidate ZIP hash does not match SHA256SUMS.txt.' }
$count = Test-LauncherPackageIntegrity -ZipPath $archive
$info = Get-Content -LiteralPath (Join-Path $candidate 'build-info.json') -Raw | ConvertFrom-Json
if ($ExpectedCommit -and $info.sourceCommit -ne $ExpectedCommit) { throw 'Candidate commit does not match the release tag.' }
if ($ExpectedVersion -and $info.version -ne $ExpectedVersion) { throw 'Candidate version does not match the release tag.' }
if ($RequireClean -and ($info.sourceKind -ne 'git-checkout' -or $info.sourceDirty -ne $false)) { throw 'Release requires a known, clean Git checkout.' }
$zip = [System.IO.Compression.ZipFile]::OpenRead($archive)
try {
    foreach ($required in @('AnimaRandomStudio.exe', 'AnimaRandomStudio.dll', 'Anima.Launcher.Core.dll', 'AnimaRandomStudio.deps.json', 'AnimaRandomStudio.runtimeconfig.json', 'AnimaRandomStudio.pri', 'Microsoft.UI.Xaml.dll', 'coreclr.dll', 'README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.txt', 'third-party-packages.json')) {
        # Windows package filenames are case-insensitive; the SDK publishes Microsoft.ui.xaml.dll.
        if ($zip.Entries.FullName -notcontains $required) { throw "Candidate ZIP is missing required root file: $required" }
    }
    foreach ($file in $zip.Entries) {
        if ($file.FullName -match '(^|/)(data|output|\.git|\.codex-local|\.claude|__pycache__)/' -or ($file.FullName -like '*.md' -and $file.FullName -ne 'README.md')) { throw "Unexpected local file or internal note in candidate: $($file.FullName)" }
    }
    $entry = $zip.GetEntry('build-info.json')
    if ($null -eq $entry) { throw 'Candidate ZIP is missing build-info.json.' }
    $reader = [System.IO.StreamReader]::new($entry.Open())
    try { $inside = $reader.ReadToEnd() } finally { $reader.Dispose() }
    if ($inside -ne [System.IO.File]::ReadAllText((Join-Path $candidate 'build-info.json'))) { throw 'External and packaged build-info.json differ.' }
} finally { $zip.Dispose() }
Write-Output "PASS: candidate version $($info.version), $count files, ZIP checksum and build provenance verified."
