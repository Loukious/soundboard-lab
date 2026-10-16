[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$EndpointId,[string]$Label='live')
$ErrorActionPreference = 'Stop'
if ($Label -notmatch '^[a-zA-Z0-9-]+$') { throw 'Use letters, digits and hyphens for the report label.' }
$probe = Join-Path $PSScriptRoot 'bin\Release\net10.0-windows\LiveMicrophone.exe'
if (-not (Test-Path -LiteralPath $probe)) { throw 'Build LiveMicrophone.csproj in Release first.' }
$report = Join-Path $PSScriptRoot ($Label+'-capture.json')
$feedReport = Join-Path $PSScriptRoot ($Label+'-feed.json')
$ready = $report+'.ready'
if (Test-Path -LiteralPath $ready) { Remove-Item -LiteralPath $ready }
$captureProcess = Start-Process -FilePath $probe -WindowStyle Hidden -ArgumentList @('capture',$EndpointId,('"'+$report+'"'),'9') -PassThru -RedirectStandardOutput (Join-Path $PSScriptRoot ($Label+'-stdout.txt')) -RedirectStandardError (Join-Path $PSScriptRoot ($Label+'-stderr.txt'))
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    while (-not (Test-Path -LiteralPath $ready) -and [DateTime]::UtcNow -lt $deadline -and -not $captureProcess.HasExited) { Start-Sleep -Milliseconds 100 }
    if (-not (Test-Path -LiteralPath $ready)) { throw 'Separate recording client did not become ready.' }
    Start-Sleep -Milliseconds 1000
    & $probe feed $EndpointId $feedReport
    $feedExit = $LASTEXITCODE
    if (-not $captureProcess.WaitForExit(12000)) { throw 'Recording client did not finish.' }
    $capture = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    $feed = Get-Content -LiteralPath $feedReport -Raw | ConvertFrom-Json
    $toneBins = @($capture.Bins | Where-Object { $_.ToneAmplitude -gt .005 -and $_.ToneAmplitude -gt $_.Rms * 1.2 })
    $tail = @($capture.Bins | Where-Object { $_.EndSeconds -ge 7 })
    $tailTone = ($tail | Measure-Object -Property ToneAmplitude -Maximum).Maximum
    $active = @($feed.Snapshots | Where-Object { $_.ActiveConsumers -gt 0 })
    $verified = $feedExit -eq 0 -and $captureProcess.ExitCode -eq 0 -and $null -eq $capture.Error -and $capture.ProcessId -ne $feed.ProcessId -and $feed.FramesSent -eq 192000 -and $active.Count -gt 0 -and $toneBins.Count -ge 8 -and $tail.Count -gt 0 -and $tailTone -lt .001
    $summary = [pscustomobject]@{Verified=$verified;EndpointId=$EndpointId;RecorderPid=$capture.ProcessId;ProducerPid=$feed.ProcessId;ToneFrequencyHz=997;ExpectedAmplitude=.15;DetectedQuarterSecondBins=$toneBins.Count;MaximumToneAmplitude=($capture.Bins | Measure-Object ToneAmplitude -Maximum).Maximum;TailMaximumToneAmplitude=$tailTone;ActiveConsumerSnapshots=$active.Count;FeedExit=$feedExit;CaptureExit=$captureProcess.ExitCode;Utc=[DateTime]::UtcNow.ToString('o')}
    $summary | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot ($Label+'-summary.json')) -Encoding utf8
    $summary | Format-List
    if (-not $verified) { throw 'Live microphone injection did not pass; inspect the saved reports.' }
} finally {
    if (-not $captureProcess.HasExited) { $captureProcess.Kill(); $captureProcess.WaitForExit() }
    $captureProcess.Dispose()
}
