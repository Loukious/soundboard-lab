#Requires -RunAsAdministrator
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$EndpointId)
$ErrorActionPreference = 'Stop'
$endpointGuid = [regex]::Match($EndpointId, '\{[0-9a-fA-F-]{36}\}$').Value
if (-not $endpointGuid) { throw 'Select a full Windows recording endpoint ID.' }
$backupFile = Join-Path $env:ProgramData ('SoundboardLab\EndpointBackups\'+$endpointGuid+'.json')
$snapshot = Get-Content -Raw -LiteralPath $backupFile | ConvertFrom-Json
if ($snapshot.EndpointId -ne $EndpointId) { throw 'The backup belongs to a different recording endpoint.' }
$expectedPath = 'SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture\'+$endpointGuid
if ($snapshot.EndpointPath -ne $expectedPath) { throw 'Invalid endpoint backup path.' }
$machine = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,[Microsoft.Win32.RegistryView]::Registry64)
$fx = $machine.OpenSubKey($expectedPath+'\FxProperties',[Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree,([System.Security.AccessControl.RegistryRights]::ReadKey -bor [System.Security.AccessControl.RegistryRights]::SetValue))
if (-not $fx) { throw 'The recording endpoint is unavailable.' }
try {
    foreach ($value in $snapshot.Values) {
        if ($value.Exists) {
            $kind = [Microsoft.Win32.RegistryValueKind][int]$value.Kind
            $data = $value.Value
            if ($kind -eq [Microsoft.Win32.RegistryValueKind]::DWord) { $data = [int]$data }
            if ($kind -eq [Microsoft.Win32.RegistryValueKind]::QWord) { $data = [long]$data }
            if ($kind -eq [Microsoft.Win32.RegistryValueKind]::Binary) { $data = [byte[]]$data }
            if ($kind -eq [Microsoft.Win32.RegistryValueKind]::MultiString) { $data = [string[]]$data }
            $fx.SetValue([string]$value.Name,$data,$kind)
        } else { $fx.DeleteValue([string]$value.Name,$false) }
    }
    $archive = $backupFile + '.restored.' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss')
    Move-Item -LiteralPath $backupFile -Destination $archive
    Write-Output 'Restored the endpoint effects and saved the completed backup history. Close and reopen recording applications.'
} finally { $fx.Dispose(); $machine.Dispose() }
