[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Executable,[Parameter(Mandatory=$true)][string]$EndpointId,[string]$ExpectedHostDllSHA256)
$ErrorActionPreference = 'Stop'
if (Get-Process SoundboardLab -ErrorAction SilentlyContinue) { throw 'Close the existing Soundboard Lab instance before this isolated test.' }
$appPath = (Resolve-Path -LiteralPath $Executable).Path
$probe = Join-Path $PSScriptRoot 'bin\Release\net10.0-windows\LiveMicrophone.exe'
$projectRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$remote = Join-Path $projectRoot 'Remote.ps1'
$pipeName='SoundboardLab.Live.'+[Guid]::NewGuid().ToString('N')
$runDirectory = Join-Path $PSScriptRoot ('app-run-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
@{CaptureDeviceId=$EndpointId;BlockVoice=$true;Normalize=$false;SpeakerVolume=0;ClipVolume=1;RemoteEnabled=$true} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runDirectory 'preferences.json') -Encoding utf8
$tone = Join-Path $runDirectory 'tone.wav'
& $probe tone $tone
function Request($data) {
    $response = & $remote -PipeName $pipeName -Command $data.command -Json ($data | ConvertTo-Json -Compress) | ConvertFrom-Json
    if (-not $response.ok) { throw $response.error }
    return $response
}
$app = Start-Process -FilePath $appPath -WindowStyle Hidden -ArgumentList @('--data-dir',('"'+$runDirectory+'"'),'--pipe-name',$pipeName) -PassThru
$captureProcess = $null
try {
    if((Request @{command='state'}).processId -ne $app.Id){throw 'Live test connected to the wrong application instance.'}
    $report = Join-Path $runDirectory 'capture.json'
    $captureProcess = Start-Process -FilePath $probe -WindowStyle Hidden -ArgumentList @('capture',$EndpointId,('"'+$report+'"'),'9') -PassThru -RedirectStandardOutput (Join-Path $runDirectory 'stdout.txt') -RedirectStandardError (Join-Path $runDirectory 'stderr.txt')
    # Cache the process handle before exit so Windows PowerShell 5.1 retains ExitCode.
    $null=$captureProcess.Handle
    $deadline=[DateTime]::UtcNow.AddSeconds(5)
    while (-not (Test-Path -LiteralPath ($report+'.ready')) -and [DateTime]::UtcNow -lt $deadline -and -not $captureProcess.HasExited) { Start-Sleep -Milliseconds 100 }
    if (-not (Test-Path -LiteralPath ($report+'.ready'))) { throw 'Recorder did not become ready.' }
    Start-Sleep -Milliseconds 1000
    Request @{command='play';path=$tone;mode='Microphone'} | Out-Null
    $playing=Request @{command='state'}
    $playing | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $runDirectory 'playing-state.json') -Encoding utf8
    if (-not $captureProcess.WaitForExit(12000)) { throw 'Recorder did not finish.' }
    $finished=Request @{command='state'}
    $capture=Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    $toneBins=@($capture.Bins | Where-Object { $_.ToneAmplitude -gt .005 -and $_.ToneAmplitude -gt $_.Rms*1.2 })
    $tail=@($capture.Bins | Where-Object { $_.EndSeconds -ge 7 })
    $tailTone=($tail | Measure-Object ToneAmplitude -Maximum).Maximum
    $appDllHash=(Get-FileHash -LiteralPath (Join-Path (Split-Path $appPath -Parent) 'SoundboardLab.Audio.dll') -Algorithm SHA256).Hash
    $installedDllHash=(Get-FileHash -LiteralPath (Join-Path $env:ProgramFiles 'SoundboardLab\Audio\SoundboardLab.Audio.dll') -Algorithm SHA256).Hash
    # An explicitly pinned older host is permitted for the unchanged v1 shared-memory protocol.
    # The actual client/host signal test below establishes compatibility for these exact hashes.
    $hostVerified=if($ExpectedHostDllSHA256){$installedDllHash -eq $ExpectedHostDllSHA256}else{$appDllHash -eq $installedDllHash}
    $checks=[ordered]@{
        PlaybackStarted=[bool]$playing.playing
        MicrophoneConnected=[bool]$playing.microphone.Connected
        FourSecondClip=($playing.duration -eq 4)
        PlaybackCompleted=(-not $finished.playing)
        PositionReset=($finished.position -eq 0)
        RecorderExitedSuccessfully=($captureProcess.ExitCode -eq 0)
        CaptureHasNoError=($null -eq $capture.Error)
        ToneDetected=($toneBins.Count -ge 8)
        TailRecorded=($tail.Count -gt 0)
        TailSilent=($tailTone -lt .001)
        HostHashVerified=[bool]$hostVerified
    }
    $verified=@($checks.Values|Where-Object {-not $_}).Count -eq 0
    $summary=[pscustomobject]@{Verified=$verified;Executable=$appPath;ExecutableSHA256=(Get-FileHash -LiteralPath $appPath -Algorithm SHA256).Hash;NativeDllSHA256=$appDllHash;EndpointId=$EndpointId;AppPid=$app.Id;RecorderPid=$captureProcess.Id;ToneFrequencyHz=997;DurationSeconds=$playing.duration;ActiveConsumers=$playing.microphone.ActiveConsumers;DetectedQuarterSecondBins=$toneBins.Count;MaximumToneAmplitude=($capture.Bins | Measure-Object ToneAmplitude -Maximum).Maximum;TailMaximumToneAmplitude=$tailTone;PlaybackCompleted=(-not $finished.playing);RunDirectory=$runDirectory;Utc=[DateTime]::UtcNow.ToString('o')}
    $summary|Add-Member InstalledHostDllSHA256 $installedDllHash
    $summary|Add-Member IdenticalNativeBinaries ($appDllHash -eq $installedDllHash)
    $summary|Add-Member ExpectedHostDllSHA256 $ExpectedHostDllSHA256
    $summary|Add-Member SharedMemoryProtocolVersion 1
    $summary|Add-Member RecorderExitCode $captureProcess.ExitCode
    $summary|Add-Member Checks $checks
    $summary | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'app-latest-summary.json') -Encoding utf8
    $summary | Format-List
    if (-not $verified) { throw 'Application microphone playback failed the live signal checks.' }
} finally {
    if (-not $app.HasExited) { try { Request @{command='close'} | Out-Null } catch { }; if (-not $app.WaitForExit(5000)) { $app.Kill(); $app.WaitForExit() } }
    if ($captureProcess -and -not $captureProcess.HasExited) { $captureProcess.Kill(); $captureProcess.WaitForExit() }
    if ($captureProcess) { $captureProcess.Dispose() }; $app.Dispose()
}
