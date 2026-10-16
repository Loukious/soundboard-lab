[CmdletBinding()]
param([ValidateSet('Install','Restore','Restart','Diagnose')][string]$Operation,[string]$EndpointFile)
$ErrorActionPreference = 'Stop'
$resultPath = Join-Path $PSScriptRoot ($Operation.ToLowerInvariant() + '-result.json')
Start-Transcript -Path (Join-Path $PSScriptRoot ($Operation.ToLowerInvariant() + '-transcript.txt')) -Force
$success = $false
try {
    $endpointId = (Get-Content -LiteralPath $EndpointFile -Raw | ConvertFrom-Json).Communications.ID
    $projectRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    if ($Operation -eq 'Install') {
        & (Join-Path $projectRoot 'installer\Install-Microphone.ps1') -EndpointId $endpointId -DllPath (Join-Path $projectRoot 'native\build\SoundboardLab.Audio.dll')
    } elseif ($Operation -eq 'Restore') { & (Join-Path $projectRoot 'installer\Restore-Microphone.ps1') -EndpointId $endpointId }
    elseif ($Operation -eq 'Restart') { & (Join-Path $projectRoot 'installer\Restart-Audio.ps1') }
    elseif ($Operation -eq 'Diagnose') {
        Get-Process audiodg | ForEach-Object { $_.Modules | Select-Object ModuleName,FileName } | Format-Table -AutoSize
        Get-WinEvent -FilterHashtable @{LogName='Microsoft-Windows-Audio/Operational';StartTime=(Get-Date).AddMinutes(-10)} -MaxEvents 30 -ErrorAction SilentlyContinue | Format-List TimeCreated,Id,Message
        Get-WinEvent -FilterHashtable @{LogName='Microsoft-Windows-CodeIntegrity/Operational';StartTime=(Get-Date).AddMinutes(-10)} -MaxEvents 15 -ErrorAction SilentlyContinue | Format-List TimeCreated,Id,Message
    }
    $success = $true
} catch { $failure = $_.ToString(); Write-Output $failure }
finally {
    [pscustomobject]@{Operation=$Operation;Success=$success;Error=$failure;Utc=[DateTime]::UtcNow.ToString('o')} | ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding utf8
    Stop-Transcript
}
if (-not $success) { exit 1 }
