# Runs inside Test-GUI.ps1 against its isolated app, data directory and named pipe.
function FindWindow($title) {
    $condition=[System.Windows.Automation.AndCondition]::new([System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$app.Id),[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$title),[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Window))
    for($attempt=0;$attempt -lt 40;$attempt++) {
        $window=[System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children,$condition)
        if(-not $window){$window=$mainRoot.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)}
        if($window){return $window};Start-Sleep -Milliseconds 50
    }
    throw ('Missing test window: '+$title)
}
function EditorState { @((Request @{command='tool-state'}).windows|Where-Object {$_.editor})[0].editor }
function SelectComboIndex($combo,$index) {
    $combo.SetFocus();$handle=[IntPtr]$combo.Current.NativeWindowHandle
    if($handle -eq [IntPtr]::Zero){$handle=[IntPtr]$script:root.Current.NativeWindowHandle}
    foreach($key in @(@(0x24)+@(0x28)*$index)){
        [SeekMouseTest]::SendMessage($handle,0x100,[IntPtr]$key,[IntPtr]::Zero)|Out-Null
        [SeekMouseTest]::SendMessage($handle,0x101,[IntPtr]$key,[IntPtr]::Zero)|Out-Null
    }
    Start-Sleep -Milliseconds 100
}
function EditorAction($menu,$action) {
    (FindName $menu ([System.Windows.Automation.ControlType]::MenuItem)).GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    (FindName $action ([System.Windows.Automation.ControlType]::MenuItem)).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 100
}
$mainRoot=$script:root
Add-Type -AssemblyName System.Drawing
function BluePixels($path) {
    $bitmap=[Drawing.Bitmap]::FromFile($path);$count=0
    try{for($y=0;$y -lt [Math]::Min(60,$bitmap.Height);$y++){for($x=0;$x -lt $bitmap.Width;$x++){$pixel=$bitmap.GetPixel($x,$y);if($pixel.R -eq 0 -and $pixel.G -eq 125 -and $pixel.B -eq 184){$count++}}};return $count}finally{$bitmap.Dispose()}
}
$volume=(FindId 'SpeakerSlider').GetCurrentPattern([Windows.Automation.RangeValuePattern]::Pattern)
$blueCounts=@()
foreach($level in @(0,.5,1)){
    $volume.SetValue($level);Start-Sleep -Milliseconds 60
    $imagePath=Join-Path $PSScriptRoot ('volume-fill-'+$level+'.png')
    Request @{command='screenshot';path=$imagePath}|Out-Null;$blueCounts+=BluePixels $imagePath
}
$volume.SetValue(0)
Check ($blueCounts[1]-$blueCounts[0] -gt 100 -and [Math]::Abs(($blueCounts[2]-$blueCounts[0])-2*($blueCounts[1]-$blueCounts[0])) -lt 20) 'volume slider renders a proportional blue fill at zero, half and full volume'
$button=FindId 'EditCategoryButton';$bounds=$button.Current.BoundingRectangle
$edge=[System.Windows.Automation.AutomationElement]::FromPoint([System.Windows.Point]::new($bounds.Right-2,$bounds.Y+$bounds.Height/2))
Check ($edge.Current.AutomationId -eq 'EditCategoryButton' -and $bounds.Width -ge 31) 'Edit category button is fully visible and hit-testable at its right edge'
[SeekMouseTest]::SetForegroundWindow($app.MainWindowHandle)|Out-Null;$button.SetFocus();Start-Sleep -Milliseconds 200
[SeekMouseTest]::RealClick($app.MainWindowHandle,$app.Id,$bounds.Right-3,$bounds.Y+$bounds.Height/2)
$script:root=FindWindow 'Edit category'
Check ((FindId 'CategoryEditName').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq 'My Sounds') 'footer Edit category opens the My Sounds editor'
foreach($mode in @('Generated','Stock','Custom')){FindId ('CategoryIcon'+$mode)|Out-Null}
$hotkey=FindName 'Set hotkey' ([System.Windows.Automation.ControlType]::Button)
$ok=FindName 'OK' ([System.Windows.Automation.ControlType]::Button)
Check (-not $hotkey.Current.IsOffscreen -and $hotkey.Current.BoundingRectangle.Bottom -lt $ok.Current.BoundingRectangle.Top) 'category name, icon and random-file hotkey controls fit above the footer'
$colors=FindId 'CategoryIconColor';$colors.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
Check ($colors.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem)).Count -eq 28) 'category color dropdown includes all 28 observed choices'
Request @{command='screenshot';path=(Join-Path $PSScriptRoot 'category-colors-open.png')}|Out-Null
$colors.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
SelectComboIndex $colors 3
Request @{command='screenshot';path=(Join-Path $PSScriptRoot 'category-editor.png')}|Out-Null
(FindId 'CategoryEditName').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('Canceled name')
InvokeButton 'Cancel';$script:root=$mainRoot
$button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke();$script:root=FindWindow 'Edit category'
Check ((FindId 'CategoryEditName').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq 'My Sounds') 'canceling category edits leaves My Sounds unchanged'
(FindId 'CategoryEditName').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('All clips')
(FindId 'CategoryIconGenerated').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
$colors=FindId 'CategoryIconColor';SelectComboIndex $colors 3
InvokeButton 'OK';$script:root=$mainRoot
Check ((FindId 'CategoryNameLabel').Current.Name -eq 'All clips') 'category edits immediately update the footer name'
Request @{command='save'}|Out-Null
$saved=Get-Content -LiteralPath (Join-Path $runRoot 'library.json') -Raw|ConvertFrom-Json
Check ($saved.rootCategory.name -eq 'All clips' -and $saved.rootCategory.iconMode -eq 'Generated') 'category name and icon settings persist in the library'
Check ($saved.rootCategory.iconColor -eq '#034C70') 'category color selection persists the chosen Navy swatch'
Request @{command='undo'}|Out-Null
Check ((FindId 'CategoryNameLabel').Current.Name -eq 'My Sounds') 'undo restores the root category name and icon'

$clip=Join-Path $runRoot 'editor.wav'
@'
import math,struct,sys,wave
with wave.open(sys.argv[1],"wb") as out:
    out.setnchannels(2);out.setsampwidth(2);out.setframerate(48000)
    out.writeframes(b"".join(struct.pack("<hh",int(6000*math.sin(2*math.pi*440*n/48000)),int(3000*math.sin(2*math.pi*440*n/48000))) for n in range(192000)))
'@ | & python - $clip
if($LASTEXITCODE -ne 0){throw 'Editor fixture generation failed'}
Request @{command='add';paths=@($clip)}|Out-Null
Request @{command='select';index=0}|Out-Null
Request @{command='open-tool';tool='editor'}|Out-Null
$script:root=FindWindow ('Sound Editor - '+$clip)
for($attempt=0;$attempt -lt 80;$attempt++){if((EditorState).loaded){break};Start-Sleep -Milliseconds 50}
Check ((EditorState).loaded -and (EditorState).duration -eq 4) 'sound editor decodes the selected file'
$menu=$script:root.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Menu))
$headers=$menu[0].FindAll([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.Condition]::TrueCondition).Current.Name
Check (($headers -join ',') -eq 'File,Edit,View,Play,Effects') 'sound editor has the original five menus in order'
Request @{command='screenshot';path=(Join-Path $PSScriptRoot 'sound-editor.png')}|Out-Null
$wave=FindId 'EditorWaveform';$bounds=$wave.Current.BoundingRectangle
# Default zoom is 400 logical pixels/sec; the test desktop is scaled by DPI.
$scale=$script:root.Current.BoundingRectangle.Width/624
[SeekMouseTest]::RealDrag([IntPtr]$script:root.Current.NativeWindowHandle,$app.Id,$bounds.X+40*$scale,$bounds.Y+60*$scale,$bounds.X+200*$scale,$bounds.Y+60*$scale)
$state=EditorState
Check ($state.selectionStart -gt .07 -and $state.selectionStart -lt .14 -and $state.selectionEnd -gt .45 -and $state.selectionEnd -lt .55) 'dragging the waveform selects the pointed audio range'
Check ($null -eq $script:root.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'EditorSelectedRange'))) 'editor selection adds no extra cropped footer text'
$horizontal=$script:root.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::IsScrollPatternAvailableProperty,$true))|Where-Object {$_.GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern).Current.HorizontallyScrollable}|Select-Object -First 1
$scrollPattern=$horizontal.GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern)
$scrollPattern.Scroll([Windows.Automation.ScrollAmount]::LargeIncrement,[Windows.Automation.ScrollAmount]::NoAmount)
Start-Sleep -Milliseconds 100
Check ($scrollPattern.Current.HorizontalScrollPercent -gt 0) 'themed editor scrollbar moves through the waveform'
$scrollPattern.SetScrollPercent(0,-1)
Start-Sleep -Milliseconds 100
Request @{command='screenshot';path=(Join-Path $PSScriptRoot 'editor-selection-scrollbar.png')}|Out-Null
EditorAction 'Edit' 'Crop selected range'
$cropped=(EditorState).duration
Check ($cropped -gt .35 -and $cropped -lt .45 -and (EditorState).canUndo) 'crop changes the editor audio to the selected range'
EditorAction 'Edit' 'Undo'
Check ((EditorState).duration -eq 4 -and -not (EditorState).modified) 'editor Undo restores the complete unmodified sound'
EditorAction 'Edit' 'Redo'
Check ([Math]::Abs((EditorState).duration-$cropped) -lt .001) 'editor Redo restores the crop'
EditorAction 'Effects' 'Reverse'
EditorAction 'Effects' 'Fade-in'
EditorAction 'Effects' 'Fade-out'
Check ((EditorState).modified) 'all three editor effects are available and modify the selected audio'
EditorAction 'Play' 'Play selected section'
Check ((EditorState).playing) 'editor previews the selected audio through its separate player'
InvokeButton 'Stop playback'
Check (-not (EditorState).playing) 'editor stop ends preview playback'
EditorAction 'View' 'Zoom in'
Check ((EditorState).zoom -eq 600) 'editor View zoom changes waveform scale'
EditorAction 'View' 'Default zoom'
Check ((EditorState).zoom -eq 400) 'editor restores the original default zoom'
# Disable overwrite prompt through the actual menu and save only our private WAV.
EditorAction 'File' 'Show overwrite confirmation prompt'
InvokeButton 'Save'
for($attempt=0;$attempt -lt 80;$attempt++){if(-not (EditorState).saving -and -not (EditorState).modified){break};Start-Sleep -Milliseconds 50}
Check (-not (EditorState).modified -and -not (EditorState).saving) 'Save writes editor changes and clears its modified state'
$duration=@'
import sys,wave
with wave.open(sys.argv[1],"rb") as audio: print(audio.getnframes()/audio.getframerate())
'@ | & python - $clip
Check ([Math]::Abs([double]$duration-$cropped) -lt .001) 'saved WAV duration matches the edited waveform'
$editorRoot=$script:root
InvokeButton 'Save as...';$script:root=FindWindow 'Save sound'
try {
    $dialogHandle=[IntPtr]$script:root.Current.NativeWindowHandle
    $combos=[SeekMouseTest]::Children($dialogHandle,'ComboBox')
    $format=@($combos|Where-Object {[SeekMouseTest]::ComboItems($_) -contains 'MP3 (*.mp3)'})[0]
    $bitrate=@($combos|Where-Object {[SeekMouseTest]::ComboItems($_) -contains '320 Kbps'})[0]
    Check (-not [SeekMouseTest]::IsWindowEnabled($bitrate)) 'Save as greys bitrate for the default M4A format'
    [SeekMouseTest]::ComboSelect($format,1);Start-Sleep -Milliseconds 100
    Check ([SeekMouseTest]::IsWindowEnabled($bitrate)) 'Save as enables its inline bitrate selector when MP3 is selected'
    Check ([SeekMouseTest]::ComboItems($bitrate).Count -eq 8) 'MP3 Save as offers the original eight bitrates'
    [SeekMouseTest]::ComboSelect($bitrate,5)
    $exported=Join-Path $runRoot 'export-128.mp3'
    [SeekMouseTest]::FileName($dialogHandle,$exported)
    [SeekMouseTest]::NativeButton($dialogHandle,'Save')
} finally {
    try{[SeekMouseTest]::NativeButton([IntPtr]$script:root.Current.NativeWindowHandle,'Cancel')}catch{}
    $script:root=$editorRoot
}
for($attempt=0;$attempt -lt 80;$attempt++){if((Test-Path -LiteralPath $exported) -and -not (EditorState).saving){break};Start-Sleep -Milliseconds 50}
Check ((Test-Path -LiteralPath $exported) -and -not (EditorState).saving) 'Save as encodes an MP3 using the selected bitrate'
$encodedRate=& ffprobe -v error -select_streams a:0 -show_entries stream=bit_rate -of default=noprint_wrappers=1:nokey=1 $exported
Check ($LASTEXITCODE -eq 0 -and [int]$encodedRate -eq 128000) 'saved MP3 stream is actually encoded at 128 Kbps'
InvokeButton 'Cancel';$script:root=$mainRoot

Request @{command='open-tool';tool='settings'}|Out-Null
$script:root=FindWindow 'Preferences'
(FindId 'MeasureVoiceVolume').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Milliseconds 500
Check ((FindId 'MeasureVoiceVolume').Current.Name -eq 'Stop measurement' -and -not (FindId 'MeasuredVoiceVolume').Current.IsOffscreen) 'voice measurement expands inline with a running meter and Stop button'
Request @{command='screenshot';path=(Join-Path $PSScriptRoot 'preferences-voice-measurement.png')}|Out-Null
(FindId 'MeasureVoiceVolume').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Check ((FindId 'MeasureVoiceVolume').Current.Name -eq 'Measure voice volume') 'stopping voice measurement collapses its inline panel and releases capture'
(FindId 'FixedNormalization').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
(FindId 'MeasureFixedAdjustment').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
for($attempt=0;$attempt -lt 100;$attempt++){if((FindId 'FileMeasurementStatus').Current.Name -eq 'Status: Completed'){break};Start-Sleep -Milliseconds 50}
Check ((FindId 'FileMeasurementStatus').Current.Name -eq 'Status: Completed' -and (FindId 'AnalyzedFileCount').Current.Name -eq 'Analyzed files: 1') 'fixed adjustment measures library audio and shows completed results inline'
Check (@((Request @{command='tool-state'}).windows).Count -eq 1) 'measurement uses the Preferences page without a separate progress dialog'
Request @{command='screenshot';path=(Join-Path $PSScriptRoot 'preferences-file-measurement.png')}|Out-Null
(FindName 'Hotkeys' ([System.Windows.Automation.ControlType]::TabItem)).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 100
$changes=$script:root.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.AndCondition]::new([System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Change modifier'),[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Button)))
$num=(FindId 'AltNumcodeHotkeys').Current.BoundingRectangle;$index=(FindId 'IndexHotkeys').Current.BoundingRectangle
Request @{command='screenshot';path=(Join-Path $PSScriptRoot 'preferences-hotkeys-layout.png')}|Out-Null
@{Num=$num.ToString();Index=$index.ToString();Changes=@($changes|ForEach-Object {$_.Current.BoundingRectangle.ToString()})}|ConvertTo-Json|Set-Content (Join-Path $PSScriptRoot 'hotkeys-alignment.json')
Check ($changes.Count -eq 2 -and [Math]::Abs($num.Y+$num.Height/2-$changes[0].Current.BoundingRectangle.Y-$changes[0].Current.BoundingRectangle.Height/2) -lt 3 -and [Math]::Abs($index.Y+$index.Height/2-$changes[1].Current.BoundingRectangle.Y-$changes[1].Current.BoundingRectangle.Height/2) -lt 3) 'Hotkeys Preferences has two horizontal modifier rows matching their checkboxes'
Request @{command='screenshot';path=(Join-Path $PSScriptRoot 'preferences-hotkeys-layout.png')}|Out-Null
InvokeButton 'Cancel';$script:root=$mainRoot
