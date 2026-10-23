param([Parameter(Mandatory)][string]$SourceExe, [Parameter(Mandatory)][string]$Version, [Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repo = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$pin = (Get-Content -LiteralPath (Join-Path $repo 'launcher\installer\tools.json') -Raw | ConvertFrom-Json).innoSetup
$toolRoot = Join-Path $repo ('tools\inno-setup-' + $pin.version)
$installer = Join-Path $repo ('tools\innosetup-' + $pin.version + '.exe')
[System.IO.Directory]::CreateDirectory((Split-Path $installer -Parent)) | Out-Null
if (-not (Test-Path -LiteralPath $installer)) { Invoke-WebRequest -Uri $pin.url -OutFile $installer }
if ((Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant() -ne $pin.sha256) { throw 'Pinned Inno Setup download checksum mismatch.' }
$signature = Get-AuthenticodeSignature -LiteralPath $installer
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike ('*CN=' + $pin.publisher + ',*')) { throw 'Pinned Inno Setup installer signature or publisher is invalid.' }
$compiler = Join-Path $toolRoot 'ISCC.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', '/NOICONS', '/TASKS=""', ('/DIR="' + $toolRoot + '"'))
    $process = Start-Process -FilePath $installer -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -ne 0) { throw "Inno Setup installation failed: $($process.ExitCode)" }
}
if ((Get-FileHash -LiteralPath $compiler -Algorithm SHA256).Hash.ToLowerInvariant() -ne $pin.compilerSha256) { throw 'The installed Inno Setup compiler differs from its pin.' }
& $compiler ("/DAppVersion=$Version") ("/DSourceExe=$([System.IO.Path]::GetFullPath($SourceExe))") ("/DOutputDirectory=$([System.IO.Path]::GetFullPath($OutputDirectory))") (Join-Path $repo 'launcher\installer\AnimaRandomStudio.iss')
if ($LASTEXITCODE -ne 0) { throw 'Launcher Setup compilation failed.' }
