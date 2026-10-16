#Requires -RunAsAdministrator
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$SelectionFile,[Parameter(Mandatory=$true)][string]$DllPath,[switch]$Repair)
$ErrorActionPreference='Stop'
$selected=@(Get-Content -LiteralPath $SelectionFile -Raw|ConvertFrom-Json)
foreach($id in $selected){if($id -notmatch '^\{0\.0\.1\.[^}]+\}\.\{[0-9a-fA-F-]{36}\}$'){throw 'Invalid recording endpoint selection.'}}
$custom='{5f294d10-e843-43ba-8860-326e9f173109},1'
$base='HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture'
$remove=@()
foreach($endpoint in Get-ChildItem -LiteralPath $base){
 $fx=Join-Path $endpoint.PSPath 'FxProperties'
 if(Test-Path -LiteralPath $fx){$id=Get-ItemPropertyValue -LiteralPath $fx -Name $custom -ErrorAction SilentlyContinue;if($id -and $selected -notcontains $id){$remove+= $id}}
}
if($selected.Count -eq 0){throw 'Select at least one recording device.'}
foreach($id in $selected){$guid=[regex]::Match($id,'\{[0-9a-fA-F-]{36}\}$').Value;if(-not (Test-Path -LiteralPath (Join-Path $base $guid))){throw 'A selected recording device is unavailable.'}}
$added=@()
try{
 foreach($id in $selected){
  $guid=[regex]::Match($id,'\{[0-9a-fA-F-]{36}\}$').Value
  $fx=Join-Path (Join-Path $base $guid) 'FxProperties'
  $installed=Get-ItemPropertyValue -LiteralPath $fx -Name $custom -ErrorAction SilentlyContinue
  if($installed -ne $id){& (Join-Path $PSScriptRoot 'Install-Microphone.ps1') -EndpointId $id -DllPath $DllPath;$added+=$id}elseif($Repair){& (Join-Path $PSScriptRoot 'Install-Microphone.ps1') -EndpointId $id -DllPath $DllPath -Repair}
 }
}catch{
 $installationError=$_
 foreach($id in $added){try{& (Join-Path $PSScriptRoot 'Restore-Microphone.ps1') -EndpointId $id}catch{Write-Warning "Restoration failed for ${id}: $_"}}
 throw $installationError
}
# Keep working devices configured until every newly selected device was installed.
foreach($id in $remove){& (Join-Path $PSScriptRoot 'Restore-Microphone.ps1') -EndpointId $id}
