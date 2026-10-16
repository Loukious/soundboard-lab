# Pure planning helper: no registry, processes, audio, or files are changed here.
function New-EndpointInstallPlan {
    param([string]$EndpointId,[string]$EndpointPath,[ValidateSet('PostMix','Stream','Mode','Endpoint')][string]$EffectSlot,[object[]]$Records,$Previous,[string]$DllPath)
    $effectGuid='{d04e05a6-594b-4fb6-a80d-01af5eed7d1d}'
    $effectName=$effectGuid+','+@{PostMix=2;Stream=5;Mode=6;Endpoint=7}[$EffectSlot]
    $clsid='{AE6B213A-2854-47D6-93D6-CA9F22880D3E}'
    $priorValues=@();$restore=$null
    if($Previous){
        if($Previous.EndpointId -ne $EndpointId -or $Previous.EndpointPath -ne $EndpointPath){throw 'The saved endpoint backup does not match this microphone.'}
        if($Previous.EffectName -notin @(2,5,6,7 | ForEach-Object {$effectGuid+','+$_})){throw 'Invalid saved effect slot.'}
        $priorValues=@($Previous.Values)
        if(@($priorValues.Name | Select-Object -Unique).Count -ne $priorValues.Count){throw 'Duplicate values in the saved endpoint backup.'}
        $priorEffect=@($priorValues | Where-Object {$_.Name -eq $Previous.EffectName})
        if($priorEffect.Count -ne 1){throw 'The saved previous effect is missing.'}
        if($Previous.EffectName -ne $effectName){$restore=$priorEffect[0]}
    }
    $original=@($priorValues | Where-Object {$_.Name -eq $effectName})
    if($original.Count -eq 0){$original=@($Records | Where-Object {$_.Name -eq $effectName})}
    if($original.Count -ne 1){throw 'The selected effect snapshot is missing or duplicated.'}
    $chain=if($original[0].Exists){[string]$original[0].Value}else{''}
    if($chain -eq $clsid){throw 'Cannot chain the mixer to itself without an original effect backup.'}
    $values=$priorValues+@($Records | Where-Object {$_.Name -notin @($priorValues.Name)})
    $snapshot=[pscustomobject]@{Version=1;EndpointId=$EndpointId;EndpointPath=$EndpointPath;EffectSlot=$EffectSlot;EffectName=$effectName;DllPath=$DllPath;CreatedUtc=if($Previous){$Previous.CreatedUtc}else{[DateTime]::UtcNow.ToString('o')};Values=@($values)}
    [pscustomobject]@{Snapshot=$snapshot;ChainedEffect=$chain;RestoreEffect=$restore}
}
