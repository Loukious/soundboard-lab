[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
$package=Join-Path $projectRoot 'artifacts\SoundboardLab-preview'
if(-not (Test-Path -LiteralPath (Join-Path $package 'SoundboardLab.exe'))){throw 'Build the portable application before packaging.'}
$entries=Get-Content -LiteralPath (Join-Path $package 'sha256.json') -Raw | ConvertFrom-Json
foreach($entry in $entries){if((Get-FileHash -LiteralPath (Join-Path $package $entry.Path) -Algorithm SHA256).Hash -ne $entry.SHA256){throw ('Package hash mismatch: '+$entry.Path)}}
$release=Join-Path $projectRoot 'artifacts\release'
New-Item -ItemType Directory -Path $release -Force | Out-Null
$archive=Join-Path $release 'SoundboardLab-windows-x64.zip'
Compress-Archive -Path (Join-Path $package '*') -DestinationPath $archive -Force -CompressionLevel Optimal
$hash=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $release 'SHA256SUMS.txt'),($hash+'  '+[IO.Path]::GetFileName($archive)+"`n"),[Text.Encoding]::ASCII)
Write-Output ('Packaged '+$archive)
