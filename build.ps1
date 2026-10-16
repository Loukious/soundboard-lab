[CmdletBinding()]
param([switch]$SkipTests,[switch]$SkipPublish,[switch]$SkipNativeBuild,[switch]$CI,[switch]$RunInteractiveTests,[switch]$RunCommunicationsTests,[string]$LiveMicrophoneEndpointId,[string]$ExpectedHostDllSHA256)
$ErrorActionPreference='Stop'
$projectRoot=$PSScriptRoot
$versionOutput=& dotnet msbuild (Join-Path $projectRoot 'app\SoundboardLab.csproj') -getProperty:Version -nologo
if($LASTEXITCODE -ne 0){throw 'Could not read the application version.'}
$version=($versionOutput -join "`n").Trim()
if($version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*)?$'){throw ('Invalid semantic version: '+$version)}
if($env:GITHUB_REF -like 'refs/tags/v*' -and $env:GITHUB_REF -cne ('refs/tags/v'+$version)){throw ('Release tag must match app version v'+$version)}
Write-Output ('Building Soundboard Lab '+$version)
& (Join-Path $projectRoot 'Prepare-Translations.ps1')
if($LiveMicrophoneEndpointId -and -not $RunInteractiveTests){throw 'Live app verification opens windows; specify -RunInteractiveTests explicitly to run it.'}
$artifactRoot=Join-Path $projectRoot 'artifacts'
$package=Join-Path $artifactRoot 'SoundboardLab-preview'
if(-not $SkipPublish){
    $runningPreview=Get-Process SoundboardLab -ErrorAction SilentlyContinue | Where-Object {$_.Path -and $_.Path.StartsWith($package+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)}
    if($runningPreview){throw 'Close the running preview before rebuilding artifacts\SoundboardLab-preview.'}
}
if(-not $SkipNativeBuild) { & (Join-Path $projectRoot 'native\build-native.ps1') -SkipTests:$SkipTests }
elseif(-not (Test-Path -LiteralPath (Join-Path $projectRoot 'native\build\SoundboardLab.Audio.dll'))) { throw 'Build the native audio component before reusing it.' }
if(-not $SkipTests) {
    & (Join-Path $projectRoot 'tests\Test-GuiHelper.ps1')
    & (Join-Path $projectRoot 'tests\Test-EndpointInstallPlan.ps1') | Tee-Object -FilePath (Join-Path $projectRoot 'tests\endpoint-install-plan-latest.txt')
    $fixtures=Join-Path $projectRoot 'tests\fixtures'
    if(Test-Path -LiteralPath $fixtures){$env:SB_CODEC_FIXTURES=$fixtures}
    $engineArguments=@();if($RunCommunicationsTests){$engineArguments+= '--communications-stream'};if($CI){$engineArguments+= '--offline'}
    & dotnet run --project (Join-Path $projectRoot 'tests\engine\Engine.Tests.csproj') --verbosity quiet -- @engineArguments | Tee-Object -FilePath (Join-Path $projectRoot 'tests\engine\latest-results.txt')
    if($LASTEXITCODE -ne 0){throw 'Audio engine tests failed.'}
    & dotnet run --project (Join-Path $projectRoot 'tests\services\ServiceTests.csproj') --verbosity quiet | Tee-Object -FilePath (Join-Path $projectRoot 'tests\services\latest-results.txt')
    if($LASTEXITCODE -ne 0){throw 'Library/hotkey/remote service tests failed.'}
}
& dotnet build (Join-Path $projectRoot 'app\SoundboardLab.csproj') --verbosity minimal
if($LASTEXITCODE -ne 0){throw 'Desktop application build failed.'}
if(-not $SkipTests){
    $controlArguments=@();if($CI){$controlArguments+= '--layout-only'}
    & dotnet run --project (Join-Path $projectRoot 'tests\controls\Controls.Tests.csproj') --verbosity quiet -- @controlArguments | Tee-Object -FilePath (Join-Path $projectRoot 'tests\controls\latest-results.txt')
    if($LASTEXITCODE -ne 0){throw 'Headless WPF control checks failed.'}
}
if(-not $SkipTests -and $RunInteractiveTests){ & (Join-Path $projectRoot 'tests\Test-UI.ps1') -AllowWindowActivation; & (Join-Path $projectRoot 'tests\Test-GUI.ps1') -AllowDesktopInput; & (Join-Path $projectRoot 'tests\Test-GUI.ps1') -AllowDesktopInput -ColumnsOnly; & (Join-Path $projectRoot 'tests\Test-GUI.ps1') -AllowDesktopInput -DetailsOnly }
if(-not $SkipPublish) {
    New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
    & dotnet publish (Join-Path $projectRoot 'app\SoundboardLab.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $package --verbosity minimal
    if($LASTEXITCODE -ne 0){throw 'Self-contained publish failed.'}
    if(-not $SkipTests){
        # Exercises the real compiled helper/IPC and reads existing configuration;
        # no UAC, windows, endpoint writes, streams or service changes.
        $setupCheck=Start-Process -FilePath (Join-Path $package 'SoundboardLab.exe') -ArgumentList '--check-device-setup' -PassThru -WindowStyle Hidden
        if(-not $setupCheck.WaitForExit(30000)){throw 'Compiled device setup helper check timed out.'}
        if($setupCheck.ExitCode -ne 0){throw 'Compiled device setup helper check failed.'}
        Write-Output 'PASS compiled device setup helper and read-only configuration check'
        $setupCheck.Dispose()
    }
    $smokeVerified=$false
    if($RunInteractiveTests){
    $smokeState=Join-Path $projectRoot 'tests\published-state-preview'
    New-Item -ItemType Directory -Path $smokeState -Force | Out-Null
    $smokeRecord=Join-Path $smokeState 'ui-smoke.json';if(Test-Path -LiteralPath $smokeRecord){Remove-Item -LiteralPath $smokeRecord}
    $smoke=Start-Process -FilePath (Join-Path $package 'SoundboardLab.exe') -ArgumentList @('--smoke-test','--data-dir',('"'+$smokeState+'"')) -PassThru -WindowStyle Hidden
    if(-not $smoke.WaitForExit(20000)){throw ('Published smoke process did not exit; inspect PID '+$smoke.Id)}
    if($smoke.ExitCode -ne 0 -or -not (Get-Content -Raw -LiteralPath (Join-Path $smokeState 'ui-smoke.json') | ConvertFrom-Json).passed){throw 'Published package smoke check failed.'}
    $smokeVerified=$true
    }
    $microphoneVerified=$false
    $microphoneRecord=Join-Path $package 'microphone-validation.json';if(Test-Path -LiteralPath $microphoneRecord){Remove-Item -LiteralPath $microphoneRecord}
    if($LiveMicrophoneEndpointId -and $RunInteractiveTests) {
        & (Join-Path $projectRoot 'tests\live\Test-AppMicrophone.ps1') -Executable (Join-Path $package 'SoundboardLab.exe') -EndpointId $LiveMicrophoneEndpointId -ExpectedHostDllSHA256 $ExpectedHostDllSHA256
        $liveResult=Get-Content -LiteralPath (Join-Path $projectRoot 'tests\live\app-latest-summary.json') -Raw | ConvertFrom-Json
        if(-not $liveResult.Verified -or $liveResult.Executable -ne (Join-Path $package 'SoundboardLab.exe')) { throw 'Published microphone verification failed.' }
        $microphoneVerified=$true
        Copy-Item -LiteralPath (Join-Path $projectRoot 'tests\live\app-latest-summary.json') -Destination (Join-Path $package 'microphone-validation.json')
    }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md'),(Join-Path $projectRoot 'CHANGELOG.md'),(Join-Path $projectRoot 'FEATURES.md'),(Join-Path $projectRoot 'PREFERENCES.md'),(Join-Path $projectRoot 'VALIDATION.md'),(Join-Path $projectRoot 'Remote.ps1') -Destination $package
    Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') -Destination $package
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs') -Destination $package -Recurse -Force
    $info=[ordered]@{Version=$version;CreatedUtc=[DateTime]::UtcNow.ToString('o');Architecture='win-x64';SelfContained=$true;TestsExecuted=(-not $SkipTests);HeadlessControlsVerified=(-not $SkipTests);HardwarePlaybackTestsExecuted=((-not $SkipTests) -and -not $CI);CIBuild=[bool]$CI;Commit=$env:GITHUB_SHA;HeadlessControlsScope=$(if($CI){'layout-only'}else{'playback-and-layout'});InteractiveTestsExecuted=((-not $SkipTests) -and [bool]$RunInteractiveTests);CommunicationsTestsExecuted=((-not $SkipTests) -and [bool]$RunCommunicationsTests);NativeBuildReused=[bool]$SkipNativeBuild;PublishedSmokeVerified=$smokeVerified;MicrophoneServiceIntegrationVerified=$microphoneVerified;MicrophoneVerifiedEndpoint=$(if($microphoneVerified){$LiveMicrophoneEndpointId}else{$null});GoalComplete=$false}
    $info | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $package 'build-info.json') -Encoding utf8
    $manifestPath=Join-Path $package 'sha256.json'
    $hashes=Get-ChildItem -LiteralPath $package -File -Recurse | Where-Object {$_.FullName -ne $manifestPath} | ForEach-Object { [pscustomobject]@{Path=$_.FullName.Substring($package.Length+1);SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash} }
    $hashes | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $package 'sha256.json') -Encoding utf8
    [pscustomobject]@{Directory=$package;Executable=(Join-Path $package 'SoundboardLab.exe')} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $artifactRoot 'latest.json') -Encoding utf8
    Get-Content -LiteralPath (Join-Path $artifactRoot 'latest.json')
}
