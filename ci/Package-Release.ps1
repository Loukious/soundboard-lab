[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
$package=Join-Path $projectRoot 'artifacts\SoundboardLab-preview'
if(-not (Test-Path -LiteralPath (Join-Path $package 'SoundboardLab.exe'))){throw 'Build the portable application before packaging.'}
$buildInfo=Get-Content -LiteralPath (Join-Path $package 'build-info.json') -Raw | ConvertFrom-Json
$version=$buildInfo.Version
if($version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$'){throw 'The portable build has no valid release version.'}
if($env:GITHUB_REF -like 'refs/tags/v*' -and $env:GITHUB_REF -cne ('refs/tags/v'+$version)){throw ('Package version differs from release tag: '+$version)}
$notes=[regex]::Match((Get-Content -LiteralPath (Join-Path $package 'CHANGELOG.md') -Raw),('(?ms)^## \['+[regex]::Escape($version)+'\][^\r\n]*\r?\n(.*?)(?=^## \[|\z)'))
if(-not $notes.Success){throw ('No changelog entry for version '+$version)}
$entries=Get-Content -LiteralPath (Join-Path $package 'sha256.json') -Raw | ConvertFrom-Json
foreach($entry in $entries){if((Get-FileHash -LiteralPath (Join-Path $package $entry.Path) -Algorithm SHA256).Hash -ne $entry.SHA256){throw ('Package hash mismatch: '+$entry.Path)}}
$release=Join-Path $projectRoot 'artifacts\release'
New-Item -ItemType Directory -Path $release -Force | Out-Null
$archive=Join-Path $release ('SoundboardLab-'+$version+'-windows-x64.zip')
Compress-Archive -Path (Join-Path $package '*') -DestinationPath $archive -Force -CompressionLevel Optimal
$hash=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $release 'SHA256SUMS.txt'),($hash+'  '+[IO.Path]::GetFileName($archive)+"`n"),[Text.Encoding]::ASCII)
[IO.File]::WriteAllText((Join-Path $release 'release-notes.md'),$notes.Groups[1].Value.Trim()+"`n",[Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $release 'release-metadata.json'),([ordered]@{Version=$version;Commit=$buildInfo.Commit}|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
Write-Output ('Packaged '+$archive)
