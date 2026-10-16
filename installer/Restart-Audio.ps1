#Requires -RunAsAdministrator
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
# This operation interrupts playback and recording in every application.
# Only invoke after the user explicitly chooses to restart Windows Audio.
$runningDependents = @((Get-Service -Name Audiosrv).DependentServices | Where-Object { $_.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Running } | Select-Object -ExpandProperty Name)
try {
    foreach ($serviceName in $runningDependents) { Stop-Service -Name $serviceName -ErrorAction Stop }
    Restart-Service -Name Audiosrv -ErrorAction Stop
    (Get-Service -Name Audiosrv).WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running,[TimeSpan]::FromSeconds(15))
    Write-Output 'Windows Audio restarted. Reopen applications that do not recover automatically.'
} catch {
    Start-Service -Name Audiosrv -ErrorAction SilentlyContinue
    throw
} finally {
    foreach ($serviceName in $runningDependents) { Start-Service -Name $serviceName -ErrorAction Continue }
}
