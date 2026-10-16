#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$CandidateDirectory,
    [string]$TargetDirectory = (Join-Path $PSScriptRoot '..\..')
)
$ErrorActionPreference = 'Stop'
$candidate = [System.IO.Path]::GetFullPath($CandidateDirectory)
$target = [System.IO.Path]::GetFullPath($TargetDirectory)
if (-not (Test-Path -LiteralPath $target -PathType Container)) { throw 'The target project directory must already exist.' }
& (Join-Path $PSScriptRoot 'Test-LauncherCandidate.ps1') -CandidateDirectory $candidate
$info = Get-Content -LiteralPath (Join-Path $candidate 'build-info.json') -Raw | ConvertFrom-Json
if ($info.version -notmatch '^\d+\.\d+\.\d+$') { throw 'The candidate version must be a semantic release version.' }
$sum = [System.IO.File]::ReadAllText((Join-Path $candidate 'SHA256SUMS.txt')).Trim()
if ($sum -notmatch '^([a-f0-9]{64})  (AnimaRandomStudio-win-x64-(?:un)?signed\.zip)$') { throw 'Invalid candidate checksum file.' }
$zipHash = $Matches[1]
$archive = Join-Path $candidate $Matches[2]

# The native apphost runs a tiny bridge using the existing self-contained runtime.
# The bridge starts WinUI from its original directory for COM/PRI resource loading.
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$sdkVersion = (& $dotnet --version).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify the .NET SDK.' }
$sdkRoot = Join-Path (Split-Path $dotnet -Parent) "sdk\$sdkVersion"
$hostModel = Join-Path $sdkRoot 'Microsoft.NET.HostModel.dll'
$template = Join-Path $sdkRoot 'AppHostTemplate\apphost.exe'
if (-not (Test-Path -LiteralPath $template)) {
    $hostPacks = Join-Path (Split-Path $dotnet -Parent) 'packs\Microsoft.NETCore.App.Host.win-x64'
    $hostPack = Get-ChildItem -LiteralPath $hostPacks -Directory | Where-Object Name -Match '^8\.0\.\d+$' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    if ($null -eq $hostPack) { throw 'The .NET 8 Windows x64 apphost template is missing.' }
    $template = Join-Path $hostPack.FullName 'runtimes\win-x64\native\apphost.exe'
}
if (-not (Test-Path -LiteralPath $hostModel) -or -not (Test-Path -LiteralPath $template)) { throw 'The SDK host tools are incomplete.' }
[System.Reflection.Assembly]::LoadFrom($hostModel) | Out-Null
$id = "$($info.version)-$($zipHash.Substring(0, 12))-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
$runtimeContainer = Join-Path $target ".launcher\$id"
# Each deployment gets a new folder; running instances and old files stay intact.
Expand-Archive -LiteralPath $archive -DestinationPath $runtimeContainer
$runtime = $runtimeContainer
$runtimeConfig = Get-Content -LiteralPath (Join-Path $runtime 'AnimaRandomStudio.runtimeconfig.json') -Raw | ConvertFrom-Json
$frameworkVersion = ($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App').version
if (-not $frameworkVersion) { throw 'The candidate does not include a self-contained .NET runtime.' }
$entryBuild = Join-Path $target "dist\root-entry-build\$id"
& $dotnet publish (Join-Path $PSScriptRoot '..\Anima.Launcher.Entry\Anima.Launcher.Entry.csproj') -c Release -r win-x64 -o $entryBuild --nologo "-p:Version=$($info.version)" "-p:RuntimeFrameworkVersion=$frameworkVersion"
if ($LASTEXITCODE -ne 0) { throw 'Cannot build the root entry; the previous root entry is unchanged.' }
foreach ($name in @('AnimaLauncherEntry.dll', 'AnimaLauncherEntry.deps.json', 'AnimaLauncherEntry.runtimeconfig.json')) {
    Copy-Item -LiteralPath (Join-Path $entryBuild $name) -Destination (Join-Path $runtime $name)
}
$relativeDll = [System.IO.Path]::GetRelativePath($target, (Join-Path $runtime 'AnimaLauncherEntry.dll'))
if ([System.Text.Encoding]::UTF8.GetByteCount($relativeDll) -ge 1024) { throw 'The relative launcher path is too long.' }
$temporaryExe = Join-Path $target ('.launcher\entry-' + [Guid]::NewGuid().ToString('N') + '.exe')
[Microsoft.NET.HostModel.AppHost.HostWriter]::CreateAppHost($template, $temporaryExe, $relativeDll, $true, (Join-Path $entryBuild 'AnimaLauncherEntry.exe'), $false)
$entry = Join-Path $target 'AnimaRandomStudio.exe'
if (Test-Path -LiteralPath $entry) {
    Copy-Item -LiteralPath $entry -Destination (Join-Path $target ('.launcher\entry-backup-' + [Guid]::NewGuid().ToString('N') + '.exe'))
}
[System.IO.File]::Move($temporaryExe, $entry, $true)
$result = [ordered]@{ entry = $entry; runtimeDirectory = $runtime; version = $info.version; sourceZipSha256 = $zipHash; signed = $false }
[System.IO.File]::WriteAllText((Join-Path $target '.launcher\root-entry.json'), ($result | ConvertTo-Json) + "`n", [System.Text.UTF8Encoding]::new($false))
$result | ConvertTo-Json
