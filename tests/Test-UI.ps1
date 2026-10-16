param([switch]$UseRunningApp,[string]$PipeName='SoundboardLab',[switch]$AllowWindowActivation)
$ErrorActionPreference='Stop'
if(-not $AllowWindowActivation){throw 'Desktop workflow tests open and activate windows. Run explicitly with -AllowWindowActivation only when you want interactive tests.'}
$testRoot=$PSScriptRoot
$projectRoot=Split-Path -Parent $testRoot
$remote=Join-Path $projectRoot 'Remote.ps1'
$results=[Collections.Generic.List[string]]::new()
$runRoot=Join-Path $testRoot ('ui-output-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
function Request($data) {
    $json=$data | ConvertTo-Json -Depth 8 -Compress
    $response=& $remote -PipeName $PipeName -Command ([string]$data.command) -Json $json | ConvertFrom-Json
    if(-not $response.ok){throw $response.error}; return $response
}
function Check($condition,$name) { if(-not $condition){throw $name}; $results.Add('PASS '+$name) }
if(-not $UseRunningApp) {
    $PipeName='SoundboardLab.Test.'+[Guid]::NewGuid().ToString('N')
    $state=Join-Path $testRoot ('ui-state-'+[Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $state -Force | Out-Null
    @{SpeakerVolume=0}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $state 'preferences.json')
    $process=Start-Process -FilePath (Join-Path $projectRoot 'app\bin\Debug\net10.0-windows\SoundboardLab.exe') -ArgumentList @('--data-dir',('"'+$state+'"'),'--pipe-name',$PipeName) -PassThru -WindowStyle Hidden
}
try {
    $initial=Request @{command='state'}
    if(-not $UseRunningApp -and $initial.processId -ne $process.Id){throw 'Workflow test connected to the wrong app instance'}
    Check ($initial.hotbarCount -eq 12 -and $initial.hotbarPages -eq 1) 'live WPF hotbar defaults to twelve slots and one page'
    Request @{command='new'} | Out-Null
    $files=@((Join-Path $testRoot 'fixtures\tone.wav'),(Join-Path $testRoot 'fixtures\tone.mp3'),(Join-Path $testRoot 'fixtures\tone.opus'))
    Request @{command='add';paths=$files} | Out-Null
    Check ((Request @{command='state'}).libraryCount -eq 3) 'file import populates the bound library'
    Request @{command='rename';index=0;title='Amber test tone'} | Out-Null
    Request @{command='search';text='Amber'} | Out-Null
    Check ((Request @{command='state'}).visibleCount -eq 1) 'search filters live collection view'
    Request @{command='search';text=''} | Out-Null
    Request @{command='category';name='Effects/Short'} | Out-Null
    Request @{command='move';index=0;category='Effects/Short'} | Out-Null
    Check ((Request @{command='state'}).visibleCount -eq 1) 'nested category selection filters sounds'
    Request @{command='pin';index=0;page=0;slot=0} | Out-Null
    Check ((Request @{command='state'}).pins[0].Slot -eq 0) 'pin persists a real hotbar assignment'
    Request @{command='add-hotbar-page'}|Out-Null
    Request @{command='pin';index=0;page=1;slot=3} | Out-Null
    Check ((Request @{command='state'}).pins.Count -eq 2) 'same sound can be pinned on multiple pages independently'
    Request @{command='category';name='*'} | Out-Null
    Request @{command='volume';value=0} | Out-Null
    Request @{command='play';index=0;mode='Speakers'} | Out-Null
    $playing=Request @{command='state'}
    Check ($playing.playing -and $playing.duration -gt .9) 'live speaker playback starts through remote command'
    Request @{command='pause'} | Out-Null
    Check ((Request @{command='state'}).paused) 'pause updates application state'
    Request @{command='seek';seconds=.4} | Out-Null
    $seek=Request @{command='state'}
    Check ($seek.paused) 'seek preserves paused playback state'
    Request @{command='pause'} | Out-Null
    Request @{command='stop'} | Out-Null
    Check (-not (Request @{command='state'}).playing) 'stop terminates playback'
    $save=Join-Path $runRoot 'ui-library.json'
    Request @{command='save';path=$save} | Out-Null
    Check (Test-Path -LiteralPath $save) 'save writes a reloadable library'
    Request @{command='new'} | Out-Null
    Check ((Request @{command='state'}).libraryCount -eq 0) 'new library clears the bound grid'
    Request @{command='load';path=$save} | Out-Null
    Check ((Request @{command='state'}).libraryCount -eq 3) 'reload restores library and hotbar data'
    $export=Join-Path $runRoot 'ui-export'
    Request @{command='export';path=$export} | Out-Null
    Check ((Test-Path (Join-Path $export 'library.json')) -and (Get-ChildItem (Join-Path $export 'sounds') -File).Count -eq 3) 'portable export contains library and audio files'
    $python=(Get-Command python -ErrorAction Stop).Source
    $port=18757
    $ready=Join-Path $runRoot 'http-ready.txt'
    $server=Start-Process -FilePath $python -ArgumentList @('-u',('"'+(Join-Path $testRoot 'serve-once.py')+'"'),$port,('"'+$files[0]+'"')) -RedirectStandardOutput $ready -RedirectStandardError (Join-Path $runRoot 'http-error.txt') -PassThru -WindowStyle Hidden
    for($attempt=0;$attempt -lt 30;$attempt++){if((Test-Path -LiteralPath $ready) -and (Get-Content -Raw -LiteralPath $ready) -match 'ready'){break};Start-Sleep -Milliseconds 100}
    try {
        Request @{command='import-url';url=('http://127.0.0.1:'+$port+'/audio?id=fixture')} | Out-Null
        Check ((Request @{command='state'}).libraryCount -eq 4) 'direct HTTP import honors filename and decodes downloaded audio'
    } finally { if(-not $server.WaitForExit(3000)){ $server.Kill() } }
    Request @{command='screenshot';path=(Join-Path $testRoot 'ui-preview.png')} | Out-Null
    Check (Test-Path (Join-Path $testRoot 'ui-preview.png')) 'running WPF view renders to review image'
    Request @{command='select';index=0} | Out-Null
    foreach($tool in @('settings','editor','speech','recorder','microphone')) {
        Request @{command='open-tool';tool=$tool} | Out-Null
        $toolState=Request @{command='tool-state'}
        Check ($toolState.windows.Count -eq 1 -and $toolState.windows[0].background -eq $(if($tool -eq 'settings'){'#FFF0F0F0'}else{'#FF252525'})) ($tool+' tool opens with readable theme')
        Request @{command='screenshot';path=(Join-Path $testRoot ('ui-'+$tool+'.png'))} | Out-Null
        Request @{command='close-tools'} | Out-Null
    }
    $results | Set-Content -LiteralPath (Join-Path $testRoot 'ui-results.txt')
    $results; 'PASS '+$results.Count+' application workflow checks'
} finally {
    if(-not $UseRunningApp) { try { Request @{command='close'} | Out-Null } catch { }; if($process -and -not $process.HasExited){if(-not $process.WaitForExit(5000)){throw ('UI process failed to exit cleanly: '+$process.Id)}}; if($process.ExitCode -ne 0){throw ('UI process exit code '+$process.ExitCode)}; 'PASS application exits cleanly after workflow tests' | Add-Content -LiteralPath (Join-Path $testRoot 'ui-results.txt') }
}
