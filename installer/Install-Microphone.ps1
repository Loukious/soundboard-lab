#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$EndpointId,
    [string]$DllPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'SoundboardLab.Audio.dll'),
    [ValidateSet('PostMix','Stream','Mode','Endpoint')][string]$EffectSlot = 'PostMix',
    [switch]$Repair,
    [switch]$ChangeEffectSlot
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Endpoint-InstallPlan.ps1')
if($ChangeEffectSlot -and -not $Repair){throw 'Changing the effect slot requires the repair/update path.'}
$clsid = '{AE6B213A-2854-47D6-93D6-CA9F22880D3E}'
$custom = '{5f294d10-e843-43ba-8860-326e9f173109}'
$effectGuid = '{d04e05a6-594b-4fb6-a80d-01af5eed7d1d}'
$effectPid = @{PostMix=2;Stream=5;Mode=6;Endpoint=7}[$EffectSlot]
$endpointGuid = [regex]::Match($EndpointId, '\{[0-9a-fA-F-]{36}\}$').Value
if (-not $endpointGuid) { throw 'Select a full Windows recording endpoint ID.' }
$endpointPath = 'SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture\' + $endpointGuid
$machine = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,[Microsoft.Win32.RegistryView]::Registry64)
$endpoint = $machine.OpenSubKey($endpointPath,$false)
if (-not $endpoint) { throw 'The selected recording endpoint does not exist or cannot be configured.' }
# Windows grants administrators SetValue on audio endpoint properties, but not
# CreateSubKey. Request only the rights required to edit the existing values.
$fx = $machine.OpenSubKey($endpointPath+'\FxProperties',[Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree,([System.Security.AccessControl.RegistryRights]::ReadKey -bor [System.Security.AccessControl.RegistryRights]::SetValue))
if (-not $fx) { throw 'The selected recording endpoint has no effects property store.' }
$effectName = $effectGuid + ',' + $effectPid
$backupFolder = Join-Path $env:ProgramData 'SoundboardLab\EndpointBackups'
New-Item -ItemType Directory -Path $backupFolder -Force | Out-Null
$backupFile = Join-Path $backupFolder ($endpointGuid + '.json')
$existingSnapshot=$null
if(Test-Path -LiteralPath $backupFile){
 if(-not $Repair){throw 'This endpoint already has a Soundboard Lab backup. Restore its existing setup before reinstalling.'}
 $existingSnapshot=Get-Content -LiteralPath $backupFile -Raw|ConvertFrom-Json
 if($existingSnapshot.EndpointId -ne $EndpointId -or $existingSnapshot.EndpointPath -ne $endpointPath){throw 'The saved endpoint backup does not match this microphone.'}
 if($existingSnapshot.EffectName -notin @(($effectGuid+',2'),($effectGuid+',5'),($effectGuid+',6'),($effectGuid+',7'))){throw 'Invalid saved effect slot.'}
 if(-not $ChangeEffectSlot){$effectName=$existingSnapshot.EffectName;$EffectSlot=$existingSnapshot.EffectSlot}
}
$names = @($effectName,($custom+',1'),($custom+',2'),'{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5')
if($existingSnapshot -and $ChangeEffectSlot){$names=@($names + $existingSnapshot.EffectName | Select-Object -Unique)}
$records = foreach ($name in $names) {
    $exists = $fx.GetValueNames() -contains $name
    [pscustomobject]@{Name=$name;Exists=$exists;Kind=if($exists){[int]$fx.GetValueKind($name)}else{0};Value=if($exists){$fx.GetValue($name)}else{$null}}
}
$resolvedDll = (Resolve-Path -LiteralPath $DllPath).Path
if ([IO.Path]::GetExtension($resolvedDll) -ne '.dll') { throw 'The audio component must be a DLL.' }
$buildHash=(Get-FileHash -LiteralPath $resolvedDll -Algorithm SHA256).Hash.Substring(0,12)
$installDir = Join-Path (Join-Path $env:ProgramFiles 'SoundboardLab\Audio') $buildHash
New-Item -ItemType Directory -Path $installDir -Force | Out-Null
$installedDll = Join-Path $installDir 'SoundboardLab.Audio.dll'
if($resolvedDll -ne $installedDll -and (-not (Test-Path -LiteralPath $installedDll) -or (Get-FileHash -LiteralPath $installedDll).Hash -ne (Get-FileHash -LiteralPath $resolvedDll).Hash)){Copy-Item -LiteralPath $resolvedDll -Destination $installedDll -Force}
$plan=New-EndpointInstallPlan -EndpointId $EndpointId -EndpointPath $endpointPath -EffectSlot $EffectSlot -Records @($records) -Previous $existingSnapshot -DllPath $installedDll
$snapshot=$plan.Snapshot;$oldEffect=$plan.ChainedEffect
if(-not $existingSnapshot){$snapshot | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $backupFile -Encoding utf8}
try {
    $registration = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\regsvr32.exe') -ArgumentList @('/s',('"'+$installedDll+'"')) -Wait -PassThru -WindowStyle Hidden
    if ($registration.ExitCode -ne 0) { throw ('Windows could not register the audio component. Exit code: '+$registration.ExitCode) }
    if($plan.RestoreEffect){
        $previousEffect=$plan.RestoreEffect
        if($previousEffect.Exists){$fx.SetValue([string]$previousEffect.Name,[string]$previousEffect.Value,[Microsoft.Win32.RegistryValueKind][int]$previousEffect.Kind)}else{$fx.DeleteValue([string]$previousEffect.Name,$false)}
    }
    $fx.SetValue($custom+',1',$EndpointId)
    $fx.SetValue($custom+',2',$oldEffect)
    $fx.SetValue($effectName,$clsid)
    $fx.SetValue('{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5',0,[Microsoft.Win32.RegistryValueKind]::DWord)
    if($existingSnapshot){
        $nextBackup=$backupFile+'.next.'+[Guid]::NewGuid().ToString('N')
        try{
            $snapshot|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $nextBackup -Encoding utf8
            [IO.File]::Replace($nextBackup,$backupFile,$backupFile+'.previous')
        }finally{if(Test-Path -LiteralPath $nextBackup){Remove-Item -LiteralPath $nextBackup}}
    }
    Write-Output ('Configured ' + $EndpointId + ' in the ' + $EffectSlot + ' effect slot.')
    Write-Output ('Original effect and properties backed up to ' + $backupFile)
    Write-Output 'Close and reopen recording applications to rebuild their audio graphs. If the effect does not load, use Restore-Microphone.ps1.'
} catch {
    foreach ($record in $records) {
        if ($record.Exists) { $fx.SetValue($record.Name,$record.Value,[Microsoft.Win32.RegistryValueKind]$record.Kind) }
        else { $fx.DeleteValue($record.Name,$false) }
    }
    if(-not $existingSnapshot){$snapshot | Add-Member -NotePropertyName InstallFailed -NotePropertyValue $true;$snapshot | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $backupFile -Encoding utf8}
    throw
} finally { $fx.Dispose(); $endpoint.Dispose(); $machine.Dispose() }
