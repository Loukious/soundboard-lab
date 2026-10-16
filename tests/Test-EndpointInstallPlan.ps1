$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '..\installer\Endpoint-InstallPlan.ps1')
$script:checks=0
function Check([bool]$condition,[string]$description){if(-not $condition){throw $description};$script:checks++;Write-Output ('PASS '+$description)}
$endpoint='{0.0.1.00000000}.{11111111-1111-1111-1111-111111111111}'
$endpointPath='SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture\{11111111-1111-1111-1111-111111111111}'
$effectGuid='{d04e05a6-594b-4fb6-a80d-01af5eed7d1d}'
$clsid='{AE6B213A-2854-47D6-93D6-CA9F22880D3E}'
$original=@(
 [pscustomobject]@{Name=$effectGuid+',2';Exists=$true;Kind=1;Value='{VENDOR-POST-MIX}'},
 [pscustomobject]@{Name=$effectGuid+',5';Exists=$true;Kind=1;Value='{VENDOR-STREAM}'},
 [pscustomobject]@{Name=$effectGuid+',6';Exists=$false;Kind=0;Value=$null},
 [pscustomobject]@{Name='{5f294d10-e843-43ba-8860-326e9f173109},1';Exists=$false;Kind=0;Value=$null},
 [pscustomobject]@{Name='{5f294d10-e843-43ba-8860-326e9f173109},2';Exists=$false;Kind=0;Value=$null},
 [pscustomobject]@{Name='{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5';Exists=$true;Kind=4;Value=1}
)
function Plan([string]$slot,[object[]]$records,$previous){New-EndpointInstallPlan -EndpointId $endpoint -EndpointPath $endpointPath -EffectSlot $slot -Records $records -Previous $previous -DllPath 'C:\fixture\audio.dll'}
$postRecords=@($original | Where-Object {$_.Name -ne $effectGuid+',5' -and $_.Name -ne $effectGuid+',6'})
$first=Plan 'PostMix' $postRecords $null
Check ($first.ChainedEffect -eq '{VENDOR-POST-MIX}' -and $null -eq $first.RestoreEffect) 'initial install chains the vendor effect and does not restore an unrelated slot'
Check ($first.Snapshot.Values.Count -eq 4 -and $first.Snapshot.EffectSlot -eq 'PostMix') 'initial backup retains the original endpoint and bypass properties'
$livePost=$postRecords | ConvertTo-Json -Depth 8 | ConvertFrom-Json;$livePost[0].Value=$clsid
$repair=Plan 'PostMix' $livePost $first.Snapshot
Check ($repair.ChainedEffect -eq '{VENDOR-POST-MIX}' -and $repair.Snapshot.CreatedUtc -eq $first.Snapshot.CreatedUtc) 'repair reuses the first original effect rather than chaining the mixer to itself'
Check (($repair.Snapshot.Values | ConvertTo-Json -Depth 8) -eq ($first.Snapshot.Values | ConvertTo-Json -Depth 8)) 'same-mode repair leaves the restoration history intact'
$streamRecords=@($livePost)+@($original | Where-Object {$_.Name -eq $effectGuid+',5'})
$stream=Plan 'Stream' $streamRecords $repair.Snapshot
Check ($stream.ChainedEffect -eq '{VENDOR-STREAM}' -and $stream.RestoreEffect.Value -eq '{VENDOR-POST-MIX}') 'switching to Stream restores PostMix and chains the original Stream effect'
Check ($stream.Snapshot.Values.Count -eq 5 -and ($stream.Snapshot.Values | Where-Object Name -EQ ($effectGuid+',2')).Value -eq '{VENDOR-POST-MIX}') 'switching mode adds one original slot without overwriting previous originals'
$liveStream=$streamRecords | ConvertTo-Json -Depth 8 | ConvertFrom-Json;($liveStream | Where-Object Name -EQ ($effectGuid+',2')).Value='{VENDOR-POST-MIX}';($liveStream | Where-Object Name -EQ ($effectGuid+',5')).Value=$clsid
$modeRecords=@($liveStream)+@($original | Where-Object {$_.Name -eq $effectGuid+',6'})
$mode=Plan 'Mode' $modeRecords $stream.Snapshot
Check ($mode.ChainedEffect -eq '' -and $mode.RestoreEffect.Value -eq '{VENDOR-STREAM}') 'switching to an originally absent Mode effect restores Stream and uses an empty effect chain'
$back=Plan 'PostMix' $modeRecords $mode.Snapshot
Check ($back.ChainedEffect -eq '{VENDOR-POST-MIX}' -and -not $back.RestoreEffect.Exists -and $back.Snapshot.Values.Count -eq 6) 'returning to PostMix removes the formerly absent Mode effect and preserves every original'
Check (($back.Snapshot.Values | ConvertTo-Json -Depth 8) -notmatch 'AE6B213A') 'repeated mode changes never save the mixer itself as an original effect'
$errorSeen=$false;try{Plan 'PostMix' $livePost $null | Out-Null}catch{$errorSeen=$true}
Check $errorSeen 'a mixer without an original backup is rejected instead of self-chained'
$invalid=$first.Snapshot | ConvertTo-Json -Depth 8 | ConvertFrom-Json;$invalid.EndpointId='other-device';$errorSeen=$false;try{Plan 'PostMix' $postRecords $invalid | Out-Null}catch{$errorSeen=$true}
Check $errorSeen 'a backup from another endpoint is rejected'
$invalid=$first.Snapshot | ConvertTo-Json -Depth 8 | ConvertFrom-Json;$invalid.Values=@($invalid.Values)+@($invalid.Values[0]);$errorSeen=$false;try{Plan 'PostMix' $postRecords $invalid | Out-Null}catch{$errorSeen=$true}
Check $errorSeen 'ambiguous duplicate original values are rejected'
foreach($file in Get-ChildItem (Join-Path $PSScriptRoot '..\installer') -Filter '*.ps1'){
 $parseErrors=$null;$tokens=$null;[System.Management.Automation.Language.Parser]::ParseFile($file.FullName,[ref]$tokens,[ref]$parseErrors) | Out-Null
 if($parseErrors.Count){throw ('Installer syntax error in '+$file.Name+': '+($parseErrors | Out-String))}
}
Check $true 'all installer scripts parse without running any installer command'
Write-Output ('PASS '+$script:checks+' endpoint install-plan checks. Fixtures only; no registry, audio service, or device settings changed.')
